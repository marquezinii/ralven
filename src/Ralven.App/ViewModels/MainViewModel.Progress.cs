using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Windows.Threading;
using Ralven.App.Services;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Core.Planning;

namespace Ralven.App.ViewModels;

public sealed partial class MainViewModel
{
    public double ProgressPercent
    {
        get => progressPercent;
        private set
        {
            if (SetProperty(ref progressPercent, value))
            {
                OnPropertyChanged(nameof(ProgressIntensity));
                OnPropertyChanged(nameof(ProgressPercentLabel));
            }
        }
    }

    /// <summary>
    /// Progresso real da execução mapeado para a faixa 0,3–1, que a cena 3D do
    /// Otimizador usa como velocidade e brilho. Não é uma medida nova: é o
    /// mesmo <see cref="ProgressPercent"/>, com um piso para que o núcleo nunca
    /// pareça parado nos primeiros segundos de uma execução que já começou.
    /// </summary>
    public string ProgressPercentLabel => $"{Math.Round(ProgressPercent, MidpointRounding.AwayFromZero).ToString("0", localization.CurrentCulture)}%";

    public double ProgressIntensity => 0.3 + (Math.Clamp(ProgressPercent / 100d, 0, 1) * 0.7);

    public string ProgressHeadline
    {
        get => progressHeadline;
        private set
        {
            if (!string.Equals(progressHeadline, value, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(progressHeadline))
            {
                PreviousProgressHeadline = progressHeadline;
            }

            SetProperty(ref progressHeadline, value);
        }
    }

    public string PreviousProgressHeadline
    {
        get => previousProgressHeadline;
        private set
        {
            if (SetProperty(ref previousProgressHeadline, value))
            {
                OnPropertyChanged(nameof(HasPreviousProgressHeadline));
            }
        }
    }

    public bool HasPreviousProgressHeadline => !string.IsNullOrWhiteSpace(PreviousProgressHeadline);

    public string ElapsedTimeLabel { get => elapsedTimeLabel; private set => SetProperty(ref elapsedTimeLabel, value); }

    public string RemainingTimeLabel { get => remainingTimeLabel; private set => SetProperty(ref remainingTimeLabel, value); }

    private void TrackOptimizationTelemetry(
        string eventName,
        TimeSpan executionTime,
        string? errorCategory,
        BugCode? bugCode = null,
        Guid? operationId = null)
    {
        if (!telemetry.IsEnabled)
        {
            return;
        }

        var telemetryEvent = new AnonymousTelemetryEvent(
            eventName,
            executionTime,
            AppVersion.TrimStart('v', 'V'),
            errorCategory,
            OsVersion: diagnostic?.OsLabel,
            SystemArchitecture: diagnostic?.SystemArchitecture,
            CpuModel: ShareOptionalReports ? diagnostic?.CpuName : null,
            GpuModel: ShareOptionalReports ? diagnostic?.GpuName : null,
            RamBucketGiB: ShareOptionalReports && diagnostic is not null ? RamBucketCalculator.ComputeBucketGiB(diagnostic.TotalMemoryGiB) : null,
            Profile: ShareOptionalReports ? selectedProfile.ToString() : null,
            ActionIds: ShareOptionalReports && eventName != TelemetryEventNames.OptimizationStarted ? currentPlan?.Actions
                .Select(action => action.Metadata.Id)
                .Take(TelemetryEventValidator.MaxActionIds)
                .ToArray() : null,
            BugCode: bugCode,
            // v5: diagnósticos essenciais expandidos (sempre enviados)
            FiveMInstallDetected: diagnostic?.FiveMRoot is not null,
            GtaEdition: diagnostic?.Edition.ToString(),
            OptimizationTargetCount: currentPlan?.Actions.Count,
            // v5: dados opcionais de contexto (só com consentimento)
            WindowsBuild: ShareOptionalReports ? GetWindowsBuild() : null,
            DiskType: ShareOptionalReports ? GetDiskType() : null,
            FreeSpaceGiBBucket: ShareOptionalReports && diagnostic is not null ? BucketFreeSpaceGiB(diagnostic.FreeDiskGiB) : null,
            RunTimestamp: ShareOptionalReports ? DateTimeOffset.UtcNow : null,
            DaysSinceLastRunBucket: ShareOptionalReports ? GetDaysSinceLastRunBucket() : null,
            // These fields remain reserved until the transaction exposes an
            // authoritative outcome; never infer a backup or UAC result.
            BackupCreated: null,
            BackupRestored: null,
            ElevationUsed: null,
            ProcessCountAtStart: ShareOptionalReports ? GetProcessCountBucket() : null,
            OperationId: ShareOptionalReports ? operationId : null);
        if (eventName != TelemetryEventNames.OptimizationStarted
            && currentPlan?.PlanId is { } transactionId && transactionId != Guid.Empty)
        {
            telemetryEvent = telemetryEvent with { EventId = transactionId };
        }

        _ = TrackOptimizationTelemetryAsync(telemetryEvent);
    }

    internal AnonymousTelemetryEvent CreateAppInitializedTelemetryEvent() => new(
        TelemetryEventNames.AppInitialized,
        TimeSpan.Zero,
        AppVersion.TrimStart('v', 'V'),
        OsVersion: diagnostic?.OsLabel,
        SystemArchitecture: diagnostic?.SystemArchitecture);

    private void TrackOptimizationStartedTelemetry()
    {
        if (!ShareOptionalReports || currentPlan?.PlanId is not { } operationId || operationId == Guid.Empty)
        {
            return;
        }

        TrackOptimizationTelemetry(
            TelemetryEventNames.OptimizationStarted,
            TimeSpan.Zero,
            errorCategory: null,
            operationId: operationId);
    }

    private void TrackGtaVBenchmarkTelemetry(string eventName, TimeSpan executionTime, string? errorCategory)
    {
        if (!ShareOptionalReports || !telemetry.IsEnabled)
        {
            return;
        }

        _ = TrackOptimizationTelemetryAsync(new AnonymousTelemetryEvent(
            eventName,
            executionTime,
            AppVersion.TrimStart('v', 'V'),
            errorCategory,
            OsVersion: diagnostic?.OsLabel,
            SystemArchitecture: diagnostic?.SystemArchitecture));
    }

    private async Task TrackOptimizationTelemetryAsync(AnonymousTelemetryEvent telemetryEvent)
    {
        try
        {
            await telemetry.TrackAsync(telemetryEvent).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Telemetria é opcional e não pode afetar a experiência nem gerar logs locais adicionais.
        }
    }

    // --- v5: helpers para os novos campos de telemetria ---

    private static int? GetWindowsBuild()
    {
        try
        {
            return Environment.OSVersion.Version.Build;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private string? GetDiskType()
    {
        try
        {
            if (diagnostic?.GtaVExecutablePath is not { Length: > 0 } gtaPath)
            {
                return null;
            }

            var driveInfo = new DriveInfo(Path.GetPathRoot(gtaPath)!);
            return driveInfo.DriveType switch
            {
                DriveType.Fixed => driveInfo.IsReady && driveInfo.Name.StartsWith("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe" : "SSD",
                DriveType.Removable => "HDD",
                _ => "Unknown"
            };
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    private static int? BucketFreeSpaceGiB(double freeDiskGiB)
    {
        var freeGiB = (int)Math.Ceiling(freeDiskGiB);
        return freeGiB switch
        {
            <= 0 => 0,
            <= 10 => 10,
            <= 50 => 50,
            <= 100 => 100,
            _ => 250
        };
    }

    private static int? GetDaysSinceLastRunBucket()
    {
        try
        {
            var historyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ralven", "history.json");
            if (!File.Exists(historyPath))
            {
                return null;
            }

            var lastWrite = File.GetLastWriteTimeUtc(historyPath);
            var daysSince = (int)(DateTime.UtcNow - lastWrite).TotalDays;
            return daysSince switch
            {
                <= 1 => 0,
                <= 7 => 2,
                <= 29 => 8,
                _ => 30
            };
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    private static int? GetProcessCountBucket()
    {
        try
        {
            var total = CountRunning("FiveM") + CountRunning("GTA5");
            return total switch
            {
                0 => 0,
                <= 3 => 1,
                _ => 4
            };
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    /// <summary>
    /// Conta os processos em execução com este nome e descarta cada
    /// <see cref="Process"/> retornado: cada instância carrega um handle nativo
    /// que ficaria para o finalizador a cada relatório de otimização.
    /// </summary>
    private static int CountRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private void ApplyProgress(AppProgressUpdate update)
    {
        ProgressPercent = Math.Clamp(update.Percent, 0, 100);
        EnqueueHeadline(update.Headline);
        if (update.ActionId is not null && update.Outcome is { } outcome
            && outcome != ActionExecutionOutcome.Pending)
        {
            UpsertStepLedgerItem(update.ActionId, outcome);
        }

        UpdateOperationTiming();
    }

    private void ClearProgressHistory()
    {
        headlineDwellTimer?.Stop();
        headlineDwellTimer = null;
        pendingHeadlines.Clear();
        headlineShownAtUtc = default;
        previousProgressHeadline = string.Empty;
        progressHeadline = string.Empty;
        OnPropertyChanged(nameof(PreviousProgressHeadline));
        OnPropertyChanged(nameof(HasPreviousProgressHeadline));
        OnPropertyChanged(nameof(ProgressHeadline));
    }

    /// <summary>
    /// Cada passo do otimizador fica visível pelo menos <see cref="HeadlineMinimumDwell"/>
    /// antes de dar lugar ao próximo, em qualquer modo (leve/médio/agressivo).
    /// </summary>
    private void EnqueueHeadline(string headline)
    {
        if (string.IsNullOrWhiteSpace(headline)
            || string.Equals(headline, ProgressHeadline, StringComparison.Ordinal)
            || (pendingHeadlines.Count > 0 && string.Equals(pendingHeadlines.Last(), headline, StringComparison.Ordinal)))
        {
            return;
        }

        pendingHeadlines.Enqueue(headline);
        AdvanceHeadlineQueue();
    }

    private void AdvanceHeadlineQueue()
    {
        if (headlineDwellTimer is not null || pendingHeadlines.Count == 0)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - headlineShownAtUtc;
        if (elapsed >= HeadlineMinimumDwell)
        {
            ShowNextQueuedHeadline();
        }
        else
        {
            StartHeadlineDwellTimer(HeadlineMinimumDwell - elapsed);
        }
    }

    private void ShowNextQueuedHeadline()
    {
        ProgressHeadline = pendingHeadlines.Dequeue();
        headlineShownAtUtc = DateTime.UtcNow;
        StartHeadlineDwellTimer(HeadlineMinimumDwell);
    }

    private void StartHeadlineDwellTimer(TimeSpan due)
    {
        headlineDwellTimer?.Stop();
        headlineDwellTimer = new DispatcherTimer
        {
            Interval = due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1)
        };
        headlineDwellTimer.Tick += OnHeadlineDwellTimerTick;
        headlineDwellTimer.Start();
    }

    private void OnHeadlineDwellTimerTick(object? sender, EventArgs e)
    {
        headlineDwellTimer?.Stop();
        headlineDwellTimer = null;

        if (pendingHeadlines.Count > 0)
        {
            ShowNextQueuedHeadline();
        }
    }

    /// <summary>
    /// Usado para estados terminais (concluído, cancelado, falhou): descarta a fila de
    /// passos pendentes e mostra o resultado final imediatamente, sem esperar o dwell.
    /// </summary>
    private void FinalizeHeadline(string headline)
    {
        headlineDwellTimer?.Stop();
        headlineDwellTimer = null;
        pendingHeadlines.Clear();
        ProgressHeadline = headline;
        headlineShownAtUtc = DateTime.UtcNow;
    }

    private void StartOperationTiming()
    {
        operationTimer?.Stop();
        operationStopwatch = Stopwatch.StartNew();
        progressTimingEstimator.Reset();
        UpdateOperationTiming();

        operationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        operationTimer.Tick += OperationTimerOnTick;
        operationTimer.Start();
    }

    private void OperationTimerOnTick(object? sender, EventArgs eventArgs)
    {
        UpdateOperationTiming();
    }

    private void StopOperationTiming(bool completedSuccessfully)
    {
        if (operationTimer is not null)
        {
            operationTimer.Stop();
            operationTimer.Tick -= OperationTimerOnTick;
            operationTimer = null;
        }

        if (operationStopwatch is null)
        {
            return;
        }

        operationStopwatch.Stop();
        var elapsed = operationStopwatch.Elapsed;
        ElapsedTimeLabel = localization.Format(
            "Progress.ElapsedFormat",
            FormatDuration(elapsed));
        RemainingTimeLabel = completedSuccessfully
            ? localization.Format("Progress.CompletedInFormat", FormatDuration(elapsed))
            : string.Empty;
        operationStopwatch = null;
        progressTimingEstimator.Reset();
    }

    private void UpdateOperationTiming()
    {
        if (operationStopwatch is null)
        {
            return;
        }

        var elapsed = operationStopwatch.Elapsed;
        ElapsedTimeLabel = localization.Format(
            "Progress.ElapsedFormat",
            FormatDuration(elapsed));

        if (ProgressPercent >= 99)
        {
            RemainingTimeLabel = localization.GetString("Progress.Finishing");
            return;
        }

        if (elapsed < TimeSpan.FromSeconds(2) || ProgressPercent < 3)
        {
            RemainingTimeLabel = localization.GetString("Progress.Calculating");
            return;
        }

        var estimate = progressTimingEstimator.EstimateRemaining(elapsed, ProgressPercent);
        if (estimate is null)
        {
            RemainingTimeLabel = localization.GetString("Progress.Calculating");
            return;
        }

        RemainingTimeLabel = localization.Format(
            "Progress.RemainingFormat",
            FormatDuration(estimate.Value));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var rounded = TimeSpan.FromSeconds(Math.Max(0, Math.Round(duration.TotalSeconds)));
        return rounded.TotalHours >= 1
            ? $"{(int)rounded.TotalHours:00}:{rounded.Minutes:00}:{rounded.Seconds:00}"
            : $"{rounded.Minutes:00}:{rounded.Seconds:00}";
    }
}
