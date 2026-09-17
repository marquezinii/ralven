using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Core.Planning;
using Ralven.Windows.Actions;
using Ralven.Windows.Engine;
using Ralven.Windows.Infrastructure;

namespace Ralven.Windows;

public sealed record WindowsOptimizationEnvironment
{
    public required string FiveMInstallationRoot { get; init; }

    public required string FiveMAppRoot { get; init; }

    public required string FiveMExecutablePath { get; init; }

    public required string LegacyGraphicsSettingsPath { get; init; }

    public string? GtaVInstallationRoot { get; init; }

    public string? GtaVExecutablePath { get; init; }

    public required string GtaVGraphicsSettingsPath { get; init; }

    public required string UserTemporaryDirectory { get; init; }

    public required string JournalDirectory { get; init; }

    public static WindowsOptimizationEnvironment DetectDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData) || string.IsNullOrWhiteSpace(roamingAppData))
        {
            throw new InvalidOperationException("Windows user profile directories are unavailable.");
        }

        var detectedInstallation = new FiveMInstallationLocator().Detect();
        var installationRoot = detectedInstallation.Installation?.Root
            ?? Path.Combine(localAppData, "FiveM");
        var gtaV = GtaVLocator.Detect(installationRoot);
        return new WindowsOptimizationEnvironment
        {
            FiveMInstallationRoot = installationRoot,
            FiveMAppRoot = detectedInstallation.Installation?.AppRoot
                ?? Path.Combine(installationRoot, "FiveM.app"),
            FiveMExecutablePath = detectedInstallation.Installation?.ExecutablePath
                ?? Path.Combine(installationRoot, "FiveM.exe"),
            LegacyGraphicsSettingsPath = Path.Combine(
                roamingAppData,
                "CitizenFX",
                "gta5_settings.xml"),
            GtaVInstallationRoot = gtaV.InstallationRoot,
            GtaVExecutablePath = gtaV.ExecutablePath,
            GtaVGraphicsSettingsPath = gtaV.GraphicsSettingsPath,
            UserTemporaryDirectory = Path.Combine(localAppData, "Temp"),
            JournalDirectory = Path.Combine(localAppData, "Ralven", "Transactions")
        };
    }
}

public sealed record WindowsOptimizationDependencies
{
    public required IRegistryStore Registry { get; init; }

    public required IFiveMProcessInspector ProcessInspector { get; init; }

    public required IGtaVProcessInspector GtaVProcessInspector { get; init; }

    public required SafeFileTree FileTree { get; init; }

    public required IVisualEffectsController VisualEffects { get; init; }

    public required IPowerPlanController PowerPlans { get; init; }

    public required IPowerStatusProvider PowerStatus { get; init; }

    public required IWindowsTransactionJournalStore JournalStore { get; init; }

    public required ISystemResourceInspector SystemResources { get; init; }

    public required WindowsActionTextResolver ActionText { get; init; }

    public required IWindowsSystemHealthInspector WindowsSystemHealth { get; init; }

    public required IWindowsApplicationInventoryInspector ApplicationInventory { get; init; }

    public required ITrimStatusInspector TrimStatus { get; init; }

    public required IMouseAccelerationController MouseAcceleration { get; init; }

    public required IOverlaySoftwareInspector OverlaySoftware { get; init; }

    public required INetworkHealthInspector NetworkHealth { get; init; }

    public required IThermalInspector Thermal { get; init; }

    public required IGpuVendorInspector GpuVendor { get; init; }

    public required ICpuInspector Cpu { get; init; }

    public required IGpuDetailsInspector GpuDetails { get; init; }

    public required IRamDetailsInspector RamDetails { get; init; }

    public required IStorageHealthInspector StorageHealth { get; init; }

    public required IDriverVersionInspector DriverVersions { get; init; }

    public required IDisplayConfigurationInspector DisplayConfiguration { get; init; }

    public required IResourceUsageInspector ResourceUsage { get; init; }

    public required IPciLinkInspector PciLink { get; init; }

    public required IHardwareStabilityInspector HardwareStability { get; init; }

    public required IBackgroundProcessInspector BackgroundProcess { get; init; }

    public required IStuckFiveMProcessInspector StuckProcess { get; init; }

    public required IFiveMProcessTerminator StuckProcessTerminator { get; init; }

    public required IVendorLaptopSoftwareInspector VendorLaptopSoftware { get; init; }

    public static WindowsOptimizationDependencies CreateDefault(
        WindowsOptimizationEnvironment environment,
        WindowsActionTextResolver? actionText = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var commandRunner = new ProcessCommandRunner();
        return new WindowsOptimizationDependencies
        {
            Registry = new WindowsRegistryStore(),
            ProcessInspector = new WindowsFiveMProcessInspector(),
            GtaVProcessInspector = new WindowsGtaVProcessInspector(),
            FileTree = new SafeFileTree(),
            VisualEffects = new WindowsVisualEffectsController(),
            PowerPlans = new PowerCfgController(commandRunner),
            PowerStatus = new WindowsPowerStatusProvider(),
            JournalStore = new JsonWindowsTransactionJournalStore(environment.JournalDirectory),
            SystemResources = new WindowsSystemResourceInspector(),
            ActionText = actionText ?? (static (key, _) => key),
            WindowsSystemHealth = new WindowsSystemHealthInspector(),
            ApplicationInventory = new WindowsApplicationInventoryInspector(),
            TrimStatus = new WindowsTrimStatusInspector(),
            MouseAcceleration = new WindowsMouseAccelerationInspector(),
            OverlaySoftware = new WindowsOverlaySoftwareInspector(),
            NetworkHealth = new WindowsNetworkHealthInspector(),
            Thermal = new WindowsThermalInspector(),
            GpuVendor = new WindowsGpuVendorInspector(),
            Cpu = new WindowsCpuInspector(),
            GpuDetails = new WindowsGpuDetailsInspector(),
            RamDetails = new WindowsRamDetailsInspector(),
            StorageHealth = new WindowsStorageHealthInspector(),
            DriverVersions = new WindowsDriverVersionInspector(),
            DisplayConfiguration = new WindowsDisplayConfigurationInspector(),
            ResourceUsage = new WindowsResourceUsageInspector(),
            PciLink = new WindowsPciLinkInspector(),
            HardwareStability = new WindowsHardwareStabilityInspector(),
            BackgroundProcess = new WindowsBackgroundProcessInspector(),
            StuckProcess = new WindowsStuckFiveMProcessInspector(),
            StuckProcessTerminator = new WindowsFiveMProcessTerminator(),
            VendorLaptopSoftware = new WindowsVendorLaptopSoftwareInspector()
        };
    }
}

public sealed class WindowsOptimizationActionFactory
{
    private const long GiB = 1024L * 1024L * 1024L;
    private readonly WindowsOptimizationEnvironment environment;
    private readonly WindowsOptimizationDependencies dependencies;

    public WindowsOptimizationActionFactory(
        WindowsOptimizationEnvironment environment,
        WindowsOptimizationDependencies dependencies)
    {
        this.environment = ValidateEnvironment(environment);
        this.dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    private string RosIdPath =>
        Path.Combine(Path.GetDirectoryName(environment.LegacyGraphicsSettingsPath)!, "ros_id.dat");

    private string DigitalEntitlementsRoot =>
        Path.Combine(Path.GetDirectoryName(environment.UserTemporaryDirectory)!, "DigitalEntitlements");

    private string AuthQuarantineRoot =>
        Path.Combine(Path.GetDirectoryName(environment.JournalDirectory)!, "AuthQuarantine");

    public IReadOnlyList<IWindowsOptimizationAction> Create(OptimizationPlanDto plan)
    {
        ValidatePlan(plan);
        return plan.Actions
            .OrderBy(action => action.Sequence)
            .Select(action => CreateAction(action.Metadata.Id, plan.Options))
            .ToArray();
    }

    /// <summary>
    /// Builds one canonical instance per catalog action id, used to seed
    /// <see cref="WindowsActionCatalog"/>. Delegates to the same per-id
    /// construction as <see cref="CreateAction"/> so the two never drift.
    /// </summary>
    internal IReadOnlyList<IWindowsOptimizationAction> CreateCatalogActions()
    {
        var defaults = new OptimizationOptionsDto();

        // LegacyServerCacheRepairAction rejects CacheRepairPolicy.Off (its
        // default), so catalog registration substitutes a constructible
        // policy; the plan-driven path below always uses the real option.
        var cacheRepairDefaults = defaults with { ServerCacheRepair = CacheRepairPolicy.RepairNow };
        return
        [
            .. CreateDiagnosticActions(defaults),
            .. CreateCleanupActions(defaults, cacheRepairDefaults),
            .. CreateRegistryAndPowerActions(defaults),
            .. CreateGraphicsPresetActions(defaults),
            .. CreateVisualEffectsActions(defaults)
        ];
    }

    private IReadOnlyList<IWindowsOptimizationAction> CreateDiagnosticActions(OptimizationOptionsDto options)
    {
        return
        [
            CreateAction(OptimizationActionIds.VerifyFiveMIsStopped, options),
            CreateAction(OptimizationActionIds.VerifyGtaVIsStopped, options),
            CreateAction(OptimizationActionIds.DiagnoseBottleneck, options),
            CreateAction(OptimizationActionIds.DiagnoseWindowsSecurityHealth, options),
            CreateAction(OptimizationActionIds.DiagnoseStartupLoad, options),
            CreateAction(OptimizationActionIds.DiagnoseTrimStatus, options),
            CreateAction(OptimizationActionIds.DiagnoseMouseAcceleration, options),
            CreateAction(OptimizationActionIds.DetectOverlaysAndCaptureSoftware, options),
            CreateAction(OptimizationActionIds.ReadFiveMLegacyLogs, options),
            CreateAction(OptimizationActionIds.GuidePerformanceDiagnostics, options),
            CreateAction(OptimizationActionIds.DiagnoseNetworkHealth, options),
            CreateAction(OptimizationActionIds.DiagnoseThermalThrottling, options),
            CreateAction(OptimizationActionIds.DiagnosePagefileCommit, options),
            CreateAction(OptimizationActionIds.DiagnoseCacheIntegrity, options),
            CreateAction(OptimizationActionIds.DetectGpuVendor, options),
            CreateAction(OptimizationActionIds.DiagnoseCpuDetails, options),
            CreateAction(OptimizationActionIds.DiagnoseGpuDetails, options),
            CreateAction(OptimizationActionIds.DiagnoseRamDetails, options),
            CreateAction(OptimizationActionIds.DiagnoseStorageHealth, options),
            CreateAction(OptimizationActionIds.DiagnoseDriverVersions, options),
            CreateAction(OptimizationActionIds.DiagnoseDisplayConfiguration, options),
            CreateAction(OptimizationActionIds.GuideGSync, options),
            CreateAction(OptimizationActionIds.GuideDriverReinstall, options),
            CreateAction(OptimizationActionIds.DiagnoseHybridLaptop, options),
            CreateAction(OptimizationActionIds.DiagnoseSessionSettings, options),
            CreateAction(OptimizationActionIds.DiagnoseThrottlingSignal, options),
            CreateAction(OptimizationActionIds.DiagnoseResourceUsage, options),
            CreateAction(OptimizationActionIds.DiagnosePciLink, options),
            CreateAction(OptimizationActionIds.DiagnoseHardwareStability, options),
            CreateAction(OptimizationActionIds.ClassifyBottleneck, options),
            CreateAction(OptimizationActionIds.DiagnoseGtaVLaunchParameters, options),
            CreateAction(OptimizationActionIds.RecommendGraphicsPreset, options),
            CreateAction(OptimizationActionIds.DiagnoseTextureVramFit, options),
            CreateAction(OptimizationActionIds.DiagnoseCacheStorage, options),
            CreateAction(OptimizationActionIds.DiagnoseInstallationHealth, options),
            CreateAction(OptimizationActionIds.DiagnoseCrashPatterns, options)
        ];
    }

    private IReadOnlyList<IWindowsOptimizationAction> CreateCleanupActions(
        OptimizationOptionsDto options,
        OptimizationOptionsDto cacheRepairOptions)
    {
        return
        [
            CreateAction(OptimizationActionIds.CleanUserTemporaryFiles, options),
            CreateAction(OptimizationActionIds.PruneLegacyCrashDumps, options),
            CreateAction(OptimizationActionIds.RepairLegacyServerCache, cacheRepairOptions),
            CreateAction(OptimizationActionIds.TerminateStuckFiveMProcess, options),
            CreateAction(OptimizationActionIds.RecreateFiveMLocalData, options),
            CreateAction(OptimizationActionIds.RepairStaleAuthData, options)
        ];
    }

    private IReadOnlyList<IWindowsOptimizationAction> CreateRegistryAndPowerActions(OptimizationOptionsDto options)
    {
        return
        [
            CreateAction(OptimizationActionIds.EnableGameMode, options),
            CreateAction(OptimizationActionIds.PreferHighPerformanceGpu, options),
            CreateAction(OptimizationActionIds.DiagnoseGpuPreferenceMismatch, options),
            CreateAction(OptimizationActionIds.DisableBackgroundCapture, options),
            CreateAction(OptimizationActionIds.ToggleFullscreenOptimizations, options),
            CreateAction(OptimizationActionIds.ToggleHags, options),
            CreateAction(OptimizationActionIds.EnableSessionPerformancePowerPlan, options),
            CreateAction(OptimizationActionIds.AdjustPciExpressPowerManagement, options),
            CreateAction(OptimizationActionIds.DisableMouseAcceleration, options),
            CreateAction(OptimizationActionIds.GuideMousePollingRate, options)
        ];
    }

    private IReadOnlyList<IWindowsOptimizationAction> CreateGraphicsPresetActions(OptimizationOptionsDto options)
    {
        return
        [
            CreateAction(OptimizationActionIds.ApplyLightLegacyGraphics, options),
            CreateAction(OptimizationActionIds.ApplyBalancedLegacyGraphics, options),
            CreateAction(OptimizationActionIds.ApplyAggressiveLegacyGraphics, options),
            CreateAction(OptimizationActionIds.ApplyLightGtaVGraphics, options),
            CreateAction(OptimizationActionIds.ApplyBalancedGtaVGraphics, options),
            CreateAction(OptimizationActionIds.ApplyAggressiveGtaVGraphics, options),
            CreateAction(OptimizationActionIds.ApplyQualityLegacyGraphics, options),
            CreateAction(OptimizationActionIds.ApplyQualityGtaVGraphics, options),
            CreateAction(OptimizationActionIds.ApplyLegacyDisplayPreferences, options),
            CreateAction(OptimizationActionIds.ApplyGtaVDisplayPreferences, options),
            CreateAction(OptimizationActionIds.ApplyGtaVGraphicsLaunchParameters, options),
            CreateAction(OptimizationActionIds.ApplyGtaVDisplayLaunchParameters, options),
            CreateAction(OptimizationActionIds.ApplyGtaVRepairLaunchParameters, options)
        ];
    }

    private IReadOnlyList<IWindowsOptimizationAction> CreateVisualEffectsActions(OptimizationOptionsDto options)
    {
        return
        [
            CreateAction(OptimizationActionIds.ReduceMenuShowDelay, options),
            CreateAction(OptimizationActionIds.ReduceWindowsVisualEffects, options)
        ];
    }

    private IWindowsOptimizationAction CreateAction(
        string actionId,
        OptimizationOptionsDto options)
    {
        return actionId switch
        {
            OptimizationActionIds.VerifyFiveMIsStopped => new VerifyFiveMStoppedAction(
                environment.FiveMInstallationRoot,
                dependencies.ProcessInspector),
            OptimizationActionIds.VerifyGtaVIsStopped => new VerifyGtaVStoppedAction(
                environment.GtaVInstallationRoot,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.DiagnoseBottleneck => new BottleneckDiagnosisAction(
                dependencies.SystemResources),
            OptimizationActionIds.DiagnoseWindowsSecurityHealth => new WindowsSecurityHealthDiagnosisAction(
                dependencies.WindowsSystemHealth,
                dependencies.ActionText),
            OptimizationActionIds.DiagnoseStartupLoad => new StartupLoadDiagnosisAction(
                dependencies.ApplicationInventory,
                dependencies.ActionText),
            OptimizationActionIds.DiagnoseTrimStatus => new TrimStatusDiagnosisAction(
                dependencies.TrimStatus,
                dependencies.ActionText),
            OptimizationActionIds.DiagnoseMouseAcceleration => new MouseAccelerationDiagnosisAction(
                dependencies.MouseAcceleration,
                dependencies.ActionText),
            OptimizationActionIds.DetectOverlaysAndCaptureSoftware => new OverlaySoftwareDetectionAction(
                dependencies.OverlaySoftware),
            OptimizationActionIds.ReadFiveMLegacyLogs => new FiveMLegacyLogReaderAction(
                environment.FiveMAppRoot),
            OptimizationActionIds.GuidePerformanceDiagnostics => new PerformanceDiagnosticsGuideAction(),
            OptimizationActionIds.DiagnoseNetworkHealth => new NetworkHealthDiagnosisAction(
                dependencies.NetworkHealth),
            OptimizationActionIds.DiagnoseThermalThrottling => new ThermalDiagnosisAction(
                dependencies.Thermal),
            OptimizationActionIds.DiagnosePagefileCommit => new PagefileCommitDiagnosisAction(
                dependencies.SystemResources),
            OptimizationActionIds.DiagnoseCacheIntegrity => new CacheIndexIntegrityDiagnosisAction(
                environment.FiveMAppRoot),
            OptimizationActionIds.DetectGpuVendor => new GpuVendorDetectionAction(
                dependencies.GpuVendor),
            OptimizationActionIds.DiagnoseCpuDetails => new CpuDetailsDiagnosisAction(
                dependencies.Cpu),
            OptimizationActionIds.DiagnoseGpuDetails => new GpuDetailsDiagnosisAction(
                dependencies.GpuDetails),
            OptimizationActionIds.DiagnoseRamDetails => new RamDetailsDiagnosisAction(
                dependencies.RamDetails),
            OptimizationActionIds.DiagnoseStorageHealth => new StorageHealthDiagnosisAction(
                dependencies.StorageHealth),
            OptimizationActionIds.DiagnoseDriverVersions => new DriverVersionsDiagnosisAction(
                dependencies.DriverVersions),
            OptimizationActionIds.DiagnoseDisplayConfiguration => new DisplayConfigurationDiagnosisAction(
                dependencies.DisplayConfiguration),
            OptimizationActionIds.GuideGSync => new GSyncGuidanceDiagnosisAction(
                dependencies.DisplayConfiguration,
                dependencies.GpuVendor),
            OptimizationActionIds.GuideDriverReinstall => new GuidedDriverReinstallAction(),
            OptimizationActionIds.DiagnoseHybridLaptop => new HybridLaptopDiagnosisAction(
                dependencies.PowerStatus,
                dependencies.VendorLaptopSoftware),
            OptimizationActionIds.DiagnoseSessionSettings => new SessionSettingsDiagnosisAction(
                dependencies.Registry,
                dependencies.PowerPlans),
            OptimizationActionIds.DiagnoseThrottlingSignal => new ThrottlingSignalDiagnosisAction(
                dependencies.Cpu,
                dependencies.ResourceUsage,
                dependencies.HardwareStability,
                dependencies.Thermal),
            OptimizationActionIds.DiagnoseResourceUsage => new ResourceUsageDiagnosisAction(
                dependencies.ResourceUsage),
            OptimizationActionIds.DiagnosePciLink => new PciLinkDiagnosisAction(
                dependencies.PciLink),
            OptimizationActionIds.DiagnoseHardwareStability => new HardwareStabilityDiagnosisAction(
                dependencies.HardwareStability),
            OptimizationActionIds.ClassifyBottleneck => new BottleneckClassificationAction(
                dependencies.SystemResources,
                dependencies.ResourceUsage,
                dependencies.Thermal,
                dependencies.GpuDetails,
                dependencies.BackgroundProcess),
            OptimizationActionIds.DiagnoseGtaVLaunchParameters => new GtaVLaunchParametersDiagnosisAction(
                environment.GtaVInstallationRoot),
            OptimizationActionIds.RecommendGraphicsPreset => new GraphicsPresetRecommendationAction(
                dependencies.GpuDetails,
                dependencies.Cpu,
                dependencies.RamDetails,
                dependencies.DisplayConfiguration),
            OptimizationActionIds.DiagnoseTextureVramFit => new TextureVramFitDiagnosisAction(
                environment.LegacyGraphicsSettingsPath,
                dependencies.GpuDetails),
            OptimizationActionIds.DiagnoseCacheStorage => new CacheStorageDiagnosisAction(
                environment.FiveMAppRoot),
            OptimizationActionIds.DiagnoseInstallationHealth => new InstallationHealthDiagnosisAction(
                environment.FiveMInstallationRoot,
                environment.FiveMAppRoot),
            OptimizationActionIds.DiagnoseCrashPatterns => new CrashPatternDiagnosisAction(
                environment.FiveMAppRoot),
            OptimizationActionIds.TerminateStuckFiveMProcess => new StuckProcessTerminationAction(
                environment.FiveMInstallationRoot,
                dependencies.StuckProcess,
                dependencies.StuckProcessTerminator),
            OptimizationActionIds.RecreateFiveMLocalData => new RecreateFiveMLocalDataAction(
                environment.FiveMAppRoot,
                environment.FiveMInstallationRoot,
                dependencies.ProcessInspector,
                dependencies.FileTree),
            OptimizationActionIds.RepairStaleAuthData => new StaleAuthDataRepairAction(
                environment.FiveMAppRoot,
                environment.FiveMInstallationRoot,
                RosIdPath,
                Path.GetDirectoryName(environment.LegacyGraphicsSettingsPath)!,
                DigitalEntitlementsRoot,
                Path.GetDirectoryName(environment.UserTemporaryDirectory)!,
                AuthQuarantineRoot,
                dependencies.ProcessInspector),
            OptimizationActionIds.CleanUserTemporaryFiles => new UserTemporaryFilesCleanupAction(
                environment.UserTemporaryDirectory,
                TimeSpan.FromDays(options.TemporaryFileMinimumAgeDays),
                dependencies.FileTree),
            OptimizationActionIds.PruneLegacyCrashDumps => new LegacyCrashDumpsPruneAction(
                environment.FiveMAppRoot,
                environment.FiveMInstallationRoot,
                TimeSpan.FromDays(options.DiagnosticRetentionDays),
                dependencies.ProcessInspector,
                dependencies.FileTree),
            OptimizationActionIds.RepairLegacyServerCache => new LegacyServerCacheRepairAction(
                environment.FiveMAppRoot,
                environment.FiveMInstallationRoot,
                options.ServerCacheRepair,
                checked(options.ServerCacheThresholdGiB * GiB),
                dependencies.ProcessInspector,
                dependencies.FileTree),
            OptimizationActionIds.EnableGameMode => new GameModeRegistryAction(
                dependencies.Registry,
                dependencies.ProcessInspector,
                environment.FiveMInstallationRoot),
            OptimizationActionIds.PreferHighPerformanceGpu => new GpuPreferenceRegistryAction(
                dependencies.Registry,
                environment.FiveMExecutablePath,
                environment.FiveMInstallationRoot),
            OptimizationActionIds.DiagnoseGpuPreferenceMismatch => new GpuPreferenceMismatchDiagnosisAction(
                dependencies.GpuVendor,
                dependencies.Registry,
                environment.FiveMExecutablePath),
            OptimizationActionIds.DisableBackgroundCapture => new GameDvrRegistryAction(
                dependencies.Registry,
                dependencies.ProcessInspector,
                environment.FiveMInstallationRoot),
            OptimizationActionIds.ToggleFullscreenOptimizations => new FullscreenOptimizationsRegistryAction(
                dependencies.Registry,
                environment.FiveMExecutablePath,
                environment.GtaVExecutablePath),
            OptimizationActionIds.ToggleHags => new HagsToggleAction(dependencies.Registry),
            OptimizationActionIds.DisableMouseAcceleration => new PointerAccelerationAction(
                dependencies.MouseAcceleration,
                dependencies.ActionText),
            OptimizationActionIds.EnableSessionPerformancePowerPlan =>
                new SessionPerformancePowerPlanAction(
                    dependencies.PowerPlans,
                    dependencies.PowerStatus),
            OptimizationActionIds.AdjustPciExpressPowerManagement => new PciExpressPowerManagementAction(
                dependencies.PowerPlans),
            OptimizationActionIds.GuideMousePollingRate => new MousePollingRateGuidanceAction(
                dependencies.ResourceUsage),
            OptimizationActionIds.ApplyLightLegacyGraphics => new LegacyGraphicsPresetAction(
                environment.LegacyGraphicsSettingsPath,
                environment.FiveMInstallationRoot,
                OptimizationProfile.Light,
                GraphicsSettingsTarget.FiveM,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyBalancedLegacyGraphics => new LegacyGraphicsPresetAction(
                environment.LegacyGraphicsSettingsPath,
                environment.FiveMInstallationRoot,
                OptimizationProfile.Balanced,
                GraphicsSettingsTarget.FiveM,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyAggressiveLegacyGraphics => new LegacyGraphicsPresetAction(
                environment.LegacyGraphicsSettingsPath,
                environment.FiveMInstallationRoot,
                OptimizationProfile.Aggressive,
                GraphicsSettingsTarget.FiveM,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyLightGtaVGraphics => new LegacyGraphicsPresetAction(
                environment.GtaVGraphicsSettingsPath,
                environment.GtaVInstallationRoot,
                OptimizationProfile.Light,
                GraphicsSettingsTarget.GtaV,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyBalancedGtaVGraphics => new LegacyGraphicsPresetAction(
                environment.GtaVGraphicsSettingsPath,
                environment.GtaVInstallationRoot,
                OptimizationProfile.Balanced,
                GraphicsSettingsTarget.GtaV,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyAggressiveGtaVGraphics => new LegacyGraphicsPresetAction(
                environment.GtaVGraphicsSettingsPath,
                environment.GtaVInstallationRoot,
                OptimizationProfile.Aggressive,
                GraphicsSettingsTarget.GtaV,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyQualityLegacyGraphics => new LegacyGraphicsPresetAction(
                environment.LegacyGraphicsSettingsPath,
                environment.FiveMInstallationRoot,
                GraphicsSettingsTarget.FiveM,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector,
                OptimizationActionIds.ApplyQualityLegacyGraphics,
                LegacyGraphicsPresets.Quality,
                GraphicsPresetDirection.RaiseOnly),
            OptimizationActionIds.ApplyQualityGtaVGraphics => new LegacyGraphicsPresetAction(
                environment.GtaVGraphicsSettingsPath,
                environment.GtaVInstallationRoot,
                GraphicsSettingsTarget.GtaV,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector,
                OptimizationActionIds.ApplyQualityGtaVGraphics,
                LegacyGraphicsPresets.Quality,
                GraphicsPresetDirection.RaiseOnly),
            OptimizationActionIds.ApplyLegacyDisplayPreferences => new DisplayPreferencesAction(
                environment.LegacyGraphicsSettingsPath,
                environment.FiveMInstallationRoot,
                GraphicsSettingsTarget.FiveM,
                options.PreferWindowedMode,
                options.EnableVSync,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyGtaVDisplayPreferences => new DisplayPreferencesAction(
                environment.GtaVGraphicsSettingsPath,
                environment.GtaVInstallationRoot,
                GraphicsSettingsTarget.GtaV,
                options.PreferWindowedMode,
                options.EnableVSync,
                dependencies.ProcessInspector,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyGtaVGraphicsLaunchParameters => new GtaVGraphicsLaunchParametersAction(
                environment.GtaVInstallationRoot,
                dependencies.DisplayConfiguration,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyGtaVDisplayLaunchParameters => new GtaVDisplayLaunchParametersAction(
                environment.GtaVInstallationRoot,
                options.PreferWindowedMode,
                options.PreferBorderlessWindow,
                options.GtaVLaunchDirectXVersion,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ApplyGtaVRepairLaunchParameters => new GtaVRepairLaunchParametersAction(
                environment.GtaVInstallationRoot,
                options.UseGtaVSafeMode,
                options.UseGtaVMinimumSettings,
                options.UseGtaVAutoSettingsRebuild,
                dependencies.GtaVProcessInspector),
            OptimizationActionIds.ReduceWindowsVisualEffects => new VisualEffectsAction(
                dependencies.VisualEffects),
            OptimizationActionIds.ReduceMenuShowDelay => new MenuShowDelayAction(
                dependencies.VisualEffects,
                dependencies.ActionText),
            _ => throw new InvalidOperationException(
                $"Core action '{actionId}' has no registered Windows handler.")
        };
    }

    private static void ValidatePlan(OptimizationPlanDto plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Options);
        ArgumentNullException.ThrowIfNull(plan.Actions);

        if (plan.PlanId == Guid.Empty
            || plan.SchemaVersion != ProductIdentity.PlanSchemaVersion
            || plan.CatalogVersion != ActionCatalog.CurrentVersion
            || plan.ProductName != ProductIdentity.Name
            || plan.ProductSubtitle != ProductIdentity.Subtitle)
        {
            throw new InvalidOperationException("The optimization plan identity or version is invalid.");
        }

        if (!plan.IsExecutable
            || !PlanScopeSupport.IsSupported(plan.Scope, plan.Edition)
            || plan.Blocks.Count != 0
            || plan.Actions.Count == 0)
        {
            throw new InvalidOperationException("Only an executable plan with a supported scope and edition can be resolved.");
        }

        var canonical = PlanBuilder.Build(
            PlanBuilder.CanonicalRequestFor(plan),
            PlanBuildContext.For(plan));
        if (!canonical.IsExecutable
            || canonical.Options != plan.Options
            || canonical.Scope != plan.Scope
            || canonical.Actions.Count != plan.Actions.Count
            || canonical.RequiresElevation != plan.RequiresElevation
            || canonical.ContainsNonReversibleActions != plan.ContainsNonReversibleActions
            || canonical.MaximumRisk != plan.MaximumRisk)
        {
            throw new InvalidOperationException("The optimization plan summary does not match Core policy.");
        }

        for (var index = 0; index < canonical.Actions.Count; index++)
        {
            var supplied = plan.Actions[index];
            var expected = canonical.Actions[index];
            if (supplied.Sequence != index + 1
                || expected.Sequence != supplied.Sequence
                || !WindowsActionMetadata.MatchesCore(supplied.Metadata)
                || supplied.Metadata.Id != expected.Metadata.Id
                || supplied.Metadata.Version != expected.Metadata.Version)
            {
                throw new InvalidOperationException(
                    $"Action at sequence {index + 1} does not match the canonical Core plan.");
            }
        }
    }

    private static WindowsOptimizationEnvironment ValidateEnvironment(
        WindowsOptimizationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var installationRoot = SafePath.Normalize(environment.FiveMInstallationRoot);
        var appRoot = SafePath.EnsureDescendant(installationRoot, environment.FiveMAppRoot);
        var executable = SafePath.EnsureDescendant(
            installationRoot,
            environment.FiveMExecutablePath);
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("FiveMExecutablePath must point to an executable.", nameof(environment));
        }

        var settings = Path.GetFullPath(environment.LegacyGraphicsSettingsPath);
        if (!Path.GetFileName(settings).Equals(
            "gta5_settings.xml",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "LegacyGraphicsSettingsPath must point to gta5_settings.xml.",
                nameof(environment));
        }

        var gtaSettings = Path.GetFullPath(environment.GtaVGraphicsSettingsPath);
        if (!Path.GetFileName(gtaSettings).Equals("settings.xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "GtaVGraphicsSettingsPath deve apontar para settings.xml.",
                nameof(environment));
        }

        string? gtaRoot = null;
        string? gtaExecutable = null;
        if (environment.GtaVInstallationRoot is not null
            || environment.GtaVExecutablePath is not null)
        {
            if (string.IsNullOrWhiteSpace(environment.GtaVInstallationRoot)
                || string.IsNullOrWhiteSpace(environment.GtaVExecutablePath))
            {
                throw new ArgumentException(
                    "A raiz e o executável do GTA V devem ser informados juntos.",
                    nameof(environment));
            }

            gtaRoot = SafePath.Normalize(environment.GtaVInstallationRoot);
            gtaExecutable = SafePath.EnsureDescendant(gtaRoot, environment.GtaVExecutablePath);
            if (!Path.GetFileName(gtaExecutable).Equals("GTA5.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "GtaVExecutablePath deve apontar para GTA5.exe.",
                    nameof(environment));
            }
        }

        return environment with
        {
            FiveMInstallationRoot = installationRoot,
            FiveMAppRoot = appRoot,
            FiveMExecutablePath = executable,
            LegacyGraphicsSettingsPath = settings,
            GtaVInstallationRoot = gtaRoot,
            GtaVExecutablePath = gtaExecutable,
            GtaVGraphicsSettingsPath = gtaSettings,
            UserTemporaryDirectory = SafePath.Normalize(environment.UserTemporaryDirectory),
            JournalDirectory = SafePath.Normalize(environment.JournalDirectory)
        };
    }
}

public sealed class WindowsOptimizationRuntime
{
    private readonly WindowsOptimizationActionFactory factory;

    private WindowsOptimizationRuntime(
        WindowsOptimizationActionFactory factory,
        WindowsActionCatalog catalog,
        WindowsTransactionEngine engine)
    {
        this.factory = factory;
        Catalog = catalog;
        Engine = engine;
    }

    public WindowsActionCatalog Catalog { get; }

    public WindowsTransactionEngine Engine { get; }

    public static WindowsOptimizationRuntime CreateDefault()
    {
        var environment = WindowsOptimizationEnvironment.DetectDefault();
        return Create(environment, WindowsOptimizationDependencies.CreateDefault(environment));
    }

    public static WindowsOptimizationRuntime Create(
        WindowsOptimizationEnvironment environment,
        WindowsOptimizationDependencies dependencies)
    {
        var factory = new WindowsOptimizationActionFactory(environment, dependencies);
        var catalog = new WindowsActionCatalog(factory.CreateCatalogActions());
        var engine = new WindowsTransactionEngine(
            catalog,
            dependencies.JournalStore,
            dependencies.ActionText);
        return new WindowsOptimizationRuntime(factory, catalog, engine);
    }

    public IReadOnlyList<IWindowsOptimizationAction> ResolveActions(OptimizationPlanDto plan)
    {
        return factory.Create(plan);
    }

    public IReadOnlyList<IWindowsOptimizationAction> ResolveAdministratorActions(
        OptimizationPlanDto plan)
    {
        return ResolveActions(plan)
            .Where(action => action.Metadata.RequiredPrivilege == RequiredPrivilege.Administrator)
            .ToArray();
    }

    public IReadOnlyList<IWindowsOptimizationAction> ResolveAdministratorActions(
        IEnumerable<(string Id, int Version)> requestedActions)
    {
        ArgumentNullException.ThrowIfNull(requestedActions);
        var resolved = new List<IWindowsOptimizationAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requestedActions)
        {
            if (!seen.Add(request.Id))
            {
                throw new InvalidOperationException($"Action '{request.Id}' was requested more than once.");
            }

            var action = Catalog.GetRequired(request.Id, request.Version);
            if (action.Metadata.RequiredPrivilege != RequiredPrivilege.Administrator)
            {
                throw new UnauthorizedAccessException(
                    $"Action '{request.Id}' is not an administrator action.");
            }

            resolved.Add(action);
        }

        return resolved;
    }

    public Task<WindowsTransactionResult> ExecuteAsync(
        OptimizationPlanDto plan,
        WindowsActionContext context,
        WindowsTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Engine.ExecuteAsync(
            ResolveActions(plan),
            context with { Profile = plan.Profile, PersonalUsage = plan.PersonalPreferences?.Usage },
            options,
            cancellationToken);
    }
}
