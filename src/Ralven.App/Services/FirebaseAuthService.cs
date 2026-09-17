using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Ralven.Contracts;

namespace Ralven.App.Services;

public sealed class FirebaseAuthService : IFirebaseAuthService
{
    public const string ProfileDeletionFailedError = "account-profile-deletion-failed";
    public const string AccountDeletionBillingRequiredError = "account-deletion-billing-required";
    public const string AccountDeletionReauthenticationRequiredError = "account-deletion-reauthentication-required";
    public const string AccountDeletionRateLimitedError = "account-deletion-rate-limited";
    public const string AccountDeletionUnavailableError = "account-deletion-unavailable";
    public const string ProfileUnavailableError = "account-profile-unavailable";
    public const string LocalSessionCleanupFailedError = "local-session-cleanup-failed";
    private const string IdentityBase = "https://identitytoolkit.googleapis.com/v1/";
    private const string IdentityV2Base = "https://identitytoolkit.googleapis.com/v2/";
    private const string SecureTokenBase = "https://securetoken.googleapis.com/v1/token";
    private readonly HttpClient client;
    private readonly string apiKey;
    private readonly SecureFirebaseSessionStore sessionStore;
    private readonly IAccountProfileService profiles;
    private readonly ILocalizationService localization;
    private string? idToken;
    private string? refreshToken;
    private bool persistSession;
    private DateTimeOffset tokenExpiresAt;
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private long sessionGeneration;
    private string? pendingMfaCredential;
    private long pendingMfaGeneration;
    private FirebaseUser? pendingMfaReauthenticationUser;
    private AuthenticationState? pendingMfaReauthenticationState;

    public FirebaseAuthService(string apiKey, IAccountProfileService profiles, ILocalizationService? localization = null)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, apiKey,
            new SecureFirebaseSessionStore(AppDataPaths.Combine("firebase.session")), profiles, localization)
    { }

    internal FirebaseAuthService(HttpClient client, string apiKey, SecureFirebaseSessionStore sessionStore, IAccountProfileService profiles, ILocalizationService? localization = null)
    {
        this.client = client;
        this.apiKey = apiKey;
        this.sessionStore = sessionStore;
        this.profiles = profiles;
        this.localization = localization ?? LocalizationService.Current;
    }

    public AuthenticationSnapshot Current { get; private set; } = new(AuthenticationState.SignedOut, null);
    public event EventHandler<AuthenticationSnapshot>? StateChanged;

    public async Task<FirebaseAuthResult> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var generation = BeginAuthentication(AuthenticationState.RefreshingSession);
        var stored = await sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null || string.IsNullOrWhiteSpace(stored.RefreshToken))
        {
            RestoreSignedOut(generation);
            return Result();
        }
        return await RefreshAsync(stored.RefreshToken, persist: true, generation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirebaseAuthResult> RegisterAsync(string email, string password, bool keepSignedIn, CancellationToken cancellationToken = default)
    {
        var generation = BeginAuthentication(AuthenticationState.SigningIn);
        if (!AccountValidation.IsValidEmail(email)) return FailAuthentication(generation, "INVALID_EMAIL");
        if (!AccountPasswordPolicy.IsValid(password)) return FailAuthentication(generation, "WEAK_PASSWORD");
        var response = await PostAsync<FirebaseTokenResponse>("accounts:signUp", new { email, password, returnSecureToken = true }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return FailAuthentication(generation, response.Error);
        var result = await AcceptTokensAsync(response.Value!, keepSignedIn, generation, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            var verification = await ResendVerificationEmailAsync(cancellationToken).ConfigureAwait(false);
            if (verification.Error is not null)
            {
                return new FirebaseAuthResult(result.State, result.User, localization["Account.Verification.SendFailed"]);
            }
        }
        return result;
    }

    public async Task<FirebaseAuthResult> SignInAsync(string email, string password, bool keepSignedIn, CancellationToken cancellationToken = default)
    {
        var generation = BeginAuthentication(AuthenticationState.SigningIn);
        if (!AccountValidation.IsValidEmail(email) || string.IsNullOrEmpty(password)) return FailAuthentication(generation, null, sensitiveFlow: true);
        var response = await PostAsync<FirebaseTokenResponse>("accounts:signInWithPassword", new { email, password, returnSecureToken = true }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return FailAuthentication(generation, response.Error, sensitiveFlow: true);
        if (TryBeginMfaChallenge(response.Value!, generation, reauthenticationUser: null, out var challenge)) return challenge;
        return await AcceptTokensAsync(response.Value!, keepSignedIn, generation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirebaseAuthResult> CompleteMfaSignInAsync(string enrollmentId, string verificationCode, bool keepSignedIn, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(enrollmentId) || !IsValidTotpCode(verificationCode)) return MfaFailure(FirebaseAuthErrorCodes.InvalidMfaCode);
        var generation = Volatile.Read(ref sessionGeneration);
        var credential = pendingMfaGeneration == generation ? pendingMfaCredential : null;
        if (string.IsNullOrWhiteSpace(credential)) return MfaFailure(FirebaseAuthErrorCodes.MfaChallengeExpired);
        var response = await PostV2Async<FirebaseMfaTokenResponse>("accounts/mfaSignIn:finalize", new
        {
            mfaPendingCredential = credential,
            mfaEnrollmentId = enrollmentId,
            totpVerificationInfo = new { verificationCode },
        }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return MfaFailure(response.Error);
        var tokens = new FirebaseTokenResponse(null, response.Value?.idToken, response.Value?.refreshToken, "3600");
        return await AcceptTokensAsync(tokens, keepSignedIn, generation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirebaseAuthResult> CompleteMfaReauthenticationAsync(string enrollmentId, string verificationCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(enrollmentId) || !IsValidTotpCode(verificationCode)) return MfaFailure(FirebaseAuthErrorCodes.InvalidMfaCode);
        var generation = Volatile.Read(ref sessionGeneration);
        var credential = pendingMfaGeneration == generation ? pendingMfaCredential : null;
        var expectedUser = pendingMfaReauthenticationUser;
        if (string.IsNullOrWhiteSpace(credential) || expectedUser is null) return MfaFailure(FirebaseAuthErrorCodes.MfaChallengeExpired);
        var response = await PostV2Async<FirebaseMfaTokenResponse>("accounts/mfaSignIn:finalize", new
        {
            mfaPendingCredential = credential,
            mfaEnrollmentId = enrollmentId,
            totpVerificationInfo = new { verificationCode },
        }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return MfaFailure(response.Error);
        return await CommitReauthenticationTokensAsync(
            new FirebaseTokenResponse(expectedUser.Uid, response.Value?.idToken, response.Value?.refreshToken, "3600"),
            expectedUser,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FederatedSignInResult> SignInWithGoogleAsync(string googleIdToken, bool keepSignedIn, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(googleIdToken);
        var generation = BeginAuthentication(AuthenticationState.SigningIn);
        var response = await PostGoogleAssertionAsync(googleIdToken, firebaseIdToken: null, autoCreate: true, cancellationToken).ConfigureAwait(false);

        if (response.Error is not null)
        {
            return new FederatedSignInResult(FailAuthentication(generation, response.Error, sensitiveFlow: true));
        }

        var idp = response.Value!;
        if (TryBeginMfaChallenge(idp.ToTokens(), generation, reauthenticationUser: null, out var challenge)) return new FederatedSignInResult(challenge);
        var result = await AcceptTokensAsync(idp.ToTokens(), keepSignedIn, generation, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new FederatedSignInResult(result, idp.isNewUser, idp.firstName, idp.lastName)
            : new FederatedSignInResult(result);
    }

    public async Task<FirebaseAuthResult> ReauthenticateWithGoogleAsync(string googleIdToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(googleIdToken);
        var expectedUser = Current.User;
        if (expectedUser is null) return Result();
        var generation = Volatile.Read(ref sessionGeneration);
        var response = await PostGoogleAssertionAsync(googleIdToken, firebaseIdToken: null, autoCreate: false, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return Fail(response.Error, sensitiveFlow: true);
        if (TryBeginMfaChallenge(response.Value!.ToTokens(), generation, expectedUser, out var challenge)) return challenge;
        if (!string.Equals(response.Value?.localId, expectedUser.Uid, StringComparison.Ordinal))
        {
            return new FirebaseAuthResult(
                AuthenticationState.ReauthenticationRequired,
                expectedUser,
                FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.GoogleAccountMismatch, localization));
        }
        return await CommitReauthenticationTokensAsync(response.Value!.ToTokens(), expectedUser, generation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirebaseAuthResult> LinkGoogleAsync(string googleIdToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(googleIdToken);
        var user = Current.User;
        if (user is null) return Result();
        if (user.HasGoogle) return new FirebaseAuthResult(Current.State, user, FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.ProviderAlreadyLinked, localization));
        var generation = Volatile.Read(ref sessionGeneration);
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var response = await PostGoogleAssertionAsync(googleIdToken, token, autoCreate: false, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return Fail(response.Error, sensitiveFlow: true);
        if (!string.Equals(response.Value?.localId, user.Uid, StringComparison.Ordinal))
        {
            return new FirebaseAuthResult(AuthenticationState.ReauthenticationRequired, user, FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.GoogleAccountMismatch, localization));
        }
        return await AcceptTokensAsync(response.Value!.ToTokens(), persistSession, generation, cancellationToken).ConfigureAwait(false);
    }

    public Task<FirebaseAuthResult> UnlinkGoogleAsync(CancellationToken cancellationToken = default) =>
        UnlinkProviderAsync("google.com", requireAlternative: Current.User?.HasPassword == true, cancellationToken);

    public async Task<FirebaseAuthResult> RefreshEmailVerificationAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        return await LoadUserAsync(token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirebaseAuthResult> RefreshAccountReadinessAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        return token is null ? Result() : await LoadUserAsync(token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirebaseAuthResult> ResendVerificationEmailAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var response = await PostAsync<object>("accounts:sendOobCode", new { requestType = "VERIFY_EMAIL", idToken = token }, cancellationToken, localizeEmail: true).ConfigureAwait(false);
        return response.Error is null ? Result() : Fail(response.Error, sensitiveFlow: true);
    }

    public async Task<FirebaseAuthResult> SendPasswordResetEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!AccountValidation.IsValidEmail(email)) return SensitiveValidationFailure();
        var response = await PostAsync<object>("accounts:sendOobCode", new { requestType = "PASSWORD_RESET", email }, cancellationToken, localizeEmail: true).ConfigureAwait(false);
        return response.Error is null ? Result() : new FirebaseAuthResult(Current.State, Current.User, FirebaseAuthErrorMapper.Map(response.Error, localization, true));
    }

    public async Task<FirebaseAuthResult> CreatePasswordAsync(string newPassword, CancellationToken cancellationToken = default)
    {
        var user = Current.User;
        if (user is null) return Result();
        if (user.HasPassword)
        {
            return new FirebaseAuthResult(
                AuthenticationState.ReauthenticationRequired,
                user,
                FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.AccountAlreadyHasPassword, localization));
        }
        if (!AccountPasswordPolicy.IsValid(newPassword)) return ValidationFailure("WEAK_PASSWORD");
        var generation = Volatile.Read(ref sessionGeneration);
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var response = await PostAsync<FirebaseTokenResponse>("accounts:signUp", new
        {
            idToken = token,
            email = user.Email,
            password = newPassword,
            returnSecureToken = true,
        }, cancellationToken).ConfigureAwait(false);
        return response.Error is null
            ? await AcceptTokensAsync(response.Value!, persistSession, generation, cancellationToken).ConfigureAwait(false)
            : Fail(response.Error, sensitiveFlow: true);
    }

    public Task<FirebaseAuthResult> UnlinkPasswordAsync(CancellationToken cancellationToken = default) =>
        UnlinkProviderAsync("password", requireAlternative: Current.User?.HasGoogle == true, cancellationToken);

    public async Task<FirebaseAuthResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!AccountPasswordPolicy.IsValid(newPassword)) return ValidationFailure("WEAK_PASSWORD");
        var reauthentication = await ReauthenticateWithPasswordAsync(currentPassword, cancellationToken).ConfigureAwait(false);
        if (!reauthentication.Succeeded) return reauthentication;
        return await UpdatePasswordAfterReauthenticationAsync(newPassword, cancellationToken).ConfigureAwait(false);
    }

    public Task<FirebaseAuthResult> UpdatePasswordAfterReauthenticationAsync(string newPassword, CancellationToken cancellationToken = default) =>
        AccountPasswordPolicy.IsValid(newPassword)
            ? UpdateAsync(newPassword, null, cancellationToken)
            : Task.FromResult(ValidationFailure("WEAK_PASSWORD"));

    public async Task<FirebaseAuthResult> ChangeEmailAsync(string currentPassword, string newEmail, CancellationToken cancellationToken = default)
    {
        if (!AccountValidation.IsValidEmail(newEmail)) return ValidationFailure("INVALID_EMAIL");
        var reauthentication = await ReauthenticateWithPasswordAsync(currentPassword, cancellationToken).ConfigureAwait(false);
        return reauthentication.Succeeded
            ? await RequestEmailChangeAfterReauthenticationAsync(newEmail, cancellationToken).ConfigureAwait(false)
            : reauthentication;
    }

    public Task<FirebaseAuthResult> RequestEmailChangeAfterReauthenticationAsync(string newEmail, CancellationToken cancellationToken = default) =>
        AccountValidation.IsValidEmail(newEmail)
            ? SendVerifyAndChangeEmailAsync(newEmail, cancellationToken)
            : Task.FromResult(ValidationFailure("INVALID_EMAIL"));

    public async Task<FirebaseAuthResult> ChangeEmailWithGoogleAsync(string googleIdToken, string newEmail, CancellationToken cancellationToken = default)
    {
        if (!AccountValidation.IsValidEmail(newEmail)) return ValidationFailure("INVALID_EMAIL");
        var reauthenticated = await ReauthenticateWithGoogleAsync(googleIdToken, cancellationToken).ConfigureAwait(false);
        return reauthenticated.Succeeded
            ? await RequestEmailChangeAfterReauthenticationAsync(newEmail, cancellationToken).ConfigureAwait(false)
            : reauthenticated;
    }

    public async Task<FirebaseAuthResult> DeleteAccountAsync(string currentPassword, CancellationToken cancellationToken = default)
    {
        var reauthentication = await ReauthenticateWithPasswordAsync(currentPassword, cancellationToken).ConfigureAwait(false);
        return reauthentication.Succeeded
            ? await DeleteAccountAfterReauthenticationAsync(cancellationToken).ConfigureAwait(false)
            : reauthentication;
    }

    public Task<FirebaseAuthResult> DeleteAccountAfterReauthenticationAsync(CancellationToken cancellationToken = default) =>
        DeleteRemoteAccountAsync(cancellationToken);

    public async Task<FirebaseAuthResult> DeleteAccountWithGoogleAsync(string googleIdToken, CancellationToken cancellationToken = default)
    {
        var reauthenticated = await ReauthenticateWithGoogleAsync(googleIdToken, cancellationToken).ConfigureAwait(false);
        return reauthenticated.Succeeded
            ? await DeleteAccountAfterReauthenticationAsync(cancellationToken).ConfigureAwait(false)
            : reauthenticated;
    }

    public async Task<TotpEnrollmentStartResult> StartTotpEnrollmentAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return new(null, null, 0, null, 0, null, localization["Account.Error.SessionInvalid"]);
        var response = await PostV2Async<FirebaseTotpEnrollmentStartResponse>("accounts/mfaEnrollment:start", new
        {
            idToken = token,
            totpEnrollmentInfo = new { },
        }, cancellationToken).ConfigureAwait(false);
        var info = response.Value?.totpSessionInfo;
        if (response.Error is not null || string.IsNullOrWhiteSpace(info?.sharedSecretKey) || string.IsNullOrWhiteSpace(info.sessionInfo))
        {
            return new(null, null, 0, null, 0, null, FirebaseAuthErrorMapper.Map(response.Error, localization, sensitiveFlow: true));
        }
        return new(
            info.sharedSecretKey,
            info.sessionInfo,
            info.verificationCodeLength,
            info.hashingAlgorithm,
            info.periodSec,
            DateTimeOffset.TryParse(info.finalizeEnrollmentTime, out var expiresAt) ? expiresAt : null);
    }

    public async Task<FirebaseAuthResult> FinalizeTotpEnrollmentAsync(string sessionInfo, string verificationCode, string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionInfo) || !IsValidTotpCode(verificationCode)) return MfaFailure(FirebaseAuthErrorCodes.InvalidMfaCode);
        var generation = Volatile.Read(ref sessionGeneration);
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var response = await PostV2Async<FirebaseMfaTokenResponse>("accounts/mfaEnrollment:finalize", new
        {
            idToken = token,
            displayName = string.IsNullOrWhiteSpace(displayName) ? "Authenticator" : displayName.Trim(),
            totpVerificationInfo = new { sessionInfo, verificationCode },
        }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return MfaFailure(response.Error);
        return await AcceptTokensAsync(
            new FirebaseTokenResponse(null, response.Value?.idToken, response.Value?.refreshToken, "3600"),
            persistSession,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirebaseAuthResult> WithdrawMfaEnrollmentAsync(string enrollmentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(enrollmentId)) return MfaFailure(FirebaseAuthErrorCodes.InvalidMfaCode);
        var generation = Volatile.Read(ref sessionGeneration);
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var response = await PostV2Async<FirebaseMfaTokenResponse>("accounts/mfaEnrollment:withdraw", new
        {
            idToken = token,
            mfaEnrollmentId = enrollmentId,
        }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) return MfaFailure(response.Error);
        return await AcceptTokensAsync(
            new FirebaseTokenResponse(null, response.Value?.idToken, response.Value?.refreshToken, "3600"),
            persistSession,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetIdTokenAsync(CancellationToken cancellationToken = default)
    {
        string? token;
        string? refresh;
        bool persist;
        DateTimeOffset expiresAt;
        long generation;
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current.User is null) return null;
            token = idToken;
            refresh = refreshToken;
            persist = persistSession;
            expiresAt = tokenExpiresAt;
            generation = sessionGeneration;
        }
        finally { sessionLock.Release(); }

        if (DateTimeOffset.UtcNow >= expiresAt - TimeSpan.FromMinutes(5))
        {
            var refreshed = await RefreshAsync(refresh, persist, generation, cancellationToken).ConfigureAwait(false);
            if (!refreshed.Succeeded) return null;
            await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return generation == sessionGeneration ? idToken : null; }
            finally { sessionLock.Release(); }
        }
        return token;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (Current.State == AuthenticationState.SignedOut) return;
        await LogoutCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LogoutCoreAsync(CancellationToken cancellationToken)
    {
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await sessionStore.ClearAsync().ConfigureAwait(false);
            sessionGeneration++;
            ClearInMemorySession();
            Current = new AuthenticationSnapshot(AuthenticationState.SignedOut, null);
        }
        finally
        {
            sessionLock.Release();
        }
        StateChanged?.Invoke(this, Current);
    }

    /// <summary>Invalidates in-memory tokens and best-effort removes an already rejected persisted session. Caller must hold <see cref="sessionLock"/>.</summary>
    private async Task ClearSessionStateAsync()
    {
        ClearInMemorySession();
        try
        {
            await sessionStore.ClearAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ClearInMemorySession()
    {
        idToken = refreshToken = null;
        pendingMfaCredential = null;
        pendingMfaGeneration = 0;
        pendingMfaReauthenticationUser = null;
        pendingMfaReauthenticationState = null;
        persistSession = false;
        tokenExpiresAt = default;
    }

    private async Task<FirebaseAuthResult> RefreshAsync(string? refresh, bool persist, long generation, CancellationToken cancellationToken)
    {
        FirebaseRefreshResponse? payload = null;
        var signedOut = false;
        try
        {
            await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (generation != sessionGeneration || string.IsNullOrWhiteSpace(refresh)) return Result();
                if (!string.IsNullOrWhiteSpace(idToken) && DateTimeOffset.UtcNow < tokenExpiresAt - TimeSpan.FromMinutes(5)) return Result();

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{SecureTokenBase}?key={apiKey}") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }) };
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                payload = await response.Content.ReadFromJsonAsync<FirebaseRefreshResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload?.id_token) || string.IsNullOrWhiteSpace(payload.refresh_token))
                {
                    Interlocked.Increment(ref sessionGeneration);
                    await ClearSessionStateAsync().ConfigureAwait(false);
                    Current = new AuthenticationSnapshot(AuthenticationState.SignedOut, null);
                    signedOut = true;
                }
                else
                {
                    if (persist) await sessionStore.WriteAsync(payload.refresh_token, cancellationToken).ConfigureAwait(false);
                    else await sessionStore.ClearAsync().ConfigureAwait(false);
                    idToken = payload.id_token;
                    refreshToken = payload.refresh_token;
                    tokenExpiresAt = Expiry(payload.expires_in);
                    persistSession = persist;
                }
            }
            finally { sessionLock.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Fail("NETWORK_REQUEST_FAILED"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException) { return Fail("NETWORK_REQUEST_FAILED"); }
        if (signedOut)
        {
            StateChanged?.Invoke(this, Current);
            return new FirebaseAuthResult(AuthenticationState.SignedOut, null, localization["Account.Error.SessionInvalid"]);
        }

        return await LoadUserAsync(payload!.id_token!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FirebaseAuthResult> UpdateAsync(string? password, string? email, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref sessionGeneration);
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var request = new Dictionary<string, object?> { ["idToken"] = token, ["returnSecureToken"] = true };
        if (password is not null) request["password"] = password;
        if (email is not null) request["email"] = email;
        var response = await PostAsync<FirebaseTokenResponse>("accounts:update", request, cancellationToken).ConfigureAwait(false);
        return response.Error is null ? await AcceptTokensAsync(response.Value!, persistSession, generation, cancellationToken).ConfigureAwait(false) : Fail(response.Error);
    }

    public async Task<FirebaseAuthResult> ReauthenticateWithPasswordAsync(string currentPassword, CancellationToken cancellationToken = default)
    {
        var expectedUser = Current.User;
        if (expectedUser is null || string.IsNullOrEmpty(currentPassword))
            return new FirebaseAuthResult(AuthenticationState.ReauthenticationRequired, expectedUser, FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.CurrentPasswordInvalid, localization));
        var generation = Volatile.Read(ref sessionGeneration);
        var response = await PostAsync<FirebaseTokenResponse>("accounts:signInWithPassword", new { email = expectedUser.Email, password = currentPassword, returnSecureToken = true }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null)
        {
            return response.Error is "INVALID_LOGIN_CREDENTIALS" or "INVALID_PASSWORD" or "EMAIL_NOT_FOUND"
                ? new FirebaseAuthResult(AuthenticationState.ReauthenticationRequired, expectedUser, FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.CurrentPasswordInvalid, localization))
                : Fail(response.Error, sensitiveFlow: true);
        }
        if (TryBeginMfaChallenge(response.Value!, generation, expectedUser, out var challenge)) return challenge;
        if (!string.Equals(response.Value?.localId, expectedUser.Uid, StringComparison.Ordinal))
            return new FirebaseAuthResult(AuthenticationState.ReauthenticationRequired, expectedUser, FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.CurrentPasswordInvalid, localization));
        return await CommitReauthenticationTokensAsync(response.Value!, expectedUser, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FirebaseAuthResult> AcceptTokensAsync(FirebaseTokenResponse tokens, bool persist, long generation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokens.idToken) || string.IsNullOrWhiteSpace(tokens.refreshToken)) return Fail("INVALID_ID_TOKEN");
        var loaded = await LoadUserCandidateAsync(tokens.idToken, cancellationToken).ConfigureAwait(false);
        if (loaded.User is null)
        {
            if (IsSessionInvalid(loaded.Error)) await InvalidateGenerationAsync(generation).ConfigureAwait(false);
            return Fail(loaded.Error ?? "INVALID_ID_TOKEN");
        }

        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != sessionGeneration) return Result();
            if (!persist)
            {
                await sessionStore.ClearAsync().ConfigureAwait(false);
            }

            idToken = tokens.idToken; refreshToken = tokens.refreshToken; tokenExpiresAt = Expiry(tokens.expiresIn);
            if (persist) await sessionStore.WriteAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            persistSession = persist;
            pendingMfaCredential = null;
            pendingMfaGeneration = 0;
            Current = new AuthenticationSnapshot(loaded.State, loaded.User);
        }
        finally
        {
            sessionLock.Release();
        }

        StateChanged?.Invoke(this, Current);
        return new FirebaseAuthResult(Current.State, Current.User, loaded.Error);
    }

    private async Task<FirebaseAuthResult> LoadUserAsync(string token, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref sessionGeneration);
        var loaded = await LoadUserCandidateAsync(token, cancellationToken).ConfigureAwait(false);
        if (loaded.User is null)
        {
            if (IsSessionInvalid(loaded.Error)) await InvalidateGenerationAsync(generation).ConfigureAwait(false);
            return Fail(loaded.Error ?? "INVALID_ID_TOKEN");
        }
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != sessionGeneration || !string.Equals(idToken, token, StringComparison.Ordinal)) return Result();
            Current = new AuthenticationSnapshot(loaded.State, loaded.User);
        }
        finally { sessionLock.Release(); }
        StateChanged?.Invoke(this, Current);
        return new FirebaseAuthResult(Current.State, Current.User, loaded.Error);
    }

    private async Task<(AuthenticationState State, FirebaseUser? User, string? Error)> LoadUserCandidateAsync(string token, CancellationToken cancellationToken)
    {
        var response = await PostAsync<FirebaseLookupResponse>("accounts:lookup", new { idToken = token }, cancellationToken).ConfigureAwait(false);
        var user = response.Value?.users?.FirstOrDefault();
        if (response.Error is not null || string.IsNullOrWhiteSpace(user?.localId) || string.IsNullOrWhiteSpace(user.email)) return (AuthenticationState.SignedOut, null, response.Error ?? "INVALID_ID_TOKEN");
        var hasPassword = user.providerUserInfo?.Any(provider =>
            string.Equals(provider.providerId, "password", StringComparison.Ordinal)) == true;
        var hasGoogle = user.providerUserInfo?.Any(provider =>
            string.Equals(provider.providerId, "google.com", StringComparison.Ordinal)) == true;
        var factors = MapEnrollments(user.mfaInfo);
        var firebaseUser = new FirebaseUser(user.localId, user.email, user.emailVerified, hasPassword, hasGoogle, factors);
        if (!firebaseUser.EmailVerified)
        {
            return (AuthenticationState.EmailVerificationRequired, firebaseUser, null);
        }

        var readiness = await ResolveReadinessAsync(token, cancellationToken).ConfigureAwait(false);
        return (readiness.State, firebaseUser, readiness.Error);
    }

    private async Task<(AuthenticationState State, string? Error)> ResolveReadinessAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await profiles.FetchAsync(token, cancellationToken).ConfigureAwait(false);
            return profile.Outcome switch
            {
                AccountProfileFetchOutcome.Found when profile.TermsVersion == AccountTerms.CurrentVersion =>
                    (AuthenticationState.SignedIn, null),
                AccountProfileFetchOutcome.Found or AccountProfileFetchOutcome.NotFound =>
                    (AuthenticationState.ProfileCompletionRequired, null),
                _ => (AuthenticationState.ProfileUnavailable, ProfileUnavailableError),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return (AuthenticationState.ProfileUnavailable, ProfileUnavailableError);
        }
    }

    private async Task<(T? Value, string? Error)> PostAsync<T>(string path, object body, CancellationToken cancellationToken, bool localizeEmail = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{IdentityBase}{path}?key={apiKey}")
            {
                Content = JsonContent.Create(body),
            };
            if (localizeEmail)
            {
                request.Headers.TryAddWithoutValidation("X-Firebase-Locale", localization.CurrentCulture.Name);
            }
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false), null);
            var error = await response.Content.ReadFromJsonAsync<FirebaseErrorEnvelope>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return (default, error?.error?.message ?? "UNKNOWN");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (default, "NETWORK_REQUEST_FAILED"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException) { return (default, "NETWORK_REQUEST_FAILED"); }
    }

    private async Task<(T? Value, string? Error)> PostV2Async<T>(string path, object body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync($"{IdentityV2Base}{path}?key={apiKey}", body, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false), null);
            var error = await response.Content.ReadFromJsonAsync<FirebaseErrorEnvelope>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return (default, error?.error?.message ?? "UNKNOWN");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (default, "NETWORK_REQUEST_FAILED"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException) { return (default, "NETWORK_REQUEST_FAILED"); }
    }

    private Task<(FirebaseIdpResponse? Value, string? Error)> PostGoogleAssertionAsync(
        string googleIdToken,
        string? firebaseIdToken,
        bool autoCreate,
        CancellationToken cancellationToken) =>
        PostAsync<FirebaseIdpResponse>(
            "accounts:signInWithIdp",
            new Dictionary<string, object?>
            {
                ["postBody"] = $"id_token={Uri.EscapeDataString(googleIdToken)}&providerId=google.com",
                // Identity Toolkit only uses this as the claimed assertion
                // origin; the browser's actual loopback port is irrelevant.
                ["requestUri"] = "http://localhost",
                ["returnIdpCredential"] = true,
                ["returnSecureToken"] = true,
                ["autoCreate"] = autoCreate,
                ["idToken"] = firebaseIdToken,
            },
            cancellationToken);

    private long BeginAuthentication(AuthenticationState state)
    {
        var generation = Interlocked.Increment(ref sessionGeneration);
        pendingMfaCredential = null;
        pendingMfaGeneration = 0;
        pendingMfaReauthenticationUser = null;
        pendingMfaReauthenticationState = null;
        SetState(state, user: null, preserveUser: false);
        return generation;
    }

    private void RestoreSignedOut(long generation)
    {
        if (generation == Volatile.Read(ref sessionGeneration)) SetState(AuthenticationState.SignedOut, preserveUser: false);
    }

    private FirebaseAuthResult FailAuthentication(long generation, string? error, bool sensitiveFlow = false)
    {
        RestoreSignedOut(generation);
        return new FirebaseAuthResult(Current.State, Current.User, FirebaseAuthErrorMapper.Map(error, localization, sensitiveFlow));
    }
    private bool TryBeginMfaChallenge(FirebaseTokenResponse tokens, long generation, FirebaseUser? reauthenticationUser, out FirebaseAuthResult result)
    {
        if (string.IsNullOrWhiteSpace(tokens.mfaPendingCredential) || tokens.mfaInfo is not { Length: > 0 })
        {
            result = Result();
            return false;
        }
        if (generation != Volatile.Read(ref sessionGeneration))
        {
            result = Result();
            return true;
        }
        pendingMfaCredential = tokens.mfaPendingCredential;
        pendingMfaGeneration = generation;
        pendingMfaReauthenticationUser = reauthenticationUser;
        pendingMfaReauthenticationState = reauthenticationUser is null ? null : Current.State;
        var challenge = new FirebaseMfaChallenge(tokens.mfaPendingCredential, MapEnrollments(tokens.mfaInfo));
        if (reauthenticationUser is null)
        {
            SetState(AuthenticationState.MfaChallengeRequired);
        }
        result = new FirebaseAuthResult(AuthenticationState.MfaChallengeRequired, reauthenticationUser) { MfaChallenge = challenge };
        return true;
    }

    private async Task<FirebaseAuthResult> CommitReauthenticationTokensAsync(
        FirebaseTokenResponse tokens,
        FirebaseUser expectedUser,
        long generation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokens.idToken) || string.IsNullOrWhiteSpace(tokens.refreshToken)) return Fail("INVALID_ID_TOKEN", sensitiveFlow: true);
        var stateChanged = false;
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != sessionGeneration || Current.User?.Uid != expectedUser.Uid) return Result();
            idToken = tokens.idToken;
            refreshToken = tokens.refreshToken;
            tokenExpiresAt = Expiry(tokens.expiresIn);
            if (persistSession) await sessionStore.WriteAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            if (pendingMfaReauthenticationState is { } previousState)
            {
                Current = new AuthenticationSnapshot(previousState, expectedUser);
                stateChanged = true;
            }
            pendingMfaCredential = null;
            pendingMfaGeneration = 0;
            pendingMfaReauthenticationUser = null;
            pendingMfaReauthenticationState = null;
        }
        finally { sessionLock.Release(); }
        if (stateChanged) StateChanged?.Invoke(this, Current);
        return Result();
    }

    private async Task<FirebaseAuthResult> UnlinkProviderAsync(string providerId, bool requireAlternative, CancellationToken cancellationToken)
    {
        var user = Current.User;
        if (user is null) return Result();
        var linked = providerId == "google.com" ? user.HasGoogle : user.HasPassword;
        if (!linked) return new FirebaseAuthResult(Current.State, user, FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.ProviderNotLinked, localization));
        if (!requireAlternative) return new FirebaseAuthResult(Current.State, user, FirebaseAuthErrorMapper.Map(FirebaseAuthErrorCodes.LastSignInMethod, localization));
        var generation = Volatile.Read(ref sessionGeneration);
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var response = await PostAsync<FirebaseTokenResponse>("accounts:update", new
        {
            idToken = token,
            deleteProvider = new[] { providerId },
            returnSecureToken = true,
        }, cancellationToken).ConfigureAwait(false);
        return response.Error is null
            ? await AcceptTokensAsync(response.Value!, persistSession, generation, cancellationToken).ConfigureAwait(false)
            : Fail(response.Error, sensitiveFlow: true);
    }

    private async Task<FirebaseAuthResult> SendVerifyAndChangeEmailAsync(string newEmail, CancellationToken cancellationToken)
    {
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var response = await PostAsync<object>("accounts:sendOobCode", new
        {
            requestType = "VERIFY_AND_CHANGE_EMAIL",
            idToken = token,
            newEmail,
        }, cancellationToken, localizeEmail: true).ConfigureAwait(false);
        return response.Error is null ? Result() : Fail(response.Error, sensitiveFlow: true);
    }

    private async Task<FirebaseAuthResult> DeleteRemoteAccountAsync(CancellationToken cancellationToken)
    {
        var token = await GetIdTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return Result();
        var deleted = await profiles.DeleteAsync(token, cancellationToken).ConfigureAwait(false);
        if (deleted.Outcome != AccountProfileDeletionOutcome.Deleted)
        {
            var error = deleted.Outcome switch
            {
                AccountProfileDeletionOutcome.BillingCancellationRequired => AccountDeletionBillingRequiredError,
                AccountProfileDeletionOutcome.ReauthenticationRequired => AccountDeletionReauthenticationRequiredError,
                AccountProfileDeletionOutcome.RateLimited => AccountDeletionRateLimitedError,
                AccountProfileDeletionOutcome.Unavailable => AccountDeletionUnavailableError,
                _ => ProfileDeletionFailedError,
            };
            return new FirebaseAuthResult(Current.State, Current.User, error);
        }

        var localCleanupFailed = false;
        await sessionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            sessionGeneration++;
            ClearInMemorySession();
            Current = new AuthenticationSnapshot(AuthenticationState.SignedOut, null);
            try { await sessionStore.ClearAsync().ConfigureAwait(false); }
            catch (Exception) { localCleanupFailed = true; }
        }
        finally { sessionLock.Release(); }
        StateChanged?.Invoke(this, Current);
        return new FirebaseAuthResult(
            AuthenticationState.SignedOut,
            null,
            localCleanupFailed ? LocalSessionCleanupFailedError : null)
        { AccountDeleted = true };
    }

    private async Task InvalidateGenerationAsync(long generation)
    {
        await sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != sessionGeneration) return;
            sessionGeneration++;
            ClearInMemorySession();
            Current = new AuthenticationSnapshot(AuthenticationState.SignedOut, null);
            try { await sessionStore.ClearAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        finally { sessionLock.Release(); }
        StateChanged?.Invoke(this, Current);
    }

    private static IReadOnlyList<FirebaseMfaEnrollment> MapEnrollments(FirebaseMfaEnrollmentResponse[]? values) =>
        values?.Where(value => !string.IsNullOrWhiteSpace(value.mfaEnrollmentId)).Select(value => new FirebaseMfaEnrollment(
            value.mfaEnrollmentId!,
            value.displayName,
            DateTimeOffset.TryParse(value.enrolledAt, out var enrolledAt) ? enrolledAt : null,
            value.totpInfo is not null ? FirebaseMfaFactorType.Totp
                : !string.IsNullOrWhiteSpace(value.phoneInfo) ? FirebaseMfaFactorType.Phone
                : FirebaseMfaFactorType.Unknown)).ToArray() ?? [];

    private static bool IsValidTotpCode(string? value) =>
        value is { Length: >= 6 and <= 8 } && value.All(char.IsAsciiDigit);

    private FirebaseAuthResult MfaFailure(string? error) =>
        new(AuthenticationState.MfaChallengeRequired, Current.User, FirebaseAuthErrorMapper.Map(error switch
        {
            "INVALID_CODE" or "INVALID_VERIFICATION_CODE" => FirebaseAuthErrorCodes.InvalidMfaCode,
            "MFA_PENDING_CREDENTIAL_INVALID" or "MFA_PENDING_CREDENTIAL_EXPIRED" => FirebaseAuthErrorCodes.MfaChallengeExpired,
            _ => error,
        }, localization, sensitiveFlow: true));

    private FirebaseAuthResult ValidationFailure(string code) =>
        new(Current.State, Current.User, FirebaseAuthErrorMapper.Map(code, localization));

    private FirebaseAuthResult SensitiveValidationFailure() =>
        new(Current.State, Current.User, FirebaseAuthErrorMapper.Map(null, localization, sensitiveFlow: true));

    private FirebaseAuthResult Result() => new(Current.State, Current.User);
    private FirebaseAuthResult Fail(string? error, bool sensitiveFlow = false)
        => new(Current.State, Current.User, FirebaseAuthErrorMapper.Map(error, localization, sensitiveFlow));
    private void SetState(AuthenticationState state, FirebaseUser? user = null, bool preserveUser = true)
    {
        Current = new AuthenticationSnapshot(state, user ?? (preserveUser ? Current.User : null));
        if (state == AuthenticationState.SignedOut) Current = new AuthenticationSnapshot(state, null);
        StateChanged?.Invoke(this, Current);
    }
    private static DateTimeOffset Expiry(string? seconds) => DateTimeOffset.UtcNow.AddSeconds(long.TryParse(seconds, out var value) ? value : 3600);
    private static bool IsSessionInvalid(string? error) => error is "INVALID_ID_TOKEN" or "TOKEN_EXPIRED" or "INVALID_REFRESH_TOKEN" or "USER_DISABLED";
    public void Dispose() => client.Dispose();
}
