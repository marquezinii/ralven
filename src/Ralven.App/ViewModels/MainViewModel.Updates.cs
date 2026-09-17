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

namespace Ralven.App.ViewModels;

public sealed partial class MainViewModel
{
    public bool IsUpdateBannerVisible => !isUpdateBannerDismissed && (availableUpdate is not null
        || updatePresentationState == UpdatePresentationState.Failed
        || JustUpdatedToVersion is not null);

    public bool IsUpdateAttentionVisible => !IsBusy && (availableUpdate is not null
        || updatePresentationState == UpdatePresentationState.Failed);

    public bool IsUpdateDownloading
    {
        get => isUpdateDownloading;
        private set
        {
            if (SetProperty(ref isUpdateDownloading, value))
            {
                RaiseCommandState();
            }
        }
    }

    public bool IsInstallingUpdate
    {
        get => isInstallingUpdate;
        private set
        {
            if (SetProperty(ref isInstallingUpdate, value))
            {
                RaiseCommandState();
            }
        }
    }

    /// <summary>
    /// Updating replaces the running executable and restarts the app. Doing
    /// that while a transaction is applying changes, rolling back or writing
    /// its journal would abandon the operation halfway with no way to finish
    /// or revert it, so the update button stays disabled until the app is idle.
    /// </summary>
    public bool CanDownloadUpdate => availableUpdate is not null
        && !IsUpdateDownloading
        && !IsInstallingUpdate
        && !IsCacheOperationRunning
        && !IsBusy
        && !isWindowsGamingBusy;

    /// <summary>
    /// Non-null only on the launch that immediately follows a successful
    /// automatic update (the installer relaunches the app with
    /// <c>--updated=X.Y.Z</c>), so the banner can confirm what happened.
    /// </summary>
    public string? JustUpdatedToVersion { get; private set; }

    public Uri? ReleaseNotesUri => availableUpdate?.ReleaseNotesUri;

    /// <summary>Core version string (e.g. "1.2.3") of the pending update, for
    /// the one confirmation dialog shown before the silent install starts.</summary>
    public string? AvailableUpdateVersion => availableUpdate?.Version.CoreVersion;

    public bool CanOpenReleaseNotes => ReleaseNotesUri is not null;

    public double UpdateDownloadPercent
    {
        get => updateDownloadPercent;
        private set => SetProperty(ref updateDownloadPercent, value);
    }

    public string UpdateBannerTitle
    {
        get => updateBannerTitle;
        private set => SetProperty(ref updateBannerTitle, value);
    }

    public string UpdateBannerDetail
    {
        get => updateBannerDetail;
        private set => SetProperty(ref updateBannerDetail, value);
    }

    public string UpdateBannerToneBrushKey => updatePresentationState switch
    {
        UpdatePresentationState.Failed => "DangerBaseBrush",
        _ when JustUpdatedToVersion is not null => "SuccessBaseBrush",
        _ => "InfoBaseBrush",
    };

    public string UpdateBannerIconKey => updatePresentationState switch
    {
        UpdatePresentationState.Failed => "IconAlertTriangle",
        _ when JustUpdatedToVersion is not null => "IconInfo",
        _ => "IconDownload",
    };

    /// <summary>
    /// The post-update confirmation banner has nothing left to act on, so the
    /// action button disappears instead of offering a redundant update.
    /// </summary>
    public bool IsUpdateActionVisible => JustUpdatedToVersion is null;

    /// <summary>
    /// True on the launch that follows a successful automatic update, when the
    /// confirmation banner can be dismissed once the user has read it.
    /// </summary>
    public bool IsUpdateCompletedBannerVisible => JustUpdatedToVersion is not null;

    /// <summary>
    /// One button, one meaning: the whole update is a single click. It only
    /// changes wording to reflect a retry after a failure.
    /// </summary>
    public string UpdateActionLabel => localization.GetString(
        updatePresentationState == UpdatePresentationState.Failed
            ? "Common.Retry"
            : "Update.InstallNow");

    public string UpdateReleaseNotesLabel => localization.GetString("Update.ReleaseNotes");

    public bool IsCheckingForUpdatesManually
    {
        get => isCheckingForUpdatesManually;
        private set
        {
            if (SetProperty(ref isCheckingForUpdatesManually, value))
            {
                OnPropertyChanged(nameof(CanCheckForUpdatesManually));
            }
        }
    }

    public bool CanCheckForUpdatesManually => releaseUpdateService is not null && !IsCheckingForUpdatesManually;

    public string? ManualUpdateCheckMessage
    {
        get => manualUpdateCheckMessage;
        private set => SetProperty(ref manualUpdateCheckMessage, value);
    }

    /// <summary>
    /// Raised exactly once, right when a newer version is first detected,
    /// carrying the new version's core string (e.g. "1.2.3"). The main
    /// window subscribes to this to show the native Windows notification
    /// regardless of whether the window is currently in the foreground or
    /// minimized to the tray.
    /// </summary>
    public event EventHandler<string>? UpdateAvailableDetected;

    public async Task CheckForUpdatesAsync()
    {
        if (releaseUpdateService is null || availableUpdate is not null)
        {
            return;
        }

        try
        {
            var update = await releaseUpdateService.CheckForUpdateAsync(
                StableSemanticVersion.FromVersion(GetAssemblyVersion()));
            if (update is null)
            {
                return;
            }

            ApplyDetectedUpdate(update, notifyUser: true);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Falha de rede na inicialização não interrompe diagnóstico nem otimização.
        }
    }

    /// <summary>
    /// Explicit "Procurar atualizações" entry point from Settings. Unlike
    /// <see cref="CheckForUpdatesAsync"/> (silent, startup-only, no-ops once
    /// an update was already found), this always performs a fresh check and
    /// always reports an outcome -- either the existing update banner, or an
    /// explicit "already on the latest version" message.
    /// </summary>
    public async Task CheckForUpdatesManuallyAsync()
    {
        if (releaseUpdateService is null || IsCheckingForUpdatesManually)
        {
            return;
        }

        IsCheckingForUpdatesManually = true;
        ManualUpdateCheckMessage = null;

        try
        {
            var update = await releaseUpdateService.CheckForUpdateAsync(
                StableSemanticVersion.FromVersion(GetAssemblyVersion()));

            if (update is null)
            {
                ManualUpdateCheckMessage = localization.GetString("Update.ManualCheck.UpToDate");
                return;
            }

            ApplyDetectedUpdate(update, notifyUser: false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var message = localization.DescribeException(exception);
            ManualUpdateCheckMessage = localization.Format("Update.ManualCheck.Failed", message);
        }
        finally
        {
            IsCheckingForUpdatesManually = false;
        }
    }

    private static Version GetAssemblyVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    private void ApplyDetectedUpdate(ReleaseUpdate update, bool notifyUser)
    {
        availableUpdate = update;
        isUpdateBannerDismissed = false;
        updatePresentationState = UpdatePresentationState.Available;
        RefreshUpdatePresentation();
        if (notifyUser)
        {
            UpdateAvailableDetected?.Invoke(this, update.Version.CoreVersion);
        }
    }

    public async Task<DownloadedUpdate?> DownloadAvailableUpdateAsync()
    {
        if (releaseUpdateService is null || availableUpdate is null || IsUpdateDownloading)
        {
            return null;
        }

        IsUpdateDownloading = true;
        updatePresentationState = UpdatePresentationState.Downloading;
        UpdateDownloadPercent = 0;
        RefreshUpdatePresentation();
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            UpdateDownloadPercent = value.Percentage;
            RefreshUpdatePresentation();
        });

        try
        {
            var downloaded = await releaseUpdateService.DownloadUpdateAsync(
                availableUpdate,
                progress);
            UpdateDownloadPercent = 100;
            updatePresentationState = UpdatePresentationState.Ready;
            RefreshUpdatePresentation();
            return downloaded;
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            updateFailureMessage = localization.DescribeException(exception);
            updatePresentationState = UpdatePresentationState.Failed;
            RefreshUpdatePresentation();
            return null;
        }
        finally
        {
            IsUpdateDownloading = false;
        }
    }

    /// <summary>
    /// The whole one-click update: download the verified installer, then run it
    /// silently. Returns <see langword="true"/> only when the installer is
    /// actually running and the caller must now close the app so its files can
    /// be replaced — the installer reopens the new version by itself. Any
    /// failure returns <see langword="false"/> with the banner already
    /// explaining what happened, and the app must stay open.
    /// </summary>
    public async Task<bool> DownloadAndInstallUpdateAsync()
    {
        if (!CanDownloadUpdate)
        {
            return false;
        }

        var downloaded = await DownloadAvailableUpdateAsync().ConfigureAwait(true);
        if (downloaded is null)
        {
            return false;
        }

        return await InstallDownloadedUpdateAsync(downloaded).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs an already downloaded and hash-verified installer in silent mode.
    /// Kept separate from the download so a retry does not re-download an
    /// installer that is already on disk and already verified.
    /// </summary>
    public async Task<bool> InstallDownloadedUpdateAsync(DownloadedUpdate downloaded)
    {
        ArgumentNullException.ThrowIfNull(downloaded);
        if (silentUpdateInstaller is null
            || IsInstallingUpdate
            || IsBusy
            || isWindowsGamingBusy)
        {
            return false;
        }

        IsInstallingUpdate = true;
        updatePresentationState = UpdatePresentationState.Installing;
        RefreshUpdatePresentation();

        try
        {
            var launch = await silentUpdateInstaller
                .StartAsync(downloaded)
                .ConfigureAwait(true);
            if (launch.Started)
            {
                return true;
            }

            updateFailureMessage = localization.GetString("Error.Unexpected");
            updatePresentationState = UpdatePresentationState.Failed;
            RefreshUpdatePresentation();
            return false;
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            updateFailureMessage = localization.DescribeException(exception);
            updatePresentationState = UpdatePresentationState.Failed;
            RefreshUpdatePresentation();
            return false;
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    /// <summary>
    /// Called at startup when the app was relaunched by its own installer after
    /// an automatic update. Shows the confirmation banner instead of the
    /// "update available" one.
    /// </summary>
    public void ReportCompletedUpdate(string installedVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return;
        }

        JustUpdatedToVersion = installedVersion;
        isUpdateBannerDismissed = false;
        availableUpdate = null;
        updatePresentationState = UpdatePresentationState.None;
        UpdateBannerTitle = localization.Format("Update.Completed.Title", installedVersion);
        UpdateBannerDetail = localization.GetString("Update.Completed.Detail");
        OnPropertyChanged(nameof(JustUpdatedToVersion));
        OnPropertyChanged(nameof(IsUpdateBannerVisible));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(IsUpdateActionVisible));
        OnPropertyChanged(nameof(IsUpdateCompletedBannerVisible));
        OnPropertyChanged(nameof(UpdateBannerToneBrushKey));
        OnPropertyChanged(nameof(UpdateBannerIconKey));
    }

    /// <summary>
    /// Hides the current update surface for this session only. Available
    /// updates remain discoverable through the title-bar indicator and return
    /// on the next launch instead of being silently ignored forever.
    /// </summary>
    public void DismissUpdateBanner()
    {
        if (!IsUpdateBannerVisible)
        {
            return;
        }

        if (JustUpdatedToVersion is not null)
        {
            JustUpdatedToVersion = null;
            UpdateBannerTitle = string.Empty;
            UpdateBannerDetail = string.Empty;
            OnPropertyChanged(nameof(JustUpdatedToVersion));
        }
        else
        {
            isUpdateBannerDismissed = true;
        }

        OnPropertyChanged(nameof(IsUpdateBannerVisible));
        OnPropertyChanged(nameof(IsUpdateActionVisible));
        OnPropertyChanged(nameof(IsUpdateCompletedBannerVisible));
    }

    public void DismissCompletedUpdateBanner() => DismissUpdateBanner();

    public void ShowUpdateBanner()
    {
        if (!IsUpdateAttentionVisible || !isUpdateBannerDismissed)
        {
            return;
        }

        isUpdateBannerDismissed = false;
        OnPropertyChanged(nameof(IsUpdateBannerVisible));
    }

    private void RefreshUpdatePresentation()
    {
        switch (updatePresentationState)
        {
            case UpdatePresentationState.Available when availableUpdate is not null:
                UpdateBannerTitle = localization.Format(
                    "Update.Available.Title",
                    availableUpdate.Version.CoreVersion);
                UpdateBannerDetail = localization.Format(
                    "Update.Available.Detail",
                    FormatBytes(availableUpdate.SizeBytes));
                break;
            case UpdatePresentationState.Downloading:
                UpdateBannerTitle = localization.GetString("Update.Downloading.Title");
                UpdateBannerDetail = localization.Format(
                    "Update.Downloading.Detail",
                    UpdateDownloadPercent);
                break;
            case UpdatePresentationState.Ready when availableUpdate is not null:
                UpdateBannerTitle = localization.Format(
                    "Update.Ready.Title",
                    availableUpdate.Version.CoreVersion);
                UpdateBannerDetail = localization.GetString("Update.Ready.Detail");
                break;
            case UpdatePresentationState.Installing when availableUpdate is not null:
                UpdateBannerTitle = localization.Format(
                    "Update.Installing.Title",
                    availableUpdate.Version.CoreVersion);
                UpdateBannerDetail = localization.GetString("Update.Installing.Detail");
                break;
            case UpdatePresentationState.Failed:
                UpdateBannerTitle = localization.GetString("Update.Failed.Title");
                UpdateBannerDetail = localization.Format(
                    "Update.Failed.Detail",
                    updateFailureMessage ?? localization.GetString("Common.Unknown"));
                break;
        }

        OnPropertyChanged(nameof(IsUpdateBannerVisible));
        OnPropertyChanged(nameof(IsUpdateActionVisible));
        OnPropertyChanged(nameof(UpdateActionLabel));
        OnPropertyChanged(nameof(UpdateReleaseNotesLabel));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(ReleaseNotesUri));
        OnPropertyChanged(nameof(CanOpenReleaseNotes));
        OnPropertyChanged(nameof(UpdateBannerToneBrushKey));
        OnPropertyChanged(nameof(UpdateBannerIconKey));
        OnPropertyChanged(nameof(IsUpdateAttentionVisible));
    }

    private string FormatBytes(long bytes)
    {
        const double giB = 1024d * 1024d * 1024d;
        const double miB = 1024d * 1024d;
        var culture = localization.CurrentCulture;
        return bytes >= giB
            ? $"{(bytes / giB).ToString("0.##", culture)} GB"
            : $"{(bytes / miB).ToString("0.#", culture)} MB";
    }

    private enum UpdatePresentationState
    {
        None,
        Available,
        Downloading,
        Ready,
        Installing,
        Failed
    }
}
