using System.IO;

using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>
/// Evento técnico mínimo, sem texto livre, caminhos, identificadores de
/// máquina ou dados do usuário. O contrato deliberadamente não aceita campos
/// adicionais para evitar que telemetria se transforme em coleta de logs.
/// </summary>
/// <remarks>
/// Os campos a partir de <see cref="OsVersion"/> foram adicionados na versão
/// 2 do consentimento de privacidade (<see cref="PrivacyConsentPolicy"/>):
/// perfil de hardware (CPU/GPU/RAM, sem identificar a máquina — os mesmos
/// nomes de modelo já mostrados no diagnóstico local) e os identificadores
/// técnicos de ação já listados na tela de consentimento.
///
/// Os campos a partir de <see cref="FiveMInstallDetected"/> foram adicionados
/// na versão 5 do consentimento de privacidade: diagnósticos essenciais
/// adicionais (detecção do FiveM/GTA V, contagem de alvos) e dados
/// opcionais de contexto (disco, timestamp, backup, elevação, processos).
/// Continua sem texto livre, caminhos ou qualquer identificador único de máquina.
/// </remarks>
public sealed record AnonymousTelemetryEvent(
    string EventName,
    TimeSpan ExecutionTime,
    string AppVersion,
    string? ErrorCategory = null,
    // --- v2: perfil de hardware (opcional) ---
    string? OsVersion = null,
    string? SystemArchitecture = null,
    string? CpuModel = null,
    string? GpuModel = null,
    int? RamBucketGiB = null,
    string? Profile = null,
    IReadOnlyList<string>? ActionIds = null,
    BugCode? BugCode = null,
    // --- v5: diagnósticos essenciais expandidos ---
    bool? FiveMInstallDetected = null,
    string? GtaEdition = null,
    int? OptimizationTargetCount = null,
    // --- v5: dados opcionais de contexto ---
    int? WindowsBuild = null,
    string? DiskType = null,
    int? FreeSpaceGiBBucket = null,
    DateTimeOffset? RunTimestamp = null,
    int? DaysSinceLastRunBucket = null,
    bool? BackupCreated = null,
    bool? BackupRestored = null,
    bool? ElevationUsed = null,
    int? ProcessCountAtStart = null,
    // v9: correlates one optional optimization flow only; never identifies an installation.
    Guid? OperationId = null)
{
    // This identifies a delivery attempt's logical event, never a device or user.
    // It is persisted with the queued payload so retries retain the same value.
    public Guid EventId { get; init; } = Guid.NewGuid();

    public AnonymousTelemetryEvent WithoutOptionalData() => this with
    {
        CpuModel = null,
        GpuModel = null,
        RamBucketGiB = null,
        Profile = null,
        ActionIds = null,
        WindowsBuild = null,
        DiskType = null,
        FreeSpaceGiBBucket = null,
        RunTimestamp = null,
        DaysSinceLastRunBucket = null,
        BackupCreated = null,
        BackupRestored = null,
        ElevationUsed = null,
        ProcessCountAtStart = null,
        OperationId = null
    };
}

/// <summary>Closed names for telemetry that has an operational consumer.</summary>
public static class TelemetryEventNames
{
    public const string AppInitialized = "app-initialized";
    public const string OptimizationStarted = "optimization-started";
    public const string OptimizationCompleted = "optimization-completed";
    public const string OptimizationFailed = "optimization-failed";
    public const string OptimizationCancelled = "optimization-cancelled";
    public const string GtaVBenchmarkCompleted = "gtav-benchmark-completed";
    public const string GtaVBenchmarkFailed = "gtav-benchmark-failed";
}

public interface IAnonymousTelemetryService
{
    bool IsEnabled { get; }

    bool IncludesOptionalData { get; }

    void Configure(bool enabled, bool includeOptionalData);

    Task TrackAsync(AnonymousTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default);

    long SuccessfulSends { get; }
    long FailedSends { get; }
    bool IsHealthy => FailedSends == 0 || SuccessfulSends > FailedSends;
}

public sealed class DisabledAnonymousTelemetryService : IAnonymousTelemetryService
{
    public static DisabledAnonymousTelemetryService Instance { get; } = new();

    private DisabledAnonymousTelemetryService()
    {
    }

    public bool IsEnabled => false;

    public bool IncludesOptionalData => false;

    public void Configure(bool enabled, bool includeOptionalData)
    {
    }

    public Task TrackAsync(AnonymousTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public long SuccessfulSends => 0;
    public long FailedSends => 0;
    public bool IsHealthy => true;
}

/// <summary>
/// Maps an exception to one of the closed telemetry error categories.
/// Deliberately independent of any specific transport — both the anonymous
/// telemetry pipeline and bug reports use this same fixed allowlist so an
/// exception's raw type/message never leaks into what gets sent.
/// </summary>
public static class TelemetryErrorClassifier
{
    public static string ClassifyBrokerFailure(string? errorCode, bool wasCancelled)
    {
        if (wasCancelled)
        {
            return "cancelled";
        }

        return errorCode switch
        {
            "broker-pipe-connection-failed" => "io",
            "broker-not-elevated" => "access-denied",
            "broker-invalid-arguments" => "invalid-data",
            _ => "unexpected"
        };
    }

    public static string ClassifyException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            BrokerIntegrityException { InnerException: { } innerException } => ClassifyException(innerException),
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            UnauthorizedAccessException => "access-denied",
            IOException => "io",
            InvalidDataException => "invalid-data",
            _ => "unexpected"
        };
    }
}
