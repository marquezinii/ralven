using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ralven.App.Services;
using Ralven.App.Views;
namespace Ralven.App;

/// <summary>
/// The "Sua conta" section of the Settings page: profile photo, password,
/// e-mail and account deletion. This used to live inside a popup
/// (<c>AccountWindow</c>'s now-removed management panel) that reappeared
/// every time the signed-in user clicked their name in the header; it is now
/// a permanent card in Configurações, consistent with every other setting.
/// <c>AccountWindow</c> itself is used only for the sign-in/registration
/// journey and closes itself the moment the account becomes fully signed in
/// (see its <c>Render</c>/<c>CloseAfterSignIn</c>).
/// </summary>
public partial class MainWindow
{
    private readonly AccountAvatarStore avatarStore = new();
    private AccountEntitlementSnapshot accountEntitlement = new(AccountEntitlementTier.Unavailable);
    private DispatcherTimer? accountEntitlementExpiryTimer;
    private int accountEntitlementSyncVersion;
    private string? accountProfileUid;
    private string? accountUsername;

    private void OpenAccountFromSettings_Click(object sender, RoutedEventArgs e) => OpenAccountWindow();

    /// <summary>
    /// Reflects the current sign-in state into the Settings card: which of
    /// the three panels (unavailable / signed out / signed in) is shown, the
    /// e-mail/username readout, and the avatar in both the card and the
    /// header button. Called on every account state change and whenever the
    /// user navigates into Settings.
    /// </summary>
    private void RefreshAccountSettingsCard()
    {
        if (accountService is null)
        {
            AccountSettingsUnavailablePanel.Visibility = Visibility.Visible;
            AccountSettingsUnavailableText.Text = LocalizationService.Current.GetString("Settings.Account.Unavailable");
            AccountSettingsUnavailableRetryButton.Visibility = Visibility.Collapsed;
            AccountSettingsSignedOutPanel.Visibility = Visibility.Collapsed;
            AccountSettingsSignedInPanel.Visibility = Visibility.Collapsed;
            ApplyAccountEntitlementPresentation();
            return;
        }

        var profileUnavailable = accountService.Current.State == AuthenticationState.ProfileUnavailable;
        var currentUser = accountService.Current.User;
        var user = accountService.Current.State == AuthenticationState.SignedIn
            ? currentUser
            : null;
        AccountSettingsUnavailablePanel.Visibility = profileUnavailable ? Visibility.Visible : Visibility.Collapsed;
        AccountSettingsUnavailableText.Text = LocalizationService.Current.GetString(
            profileUnavailable ? "Account.ProfileUnavailable.Description" : "Settings.Account.Unavailable");
        AccountSettingsUnavailableRetryButton.Visibility = profileUnavailable ? Visibility.Visible : Visibility.Collapsed;
        AccountSettingsSignedOutPanel.Visibility = user is null && !profileUnavailable ? Visibility.Visible : Visibility.Collapsed;
        AccountSettingsSignedInPanel.Visibility = user is null ? Visibility.Collapsed : Visibility.Visible;

        AccountLabel.Visibility = currentUser is null || accountUsername is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (user is null)
        {
            DiscordLinkCodeText.Visibility = Visibility.Collapsed;
            ApplyAvatar(currentUser is null ? null : avatarStore.TryLoad(currentUser.Uid), AccountAvatarEllipse, AccountFallbackIcon);
            ApplyAvatar(null, AccountSettingsAvatarEllipse, AccountSettingsFallbackIcon);
            ApplyAccountEntitlementPresentation();
            return;
        }

        AccountSettingsEmailText.Text = user.Email;
        var hasPassword = user.HasPassword;
        AccountSettingsPasswordValue.Text = hasPassword
            ? "••••••••••"
            : LocalizationService.Current.GetString("Settings.Account.PasswordNotConfigured");
        var passwordAction = LocalizationService.Current.GetString(
            hasPassword ? "Settings.Account.ResetPasswordTooltip" : "Settings.Account.CreatePasswordTooltip");
        AccountSettingsPasswordButton.ToolTip = passwordAction;
        AutomationProperties.SetName(AccountSettingsPasswordButton, passwordAction);
        AccountSettingsCurrentPasswordPanel.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
        AccountSettingsCurrentMfaPanel.Visibility = user.Factors.Any(
            factor => factor.FactorType == FirebaseMfaFactorType.Totp)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccountSettingsPasswordRequiredHint.Visibility = user.HasGoogle ? Visibility.Visible : Visibility.Collapsed;
        AccountSettingsPasswordRequiredHint.Text = LocalizationService.Current.GetString(
            hasPassword ? "PasswordSecurity.GoogleFallback" : "Settings.Account.GoogleRequiredForSensitiveActions");
        var canConfirmIdentity = hasPassword || user.HasGoogle && googleOAuth.IsConfigured;
        AccountSettingsChangeEmailButton.IsEnabled = canConfirmIdentity;
        AccountSettingsDeleteAccountButton.IsEnabled = canConfirmIdentity;
        AccountSettingsChangeEmailButton.ToolTip = null;
        AccountSettingsDeleteAccountButton.ToolTip = null;
        AccountSettingsGoogleValue.Text = LocalizationService.Current.GetString(
            user.HasGoogle ? "Settings.Account.GoogleLinked" : "Settings.Account.GoogleNotLinked");
        AccountSettingsGoogleButton.Content = LocalizationService.Current.GetString(
            user.HasGoogle ? "Settings.Account.GoogleUnlink" : "Settings.Account.GoogleLink");
        AccountSettingsGoogleButton.IsEnabled = googleOAuth.IsConfigured
            && (!user.HasGoogle || user.HasPassword);
        AccountSettingsGoogleButton.ToolTip = user.HasGoogle && !user.HasPassword
            ? LocalizationService.Current.GetString("Settings.Account.LastSignInMethod")
            : null;
        AccountSettingsMfaValue.Text = LocalizationService.Current.GetString(
            user.Factors.Any(factor => factor.FactorType == FirebaseMfaFactorType.Totp)
                ? "Settings.Account.TwoFactorEnabled"
                : "Settings.Account.TwoFactorDisabled");
        RemovePhotoButton.Visibility = File.Exists(avatarStore.PathFor(user.Uid)) ? Visibility.Visible : Visibility.Collapsed;
        DiscordLinkButton.IsEnabled = discordLinkService is not null;

        var avatar = avatarStore.TryLoad(user.Uid);
        ApplyAvatar(avatar, AccountAvatarEllipse, AccountFallbackIcon);
        ApplyAvatar(avatar, AccountSettingsAvatarEllipse, AccountSettingsFallbackIcon);
        ApplyAccountEntitlementPresentation();
    }

    private async void DiscordLink_Click(object sender, RoutedEventArgs e)
    {
        if (discordLinkService is null
            || accountService?.Current is not { State: AuthenticationState.SignedIn }) return;
        DiscordLinkButton.IsEnabled = false;
        try
        {
            var token = await accountService.GetIdTokenAsync();
            var link = token is null ? null : await discordLinkService.CreateAsync(token);
            if (link is null)
            {
                AccountSettingsStatus(LocalizationService.Current.GetString("Settings.Account.Discord.Failed"), true);
                return;
            }

            var displayCode = $"{link.Code[..5]}-{link.Code[5..]}";
            DiscordLinkCodeText.Text = displayCode;
            DiscordLinkCodeText.Visibility = Visibility.Visible;
            try
            {
                System.Windows.Clipboard.SetText(displayCode);
                AccountSettingsStatus(LocalizationService.Current.GetString("Settings.Account.Discord.Copied"), false);
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException)
            {
                AccountSettingsStatus(LocalizationService.Current.GetString("Settings.Account.Discord.Generated"), false);
            }
        }
        finally
        {
            DiscordLinkButton.IsEnabled = true;
        }
    }

    private async void RetryAccountReadinessFromSettings_Click(object sender, RoutedEventArgs e)
    {
        if (accountService?.Current.State != AuthenticationState.ProfileUnavailable)
        {
            return;
        }

        AccountSettingsUnavailableRetryButton.IsEnabled = false;
        try
        {
            await accountService.RefreshAccountReadinessAsync();
        }
        finally
        {
            AccountSettingsUnavailableRetryButton.IsEnabled = true;
            RefreshAccountSettingsCard();
        }
    }

    private async void AccountEntitlementRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (accountService?.Current is not { State: AuthenticationState.SignedIn, User: { } user })
        {
            return;
        }

        await SyncAccountEntitlementAsync(user.Uid);
    }

    private async Task SyncAccountEntitlementAsync(string expectedUid)
    {
        var version = Interlocked.Increment(ref accountEntitlementSyncVersion);
        await Dispatcher.InvokeAsync(() => AccountEntitlementRefreshButton.IsEnabled = false);

        var snapshot = new AccountEntitlementSnapshot(AccountEntitlementTier.Unavailable);
        try
        {
            var idToken = await accountService!.GetIdTokenAsync().ConfigureAwait(false);
            if (idToken is not null && entitlementService is not null)
            {
                snapshot = await entitlementService.FetchAsync(idToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            snapshot = new AccountEntitlementSnapshot(AccountEntitlementTier.Unavailable);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrentAccountEntitlementResponse(
                    version,
                    Volatile.Read(ref accountEntitlementSyncVersion),
                    expectedUid,
                    accountService?.Current))
            {
                return;
            }

            accountEntitlement = snapshot;
            ApplyAccountEntitlementPresentation();
            ScheduleAccountEntitlementExpiry();
            AccountEntitlementRefreshButton.IsEnabled = true;
        });
    }

    internal static bool IsCurrentAccountEntitlementResponse(
        int responseVersion,
        int currentVersion,
        string expectedUid,
        AuthenticationSnapshot? current)
    {
        return responseVersion == currentVersion
            && current is { State: AuthenticationState.SignedIn, User: { } user }
            && string.Equals(user.Uid, expectedUid, StringComparison.Ordinal);
    }

    internal static bool IsEffectiveProEntitlement(
        AccountEntitlementSnapshot snapshot,
        DateTimeOffset now)
    {
        return snapshot is { Tier: AccountEntitlementTier.Pro, ValidUntil: { } validUntil }
            && validUntil > now;
    }

    private void ScheduleAccountEntitlementExpiry()
    {
        CancelAccountEntitlementExpiry();
        if (!IsEffectiveProEntitlement(accountEntitlement, TimeProvider.System.GetUtcNow())
            || accountEntitlement.ValidUntil is not { } validUntil)
        {
            return;
        }

        accountEntitlementExpiryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = NextAccountEntitlementExpiryCheck(validUntil)
        };
        accountEntitlementExpiryTimer.Tick += AccountEntitlementExpiryTimer_Tick;
        accountEntitlementExpiryTimer.Start();
    }

    private void AccountEntitlementExpiryTimer_Tick(object? sender, EventArgs e)
    {
        if (IsEffectiveProEntitlement(accountEntitlement, TimeProvider.System.GetUtcNow())
            && accountEntitlement.ValidUntil is { } validUntil)
        {
            accountEntitlementExpiryTimer!.Interval = NextAccountEntitlementExpiryCheck(validUntil);
            return;
        }

        CancelAccountEntitlementExpiry();
        accountEntitlement = new AccountEntitlementSnapshot(AccountEntitlementTier.Unavailable);
        ApplyAccountEntitlementPresentation();
    }

    private static TimeSpan NextAccountEntitlementExpiryCheck(DateTimeOffset validUntil)
    {
        var remaining = validUntil - TimeProvider.System.GetUtcNow();
        return remaining <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
    }

    private void CancelAccountEntitlementExpiry()
    {
        if (accountEntitlementExpiryTimer is null)
        {
            return;
        }

        accountEntitlementExpiryTimer.Stop();
        accountEntitlementExpiryTimer.Tick -= AccountEntitlementExpiryTimer_Tick;
        accountEntitlementExpiryTimer = null;
    }

    private void ClearAccountEntitlement()
    {
        Interlocked.Increment(ref accountEntitlementSyncVersion);
        CancelAccountEntitlementExpiry();
        accountEntitlement = new AccountEntitlementSnapshot(AccountEntitlementTier.Unavailable);
        AccountEntitlementRefreshButton.IsEnabled = true;
        ApplyAccountEntitlementPresentation();
    }

    private async Task<bool> AuthorizeProOperationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Demo uses only simulated optimization and an in-memory workspace.
        if (demoMode) return true;
        if (accountService?.Current is not { State: AuthenticationState.SignedIn, User: { } user }
            || entitlementService is null) return false;
        var version = Volatile.Read(ref accountEntitlementSyncVersion);
        try
        {
            var token = await accountService.GetIdTokenAsync().ConfigureAwait(false);
            if (token is null) return false;
            var snapshot = await entitlementService.FetchAsync(token, cancellationToken).ConfigureAwait(false);
            return IsCurrentAccountEntitlementResponse(version, Volatile.Read(ref accountEntitlementSyncVersion), user.Uid, accountService.Current)
                && IsEffectiveProEntitlement(snapshot, TimeProvider.System.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return false;
        }
    }

    private void ApplyAccountEntitlementPresentation()
    {
        proViewModel.Refresh();
        var hasProAccess = IsEffectiveProEntitlement(accountEntitlement, TimeProvider.System.GetUtcNow());
        viewModel.SetProAccess(demoMode || hasProAccess);
        viewModel.SetRalvenAiAccess(demoMode || (hasProAccess && accountEntitlement.HasRalvenAi));
        var localization = LocalizationService.Current;
        switch (accountEntitlement.Tier)
        {
            case AccountEntitlementTier.Free:
                AccountEntitlementValueText.Text = localization.GetString("Settings.Account.Plan.Free");
                AccountEntitlementDetailText.Text = localization.GetString("Settings.Account.Plan.FreeDetail");
                break;
            case AccountEntitlementTier.Pro when
                IsEffectiveProEntitlement(accountEntitlement, TimeProvider.System.GetUtcNow())
                && accountEntitlement.ValidUntil is { } validUntil:
                AccountEntitlementValueText.Text = localization.Format(
                    "Settings.Account.Plan.ProUntil",
                    validUntil.ToLocalTime().ToString("d", localization.CurrentCulture));
                AccountEntitlementDetailText.Text = localization.GetString("Settings.Account.Plan.ProDetail");
                break;
            default:
                AccountEntitlementValueText.Text = localization.GetString("Settings.Account.Plan.Unavailable");
                AccountEntitlementDetailText.Text = localization.GetString("Settings.Account.Plan.UnavailableDetail");
                break;
        }
    }

    /// <summary>Sets <paramref name="username"/> on the account surfaces once <see cref="SyncAccountFirstNameAsync"/> has read the profile.</summary>
    private void ApplyAccountSettingsUsername(string? username)
    {
        accountUsername = string.IsNullOrWhiteSpace(username) ? null : username;
        AccountSettingsUsernameText.Text = FormatAccountUsername(accountUsername);
    }

    internal static string FormatAccountUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? string.Empty : $"@{username}";

    private static void ApplyAvatar(BitmapImage? avatar, System.Windows.Shapes.Ellipse ellipse, Wpf.Ui.Controls.SymbolIcon fallback)
    {
        if (avatar is null)
        {
            ellipse.Fill = null;
            ellipse.Visibility = Visibility.Collapsed;
            fallback.Visibility = Visibility.Visible;
            return;
        }

        ellipse.Fill = new ImageBrush(avatar) { Stretch = Stretch.UniformToFill };
        ellipse.Visibility = Visibility.Visible;
        fallback.Visibility = Visibility.Collapsed;
    }

    private void ChangePhoto_Click(object sender, RoutedEventArgs e)
    {
        var user = accountService?.Current.User;
        if (user is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Current.GetString("Settings.Account.PhotoDialog.Title"),
            Filter = LocalizationService.Current.GetString("Settings.Account.PhotoDialog.Filter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (avatarStore.TrySave(user.Uid, dialog.FileName))
        {
            AccountSettingsStatus(LocalizationService.Current.GetString("Settings.Account.PhotoUpdated"), error: false);
            RefreshAccountSettingsCard();
        }
        else
        {
            AccountSettingsStatus(LocalizationService.Current.GetString("Settings.Account.PhotoInvalid"), error: true);
        }
    }

    private void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        var user = accountService?.Current.User;
        if (user is null)
        {
            return;
        }

        avatarStore.Delete(user.Uid);
        AccountSettingsStatus(LocalizationService.Current.GetString("Settings.Account.PhotoRemoved"), error: false);
        RefreshAccountSettingsCard();
    }

    private void AccountSettingsPassword_Click(object sender, RoutedEventArgs e)
    {
        if (accountService is null)
        {
            return;
        }

        var dialog = new PasswordSecurityWindow(accountService, googleOAuth) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            AccountSettingsStatus(
                LocalizationService.Current.GetString("PasswordSecurity.Success"),
                error: false);
            RefreshAccountSettingsCard();
        }
    }

    private async void AccountSettingsGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (accountService?.Current.User is not { } user || !googleOAuth.IsConfigured)
        {
            AccountSettingsStatus(LocalizationService.Current.GetString("Account.Google.NotConfigured"), true);
            return;
        }

        if (user.HasGoogle && !user.HasPassword)
        {
            AccountSettingsStatus(LocalizationService.Current.GetString("Settings.Account.LastSignInMethod"), true);
            return;
        }

        SetAccountSettingsBusy(true);
        try
        {
            if (!await TryReauthenticateAccountSettingsAsync(user))
            {
                return;
            }

            FirebaseAuthResult result;
            if (user.HasGoogle)
            {
                result = await accountService.UnlinkGoogleAsync();
            }
            else
            {
                var ticket = await googleOAuth.AuthenticateAsync();
                if (ticket.IdToken is null)
                {
                    AccountSettingsStatus(ticket.Error ?? LocalizationService.Current.GetString("Account.Google.Failed"), true);
                    return;
                }
                result = await accountService.LinkGoogleAsync(ticket.IdToken);
            }

            AccountSettingsStatus(
                result.Error ?? LocalizationService.Current.GetString(
                    user.HasGoogle ? "Settings.Account.GoogleUnlinked" : "Settings.Account.GoogleLinkedSuccess"),
                result.Error is not null);
            RefreshAccountSettingsCard();
        }
        finally
        {
            ClearAccountSettingsCredentials();
            SetAccountSettingsBusy(false);
        }
    }

    private void AccountSettingsMfa_Click(object sender, RoutedEventArgs e)
    {
        if (accountService is null)
        {
            return;
        }

        var dialog = new TwoFactorSecurityWindow(accountService, accountSecurityService, googleOAuth) { Owner = this };
        dialog.ShowDialog();
        RefreshAccountSettingsCard();
    }

    private async void AccountSettingsChangeEmail_Click(object sender, RoutedEventArgs e)
    {
        if (accountService is null)
        {
            return;
        }

        var user = accountService.Current.User;

        if (!AccountValidation.IsValidEmail(AccountSettingsNewEmailBox.Text))
        {
            AccountSettingsStatus(LocalizationService.Current.GetString("Account.Validation.InvalidEmail"), error: true);
            AccountSettingsNewEmailBox.Focus();
            return;
        }

        try
        {
            SetAccountSettingsBusy(true);
            if (user is null)
            {
                return;
            }

            if (!await TryReauthenticateAccountSettingsAsync(user))
            {
                return;
            }

            await RunAccountSettingsActionAsync(
                () => accountService.RequestEmailChangeAfterReauthenticationAsync(AccountSettingsNewEmailBox.Text.Trim()),
                success: LocalizationService.Current.GetString("Settings.Account.EmailChangeSent"));
        }
        finally
        {
            ClearAccountSettingsCredentials();
            AccountSettingsNewEmailBox.Clear();
            SetAccountSettingsBusy(false);
        }
    }

    private async void AccountSettingsDeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (accountService is null)
        {
            return;
        }

        if (OptimizationConfirmationWindow.Confirm(this,
                LocalizationService.Current.GetString("Settings.Account.DeleteConfirmation"),
                LocalizationService.Current.GetString("Settings.Account.Delete"),
                LocalizationService.Current.GetString("Settings.Account.Delete")) != true)
        {
            return;
        }

        var user = accountService.Current.User;
        if (user is null)
        {
            return;
        }
        FirebaseAuthResult result;
        try
        {
            SetAccountSettingsBusy(true);
            if (!await TryReauthenticateAccountSettingsAsync(user))
            {
                return;
            }

            result = await RunAccountSettingsActionAsync(
                () => accountService.DeleteAccountAfterReauthenticationAsync(),
                errorKey: "Account.Profile.DeleteFailed");
        }
        finally
        {
            ClearAccountSettingsCredentials();
            SetAccountSettingsBusy(false);
        }
        if (result.Succeeded && user is not null)
        {
            // The account is gone; a leftover local photo would just be an
            // orphaned file with nothing to attach it to.
            avatarStore.Delete(user.Uid);
        }
    }

    private async void AccountSettingsLogout_Click(object sender, RoutedEventArgs e)
    {
        if (accountService is null)
        {
            return;
        }

        SetAccountSettingsBusy(true);
        try
        {
            await accountService.LogoutAsync();
            AccountSettingsCurrentPasswordField.Clear();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AccountSettingsStatus(
                LocalizationService.Current.GetString("Account.Session.ClearFailed"),
                error: true);
        }
        finally
        {
            SetAccountSettingsBusy(false);
        }
    }

    private async Task<FirebaseAuthResult> RunAccountSettingsActionAsync(
        Func<Task<FirebaseAuthResult>> action,
        string? success = null,
        string? errorKey = null)
    {
        SetAccountSettingsBusy(true);
        try
        {
            var result = await action();
            var error = result.Error switch
            {
                FirebaseAuthService.ProfileDeletionFailedError when errorKey is not null =>
                    LocalizationService.Current.GetString(errorKey),
                FirebaseAuthService.AccountDeletionBillingRequiredError =>
                    LocalizationService.Current.GetString("Settings.Account.DeleteBillingRequired"),
                FirebaseAuthService.AccountDeletionReauthenticationRequiredError =>
                    LocalizationService.Current.GetString("Account.Error.ReauthenticationRequired"),
                FirebaseAuthService.AccountDeletionRateLimitedError =>
                    LocalizationService.Current.GetString("Account.Error.TooManyAttempts"),
                FirebaseAuthService.AccountDeletionUnavailableError =>
                    LocalizationService.Current.GetString("Settings.Account.DeleteUnavailable"),
                FirebaseAuthService.ProfileUnavailableError =>
                    LocalizationService.Current.GetString("Account.ProfileUnavailable.Description"),
                _ => result.Error,
            };
            AccountSettingsStatus(error ?? success ?? string.Empty, error: error is not null);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var error = LocalizationService.Current.GetString("Account.Session.ClearFailed");
            AccountSettingsStatus(error, error: true);
            return new FirebaseAuthResult(
                accountService?.Current.State ?? AuthenticationState.SignedOut,
                accountService?.Current.User,
                error);
        }
        finally
        {
            SetAccountSettingsBusy(false);
        }
    }

    private void SetAccountSettingsBusy(bool busy)
    {
        ChangePhotoButton.IsEnabled = !busy;
        RemovePhotoButton.IsEnabled = !busy;
        AccountSettingsPasswordButton.IsEnabled = !busy;
        AccountSettingsGoogleButton.IsEnabled = !busy
            && googleOAuth.IsConfigured
            && !(accountService?.Current.User is { HasGoogle: true, HasPassword: false });
        AccountSettingsMfaButton.IsEnabled = !busy;
        var hasIdentityProvider = accountService?.Current.User is { HasPassword: true }
            || accountService?.Current.User is { HasGoogle: true } && googleOAuth.IsConfigured;
        AccountSettingsChangeEmailButton.IsEnabled = !busy && hasIdentityProvider;
        AccountSettingsDeleteAccountButton.IsEnabled = !busy && hasIdentityProvider;
        AccountSettingsLogoutButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    /// <summary>
    /// Reautentica antes de uma mudança sensível e, quando falha, já publica o
    /// motivo na área de status. Existe porque os três fluxos que exigem
    /// reautenticação (Google, e-mail e exclusão de conta) repetiam a mesma
    /// checagem e a mesma mensagem de recuo.
    /// </summary>
    private async Task<bool> TryReauthenticateAccountSettingsAsync(FirebaseUser user)
    {
        var reauthenticated = await ReauthenticateAccountSettingsAsync(user);
        if (reauthenticated.Succeeded)
        {
            return true;
        }

        AccountSettingsStatus(
            reauthenticated.Error ?? LocalizationService.Current.GetString("Account.Error.ReauthenticationRequired"),
            true);
        return false;
    }

    private async Task<FirebaseAuthResult> ReauthenticateAccountSettingsAsync(FirebaseUser user)
    {
        FirebaseAuthResult result;
        if (user.HasPassword && (AccountSettingsCurrentPasswordField.Password.Length > 0 || !user.HasGoogle))
        {
            if (AccountSettingsCurrentPasswordField.Password.Length == 0)
            {
                AccountSettingsCurrentPasswordField.Focus();
                return new FirebaseAuthResult(
                    AuthenticationState.ReauthenticationRequired,
                    user,
                    LocalizationService.Current.GetString("PasswordSecurity.Validation.CurrentPasswordRequired"));
            }

            result = await accountService!.ReauthenticateWithPasswordAsync(AccountSettingsCurrentPasswordField.Password);
        }
        else
        {
            if (!googleOAuth.IsConfigured)
            {
                return new FirebaseAuthResult(
                    AuthenticationState.ReauthenticationRequired,
                    user,
                    LocalizationService.Current.GetString("PasswordSecurity.GoogleUnavailable"));
            }

            var ticket = await googleOAuth.AuthenticateAsync();
            if (ticket.IdToken is null)
            {
                return new FirebaseAuthResult(
                    AuthenticationState.ReauthenticationRequired,
                    user,
                    ticket.Error ?? LocalizationService.Current.GetString("Account.Google.Failed"));
            }
            result = await accountService!.ReauthenticateWithGoogleAsync(ticket.IdToken);
        }

        if (result.State != AuthenticationState.MfaChallengeRequired)
        {
            return result;
        }

        var factor = result.MfaChallenge?.Enrollments.FirstOrDefault(
            item => item.FactorType == FirebaseMfaFactorType.Totp);
        var code = AccountSettingsCurrentMfaCodeBox.Text.Trim();
        if (factor is null || code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            AccountSettingsCurrentMfaCodeBox.Focus();
            return new FirebaseAuthResult(
                AuthenticationState.MfaChallengeRequired,
                user,
                LocalizationService.Current.GetString("Account.Mfa.InvalidCode"));
        }

        return await accountService!.CompleteMfaReauthenticationAsync(factor.Id, code);
    }

    private void ClearAccountSettingsCredentials()
    {
        AccountSettingsCurrentPasswordField.Clear();
        AccountSettingsCurrentMfaCodeBox.Clear();
    }

    private void AccountSettingsStatus(string text, bool error)
    {
        if (string.IsNullOrEmpty(text))
        {
            AccountSettingsStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AccountSettingsStatusPanel.Visibility = Visibility.Visible;
        AccountSettingsStatusText.Text = text;
        AccountSettingsStatusText.SetResourceReference(ForegroundProperty, error ? "DangerBaseBrush" : "SuccessBaseBrush");
        AccountSettingsStatusIcon.Data = (Geometry)FindResource(error ? "IconAlertTriangle" : "IconCheck");
        AccountSettingsStatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, error ? "DangerBaseBrush" : "SuccessBaseBrush");
        AccountSettingsStatusPanel.SetResourceReference(BorderBrushProperty, error ? "DangerBorderBrush" : "SuccessBorderBrush");
        AccountSettingsStatusPanel.SetResourceReference(BackgroundProperty, error ? "DangerSurfaceBrush" : "SuccessSurfaceBrush");
    }

    private IFirebaseAuthService? CreateAccountService(
        bool demoMode,
        RemoteServicesOptions options,
        IAccountProfileService profiles)
    {
        if (demoMode
            || !FirebaseAuthConfiguration.TryGetApiKey(options.FirebaseApiKey, out var firebaseApiKey))
        {
            return null;
        }

        var service = new FirebaseAuthService(firebaseApiKey, profiles);
        service.StateChanged += (_, _) => Dispatcher.Invoke(UpdateAccountButton);
        return service;
    }

    private async Task RestoreAccountSessionQuietlyAsync()
    {
        try
        {
            await accountService!.RestoreSessionAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (accountService is null)
        {
            Ralven.App.Views.OptimizationConfirmationWindow.Inform(this, LocalizationService.Current.GetString("Settings.Account.Unavailable"), LocalizationService.Current.GetString("Settings.Account.Title"));
            return;
        }

        if (accountService.Current.State == AuthenticationState.SignedIn)
        {
            // Já logado: o clique no cabeçalho leva direto para
            // Configurações > Sua conta, em vez de reabrir a janela de
            // entrar/cadastrar (que agora só cuida de autenticar, não de
            // gerenciar a conta -- ver AccountWindow.CloseAfterSignIn).
            ActivateNavItem(SettingsNav);
            Navigate(SettingsPage);
            AccountSettingsCard.BringIntoView();
            return;
        }

        OpenAccountWindow();
    }

    private void OpenAccountWindow()
    {
        var dialog = new AccountWindow(accountService!, profileService, googleOAuth, accountSecurity: accountSecurityService) { Owner = this };
        if (dialog.ShowDialog() == true) UpdateAccountButton();
    }

    private void UpdateAccountButton()
    {
        var profile = accountService?.Current.User;
        if (profile is null || !string.Equals(profile.Uid, accountProfileUid, StringComparison.Ordinal))
        {
            accountProfileUid = null;
            ApplyAccountSettingsUsername(null);
        }

        AccountLabel.Text = profile is null
            ? LocalizationService.Current.GetString("Account.SignInButton")
            : FormatAccountUsername(accountUsername);
        AccountLabel.MaxWidth = profile is null ? 200 : 120;
        var accountAction = LocalizationService.Current.GetString(
            profile is null ? "Account.SignInTooltip" : "Account.ViewTooltip");
        AccountButton.ToolTip = accountAction;
        AutomationProperties.SetName(AccountButton, accountAction);
        // Also sets AccountInitials/avatar for both the header and the
        // Settings card, so a direct assignment here would just be
        // immediately overwritten.
        RefreshAccountSettingsCard();
    }

    private async void AccountService_StateChanged(object? sender, AuthenticationSnapshot snapshot)
    {
        Dispatcher.Invoke(UpdateAccountButton);
        Dispatcher.Invoke(UpdateBillingSession);
        if (snapshot.State != AuthenticationState.SignedIn || snapshot.User is null)
        {
            Dispatcher.Invoke(() =>
            {
                viewModel.SetAccountFirstName(null);
                ApplyAccountSettingsUsername(null);
                ClearAccountEntitlement();
            });
            return;
        }

        await Task.WhenAll(
            SyncAccountFirstNameAsync(snapshot.User.Uid),
            SyncAccountEntitlementAsync(snapshot.User.Uid));
    }

    /// <summary>
    /// Reads the caller's own first name for the Overview greeting. Firebase
    /// Authentication REST never stores it, so it only exists in the
    /// Worker's profile table; this is why login and quiet session restore
    /// both need a read call instead of getting it for free off the token.
    /// </summary>
    private async Task SyncAccountFirstNameAsync(string expectedUid)
    {
        if (accountService is null)
        {
            return;
        }

        try
        {
            var idToken = await accountService.GetIdTokenAsync().ConfigureAwait(false);
            if (idToken is null)
            {
                return;
            }

            var result = await profileService.FetchAsync(idToken).ConfigureAwait(false);
            if (result.Outcome == AccountProfileFetchOutcome.Found)
            {
                Dispatcher.Invoke(() =>
                {
                    if (!IsCurrentAccountProfileResponse(expectedUid, accountService.Current))
                    {
                        return;
                    }

                    accountProfileUid = expectedUid;
                    viewModel.SetAccountFirstName(result.FirstName);
                    ApplyAccountSettingsUsername(result.Username);
                    UpdateAccountButton();
                });
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Sem nome não é um estado de erro visível: a saudação simplesmente
            // fica sem o nome até a próxima sincronização bem-sucedida.
        }
    }

    internal static bool IsCurrentAccountProfileResponse(
        string expectedUid,
        AuthenticationSnapshot? current) =>
        current is { State: AuthenticationState.SignedIn, User: { } user }
        && string.Equals(user.Uid, expectedUid, StringComparison.Ordinal);
}
