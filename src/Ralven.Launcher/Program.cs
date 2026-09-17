using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ralven.App.Services;
using Ralven.UpdateRuntime;

namespace Ralven.Launcher;

internal static class Program
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(45);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (PackageIdentity.IsPackaged) Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ralven");
        var diagnostics = new UpdaterDiagnostics(dataRoot);
        var telemetryAuthorized = UpdaterDiagnostics.IsTelemetryAuthorized(dataRoot);
        try
        {
            return Supervise(args, diagnostics, dataRoot, telemetryAuthorized);
        }
        finally
        {
            // Supervision has released its thread-owned lifecycle mutex. Remote
            // delivery cannot delay launching the app or block another launcher.
            await diagnostics.FlushPendingAsync(telemetryAuthorized);
        }
    }

    // Named mutex ownership is thread-affine. This launcher has no UI dispatcher;
    // keep local supervision on its acquiring thread and await network only after it returns.
    private static int Supervise(string[] args, UpdaterDiagnostics diagnostics, string dataRoot, bool telemetryAuthorized)
    {
        var forwardedArguments = args
            .Where(argument => !argument.StartsWith("--wait-for-pid=", StringComparison.OrdinalIgnoreCase)
                && !argument.StartsWith("--wait-for-start=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "Runtime");
        if (PackageIdentity.IsPackaged)
        {
            // Store owns activation and updates. Read the bundled pointer without recovery writes.
            var bundled = new RuntimeActivationStore(runtimeRoot);
            var executable = Path.Combine(bundled.VersionsRoot, bundled.ReadActiveVersion(), "Ralven.exe");
            UpdatePathSafety.EnsureNoReparsePoints(executable);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
            foreach (var argument in forwardedArguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start Ralven.");
            return 0;
        }
        using var lifecycleLease = RuntimeUpdateLease.TryAcquire(runtimeRoot);
        if (lifecycleLease is null) return 0;
        var localization = LocalizationService.Current;
        ApplyStoredLanguage(localization, dataRoot);
        UpdateTransaction? currentTransaction = null;
        try
        {
            // Read the journal before WaitForParent (not after): WaitForParent
            // is exactly the step that can fail (the previous process not
            // exiting in time), and the catch block below needs
            // currentTransaction populated to be able to abandon/roll back a
            // candidate that never got the chance to launch.
            var journal = new UpdateRecoveryJournal(runtimeRoot);
            journal.TryRead(out currentTransaction!);
            WaitForParent(args);
            var recovery = new RecoveryCoordinator(runtimeRoot);
            var initialDecision = recovery.Reconcile(DateTimeOffset.UtcNow, HealthTimeout);
            if (initialDecision == RecoveryDecision.RolledBack && currentTransaction is not null)
                Record(diagnostics, currentTransaction, "rollback", "rolled-back", UpdaterEventCodes.HealthCheckTimeout, null, dataRoot, telemetryAuthorized);
            var activation = new RuntimeActivationStore(runtimeRoot);
            var version = activation.ReadActiveVersion();
            var floor = new VersionFloorStore(dataRoot).Read(version);
            if (Version.Parse(version) < Version.Parse(floor))
            {
                if (!Directory.Exists(Path.Combine(activation.VersionsRoot, floor)))
                    throw new CryptographicException("A versão ativa está abaixo do piso anti-downgrade confirmado.");
                activation.Activate(floor);
                version = floor;
            }
            if (!journal.TryRead(out _))
                activation.PruneInactiveVersions();
            var executable = Path.Combine(activation.VersionsRoot, version, "Ralven.exe");
            UpdatePathSafety.EnsureNoReparsePoints(executable);
            if (!File.Exists(executable)) throw new FileNotFoundException("A versão ativa não contém o aplicativo.", executable);

            var hasCandidate = journal.TryRead(out var transaction) && transaction.CandidateVersion == version;
            if (hasCandidate && transaction.CandidateLaunchedAtUtc is null)
                transaction = journal.MarkCandidateLaunched(transaction);

            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
            foreach (var argument in forwardedArguments) start.ArgumentList.Add(argument);
            if (hasCandidate)
            {
                start.ArgumentList.Add($"--update-transaction={transaction.Id}");
                start.ArgumentList.Add($"--update-nonce={transaction.Nonce}");
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("O Windows não iniciou o Ralven.");
            if (!hasCandidate) return 0;

            var receipt = new UpdateHealthReceiptStore(runtimeRoot);
            var deadline = DateTimeOffset.UtcNow + HealthTimeout;

            bool TryConfirmHealth()
            {
                if (!receipt.Confirms(transaction)) return false;
                recovery.Reconcile(DateTimeOffset.UtcNow, HealthTimeout);
                Record(diagnostics, transaction, "health-check", "completed", UpdaterEventCodes.HealthConfirmed, null, dataRoot, telemetryAuthorized);
                return true;
            }

            while (DateTimeOffset.UtcNow < deadline && !HasExitedSafely(process))
            {
                if (TryConfirmHealth()) return 0;
                Thread.Sleep(250);
            }
            if (TryConfirmHealth()) return 0;
            recovery.Reconcile(DateTimeOffset.UtcNow, TimeSpan.Zero);
            Record(diagnostics, transaction, "rollback", "rolled-back", UpdaterEventCodes.HealthCheckTimeout, null, dataRoot, telemetryAuthorized);
            MessageBox.Show(
                localization.GetString("Launcher.Recovery.Message"),
                localization.GetString("Launcher.Recovery.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        catch (Exception exception)
        {
            if (currentTransaction is not null)
            {
                try
                {
                    var recovery = new RecoveryCoordinator(runtimeRoot);
                    // Re-read the journal instead of trusting the snapshot taken
                    // before WaitForParent: MarkCandidateLaunched may have run
                    // since then, and only the current on-disk state can say
                    // whether the candidate actually got its process started.
                    var latest = new UpdateRecoveryJournal(runtimeRoot).TryRead(out var current)
                        ? current
                        : currentTransaction;
                    // A candidate that was never launched (this failure struck
                    // before Process.Start, e.g. the previous process did not
                    // exit in time) has no running process for Reconcile's
                    // health-timeout wait to apply to -- Abandon reverts it
                    // unconditionally instead of leaving active.json pointed
                    // at a version that never ran.
                    if (latest.CandidateLaunchedAtUtc is null)
                        recovery.Abandon(latest);
                    else
                        recovery.Reconcile(DateTimeOffset.UtcNow, TimeSpan.Zero);
                }
                catch (Exception recoveryException) when (recoveryException is not (
                    OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                }
                Record(diagnostics, currentTransaction, "activation", "failed", Classify(exception), exception.ToString(), dataRoot, telemetryAuthorized);
            }
            MessageBox.Show(DescribeFailure(localization, exception), "Ralven", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    private static void WaitForParent(string[] args)
    {
        var pidText = args.FirstOrDefault(value => value.StartsWith("--wait-for-pid=", StringComparison.OrdinalIgnoreCase))?["--wait-for-pid=".Length..];
        var startText = args.FirstOrDefault(value => value.StartsWith("--wait-for-start=", StringComparison.OrdinalIgnoreCase))?["--wait-for-start=".Length..];
        if (pidText is null && startText is null) return;
        if (!int.TryParse(pidText, out var pid) || pid <= 0 || !long.TryParse(startText, out var expectedStart) || expectedStart <= 0)
            throw new InvalidDataException("Identidade do processo anterior inválida.");
        ParentProcessWait.WaitForExit(pid, expectedStart, 30_000, "O Ralven anterior não encerrou a tempo.");
    }

    private static void Record(
        UpdaterDiagnostics diagnostics, UpdateTransaction transaction, string stage,
        string outcome, string code, string? detail, string dataRoot, bool telemetryAuthorized) =>
        diagnostics.RecordAsync(
            new UpdaterEvent(transaction.Id, stage, outcome, code, transaction.PreviousVersion,
                transaction.CandidateVersion, UpdaterDiagnostics.ResolveEnvironment()),
            detail,
            telemetryAuthorized, flushPending: false).GetAwaiter().GetResult();

    // O processo pode sair entre o Process.Start e a leitura de HasExited, e
    // o Windows nega a consulta (Win32Exception) ou a propriedade
    // (InvalidOperationException) no processo já encerrado -- o mesmo caso
    // "já se foi" que o health-check precisa tratar como exit, não como erro.
    private static bool HasExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return true;
        }
    }

    private static string Classify(Exception exception) => exception switch
    {
        TimeoutException => UpdaterEventCodes.ParentExitTimeout,
        CryptographicException => UpdaterEventCodes.ActiveRuntimeInvalid,
        InvalidDataException or FileNotFoundException => UpdaterEventCodes.ActiveRuntimeInvalid,
        UnauthorizedAccessException => UpdaterEventCodes.AccessDenied,
        IOException => UpdaterEventCodes.LocalIoFailed,
        InvalidOperationException => UpdaterEventCodes.LauncherStartFailed,
        _ => UpdaterEventCodes.Unexpected,
    };

    private static string DescribeFailure(ILocalizationService localization, Exception exception) => exception switch
    {
        TimeoutException => localization.GetString("Launcher.Error.ParentTimeout"),
        UnauthorizedAccessException => localization.GetString("Launcher.Error.AccessDenied"),
        CryptographicException or InvalidDataException => localization.GetString("Launcher.Error.Security"),
        FileNotFoundException => localization.GetString("Launcher.Error.MissingFiles"),
        _ => localization.GetString("Launcher.Error.Unexpected")
    };

    private static void ApplyStoredLanguage(LocalizationService localization, string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, "settings.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1_048_576) return;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("language", out var language)
                && language.GetString() is { } preference)
                localization.Apply(preference, CultureInfo.CurrentUICulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A falha ao ler uma preferência nunca pode impedir o rollback ou o startup.
        }
    }
}
