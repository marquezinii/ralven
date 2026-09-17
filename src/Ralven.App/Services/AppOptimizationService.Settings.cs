using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>
/// Persistência das preferências locais do aplicativo: leitura tolerante a
/// deriva de schema e gravação atômica de <c>settings.json</c>.
/// </summary>
public sealed partial class AppOptimizationService
{
    /// <summary>
    /// Read settings leniently. Writing stays strict
    /// (<see cref="indentedJson"/>), but a settings.json that drifted from the
    /// current schema must never wipe the user's stored preferences: a file
    /// written by a newer build (unknown members), hand-edited (comments,
    /// differently-cased keys) or edited by another tool used to throw a
    /// <see cref="JsonException"/> under the strict options, the catch in
    /// <see cref="LoadSettingsAsync"/> then returned a fresh
    /// <see cref="AppSettings"/>, silently re-arming the privacy consent
    /// screen and flipping the declined telemetry toggles back to their
    /// defaults. Unknown members are skipped, keys match case-insensitively
    /// and comments are tolerated; only genuinely unparseable content still
    /// falls through to the defaults.
    /// </summary>
    private static readonly JsonSerializerOptions SettingsReadOptions = new(RalvenJson.Options)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    /// <summary>
    /// Pure settings deserialization, exposed for tests so the "schema drift
    /// must not reset stored preferences" contract can be locked down without
    /// touching the real LocalApplicationData path.
    /// </summary>
    internal static AppSettings DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, SettingsReadOptions) ?? new AppSettings();

    public bool SettingsFileExists() => !demoMode && File.Exists(settingsPath);

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (demoMode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AppSettings();
        }

        if (!File.Exists(settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken)
                .ConfigureAwait(false);
            return DeserializeSettings(json);
        }
        catch (Exception exception) when (exception is JsonException
            or NotSupportedException
            or IOException)
        {
            // A leitura tolerante cobre arquivos fora do schema atual; este
            // caminho só é atingido por conteúdo genuinamente ilegível
            // (JSON truncado/corrompido), em que não há valores a preservar.
            return new AppSettings();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (demoMode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await AtomicFile.WriteJsonAsync(settingsPath, settings, indentedJson, cancellationToken)
            .ConfigureAwait(false);
    }
}
