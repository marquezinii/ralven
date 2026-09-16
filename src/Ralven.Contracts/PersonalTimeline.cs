namespace Ralven.Contracts;

public enum PcChangeKind
{
    Hardware, Windows, GameMode, BackgroundCapture, LowDiskSpace,
    PointerAcceleration, StartupApps, GraphicsDriver
}

public sealed record PcObservation(
    DateTimeOffset CapturedAt, string HardwareSignature, string WindowsVersion,
    double FreeDiskGiB, WindowsGamingSettingState GameMode, WindowsGamingSettingState BackgroundCapture)
{
    public bool? PointerAccelerationEnabled { get; init; }
    public string HardwareDescription { get; init; } = string.Empty;
    public IReadOnlyList<string> StartupAppNames { get; init; } = [];
    public IReadOnlyList<string> GraphicsDriverVersions { get; init; } = [];

    // The compiler-generated record equality compares list properties by reference,
    // which breaks equality across a JSON round-trip. Compare their contents instead.
    public bool Equals(PcObservation? other) => other is not null
        && CapturedAt == other.CapturedAt && HardwareSignature == other.HardwareSignature
        && WindowsVersion == other.WindowsVersion && FreeDiskGiB.Equals(other.FreeDiskGiB)
        && GameMode == other.GameMode && BackgroundCapture == other.BackgroundCapture
        && PointerAccelerationEnabled == other.PointerAccelerationEnabled
        && HardwareDescription == other.HardwareDescription
        && StartupAppNames.SequenceEqual(other.StartupAppNames)
        && GraphicsDriverVersions.SequenceEqual(other.GraphicsDriverVersions);

    public override int GetHashCode() => HashCode.Combine(
        CapturedAt, HardwareSignature, WindowsVersion, FreeDiskGiB, GameMode, BackgroundCapture);
}

/// <summary>
/// A single detected difference between two observations. <see cref="PreviousValue"/>
/// and <see cref="CurrentValue"/> are null when the corresponding side has no
/// legible evidence to show -- never a placeholder or invented value.
/// </summary>
public sealed record PcChange(
    DateTimeOffset CapturedAt, PcChangeKind Kind, string? PreviousValue = null, string? CurrentValue = null);

public sealed record PersonalMeasurement(
    DateTimeOffset CapturedAt, PersonalUsage Usage, string Context, string HardwareSignature,
    string WindowsVersion, int SampleCount, double DurationSeconds,
    double? CpuPercent, double? GpuPercent, double? MemoryPercent, double? DiskPercent);

public sealed record PersonalWorkspace
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<PersonalOptimizationPreferencesDto> Profiles { get; init; } = [];
    public bool TrackingEnabled { get; init; }
    public PcObservation? Reference { get; init; }
    public PcObservation? LastObservation { get; init; }
    public IReadOnlyList<PcChange> Changes { get; init; } = [];
    public IReadOnlyList<PersonalMeasurement> Measurements { get; init; } = [];
}
