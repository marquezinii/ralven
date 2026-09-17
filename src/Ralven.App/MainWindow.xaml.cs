using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ralven.App.Services;
using Ralven.App.ViewModels;
using Ralven.App.Views;
using Ralven.App.Views.Pages;
using Ralven.Contracts;
using Ralven.UpdateRuntime;
using Ralven.Windows.Infrastructure;

namespace Ralven.App;

/// <summary>
/// O shell: title bar, navegação lateral e as seções de nível
/// superior. É o único dono de estado de janela (fechar, bandeja, conta,
/// atualização) — as páginas em <c>Views/Pages</c> chamam de volta os
/// métodos <c>Request*</c> públicos abaixo quando uma ação delas precisa
/// desse estado; o resto é local a cada página.
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{

    private readonly MainViewModel viewModel;
    private readonly ThemeManager themeManager;
    private readonly TrayIconService trayIcon;
    private readonly System.Windows.Controls.ContextMenu trayMenu;
    private readonly IReleaseUpdateService? releaseUpdateService;
    private readonly bool startupLaunch;
    private readonly bool demoMode;
    private readonly RemoteServicesOptions remoteServicesOptions;
    private readonly QueuedCloudflareTelemetryService? queuedCloudflareTelemetry;
    private SystemPage? systemPage;
    private ApplicationsPage? applicationsPage;
    private GamesPage? gamesPage;
    private FiveMPage? fiveMPage;
    private OptimizerPage? optimizerPage;
    private HistoryPage? historyPage;
    private RalvenAiPage? ralvenAiPage;
    private readonly IFirebaseAuthService? accountService;
    private readonly IAccountProfileService profileService;
    private readonly IAccountSecurityService accountSecurityService;
    private readonly CloudflareAccountEntitlementService? entitlementService;
    private readonly CloudflareDiscordLinkService? discordLinkService;
    private readonly RalvenAiService? ralvenAiService;
    private readonly IGoogleOAuthClient googleOAuth;
    private HwndSource? windowSource;
    private bool allowClose;
    private bool closeAfterOptimizationStops;
    private bool trayAnnouncementShown;
    private bool systemSessionEnding;
    private bool syncingLanguageSelector;
    private bool crashReportingConfigured;
    private readonly Task initialization;
    private bool startupCompleted;
    private bool activationRequested;
    public MainWindow()
    {
        StartupTrace.Mark("window-construct-start");

        var commandLine = ParseCommandLine();
        demoMode = commandLine.DemoMode;
        startupLaunch = commandLine.StartupLaunch;

        var runtimeLayout = RuntimeLayout.Resolve(AppContext.BaseDirectory);
        var installRoot = runtimeLayout.InstallRoot;
        var runtimeRoot = runtimeLayout.RuntimeRoot;

        var startupRegistration = CreateStartupRegistrationService(demoMode, installRoot, runtimeRoot);
        releaseUpdateService = CreateReleaseUpdateService(demoMode, runtimeRoot);
        var silentUpdateInstaller = CreateSilentUpdateInstaller(demoMode, installRoot, runtimeRoot);

        var runtimeEnvironment = AppEnvironment.Resolve();
        remoteServicesOptions = RemoteServicesOptionsLoader.Load(runtimeEnvironment, AppContext.BaseDirectory);

        if (AccountProfileEndpointPolicy.TryCreate(
            remoteServicesOptions.AccountProfileEndpoint,
            runtimeEnvironment,
            out var profileEndpoint))
        {
            profileService = new CloudflareAccountProfileService(profileEndpoint);
            accountSecurityService = new CloudflareAccountSecurityService(profileEndpoint);
            entitlementService = new CloudflareAccountEntitlementService(profileEndpoint);
            // Keep the account action hidden until the official bot and matching Worker secret are deployed.
            discordLinkService = null;
            billingService = new CloudflareBillingService(profileEndpoint);
            ralvenAiService = new RalvenAiService(profileEndpoint);
        }
        else
        {
            profileService = new DisabledAccountProfileService();
            accountSecurityService = new DisabledAccountSecurityService();
            entitlementService = null;
            discordLinkService = null;
            billingService = null;
            ralvenAiService = null;
        }

        // Demo runs never poll the live alert -- same trade as telemetry below.
        ILiveAlertService? liveAlertService = !demoMode
            && TryCreateHttpsEndpoint(remoteServicesOptions.LiveAlertEndpoint, out var liveAlertEndpoint)
                ? new CloudflareLiveAlertService(liveAlertEndpoint)
                : null;

        // Demo runs never talk to Google: an unconfigured client reports
        // IsConfigured=false and the account window hides the button.
        googleOAuth = new GoogleOAuthClient(
            demoMode ? null : remoteServicesOptions.GoogleOAuthClientId,
            demoMode ? null : remoteServicesOptions.GoogleOAuthClientSecret);

        accountService = CreateAccountService(demoMode, remoteServicesOptions, profileService);
        if (accountService is not null)
        {
            accountService.StateChanged += AccountService_StateChanged;
        }

        // Plan.Title and the Refresh button follow the language automatically
        // through their {Binding [key], Source={StaticResource
        // LocalizedStrings}} markup, but the entitlement value/detail text is
        // set imperatively (it depends on server state, not just a static
        // key), so it needs its own re-render on language change.

        var telemetry = CreateTelemetryServices(demoMode, remoteServicesOptions, runtimeEnvironment);
        queuedCloudflareTelemetry = telemetry.Queued;

        viewModel = new MainViewModel(
            new AppOptimizationService(demoMode, commandLine.SyntheticDemo) { AuthorizePro = AuthorizeProOperationAsync },
            localization: LocalizationService.Current,
            startupRegistration: startupRegistration,
            releaseUpdateService: releaseUpdateService,
            telemetry: telemetry.Service,
            silentUpdateInstaller: silentUpdateInstaller,
            liveAlertService: liveAlertService,
            windowsGamingControls: new WindowsGamingControlsService(demoMode),
            windowsSystemHealthInspector: demoMode
                ? new SyntheticWindowsSystemHealthInspector()
                : new WindowsSystemHealthInspector(),
            personalWorkspaceService: new PersonalWorkspaceService(AuthorizeProOperationAsync, inMemory: demoMode,
                applicationInventory: demoMode ? new SyntheticWindowsApplicationInventoryInspector() : new WindowsApplicationInventoryInspector(),
                driverVersion: demoMode ? new SyntheticDriverVersionInspector() : new WindowsDriverVersionInspector()));

        StartupTrace.Mark("window-services-ready");
        // Independent disk/system reads overlap WPF construction. The awaited
        // continuation still applies bindable state on this Dispatcher.
        initialization = viewModel.InitializeAsync(startBackgroundServices: false);
        themeManager = new ThemeManager();
        themeManager.Apply(viewModel.ThemePreference);
        StartupTrace.Mark("initial-theme-ready");
        InitializeComponent();
        StartupTrace.Mark("window-xaml-ready");
        trayMenu = (System.Windows.Controls.ContextMenu)Resources["TrayContextMenu"];
        CategoryGeneral.IsChecked = true;
        LocalizationService.Current.LanguageChanged += MainWindow_LanguageChanged;

        // StateChanged only fires once RestoreSessionAsync actually finds a
        // stored session; a fresh install or an already-signed-out user
        // never raises it, so the Settings card needs one explicit call here
        // to land on the right panel (unavailable/signed-out/signed-in)
        // instead of relying on whatever Visibility happens to be XAML's
        // default.
        RefreshAccountSettingsCard();
        if (!string.IsNullOrWhiteSpace(commandLine.JustUpdatedVersion))
        {
            viewModel.ReportCompletedUpdate(commandLine.JustUpdatedVersion);
        }
        trayIcon = new TrayIconService(LocalizationService.Current);
        trayIcon.ShowRequested += TrayIcon_ShowRequested;
        trayIcon.MenuRequested += TrayIcon_MenuRequested;
        viewModel.UpdateAvailableDetected += ViewModel_UpdateAvailableDetected;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = viewModel;
        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Activated += BillingWindow_Activated;
        Activated += MainWindow_ActivityChanged;
        Deactivated += MainWindow_ActivityChanged;
        StateChanged += MainWindow_ActivityChanged;
        IsVisibleChanged += (_, _) => RefreshLiveMetricsActivity();
        System.Windows.Application.Current.SessionEnding += Application_SessionEnding;
        ContentRendered += (_, _) => StartupTrace.Mark("main-rendered");
        StartupTrace.Mark("window-constructed");
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 1200)
        {
            RootNavigationView.IsPaneOpen = false;
        }
    }

    private sealed record MainWindowCommandLine(
        bool DemoMode,
        bool SyntheticDemo,
        bool StartupLaunch,
        string? JustUpdatedVersion);

    private static MainWindowCommandLine ParseCommandLine()
    {
        var commandLine = Environment.GetCommandLineArgs();
        var syntheticDemo = commandLine
            .Any(value => value.Equals("--demo-synthetic", StringComparison.OrdinalIgnoreCase));
        var demoMode = syntheticDemo || commandLine
            .Any(value => value.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var startupLaunch = commandLine
            .Any(value => value.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        var justUpdatedVersion = commandLine
            .FirstOrDefault(value => value.StartsWith("--updated=", StringComparison.OrdinalIgnoreCase))
            ?["--updated=".Length..];
        return new MainWindowCommandLine(demoMode, syntheticDemo, startupLaunch, justUpdatedVersion);
    }

    private IStartupRegistrationService CreateStartupRegistrationService(
        bool demoMode,
        string? installRoot,
        string? runtimeRoot)
    {
        if (demoMode)
        {
            return new SessionStartupRegistrationService();
        }

        return runtimeRoot is null
            ? new WindowsStartupRegistrationService()
            : new WindowsStartupRegistrationService(
                Path.Combine(installRoot!, "Ralven.Launcher.exe"));
    }

    private IReleaseUpdateService? CreateReleaseUpdateService(bool demoMode, string? runtimeRoot)
    {
        return demoMode
            ? null
            : new SignedManifestUpdateService(
                runtimeRoot is null ? ReleasePackageKind.Installer : ReleasePackageKind.Runtime);
    }

    private ISilentUpdateInstaller? CreateSilentUpdateInstaller(
        bool demoMode,
        string? installRoot,
        string? runtimeRoot)
    {
        if (demoMode)
        {
            return null;
        }

        if (runtimeRoot is not null)
        {
            return new AtomicUpdateInstaller(
                runtimeRoot,
                Path.Combine(installRoot!, "Ralven.Launcher.exe"));
        }

        return new SilentUpdateInstaller(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.Name,
                "Updates"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.Name,
                "Logs"),
            Path.Combine(AppContext.BaseDirectory, "updater", "Ralven.Updater.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.Name,
                "Updater"));
    }

    private sealed record MainWindowTelemetry(
        IAnonymousTelemetryService Service,
        QueuedCloudflareTelemetryService? Queued);

    /// <summary>
    /// Creates the telemetry services based on the configured endpoint.
    /// If the endpoint is missing or malformed, telemetry safely does
    /// nothing rather than crash.
    /// </summary>
    private MainWindowTelemetry CreateTelemetryServices(
        bool demoMode,
        RemoteServicesOptions options,
        AppRuntimeEnvironment runtimeEnvironment)
    {
        if (demoMode)
        {
            return new MainWindowTelemetry(DisabledAnonymousTelemetryService.Instance, null);
        }

        if (TelemetryEndpointPolicy.TryCreate(
            options.TelemetryEndpoint,
            runtimeEnvironment,
            out var telemetryEndpoint,
            out _))
        {
            var queued = new QueuedCloudflareTelemetryService(
                new LocalTelemetryQueue(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductIdentity.Name,
                    "Telemetry",
                    "pending")),
                new CloudflareTelemetryTransport(telemetryEndpoint, options.Environment));
            return new MainWindowTelemetry(queued, queued);
        }

        return new MainWindowTelemetry(DisabledAnonymousTelemetryService.Instance, null);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        try
        {
            await InitializeWindowAsync();
        }
        catch
        {
            if (!demoMode) InvalidateUpdateHealthReceiptIfRequested();
            throw;
        }
    }

    private async Task InitializeWindowAsync()
    {
        StartupTrace.Mark("main-loaded");
        ActivateNavItem(DashboardNav);
        Navigate(DashboardPage);
        if (!demoMode)
        {
            // O recibo de saúde precisa ser gravado antes do InitializeAsync:
            // a janela de saúde do launcher (45s) começa no spawn do processo,
            // e a inicialização (varredura WMI/registro, flush de telemetria,
            // checagem de update) pode passar disso em máquinas lentas. Um
            // candidato saudável, apenas lento, não deve ser revertido -- o
            // recibo confirma "o processo iniciou e a interface respondeu",
            // não "todo o trabalho em segundo plano terminou".
            ConfirmUpdateHealthIfRequested();
        }

        await initialization;
        if (billingLifetime.IsCancellationRequested) return;
        StartupTrace.Mark("local-ready");
        RefreshTrayIconPresentation();
        themeManager.Apply(viewModel.ThemePreference);
        PopulateLanguageSelector(viewModel.LanguagePreference);
        switch (viewModel.ThemePreference)
        {
            case AppThemePreference.Dark:
                ThemeDarkOption.IsChecked = true;
                break;
            case AppThemePreference.Light:
                ThemeLightOption.IsChecked = true;
                break;
            default:
                ThemeSystemOption.IsChecked = true;
                break;
        }
        StartupTrace.Mark("saved-theme-ready");
        // Allow the completed bindings/layout to render before dismissing the
        // splash. Consent dialogs must never sit behind the startup window.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (System.Windows.Application.Current is App app)
        {
            await app.CompleteStartupPresentationAsync();
        }
        if (billingLifetime.IsCancellationRequested) return;
        if (!demoMode)
        {
            await ShowPrivacyConsentIfNeededAsync();
            await ShowReleaseNotesIfNeededAsync();
            InitializeCrashReportingIfAuthorized();
            await FlushPendingTelemetryIfAnyAsync();
            await TrackAppInitializedTelemetryIfAuthorizedAsync();
        }
        startupCompleted = true;
        RefreshLiveMetricsActivity();
        if (startupLaunch && viewModel.StartMinimized && !activationRequested)
        {
            HideToTray();
        }
        StartupTrace.Mark("startup-ready");
        _ = Dispatcher.InvokeAsync(StartBackgroundServices, System.Windows.Threading.DispatcherPriority.Background);
        await CaptureIfRequestedAsync();
    }

    private void StartBackgroundServices()
    {
        if (billingLifetime.IsCancellationRequested) return;
        viewModel.StartBackgroundServices();
        if (accountService is not null) _ = RestoreAccountSessionQuietlyAsync();
        if (!demoMode)
        {
            InitializeCrashReportingIfAuthorized();
            _ = FlushPendingTelemetryIfAnyAsync();
        }
    }

    private static bool TryCreateHttpsEndpoint(string? value, out Uri endpoint)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme == Uri.UriSchemeHttps)
        {
            endpoint = candidate;
            return true;
        }

        endpoint = null!;
        return false;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        billingLifetime.Cancel();
        billingLifetime.Dispose();
        Activated -= BillingWindow_Activated;
        applicationsPage?.Dispose();
        ralvenAiPage?.Dispose();
        viewModel.Dispose();
        windowSource?.RemoveHook(WindowMessageHook);
        System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
        viewModel.UpdateAvailableDetected -= ViewModel_UpdateAvailableDetected;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        LocalizationService.Current.LanguageChanged -= MainWindow_LanguageChanged;
        themeManager.Dispose();
        trayMenu.IsOpen = false;
        trayIcon.Dispose();
        CancelAccountEntitlementExpiry();
        accountService?.Dispose();
        (releaseUpdateService as IDisposable)?.Dispose();
    }

    private void MainWindow_LanguageChanged(object? sender, AppLanguageChangedEventArgs e)
    {
        PopulateLanguageSelector(e.Preference);
        ApplyAccountEntitlementPresentation();
        proViewModel.Refresh();
        RefreshTrayIconPresentation();
    }

    private void PopulateLanguageSelector(string preference)
    {
        // A sincronização programática não pode transformar "automatic" no
        // idioma detectado e persistir esse pin durante o startup.
        syncingLanguageSelector = true;
        try
        {
            LanguageSelector.Items.Clear();
            LanguageSelector.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Tag = AppLanguagePreference.Automatic,
                Content = LocalizationService.Current.GetString("Settings.Language.Automatic")
            });
            foreach (var language in LocalizationCatalog.SupportedLanguages)
            {
                LanguageSelector.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Tag = language.CultureName,
                    Content = language.DisplayName
                });
            }

            var normalized = LocalizationCatalog.NormalizePreference(preference);
            LanguageSelector.SelectedIndex = normalized == AppLanguagePreference.Automatic
                ? 0
                : LocalizationCatalog.SupportedLanguages
                    .Select((language, index) => (language, index))
                    .Where(item => item.language.CultureName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.index + 1)
                    .DefaultIfEmpty(0)
                    .First();
        }
        finally
        {
            syncingLanguageSelector = false;
        }
    }

    private void Application_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // Nunca transforma a preferência de bandeja em bloqueio de logoff/desligamento.
        systemSessionEnding = true;
        allowClose = true;
        viewModel.CancelOptimization();
    }
}
