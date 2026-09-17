using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ralven.App.Services;

public sealed class CloudflareAccountSecurityService : IAccountSecurityService
{
    private const int MaximumResponseBytes = 16 * 1024;
    private const int MaximumTokenCharacters = 16 * 1024;
    private const int MaximumPendingCredentialCharacters = 4096;
    private const int MaximumEnrollmentIdCharacters = 256;
    private const int MaximumRecoveryCodeCharacters = 128;
    private const int MaximumRecoveryCodes = 20;
    private static readonly Regex EnrollmentIdPattern = new(
        "^[A-Za-z0-9_-]{1,256}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RecoveryCodePattern = new(
        "^[23456789A-HJ-NP-Z]{5}-?[23456789A-HJ-NP-Z]{5}$",
        RegexOptions.CultureInvariant);
    private static readonly HttpClient SharedClient =
        CloudflareTransportDefaults.CreateClient(TimeSpan.FromSeconds(20));

    private readonly HttpClient httpClient;
    private readonly Uri recoveryCodesEndpoint;
    private readonly Uri recoverEndpoint;

    public CloudflareAccountSecurityService(Uri accountProfileEndpoint)
        : this(SharedClient, accountProfileEndpoint)
    {
    }

    internal CloudflareAccountSecurityService(HttpClient httpClient, Uri accountProfileEndpoint)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        var profileEndpoint = CloudflareTransportDefaults.ValidateHttpsEndpoint(
            accountProfileEndpoint,
            "Endpoint de segurança da conta inválido.");
        if (!profileEndpoint.AbsolutePath.EndsWith("/account/profile", StringComparison.Ordinal))
        {
            throw new ArgumentException("Endpoint de segurança da conta inválido.", nameof(accountProfileEndpoint));
        }

        recoveryCodesEndpoint = BuildEndpoint(profileEndpoint, "/account/mfa/recovery-codes");
        recoverEndpoint = BuildEndpoint(profileEndpoint, "/account/mfa/recover");
    }

    public async Task<RecoveryCodesResult> GenerateRecoveryCodesAsync(
        string idToken,
        string mfaEnrollmentId,
        CancellationToken cancellationToken = default)
    {
        if (!IsBoundedValue(idToken, MaximumTokenCharacters)
            || !IsEnrollmentId(mfaEnrollmentId))
        {
            return new RecoveryCodesResult(RecoveryCodesOutcome.InvalidInput, []);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, recoveryCodesEndpoint)
        {
            Content = JsonContent.Create(new { mfaEnrollmentId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var body = await ReadBoundedJsonAsync<RecoveryCodesResponse>(response, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var codes = body?.RecoveryCodes;
                if (codes is null || codes.Count is < 1 or > MaximumRecoveryCodes
                    || codes.Any(code => !IsBoundedValue(code, MaximumRecoveryCodeCharacters))
                    || codes.Any(code => !RecoveryCodePattern.IsMatch(code))
                    || codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
                {
                    return new RecoveryCodesResult(RecoveryCodesOutcome.Failed, []);
                }

                return new RecoveryCodesResult(RecoveryCodesOutcome.Created, codes.AsReadOnly());
            }

            var error = NormalizeErrorCode(body?.Error);
            return new RecoveryCodesResult(MapCodesFailure(response.StatusCode, error), [], error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return new RecoveryCodesResult(RecoveryCodesOutcome.Unavailable, []);
        }
    }

    public async Task<AccountRecoveryResult> RecoverAsync(
        string mfaPendingCredential,
        string mfaEnrollmentId,
        string recoveryCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedRecoveryCode = recoveryCode?.Trim().ToUpperInvariant();
        if (!IsBoundedValue(mfaPendingCredential, MaximumPendingCredentialCharacters)
            || mfaPendingCredential.Length < 16
            || !IsEnrollmentId(mfaEnrollmentId)
            || normalizedRecoveryCode is null
            || !IsBoundedValue(normalizedRecoveryCode, MaximumRecoveryCodeCharacters)
            || !RecoveryCodePattern.IsMatch(normalizedRecoveryCode))
        {
            return new AccountRecoveryResult(AccountRecoveryOutcome.InvalidInput);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, recoverEndpoint)
        {
            Content = JsonContent.Create(new
            {
                mfaPendingCredential,
                mfaEnrollmentId,
                recoveryCode = normalizedRecoveryCode,
            }),
        };

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var body = await ReadBoundedJsonAsync<RecoveryResponse>(response, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode && body?.Success is true)
            {
                return new AccountRecoveryResult(AccountRecoveryOutcome.Recovered);
            }

            var error = NormalizeErrorCode(body?.Error);
            var outcome = error switch
            {
                "invalid-recovery-code" or "recovery-code-used" or "invalid-recovery-proof" =>
                    AccountRecoveryOutcome.InvalidOrUsedCode,
                "recovery-in-progress" => AccountRecoveryOutcome.InProgress,
                "reauthentication-required" => AccountRecoveryOutcome.ReauthenticationRequired,
                "rate-limited" or "account-rate-limited" => AccountRecoveryOutcome.RateLimited,
                "recovery-unavailable" or "server-misconfigured" => AccountRecoveryOutcome.Unavailable,
                _ when response.StatusCode == HttpStatusCode.BadRequest => AccountRecoveryOutcome.InvalidInput,
                _ when response.StatusCode == HttpStatusCode.TooManyRequests => AccountRecoveryOutcome.RateLimited,
                _ when response.StatusCode == HttpStatusCode.ServiceUnavailable => AccountRecoveryOutcome.Unavailable,
                _ => AccountRecoveryOutcome.Failed,
            };
            return new AccountRecoveryResult(outcome, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return new AccountRecoveryResult(AccountRecoveryOutcome.Unavailable);
        }
    }

    public async Task<RecoveryCodesDeletionResult> DeleteRecoveryCodesAsync(
        string idToken,
        string mfaEnrollmentId,
        CancellationToken cancellationToken = default)
    {
        if (!IsBoundedValue(idToken, MaximumTokenCharacters)
            || !IsEnrollmentId(mfaEnrollmentId))
        {
            return new RecoveryCodesDeletionResult(RecoveryCodesDeletionOutcome.InvalidInput);
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, recoveryCodesEndpoint)
        {
            Content = JsonContent.Create(new { mfaEnrollmentId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new RecoveryCodesDeletionResult(RecoveryCodesDeletionOutcome.Deleted);
            }

            var body = await ReadBoundedJsonAsync<RecoveryResponse>(response, cancellationToken)
                .ConfigureAwait(false);
            var error = NormalizeErrorCode(body?.Error);
            var outcome = error switch
            {
                "invalid-mfa-enrollment" or "mfa-enrollment-not-found" =>
                    RecoveryCodesDeletionOutcome.InvalidInput,
                "reauthentication-required" or "recent-authentication-required" =>
                    RecoveryCodesDeletionOutcome.ReauthenticationRequired,
                "rate-limited" or "account-rate-limited" => RecoveryCodesDeletionOutcome.RateLimited,
                "recovery-codes-unavailable" or "account-security-unavailable" or "server-misconfigured" =>
                    RecoveryCodesDeletionOutcome.Unavailable,
                _ when response.StatusCode == HttpStatusCode.BadRequest =>
                    RecoveryCodesDeletionOutcome.InvalidInput,
                _ when response.StatusCode == HttpStatusCode.TooManyRequests =>
                    RecoveryCodesDeletionOutcome.RateLimited,
                _ when response.StatusCode == HttpStatusCode.ServiceUnavailable =>
                    RecoveryCodesDeletionOutcome.Unavailable,
                _ => RecoveryCodesDeletionOutcome.Failed,
            };
            return new RecoveryCodesDeletionResult(outcome, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return new RecoveryCodesDeletionResult(RecoveryCodesDeletionOutcome.Unavailable);
        }
    }

    private static RecoveryCodesOutcome MapCodesFailure(HttpStatusCode status, string? error) => error switch
    {
        "invalid-mfa-enrollment" => RecoveryCodesOutcome.InvalidInput,
        "mfa-enrollment-not-found" => RecoveryCodesOutcome.EnrollmentNotFound,
        "reauthentication-required" or "recent-authentication-required" =>
            RecoveryCodesOutcome.ReauthenticationRequired,
        "rate-limited" or "account-rate-limited" => RecoveryCodesOutcome.RateLimited,
        "recovery-codes-unavailable" or "account-security-unavailable" or "server-misconfigured" =>
            RecoveryCodesOutcome.Unavailable,
        _ when status == HttpStatusCode.BadRequest => RecoveryCodesOutcome.InvalidInput,
        _ when status == HttpStatusCode.TooManyRequests => RecoveryCodesOutcome.RateLimited,
        _ when status == HttpStatusCode.ServiceUnavailable => RecoveryCodesOutcome.Unavailable,
        _ => RecoveryCodesOutcome.Failed,
    };

    private static Uri BuildEndpoint(Uri profileEndpoint, string accountPath)
    {
        const string profilePath = "/account/profile";
        var prefix = profileEndpoint.AbsolutePath[..^profilePath.Length];
        var builder = new UriBuilder(profileEndpoint)
        {
            Path = prefix + accountPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static bool IsBoundedValue(string? value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumCharacters
        && !value.Any(char.IsControl);

    private static bool IsEnrollmentId(string? value) =>
        value is not null
        && IsBoundedValue(value, MaximumEnrollmentIdCharacters)
        && EnrollmentIdPattern.IsMatch(value);

    private static string? NormalizeErrorCode(string? error) =>
        error is { Length: > 0 and <= 64 }
        && error.All(character => char.IsAsciiLetterLower(character) || character is '-') ? error : null;

    private static Task<T?> ReadBoundedJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        => CloudflareTransportDefaults.ReadBoundedJsonAsync<T>(
            response,
            MaximumResponseBytes,
            options: null,
            cancellationToken);

    private sealed record RecoveryCodesResponse(
        [property: JsonPropertyName("recoveryCodes")] List<string>? RecoveryCodes,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record RecoveryResponse(
        [property: JsonPropertyName("success")] bool? Success,
        [property: JsonPropertyName("error")] string? Error);
}
