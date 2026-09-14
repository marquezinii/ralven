using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Ralven.Contracts;

namespace Ralven.App.Services;

internal sealed record ElevatedBrokerResult
{
    public required bool Succeeded { get; init; }

    public required bool WasCancelled { get; init; }

    public required string Message { get; init; }

    public string? State { get; init; }

    public string? ErrorCode { get; init; }

    public IReadOnlyList<string> AppliedActionIds { get; init; } = [];
}

internal sealed class ElevatedBrokerClient
{
    private const int ErrorCancelled = 1223;
    private const int MaximumEvents = 128;
    private const int MaximumEventCharacters = 64 * 1024;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    // A fase elevada do perfil médio executa somente ações allowlisted e curtas.
    // Não deixar a interface em espera por dezenas de minutos torna uma falha
    // do Windows recuperável e visível no relatório.
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);
    private readonly string requestDirectory;
    private readonly string brokerPath;
    private readonly ILocalizationService localization;

    public ElevatedBrokerClient(string appDataDirectory, ILocalizationService? localization = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        requestDirectory = Path.Combine(Path.GetFullPath(appDataDirectory), "Requests");
        brokerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "broker",
            "Ralven.Broker.exe"));
        this.localization = localization ?? LocalizationService.Current;
    }

    public async Task<ElevatedBrokerResult> ExecuteAsync(
        OptimizationPlanDto plan,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();

        var requestPath = await WriteRequestAsync(plan, cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(
                $"--request \"{requestPath}\" --pipe {{0}} --culture {localization.CurrentCulture.Name}",
                plan.PlanId,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteRequest(requestPath);
        }
    }

    public Task<ElevatedBrokerResult> RollbackAsync(
        Guid transactionId,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("O identificador da transação não pode ser vazio.", nameof(transactionId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return RunAsync(
            $"--rollback {transactionId:D} --pipe {{0}} --culture {localization.CurrentCulture.Name}",
            transactionId,
            progress,
            cancellationToken);
    }

    private async Task<ElevatedBrokerResult> RunAsync(
        string argumentTemplate,
        Guid expectedTransactionId,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(brokerPath))
        {
            throw new FileNotFoundException(
                "O componente administrativo não foi encontrado ao lado do aplicativo.",
                brokerPath);
        }

        using var integrityLease = BrokerIntegrityVerifier.VerifyBeforeElevation(
            AppContext.BaseDirectory,
            brokerPath);

        cancellationToken.ThrowIfCancellationRequested();
        var pipeId = Guid.NewGuid();
        await using var pipe = new NamedPipeServerStream(
            pipeId.ToString("N"),
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = brokerPath,
                Arguments = string.Format(
                    CultureInfo.InvariantCulture,
                    argumentTemplate,
                    pipeId.ToString("N")),
                WorkingDirectory = Path.GetDirectoryName(brokerPath)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("O Windows não iniciou o componente administrativo.");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedBrokerResult
            {
                Succeeded = false,
                WasCancelled = true,
                Message = localization.GetString("Broker.Error.Cancelled")
            };
        }

        // Depois que o broker elevado começa, deixamos a transação terminar ou se
        // reverter com segurança mesmo se o botão Cancelar for usado na interface.
        using var timeout = new CancellationTokenSource(OperationTimeout);
        try
        {
            try
            {
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
                connectionTimeout.CancelAfter(ConnectionTimeout);
                await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The 30s connection window elapsed on its own (neither the
                // overall 2-minute safety timeout nor a user cancellation).
                // A cancelled TimeoutException here — instead of letting this
                // propagate unhandled — gives a catchable, honest result
                // instead of a generic app-level error.
                throw new TimeoutException(
                    localization.GetString("Broker.Error.ConnectionTimeout"));
            }

            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            BrokerEvent? terminal = await ReadUntilTerminalAsync(
                reader,
                expectedTransactionId,
                progress,
                localization,
                timeout.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var succeeded = process.ExitCode == 0
                && terminal?.Success == true
                && terminal.Kind is BrokerEventKind.Completed
                    or BrokerEventKind.RollbackCompleted;
            return new ElevatedBrokerResult
            {
                Succeeded = succeeded,
                WasCancelled = false,
                Message = terminal is null
                    ? DescribeMissingTerminalEvent(process.ExitCode, localization)
                    : DescribeBrokerEvent(terminal, localization),
                State = terminal?.State,
                ErrorCode = terminal?.ErrorCode ?? ResolveMissingTerminalErrorCode(process.ExitCode),
                AppliedActionIds = terminal?.AppliedActionIds ?? []
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                localization.GetString("Broker.Error.OperationTimeout"));
        }
    }

    private async Task<string> WriteRequestAsync(
        OptimizationPlanDto plan,
        CancellationToken cancellationToken)
    {
        var productDirectory = Path.GetDirectoryName(requestDirectory)!;
        Directory.CreateDirectory(productDirectory);
        Directory.CreateDirectory(requestDirectory);
        EnsurePlainDirectory(productDirectory);
        EnsurePlainDirectory(requestDirectory);

        var destination = Path.Combine(requestDirectory, $"{plan.PlanId:N}.json");
        var temporary = Path.Combine(requestDirectory, $".{plan.PlanId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = new UTF8Encoding(false, true).GetBytes(RalvenJson.SerializePlan(plan));
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally
        {
            // Best-effort only: after a successful Move the temp file is
            // already gone, and a cleanup failure (antivirus still holding the
            // handle) must never replace a completed write with an exception.
            if (File.Exists(temporary))
            {
                TryDeleteRequest(temporary);
            }
        }
    }

    /// <summary>
    /// The broker connected to the local progress pipe (otherwise a
    /// <see cref="BrokerPipeException"/>-derived timeout would have already
    /// been raised) but exited without ever publishing a terminal event —
    /// meaning it was interrupted mid-run rather than rejecting or failing
    /// the request cleanly. The known broker exit codes narrow this down
    /// where possible; an unrecognized code most often means the process
    /// was terminated externally (commonly antivirus/SmartScreen reacting
    /// to an elevated, unsigned executable) rather than a bug in the
    /// broker's own logic.
    /// </summary>
    private static string DescribeMissingTerminalEvent(
        int exitCode,
        ILocalizationService localization)
    {
        var known = exitCode switch
        {
            2 => localization.GetString("Broker.Error.InvalidArguments"),
            3 => localization.GetString("Broker.Error.ConnectionFailed"),
            7 => localization.GetString("Broker.Error.InvalidAdminToken"),
            _ => null
        };

        return known
            ?? localization.Format("Broker.Error.Interrupted", exitCode);
    }

    internal static string? ResolveMissingTerminalErrorCode(int exitCode) => exitCode switch
    {
        2 => "broker-invalid-arguments",
        3 => "broker-pipe-connection-failed",
        7 => "broker-not-elevated",
        _ => null
    };

    private static string DescribeBrokerEvent(
        BrokerEvent brokerEvent,
        ILocalizationService localization)
    {
        if (!string.IsNullOrWhiteSpace(brokerEvent.ActionId))
        {
            var actionName = localization.GetStringOrFallback(
                $"Actions.{brokerEvent.ActionId}.Name",
                brokerEvent.ActionId);
            return localization.Format("Runtime.BrokerActionDetail", actionName);
        }

        return localization.GetString(brokerEvent.Kind switch
        {
            BrokerEventKind.Started => "Runtime.BrokerStartedDetail",
            BrokerEventKind.Completed => "Runtime.BrokerCompletedDetail",
            BrokerEventKind.RollbackStarted => "Runtime.BrokerRollbackStartedDetail",
            BrokerEventKind.RollbackCompleted => "Runtime.BrokerRollbackCompletedDetail",
            BrokerEventKind.Rejected => "Runtime.BrokerRejectedDetail",
            _ => "Runtime.BrokerFailedDetail"
        });
    }

    /// <summary>
    /// Reads broker wire events until the terminal event for the transaction,
    /// EOF, or the safety cap. Exposed as a pure-ish helper (a reader plus a
    /// progress sink) so the reading rules can be regression-tested without
    /// a real elevated broker process. Previously the loop consumed every
    /// line up to the cap without stopping at the terminal: a plan producing
    /// enough progress events pushed the terminal event past the 128-event
    /// window, the loop ended with <c>terminal == null</c>, and a successful
    /// elevated phase was reported as failed. Breaking as soon as the
    /// terminal arrives (the broker always publishes it as its last event)
    /// makes the cap unreachable in normal operation; hitting the cap anyway
    /// is now an explicit, honest error instead of a silent false failure.
    /// </summary>
    internal static async Task<BrokerEvent?> ReadUntilTerminalAsync(
        TextReader reader,
        Guid expectedTransactionId,
        IProgress<AppProgressUpdate> progress,
        ILocalizationService localization,
        CancellationToken cancellationToken)
    {
        long previousSequence = 0;
        for (var read = 0; read < MaximumEvents; read++)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length > MaximumEventCharacters)
            {
                throw new InvalidDataException("O broker retornou um evento local acima do limite seguro.");
            }

            var brokerEvent = JsonSerializer.Deserialize<BrokerEvent>(
                line,
                RalvenJson.Options)
                ?? throw new JsonException("O broker retornou um evento vazio.");
            if (brokerEvent.SchemaVersion != BrokerEventSchema.CurrentVersion
                || brokerEvent.Sequence <= previousSequence)
            {
                throw new InvalidDataException("A sequência de eventos do broker é inválida.");
            }

            if (brokerEvent.TransactionId is Guid eventTransactionId
                && eventTransactionId != expectedTransactionId)
            {
                throw new InvalidDataException(
                    "O broker retornou eventos para outra transação.");
            }

            if (brokerEvent.Kind is BrokerEventKind.Completed
                or BrokerEventKind.RollbackCompleted
                && brokerEvent.TransactionId != expectedTransactionId)
            {
                throw new InvalidDataException(
                    "O evento terminal do broker não confirmou a transação solicitada.");
            }

            previousSequence = brokerEvent.Sequence;
            ReportBrokerProgress(brokerEvent, progress, localization);
            if (IsTerminalEvent(brokerEvent.Kind))
            {
                return brokerEvent;
            }
        }

        throw new InvalidDataException(
            "O broker produziu mais eventos do que o esperado sem confirmar a conclusão da transação.");
    }

    private static bool IsTerminalEvent(BrokerEventKind kind) =>
        kind is BrokerEventKind.Completed
            or BrokerEventKind.RollbackCompleted
            or BrokerEventKind.Rejected
            or BrokerEventKind.Failed;

    private static void ReportBrokerProgress(
        BrokerEvent brokerEvent,
        IProgress<AppProgressUpdate> progress,
        ILocalizationService localization)
    {
        var localPercent = brokerEvent.TotalWeight is > 0
            ? 72d + (23d * brokerEvent.CompletedWeight.GetValueOrDefault() / brokerEvent.TotalWeight.Value)
            : brokerEvent.Kind is BrokerEventKind.Completed or BrokerEventKind.RollbackCompleted
                ? 98d
                : 72d;
        progress.Report(new AppProgressUpdate
        {
            Timestamp = brokerEvent.TimestampUtc.ToLocalTime(),
            Kind = brokerEvent.Kind switch
            {
                BrokerEventKind.Completed or BrokerEventKind.RollbackCompleted => AppProgressKind.Verifying,
                BrokerEventKind.Failed or BrokerEventKind.Rejected => AppProgressKind.Warning,
                BrokerEventKind.RollbackStarted => AppProgressKind.RollingBack,
                _ => AppProgressKind.Applying
            },
            Percent = Math.Clamp(localPercent, 72, 98),
            Headline = brokerEvent.Kind is BrokerEventKind.RollbackStarted
                or BrokerEventKind.RollbackCompleted
                ? localization.GetString("Runtime.BrokerRestoring")
                : localization.GetString("Runtime.BrokerApplying"),
            Detail = DescribeBrokerEvent(brokerEvent, localization),
            ActionId = brokerEvent.ActionId,
            Outcome = brokerEvent.ActionId is null ? null : brokerEvent.Kind switch
            {
                BrokerEventKind.Completed or BrokerEventKind.RollbackCompleted =>
                    brokerEvent.Success == true ? ActionExecutionOutcome.Applied : ActionExecutionOutcome.Failed,
                BrokerEventKind.Failed or BrokerEventKind.Rejected => ActionExecutionOutcome.Failed,
                _ => null
            }
        });
    }

    private static void EnsurePlainDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A pasta local de solicitações não pode ser um link ou junction.");
        }
    }

    private static void TryDeleteRequest(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
