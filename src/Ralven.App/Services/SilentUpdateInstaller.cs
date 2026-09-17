using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Ralven.UpdateRuntime;

namespace Ralven.App.Services;

public sealed record SilentUpdateLaunch(bool Started, int? ExitCode, string? FailureReason)
{
    public static SilentUpdateLaunch Running() => new(true, null, null);

    public static SilentUpdateLaunch Failed(int? exitCode, string reason) =>
        new(false, exitCode, reason);
}

public interface ISilentUpdateInstaller
{
    Task<SilentUpdateLaunch> StartAsync(DownloadedUpdate update, CancellationToken cancellationToken = default);
}

public interface IUpdateProcessLauncher
{
    void Start(string updaterPath, IReadOnlyList<string> arguments);
}

/// <summary>
/// Starts the dedicated updater from local app data, never from the folder
/// being replaced. The updater owns the wait-for-parent, integrity recheck,
/// setup execution, error display and restart lifecycle.
/// </summary>
public sealed class SilentUpdateInstaller : ISilentUpdateInstaller
{
    internal const string UpdaterFileName = "Ralven.Updater.exe";
    private readonly IUpdateProcessLauncher launcher;
    private readonly string updatesRootDirectory;
    private readonly string? logDirectory;
    private readonly string updaterSourcePath;
    private readonly string updaterRuntimeDirectory;

    public SilentUpdateInstaller(
        string updatesRootDirectory,
        string? logDirectory,
        string updaterSourcePath,
        string updaterRuntimeDirectory,
        IUpdateProcessLauncher? launcher = null)
    {
        this.updatesRootDirectory = RequireAbsoluteDirectory(updatesRootDirectory, nameof(updatesRootDirectory));
        this.updaterRuntimeDirectory = RequireAbsoluteDirectory(updaterRuntimeDirectory, nameof(updaterRuntimeDirectory));
        ArgumentException.ThrowIfNullOrWhiteSpace(updaterSourcePath);
        this.updaterSourcePath = UpdatePathSafety.EnsureNoReparsePoints(updaterSourcePath);
        this.logDirectory = logDirectory;
        this.launcher = launcher ?? new ProcessUpdateLauncher();
    }

    internal IReadOnlyList<string> BuildHandoffArguments(
        DownloadedUpdate update,
        int parentProcessId,
        long parentStartTimeUtcFileTime)
    {
        var arguments = new List<string>
        {
            "--installer", update.InstallerPath,
            "--installer-size", update.SizeBytes.ToString(CultureInfo.InvariantCulture),
            "--installer-sha256", update.Sha256Hex,
            "--parent-pid", parentProcessId.ToString(CultureInfo.InvariantCulture),
            "--parent-start-time", parentStartTimeUtcFileTime.ToString(CultureInfo.InvariantCulture),
            "--culture", LocalizationService.Current.CurrentCulture.Name,
        };
        var preparedLogDirectory = TryPrepareLogDirectory();
        if (preparedLogDirectory is not null)
        {
            arguments.Add("--log");
            arguments.Add(Path.Combine(preparedLogDirectory, "update-install.log"));
        }

        return arguments;
    }

    public Task<SilentUpdateLaunch> StartAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ResolveVerifiedInstallerPath(update);
            var updaterPath = CopyUpdaterOutsideInstallDirectory(out var integrityLease);
            using (integrityLease)
            {
                using var currentProcess = Process.GetCurrentProcess();
                launcher.Start(
                    updaterPath,
                    BuildHandoffArguments(
                        update,
                        currentProcess.Id,
                        currentProcess.StartTime.ToUniversalTime().ToFileTimeUtc()));
            }
            return Task.FromResult(SilentUpdateLaunch.Running());
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return Task.FromResult(SilentUpdateLaunch.Failed(null, exception.Message));
        }
    }

    private static string RequireAbsoluteDirectory(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("O caminho precisa ser absoluto.", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(UpdatePathSafety.EnsureNoReparsePoints(value));
    }

    private string? TryPrepareLogDirectory()
    {
        if (string.IsNullOrWhiteSpace(logDirectory)) return null;
        try
        {
            UpdatePathSafety.EnsureNoReparsePoints(logDirectory);
            Directory.CreateDirectory(logDirectory);
            UpdatePathSafety.EnsureNoReparsePoints(logDirectory);
            return logDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ResolveVerifiedInstallerPath(DownloadedUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.InstallerPath) || !Path.IsPathFullyQualified(update.InstallerPath))
        {
            throw new UpdateSecurityException("O caminho do instalador da atualização não é absoluto.", UpdaterEventCodes.InstallerPathRejected);
        }

        var fullPath = UpdatePathSafety.EnsureNoReparsePoints(update.InstallerPath);
        var requiredPrefix = updatesRootDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase)
            || !fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || update.SizeBytes <= 0
            || !IsSha256(update.Sha256Hex))
        {
            throw new UpdateSecurityException("O instalador verificado ou seus metadados de integridade não são válidos.", UpdaterEventCodes.InstallerMetadataInvalid);
        }
    }

    private string CopyUpdaterOutsideInstallDirectory(out FileStream integrityLease)
    {
        UpdatePathSafety.EnsureNoReparsePoints(updaterSourcePath);
        UpdatePathSafety.EnsureNoReparsePoints(updaterRuntimeDirectory);
        if (!File.Exists(updaterSourcePath)
            || !Path.GetFileName(updaterSourcePath).Equals(UpdaterFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("O atualizador independente não foi encontrado na instalação.", updaterSourcePath);
        }

        var sourceHash = ComputeSha256(updaterSourcePath);
        Directory.CreateDirectory(updaterRuntimeDirectory);
        UpdatePathSafety.EnsureNoReparsePoints(updaterRuntimeDirectory);
        var destination = Path.Combine(updaterRuntimeDirectory, UpdaterFileName);
        var temporary = Path.Combine(updaterRuntimeDirectory, $"{UpdaterFileName}.{Guid.NewGuid():N}.new");
        try
        {
            File.Copy(updaterSourcePath, temporary, overwrite: false);
            // Reverify before the rename so a swap of the temp file mid-copy is caught
            // before it ever occupies the path we are about to execute.
            if (!ComputeSha256(temporary).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateSecurityException("A cópia local do atualizador independente falhou na verificação de integridade.", UpdaterEventCodes.UpdaterCopyIntegrityFailed);
            }
            UpdatePathSafety.EnsureNoReparsePoints(destination);
            File.Move(temporary, destination, overwrite: true);
            var lease = new FileStream(
                destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var destinationHash = Convert.ToHexString(SHA256.HashData(lease));
                if (!destinationHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateSecurityException("O atualizador independente foi alterado após a cópia local.", UpdaterEventCodes.UpdaterCopyIntegrityFailed);
                }

                integrityLease = lease;
                return destination;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(static character => char.IsAsciiHexDigit(character));

    private sealed class ProcessUpdateLauncher : IUpdateProcessLauncher
    {
        public void Start(string updaterPath, IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = Path.GetDirectoryName(updaterPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("O Windows não iniciou o atualizador independente.");
        }
    }
}
