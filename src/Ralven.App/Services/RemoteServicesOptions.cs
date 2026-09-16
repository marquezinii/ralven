using System.IO;
using System.Text.Json;
using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>
/// Centralized, environment-specific configuration for remote reporting
/// services (currently just Sentry). Deliberately holds no defaults that
/// authorize sending anything by itself — <see cref="SentryDsn"/> being
/// present only means a destination exists; whether anything is actually
/// sent still depends entirely on the user's privacy consent
/// (<see cref="PrivacyConsentEvaluator"/>).
/// </summary>
public sealed record RemoteServicesOptions
{
    public string? SentryDsn { get; init; }

    /// <summary>
    /// HTTPS endpoint of the anonymous telemetry Cloudflare Worker. Absent,
    /// empty, or non-HTTPS means telemetry safely sends nothing at all.
    /// </summary>
    public string? TelemetryEndpoint { get; init; }

    /// <summary>
    /// HTTPS endpoint of the bug-report Cloudflare Worker route. Same
    /// fail-safe rule as <see cref="TelemetryEndpoint"/>: absent, empty, or
    /// non-HTTPS means the "Reportar um bug" flow reports a clear failure
    /// instead of silently doing nothing.
    /// </summary>
    public string? BugReportEndpoint { get; init; }

    /// <summary>
    /// HTTPS endpoint of the account profile-completion Cloudflare Worker
    /// route (username, first name, last name — the fields Firebase
    /// Authentication REST does not manage). Same fail-safe rule as
    /// <see cref="TelemetryEndpoint"/>: absent or malformed means profile
    /// completion reports a clear failure instead of silently doing nothing.
    /// </summary>
    public string? AccountProfileEndpoint { get; init; }

    /// <summary>
    /// HTTPS endpoint of the public, unauthenticated live-alert Cloudflare
    /// Worker route (the admin-broadcast banner). Same fail-safe rule as
    /// <see cref="TelemetryEndpoint"/>: absent or malformed means the app
    /// simply never polls for a live alert instead of crashing.
    /// </summary>
    public string? LiveAlertEndpoint { get; init; }

    /// <summary>Public Firebase Web API key. It identifies the project but is not an administrative credential.</summary>
    public string? FirebaseApiKey { get; init; }

    /// <summary>
    /// Enables TOTP enrollment only after Firebase Authentication with
    /// Identity Platform and the matching Worker recovery secrets are live.
    /// The default is fail-closed so an incomplete backend is never offered
    /// as a working security control.
    /// </summary>
    public bool FirebaseTotpEnabled { get; init; }

    /// <summary>
    /// OAuth 2.0 client id of the Google Cloud "Desktop app" credential used
    /// by <see cref="GoogleOAuthClient"/>. Absent means the account window
    /// simply does not offer "Continuar com o Google" — the button is hidden
    /// rather than shown broken.
    /// </summary>
    public string? GoogleOAuthClientId { get; init; }

    /// <summary>
    /// Companion secret Google issues for that same desktop credential.
    /// Despite the name it is not a real secret — it necessarily ships inside
    /// every installed copy of the app, and Google documents it as such. PKCE
    /// is what actually protects the code exchange. Only set it because
    /// Google's token endpoint rejects the exchange without it when the
    /// credential was created with one.
    /// </summary>
    public string? GoogleOAuthClientSecret { get; init; }

    public required string Environment { get; init; }
}

/// <summary>
/// Loads <see cref="RemoteServicesOptions"/> from the environment-specific
/// config file under <c>Config/</c>
/// (<c>appsettings.Development.json</c> / <c>appsettings.Production.json</c>),
/// falling back to the shared <c>appsettings.json</c> baseline — which never
/// carries a DSN — when the environment-specific file is missing or
/// unreadable. No identifier or secret is hardcoded in source; this is the
/// only place that reads these files.
/// </summary>
public static class RemoteServicesOptionsLoader
{
    private const string ConfigDirectoryName = "Config";
    private const string BaseFileName = "appsettings.json";

    public static RemoteServicesOptions Load(AppRuntimeEnvironment environment, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var environmentSpecificPath = Path.Combine(
            baseDirectory,
            ConfigDirectoryName,
            $"appsettings.{environment}.json");
        var basePath = Path.Combine(baseDirectory, ConfigDirectoryName, BaseFileName);
        var path = File.Exists(environmentSpecificPath) ? environmentSpecificPath : basePath;

        var fallback = new RemoteServicesOptions { SentryDsn = null, Environment = environment.ToString() };
        var loaded = File.Exists(path) ? LoadFile(path) : fallback;
        // The environment is selected by the executable, never by a
        // mutable JSON file. Otherwise a stale or edited Production file
        // could make real user events appear as Development in D1.
        var result = loaded is null ? fallback : loaded with { Environment = environment.ToString() };

        return ApplyLocalOverlay(result, Path.Combine(baseDirectory, ConfigDirectoryName, $"appsettings.{environment}.local.json"));
    }

    private static RemoteServicesOptions? LoadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<RemoteServicesOptions>(stream, RalvenJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges a machine-local, git-ignored <c>appsettings.{environment}.local.json</c>
    /// on top of the tracked config, matching the Cloudflare Worker's own
    /// convention for real secrets: <c>ADMIN_PASSWORD_HASH</c> and
    /// <c>IP_HASH_SECRET</c> are "never written to this file or committed"
    /// (see <c>infra/cloudflare-worker/wrangler.toml</c>). GitHub's push
    /// protection rejects a commit containing an OAuth client secret
    /// regardless of how sensitive that particular value actually is, so
    /// anything that looks like one belongs only on the developer's machine.
    /// Absent or unreadable overlay files leave the tracked config untouched.
    /// </summary>
    private static RemoteServicesOptions ApplyLocalOverlay(RemoteServicesOptions options, string overlayPath)
    {
        if (!File.Exists(overlayPath))
        {
            return options;
        }

        RemoteServicesOverlay? overlay;
        try
        {
            using var stream = File.OpenRead(overlayPath);
            overlay = JsonSerializer.Deserialize<RemoteServicesOverlay>(stream, RalvenJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            return options;
        }

        return overlay is null
            ? options
            : options with
            {
                SentryDsn = overlay.SentryDsn ?? options.SentryDsn,
                TelemetryEndpoint = overlay.TelemetryEndpoint ?? options.TelemetryEndpoint,
                BugReportEndpoint = overlay.BugReportEndpoint ?? options.BugReportEndpoint,
                AccountProfileEndpoint = overlay.AccountProfileEndpoint ?? options.AccountProfileEndpoint,
                LiveAlertEndpoint = overlay.LiveAlertEndpoint ?? options.LiveAlertEndpoint,
                FirebaseApiKey = overlay.FirebaseApiKey ?? options.FirebaseApiKey,
                FirebaseTotpEnabled = overlay.FirebaseTotpEnabled ?? options.FirebaseTotpEnabled,
                GoogleOAuthClientId = overlay.GoogleOAuthClientId ?? options.GoogleOAuthClientId,
                GoogleOAuthClientSecret = overlay.GoogleOAuthClientSecret ?? options.GoogleOAuthClientSecret,
            };
    }

    /// <summary>
    /// Same fields as <see cref="RemoteServicesOptions"/>, all optional and
    /// without a required <see cref="RemoteServicesOptions.Environment"/> —
    /// a local overlay file only ever sets the handful of values a developer
    /// needs to override, never the environment itself.
    /// </summary>
    private sealed record RemoteServicesOverlay
    {
        public string? SentryDsn { get; init; }
        public string? TelemetryEndpoint { get; init; }
        public string? BugReportEndpoint { get; init; }
        public string? AccountProfileEndpoint { get; init; }
        public string? LiveAlertEndpoint { get; init; }
        public string? FirebaseApiKey { get; init; }
        public bool? FirebaseTotpEnabled { get; init; }
        public string? GoogleOAuthClientId { get; init; }
        public string? GoogleOAuthClientSecret { get; init; }
    }
}

/// <summary>
/// Production-only guard for the anonymous telemetry destination. This is
/// deliberately stricter than generic HTTPS validation: the app must never
/// silently start reporting to an arbitrary host because a local config file
/// was stale, missing, or edited. Development remains HTTPS-only so test
/// traffic receives the same transport security guarantees.
/// </summary>
public static class TelemetryEndpointPolicy
{
    public const string ProductionHost = "api.vemryx.com";
    public const string TelemetryPath = "/telemetry";

    public static bool TryCreate(
        string? configuredValue,
        AppRuntimeEnvironment runtimeEnvironment,
        out Uri endpoint,
        out string? error)
    {
        endpoint = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(configuredValue)
            || !Uri.TryCreate(configuredValue, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment))
        {
            error = "O endpoint de telemetria não é uma URL HTTPS absoluta válida.";
            return false;
        }

        if (!string.Equals(candidate.AbsolutePath, TelemetryPath, StringComparison.Ordinal))
        {
            error = "O endpoint de telemetria não usa a rota esperada.";
            return false;
        }

        if (runtimeEnvironment == AppRuntimeEnvironment.Production
            && !string.Equals(candidate.Host, ProductionHost, StringComparison.OrdinalIgnoreCase))
        {
            error = "O endpoint de telemetria de produção não usa o host autorizado.";
            return false;
        }

        endpoint = candidate;
        return true;
    }
}

/// <summary>
/// Production allowlist for the Worker route that receives Firebase ID
/// tokens. Development may use another HTTPS origin, but a mutable local
/// production overlay must never redirect account credentials off-domain.
/// </summary>
public static class AccountProfileEndpointPolicy
{
    public const string ProfilePath = "/account/profile";

    public static bool TryCreate(
        string? configuredValue,
        AppRuntimeEnvironment runtimeEnvironment,
        out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(configuredValue)
            || !Uri.TryCreate(configuredValue, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.Equals(candidate.AbsolutePath, ProfilePath, StringComparison.Ordinal)
            || runtimeEnvironment == AppRuntimeEnvironment.Production
                && !string.Equals(
                    candidate.Host,
                    TelemetryEndpointPolicy.ProductionHost,
                    StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        endpoint = candidate;
        return true;
    }
}

public static class FirebaseAuthConfiguration
{
    public static bool TryGetApiKey(string? configuredValue, out string apiKey)
    {
        apiKey = configuredValue?.Trim() ?? string.Empty;
        return apiKey.Length >= 20 && apiKey.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
    }
}
