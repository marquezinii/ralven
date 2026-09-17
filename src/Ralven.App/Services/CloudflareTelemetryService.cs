using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>
/// Validates the full (version-2-consent) telemetry event schema before it
/// is queued for the Cloudflare transport.
/// </summary>
public static class TelemetryEventValidator
{
    private static readonly HashSet<int> AllowedRamBucketsGiB = [2, 4, 8, 16, 32, 64, 128, 256];
    private static readonly HashSet<string> AllowedProfiles = new(StringComparer.Ordinal) { "Light", "Balanced", "Aggressive" };
    private static readonly HashSet<string> AllowedGtaEditions = new(StringComparer.Ordinal) { "Legacy", "Enhanced", "Unknown" };
    private static readonly HashSet<string> AllowedDiskTypes = new(StringComparer.Ordinal) { "HDD", "SSD", "NVMe", "Unknown" };
    private static readonly HashSet<int> AllowedFreeSpaceGiBBuckets = [0, 10, 50, 100, 250];
    private static readonly HashSet<int> AllowedDaysSinceLastRunBuckets = [0, 2, 8, 30];
    private static readonly HashSet<int> AllowedProcessCountBuckets = [0, 1, 4];
    public const int MaxActionIds = 30;
    private const int MaxShortFieldLength = 128;

    public static void Validate(AnonymousTelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        if (telemetryEvent.EventId == Guid.Empty)
        {
            throw new ArgumentException("Identificador de evento de telemetria inválido.", nameof(telemetryEvent));
        }

        if (telemetryEvent.EventName is not (
            TelemetryEventNames.AppInitialized or
            TelemetryEventNames.OptimizationStarted or
            TelemetryEventNames.OptimizationCompleted or
            TelemetryEventNames.OptimizationFailed or
            TelemetryEventNames.OptimizationCancelled or
            TelemetryEventNames.GtaVBenchmarkCompleted or
            TelemetryEventNames.GtaVBenchmarkFailed))
        {
            throw new ArgumentException("Evento de telemetria não permitido.", nameof(telemetryEvent));
        }

        if (string.IsNullOrWhiteSpace(telemetryEvent.AppVersion)
            || telemetryEvent.AppVersion.Length > 32
            || telemetryEvent.AppVersion.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException("Versão de telemetria inválida.", nameof(telemetryEvent));
        }

        if (telemetryEvent.ErrorCategory is not null
            && telemetryEvent.ErrorCategory is not ("cancelled" or "timeout" or "access-denied" or "io" or "invalid-data" or "unexpected"))
        {
            throw new ArgumentException("Categoria de erro de telemetria não permitida.", nameof(telemetryEvent));
        }

        if (telemetryEvent.BugCode is BugCode.Unknown
            || telemetryEvent.BugCode is { } bugCode && !Enum.IsDefined(bugCode))
        {
            throw new ArgumentException("Código de bug de telemetria não permitido.", nameof(telemetryEvent));
        }

        ValidateShortField(telemetryEvent.OsVersion, nameof(telemetryEvent.OsVersion));
        ValidateShortField(telemetryEvent.SystemArchitecture, nameof(telemetryEvent.SystemArchitecture));
        ValidateShortField(telemetryEvent.CpuModel, nameof(telemetryEvent.CpuModel));
        ValidateShortField(telemetryEvent.GpuModel, nameof(telemetryEvent.GpuModel));

        if (telemetryEvent.RamBucketGiB is { } ramBucket && !AllowedRamBucketsGiB.Contains(ramBucket))
        {
            throw new ArgumentException("Faixa de RAM de telemetria não permitida.", nameof(telemetryEvent));
        }

        if (telemetryEvent.Profile is not null && !AllowedProfiles.Contains(telemetryEvent.Profile))
        {
            throw new ArgumentException("Perfil de telemetria não permitido.", nameof(telemetryEvent));
        }

        if (telemetryEvent.ActionIds is { } actionIds)
        {
            if (actionIds.Count > MaxActionIds)
            {
                throw new ArgumentException("Número de ações de telemetria excede o limite.", nameof(telemetryEvent));
            }

            foreach (var actionId in actionIds)
            {
                if (string.IsNullOrWhiteSpace(actionId)
                    || actionId.Length > MaxShortFieldLength
                    || actionId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
                {
                    throw new ArgumentException("Identificador de ação de telemetria inválido.", nameof(telemetryEvent));
                }
            }
        }

        // v5: diagnósticos essenciais expandidos
        if (telemetryEvent.GtaEdition is not null && !AllowedGtaEditions.Contains(telemetryEvent.GtaEdition))
        {
            throw new ArgumentException("Edição do GTA V de telemetria não permitida.", nameof(telemetryEvent));
        }

        if (telemetryEvent.OptimizationTargetCount is { } targetCount && (targetCount < 0 || targetCount > 100_000))
        {
            throw new ArgumentException("Contagem de alvos de otimização fora do intervalo permitido.", nameof(telemetryEvent));
        }

        // v5: dados opcionais de contexto
        if (telemetryEvent.WindowsBuild is { } windowsBuild && (windowsBuild < 0 || windowsBuild > 99999))
        {
            throw new ArgumentException("Build do Windows de telemetria fora do intervalo permitido.", nameof(telemetryEvent));
        }

        if (telemetryEvent.DiskType is not null && !AllowedDiskTypes.Contains(telemetryEvent.DiskType))
        {
            throw new ArgumentException("Tipo de disco de telemetria não permitido.", nameof(telemetryEvent));
        }

        if (telemetryEvent.FreeSpaceGiBBucket is { } freeSpaceBucket && !AllowedFreeSpaceGiBBuckets.Contains(freeSpaceBucket))
        {
            throw new ArgumentException("Faixa de espaço livre de telemetria não permitida.", nameof(telemetryEvent));
        }

        if (telemetryEvent.DaysSinceLastRunBucket is { } daysBucket && !AllowedDaysSinceLastRunBuckets.Contains(daysBucket))
        {
            throw new ArgumentException("Faixa de dias desde a última execução não permitida.", nameof(telemetryEvent));
        }

        if (telemetryEvent.ProcessCountAtStart is { } processCount && !AllowedProcessCountBuckets.Contains(processCount))
        {
            throw new ArgumentException("Faixa de contagem de processos não permitida.", nameof(telemetryEvent));
        }

        if (telemetryEvent.OperationId == Guid.Empty)
        {
            throw new ArgumentException("Identificador de operação de telemetria inválido.", nameof(telemetryEvent));
        }
    }

    private static void ValidateShortField(string? value, string fieldName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > MaxShortFieldLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"Campo de telemetria '{fieldName}' inválido.", fieldName);
        }
    }
}

/// <summary>
/// Persists queued telemetry events as small JSON files under
/// <c>%LOCALAPPDATA%\Ralven\Telemetry\pending</c>, following the same
/// atomic-write and best-effort-cleanup discipline already used for
/// journals and quarantine. Gives the telemetry pipeline resilience across
/// restarts and offline periods without needing a database or a long-lived
/// background timer.
/// </summary>
public sealed class LocalTelemetryQueue
{
    private static long lastQueuedUtcTicks;
    private readonly string queueDirectory;
    private readonly JsonSerializerOptions jsonOptions;

    public string QueueDirectory => queueDirectory;

    public LocalTelemetryQueue(string queueDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueDirectory);
        this.queueDirectory = queueDirectory;
        jsonOptions = RalvenJson.Options;
    }

    public async Task EnqueueAsync(AnonymousTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        Directory.CreateDirectory(queueDirectory);

        var enqueuedAt = new DateTimeOffset(NextQueuedUtcTicks(), TimeSpan.Zero);
        var fileName = $"{enqueuedAt:yyyyMMddHHmmssfffffff}_{Guid.NewGuid():N}.json";
        var finalPath = Path.Combine(queueDirectory, fileName);

        await AtomicFile.WriteJsonAsync(finalPath, telemetryEvent, jsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static long NextQueuedUtcTicks()
    {
        while (true)
        {
            var previous = Volatile.Read(ref lastQueuedUtcTicks);
            var next = Math.Max(DateTime.UtcNow.Ticks, previous + 1);
            if (Interlocked.CompareExchange(ref lastQueuedUtcTicks, next, previous) == previous)
            {
                return next;
            }
        }
    }

    /// <summary>
    /// Reads up to <paramref name="maxCount"/> pending events, oldest first
    /// (filenames are timestamp-prefixed so ordinal sort is chronological).
    /// A file that fails to parse (corrupted by a crash mid-write, for
    /// example) is dropped rather than blocking the queue forever.
    /// </summary>
    public IReadOnlyList<QueuedTelemetryFile> ReadPending(int maxCount)
    {
        if (!Directory.Exists(queueDirectory))
        {
            return [];
        }

        var result = new List<QueuedTelemetryFile>();
        foreach (var filePath in Directory.EnumerateFiles(queueDirectory, "*.json")
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(maxCount))
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                var telemetryEvent = JsonSerializer.Deserialize<AnonymousTelemetryEvent>(stream, jsonOptions);
                if (telemetryEvent is null)
                {
                    TryDelete(filePath);
                    continue;
                }

                result.Add(new QueuedTelemetryFile(filePath, telemetryEvent));
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                TryDelete(filePath);
            }
        }

        return result;
    }

    public void Remove(string filePath) => TryDelete(filePath);

    /// <summary>
    /// Reserves one low-volume health event per UTC day and app version. The
    /// marker has no user or machine identifier; it only prevents repeated
    /// launches from turning a health signal into a usage trace.
    /// </summary>
    public bool TryReserveDailyEvent(string eventName, string appVersion, DateOnly utcDate)
    {
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(appVersion)
            || eventName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-'))
            || appVersion.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(queueDirectory);
            var markerPath = Path.Combine(queueDirectory, $".{eventName}-{appVersion}-{utcDate:yyyyMMdd}.daily");
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bounds the queue by age *and* by count. Age alone is not a real bound:
    /// each run enqueues events but a flush only drains one batch, so while
    /// sending keeps failing the queue grows for the whole retention window
    /// with nothing to stop it. When the count ceiling is exceeded the oldest
    /// events are dropped first — they are the least useful and the most
    /// likely to already be stale.
    /// </summary>
    public void Prune(TimeSpan maxAge, int maxFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);
        if (!Directory.Exists(queueDirectory))
        {
            return;
        }

        var cutoffUtc = DateTimeOffset.UtcNow - maxAge;
        var survivors = new List<string>();
        foreach (var filePath in Directory.EnumerateFiles(queueDirectory, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (File.GetCreationTimeUtc(filePath) < cutoffUtc)
            {
                TryDelete(filePath);
                continue;
            }

            survivors.Add(filePath);
        }

        // Filenames are timestamp-prefixed, so ordinal order is chronological.
        for (var index = 0; index < survivors.Count - maxFiles; index++)
        {
            TryDelete(survivors[index]);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a locked, read-only or missing file is not
            // fatal. UnauthorizedAccessException is not an IOException, so
            // leaving it uncaught let a single undeletable file escape out of
            // Remove() after a *successful* send — which resurfaced the same
            // event on the next flush, forever.
        }
    }
}

public sealed record QueuedTelemetryFile(string FilePath, AnonymousTelemetryEvent Event);

internal enum TelemetrySendOutcome
{
    Accepted,
    TransientFailure,
    PermanentlyRejected
}

/// <summary>
/// Sends a batch of telemetry events to the anonymous telemetry Cloudflare
/// Worker as a single HTTPS request. Never throws to the caller: any
/// network failure is reported as <see langword="false"/> so the queue
/// keeps the events for the next attempt.
/// </summary>
public sealed class CloudflareTelemetryTransport
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    private readonly string environment;

    public CloudflareTelemetryTransport(Uri endpoint, string environment)
        : this(SharedClient, endpoint, environment)
    {
    }

    internal CloudflareTelemetryTransport(HttpClient httpClient, Uri endpoint, string environment = "Production")
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.endpoint = ValidateEndpoint(endpoint);
        this.environment = ValidateEnvironment(environment);
    }

    public async Task<bool> SendBatchAsync(
        IReadOnlyList<AnonymousTelemetryEvent> events,
        CancellationToken cancellationToken = default)
        => await SendBatchWithOutcomeAsync(events, cancellationToken).ConfigureAwait(false) == TelemetrySendOutcome.Accepted;

    internal async Task<TelemetrySendOutcome> SendBatchWithOutcomeAsync(
        IReadOnlyList<AnonymousTelemetryEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return TelemetrySendOutcome.Accepted;
        }

        try
        {
            var payload = events.Select(eventItem => ToWirePayload(eventItem, environment)).ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return TelemetrySendOutcome.Accepted;
            }

            // A malformed/unauthorized payload will not become valid by being
            // retried forever. 429 and 5xx remain transient because the same
            // queue item may succeed after the service recovers.
            return (int)response.StatusCode is >= 400 and < 500
                && response.StatusCode != HttpStatusCode.TooManyRequests
                ? TelemetrySendOutcome.PermanentlyRejected
                : TelemetrySendOutcome.TransientFailure;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return TelemetrySendOutcome.TransientFailure;
        }
    }

    private static object ToWirePayload(AnonymousTelemetryEvent telemetryEvent, string environment) => new
    {
        eventId = telemetryEvent.EventId.ToString("D"),
        eventName = telemetryEvent.EventName,
        executionTimeMs = (long)Math.Clamp(telemetryEvent.ExecutionTime.TotalMilliseconds, 0, 86_400_000),
        appVersion = telemetryEvent.AppVersion,
        errorCategory = telemetryEvent.ErrorCategory,
        bugCode = telemetryEvent.BugCode?.ToString(),
        osVersion = telemetryEvent.OsVersion,
        systemArchitecture = telemetryEvent.SystemArchitecture,
        cpuModel = telemetryEvent.CpuModel,
        gpuModel = telemetryEvent.GpuModel,
        ramBucketGiB = telemetryEvent.RamBucketGiB,
        profile = telemetryEvent.Profile,
        actionIds = telemetryEvent.ActionIds,
        fiveMInstallDetected = telemetryEvent.FiveMInstallDetected,
        gtaEdition = telemetryEvent.GtaEdition,
        optimizationTargetCount = telemetryEvent.OptimizationTargetCount,
        windowsBuild = telemetryEvent.WindowsBuild,
        diskType = telemetryEvent.DiskType,
        freeSpaceGiBBucket = telemetryEvent.FreeSpaceGiBBucket,
        runTimestamp = telemetryEvent.RunTimestamp?.ToString("O"),
        daysSinceLastRunBucket = telemetryEvent.DaysSinceLastRunBucket,
        backupCreated = telemetryEvent.BackupCreated,
        backupRestored = telemetryEvent.BackupRestored,
        elevationUsed = telemetryEvent.ElevationUsed,
        processCountAtStart = telemetryEvent.ProcessCountAtStart,
        operationId = telemetryEvent.OperationId?.ToString("D"),
        environment
    };

    private static Uri ValidateEndpoint(Uri value) =>
        CloudflareTransportDefaults.ValidateHttpsEndpoint(value, "Endpoint de telemetria inválido.");

    private static string ValidateEnvironment(string value)
    {
        if (value is not ("Development" or "Production"))
        {
            throw new ArgumentException("Ambiente de telemetria inválido.", nameof(value));
        }

        return value;
    }

    private static HttpClient CreateClient() => CloudflareTransportDefaults.CreateClient(TimeSpan.FromSeconds(15));
}

/// <summary>
/// The Cloudflare-backed <see cref="IAnonymousTelemetryService"/>: enqueues
/// locally, then makes a best-effort attempt to flush the whole pending
/// queue as one batch.
/// </summary>
public sealed class QueuedCloudflareTelemetryService : IAnonymousTelemetryService
{
    // D1 executes this whole request as one transaction. 16 events with at
    // most 30 actions each require 496 statements, below its 500 limit.
    private const int MaxBatchSize = 16;
    private const int MaxQueuedEvents = 200;
    private static readonly TimeSpan MaxQueueAge = TimeSpan.FromDays(14);

    private readonly LocalTelemetryQueue queue;
    private readonly CloudflareTelemetryTransport transport;
    private readonly SemaphoreSlim flushLock = new(1, 1);
    private readonly string logDirectory;
    private volatile bool enabled;
    private volatile bool includeOptionalData;
    private long successfulSends;
    private long failedSends;

    public QueuedCloudflareTelemetryService(LocalTelemetryQueue queue, CloudflareTelemetryTransport transport)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        logDirectory = queue.QueueDirectory;
    }

    public bool IsEnabled => enabled;

    public bool IncludesOptionalData => includeOptionalData;

    public void Configure(bool enabled, bool includeOptionalData)
    {
        this.includeOptionalData = includeOptionalData;
        this.enabled = enabled;
    }

    public long SuccessfulSends => Interlocked.Read(ref successfulSends);
    public long FailedSends => Interlocked.Read(ref failedSends);
    public bool IsHealthy => FailedSends == 0 || SuccessfulSends > FailedSends;

    public async Task TrackAsync(AnonymousTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        if (!enabled)
        {
            return;
        }

        telemetryEvent = includeOptionalData ? telemetryEvent : telemetryEvent.WithoutOptionalData();
        TelemetryEventValidator.Validate(telemetryEvent);
        await queue.EnqueueAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);

        // Best-effort, fire-and-forget: a failed flush leaves the event
        // queued for the next opportunity (the next TrackAsync call or the
        // next app startup) without delaying or affecting the optimization
        // that just completed.
        FlushPendingAsync(cancellationToken).Forget();
    }

    /// <summary>Queues a health event only once per UTC day/version.</summary>
    public Task TrackOncePerUtcDayAsync(
        AnonymousTelemetryEvent telemetryEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        if (!enabled)
        {
            return Task.CompletedTask;
        }

        telemetryEvent = includeOptionalData ? telemetryEvent : telemetryEvent.WithoutOptionalData();
        TelemetryEventValidator.Validate(telemetryEvent);
        if (!queue.TryReserveDailyEvent(
                telemetryEvent.EventName,
                telemetryEvent.AppVersion,
                DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            return Task.CompletedTask;
        }

        return TrackAsync(telemetryEvent, cancellationToken);
    }

    /// <summary>
    /// Attempts to send every currently queued event as one batch. Safe to
    /// call at any time, including with an empty queue or while disabled —
    /// used both after every <see cref="TrackAsync"/> call and once at
    /// startup to retry whatever could not be sent during a previous,
    /// possibly offline, run.
    /// </summary>
    public async Task FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!await flushLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            queue.Prune(MaxQueueAge, MaxQueuedEvents);
            if (!enabled)
            {
                return;
            }

            var pending = queue.ReadPending(MaxBatchSize);
            if (pending.Count == 0)
            {
                return;
            }

            var outcome = await transport
                .SendBatchWithOutcomeAsync(
                    pending.Select(item => includeOptionalData ? item.Event : item.Event.WithoutOptionalData()).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome == TelemetrySendOutcome.Accepted)
            {
                foreach (var item in pending)
                {
                    queue.Remove(item.FilePath);
                }
                Interlocked.Add(ref successfulSends, pending.Count);
            }
            else if (outcome == TelemetrySendOutcome.PermanentlyRejected)
            {
                foreach (var item in pending)
                {
                    queue.Remove(item.FilePath);
                }
                Interlocked.Add(ref failedSends, pending.Count);
                LogFailure($"Telemetry flush was permanently rejected. {pending.Count} invalid events removed from queue.");
            }
            else
            {
                Interlocked.Add(ref failedSends, pending.Count);
                LogFailure($"Telemetry flush failed with outcome: {outcome}. {pending.Count} events retained in queue.");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref failedSends, 1);
            LogFailure($"Telemetry flush threw: {ex.GetType().Name}: {ex.Message}");
            // A flush failure must never affect the optimization flow or
            // startup sequence that triggered it.
        }
        finally
        {
            flushLock.Release();
        }
    }

    private void LogFailure(string message)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "telemetry_failures.log");
            var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC - {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // Best-effort logging; never throw from telemetry.
        }
    }
}
