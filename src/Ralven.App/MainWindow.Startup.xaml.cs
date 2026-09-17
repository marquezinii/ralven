using System.IO;
using Ralven.App.Services;
using Ralven.App.ViewModels;
using Ralven.App.Views;
using Ralven.Contracts;
using Ralven.UpdateRuntime;

namespace Ralven.App;

public partial class MainWindow
{
    internal void InvalidateStartupHealthIfPending()
    {
        if (!demoMode && !startupCompleted) InvalidateUpdateHealthReceiptIfRequested();
    }

    private static void ConfirmUpdateHealthIfRequested()
    {
        if (PackageIdentity.IsPackaged) return;
        var arguments = Environment.GetCommandLineArgs();
        var transaction = arguments.FirstOrDefault(value => value.StartsWith("--update-transaction=", StringComparison.OrdinalIgnoreCase))?["--update-transaction=".Length..];
        var nonce = arguments.FirstOrDefault(value => value.StartsWith("--update-nonce=", StringComparison.OrdinalIgnoreCase))?["--update-nonce=".Length..];
        var runtimeRoot = RuntimeLayout.Resolve(AppContext.BaseDirectory).RuntimeRoot;
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        if (version is null || runtimeRoot is null) return;
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.Name);
        if (transaction is not null && nonce is not null)
        {
            try { new UpdateHealthReceiptStore(runtimeRoot).Confirm(transaction, version, nonce); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // A falha preserva o app ativo; o launcher apenas não vê o
                // recibo de saúde neste momento e re-verifica na próxima
                // abertura antes de qualquer rollback.
            }
        }
        try { new VersionFloorStore(dataRoot).Advance(version); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // A falha preserva o app ativo; a próxima consulta de update falha
            // fechada ao não conseguir validar o piso DPAPI.
        }
    }

    private static void InvalidateUpdateHealthReceiptIfRequested()
    {
        if (PackageIdentity.IsPackaged) return;
        var runtimeRoot = RuntimeLayout.Resolve(AppContext.BaseDirectory).RuntimeRoot;
        if (runtimeRoot is null) return;
        try { new UpdateHealthReceiptStore(runtimeRoot).Invalidate(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Melhor esforço: se não for possível invalidar aqui, a
            // reverificação do launcher na próxima abertura é o fallback.
        }
    }

    /// <summary>
    /// Shows the blocking privacy consent screen when
    /// <see cref="MainViewModel.PrivacyConsentDecision"/> (computed once,
    /// right after settings finish loading in
    /// <see cref="MainViewModel.InitializeAsync"/>) says a decision is still
    /// pending. Runs before anything else in <see cref="MainWindow_Loaded"/>
    /// so the main window is shown but not meaningfully usable — the modal
    /// dialog blocks input to it — until the user confirms or closes it.
    /// Demo mode never shows this screen: it never persists settings or
    /// sends telemetry regardless, and smoke tests must not hang on a modal.
    /// </summary>
    private async Task ShowPrivacyConsentIfNeededAsync()
    {
        var decision = viewModel.PrivacyConsentDecision;
        if (decision is null || !decision.RequiresConsentScreen)
        {
            return;
        }

        var consentWindow = new PrivacyConsentWindow(
            decision.Variant,
            viewModel.ShareOptionalReports)
        {
            Owner = this
        };
        consentWindow.ShowDialog();
        await viewModel.ConfirmPrivacyConsentAsync(consentWindow.AcceptedOptionalReports);
    }

    /// <summary>
    /// Shows the informational, non-blocking "What's New" panel when
    /// <see cref="MainViewModel.PendingReleaseNotes"/> (computed once, right
    /// after settings finish loading in
    /// <see cref="MainViewModel.InitializeAsync"/>) says this version has
    /// notes the user has not seen yet. Persistence only happens after the
    /// panel is actually closed, so a crash before that point leaves the
    /// notes unseen and they are shown again next launch. When there is
    /// nothing to show but the evaluator still wants the current version
    /// recorded as a baseline (brand-new installation, or a version with no
    /// catalog entry), that happens immediately instead. Demo mode never
    /// shows this screen, for the same reason it never shows the privacy
    /// consent screen: it never persists settings, and smoke tests must not
    /// hang on a modal.
    /// </summary>
    private async Task ShowReleaseNotesIfNeededAsync()
    {
        var decision = viewModel.PendingReleaseNotes;
        if (decision is null)
        {
            return;
        }

        if (decision.ShouldShow && decision.Entry is not null)
        {
            var releaseNotesWindow = new ReleaseNotesWindow(decision.Entry) { Owner = this };
            releaseNotesWindow.ShowDialog();
            await viewModel.ConfirmReleaseNotesSeenAsync(decision.Entry.Version);
            return;
        }

        if (decision.ShouldRecordSilently)
        {
            await viewModel.ConfirmReleaseNotesSeenAsync(viewModel.AppVersion);
        }
    }

    /// <summary>
    /// Initializes the real Sentry-backed crash reporter, but only after
    /// <see cref="ShowPrivacyConsentIfNeededAsync"/> has resolved. The
    /// decision is evaluated from both the toggle and the consent version;
    /// anything else remains fail-closed. Loads
    /// the Sentry DSN from the environment-specific config file
    /// (<see cref="RemoteServicesOptionsLoader"/>) — never from a literal in
    /// source — and tags the event with the resolved
    /// <see cref="AppEnvironment"/> so development and production errors are
    /// never mixed together in Sentry.
    /// </summary>
    private void InitializeCrashReportingIfAuthorized()
    {
        var current = CrashReporting.Current;
        var next = CrashReportingLifecycle.InitializeIfAuthorized(
            viewModel.PrivacyConsentDecision?.AreOptionalReportsAuthorized == true,
            remoteServicesOptions,
            viewModel.AppVersion,
            static () => new SentryCrashReportingService());
        if (!ReferenceEquals(current, next))
        {
            CrashReportingLifecycle.TryShutdown(current);
        }

        CrashReporting.Current = next;
        crashReportingConfigured = true;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.MinimizeToTrayOnClose)
            or nameof(MainViewModel.IsBusy)
            or nameof(MainViewModel.ProgressHeadline)
            or nameof(MainViewModel.IsFiveMSessionMonitoring)
            or nameof(MainViewModel.IsFiveMSessionActive)
            or nameof(MainViewModel.FiveMSessionStatusLabel)
            or nameof(MainViewModel.IsUpdateBannerVisible)
            or nameof(MainViewModel.UpdateBannerTitle)
            or nameof(MainViewModel.IsLiveAlertIconVisible)
            or nameof(MainViewModel.LiveAlertMessage))
        {
            RefreshTrayIconPresentation();
        }

        if (crashReportingConfigured
            && e.PropertyName == nameof(MainViewModel.ShareOptionalReports))
        {
            InitializeCrashReportingIfAuthorized();
        }
    }

    /// <summary>
    /// Retries sending whatever telemetry could not be delivered during a
    /// previous, possibly offline, run. A no-op unless the Cloudflare
    /// transport is the active one (<see cref="queuedCloudflareTelemetry"/>
    /// is only set when a telemetry endpoint is configured).
    /// </summary>
    private Task FlushPendingTelemetryIfAnyAsync() =>
        queuedCloudflareTelemetry?.FlushPendingAsync() ?? Task.CompletedTask;

    private Task TrackAppInitializedTelemetryIfAuthorizedAsync() =>
        queuedCloudflareTelemetry?.TrackOncePerUtcDayAsync(viewModel.CreateAppInitializedTelemetryEvent())
        ?? Task.CompletedTask;
}
