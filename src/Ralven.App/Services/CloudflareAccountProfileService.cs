using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Ralven.App.Services;

/// <summary>
/// Sends the profile-completion request to the Cloudflare Worker's
/// <c>POST /account/profile</c> route, authenticated with the caller's
/// fresh Firebase ID token. Validation mirrors the server (see
/// <c>infra/cloudflare-worker/src/auth/accountProfile.js</c>), which
/// re-validates and is the only source of truth for username uniqueness.
/// </summary>
public sealed class CloudflareAccountProfileService : IAccountProfileService
{
    private static readonly HttpClient SharedClient =
        CloudflareTransportDefaults.CreateClient(TimeSpan.FromSeconds(20));

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;
    private readonly Uri accountEndpoint;
    private readonly ILocalizationService localization;

    public CloudflareAccountProfileService(Uri endpoint, ILocalizationService? localization = null)
        : this(SharedClient, endpoint, localization)
    {
    }

    internal CloudflareAccountProfileService(HttpClient httpClient, Uri endpoint, ILocalizationService? localization = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.endpoint = CloudflareTransportDefaults.ValidateHttpsEndpoint(
            endpoint,
            "Endpoint de perfil de conta inválido.");
        accountEndpoint = BuildAccountEndpoint(this.endpoint);
        this.localization = localization ?? LocalizationService.Current;
    }

    public async Task<AccountProfileResult> CreateAsync(
        string idToken,
        AccountProfileSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        ArgumentNullException.ThrowIfNull(submission);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                username = submission.Username,
                firstName = submission.FirstName,
                lastName = submission.LastName,
                termsVersion = submission.TermsVersion,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new AccountProfileResult(
                AccountProfileOutcome.Failed,
                localization["Account.Profile.ConnectionFailed"]);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                AccountProfileErrorDto? body;
                try
                {
                    body = await response.Content
                        .ReadFromJsonAsync<AccountProfileErrorDto>(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (System.Text.Json.JsonException)
                {
                    body = null;
                }

                return body?.Error switch
                {
                    "username-taken" => new AccountProfileResult(
                        AccountProfileOutcome.UsernameTaken,
                        localization["Account.Profile.UsernameTaken"]),
                    "uid-taken" => new AccountProfileResult(AccountProfileOutcome.UidTaken, null),
                    _ => new AccountProfileResult(AccountProfileOutcome.Failed, null),
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AccountProfileResult(
                    AccountProfileOutcome.Failed,
                    localization.Format("Account.Profile.SaveHttpError", (int)response.StatusCode));
            }

            return new AccountProfileResult(AccountProfileOutcome.Created, null);
        }
    }

    public async Task<AccountProfileFetchResult> FetchAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new AccountProfileFetchResult(AccountProfileFetchOutcome.Failed);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AccountProfileFetchResult(AccountProfileFetchOutcome.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AccountProfileFetchResult(AccountProfileFetchOutcome.Failed);
            }

            AccountProfileResponseDto? body;
            try
            {
                body = await response.Content
                    .ReadFromJsonAsync<AccountProfileResponseDto>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                return new AccountProfileFetchResult(AccountProfileFetchOutcome.Failed);
            }

            if (body is null || string.IsNullOrWhiteSpace(body.FirstName))
            {
                return new AccountProfileFetchResult(AccountProfileFetchOutcome.Failed);
            }

            return new AccountProfileFetchResult(
                AccountProfileFetchOutcome.Found,
                body.Username,
                body.FirstName,
                body.LastName,
                body.TermsVersion);
        }
    }

    public async Task<AccountProfileDeletionResult> DeleteAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);

        using var request = new HttpRequestMessage(HttpMethod.Delete, accountEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new AccountProfileDeletionResult(AccountProfileDeletionOutcome.Deleted);
            }

            var errorCode = await ReadDeletionErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
            var outcome = errorCode switch
            {
                "billing-cancellation-required" => AccountProfileDeletionOutcome.BillingCancellationRequired,
                "reauthentication-required" or "recent-authentication-required" =>
                    AccountProfileDeletionOutcome.ReauthenticationRequired,
                "rate-limited" or "account-rate-limited" => AccountProfileDeletionOutcome.RateLimited,
                "account-deletion-unavailable" or "account-security-unavailable" or "server-misconfigured" =>
                    AccountProfileDeletionOutcome.Unavailable,
                _ when response.StatusCode == HttpStatusCode.TooManyRequests =>
                    AccountProfileDeletionOutcome.RateLimited,
                _ when response.StatusCode == HttpStatusCode.ServiceUnavailable =>
                    AccountProfileDeletionOutcome.Unavailable,
                _ => AccountProfileDeletionOutcome.Failed,
            };
            return new AccountProfileDeletionResult(outcome, errorCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return new AccountProfileDeletionResult(AccountProfileDeletionOutcome.Unavailable);
        }
    }

    private static Uri BuildAccountEndpoint(Uri profileEndpoint)
    {
        const string profileSuffix = "/profile";
        if (!profileEndpoint.AbsolutePath.EndsWith(profileSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Endpoint de perfil de conta inválido.", nameof(profileEndpoint));
        }

        var builder = new UriBuilder(profileEndpoint)
        {
            Path = profileEndpoint.AbsolutePath[..^profileSuffix.Length],
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static async Task<string?> ReadDeletionErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 4 * 1024;
        var body = await CloudflareTransportDefaults.ReadBoundedJsonAsync<AccountProfileErrorDto>(
            response,
            maximumBytes,
            options: null,
            cancellationToken).ConfigureAwait(false);
        return body?.Error is { Length: > 0 and <= 64 } error
            && error.All(character => char.IsAsciiLetterLower(character) || character is '-') ? error : null;
    }

    public async Task<UsernameAvailability> CheckUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        // Mirrors the server's own rule (accountProfile.js) so a name that
        // could never be accepted is reported locally, without a round trip.
        if (!AccountValidation.IsValidUsername(username))
        {
            return UsernameAvailability.Invalid;
        }

        // "…/account/profile" + "username-available" resolves to
        // "…/account/username-available": the probe always follows whichever
        // Worker origin the profile endpoint was configured with.
        var probe = new Uri(new Uri(endpoint, "username-available"), $"?u={Uri.EscapeDataString(username.Trim())}");

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .GetAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return UsernameAvailability.Unknown;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 400 means the server disagrees with our local rule, 429
                // means we asked too often -- neither is evidence the name
                // is free, so both stay Unknown/Invalid rather than green.
                return response.StatusCode == HttpStatusCode.BadRequest
                    ? UsernameAvailability.Invalid
                    : UsernameAvailability.Unknown;
            }

            UsernameAvailabilityDto? body;
            try
            {
                body = await response.Content
                    .ReadFromJsonAsync<UsernameAvailabilityDto>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                return UsernameAvailability.Unknown;
            }

            if (body?.Available is not { } available)
            {
                return UsernameAvailability.Unknown;
            }

            return available ? UsernameAvailability.Available : UsernameAvailability.Taken;
        }
    }

    private sealed record UsernameAvailabilityDto(
        [property: JsonPropertyName("available")] bool? Available);

    private sealed record AccountProfileErrorDto(
        [property: JsonPropertyName("error")] string? Error);

    private sealed record AccountProfileResponseDto(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("firstName")] string? FirstName,
        [property: JsonPropertyName("lastName")] string? LastName,
        [property: JsonPropertyName("termsVersion")] string? TermsVersion);
}
