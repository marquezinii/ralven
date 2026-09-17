using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;
using Ralven.Contracts;
using Ralven.Core.Planning;
using Ralven.Windows.Infrastructure;

namespace Ralven.App.Services;

internal sealed class ProAccessRequiredException(string message) : UnauthorizedAccessException(message);

/// <summary>Local, bounded Pro data. Reading existing records and opting out never require a subscription.</summary>
public sealed class PersonalWorkspaceService
{
    private const int MaxChanges = 500;
    private const int MaxMeasurements = 120;
    private const int RetentionDays = 90;
    private const int MaxEvidenceLength = 200;
    private const int MaxStartupItems = 50;
    private const int MaxDriverEntries = 20;

    private readonly string directory;
    private readonly Func<CancellationToken, Task<bool>> authorizePro;
    private readonly ILocalizationService localization;
    private readonly IMouseAccelerationInspector mouseAcceleration;
    private readonly IWindowsApplicationInventoryInspector applicationInventory;
    private readonly IDriverVersionInspector driverVersion;
    private readonly bool inMemory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private PersonalWorkspace memory = new();
    private static readonly JsonSerializerOptions JsonOptions = new(RalvenJson.Options) { WriteIndented = true };

    public PersonalWorkspaceService(
        Func<CancellationToken, Task<bool>> authorizePro,
        bool inMemory = false,
        string? directory = null,
        ILocalizationService? localization = null,
        IMouseAccelerationInspector? mouseAcceleration = null,
        IWindowsApplicationInventoryInspector? applicationInventory = null,
        IDriverVersionInspector? driverVersion = null)
    {
        this.authorizePro = authorizePro;
        this.inMemory = inMemory;
        this.directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.Name, "Personal"));
        this.localization = localization ?? LocalizationService.Current;
        this.mouseAcceleration = mouseAcceleration ?? new WindowsMouseAccelerationInspector();
        this.applicationInventory = applicationInventory ?? new WindowsApplicationInventoryInspector();
        this.driverVersion = driverVersion ?? new WindowsDriverVersionInspector();
    }

    public async Task RequireProAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await authorizePro(cancellationToken).ConfigureAwait(false))
        {
            throw new ProAccessRequiredException(localization.GetString("Ultra.AccessRequired"));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task<PcObservation> CaptureObservationAsync(
        AppDiagnostic current, WindowsGamingControlsService gamingControls, CancellationToken cancellationToken) => Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.TotalMemoryGiB <= 0 || current.GpuNames.Count == 0
                || current.CpuName == localization.GetString("Diagnosis.CpuUnknown"))
                throw new InvalidOperationException("The hardware identity is incomplete.");
            var gaming = await gamingControls.ReadAsync(cancellationToken).ConfigureAwait(false);
            var mouse = mouseAcceleration.GetSnapshot();
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            var startup = await applicationInventory.InspectStartupAsync(cancellationToken).ConfigureAwait(false);
            var drivers = driverVersion.GetSnapshot();
            return new PcObservation(DateTimeOffset.UtcNow,
                HardwareProfileSignature.Compute(current.CpuName, current.GpuNames, current.TotalMemoryGiB),
                current.OsLabel, drive.AvailableFreeSpace / 1024d / 1024 / 1024, gaming.GameMode, gaming.BackgroundCapture)
            {
                PointerAccelerationEnabled = mouse.State == MouseAccelerationInspectionState.Available
                    ? mouse.AccelerationLevel > 0
                    : null,
                HardwareDescription = TrimTo($"{current.CpuName} · {string.Join(", ", current.GpuNames)}"),
                StartupAppNames = startup.StartupItems.Select(item => TrimTo(item.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxStartupItems).ToArray(),
                GraphicsDriverVersions = drivers.Video.Select(item => TrimTo($"{item.DeviceName} {item.DriverVersion}"))
                    .Take(MaxDriverEntries).ToArray()
            };
        }, cancellationToken);

    private static string TrimTo(string value, int maxLength = MaxEvidenceLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public async Task<PersonalWorkspace> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        // Existing records belong to the user, including after a subscription expires.
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = new StringBuilder(localization.GetString("Personal.Export.Title"));
        report.AppendLine().AppendLine(localization.GetString("Ultra.Measure.Help"));
        report.AppendLine().AppendLine(localization.GetString("Ultra.Tracking.Title"));
        foreach (var change in state.Changes)
            report.AppendLine($"{change.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture)} · {localization.GetString($"Ultra.Change.{change.Kind}")}");
        report.AppendLine().AppendLine(localization.GetString("Ultra.Measure.Title"));
        foreach (var measurement in state.Measurements)
        {
            string Metric(double? value) => value is { } number
                ? number.ToString("0.0", localization.CurrentCulture) + "%"
                : localization.GetString("Ultra.Unavailable");
            report.AppendLine(localization.Format("Ultra.Measure.Record", measurement.Context,
                localization.GetString($"Ultra.Usage.{measurement.Usage}"),
                measurement.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture),
                Metric(measurement.CpuPercent), Metric(measurement.GpuPercent),
                Metric(measurement.MemoryPercent), Metric(measurement.DiskPercent)));
        }
        // Do not export hardware identifiers, raw workspace data or account information.
        return ReportSanitizer.Sanitize(report.ToString());
    }

    public Task<PersonalWorkspace> SaveProfileAsync(PersonalOptimizationPreferencesDto profile, CancellationToken cancellationToken = default)
    {
        _ = PersonalOptimizationPolicy.CreateOptions(profile);
        return ChangeAsync(state => state with
        {
            Profiles = state.Profiles.Where(item => item.Usage != profile.Usage).Append(profile).ToArray()
        }, true, cancellationToken);
    }

    public Task<PersonalWorkspace> SetTrackingAsync(bool enabled, PcObservation? observation, CancellationToken cancellationToken = default)
    {
        if (enabled)
        {
            ArgumentNullException.ThrowIfNull(observation);
            ValidateObservation(observation);
        }

        return ChangeAsync(state => state with
        {
            TrackingEnabled = enabled,
            Reference = enabled ? observation : state.Reference,
            LastObservation = enabled ? observation : state.LastObservation
        }, enabled, cancellationToken);
    }

    public Task<PersonalWorkspace> ObserveAsync(PcObservation observation, CancellationToken cancellationToken = default)
    {
        ValidateObservation(observation);
        return ChangeAsync(state => !state.TrackingEnabled ? state : state with
        {
            LastObservation = observation,
            Changes = TrimByAge(state.Changes.Concat(PersonalTimelineAnalysis.DetectChanges(state.LastObservation, observation)),
                change => change.CapturedAt, MaxChanges)
        }, true, cancellationToken);
    }

    public async Task<PersonalWorkspace> MeasureAsync(
        PersonalUsage usage, string context, PcObservation observation,
        IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(usage) || string.IsNullOrWhiteSpace(context) || context.Trim().Length > 80)
        {
            throw new ArgumentException(localization.GetString("Ultra.Measure.ContextRequired"));
        }

        ValidateObservation(observation);
        await RequireProAsync(cancellationToken).ConfigureAwait(false);
        using var provider = new WindowsLiveSystemMetricsProvider();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var elapsed = Stopwatch.StartNew();
        var samples = new List<LiveSystemMetricsSnapshot>();
        while (samples.Count < 30 && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            samples.Add(await provider.CaptureAsync(cancellationToken).ConfigureAwait(false));
            progress.Report(samples.Count);
        }

        var measurement = Summarize(usage, context.Trim(), observation, samples, elapsed.Elapsed.TotalSeconds);
        // Authorization was checked before this finite read-only operation.
        // Expiry must not discard a completed measurement or interrupt a write.
        return await ChangeAsync(state => state with
        {
            Measurements = TrimByAge(state.Measurements.Append(measurement), item => item.CapturedAt, MaxMeasurements)
        }, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps entries from the last <see cref="RetentionDays"/> days, capped at <paramref name="maxCount"/> as a safeguard.</summary>
    private static IReadOnlyList<T> TrimByAge<T>(IEnumerable<T> items, Func<T, DateTimeOffset> capturedAt, int maxCount)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        return items.Where(item => capturedAt(item) >= cutoff).TakeLast(maxCount).ToArray();
    }

    internal static PersonalMeasurement Summarize(
        PersonalUsage usage, string context, PcObservation observation,
        IReadOnlyList<LiveSystemMetricsSnapshot> samples, double durationSeconds) => new(
            DateTimeOffset.UtcNow, usage, context, observation.HardwareSignature, observation.WindowsVersion,
            samples.Count, durationSeconds,
            Average(samples.Select(item => item.CpuPercent)), Average(samples.Select(item => item.GpuPercent)),
            Average(samples.Select(item => item.MemoryPercent)), Average(samples.Select(item => item.DiskPercent)));

    private static double? Average(IEnumerable<double?> source)
    {
        var values = source.ToArray();
        var available = values.Where(value => value is >= 0 and <= 100).Select(value => value!.Value).ToArray();
        return available.Length >= Math.Max(1, (int)Math.Ceiling(values.Length * 0.8)) ? available.Average() : null;
    }

    private async Task<PersonalWorkspace> ChangeAsync(
        Func<PersonalWorkspace, PersonalWorkspace> update, bool requiresPro, CancellationToken cancellationToken)
    {
        if (requiresPro) await RequireProAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var next = update(current);
            Validate(next);
            if (inMemory) { memory = next; return next; }
            EnsureSafePath();
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"workspace-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, JsonOptions), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSafePath();
                File.Move(temporary, Path.Combine(directory, "workspace.json"), true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return next;
        }
        finally { gate.Release(); }
    }

    private async Task<PersonalWorkspace> ReadAsync(CancellationToken cancellationToken)
    {
        if (inMemory) return memory;
        EnsureSafePath();
        var path = Path.Combine(directory, "workspace.json");
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        if (stream.Length > 512 * 1024) throw new InvalidDataException("Personal workspace exceeds its size limit.");
        var state = await JsonSerializer.DeserializeAsync<PersonalWorkspace>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Personal workspace is empty.");
        Validate(state);
        return state;
    }

    private void EnsureSafePath()
    {
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Personal workspace cannot follow a reparse point.");
        }
        var file = new FileInfo(Path.Combine(directory, "workspace.json"));
        if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Personal workspace cannot follow a reparse point.");
    }

    private static void Validate(PersonalWorkspace state)
    {
        if (state.SchemaVersion != 1 || state.Profiles is null || state.Profiles.Count > 4
            || state.Profiles.Any(item => item is null) || state.Profiles.Select(item => item.Usage).Distinct().Count() != state.Profiles.Count
            || state.Changes is null || state.Changes.Count > MaxChanges || state.Changes.Any(item => item is null || !Enum.IsDefined(item.Kind)
                || (item.PreviousValue?.Length ?? 0) > MaxEvidenceLength || (item.CurrentValue?.Length ?? 0) > MaxEvidenceLength)
            || state.Measurements is null || state.Measurements.Count > MaxMeasurements
            || state.Measurements.Any(item => item is null || !Enum.IsDefined(item.Usage)
                || string.IsNullOrWhiteSpace(item.Context) || item.Context.Length > 80
                || item.HardwareSignature is not { Length: 64 } || item.WindowsVersion is not { Length: > 0 and <= 200 }
                || item.SampleCount != 30 || !double.IsFinite(item.DurationSeconds) || item.DurationSeconds <= 0
                || !ValidMetric(item.CpuPercent) || !ValidMetric(item.GpuPercent) || !ValidMetric(item.MemoryPercent) || !ValidMetric(item.DiskPercent)))
            throw new InvalidDataException("Personal workspace is invalid; existing data was preserved.");
        foreach (var profile in state.Profiles) _ = PersonalOptimizationPolicy.CreateOptions(profile);
        if (state.Reference is not null) ValidateObservation(state.Reference);
        if (state.LastObservation is not null) ValidateObservation(state.LastObservation);
        if (state.TrackingEnabled && (state.Reference is null || state.LastObservation is null))
            throw new InvalidDataException("Personal tracking has no reference.");
    }

    private static bool ValidMetric(double? value) => value is null or (>= 0 and <= 100);

    private static void ValidateObservation(PcObservation observation)
    {
        if (observation.HardwareSignature is not { Length: 64 } || observation.WindowsVersion is not { Length: > 0 and <= 200 }
            || !double.IsFinite(observation.FreeDiskGiB) || observation.FreeDiskGiB < 0
            || !Enum.IsDefined(observation.GameMode) || !Enum.IsDefined(observation.BackgroundCapture)
            || observation.HardwareDescription.Length > MaxEvidenceLength
            || observation.StartupAppNames is null || observation.StartupAppNames.Count > MaxStartupItems
            || observation.StartupAppNames.Any(item => item is null || item.Length > MaxEvidenceLength)
            || observation.GraphicsDriverVersions is null || observation.GraphicsDriverVersions.Count > MaxDriverEntries
            || observation.GraphicsDriverVersions.Any(item => item is null || item.Length > MaxEvidenceLength))
            throw new InvalidDataException("The PC observation is incomplete.");
    }
}
