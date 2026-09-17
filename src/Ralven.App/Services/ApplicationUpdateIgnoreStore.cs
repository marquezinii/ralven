using System.IO;
using System.Text.Json;
using Ralven.Contracts;
using Ralven.Windows.Infrastructure;

namespace Ralven.App.Services;

internal interface IApplicationUpdateIgnoreStore
{
    Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyCollection<string> packageKeys,
        CancellationToken cancellationToken = default);
}

internal sealed class JsonApplicationUpdateIgnoreStore : IApplicationUpdateIgnoreStore
{
    private const int MaximumStoredPackages = 1024;
    private const long MaximumFileSizeBytes = 1024 * 1024;
    private readonly string path;

    public JsonApplicationUpdateIgnoreStore()
        : this(AppDataPaths.Combine("application-update-ignores.json"))
    {
    }

    internal JsonApplicationUpdateIgnoreStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The ignore store path must be absolute.", nameof(path));
        }

        this.path = SafePath.EnsureNoReparsePoints(path);
    }

    public async Task<IReadOnlySet<string>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException("The application update ignore store is too large.");
        }

        var values = await JsonSerializer.DeserializeAsync<string[]>(
            stream,
            RalvenJson.Options,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The application update ignore store is invalid.");
        if (values.Length > MaximumStoredPackages)
        {
            throw new InvalidDataException("The application update ignore store contains too many entries.");
        }

        return values
            .Where(IsValidKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveAsync(
        IReadOnlyCollection<string> packageKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageKeys);
        if (packageKeys.Count > MaximumStoredPackages
            || packageKeys.Any(key => !IsValidKey(key)))
        {
            throw new ArgumentException("The ignore store contains an invalid package key.", nameof(packageKeys));
        }

        if (Path.GetDirectoryName(path) is null)
        {
            throw new InvalidOperationException("The ignore store has no parent directory.");
        }

        await AtomicFile.WriteJsonAsync(
            path,
            packageKeys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            RalvenJson.Options,
            cancellationToken,
            validateDestination: target => SafePath.EnsureNoReparsePoints(target)).ConfigureAwait(false);
    }

    private static bool IsValidKey(string? value) => value is { Length: > 2 and <= 600 }
        && (value.StartsWith("winget|", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("msstore|", StringComparison.OrdinalIgnoreCase))
        && !value.Any(char.IsControl);
}

internal sealed class InMemoryApplicationUpdateIgnoreStore : IApplicationUpdateIgnoreStore
{
    private HashSet<string> values = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlySet<string>>(
            new HashSet<string>(values, StringComparer.OrdinalIgnoreCase));
    }

    public Task SaveAsync(
        IReadOnlyCollection<string> packageKeys,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        values = new HashSet<string>(packageKeys, StringComparer.OrdinalIgnoreCase);
        return Task.CompletedTask;
    }
}
