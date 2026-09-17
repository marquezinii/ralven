using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Windows.Infrastructure;
using Microsoft.Win32;

namespace Ralven.Windows.Actions;

/// <summary>
/// Read-only diagnostic that never changes the machine. A failure to read a
/// signal degrades to a generic message instead of aborting the run.
/// </summary>
public sealed class BottleneckDiagnosisAction : ReadOnlyDiagnosticAction
{
    private readonly ISystemResourceInspector inspector;

    public BottleneckDiagnosisAction(ISystemResourceInspector inspector)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseBottleneck);

    protected override string Describe()
    {
        try
        {
            return Classify(inspector.GetSnapshot());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return WindowsActionText.Format("ActionResults.Bottleneck.Unavailable");
        }
    }

    internal static string Classify(SystemResourceSnapshot snapshot)
    {
        if (DiagnosticSignals.IsMemoryUnderPressure(snapshot))
        {
            return WindowsActionText.Format("ActionResults.Bottleneck.MemoryPressure");
        }

        if (snapshot.LogicalProcessorCount <= 4)
        {
            return WindowsActionText.Format("ActionResults.Bottleneck.CpuLimited");
        }

        if (snapshot.SystemDriveFreeBytes / (double)DiagnosticSignals.GiB < 8)
        {
            return WindowsActionText.Format("ActionResults.Bottleneck.LowDiskSpace");
        }

        return WindowsActionText.Format("ActionResults.Bottleneck.Balanced");
    }
}

public sealed class OverlaySoftwareDetectionAction : ReadOnlyDiagnosticAction
{
    private readonly IOverlaySoftwareInspector inspector;

    public OverlaySoftwareDetectionAction(IOverlaySoftwareInspector inspector)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DetectOverlaysAndCaptureSoftware);

    protected override string Describe()
    {
        var found = inspector.DetectRunningOverlayNames();
        if (found.Count == 0)
        {
            return WindowsActionText.Format("ActionResults.Overlays.NoneDetected");
        }

        var message = WindowsActionText.Format("ActionResults.Overlays.Detected", string.Join(", ", found));
        if (found.Any(name => name.Contains("ShadowPlay", StringComparison.OrdinalIgnoreCase)))
        {
            // "NVIDIA Share" is the actual process behind Instant Replay; its
            // presence is the closest reliable, read-only signal this
            // product has for it. Freestyle filters run inside the same
            // overlay and have no separate process signal, so this can only
            // suggest checking manually, never assert filters are active.
            message += WindowsActionText.Format("ActionResults.Overlays.NvidiaSuffix");
        }

        return message;
    }
}

public sealed class FiveMLegacyLogReaderAction : ReadOnlyDiagnosticAction
{
    private readonly string fiveMAppRoot;

    public FiveMLegacyLogReaderAction(string fiveMAppRoot)
    {
        this.fiveMAppRoot = SafePath.Normalize(fiveMAppRoot);
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.ReadFiveMLegacyLogs);

    protected override string Describe()
    {
        var logsDirectory = Path.Combine(fiveMAppRoot, "logs");
        if (!Directory.Exists(logsDirectory))
        {
            return WindowsActionText.Format("ActionResults.FiveMLogs.NoneFound");
        }

        FileInfo? latest;
        try
        {
            latest = new DirectoryInfo(logsDirectory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return WindowsActionText.Format("ActionResults.FiveMLogs.ListFailed");
        }

        if (latest is null)
        {
            return WindowsActionText.Format("ActionResults.FiveMLogs.NoneFound");
        }

        var header = WindowsActionText.Format(
            "ActionResults.FiveMLogs.Header",
            latest.Name,
            FormatAge(DateTimeOffset.UtcNow - latest.LastWriteTimeUtc));
        try
        {
            var errorHits = CountPossibleErrors(latest.FullName);
            return errorHits > 0
                ? WindowsActionText.Format("ActionResults.FiveMLogs.PossibleErrors", header, errorHits)
                : WindowsActionText.Format("ActionResults.FiveMLogs.NoPossibleErrors", header);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WindowsActionText.Format("ActionResults.FiveMLogs.ReadFailed", header);
        }
    }

    // Separa por '\r' e '\n' para contar as mesmas linhas que StreamReader.ReadLine
    // contava antes, inclusive em um log com terminador solitário.
    private static int CountPossibleErrors(string path) => FiveMLogTailReader
        .ReadTail(path)
        .Split('\r', '\n')
        .Count(line => line.Contains("error", StringComparison.OrdinalIgnoreCase));

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalDays >= 1)
        {
            var days = (int)age.TotalDays;
            return WindowsActionText.Format(
                days == 1 ? "ActionResults.Age.Day" : "ActionResults.Age.Days",
                days);
        }

        if (age.TotalHours >= 1)
        {
            var hours = (int)age.TotalHours;
            return WindowsActionText.Format(
                hours == 1 ? "ActionResults.Age.Hour" : "ActionResults.Age.Hours",
                hours);
        }

        var minutes = Math.Max(1, (int)age.TotalMinutes);
        return WindowsActionText.Format(
            minutes == 1 ? "ActionResults.Age.Minute" : "ActionResults.Age.Minutes",
            minutes);
    }
}

public sealed class PerformanceDiagnosticsGuideAction : ReadOnlyDiagnosticAction
{
    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.GuidePerformanceDiagnostics);

    protected override string Describe()
    {
        return WindowsActionText.Format("ActionResults.PerformanceGuide.Instructions");
    }
}

public sealed class NetworkHealthDiagnosisAction : ReadOnlyDiagnosticAction
{
    private readonly INetworkHealthInspector inspector;

    public NetworkHealthDiagnosisAction(INetworkHealthInspector inspector)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseNetworkHealth);

    protected override string Describe()
    {
        try
        {
            return Classify(inspector.GetSnapshot());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return WindowsActionText.Format("ActionResults.Network.Unavailable");
        }
    }

    internal static string Classify(NetworkHealthSnapshot snapshot)
    {
        if (!snapshot.HasActiveInterface)
        {
            return WindowsActionText.Format("ActionResults.Network.NoActiveInterface");
        }

        var link = DescribeLink(snapshot);
        if (snapshot.DiscardedPackets > 0 || snapshot.ErrorPackets > 0)
        {
            return WindowsActionText.Format(
                "ActionResults.Network.ErrorsDetected",
                snapshot.DiscardedPackets,
                snapshot.ErrorPackets,
                link);
        }

        return WindowsActionText.Format("ActionResults.Network.Healthy", link);
    }

    private static string DescribeLink(NetworkHealthSnapshot snapshot)
    {
        if (snapshot.ActiveInterfaceType is not { } type || snapshot.LinkSpeedBitsPerSecond is not { } speed)
        {
            return string.Empty;
        }

        var speedLabel = speed >= 1_000_000_000
            ? $"{speed / 1_000_000_000d:0.#} Gbps"
            : $"{speed / 1_000_000d:0.#} Mbps";
        return WindowsActionText.Format("ActionResults.Network.LinkSuffix", type, speedLabel);
    }
}

public sealed class ThermalDiagnosisAction : ReadOnlyDiagnosticAction
{
    private readonly IThermalInspector inspector;

    public ThermalDiagnosisAction(IThermalInspector inspector)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseThermalThrottling);

    protected override string Describe() => Classify(inspector.GetSnapshot());

    internal static string Classify(ThermalSnapshot snapshot)
    {
        if (!snapshot.IsAvailable || snapshot.HighestCelsius is not { } celsius)
        {
            return WindowsActionText.Format("ActionResults.Thermal.Unavailable");
        }

        return DiagnosticSignals.IsTemperatureElevated(snapshot)
            ? WindowsActionText.Format("ActionResults.Thermal.Elevated", celsius)
            : WindowsActionText.Format("ActionResults.Thermal.Normal", celsius);
    }
}

public sealed class PagefileCommitDiagnosisAction : ReadOnlyDiagnosticAction
{
    private const double LowAvailablePageFileRatio = 0.10d;

    private readonly ISystemResourceInspector inspector;

    public PagefileCommitDiagnosisAction(ISystemResourceInspector inspector)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnosePagefileCommit);

    protected override string Describe()
    {
        try
        {
            return Classify(inspector.GetSnapshot());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return WindowsActionText.Format("ActionResults.Pagefile.Unavailable");
        }
    }

    internal static string Classify(SystemResourceSnapshot snapshot)
    {
        if (snapshot.CommitLimitBytes <= 0)
        {
            return WindowsActionText.Format("ActionResults.Pagefile.CommitUnavailable");
        }

        var availableRatio = (double)snapshot.AvailableCommitBytes / snapshot.CommitLimitBytes;
        var totalGiB = snapshot.CommitLimitBytes / (double)DiagnosticSignals.GiB;

        return availableRatio < LowAvailablePageFileRatio
            ? WindowsActionText.Format("ActionResults.Pagefile.NearLimit", totalGiB)
            : WindowsActionText.Format("ActionResults.Pagefile.Healthy", totalGiB);
    }
}

public sealed class CacheIndexIntegrityDiagnosisAction : ReadOnlyDiagnosticAction
{
    private readonly string fiveMAppRoot;

    public CacheIndexIntegrityDiagnosisAction(string fiveMAppRoot)
    {
        this.fiveMAppRoot = SafePath.Normalize(fiveMAppRoot);
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseCacheIntegrity);

    protected override string Describe()
    {
        var dataRoot = Path.Combine(fiveMAppRoot, "data");
        var existing = new[]
        {
            Path.Combine(dataRoot, "server-cache", "content_index.xml"),
            Path.Combine(dataRoot, "server-cache-priv", "content_index.xml")
        }.Where(File.Exists).ToArray();

        if (existing.Length == 0)
        {
            return WindowsActionText.Format("ActionResults.CacheIndex.NoneFound");
        }

        var corrupted = existing
            .Where(path => !IsWellFormedXml(path))
            .Select(path => Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path))
            .ToArray();

        return corrupted.Length > 0
            ? WindowsActionText.Format("ActionResults.CacheIndex.Corrupted", string.Join(", ", corrupted))
            : WindowsActionText.Format("ActionResults.CacheIndex.Healthy");
    }

    private static bool IsWellFormedXml(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = System.Xml.XmlReader.Create(stream);
            while (reader.Read())
            {
            }

            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked or inaccessible file is not evidence of corruption.
            return true;
        }
    }
}

public sealed class GpuVendorDetectionAction : ReadOnlyDiagnosticAction
{
    private static readonly (string Vendor, string Link)[] OfficialDriverLinks =
    [
        ("NVIDIA", "NVIDIA: nvidia.com/drivers"),
        ("AMD", "AMD: drivers.amd.com"),
        ("Intel", "Intel: intel.com/content/www/us/en/download-center/home.html")
    ];

    private readonly IGpuVendorInspector inspector;

    public GpuVendorDetectionAction(IGpuVendorInspector inspector)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DetectGpuVendor);

    protected override string Describe() => Classify(inspector.GetSnapshot());

    internal static string Classify(GpuVendorSnapshot snapshot)
    {
        if (snapshot.DriverDescriptions.Count == 0)
        {
            return WindowsActionText.Format("ActionResults.GpuVendor.Unavailable");
        }

        var vendors = snapshot.DriverDescriptions.Select(GpuVendorClassifier.VendorOf).ToArray();
        var described = snapshot.DriverDescriptions.Select(
            (description, index) => $"{vendors[index]} ({description})");

        var message = WindowsActionText.Format(
            "ActionResults.GpuVendor.Detected",
            string.Join(", ", described));

        var links = OfficialDriverLinks
            .Where(entry => vendors.Contains(entry.Vendor, StringComparer.Ordinal))
            .Select(entry => entry.Link)
            .ToArray();
        return links.Length > 0
            ? message + WindowsActionText.Format(
                "ActionResults.GpuVendor.DriverSuffix",
                string.Join("; ", links))
            : message;
    }
}

/// <summary>
/// Read-only cross-check between "this looks like a dual-GPU laptop" (from
/// <see cref="IGpuVendorInspector"/>'s driver descriptions) and "the
/// per-app GPU preference registry entry for FiveM is actually set to high
/// performance" (the same
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c> location
/// <see cref="GpuPreferenceRegistryAction"/> writes to). Item from the
/// graphics optimizations backlog: "detectar quando o jogo está usando a
/// integrada por engano" -- deliberately scoped to what can be checked
/// without hooking the running game's actual DXGI adapter (which this
/// product does not do), never alters anything.
/// </summary>
public sealed class GpuPreferenceMismatchDiagnosisAction : ReadOnlyDiagnosticAction
{
    private const string PreferencesSubKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private readonly IGpuVendorInspector gpuVendor;
    private readonly IRegistryStore registry;
    private readonly string fiveMExecutable;

    public GpuPreferenceMismatchDiagnosisAction(
        IGpuVendorInspector gpuVendor,
        IRegistryStore registry,
        string fiveMExecutablePath)
    {
        this.gpuVendor = gpuVendor ?? throw new ArgumentNullException(nameof(gpuVendor));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        fiveMExecutable = Path.GetFullPath(fiveMExecutablePath);
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseGpuPreferenceMismatch);

    protected override string Describe()
    {
        var descriptions = gpuVendor.GetSnapshot().DriverDescriptions;
        var hasIntegrated = descriptions.Any(GpuVendorClassifier.IsIntegrated);
        var hasDedicated = descriptions.Any(description => !GpuVendorClassifier.IsIntegrated(description));
        if (!hasIntegrated || !hasDedicated)
        {
            return WindowsActionText.Format("ActionResults.GpuPreference.NotApplicable");
        }

        return IsHighPerformancePreferenceConfigured()
            ? WindowsActionText.Format("ActionResults.GpuPreference.Configured")
            : WindowsActionText.Format("ActionResults.GpuPreference.Mismatch");
    }

    private bool IsHighPerformancePreferenceConfigured()
    {
        var value = registry.Read(new RegistryAddress(
            RegistryHive.CurrentUser,
            PreferencesSubKey,
            fiveMExecutable));
        if (!value.Exists || value.Kind != RegistryValueKind.String || string.IsNullOrWhiteSpace(value.StringValue))
        {
            return false;
        }

        return value.StringValue
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Any(segment => segment.Equals("GpuPreference=2", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Read-only guidance for hybrid/gaming laptops: reports whether the
/// machine is running on battery (dedicated-GPU/performance modes usually
/// only engage on AC) or with Windows Battery Saver active, and whether a
/// known manufacturer utility that exposes GPU-switch (MUX) or performance
/// mode controls (Armoury Crate, MSI Center, Lenovo Vantage, etc.) is
/// installed. Never controls a MUX switch or BIOS setting itself through an
/// undocumented, vendor-specific mechanism -- see
/// docs/graphics-optimizations-backlog.md, seção 12, for why that stays
/// out of scope. Thermal/power throttling itself is already covered by the
/// separate <c>safety.throttling-signal.diagnose</c> diagnostic; this
/// action does not duplicate that.
/// </summary>
public sealed class HybridLaptopDiagnosisAction : ReadOnlyDiagnosticAction
{
    private readonly IPowerStatusProvider powerStatus;
    private readonly IVendorLaptopSoftwareInspector vendorSoftware;

    public HybridLaptopDiagnosisAction(
        IPowerStatusProvider powerStatus,
        IVendorLaptopSoftwareInspector vendorSoftware)
    {
        this.powerStatus = powerStatus ?? throw new ArgumentNullException(nameof(powerStatus));
        this.vendorSoftware = vendorSoftware ?? throw new ArgumentNullException(nameof(vendorSoftware));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseHybridLaptop);

    protected override string Describe()
    {
        var onAc = powerStatus.IsOnAcPower();
        return Classify(
            onAc,
            !onAc && powerStatus.IsBatterySaverActive(),
            vendorSoftware.DetectInstalledToolNames());
    }

    internal static string Classify(bool onAc, bool batterySaverActive, IReadOnlyList<string> detectedTools)
    {
        var parts = new List<string>();
        if (!onAc)
        {
            parts.Add(WindowsActionText.Format("ActionResults.HybridLaptop.OnBattery"));
        }

        if (batterySaverActive)
        {
            parts.Add(WindowsActionText.Format("ActionResults.HybridLaptop.BatterySaver"));
        }

        parts.Add(detectedTools.Count == 0
            ? WindowsActionText.Format("ActionResults.HybridLaptop.NoVendorTool")
            : WindowsActionText.Format(
                "ActionResults.HybridLaptop.VendorTools",
                string.Join(", ", detectedTools)));

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Read-only guidance about very-high-polling-rate mice (4000/8000 Hz)
/// increasing CPU interrupt overhead, shown only when the CPU is currently
/// under heavy load. This app has no public, reliable way to read a mouse's
/// actual USB polling rate (it would require querying the raw USB
/// descriptor, not exposed by a documented Windows API) or to correlate
/// stutter with mouse movement in real time (this product only takes
/// point-in-time snapshots, not continuous telemetry) -- so this stays
/// text guidance tied to an existing, real signal (CPU load), never a
/// claim of having detected the mouse or its actual polling rate.
/// </summary>
public sealed class MousePollingRateGuidanceAction : ReadOnlyDiagnosticAction
{
    private const double HighCpuLoadPercent = 85d;

    private readonly IResourceUsageInspector resourceUsage;

    public MousePollingRateGuidanceAction(IResourceUsageInspector resourceUsage)
    {
        this.resourceUsage = resourceUsage ?? throw new ArgumentNullException(nameof(resourceUsage));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.GuideMousePollingRate);

    protected override string Describe() => Classify(resourceUsage.GetSnapshot().CpuPercent);

    internal static string Classify(double? cpuPercent)
    {
        if (cpuPercent is { } percent && percent >= HighCpuLoadPercent)
        {
            return WindowsActionText.Format("ActionResults.MousePolling.HighCpu", percent);
        }

        return WindowsActionText.Format("ActionResults.MousePolling.NormalCpu");
    }
}
