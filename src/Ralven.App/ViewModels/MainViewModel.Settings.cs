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
using Ralven.Windows.Diagnostics;

namespace Ralven.App.ViewModels;

public sealed partial class MainViewModel
{
    private string? settingsSaveErrorMessage;
    private BugCode? settingsSaveBugCode;
    private bool suppressSettingsPersistence;

    public AppThemePreference ThemePreference => themePreference;

    public string LanguagePreference => languagePreference;

    public string CurrentLanguage => localization.CurrentLanguage;

    public bool IsCloseAppOnCloseSelected
    {
        get => !MinimizeToTrayOnClose;
        set
        {
            if (value)
            {
                MinimizeToTrayOnClose = false;
            }
        }
    }

    public bool IsMinimizeToTrayOnCloseSelected
    {
        get => MinimizeToTrayOnClose;
        set
        {
            if (value)
            {
                MinimizeToTrayOnClose = true;
            }
        }
    }

    public bool IsSystemThemeSelected => themePreference == AppThemePreference.System;

    public bool IsDarkThemeSelected => themePreference == AppThemePreference.Dark;

    public bool IsLightThemeSelected => themePreference == AppThemePreference.Light;

    public bool MinimizeToTrayOnClose
    {
        get => minimizeToTrayOnClose;
        set
        {
            if (SetProperty(ref minimizeToTrayOnClose, value))
            {
                OnPropertyChanged(nameof(IsCloseAppOnCloseSelected));
                OnPropertyChanged(nameof(IsMinimizeToTrayOnCloseSelected));
                SettingsChanged(refreshPlan: false);
            }
        }
    }

    public bool CanConfigureStartup => !Ralven.UpdateRuntime.PackageIdentity.IsPackaged;

    public bool LaunchAtStartup
    {
        get => launchAtStartup;
        set
        {
            if (!CanConfigureStartup) return;
            if (launchAtStartup == value)
            {
                return;
            }

            try
            {
                startupRegistration.SetEnabled(value);
                launchAtStartup = value;
                OnPropertyChanged();
                SettingsChanged(refreshPlan: false);
            }
            catch (Exception)
            {
                OnPropertyChanged();
            }
        }
    }

    public bool StartMinimized
    {
        get => startMinimized;
        set
        {
            if (SetProperty(ref startMinimized, value))
            {
                SettingsChanged(refreshPlan: false);
            }
        }
    }

    public bool CheckForUpdates
    {
        get => checkForUpdates;
        set
        {
            if (SetProperty(ref checkForUpdates, value))
            {
                SettingsChanged(refreshPlan: false);
            }
        }
    }

    public bool NotifyWhenUpdateAvailable
    {
        get => notifyWhenUpdateAvailable;
        set
        {
            if (SetProperty(ref notifyWhenUpdateAvailable, value))
            {
                SettingsChanged(refreshPlan: false);
            }
        }
    }

    public bool ShareAnonymousTelemetry
    {
        get => shareAnonymousTelemetry;
        set
        {
            if (SetProperty(ref shareAnonymousTelemetry, value))
            {
                RefreshPrivacyAuthorization(settingsFileExistedBeforeLoad: true);
                OnPropertyChanged(nameof(ShareOptionalReports));
                SettingsChanged(refreshPlan: false);
            }
        }
    }

    /// <summary>
    /// Consentimento para relatórios automáticos de falhas. Alterar este
    /// toggle nas configurações persiste imediatamente pelo mesmo mecanismo
    /// já usado pelos demais ajustes, mas nunca altera
    /// <see cref="PrivacyConsentVersion"/> nem reabre a tela de
    /// consentimento — só a confirmação explícita dessa tela faz isso (ver
    /// <see cref="ConfirmPrivacyConsentAsync"/>).
    /// </summary>
    public bool ShareCrashReports
    {
        get => shareCrashReports;
        set
        {
            if (shareCrashReports == value)
            {
                return;
            }

            shareCrashReports = value;
            RefreshPrivacyAuthorization(settingsFileExistedBeforeLoad: true);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShareOptionalReports));
            SettingsChanged(refreshPlan: false);
        }
    }

    public bool ShareOptionalReports
    {
        get => shareAnonymousTelemetry && shareCrashReports;
        set
        {
            if (shareAnonymousTelemetry == value && shareCrashReports == value)
            {
                return;
            }

            shareAnonymousTelemetry = value;
            shareCrashReports = value;
            RefreshPrivacyAuthorization(settingsFileExistedBeforeLoad: true);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShareAnonymousTelemetry));
            OnPropertyChanged(nameof(ShareCrashReports));
            SettingsChanged(refreshPlan: false);
        }
    }

    /// <summary>
    /// Decisão computada pelo <see cref="PrivacyConsentEvaluator"/> a partir
    /// das configurações recém-carregadas em <see cref="InitializeAsync"/>
    /// e atualizada quando a preferência de crash reports muda. É
    /// <see langword="null"/> antes da primeira inicialização. A janela
    /// (responsabilidade da view) decide se e qual variante mostrar a partir
    /// deste valor; nenhuma leitura adicional de <c>settings.json</c> é
    /// necessária para isso.
    /// </summary>
    public PrivacyConsentDecision? PrivacyConsentDecision { get; private set; }

    public string? SettingsSaveErrorMessage
    {
        get => settingsSaveErrorMessage;
        private set => SetProperty(ref settingsSaveErrorMessage, value);
    }

    /// <summary>
    /// Decision computed by <see cref="ReleaseNotesEvaluator"/> from the
    /// settings just loaded in <see cref="InitializeAsync"/>, analogous to
    /// <see cref="PrivacyConsentDecision"/>. The window (view responsibility)
    /// decides whether and what to show from this value alone.
    /// </summary>
    public ReleaseNotesDecision? PendingReleaseNotes { get; private set; }

    public void SelectTheme(AppThemePreference theme)
    {
        if (!Enum.IsDefined(theme) || themePreference == theme)
        {
            return;
        }

        themePreference = theme;
        OnPropertyChanged(nameof(ThemePreference));
        OnPropertyChanged(nameof(IsSystemThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(IsLightThemeSelected));
        SettingsChanged(refreshPlan: false);
    }

    public void SelectLanguage(string cultureName)
    {
        if (!LocalizationCatalog.TryNormalizePreference(cultureName, out var preference))
        {
            return;
        }

        SelectLanguagePreference(preference);
    }

    public void SelectLanguagePreference(string preference)
    {
        if (!LocalizationCatalog.TryNormalizePreference(preference, out var normalized))
        {
            return;
        }

        if (languagePreference == normalized)
        {
            return;
        }

        localization.Apply(normalized);
        languagePreference = normalized;
        RefreshLocalizedState();
        SettingsChanged(refreshPlan: false);
    }

    public async Task RestoreGeneralSettingsDefaultsAsync()
    {
        var defaults = new AppSettings();
        suppressSettingsPersistence = true;
        try
        {
            SelectLanguagePreference(defaults.Language);
            SelectTheme(defaults.Theme);
            MinimizeToTrayOnClose = defaults.MinimizeToTrayOnClose;
            LaunchAtStartup = defaults.LaunchAtStartup;
            StartMinimized = defaults.StartMinimized ?? false;
            CheckForUpdates = defaults.CheckForUpdates;
            NotifyWhenUpdateAvailable = defaults.NotifyWhenUpdateAvailable;
        }
        finally
        {
            suppressSettingsPersistence = false;
        }

        await RetrySaveSettingsAsync().ConfigureAwait(false);
    }

    private void ApplySettings(AppSettings settings)
    {
        languagePreference = LocalizationCatalog.TryNormalizePreference(settings.Language, out _)
            ? settings.Language
            : AppLanguagePreference.Automatic;
        localization.Apply(languagePreference);
        themePreference = Enum.IsDefined(settings.Theme)
            ? settings.Theme
            : AppThemePreference.System;
        minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        startMinimized = settings.StartMinimized ?? false;
        checkForUpdates = settings.CheckForUpdates;
        notifyWhenUpdateAvailable = settings.NotifyWhenUpdateAvailable;
        shareAnonymousTelemetry = settings.ShareAnonymousTelemetry;
        shareCrashReports = settings.ShareCrashReports;
        manualFiveMInstallationRoot = settings.ManualFiveMInstallationRoot;
        privacyConsentVersion = settings.PrivacyConsentVersion;
        dismissedLiveAlertId = settings.DismissedLiveAlertId;
        lastSeenReleaseNotesVersion = settings.LastSeenReleaseNotesVersion;
        try
        {
            launchAtStartup = startupRegistration.IsEnabled();
        }
        catch (Exception)
        {
            launchAtStartup = settings.LaunchAtStartup;
        }

        OnPropertyChanged(nameof(LanguagePreference));
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(ThemePreference));
        OnPropertyChanged(nameof(IsSystemThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(MinimizeToTrayOnClose));
        OnPropertyChanged(nameof(IsCloseAppOnCloseSelected));
        OnPropertyChanged(nameof(IsMinimizeToTrayOnCloseSelected));
        OnPropertyChanged(nameof(LaunchAtStartup));
        OnPropertyChanged(nameof(StartMinimized));
        OnPropertyChanged(nameof(CheckForUpdates));
        OnPropertyChanged(nameof(NotifyWhenUpdateAvailable));
        OnPropertyChanged(nameof(ShareAnonymousTelemetry));
        OnPropertyChanged(nameof(ShareCrashReports));
        OnPropertyChanged(nameof(ShareOptionalReports));
        OnPropertyChanged(nameof(HasManualFiveMInstallation));
        ResetLocalizedPlaceholders(preserveDiagnostic: true);
    }

    private AppSettings BuildSettingsSnapshot() => new()
    {
        Language = languagePreference,
        Theme = ThemePreference,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        LaunchAtStartup = LaunchAtStartup,
        StartMinimized = StartMinimized,
        CheckForUpdates = CheckForUpdates,
        NotifyWhenUpdateAvailable = NotifyWhenUpdateAvailable,
        ShareAnonymousTelemetry = ShareAnonymousTelemetry,
        ShareCrashReports = ShareCrashReports,
        PrivacyConsentVersion = privacyConsentVersion,
        DismissedLiveAlertId = dismissedLiveAlertId,
        LastSeenReleaseNotesVersion = lastSeenReleaseNotesVersion,
        ManualFiveMInstallationRoot = manualFiveMInstallationRoot
    };

    private void SettingsChanged(bool refreshPlan = true)
    {
        if (suppressSettingsPersistence)
        {
            return;
        }

        if (refreshPlan)
        {
            RefreshPlan();
        }

        var revision = Interlocked.Increment(ref settingsRevision);
        _ = SaveSettingsRevisionAsync(BuildSettingsSnapshot(), revision);
    }

    /// <summary>
    /// Persists the outcome of the privacy consent screen: whether the user
    /// clicked "Continue" with their chosen toggles. Always stamps
    /// <see cref="PrivacyConsentPolicy.CurrentVersion"/> so the screen does
    /// not reappear next launch, and always reuses the same settings
    /// persistence path as every other preference
    /// (<see cref="IAppOptimizationService.SaveSettingsAsync"/>) — no second
    /// storage mechanism is introduced.
    /// </summary>
    public async Task ConfirmPrivacyConsentAsync(bool acceptOptionalReports)
    {
        var snapshot = PrivacyConsentOutcomeBuilder.BuildConfirmed(
            BuildSettingsSnapshot(),
            acceptOptionalReports);

        shareAnonymousTelemetry = snapshot.ShareAnonymousTelemetry;
        shareCrashReports = snapshot.ShareCrashReports;
        privacyConsentVersion = snapshot.PrivacyConsentVersion;
        RefreshPrivacyAuthorization(settingsFileExistedBeforeLoad: true);
        OnPropertyChanged(nameof(ShareAnonymousTelemetry));
        OnPropertyChanged(nameof(ShareCrashReports));
        OnPropertyChanged(nameof(ShareOptionalReports));
        var revision = Interlocked.Increment(ref settingsRevision);
        await SaveSettingsRevisionAsync(snapshot, revision).ConfigureAwait(false);
    }

    public Task RetrySaveSettingsAsync()
    {
        var revision = Interlocked.Increment(ref settingsRevision);
        return SaveSettingsRevisionAsync(BuildSettingsSnapshot(), revision);
    }

    /// <summary>
    /// Records <paramref name="version"/> as the last release notes version
    /// the user has seen (or silently acknowledged — see
    /// <see cref="ReleaseNotesDecision.ShouldRecordSilently"/>), through the
    /// same settings persistence path as every other preference. The caller
    /// (<c>MainWindow</c>) only invokes this after the "What's New" panel
    /// has actually been closed, or immediately for the silent cases, so a
    /// crash before the panel is dismissed does not mark unseen notes as
    /// seen.
    /// </summary>
    public async Task ConfirmReleaseNotesSeenAsync(string version)
    {
        lastSeenReleaseNotesVersion = version;
        PendingReleaseNotes = null;

        var revision = Interlocked.Increment(ref settingsRevision);
        await SaveSettingsRevisionAsync(BuildSettingsSnapshot(), revision).ConfigureAwait(false);
    }

    private async Task SaveSettingsRevisionAsync(AppSettings snapshot, long revision)
    {
        try
        {
            await settingsSaveGate.WaitAsync();
            try
            {
                if (revision != Volatile.Read(ref settingsRevision))
                {
                    return;
                }

                await service.SaveSettingsAsync(snapshot);
                if (revision == Volatile.Read(ref settingsRevision))
                {
                    SettingsSaveErrorMessage = null;
                    settingsSaveBugCode = null;
                }
            }
            finally
            {
                settingsSaveGate.Release();
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (revision == Volatile.Read(ref settingsRevision))
            {
                settingsSaveBugCode = BugCodeClassifier.ClassifyException(exception, "settings");
                SettingsSaveErrorMessage = OptimizationFailureMessageFormatter.AppendCode(
                    localization.GetString("Settings.SaveFailed"),
                    settingsSaveBugCode,
                    code => localization.Format("Report.ErrorCodeSuffix", code));
            }
        }
    }

    private void RefreshPrivacyAuthorization(bool settingsFileExistedBeforeLoad)
    {
        PrivacyConsentDecision = PrivacyConsentEvaluator.Evaluate(
            BuildSettingsSnapshot(),
            settingsFileExistedBeforeLoad);
        telemetry.Configure(
            PrivacyConsentDecision.AreEssentialDiagnosticsAuthorized,
            PrivacyConsentDecision.AreOptionalReportsAuthorized);
        OnPropertyChanged(nameof(PrivacyConsentDecision));
    }

    private void ResetLocalizedPlaceholders(bool preserveDiagnostic = false)
    {
        if (SettingsSaveErrorMessage is not null)
        {
            SettingsSaveErrorMessage = OptimizationFailureMessageFormatter.AppendCode(
                localization.GetString("Settings.SaveFailed"),
                settingsSaveBugCode,
                code => localization.Format("Report.ErrorCodeSuffix", code));
        }

        if (!IsBusy)
        {
            ProgressHeadline = localization.GetString("Status.Ready.Headline");
            ElapsedTimeLabel = localization.Format("Progress.ElapsedFormat", "00:00");
            RemainingTimeLabel = localization.GetString("Progress.Calculating");
        }

        if (!preserveDiagnostic || diagnostic is null)
        {
            var analyzing = localization.GetString("Status.Analyzing");
            CpuName = analyzing;
            GpuDetail = analyzing;
            RamLabel = analyzing;
            DiskLabel = analyzing;
            WindowsLabel = analyzing;
            ReadinessScoreExplanation = localization.GetString("Dashboard.ReadinessExplanation");
            ReadinessLevelLabel = analyzing;
            EditionLabel = localization.GetString("Status.SearchingFiveM");
            GtaStatusLabel = localization.GetString("Status.SearchingGtaV");
            IsFiveMLegacyDetected = false;
            IsGtaVLegacyDetected = false;
            RecommendationTitle = localization.GetString("Status.AnalyzingComputer");
            RecommendationText = localization.GetString("Status.LocalOnly");
            LogicalProcessorLabel = analyzing;
            LogicalProcessorDetail = localization.GetString("Dashboard.Kpi.Cores.Detail");
            AvailableMemoryLabel = analyzing;
            AvailableMemoryDetail = string.Empty;
            LegacyCacheLabel = analyzing;
            LegacyCacheDetail = localization.GetString("Dashboard.Kpi.Cache.Detail");
            PerformancePressureLabel = analyzing;
            PerformancePressureBrushKey = "TextTertiaryBrush";
            LastScanLabel = localization.GetString("Dashboard.LastScan.Pending");
        }

        if (lastLiveMetrics is null)
        {
            ResetLiveMetricPresentation();
        }
        else
        {
            ApplyLiveMetrics(lastLiveMetrics, addHistory: false);
        }

        NotifyLivePerformanceStateChanged();
        NotifyLiveMetricSelectionChanged();
        RefreshFiveMSessionMonitorPresentation();
        ApplyLastOptimization(historyRecords);
        RefreshWindowsGamingPresentation();
        RefreshWindowsSystemHealthPresentation();
        OnPropertyChanged(nameof(SystemPcStatusMessage));
    }

    private void RefreshLocalizedState()
    {
        RefreshUltraPresentation();
        RefreshGreeting();
        OnPropertyChanged(nameof(LanguagePreference));
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(SelectedProfileLabel));
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(IsSelectedProfileRecommended));
        OnPropertyChanged(nameof(ElevationLabel));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(PlanHeader));
        OnPropertyChanged(nameof(PlanNoticesText));
        OnPropertyChanged(nameof(SafetySummary));

        ResetLocalizedPlaceholders(preserveDiagnostic: diagnostic is not null);
        if (diagnostic is not null)
        {
            ApplyDiagnostic(diagnostic);
        }

        ApplyHistory(historyRecords);
        RefreshPlan();
        UpdateOperationTiming();
        RefreshUpdatePresentation();
    }
}
