using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Windows.Threading;
using Ralven.App.Services;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Core.Planning;
using Ralven.Windows.Infrastructure;

namespace Ralven.App.ViewModels;

public sealed partial class MainViewModel : BindableBase, IDisposable
{
    private readonly IAppOptimizationService service;
    private readonly ILocalizationService localization;
    private readonly IStartupRegistrationService startupRegistration;
    private readonly IReleaseUpdateService? releaseUpdateService;
    private readonly ISilentUpdateInstaller? silentUpdateInstaller;
    private readonly IAnonymousTelemetryService telemetry;
    private readonly ILiveAlertService? liveAlertService;
    private readonly ILiveSystemMetricsProvider liveSystemMetricsProvider;
    private readonly RalvenCacheService ralvenCacheService;
    private readonly ProgressTimingEstimator progressTimingEstimator = new();
    private readonly SemaphoreSlim settingsSaveGate = new(1, 1);
    private readonly Queue<string> pendingHeadlines = new();
    private static readonly TimeSpan HeadlineMinimumDwell = TimeSpan.FromSeconds(6);
    // Uma amostra por segundo: a leitura em si já leva ~300ms de janela PDH,
    // então cadências menores só se sobrepõem sem acrescentar informação.
    private static readonly TimeSpan LiveMetricsInterval = TimeSpan.FromSeconds(1);
    private const int LiveMetricsHistoryCapacity = 60;
    // Startup check plus this cadence is "almost instant" without polling the
    // free-tier Worker unnecessarily -- see
    // docs/superpowers/specs/2026-08-17-live-alerts-design.md.
    private static readonly TimeSpan LiveAlertPollInterval = TimeSpan.FromHours(1);
    private DispatcherTimer? headlineDwellTimer;
    private DateTime headlineShownAtUtc;
    private CancellationTokenSource? operationCancellation;
    private AppDiagnostic? diagnostic;
    private bool diagnosticFailed;
    private IReadOnlyList<AppHistoryRecord> historyRecords = [];
    private OptimizationPlanDto? currentPlan;
    private OptimizationProfile selectedProfile = OptimizationProfile.Balanced;
    private OptimizationScope optimizationScope = OptimizationScope.GeneralWindows;
    private bool isBusy;
    private bool isInitializing = true;
    private double progressPercent;
    private string progressHeadline = string.Empty;
    private string previousProgressHeadline = string.Empty;
    private string elapsedTimeLabel = string.Empty;
    private string remainingTimeLabel = string.Empty;
    private string cpuName = string.Empty;
    private string ramLabel = string.Empty;
    private string diskLabel = string.Empty;
    private string windowsLabel = string.Empty;
    private string gpuDetail = string.Empty;
    private string readinessScoreExplanation = string.Empty;
    private string editionLabel = string.Empty;
    private string editionBadgeLabel = "AUTO";
    private string gtaStatusLabel = string.Empty;
    private bool isFiveMLegacyDetected;
    private bool isGtaVLegacyDetected;
    private string recommendationTitle = string.Empty;
    private string recommendationText = string.Empty;
    private string streamingReadinessTitle = string.Empty;
    private string streamingReadinessDetail = string.Empty;
    private string readinessLevelLabel = string.Empty;
    private string logicalProcessorLabel = string.Empty;
    private string logicalProcessorDetail = string.Empty;
    private string availableMemoryLabel = string.Empty;
    private string availableMemoryDetail = string.Empty;
    private string legacyCacheLabel = string.Empty;
    private string legacyCacheDetail = string.Empty;
    private string performancePressureLabel = string.Empty;
    private string performancePressureBrushKey = "TextTertiaryBrush";
    private string lastScanLabel = string.Empty;
    private string greetingTitle = string.Empty;
    private string? accountFirstName;
    private string lastOptimizationTitle = string.Empty;
    private string lastOptimizationDateLabel = string.Empty;
    private string lastOptimizationSummary = string.Empty;
    private bool hasLastOptimization;
    private string memoryUsageDetailLabel = string.Empty;
    private string cpuTrendLabel = string.Empty;
    private string gpuTrendLabel = string.Empty;
    private string memoryTrendLabel = string.Empty;
    private string diskTrendLabel = string.Empty;
    private string networkTrendLabel = string.Empty;
    private double cpuUsagePercent;
    private double gpuUsagePercent;
    private double memoryUsagePercent;
    private double diskUsagePercent;
    private string cpuUsageLabel = string.Empty;
    private string gpuUsageLabel = string.Empty;
    private string memoryUsageLabel = string.Empty;
    private string diskUsageLabel = string.Empty;
    private string networkUsageLabel = string.Empty;
    private string liveMetricsUpdatedLabel = string.Empty;
    private string liveMetricsUpdatedExactLabel = string.Empty;
    private IReadOnlyList<double> cpuUsageSeries = [];
    private IReadOnlyList<double> gpuUsageSeries = [];
    private IReadOnlyList<double> memoryUsageSeries = [];
    private IReadOnlyList<double> diskUsageSeries = [];
    private IReadOnlyList<double> networkUsageSeries = [];
    private readonly Queue<double> cpuUsageHistory = new();
    private readonly Queue<double> gpuUsageHistory = new();
    private readonly Queue<double> memoryUsageHistory = new();
    private readonly Queue<double> diskUsageHistory = new();
    private readonly Queue<double> networkUsageHistory = new();
    private DispatcherTimer? liveMetricsTimer;
    private bool liveMetricsEnabled;
    private bool liveMetricsCaptureInProgress;
    private bool liveMetricsUnavailable;
    private bool liveMetricsAwaitingFreshSample;
    private int liveMetricsGeneration;
    private LiveMetricsTarget liveMetricsTarget;
    private LiveMetricKind selectedLiveMetric;
    private LiveSystemMetricsSnapshot? lastLiveMetrics;
    private int readinessScore;
    private string languagePreference = AppLanguagePreference.Automatic;
    private AppThemePreference themePreference = AppThemePreference.System;
    private bool minimizeToTrayOnClose;
    private bool launchAtStartup;
    private bool startMinimized;
    private bool checkForUpdates = true;
    private bool notifyWhenUpdateAvailable = true;
    private bool shareAnonymousTelemetry;
    private bool shareCrashReports;
    private string? manualFiveMInstallationRoot;
    private int? privacyConsentVersion;
    private string? lastSeenReleaseNotesVersion;
    private ReleaseUpdate? availableUpdate;
    private UpdatePresentationState updatePresentationState;
    private string? updateFailureMessage;
    private bool isUpdateDownloading;
    private bool isInstallingUpdate;
    private double updateDownloadPercent;
    private string updateBannerTitle = string.Empty;
    private string updateBannerDetail = string.Empty;
    private bool isUpdateBannerDismissed;
    private bool isCheckingForUpdatesManually;
    private string? manualUpdateCheckMessage;
    private long settingsRevision;
    private bool profileInitializedFromDiagnostic;
    private Stopwatch? operationStopwatch;
    private DispatcherTimer? operationTimer;
    private OptimizationReportDto? lastReport;
    private string reportSummaryLabel = string.Empty;
    private string reportDetailSummaryLabel = string.Empty;
    private string reportRestartLabel = string.Empty;
    private bool isReportAvailable;
    private string profilePresentationBenefits = string.Empty;
    private string profilePresentationImpact = string.Empty;
    private OptimizationComparisonResult? lastComparison;
    private Guid? lastTransactionId;
    private bool isComparisonAvailable;
    private bool comparisonRegressionSuspected;
    private string comparisonSummaryLabel = string.Empty;
    private string comparisonHardwareProfileLabel = string.Empty;
    private bool isGtaVBenchmarkRunning;
    private string gtaVBenchmarkStatusLabel = string.Empty;
    private DispatcherTimer? liveAlertTimer;
    private string? liveAlertId;
    private string? dismissedLiveAlertId;
    private bool isLiveAlertBannerVisible;
    private bool isLiveAlertIconVisible;
    private string liveAlertMessage = string.Empty;
    private LiveAlertSeverity liveAlertSeverity = LiveAlertSeverity.Important;

    public MainViewModel(
        IAppOptimizationService service,
        ILocalizationService? localization = null,
        IStartupRegistrationService? startupRegistration = null,
        IReleaseUpdateService? releaseUpdateService = null,
        IAnonymousTelemetryService? telemetry = null,
        ISilentUpdateInstaller? silentUpdateInstaller = null,
        ILiveSystemMetricsProvider? liveSystemMetricsProvider = null,
        ILiveAlertService? liveAlertService = null,
        WindowsGamingControlsService? windowsGamingControls = null,
        Func<string, FiveMSessionPresence>? fiveMSessionProbe = null,
        IWindowsSystemHealthInspector? windowsSystemHealthInspector = null,
        PersonalWorkspaceService? personalWorkspaceService = null,
        RalvenCacheService? ralvenCacheService = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.personalWorkspaceService = personalWorkspaceService ?? new PersonalWorkspaceService(_ => Task.FromResult(false), inMemory: true);
        this.localization = localization ?? LocalizationService.Current;
        this.startupRegistration = startupRegistration ?? new WindowsStartupRegistrationService();
        this.releaseUpdateService = releaseUpdateService;
        this.silentUpdateInstaller = silentUpdateInstaller;
        this.telemetry = telemetry ?? DisabledAnonymousTelemetryService.Instance;
        this.liveAlertService = liveAlertService;
        this.ralvenCacheService = ralvenCacheService ?? new RalvenCacheService();
        this.liveSystemMetricsProvider = liveSystemMetricsProvider ?? new WindowsLiveSystemMetricsProvider();
        this.windowsGamingControls = windowsGamingControls ?? new WindowsGamingControlsService();
        this.fiveMSessionProbe = fiveMSessionProbe ?? WindowsFiveMSessionProbe.Probe;
        this.windowsSystemHealthInspector = windowsSystemHealthInspector
            ?? new WindowsSystemHealthInspector();
        StepLedger.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStepLedgerItems));
        ResetLocalizedPlaceholders();
        RefreshProfilePresentation();
        RefreshGreeting();
    }

    public ObservableCollection<ActionDisplayItem> PlannedActions { get; } = [];

    public ObservableCollection<ActionDisplayItem> PlannedAdjustments { get; } = [];

    public ObservableCollection<ActionDisplayItem> InformationalPlannedActions { get; } = [];

    public ObservableCollection<HistoryDisplayItem> HistoryItems { get; } = [];

    public ObservableCollection<StreamingReadinessDisplayItem> StreamingReadinessItems { get; } = [];

    public ObservableCollection<StepLedgerItem> StepLedger { get; } = [];

    public ObservableCollection<ReportLineDisplayItem> ReportLines { get; } = [];

    public bool HasStepLedgerItems => StepLedger.Count > 0;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(IsOptimizerIdle));
                OnPropertyChanged(nameof(IsUpdateAttentionVisible));
                OnPropertyChanged(nameof(IsLiveAlertStatusVisible));
                RaiseCommandState();
            }
        }
    }

    public bool IsOptimizerIdle => !IsBusy && !IsReportAvailable;

    public bool CanRefresh => !IsBusy && !isInitializing && !isWindowsGamingBusy && !isPersonalBusy;

    public bool CanStart => !IsBusy
        && !isPersonalBusy
        && (!IsUltraSelected || ProFeatureAvailability.Enabled && hasProAccess)
        && !isWindowsGamingBusy
        && !isInitializing
        && diagnostic is not null
        && !diagnosticFailed
        && currentPlan?.IsExecutable == true
        && (optimizationScope == OptimizationScope.GeneralWindows
            || (!diagnostic.IsFiveMRunning
                && !diagnostic.GtaVIsRunning
                && !IsFiveMSessionActive));

    public bool CanCancel => IsBusy && operationCancellation is not null;

    public string LogsDirectory => service.LogsDirectory;

    public string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.2.0";

    public string AboutVersionDeveloper => localization.Format("About.VersionDeveloper", AppVersion);

    public Task InitializeAsync() => InitializeAsync(startBackgroundServices: true);

    internal async Task InitializeAsync(bool startBackgroundServices)
    {
        StartupTrace.Mark("initialize-start");
        isInitializing = true;
        RaiseCommandState();
        try
        {
            var settingsTask = service.LoadSettingsAsync();
            var diagnosticTask = service.DiagnoseAsync();
            var historyTask = service.LoadHistoryAsync();
            // Preferences/consent do not depend on a successful system probe.
            // Observe every task even if loading or applying settings fails.
            try
            {
                var loadedSettings = await settingsTask;
                var settingsFileExistedBeforeLoad = service.SettingsFileExists();
                ApplySettings(loadedSettings);
                StartupTrace.Mark("settings-applied");
                RefreshPrivacyAuthorization(settingsFileExistedBeforeLoad);
                PendingReleaseNotes = ReleaseNotesEvaluator.Evaluate(
                    loadedSettings,
                    settingsFileExistedBeforeLoad,
                    AppVersion,
                    ReleaseNotesCatalog.Versions);
            }
            finally
            {
                await Task.WhenAll(settingsTask, diagnosticTask, historyTask);
            }
            StartupTrace.Mark("initial-data-ready");
            ApplyDiagnostic(await diagnosticTask);
            StartupTrace.Mark("diagnostic-applied");
            SetSystemPcStatus("System.Pc.Status.Ready");
            ApplyHistory(await historyTask);
            StartupTrace.Mark("history-applied");
        }
        catch (Exception exception)
        {
            diagnosticFailed = true;
            SetSystemPcStatus(diagnostic is null
                ? "System.Pc.Status.Unavailable"
                : "System.Pc.Status.Stale");
            RecommendationTitle = localization.GetString("Diagnosis.Partial");
            RecommendationText = localization.DescribeException(exception);
        }
        finally
        {
            isInitializing = false;
            RefreshPlan();
            OnPropertyChanged(nameof(EmptyPlanMessage));
            RaiseCommandState();
        }
        await InitializePersonalWorkspaceAsync();
        StartupTrace.Mark("personal-workspace-ready");
        if (startBackgroundServices) StartBackgroundServices();
    }

    private bool backgroundServicesStarted;

    internal void StartBackgroundServices()
    {
        if (backgroundServicesStarted || personalLifetime.IsCancellationRequested) return;
        backgroundServicesStarted = true;
        if (checkForUpdates && releaseUpdateService is not null)
        {
            _ = CheckForUpdatesAsync().ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        if (liveAlertService is not null)
        {
            _ = CheckLiveAlertAsync().ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            liveAlertTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LiveAlertPollInterval };
            liveAlertTimer.Tick += (_, _) => _ = CheckLiveAlertAsync();
            liveAlertTimer.Start();
        }
        _ = ObservePersonalPcAsync();
    }

    public async Task RefreshDiagnosticAsync()
    {
        if (!CanRefresh)
        {
            return;
        }

        isInitializing = true;
        SetSystemPcStatus("System.Pc.Status.Refreshing");
        RaiseCommandState();
        try
        {
            ApplyDiagnostic(await service.DiagnoseAsync());
            SetSystemPcStatus("System.Pc.Status.Ready");
        }
        catch (Exception exception)
        {
            diagnosticFailed = true;
            SetSystemPcStatus(diagnostic is null
                ? "System.Pc.Status.Unavailable"
                : "System.Pc.Status.Stale");
            RecommendationTitle = localization.GetString("Diagnosis.CouldNotScanAgain");
            RecommendationText = localization.DescribeException(exception);
        }
        finally
        {
            isInitializing = false;
            RefreshPlan();
            OnPropertyChanged(nameof(EmptyPlanMessage));
            RaiseCommandState();
        }
        await ObservePersonalPcAsync();
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanEditPersonalPreferences));
        OnPropertyChanged(nameof(CanSavePersonalProfile));
        OnPropertyChanged(nameof(CanUsePersonalTools));
        OnPropertyChanged(nameof(CanCheckPersonalTracking));
        OnPropertyChanged(nameof(CanStopPersonalTracking));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRevertLastOptimization));
        OnPropertyChanged(nameof(CanRunGtaVBenchmark));
        OnPropertyChanged(nameof(CanRefreshWindowsGamingSettings));
        OnPropertyChanged(nameof(CanApplyWindowsGamingSettings));
        OnPropertyChanged(nameof(CanRestoreWindowsGamingSettings));
        OnPropertyChanged(nameof(CanRefreshWindowsSystemHealth));
        // Updating restarts the app, so the button has to follow IsBusy.
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanClearRalvenCache));
    }

    public void Dispose()
    {
        SetLiveMetricsEnabled(false);
        disposed = true;
        personalLifetime.Cancel();
        personalTrackingTimer?.Stop();
        personalTrackingTimer = null;
        StopFiveMSessionMonitor();
        liveMetricsEnabled = false;
        liveMetricsTimer?.Stop();
        liveMetricsTimer = null;
        liveAlertTimer?.Stop();
        liveAlertTimer = null;
        // Um DispatcherTimer em execução é mantido vivo pelo dispatcher: sem
        // parar estes dois, um view model descartado durante uma otimização
        // continuaria enraizado e disparando.
        headlineDwellTimer?.Stop();
        headlineDwellTimer = null;
        operationTimer?.Stop();
        operationTimer = null;
        (liveSystemMetricsProvider as IDisposable)?.Dispose();
    }
}
