using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Ralven.App.Services;

public interface ILiveAlertService
{
    Task<LiveAlertSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The current admin-broadcast alert, if any. <see cref="Id"/> is the
/// server's opaque version stamp (its <c>updated_at</c>), used only to tell
/// one alert apart from the next -- never parsed as a date.
/// </summary>
public enum LiveAlertSeverity
{
    Info,
    Important,
    Critical
}

public sealed record LiveAlertSnapshot(
    string? Id,
    string Message,
    bool Active,
    LiveAlertSeverity Severity = LiveAlertSeverity.Important);

/// <summary>
/// Polls the Cloudflare Worker's public <c>GET /live-alert</c> route (see
/// docs/superpowers/specs/2026-08-17-live-alerts-design.md). Unauthenticated
/// by design -- every installed app reads the same single broadcast -- and
/// never throws: a network failure or malformed response returns
/// <see langword="null"/> so a transient outage never changes what is
/// currently shown, matching the rest of the app's telemetry/bug-report
/// transports.
/// </summary>
public sealed class CloudflareLiveAlertService : ILiveAlertService
{
    private const int MaxResponseBytes = 4 * 1024;
    private const int MaxMessageLength = 300;

    private static readonly HttpClient SharedClient = CloudflareTransportDefaults.CreateClient(TimeSpan.FromSeconds(15));
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    public CloudflareLiveAlertService(Uri endpoint)
        : this(SharedClient, endpoint)
    {
    }

    internal CloudflareLiveAlertService(HttpClient httpClient, Uri endpoint)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.endpoint = CloudflareTransportDefaults.ValidateHttpsEndpoint(endpoint, "Endpoint de aviso ao vivo inválido.");
    }

    public async Task<LiveAlertSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await CloudflareTransportDefaults.ReadBoundedJsonAsync<LiveAlertPayload>(
                response,
                MaxResponseBytes,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return payload is null ? null : Sanitize(payload);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or JsonException or IOException)
        {
            return null;
        }
    }

    private static LiveAlertSnapshot? Sanitize(LiveAlertPayload payload)
    {
        if (!payload.Active)
        {
            return new LiveAlertSnapshot(null, string.Empty, false);
        }

        if (string.IsNullOrWhiteSpace(payload.Message) || payload.Message.Length > MaxMessageLength)
        {
            return null;
        }

        return new LiveAlertSnapshot(payload.Id, payload.Message.Trim(), true, payload.Severity switch
        {
            "info" => LiveAlertSeverity.Info,
            "critical" => LiveAlertSeverity.Critical,
            _ => LiveAlertSeverity.Important,
        });
    }

    private sealed record LiveAlertPayload(string? Id, string? Message, bool Active, string? Severity);
}
