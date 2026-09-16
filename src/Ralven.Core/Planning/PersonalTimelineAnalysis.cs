using Ralven.Contracts;

namespace Ralven.Core.Planning;

/// <summary>A historical (not strictly controlled) comparison between two measurements, listing every condition that differs instead of refusing.</summary>
public sealed record HistoricalComparisonResult(
    IReadOnlyList<string> DifferingConditions, PersonalMeasurement First, PersonalMeasurement Second);

/// <summary>
/// A change paired with whatever measured shift happened around it. <see cref="SymptomSummary"/>
/// is an observed association, never a claimed cause.
/// </summary>
public sealed record PcChangeAssociation(PcChange Change, string? SymptomSummary);

/// <summary>Pure comparison and change-detection rules for the personal PC timeline. No I/O.</summary>
public static class PersonalTimelineAnalysis
{
    private static readonly TimeSpan AssociationWindow = TimeSpan.FromHours(24);
    private const double AssociationThresholdPoints = 15;

    public static IReadOnlyList<PcChange> DetectChanges(PcObservation? previous, PcObservation current)
    {
        if (previous is null) return [];
        var result = new List<PcChange>();
        if (previous.HardwareSignature != current.HardwareSignature)
            result.Add(new(current.CapturedAt, PcChangeKind.Hardware, previous.HardwareDescription, current.HardwareDescription));
        if (previous.WindowsVersion != current.WindowsVersion)
            result.Add(new(current.CapturedAt, PcChangeKind.Windows, previous.WindowsVersion, current.WindowsVersion));
        if (Known(previous.GameMode) && Known(current.GameMode) && previous.GameMode != current.GameMode)
            result.Add(new(current.CapturedAt, PcChangeKind.GameMode, previous.GameMode.ToString(), current.GameMode.ToString()));
        if (Known(previous.BackgroundCapture) && Known(current.BackgroundCapture) && previous.BackgroundCapture != current.BackgroundCapture)
            result.Add(new(current.CapturedAt, PcChangeKind.BackgroundCapture, previous.BackgroundCapture.ToString(), current.BackgroundCapture.ToString()));
        if (previous.FreeDiskGiB >= 10 && current.FreeDiskGiB < 10)
            result.Add(new(current.CapturedAt, PcChangeKind.LowDiskSpace,
                previous.FreeDiskGiB.ToString("0.0"), current.FreeDiskGiB.ToString("0.0")));
        if (previous.PointerAccelerationEnabled.HasValue && current.PointerAccelerationEnabled.HasValue
            && previous.PointerAccelerationEnabled != current.PointerAccelerationEnabled)
            result.Add(new(current.CapturedAt, PcChangeKind.PointerAcceleration,
                previous.PointerAccelerationEnabled.ToString(), current.PointerAccelerationEnabled.ToString()));

        var previousStartup = previous.StartupAppNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentStartup = current.StartupAppNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var added in currentStartup.Except(previousStartup))
            result.Add(new(current.CapturedAt, PcChangeKind.StartupApps, null, added));
        foreach (var removed in previousStartup.Except(currentStartup))
            result.Add(new(current.CapturedAt, PcChangeKind.StartupApps, removed, null));

        var previousDrivers = previous.GraphicsDriverVersions
            .Select(ParseDriverEntry).Where(entry => entry is not null)
            .ToDictionary(entry => entry!.Value.Device, entry => entry!.Value.Version);
        foreach (var entry in current.GraphicsDriverVersions.Select(ParseDriverEntry).Where(entry => entry is not null))
        {
            if (previousDrivers.TryGetValue(entry!.Value.Device, out var previousVersion) && previousVersion != entry.Value.Version)
                result.Add(new(current.CapturedAt, PcChangeKind.GraphicsDriver, previousVersion, entry.Value.Version));
        }

        return result;
    }

    /// <summary>Splits a "{DeviceName} {DriverVersion}" entry back apart on its last space.</summary>
    private static (string Device, string Version)? ParseDriverEntry(string entry)
    {
        var separator = entry.LastIndexOf(' ');
        return separator <= 0 ? null : (entry[..separator], entry[(separator + 1)..]);
    }

    private static bool Known(WindowsGamingSettingState state) => state is
        WindowsGamingSettingState.Enabled or WindowsGamingSettingState.Disabled or WindowsGamingSettingState.NotConfigured;

    public static bool CanCompare(PersonalMeasurement first, PersonalMeasurement second) =>
        first.Usage == second.Usage
        && string.Equals(first.Context, second.Context, StringComparison.OrdinalIgnoreCase)
        && first.HardwareSignature == second.HardwareSignature
        && first.WindowsVersion == second.WindowsVersion
        && first.SampleCount == 30 && second.SampleCount == 30
        && first.DurationSeconds is >= 29 and <= 45 && second.DurationSeconds is >= 29 and <= 45
        && ((first.CpuPercent.HasValue && second.CpuPercent.HasValue)
            || (first.GpuPercent.HasValue && second.GpuPercent.HasValue)
            || (first.MemoryPercent.HasValue && second.MemoryPercent.HasValue)
            || (first.DiskPercent.HasValue && second.DiskPercent.HasValue));

    /// <summary>Unlike <see cref="CanCompare"/>, never refuses -- it lists every differing condition instead, so callers can present an honest historical comparison.</summary>
    public static HistoricalComparisonResult CompareHistorical(PersonalMeasurement first, PersonalMeasurement second)
    {
        var differences = new List<string>();
        if (first.Usage != second.Usage) differences.Add(nameof(PersonalMeasurement.Usage));
        if (!string.Equals(first.Context, second.Context, StringComparison.OrdinalIgnoreCase)) differences.Add(nameof(PersonalMeasurement.Context));
        if (first.HardwareSignature != second.HardwareSignature) differences.Add(nameof(PersonalMeasurement.HardwareSignature));
        if (first.WindowsVersion != second.WindowsVersion) differences.Add(nameof(PersonalMeasurement.WindowsVersion));
        return new(differences, first, second);
    }

    /// <summary>
    /// Pairs each change with the closest compatible measurements just before and after it
    /// (within <see cref="AssociationWindow"/>) and flags a large shift as a possible symptom.
    /// This is a simple heuristic, not causal detection: a null <see cref="PcChangeAssociation.SymptomSummary"/>
    /// means no comparable measurement was close enough, not that nothing happened.
    /// </summary>
    public static IReadOnlyList<PcChangeAssociation> FindAssociations(
        IReadOnlyList<PcChange> changes, IReadOnlyList<PersonalMeasurement> measurements)
    {
        var result = new List<PcChangeAssociation>();
        foreach (var change in changes)
        {
            var before = measurements
                .Where(measurement => measurement.CapturedAt < change.CapturedAt && change.CapturedAt - measurement.CapturedAt <= AssociationWindow)
                .OrderByDescending(measurement => measurement.CapturedAt).FirstOrDefault();
            var after = measurements
                .Where(measurement => measurement.CapturedAt >= change.CapturedAt && measurement.CapturedAt - change.CapturedAt <= AssociationWindow)
                .OrderBy(measurement => measurement.CapturedAt).FirstOrDefault();
            result.Add(new(change, before is null || after is null || !CanCompare(before, after)
                ? null
                : DescribeShift(before, after)));
        }
        return result;
    }

    // ponytail: fixed 15pp threshold and 24h window are an initial heuristic;
    // revisit with real data before treating this as regression detection.
    private static string? DescribeShift(PersonalMeasurement before, PersonalMeasurement after)
    {
        (string Name, double? Before, double? After)[] metrics =
        [
            ("Cpu", before.CpuPercent, after.CpuPercent),
            ("Gpu", before.GpuPercent, after.GpuPercent),
            ("Memory", before.MemoryPercent, after.MemoryPercent),
            ("Disk", before.DiskPercent, after.DiskPercent)
        ];
        var shifted = metrics
            .Where(metric => metric.Before.HasValue && metric.After.HasValue
                && Math.Abs(metric.After.Value - metric.Before.Value) >= AssociationThresholdPoints)
            .ToArray();
        return shifted.Length == 0 ? null : string.Join(", ", shifted.Select(metric => metric.Name));
    }
}
