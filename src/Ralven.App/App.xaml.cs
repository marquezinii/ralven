using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ralven.App.Services;
using Ralven.App.Views;
using Ralven.Contracts;

namespace Ralven.App;

public partial class App : System.Windows.Application
{
    private static readonly string[] MotionDurationKeys =
    {
        "MotionMicro", "MotionControl", "MotionNav", "MotionEnter", "MotionStructural", "MotionExit"
    };

    private static int isHandlingFatalError;
    private SingleInstanceGuard? singleInstanceGuard;
    private StartupSplash? startupSplash;
    private bool startupCancelled;
    private bool activationPending;
    private Dictionary<string, Duration>? originalMotionDurations;

    /// <summary>
    /// Zera (ou restaura) as durações de <c>Themes/Tokens/Motion.xaml</c>
    /// conforme o Windows pede menos animação ou não. Storyboards declarados
    /// dentro de ControlTemplate são congelados e não conseguem consultar
    /// <see cref="MotionPolicy"/> em tempo de execução, então a política é
    /// aplicada na fonte: o token de duração. Assim todo controle do app —
    /// interruptor, segmentado, navegação — respeita a preferência de
    /// acessibilidade sem que cada template precise repetir a decisão.
    ///
    /// Roda no startup e de novo sempre que o Windows notifica uma mudança de
    /// <c>ClientAreaAnimation</c> (Efeitos de animação, em Configurações de
    /// Acessibilidade) — trocar a preferência com o app aberto tinha efeito
    /// só na próxima abertura, nunca na sessão corrente.
    /// </summary>
    private void ApplyMotionPolicyToDurationTokens()
    {
        originalMotionDurations ??= CaptureMotionDurations();

        var instant = new Duration(TimeSpan.Zero);
        foreach (var key in MotionDurationKeys)
        {
            if (!originalMotionDurations.TryGetValue(key, out var original))
            {
                continue;
            }

            Resources[key] = MotionPolicy.AnimationsEnabled ? original : instant;
        }
    }

    private Dictionary<string, Duration> CaptureMotionDurations()
    {
        var captured = new Dictionary<string, Duration>();
        foreach (var key in MotionDurationKeys)
        {
            if (Resources[key] is Duration duration)
            {
                captured[key] = duration;
            }
        }

        return captured;
    }

    private void OnSystemParametersChangedForMotion(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) ApplyMotionPolicyToDurationTokens();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ApplyMotionPolicyToDurationTokens);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        StartupTrace.Mark("app-startup");
        base.OnStartup(e);
        ApplyMotionPolicyToDurationTokens();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChangedForMotion;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Exit += (_, _) =>
        {
            startupCancelled = true;
            startupSplash?.Dispose();
            TryShutdownCrashReporting();
        };

        // Demo mode (used for automated smoke tests/screenshots) is
        // intentionally exempt: it never persists settings or sends
        // telemetry either, and tooling may legitimately launch it
        // repeatedly in quick succession.
        var isDemoMode = e.Args.Any(value =>
            value.Equals("--demo", StringComparison.OrdinalIgnoreCase)
            || value.Equals("--demo-synthetic", StringComparison.OrdinalIgnoreCase));

        if (!isDemoMode)
        {
            singleInstanceGuard = new SingleInstanceGuard(AppEnvironment.Resolve());
            if (!singleInstanceGuard.TryAcquire())
            {
                // Another instance is already running: ask it to bring its
                // window to the foreground and shut down quietly, so a second
                // launch never stacks a duplicate process or tray icon.
                singleInstanceGuard.RequestActivation();
                singleInstanceGuard.Dispose();
                Shutdown(0);
                return;
            }

            singleInstanceGuard.ListenForActivation(OnActivationRequested);
            Exit += (_, _) => singleInstanceGuard.Dispose();
        }

        try
        {
            var strings = LocalizationService.Current;
            startupSplash = new StartupSplash(
                strings.GetString("Startup.Initializing"),
                strings.GetString("Startup.Close"),
                ThemeManager.IsSystemLightTheme(),
                () => Dispatcher.BeginInvoke(() =>
                {
                    startupCancelled = true;
                    Shutdown(0);
                }));
            _ = ObserveSplashAsync(startupSplash);
            if (!isDemoMode)
            {
                // Import remains mandatory and precedes every state read. Its
                // filesystem traversal must not block either window dispatcher.
                await Task.Run(LegacyDataImporter.TryImport);
                StartupTrace.Mark("legacy-import-ready");
            }
            if (startupCancelled) return;
            var window = new MainWindow();
            MainWindow = window;
            window.SourceInitialized += (_, _) => startupSplash?.SetOwner(
                new System.Windows.Interop.WindowInteropHelper(window).Handle);
            window.Show();
            if (activationPending) window.RequestActivation();
            var errorTestMode = ErrorExperienceTest.Parse(e.Args, AppEnvironment.Resolve());
            if (errorTestMode != ErrorExperienceTestMode.None)
            {
                _ = Dispatcher.BeginInvoke(() => ErrorDialog.ShowTest(window, errorTestMode));
            }
        }
        catch (Exception exception)
        {
            startupSplash?.Dispose();
            WriteCrashLog(exception);
            TryCaptureException(exception);
            ShowFatalError(exception);
            Shutdown(1);
        }
    }

    internal async Task CompleteStartupPresentationAsync()
    {
        if (startupSplash is not { } splash) return;
        startupSplash = null;
        splash.Dispose();
        await splash.Completion;
        StartupTrace.Mark("startup-presentation-ready");
    }

    private async Task ObserveSplashAsync(StartupSplash splash)
    {
        try
        {
            await splash.Completion;
        }
        catch (Exception exception)
        {
            // A failed secondary dispatcher cannot leave a hidden, half-started
            // application behind. Use the same fatal path as the main window.
            startupCancelled = true;
            WriteCrashLog(exception);
            TryCaptureException(exception);
            ShowFatalError(exception);
            Shutdown(1);
        }
    }

    private static void OnActivationRequested()
    {
        // Raised on the SingleInstanceGuard listener thread; marshal to the
        // UI thread where the window lives.
        try
        {
            Current?.Dispatcher.BeginInvoke(ActivateMainWindowIfAvailable);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // The application is shutting down; there is no window to activate.
        }
    }

    private static void ActivateMainWindowIfAvailable()
    {
        if (Current?.MainWindow is MainWindow mainWindow)
        {
            mainWindow.RequestActivation();
        }
        else if (Current is App app)
        {
            app.activationPending = true;
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteCrashLog(e.Exception);
        TryCaptureException(e.Exception);
        ShowFatalError(e.Exception);
        Current?.Shutdown(1);
    }

    /// <summary>
    /// Exceptions thrown on a background thread with no surrounding
    /// try/catch. The process is already terminating by the time this runs
    /// (<see cref="UnhandledExceptionEventArgs.IsTerminating"/> is true in
    /// practice for this case), so this only records the crash — it cannot
    /// show a dialog reliably from a thread that may not own a Dispatcher.
    /// </summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
            TryCaptureException(exception);
        }
    }

    /// <summary>
    /// A faulted <see cref="Task"/> was garbage-collected without anyone
    /// observing its exception. Not fatal in .NET (unlike classic .NET
    /// Framework), so this only records it and marks it observed.
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        TryCaptureException(e.Exception);
        e.SetObserved();
    }

    private static void TryCaptureException(Exception exception)
    {
        try
        {
            CrashReporting.Current.CaptureException(exception);
        }
        catch
        {
            // A crash-reporting failure must never mask the original crash
            // nor throw from inside a crash handler.
        }
    }

    private static void TryShutdownCrashReporting()
    {
        CrashReportingLifecycle.TryShutdown(CrashReporting.Current);
    }

    private static void ShowFatalError(Exception exception)
    {
        (Current as App)?.startupSplash?.Dispose();
        if (Interlocked.Exchange(ref isHandlingFatalError, 1) != 0)
        {
            return;
        }

        try
        {
            // Includes failures from the independent splash dispatcher, which
            // do not unwind through MainWindow_Loaded's initialization catch.
            (Current?.MainWindow as MainWindow)?.InvalidateStartupHealthIfPending();
            ErrorDialog.ShowFatal(Current?.MainWindow, exception);
        }
        catch
        {
            // Never turn a dialog failure into a Dispatcher loop.
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = AppDataPaths.Combine("Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {ReportSanitizer.Sanitize(exception.ToString())}\n\n");
        }
        catch
        {
            // O log é diagnóstico opcional e não deve mascarar a exceção original.
        }
    }
}
