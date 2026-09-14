using System.IO;
using System.Text.Json;
using Ralven.App.Services;
using Ralven.Contracts;
using Xunit;

namespace Ralven.Tests.App;

public sealed class ElevatedBrokerClientTerminalReadTests
{
    private static readonly Guid TransactionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly IProgress<AppProgressUpdate> NoProgress = new Progress<AppProgressUpdate>();
    private static readonly ILocalizationService Localization = new LocalizationService(
        System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    private static BrokerEvent Event(long sequence, BrokerEventKind kind, string message, Guid? transactionId = null, bool? success = null) =>
        new()
        {
            SchemaVersion = 1,
            Sequence = sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = kind,
            TransactionId = transactionId ?? TransactionId,
            Message = message,
            Success = success
        };

    private static StringReader Reader(params BrokerEvent[] events)
    {
        var lines = events.Select(item => JsonSerializer.Serialize(item, RalvenJson.Options));
        return new StringReader(string.Join('\n', lines));
    }

    [Fact]
    public void BrokerEventContract_RoundTripsFailureMetadata()
    {
        var source = Event(1, BrokerEventKind.Failed, "failed", success: false) with
        {
            State = "rolled-back",
            ErrorCode = "transaction-not-committed",
            AppliedActionIds = ["action.one"]
        };

        var json = JsonSerializer.Serialize(source, RalvenJson.Options);
        var restored = JsonSerializer.Deserialize<BrokerEvent>(json, RalvenJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(source.Kind, restored.Kind);
        Assert.Equal(source.ErrorCode, restored.ErrorCode);
        Assert.Equal(source.AppliedActionIds, restored.AppliedActionIds);
    }

    // Regression: the old loop kept reading past the terminal event up to the
    // 128-event cap, so a plan with enough progress events pushed the terminal
    // out of the window and a successful elevated phase was reported failed.
    [Fact]
    public async Task ReadUntilTerminalAsync_StopsReadingAtTheTerminalEvent()
    {
        var events = new[]
        {
            Event(1, BrokerEventKind.Started, "started"),
            Event(2, BrokerEventKind.Completed, "done", success: true),
            // If the reader continued past the terminal it would parse this
            // line (schemaVersion 9) and throw.
            new BrokerEvent
            {
                SchemaVersion = 9,
                Sequence = 3,
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = BrokerEventKind.Completed,
                TransactionId = TransactionId,
                Message = "must not be read"
            }
        };

        var terminal = await ElevatedBrokerClient.ReadUntilTerminalAsync(
            Reader(events), TransactionId, NoProgress, Localization, CancellationToken.None);

        Assert.NotNull(terminal);
        Assert.Equal(BrokerEventKind.Completed, terminal.Kind);
        Assert.True(terminal.Success);
    }

    [Fact]
    public async Task ReadUntilTerminalAsync_TreatsRejectedAndFailedAsTerminal()
    {
        foreach (var kind in new[] { BrokerEventKind.Rejected, BrokerEventKind.Failed })
        {
            var terminal = await ElevatedBrokerClient.ReadUntilTerminalAsync(
                Reader(Event(1, kind, "stopped", success: false)),
                TransactionId,
                NoProgress,
                Localization,
                CancellationToken.None);

            Assert.Equal(kind, terminal!.Kind);
            Assert.False(terminal.Success);
        }
    }

    [Fact]
    public async Task ReadUntilTerminalAsync_EventCapExceededWithoutTerminal_Throws()
    {
        var events = Enumerable.Range(1, 200)
            .Select(i => Event(i, BrokerEventKind.Progress, $"p{i}"))
            .ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ElevatedBrokerClient.ReadUntilTerminalAsync(
                Reader(events), TransactionId, NoProgress, Localization, CancellationToken.None));
    }

    [Fact]
    public async Task ReadUntilTerminalAsync_ReachingEofWithoutTerminal_ReturnsNull()
    {
        var events = new[]
        {
            Event(1, BrokerEventKind.Progress, "p1"),
            Event(2, BrokerEventKind.Progress, "p2")
        };

        var terminal = await ElevatedBrokerClient.ReadUntilTerminalAsync(
            Reader(events), TransactionId, NoProgress, Localization, CancellationToken.None);

        Assert.Null(terminal);
    }

    [Theory]
    [InlineData(2, "broker-invalid-arguments")]
    [InlineData(3, "broker-pipe-connection-failed")]
    [InlineData(7, "broker-not-elevated")]
    [InlineData(-1, null)]
    public void ResolveMissingTerminalErrorCode_PreservesKnownProcessFailures(
        int exitCode,
        string? expected)
    {
        Assert.Equal(expected, ElevatedBrokerClient.ResolveMissingTerminalErrorCode(exitCode));
    }

    [Fact]
    public async Task ReadUntilTerminalAsync_OutOfOrderSequence_Throws()
    {
        var events = new[]
        {
            Event(1, BrokerEventKind.Progress, "p1"),
            Event(1, BrokerEventKind.Progress, "p1-duplicate")
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ElevatedBrokerClient.ReadUntilTerminalAsync(
                Reader(events), TransactionId, NoProgress, Localization, CancellationToken.None));
    }

    [Fact]
    public async Task ReadUntilTerminalAsync_TerminalConfirmingAnotherTransaction_Throws()
    {
        var events = new[]
        {
            Event(1, BrokerEventKind.Completed, "wrong-tx", transactionId: Guid.NewGuid(), success: true)
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ElevatedBrokerClient.ReadUntilTerminalAsync(
                Reader(events), TransactionId, NoProgress, Localization, CancellationToken.None));
    }

    [Fact]
    public async Task ReadUntilTerminalAsync_ReportsLocalizedBrokerPhaseHeadlines()
    {
        var captured = new List<AppProgressUpdate>();
        var progress = new InlineProgress<AppProgressUpdate>(captured.Add);
        var events = new[]
        {
            Event(1, BrokerEventKind.Progress, "applying something"),
            Event(2, BrokerEventKind.RollbackStarted, "restoring something"),
            Event(3, BrokerEventKind.Completed, "done", success: true)
        };

        await ElevatedBrokerClient.ReadUntilTerminalAsync(
            Reader(events), TransactionId, progress, Localization, CancellationToken.None);

        var progressUpdate = captured[0];
        var rollbackUpdate = captured[1];
        Assert.Equal("Applying administrative adjustments", progressUpdate.Headline);
        Assert.Equal("Restoring administrative settings", rollbackUpdate.Headline);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
