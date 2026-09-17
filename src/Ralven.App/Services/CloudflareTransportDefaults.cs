using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Ralven.App.Services;

/// <summary>
/// HTTPS-only endpoint validation and a redirect-less, compressed
/// <see cref="HttpClient"/> shared by the Cloudflare Worker transports
/// (telemetry and bug reports) so both enforce the same transport
/// invariants instead of drifting independently.
/// </summary>
internal static class CloudflareTransportDefaults
{
    public static Uri ValidateHttpsEndpoint(Uri value, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(errorMessage, nameof(value));
        }

        return value;
    }

    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // Estes clientes são estáticos e vivem enquanto o aplicativo estiver
            // aberto. Sem reciclar a conexão, o primeiro IP resolvido ficaria
            // fixado por dias, ignorando uma mudança de DNS do backend.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// Desserializa a resposta limitando a leitura a <paramref name="maximumBytes"/>
    /// e devolve <see langword="default"/> quando o corpo excede o limite, é
    /// ilegível ou a conexão falha.
    /// </summary>
    /// <remarks>
    /// Conferir apenas <c>Content-Length</c> não basta: uma resposta com
    /// <c>Transfer-Encoding: chunked</c> não traz o cabeçalho e passaria direto
    /// para a desserialização sem limite algum. O buffer explícito é o que
    /// realmente aplica o teto.
    /// </remarks>
    public static async Task<T?> ReadBoundedJsonAsync<T>(
        HttpResponseMessage response,
        int maximumBytes,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            return default;
        }

        try
        {
            await response.Content.LoadIntoBufferAsync(maximumBytes, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return default;
        }
    }
}
