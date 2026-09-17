using System.Diagnostics;
using System.Globalization;

namespace Ralven.Windows.Infrastructure;

public sealed record GtaVBenchmarkIterationResult(
    double AverageFps,
    double MinimumFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double AverageFrametimeMs,
    double PeakFrametimeMs,
    int SampleCount);

public sealed record GtaVBenchmarkResult(
    bool Succeeded,
    string? FailureReason,
    IReadOnlyList<GtaVBenchmarkIterationResult> Iterations,
    GtaVBenchmarkIterationResult? Median);

/// <summary>
/// Launches the official, Rockstar-documented GTA V standalone benchmark
/// (<c>-benchmark -benchmarkFrameTimes</c>) and reads back whatever frame
/// time file the game itself writes. This never runs inside a FiveM
/// session, never injects code and never reads process memory — it only
/// starts a normal process with a public command-line flag and reads a file
/// the game wrote to its own profile folder.
///
/// The exact output file name/location is not officially documented in a
/// stable way across game versions, so this searches for a plausible
/// candidate created after launch and parses it defensively: if nothing
/// recognizable is found, it reports failure honestly instead of guessing a
/// result.
/// </summary>
public interface IGtaVBenchmarkRunner
{
    Task<GtaVBenchmarkResult> RunAsync(
        string gtaVExecutablePath,
        int iterations,
        TimeSpan perRunTimeout,
        CancellationToken cancellationToken);
}

public sealed class WindowsGtaVBenchmarkRunner : IGtaVBenchmarkRunner
{
    public async Task<GtaVBenchmarkResult> RunAsync(
        string gtaVExecutablePath,
        int iterations,
        TimeSpan perRunTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gtaVExecutablePath);
        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        if (!File.Exists(gtaVExecutablePath))
        {
            return new GtaVBenchmarkResult(false, "gta-executable-not-found", [], null);
        }

        var searchRoot = ResolveBenchmarkOutputSearchRoot();
        if (searchRoot is null || !Directory.Exists(searchRoot))
        {
            return new GtaVBenchmarkResult(false, "profile-folder-not-found", [], null);
        }

        var iterationResults = new List<GtaVBenchmarkIterationResult>();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var launchedAtUtc = DateTime.UtcNow;

            var exitedCleanly = await RunSingleLaunchAsync(
                gtaVExecutablePath, perRunTimeout, cancellationToken).ConfigureAwait(false);
            if (!exitedCleanly)
            {
                return new GtaVBenchmarkResult(
                    false, "benchmark-did-not-exit-in-time", iterationResults, null);
            }

            var outputFile = FindLatestBenchmarkFile(searchRoot, launchedAtUtc);
            if (outputFile is null)
            {
                return new GtaVBenchmarkResult(
                    false, "benchmark-output-file-not-found", iterationResults, null);
            }

            var parsed = TryParseFrametimeFile(outputFile);
            if (parsed is null)
            {
                return new GtaVBenchmarkResult(
                    false, "benchmark-output-file-not-recognized", iterationResults, null);
            }

            iterationResults.Add(parsed);
        }

        return new GtaVBenchmarkResult(
            true, null, iterationResults, ComputeMedian(iterationResults));
    }

    private static async Task<bool> RunSingleLaunchAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "-benchmark -benchmarkFrameTimes",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
            }
        };

        if (!process.Start())
        {
            return false;
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ProcessTermination.TryKill(process);
            return false;
        }
        catch (OperationCanceledException)
        {
            ProcessTermination.TryKill(process);
            throw;
        }
    }

    private static string? ResolveBenchmarkOutputSearchRoot()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                return null;
            }

            return Path.Combine(documents, "Rockstar Games", "GTA V");
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string? FindLatestBenchmarkFile(string searchRoot, DateTime launchedAtUtc)
    {
        try
        {
            return Directory.EnumerateFiles(searchRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.Contains("bench", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("frametime", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("frame_time", StringComparison.OrdinalIgnoreCase);
                })
                .Where(path => File.GetLastWriteTimeUtc(path) >= launchedAtUtc.AddSeconds(-2))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static GtaVBenchmarkIterationResult? TryParseFrametimeFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (lines.Length < 2)
        {
            return null;
        }

        var delimiter = lines[0].Contains(';') ? ';' : ',';
        var header = lines[0].Split(delimiter).Select(column => column.Trim()).ToArray();
        var frametimeColumn = Array.FindIndex(header, column =>
            column.Contains("frametime", StringComparison.OrdinalIgnoreCase)
            || column.Contains("frame time", StringComparison.OrdinalIgnoreCase)
            || (column.Contains("time", StringComparison.OrdinalIgnoreCase)
                && column.Contains("ms", StringComparison.OrdinalIgnoreCase)));
        var fpsColumn = Array.FindIndex(header, column =>
            column.Contains("fps", StringComparison.OrdinalIgnoreCase));

        var frametimesMs = new List<double>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            var cells = lines[lineIndex].Split(delimiter);
            if (frametimeColumn >= 0 && frametimeColumn < cells.Length
                && double.TryParse(
                    cells[frametimeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                && ms is > 0 and < 5000)
            {
                frametimesMs.Add(ms);
            }
            else if (fpsColumn >= 0 && fpsColumn < cells.Length
                && double.TryParse(
                    cells[fpsColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)
                && fps is > 0 and < 2000)
            {
                frametimesMs.Add(1000d / fps);
            }
        }

        if (frametimesMs.Count < 10)
        {
            return null;
        }

        return BuildIterationResult(frametimesMs);
    }

    internal static GtaVBenchmarkIterationResult BuildIterationResult(IReadOnlyList<double> frametimesMs)
    {
        var sortedByFrametimeDescending = frametimesMs.OrderByDescending(ms => ms).ToArray();
        var fpsValues = frametimesMs.Select(ms => 1000d / ms).ToArray();

        var onePercentCount = Math.Max(1, sortedByFrametimeDescending.Length / 100);
        var pointOnePercentCount = Math.Max(1, sortedByFrametimeDescending.Length / 1000);

        var onePercentLowFps = 1000d / sortedByFrametimeDescending.Take(onePercentCount).Average();
        var pointOnePercentLowFps = 1000d / sortedByFrametimeDescending.Take(pointOnePercentCount).Average();

        return new GtaVBenchmarkIterationResult(
            AverageFps: fpsValues.Average(),
            MinimumFps: fpsValues.Min(),
            OnePercentLowFps: onePercentLowFps,
            PointOnePercentLowFps: pointOnePercentLowFps,
            AverageFrametimeMs: frametimesMs.Average(),
            PeakFrametimeMs: frametimesMs.Max(),
            SampleCount: frametimesMs.Count);
    }

    internal static GtaVBenchmarkIterationResult? ComputeMedian(
        IReadOnlyList<GtaVBenchmarkIterationResult> iterations)
    {
        if (iterations.Count == 0)
        {
            return null;
        }

        if (iterations.Count == 1)
        {
            return iterations[0];
        }

        // Median by average FPS keeps the whole matching row together instead
        // of independently medianing each metric, which would mix numbers
        // from different runs.
        var ordered = iterations.OrderBy(result => result.AverageFps).ToArray();
        return ordered[ordered.Length / 2];
    }
}
