using System.IO;
using System.Text.Json;

namespace Ralven.App.Services;

/// <summary>
/// Grava um arquivo de estado de forma atômica: o conteúdo é escrito em um
/// arquivo temporário irmão e só então movido para o destino, de modo que uma
/// falha no meio da escrita (disco cheio, processo encerrado) nunca substitua
/// um arquivo válido por um arquivo parcial.
/// </summary>
/// <remarks>
/// Usado pelos stores que possuem um único arquivo mutável próprio. Fluxos que
/// precisam recusar a substituição de um destino existente (fila do broker,
/// importador legado) continuam com o <c>File.Move</c> próprio, porque a
/// semântica ali é <c>overwrite: false</c> e a colisão é significativa.
/// </remarks>
internal static class AtomicFile
{
    /// <summary>Grava <paramref name="bytes"/> em <paramref name="path"/>.</summary>
    /// <param name="validateDestination">
    /// Executado no destino imediatamente antes da substituição, para chamadores
    /// que precisam revalidar o caminho (por exemplo, reparse points) depois que
    /// o conteúdo já está preparado.
    /// </param>
    public static Task WriteBytesAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken,
        Action<string>? validateDestination = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return WriteAsync(
            path,
            async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
            cancellationToken,
            validateDestination);
    }

    /// <summary>Serializa <paramref name="value"/> como JSON em <paramref name="path"/>.</summary>
    /// <param name="validateDestination">Ver <see cref="WriteBytesAsync"/>.</param>
    public static Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken,
        Action<string>? validateDestination = null)
        => WriteAsync(
            path,
            async (stream, token) =>
                await JsonSerializer.SerializeAsync(stream, value, options, token).ConfigureAwait(false),
            cancellationToken,
            validateDestination);

    private static async Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken,
        Action<string>? validateDestination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"'{path}' não possui diretório de destino.", nameof(path));
        Directory.CreateDirectory(directory);
        validateDestination?.Invoke(directory);

        // Nome único por gravação: dois salvamentos simultâneos do mesmo store
        // não podem disputar o mesmo arquivo temporário.
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writeContent(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            validateDestination?.Invoke(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // Best-effort: depois que o Move conclui o conteúdo já está durável,
            // então uma limpeza malsucedida nunca pode ser reportada como falha
            // de gravação nem mascarar a exceção original.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException)
            {
            }
        }
    }
}
