using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Ralven.App.Services;
using Ralven.UpdateRuntime;

namespace Ralven.App.Services;

public sealed class AtomicUpdateInstaller : ISilentUpdateInstaller
{
    private readonly string runtimeRoot;
    private readonly string launcherPath;
    private readonly string dataRoot;
    private readonly UpdaterDiagnostics diagnostics;

    public AtomicUpdateInstaller(string runtimeRoot, string launcherPath)
    {
        this.runtimeRoot = UpdatePathSafety.EnsureNoReparsePoints(runtimeRoot);
        this.launcherPath = UpdatePathSafety.EnsureNoReparsePoints(launcherPath);
        dataRoot = AppDataPaths.Root;
        diagnostics = new UpdaterDiagnostics(dataRoot);
    }

    public async Task<SilentUpdateLaunch> StartAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
    {
        string? previous = null;
        var activated = false;
        var journalStarted = false;
        var activation = new RuntimeActivationStore(runtimeRoot);
        var journal = new UpdateRecoveryJournal(runtimeRoot);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdatePathSafety.EnsureNoReparsePoints(launcherPath);
            if (!File.Exists(launcherPath)) throw new FileNotFoundException("O launcher transacional não foi encontrado.", launcherPath);
            previous = activation.ReadActiveVersion();
            // Hash do pacote inteiro + extração do ZIP + reverificação de
            // manifesto podem levar centenas de milissegundos a segundos;
            // isto é chamado direto da UI (MainViewModel.InstallDownloadedUpdateAsync),
            // então roda fora da thread de UI.
            await Task.Run(
                () => new RuntimePackageStager(runtimeRoot).Stage(
                    update.InstallerPath, update.Version.CoreVersion, update.Sha256Hex, update.SizeBytes,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            journal.Begin(previous, update.Version.CoreVersion);
            journalStarted = true;
            activation.Activate(update.Version.CoreVersion);
            activated = true;
            using var current = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(launcherPath)
            {
                WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
                UseShellExecute = false,
            };
            start.ArgumentList.Add($"--wait-for-pid={current.Id}");
            start.ArgumentList.Add($"--wait-for-start={current.StartTime.ToUniversalTime().ToFileTimeUtc()}");
            start.ArgumentList.Add($"--updated={update.Version.CoreVersion}");
            using var launcher = Process.Start(start)
                ?? throw new InvalidOperationException("O Windows não iniciou o launcher transacional.");
            return SilentUpdateLaunch.Running();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (previous is not null && (activated || journalStarted))
            {
                try
                {
                    if (activation.ReadActiveVersion() != previous) activation.Activate(previous);
                    journal.Complete();
                }
                catch (Exception rollbackException) when (rollbackException is not (
                    OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    await RecordFailureAsync(update, previous, "rollback", UpdaterEventCodes.RollbackFailed, rollbackException);
                    return SilentUpdateLaunch.Failed(
                        null, $"{exception.Message} A restauração imediata também falhou: {rollbackException.Message}");
                }
            }
            var stage = activated ? "activation" : "staging";
            await RecordFailureAsync(update, previous, stage, Classify(exception, stage), exception);
            return SilentUpdateLaunch.Failed(null, exception.Message);
        }
    }

    private Task RecordFailureAsync(
        DownloadedUpdate update, string? previous, string stage, string code, Exception exception) =>
        diagnostics.RecordAsync(
            new UpdaterEvent(
                Guid.NewGuid().ToString("N"), stage, "failed", code,
                previous, update.Version.CoreVersion, UpdaterDiagnostics.ResolveEnvironment()),
            exception.ToString(),
            telemetryAuthorized: UpdaterDiagnostics.IsTelemetryAuthorized(dataRoot));

    private static string Classify(Exception exception, string stage) => exception switch
    {
        UpdateSecurityException security => security.DiagnosticCode,
        CryptographicException or InvalidDataException => UpdaterEventCodes.StagingIntegrityFailed,
        UnauthorizedAccessException => UpdaterEventCodes.AccessDenied,
        FileNotFoundException or InvalidOperationException => UpdaterEventCodes.LauncherStartFailed,
        IOException => UpdaterEventCodes.LocalIoFailed,
        _ => stage == "activation" ? UpdaterEventCodes.ActivationFailed : UpdaterEventCodes.StagingFailed,
    };
}
