using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>Persists only a refresh token, encrypted for the current Windows user.</summary>
public sealed class SecureFirebaseSessionStore
{
    private readonly string path;

    public SecureFirebaseSessionStore(string path) => this.path = path;

    internal async Task<PersistedFirebaseSession?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<PersistedFirebaseSession>(json, RalvenJson.Options);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal async Task WriteAsync(string refreshToken, CancellationToken cancellationToken)
    {
        byte[]? plaintext = null;
        byte[]? encrypted = null;
        try
        {
            var json = JsonSerializer.Serialize(new PersistedFirebaseSession(refreshToken), RalvenJson.Options);
            plaintext = Encoding.UTF8.GetBytes(json);
            encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            await AtomicFile.WriteBytesAsync(path, encrypted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Best-effort: losing the persistent "keep me signed in" token
            // degrades to a manual login next launch, never a crash during
            // the signup/sign-in flow that created it.
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    internal Task ClearAsync()
    {
        File.Delete(path);
        if (File.Exists(path))
        {
            throw new IOException("The persisted Firebase session could not be removed.");
        }

        return Task.CompletedTask;
    }
}
