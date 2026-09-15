using System.IO;
using Ralven.App.Services;
using Ralven.Contracts;
using Xunit;

namespace Ralven.Tests.App;

public sealed class TelemetryErrorClassifierTests
{
    [Theory]
    [InlineData(typeof(TimeoutException), "timeout")]
    [InlineData(typeof(UnauthorizedAccessException), "access-denied")]
    [InlineData(typeof(IOException), "io")]
    [InlineData(typeof(InvalidDataException), "invalid-data")]
    public void ClassifyException_UsesOnlyFixedCategories(Type exceptionType, string expected)
    {
        var exception = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType)!);

        Assert.Equal(expected, TelemetryErrorClassifier.ClassifyException(exception));
    }

    [Fact]
    public void ClassifyException_UnknownExceptionType_FallsBackToUnexpected()
    {
        Assert.Equal("unexpected", TelemetryErrorClassifier.ClassifyException(new InvalidOperationException()));
    }

    [Fact]
    public void ClassifyException_OperationCanceled_MapsToCancelled()
    {
        Assert.Equal("cancelled", TelemetryErrorClassifier.ClassifyException(new OperationCanceledException()));
    }

    [Fact]
    public void ClassifyException_BrokerIntegrityFailure_UsesUnderlyingFixedCategory()
    {
        Assert.Equal(
            "invalid-data",
            TelemetryErrorClassifier.ClassifyException(
                new BrokerIntegrityException(new InvalidDataException())));
    }

    [Theory]
    [InlineData("broker-pipe-connection-failed", false, "io")]
    [InlineData("broker-not-elevated", false, "access-denied")]
    [InlineData("broker-invalid-arguments", false, "invalid-data")]
    [InlineData(null, false, "unexpected")]
    [InlineData("broker-pipe-connection-failed", true, "cancelled")]
    public void ClassifyBrokerFailure_UsesTheMatchingFixedCategory(
        string? errorCode,
        bool wasCancelled,
        string expected)
    {
        Assert.Equal(
            expected,
            TelemetryErrorClassifier.ClassifyBrokerFailure(errorCode, wasCancelled));
    }
}

public sealed class DisabledAnonymousTelemetryServiceTests
{
    [Fact]
    public void Instance_IsNeverEnabledAndNeverThrows()
    {
        var service = DisabledAnonymousTelemetryService.Instance;

        Assert.False(service.IsEnabled);
        service.Configure(enabled: true, includeOptionalData: true);
        Assert.False(service.IsEnabled);
        Assert.False(service.IncludesOptionalData);
    }

    [Fact]
    public async Task TrackAsync_CompletesWithoutSendingAnything()
    {
        var service = DisabledAnonymousTelemetryService.Instance;

        await service.TrackAsync(new AnonymousTelemetryEvent("optimization-completed", TimeSpan.Zero, "1.0.0"), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);
    }
}

public sealed class AnonymousTelemetryEventPrivacyTests
{
    [Fact]
    public void WithoutOptionalData_RemovesEveryOptionalFieldAndPreservesEssentialDiagnostics()
    {
        var original = new AnonymousTelemetryEvent(
            "optimization-failed",
            TimeSpan.FromSeconds(4),
            "1.5.1",
            ErrorCategory: "io",
            OsVersion: "Windows 11",
            SystemArchitecture: "x64",
            CpuModel: "Test CPU",
            GpuModel: "Test GPU",
            RamBucketGiB: 16,
            Profile: "Balanced",
            ActionIds: ["windows.game-mode.enable"],
            BugCode: Ralven.Contracts.BugCode.APP_OPT_ACTION_EXECUTION,
            FiveMInstallDetected: true,
            GtaEdition: "Legacy",
            OptimizationTargetCount: 3,
            WindowsBuild: 26100,
            DiskType: "SSD",
            FreeSpaceGiBBucket: 64,
            RunTimestamp: DateTimeOffset.UtcNow,
            DaysSinceLastRunBucket: 7,
            BackupCreated: true,
            BackupRestored: false,
            ElevationUsed: true,
            ProcessCountAtStart: 2,
            OperationId: Guid.NewGuid());

        var filtered = original.WithoutOptionalData();

        Assert.Equal(original.EventId, filtered.EventId);
        Assert.Equal(original.EventName, filtered.EventName);
        Assert.Equal(original.ErrorCategory, filtered.ErrorCategory);
        Assert.Equal(original.OsVersion, filtered.OsVersion);
        Assert.Equal(original.SystemArchitecture, filtered.SystemArchitecture);
        Assert.Equal(original.BugCode, filtered.BugCode);
        Assert.Equal(original.FiveMInstallDetected, filtered.FiveMInstallDetected);
        Assert.Equal(original.GtaEdition, filtered.GtaEdition);
        Assert.Equal(original.OptimizationTargetCount, filtered.OptimizationTargetCount);
        Assert.Null(filtered.CpuModel);
        Assert.Null(filtered.GpuModel);
        Assert.Null(filtered.RamBucketGiB);
        Assert.Null(filtered.Profile);
        Assert.Null(filtered.ActionIds);
        Assert.Null(filtered.WindowsBuild);
        Assert.Null(filtered.DiskType);
        Assert.Null(filtered.FreeSpaceGiBBucket);
        Assert.Null(filtered.RunTimestamp);
        Assert.Null(filtered.DaysSinceLastRunBucket);
        Assert.Null(filtered.BackupCreated);
        Assert.Null(filtered.BackupRestored);
        Assert.Null(filtered.ElevationUsed);
        Assert.Null(filtered.ProcessCountAtStart);
        Assert.Null(filtered.OperationId);
    }
}
