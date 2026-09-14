using Ralven.Windows;
using Ralven.Windows.Actions;
using Ralven.Windows.Engine;
using Ralven.Windows.Infrastructure;

namespace Ralven.Tests.Windows;

internal sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<RegistryAddress, RegistryValueState> values = new(
        RegistryAddressComparer.Instance);

    public RegistryValueState Read(RegistryAddress address)
    {
        return values.TryGetValue(address, out var value)
            ? value
            : RegistryValueState.Missing;
    }

    public void Write(RegistryAddress address, RegistryValueState state)
    {
        values[address] = state;
    }

    public void Delete(RegistryAddress address)
    {
        values.Remove(address);
    }

    private sealed class RegistryAddressComparer : IEqualityComparer<RegistryAddress>
    {
        public static RegistryAddressComparer Instance { get; } = new();

        public bool Equals(RegistryAddress? left, RegistryAddress? right)
        {
            return ReferenceEquals(left, right)
                || (left is not null
                    && right is not null
                    && left.Hive == right.Hive
                    && left.SubKey.Equals(right.SubKey, StringComparison.OrdinalIgnoreCase)
                    && left.ValueName.Equals(right.ValueName, StringComparison.OrdinalIgnoreCase));
        }

        public int GetHashCode(RegistryAddress address)
        {
            var hash = new HashCode();
            hash.Add(address.Hive);
            hash.Add(address.SubKey, StringComparer.OrdinalIgnoreCase);
            hash.Add(address.ValueName, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}

internal sealed class FakeProcessInspector(bool running = false) : IFiveMProcessInspector
{
    public bool Running { get; set; } = running;

    public int CallCount { get; private set; }

    public string? LastInstallationRoot { get; private set; }

    public bool IsAnyRunning()
    {
        CallCount++;
        return Running;
    }

    public bool IsRunningFrom(string installationRoot)
    {
        CallCount++;
        LastInstallationRoot = installationRoot;
        return Running;
    }
}

internal sealed class FakeGtaVProcessInspector(bool running = false) : IGtaVProcessInspector
{
    public bool Running { get; set; } = running;

    public int CallCount { get; private set; }

    public string? LastInstallationRoot { get; private set; }

    public bool IsRunningFrom(string? installationRoot)
    {
        CallCount++;
        LastInstallationRoot = installationRoot;
        return Running;
    }
}

internal sealed class SequencedGtaVProcessInspector(params bool[] runningStates) : IGtaVProcessInspector
{
    private readonly Queue<bool> states = new(runningStates);

    public int CallCount { get; private set; }

    public bool IsRunningFrom(string? installationRoot)
    {
        CallCount++;
        return states.Count > 0 && states.Dequeue();
    }
}

internal sealed class FakeVisualEffectsController : IVisualEffectsController
{
    public VisualEffectsState State { get; set; } = new(true, true, true);

    public int MenuShowDelay { get; set; } = 400;

    public bool IgnoreNextStateSet { get; set; }

    public bool IgnoreNextMenuShowDelaySet { get; set; }

    public Queue<VisualEffectsState?> StateSetResults { get; } = new();

    public Queue<int?> MenuShowDelaySetResults { get; } = new();

    public VisualEffectsState Get() => State;

    public void Set(VisualEffectsState state)
    {
        if (StateSetResults.TryDequeue(out var result))
        {
            if (result is not null)
            {
                State = result;
            }

            return;
        }

        if (IgnoreNextStateSet)
        {
            IgnoreNextStateSet = false;
            return;
        }

        State = state;
    }

    public int GetMenuShowDelay() => MenuShowDelay;

    public void SetMenuShowDelay(int milliseconds)
    {
        if (MenuShowDelaySetResults.TryDequeue(out var result))
        {
            if (result.HasValue)
            {
                MenuShowDelay = result.Value;
            }

            return;
        }

        if (IgnoreNextMenuShowDelaySet)
        {
            IgnoreNextMenuShowDelaySet = false;
            return;
        }

        MenuShowDelay = milliseconds;
    }
}

internal sealed class FakePowerStatusProvider(bool isOnAcPower = true) : IPowerStatusProvider
{
    public bool OnAcPower { get; set; } = isOnAcPower;

    public bool BatterySaverActive { get; set; }

    public bool IsOnAcPower() => OnAcPower;

    public bool IsBatterySaverActive() => BatterySaverActive;
}

internal sealed class FakePowerPlanController : IPowerPlanController
{
    private bool aspmSetCompleted;

    public Guid ActiveScheme { get; set; } = Guid.NewGuid();

    public Guid PerformanceScheme { get; set; } = Guid.NewGuid();

    public Guid PerformanceSchemeId => PerformanceScheme;

    public bool PerformanceAvailable { get; set; } = true;

    /// <summary>When true, activation fails as if Windows denied permission (instead of the scheme not existing).</summary>
    public bool DenyAccess { get; set; }

    public bool IgnoreNextActivation { get; set; }

    public bool ThrowAfterPerformanceActivation { get; set; }

    public bool CancelAfterPerformanceActivation { get; set; }

    public int PerformanceActivationCount { get; private set; }

    public Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ActiveScheme);
    }

    public Task<PowerPlanActivationOutcome> TryActivatePerformanceSchemeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PerformanceActivationCount++;
        if (DenyAccess)
        {
            return Task.FromResult(PowerPlanActivationOutcome.AccessDenied);
        }

        if (!PerformanceAvailable)
        {
            return Task.FromResult(PowerPlanActivationOutcome.SchemeUnavailable);
        }

        ActiveScheme = PerformanceScheme;
        if (CancelAfterPerformanceActivation)
        {
            throw new OperationCanceledException();
        }

        if (ThrowAfterPerformanceActivation)
        {
            throw new InvalidOperationException("Simulated failure after activation.");
        }

        return Task.FromResult(PowerPlanActivationOutcome.Activated);
    }

    public Task ActivateSchemeAsync(Guid schemeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IgnoreNextActivation)
        {
            IgnoreNextActivation = false;
        }
        else
        {
            ActiveScheme = schemeId;
        }

        return Task.CompletedTask;
    }

    /// <summary>Null means "not exposed on this machine", matching the real controller's contract.</summary>
    public PciExpressAspmPolicy? AspmPolicyState { get; set; } = new(1, 1);

    public int? AspmPolicy
    {
        get => AspmPolicyState?.AcPolicy;
        set => AspmPolicyState = value is null ? null : new(value.Value, value.Value);
    }

    public bool AspmSetShouldFail { get; set; }

    public bool ThrowOnAspmReadAfterSet { get; set; }

    public Task<PciExpressAspmState?> GetPciExpressAspmPolicyAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnAspmReadAfterSet && aspmSetCompleted)
        {
            throw new InvalidOperationException("Simulated redundant ASPM read failure.");
        }

        return Task.FromResult(AspmPolicyState is null
            ? null
            : new PciExpressAspmState(ActiveScheme, AspmPolicyState));
    }

    public Task SetPciExpressAspmPolicyAsync(
        Guid schemeId,
        PciExpressAspmPolicy expectedCurrent,
        PciExpressAspmPolicy desired,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AspmSetShouldFail)
        {
            throw new InvalidOperationException("Simulated ASPM update failure.");
        }

        if (schemeId != ActiveScheme || AspmPolicyState != expectedCurrent)
        {
            throw new IOException("Simulated concurrent ASPM change.");
        }

        AspmPolicyState = desired;
        aspmSetCompleted = true;
        return Task.CompletedTask;
    }
}

internal sealed class FakeSystemResourceInspector : ISystemResourceInspector
{
    public SystemResourceSnapshot Snapshot { get; set; } = new(
        TotalMemoryBytes: 16L * 1024 * 1024 * 1024,
        AvailableMemoryBytes: 8L * 1024 * 1024 * 1024,
        LogicalProcessorCount: 12,
        SystemDriveFreeBytes: 64L * 1024 * 1024 * 1024,
        CommitLimitBytes: 20L * 1024 * 1024 * 1024,
        AvailableCommitBytes: 16L * 1024 * 1024 * 1024);

    public SystemResourceSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeWindowsSystemHealthInspector : IWindowsSystemHealthInspector
{
    public WindowsSystemHealthSnapshot Snapshot { get; set; } = new(
        new WindowsSecurityProviderHealth(WindowsSecurityHealthState.Good, 0),
        new WindowsSecurityProviderHealth(WindowsSecurityHealthState.Good, 0),
        new WindowsSecurityProviderHealth(WindowsSecurityHealthState.Good, 0),
        DateTimeOffset.UtcNow);

    public Task<WindowsSystemHealthSnapshot> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }
}

internal sealed class FakeWindowsApplicationInventoryInspector : IWindowsApplicationInventoryInspector
{
    public WindowsApplicationInventorySnapshot Snapshot { get; set; } = new(
        [],
        [],
        DateTimeOffset.UtcNow,
        InstalledApplicationsComplete: true,
        StartupItemsComplete: true);

    public Task<WindowsApplicationInventorySnapshot> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }

    public Task<WindowsApplicationInventorySnapshot> InspectStartupAsync(
        CancellationToken cancellationToken = default) => InspectAsync(cancellationToken);
}

internal sealed class FakeTrimStatusInspector : ITrimStatusInspector
{
    public TrimStatusSnapshot Snapshot { get; set; } = new(
        TrimInspectionState.Available,
        TrimDeleteNotificationState.Enabled,
        TrimDeleteNotificationState.Enabled);

    public Task<TrimStatusSnapshot> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }
}

internal sealed class FakeMouseAccelerationInspector : IMouseAccelerationController
{
    public MouseAccelerationSnapshot Snapshot { get; set; } = new(
        MouseAccelerationInspectionState.Available,
        6,
        10,
        0);

    public MouseAccelerationSnapshot GetSnapshot() => Snapshot;

    public void Set(int threshold1, int threshold2, int accelerationLevel) => Snapshot = new(
        MouseAccelerationInspectionState.Available,
        threshold1,
        threshold2,
        accelerationLevel);
}

internal sealed class FakeOverlaySoftwareInspector : IOverlaySoftwareInspector
{
    public IReadOnlyList<string> Names { get; set; } = [];

    public IReadOnlyList<string> DetectRunningOverlayNames() => Names;
}

internal sealed class FakeVendorLaptopSoftwareInspector : IVendorLaptopSoftwareInspector
{
    public IReadOnlyList<string> Names { get; set; } = [];

    public IReadOnlyList<string> DetectInstalledToolNames() => Names;
}

internal sealed class FakeNetworkHealthInspector : INetworkHealthInspector
{
    public NetworkHealthSnapshot Snapshot { get; set; } = new(
        HasActiveInterface: true,
        DiscardedPackets: 0,
        ErrorPackets: 0);

    public NetworkHealthSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeThermalInspector : IThermalInspector
{
    public ThermalSnapshot Snapshot { get; set; } = new(false, null);

    public ThermalSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeGpuVendorInspector : IGpuVendorInspector
{
    public GpuVendorSnapshot Snapshot { get; set; } = new([]);

    public GpuVendorSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeCpuInspector : ICpuInspector
{
    public CpuSnapshot? Snapshot { get; set; } = new(8, 16, 4000, 4800);

    public CpuSnapshot? GetSnapshot() => Snapshot;
}

internal sealed class FakeGpuDetailsInspector : IGpuDetailsInspector
{
    public IReadOnlyList<GpuAdapterDetails> Snapshot { get; set; } = [];

    public IReadOnlyList<GpuAdapterDetails> GetSnapshot() => Snapshot;
}

internal sealed class FakeRamDetailsInspector : IRamDetailsInspector
{
    public RamDetailsSnapshot Snapshot { get; set; } = new([]);

    public RamDetailsSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeStorageHealthInspector : IStorageHealthInspector
{
    public StorageHealthSnapshot Snapshot { get; set; } = new([]);

    public StorageHealthSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeDriverVersionInspector : IDriverVersionInspector
{
    public DriverVersionSnapshot Snapshot { get; set; } = new([], [], [], []);

    public DriverVersionSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeDisplayConfigurationInspector : IDisplayConfigurationInspector
{
    public DisplayConfigurationSnapshot? Snapshot { get; set; } = new(
        1920, 1080, 144, 144, HardwareGpuSchedulingState.NotSupportedOrUnknown);

    public DisplayConfigurationSnapshot? GetSnapshot() => Snapshot;
}

internal sealed class FakeResourceUsageInspector : IResourceUsageInspector
{
    public ResourceUsageSnapshot Snapshot { get; set; } = new(10, 5, null, 1.5);

    public ResourceUsageSnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakePciLinkInspector : IPciLinkInspector
{
    public IReadOnlyList<PciLinkSnapshot> Snapshot { get; set; } = [];

    public IReadOnlyList<PciLinkSnapshot> GetSnapshot() => Snapshot;
}

internal sealed class FakeHardwareStabilityInspector : IHardwareStabilityInspector
{
    public HardwareStabilitySnapshot Snapshot { get; set; } = new(0, 0, null);

    public HardwareStabilitySnapshot GetSnapshot() => Snapshot;
}

internal sealed class FakeBackgroundProcessInspector : IBackgroundProcessInspector
{
    public BackgroundProcessUsage? Result { get; set; }

    public BackgroundProcessUsage? GetTopConsumer(IReadOnlyCollection<string> excludedProcessNames) => Result;
}

internal sealed class FakeStuckFiveMProcessInspector : IStuckFiveMProcessInspector
{
    public StuckFiveMProcessSnapshot Snapshot { get; set; } = new(false, 0, string.Empty);

    public StuckFiveMProcessSnapshot GetSnapshot(string installationRoot) => Snapshot;
}

internal sealed class FakeFiveMProcessTerminator : IFiveMProcessTerminator
{
    public bool TerminateSucceeds { get; set; } = true;

    public int CallCount { get; private set; }

    public StuckFiveMProcessSnapshot? LastSnapshot { get; private set; }

    public string? LastInstallationRoot { get; private set; }

    public bool TryTerminate(StuckFiveMProcessSnapshot snapshot, string installationRoot)
    {
        CallCount++;
        LastSnapshot = snapshot;
        LastInstallationRoot = installationRoot;
        return TerminateSucceeds;
    }
}

internal sealed class InMemoryJournalStore : IWindowsTransactionJournalStore
{
    private readonly Dictionary<Guid, WindowsTransactionJournal> journals = [];

    public Task SaveAsync(WindowsTransactionJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        journals[journal.TransactionId] = journal;
        return Task.CompletedTask;
    }

    public Task<WindowsTransactionJournal?> LoadAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        journals.TryGetValue(transactionId, out var journal);
        return Task.FromResult(journal);
    }

    public WindowsTransactionJournal Get(Guid transactionId) => journals[transactionId];
}

internal static class WindowsTestRuntime
{
    public static (
        WindowsOptimizationRuntime Runtime,
        WindowsOptimizationEnvironment Environment,
        InMemoryJournalStore Journals) Create(TemporaryDirectory temporaryDirectory)
    {
        var installation = temporaryDirectory.Combine("FiveM");
        var gtaVInstallation = temporaryDirectory.Combine("Grand Theft Auto V");
        var environment = new WindowsOptimizationEnvironment
        {
            FiveMInstallationRoot = installation,
            FiveMAppRoot = System.IO.Path.Combine(installation, "FiveM.app"),
            FiveMExecutablePath = System.IO.Path.Combine(installation, "FiveM.exe"),
            LegacyGraphicsSettingsPath = temporaryDirectory.Combine(
                "Roaming",
                "CitizenFX",
                "gta5_settings.xml"),
            GtaVInstallationRoot = gtaVInstallation,
            GtaVExecutablePath = System.IO.Path.Combine(gtaVInstallation, "GTA5.exe"),
            GtaVGraphicsSettingsPath = temporaryDirectory.Combine(
                "Documents",
                "Rockstar Games",
                "GTA V",
                "settings.xml"),
            UserTemporaryDirectory = temporaryDirectory.Combine("UserTemp"),
            JournalDirectory = temporaryDirectory.Combine("Journals")
        };
        var journals = new InMemoryJournalStore();
        var dependencies = new WindowsOptimizationDependencies
        {
            Registry = new FakeRegistryStore(),
            ProcessInspector = new FakeProcessInspector(),
            GtaVProcessInspector = new FakeGtaVProcessInspector(),
            FileTree = new SafeFileTree(),
            VisualEffects = new FakeVisualEffectsController(),
            PowerPlans = new FakePowerPlanController(),
            PowerStatus = new FakePowerStatusProvider(),
            JournalStore = journals,
            SystemResources = new FakeSystemResourceInspector(),
            ActionText = static (key, _) => key,
            WindowsSystemHealth = new FakeWindowsSystemHealthInspector(),
            ApplicationInventory = new FakeWindowsApplicationInventoryInspector(),
            TrimStatus = new FakeTrimStatusInspector(),
            MouseAcceleration = new FakeMouseAccelerationInspector(),
            OverlaySoftware = new FakeOverlaySoftwareInspector(),
            NetworkHealth = new FakeNetworkHealthInspector(),
            Thermal = new FakeThermalInspector(),
            GpuVendor = new FakeGpuVendorInspector(),
            Cpu = new FakeCpuInspector(),
            GpuDetails = new FakeGpuDetailsInspector(),
            RamDetails = new FakeRamDetailsInspector(),
            StorageHealth = new FakeStorageHealthInspector(),
            DriverVersions = new FakeDriverVersionInspector(),
            DisplayConfiguration = new FakeDisplayConfigurationInspector(),
            ResourceUsage = new FakeResourceUsageInspector(),
            PciLink = new FakePciLinkInspector(),
            HardwareStability = new FakeHardwareStabilityInspector(),
            BackgroundProcess = new FakeBackgroundProcessInspector(),
            StuckProcess = new FakeStuckFiveMProcessInspector(),
            StuckProcessTerminator = new FakeFiveMProcessTerminator(),
            VendorLaptopSoftware = new FakeVendorLaptopSoftwareInspector()
        };

        return (
            WindowsOptimizationRuntime.Create(environment, dependencies),
            environment,
            journals);
    }
}
