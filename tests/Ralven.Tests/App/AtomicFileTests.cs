using System.Text;
using System.Text.Json;
using Ralven.App.Services;
using Xunit;

namespace Ralven.Tests.App;

/// <summary>
/// <see cref="AtomicFile"/> concentra a gravação de todo store que possui um
/// único arquivo mutável (preferências, fila de telemetria, sessão persistida,
/// workspace pessoal). As garantias verificadas aqui — substituição completa,
/// destino preservado quando a escrita falha e ausência de arquivo temporário
/// remanescente — são as mesmas que cada store implementava por conta própria.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "RalvenAtomicFileTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza best-effort: um diretório temporário remanescente não reprova o teste.
        }
    }

    [Fact]
    public async Task WriteBytesAsync_CreatesMissingDirectoryAndWritesContent()
    {
        var path = Path.Combine(directory, "nested", "state.txt");

        await AtomicFile.WriteBytesAsync(path, Encoding.UTF8.GetBytes("conteúdo com acento"), CancellationToken.None);

        Assert.Equal("conteúdo com acento", await File.ReadAllTextAsync(path, Encoding.UTF8));
    }

    [Fact]
    public async Task WriteJsonAsync_ReplacesExistingContentCompletely()
    {
        var path = Path.Combine(directory, "state.json");
        await AtomicFile.WriteJsonAsync(
            path,
            new Sample("valor-muito-mais-longo-que-o-proximo", 1),
            JsonSerializerOptions.Default,
            CancellationToken.None);

        await AtomicFile.WriteJsonAsync(
            path,
            new Sample("curto", 2),
            JsonSerializerOptions.Default,
            CancellationToken.None);

        var stored = JsonSerializer.Deserialize<Sample>(await File.ReadAllTextAsync(path));
        Assert.Equal(new Sample("curto", 2), stored);
    }

    [Fact]
    public async Task WriteBytesAsync_LeavesNoTemporaryFileBehind()
    {
        var path = Path.Combine(directory, "session.bin");

        await AtomicFile.WriteBytesAsync(path, [1, 2, 3], CancellationToken.None);

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(directory));
    }

    [Fact]
    public async Task WriteAsync_ValidationFailure_PreservesPreviousContentAndCleansUp()
    {
        var path = Path.Combine(directory, "state.txt");
        await AtomicFile.WriteBytesAsync(path, Encoding.UTF8.GetBytes("original"), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => AtomicFile.WriteBytesAsync(
            path,
            Encoding.UTF8.GetBytes("substituição"),
            CancellationToken.None,
            validateDestination: _ => throw new IOException("destino recusado")));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(directory));
    }

    [Fact]
    public async Task WriteAsync_ValidatesDirectoryBeforeWritingAndDestinationBeforeReplacing()
    {
        var path = Path.Combine(directory, "state.txt");
        var validated = new List<string>();

        await AtomicFile.WriteBytesAsync(
            path,
            Encoding.UTF8.GetBytes("conteúdo"),
            CancellationToken.None,
            validateDestination: validated.Add);

        Assert.Equal([directory, path], validated);
    }

    [Fact]
    public async Task WriteAsync_Cancelled_DoesNotReplaceTheDestination()
    {
        var path = Path.Combine(directory, "state.txt");
        await AtomicFile.WriteBytesAsync(path, Encoding.UTF8.GetBytes("original"), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AtomicFile.WriteBytesAsync(path, Encoding.UTF8.GetBytes("substituição"), cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(directory));
    }

    private sealed record Sample(string Name, int Value);
}
