using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Ralven.Contracts;

namespace Ralven.Broker;

internal sealed class BrokerPipeException : IOException
{
    public BrokerPipeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class NamedPipeEventWriter : IAsyncDisposable
{
    // The unelevated server already applies CurrentUserOnly to the pipe ACL.
    // Repeating it on this elevated client makes .NET compare token owners,
    // which can differ across UAC even when both processes belong to one user.
    internal const PipeOptions ClientPipeOptions = PipeOptions.Asynchronous;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly NamedPipeClientStream pipe;
    private readonly StreamWriter writer;
    private readonly object gate = new();
    private long sequence;
    private bool disposed;

    private NamedPipeEventWriter(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public static async Task<NamedPipeEventWriter> ConnectAsync(
        Guid pipeId,
        CancellationToken cancellationToken)
    {
        if (pipeId == Guid.Empty)
        {
            throw new ArgumentException("Pipe ID cannot be empty.", nameof(pipeId));
        }

        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: pipeId.ToString("N"),
            direction: PipeDirection.Out,
            options: ClientPipeOptions,
            impersonationLevel: TokenImpersonationLevel.Identification);

        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            return new NamedPipeEventWriter(pipe);
        }
        catch (Exception exception) when (exception is IOException
            or OperationCanceledException
            or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BrokerPipeException("Could not connect to the local progress pipe.", exception);
        }
    }

    public void Publish(BrokerEventKind kind, string message, Action<BrokerEventBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var builder = new BrokerEventBuilder
            {
                Kind = kind,
                Message = message
            };
            configure?.Invoke(builder);

            var item = builder.Build(++sequence);
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(item, RalvenJson.Options));
            }
            catch (Exception exception) when (exception is IOException
                or ObjectDisposedException
                or InvalidOperationException)
            {
                throw new BrokerPipeException("The local progress pipe was disconnected.", exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed record BrokerEventBuilder
{
    public required BrokerEventKind Kind { get; init; }

    public required string Message { get; init; }

    public Guid? TransactionId { get; set; }

    public string? ActionId { get; set; }

    public int? CompletedWeight { get; set; }

    public int? TotalWeight { get; set; }

    public bool? Success { get; set; }

    public string? State { get; set; }

    public string? ErrorCode { get; set; }

    public IReadOnlyList<string>? AppliedActionIds { get; set; }

    public BrokerEvent Build(long sequence)
    {
        return new BrokerEvent
        {
            SchemaVersion = BrokerEventSchema.CurrentVersion,
            Sequence = sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = Kind,
            TransactionId = TransactionId,
            ActionId = ActionId,
            Message = Message,
            CompletedWeight = CompletedWeight,
            TotalWeight = TotalWeight,
            Success = Success,
            State = State,
            ErrorCode = ErrorCode,
            AppliedActionIds = AppliedActionIds
        };
    }
}
