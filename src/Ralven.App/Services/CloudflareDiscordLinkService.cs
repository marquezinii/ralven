using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ralven.App.Services;

public sealed record DiscordLinkCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>Creates a short-lived code for linking the signed-in Ralven account to Discord.</summary>
public sealed class CloudflareDiscordLinkService
{
    private static readonly HttpClient SharedClient =
        CloudflareTransportDefaults.CreateClient(TimeSpan.FromSeconds(20));
    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    public CloudflareDiscordLinkService(Uri accountProfileEndpoint)
        : this(SharedClient, accountProfileEndpoint)
    {
    }

    internal CloudflareDiscordLinkService(HttpClient httpClient, Uri accountProfileEndpoint)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        CloudflareTransportDefaults.ValidateHttpsEndpoint(
            accountProfileEndpoint,
            "Endpoint de conta inválido.");
        endpoint = new Uri(accountProfileEndpoint, "discord/link-code");
    }

    public async Task<DiscordLinkCode?> CreateAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { }),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content
                .ReadFromJsonAsync<LinkCodeResponse>(cancellationToken)
                .ConfigureAwait(false);
            return body is not null
                && body.Code is { Length: 10 }
                && DateTimeOffset.TryParse(body.ExpiresAt, out var expiresAt)
                    ? new DiscordLinkCode(body.Code, expiresAt)
                    : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private sealed record LinkCodeResponse(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("expiresAt")] string? ExpiresAt);
}
