using Ralven.Contracts;
using Ralven.Core.Planning;
using Xunit;

namespace Ralven.Tests.Core;

public sealed class PersonalTimelineAnalysisTests
{
    private static PcObservation Observation => new(DateTimeOffset.UtcNow, new string('a', 64),
        "Windows 11", 40, WindowsGamingSettingState.Enabled, WindowsGamingSettingState.Disabled)
    {
        HardwareDescription = "Ryzen 5800X · RTX 3070",
        StartupAppNames = ["Discord"],
        GraphicsDriverVersions = ["NVIDIA GeForce RTX 3070 31.0.15.3623"]
    };

    [Fact]
    public void UnrelatedFieldChangesDoNotDisguiseAsAHardwareChange()
    {
        var next = Observation with { HardwareDescription = "Ryzen 5800X · RTX 4070" };
        // The hash (identity) did not change, so nothing fires even though the display text did.
        Assert.Empty(PersonalTimelineAnalysis.DetectChanges(Observation, next));
    }

    [Fact]
    public void HardwareChangeCarriesReadableEvidence()
    {
        var next = Observation with
        {
            HardwareSignature = new string('b', 64),
            HardwareDescription = "Ryzen 5800X · RTX 4070"
        };
        var change = Assert.Single(PersonalTimelineAnalysis.DetectChanges(Observation, next));
        Assert.Equal(PcChangeKind.Hardware, change.Kind);
        Assert.Equal("Ryzen 5800X · RTX 3070", change.PreviousValue);
        Assert.Equal("Ryzen 5800X · RTX 4070", change.CurrentValue);
    }

    [Fact]
    public void StartupAppAdditionAndRemovalAreReportedSeparately()
    {
        var next = Observation with { StartupAppNames = ["Steam"] };
        var changes = PersonalTimelineAnalysis.DetectChanges(Observation, next);
        Assert.Contains(changes, change => change.Kind == PcChangeKind.StartupApps && change.PreviousValue == "Discord" && change.CurrentValue is null);
        Assert.Contains(changes, change => change.Kind == PcChangeKind.StartupApps && change.PreviousValue is null && change.CurrentValue == "Steam");
    }

    [Fact]
    public void GraphicsDriverVersionBumpOnTheSameDeviceIsReported()
    {
        var next = Observation with { GraphicsDriverVersions = ["NVIDIA GeForce RTX 3070 32.0.15.6094"] };
        var change = Assert.Single(PersonalTimelineAnalysis.DetectChanges(Observation, next));
        Assert.Equal(PcChangeKind.GraphicsDriver, change.Kind);
        Assert.Equal("31.0.15.3623", change.PreviousValue);
        Assert.Equal("32.0.15.6094", change.CurrentValue);
    }

    [Fact]
    public void CompareHistoricalListsDifferencesInsteadOfRefusing()
    {
        var first = new PersonalMeasurement(DateTimeOffset.UtcNow, PersonalUsage.Gaming, "Scene", new string('a', 64), "Windows 10", 30, 30, 10, 10, 10, 10);
        var second = first with { WindowsVersion = "Windows 11", HardwareSignature = new string('b', 64) };
        var result = PersonalTimelineAnalysis.CompareHistorical(first, second);
        Assert.Contains(nameof(PersonalMeasurement.WindowsVersion), result.DifferingConditions);
        Assert.Contains(nameof(PersonalMeasurement.HardwareSignature), result.DifferingConditions);
        Assert.DoesNotContain(nameof(PersonalMeasurement.Usage), result.DifferingConditions);
    }

    [Fact]
    public void FindAssociationsFlagsALargeShiftWithinTheWindowAndIgnoresDistantOrIncompatibleMeasurements()
    {
        var change = new PcChange(DateTimeOffset.UtcNow, PcChangeKind.Windows, "Windows 10", "Windows 11");
        PersonalMeasurement Measurement(TimeSpan offset, double cpu) => new(
            change.CapturedAt + offset, PersonalUsage.Gaming, "Scene", new string('a', 64), "Windows 11", 30, 30, cpu, null, null, null);
        var measurements = new[]
        {
            Measurement(TimeSpan.FromHours(-1), 20),
            Measurement(TimeSpan.FromHours(1), 60),
            Measurement(TimeSpan.FromHours(48), 95) // outside the 24h window on either side
        };
        var association = Assert.Single(PersonalTimelineAnalysis.FindAssociations([change], measurements));
        Assert.Equal("Cpu", association.SymptomSummary);
    }

    [Fact]
    public void FindAssociationsReturnsNullSymptomWhenMeasurementsAreMissingOrIncomparable()
    {
        var change = new PcChange(DateTimeOffset.UtcNow, PcChangeKind.Windows, "Windows 10", "Windows 11");
        Assert.Null(PersonalTimelineAnalysis.FindAssociations([change], []).Single().SymptomSummary);
    }
}
