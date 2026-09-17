using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Windows.Infrastructure;

namespace Ralven.Windows.Actions;

public sealed class CacheStorageDiagnosisAction : WindowsOptimizationAction
{
    private const int MaxLockCheckPerScope = 200;
    private readonly string fiveMAppRoot;

    public CacheStorageDiagnosisAction(string fiveMAppRoot)
    {
        this.fiveMAppRoot = SafePath.Normalize(fiveMAppRoot);
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseCacheStorage);

    public override Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataRoot = Path.Combine(fiveMAppRoot, "data");
        var scopes = new (string Name, string Path)[]
        {
            ("server-cache", Path.Combine(dataRoot, "server-cache")),
            ("server-cache-priv", Path.Combine(dataRoot, "server-cache-priv")),
            ("nui-storage", Path.Combine(dataRoot, "nui-storage")),
            ("logs", Path.Combine(fiveMAppRoot, "logs")),
            ("crashes", Path.Combine(fiveMAppRoot, "crashes"))
        };

        var summaries = new List<string>();
        var lockedFiles = 0;
        long totalBytes = 0;

        foreach (var scope in scopes)
        {
            if (!Directory.Exists(scope.Path))
            {
                continue;
            }

            long scopeBytes = 0;
            var checkedForLock = 0;
            try
            {
                foreach (var file in new DirectoryInfo(scope.Path)
                             .EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scopeBytes += file.Length;
                    if (checkedForLock >= MaxLockCheckPerScope)
                    {
                        continue;
                    }

                    checkedForLock++;
                    if (IsLocked(file.FullName))
                    {
                        lockedFiles++;
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                summaries.Add(WindowsActionText.Format("ActionResults.CacheStorage.ScopeReadFailed", scope.Name));
                continue;
            }

            totalBytes += scopeBytes;
            summaries.Add(WindowsActionText.Format(
                "ActionResults.CacheStorage.ScopeSize",
                scope.Name,
                FormatBytes(scopeBytes)));
        }

        if (summaries.Count == 0)
        {
            return Task.FromResult(WindowsActionApplyResult.NoChange(
                WindowsActionText.Format("ActionResults.CacheStorage.NoneFound")));
        }

        var message = WindowsActionText.Format(
                "ActionResults.CacheStorage.Total",
                FormatBytes(totalBytes),
                string.Join(", ", summaries))
            + (lockedFiles > 0
                ? " " + WindowsActionText.Format(
                    lockedFiles == 1
                        ? "ActionResults.CacheStorage.LockedFile"
                        : "ActionResults.CacheStorage.LockedFiles",
                    lockedFiles)
                : " " + WindowsActionText.Format("ActionResults.CacheStorage.NoLockedFiles"));
        return Task.FromResult(WindowsActionApplyResult.NoChange(message));
    }

    private static string FormatBytes(long bytes)
    {
        const double mib = 1024d * 1024d;
        const double gib = mib * 1024d;
        return WindowsActionText.Format(
            bytes >= gib ? "Unit.Gigabytes" : "Unit.Megabytes",
            bytes >= gib ? bytes / gib : bytes / mib);
    }

    public override Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

}

public sealed class InstallationHealthDiagnosisAction : WindowsOptimizationAction
{
    private const long MinimumFreeSpaceGiB = 5;
    private const long GiB = 1024L * 1024L * 1024L;
    private readonly string fiveMInstallationRoot;
    private readonly string fiveMAppRoot;

    public InstallationHealthDiagnosisAction(string fiveMInstallationRoot, string fiveMAppRoot)
    {
        this.fiveMInstallationRoot = SafePath.Normalize(fiveMInstallationRoot);
        this.fiveMAppRoot = SafePath.Normalize(fiveMAppRoot);
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseInstallationHealth);

    public override Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var findings = new List<string>();

        if (TryFindDuplicateInstallation(out var duplicatePath))
        {
            findings.Add(WindowsActionText.Format("ActionResults.InstallationHealth.Duplicate", duplicatePath));
        }

        if (!HasWritePermission())
        {
            findings.Add(WindowsActionText.Format("ActionResults.InstallationHealth.WriteDenied"));
        }

        if (IsUnderOneDrive())
        {
            findings.Add(WindowsActionText.Format("ActionResults.InstallationHealth.OneDrive"));
        }

        if (TryGetLowFreeSpace(out var freeGiB))
        {
            findings.Add(WindowsActionText.Format("ActionResults.InstallationHealth.LowSpace", freeGiB));
        }

        var message = findings.Count == 0
            ? WindowsActionText.Format("ActionResults.InstallationHealth.NoIssues")
            : string.Join(" ", findings);
        return Task.FromResult(WindowsActionApplyResult.NoChange(message));
    }

    public override Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private bool TryFindDuplicateInstallation(out string duplicatePath)
    {
        duplicatePath = string.Empty;
        var parent = Path.GetDirectoryName(fiveMInstallationRoot);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return false;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(parent, "FiveM*"))
            {
                if (candidate.Equals(fiveMInstallationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(candidate, "FiveM.exe")))
                {
                    duplicatePath = candidate;
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
        }

        return false;
    }

    private bool HasWritePermission()
    {
        var dataRoot = Path.Combine(fiveMAppRoot, "data");
        var probeDirectory = Directory.Exists(dataRoot) ? dataRoot : fiveMAppRoot;
        if (!Directory.Exists(probeDirectory))
        {
            return true;
        }

        var probeFile = Path.Combine(probeDirectory, $".ralven-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probeFile, [0]);
            File.Delete(probeFile);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private bool IsUnderOneDrive()
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var oneDrivePath = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(oneDrivePath))
            {
                continue;
            }

            try
            {
                var normalized = SafePath.Normalize(oneDrivePath);
                if (fiveMInstallationRoot.StartsWith(
                    normalized + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return false;
    }

    private bool TryGetLowFreeSpace(out double freeGiB)
    {
        freeGiB = 0;
        try
        {
            var root = Path.GetPathRoot(fiveMInstallationRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            freeGiB = drive.AvailableFreeSpace / (double)GiB;
            return freeGiB < MinimumFreeSpaceGiB;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Reads the tail of the FiveM installation's most recent log file, shared by
/// the actions that look for crash/entitlement error patterns without a full
/// parse. Bounded to <see cref="MaxTailBytes"/> so a huge log never gets read
/// in full.
/// </summary>
internal static class FiveMLogTailReader
{
    private const long MaxTailBytes = 512 * 1024;

    public static string? ReadLatest(string fiveMAppRoot)
        => ReadLatestWithMetadata(fiveMAppRoot)?.Content;

    public static FiveMLogTail? ReadLatestWithMetadata(string fiveMAppRoot)
    {
        var logsDirectory = Path.Combine(fiveMAppRoot, "logs");
        if (!Directory.Exists(logsDirectory))
        {
            return null;
        }

        FileInfo? latest;
        try
        {
            latest = new DirectoryInfo(logsDirectory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        if (latest is null)
        {
            return null;
        }

        try
        {
            return new FiveMLogTail(ReadTail(latest.FullName), latest.LastWriteTimeUtc);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lê o final do arquivo, limitado a <see cref="MaxTailBytes"/>. O
    /// compartilhamento permissivo é deliberado: o log pode estar aberto pelo
    /// próprio jogo enquanto é lido.
    /// </summary>
    internal static string ReadTail(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxTailBytes)
        {
            stream.Seek(-MaxTailBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class CrashPatternDiagnosisAction : WindowsOptimizationAction
{
    private readonly string fiveMAppRoot;
    private static readonly Regex CrashCodePattern = new(
        @"0x[0-9A-Fa-f]{8}",
        RegexOptions.Compiled);

    private static readonly string[] StreamingKeywords =
    [
        "streaming", "ymap", "ytd", "ydr", "ybn", "resource start error", "failed to load"
    ];

    public CrashPatternDiagnosisAction(string fiveMAppRoot)
    {
        this.fiveMAppRoot = SafePath.Normalize(fiveMAppRoot);
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DiagnoseCrashPatterns);

    public override Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parts = new List<string>();

        var crashesDirectory = Path.Combine(fiveMAppRoot, "crashes");
        if (Directory.Exists(crashesDirectory))
        {
            try
            {
                var fileNames = Directory.EnumerateFiles(crashesDirectory)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .ToArray();
                var recurring = CountRecurringCrashCodes(fileNames);
                if (recurring.Count > 0)
                {
                    var codes = string.Join(", ", recurring.Select(pair => $"{pair.Key} ({pair.Value}x)"));
                    parts.Add(WindowsActionText.Format("ActionResults.CrashPatterns.RecurringCodes", codes));
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                parts.Add(WindowsActionText.Format("ActionResults.CrashPatterns.DumpsReadFailed"));
            }
        }

        var logTail = FiveMLogTailReader.ReadLatest(fiveMAppRoot);
        if (logTail is not null)
        {
            var streamingKeywords = FindStreamingErrorKeywords(logTail);
            if (streamingKeywords.Count > 0)
            {
                parts.Add(WindowsActionText.Format(
                    "ActionResults.CrashPatterns.StreamingErrors",
                    string.Join(", ", streamingKeywords)));
            }
        }

        var message = parts.Count == 0
            ? WindowsActionText.Format("ActionResults.CrashPatterns.NoneFound")
            : string.Join(" ", parts) + " " + WindowsActionText.Format("ActionResults.CrashPatterns.Disclaimer");
        return Task.FromResult(WindowsActionApplyResult.NoChange(message));
    }

    public override Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static IReadOnlyDictionary<string, int> CountRecurringCrashCodes(
        IEnumerable<string> dumpFileNames)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in dumpFileNames)
        {
            foreach (Match match in CrashCodePattern.Matches(fileName))
            {
                counts[match.Value] = counts.GetValueOrDefault(match.Value) + 1;
            }
        }

        return counts
            .Where(pair => pair.Value >= 2)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> FindStreamingErrorKeywords(string logTail)
    {
        return StreamingKeywords
            .Where(keyword => logTail.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}

internal sealed record TerminatedProcessSnapshot(int ProcessId, string ProcessName);

public sealed class StuckProcessTerminationAction : WindowsOptimizationAction
{
    private readonly string fiveMInstallationRoot;
    private readonly IStuckFiveMProcessInspector inspector;
    private readonly IFiveMProcessTerminator terminator;

    public StuckProcessTerminationAction(
        string fiveMInstallationRoot,
        IStuckFiveMProcessInspector inspector,
        IFiveMProcessTerminator terminator)
    {
        this.fiveMInstallationRoot = SafePath.Normalize(fiveMInstallationRoot);
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        this.terminator = terminator ?? throw new ArgumentNullException(nameof(terminator));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.TerminateStuckFiveMProcess);

    public override Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = inspector.GetSnapshot(fiveMInstallationRoot);
        if (!snapshot.Found)
        {
            return Task.FromResult(WindowsActionApplyResult.NoChange(
                WindowsActionText.Format("ActionResults.StuckProcess.NoneFound")));
        }

        if (!terminator.TryTerminate(snapshot, fiveMInstallationRoot))
        {
            throw new InvalidOperationException(
                $"Processo travado '{snapshot.ProcessName}' (PID {snapshot.ProcessId}) foi encontrado, mas não foi possível encerrá-lo agora.");
        }

        return Task.FromResult(WindowsActionApplyResult.ChangedWith(
            new TerminatedProcessSnapshot(snapshot.ProcessId, snapshot.ProcessName),
            WindowsActionText.Format(
                "ActionResults.StuckProcess.Terminated",
                snapshot.ProcessName,
                snapshot.ProcessId)));
    }

    public override Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        // Irreversible by nature: a terminated process cannot be restored.
        return Task.CompletedTask;
    }
}

public sealed class RecreateFiveMLocalDataAction : QuarantineCleanupAction
{
    private readonly string fiveMAppRoot;
    private readonly string installationRoot;
    private readonly IFiveMProcessInspector processInspector;

    public RecreateFiveMLocalDataAction(
        string fiveMAppRoot,
        string installationRoot,
        IFiveMProcessInspector processInspector,
        SafeFileTree? fileTree = null)
        : base(fileTree)
    {
        this.fiveMAppRoot = SafePath.Normalize(fiveMAppRoot);
        this.installationRoot = SafePath.Normalize(installationRoot);
        _ = SafePath.EnsureDescendant(this.installationRoot, this.fiveMAppRoot);
        this.processInspector = processInspector
            ?? throw new ArgumentNullException(nameof(processInspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.RecreateFiveMLocalData);

    protected override IReadOnlyList<CleanupScope> GetScopes(WindowsActionContext context)
    {
        if (processInspector.IsRunningFrom(installationRoot))
        {
            throw new InvalidOperationException("FiveM precisa estar fechado para recriar os dados locais.");
        }

        var dataRoot = SafePath.EnsureDescendant(fiveMAppRoot, Path.Combine(fiveMAppRoot, "data"));
        var matchAll = context.StartedAtUtc.AddMinutes(1);
        return
        [
            new CleanupScope(
                "server-cache",
                SafePath.EnsureDescendant(dataRoot, Path.Combine(dataRoot, "server-cache")),
                matchAll),
            new CleanupScope(
                "server-cache-priv",
                SafePath.EnsureDescendant(dataRoot, Path.Combine(dataRoot, "server-cache-priv")),
                matchAll),
            new CleanupScope(
                "logs",
                SafePath.EnsureDescendant(fiveMAppRoot, Path.Combine(fiveMAppRoot, "logs")),
                matchAll),
            new CleanupScope(
                "crashes",
                SafePath.EnsureDescendant(fiveMAppRoot, Path.Combine(fiveMAppRoot, "crashes")),
                matchAll)
        ];
    }

    protected override IReadOnlyDictionary<string, string> GetAllowedScopeRoots()
    {
        var dataRoot = SafePath.EnsureDescendant(fiveMAppRoot, Path.Combine(fiveMAppRoot, "data"));
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server-cache"] = SafePath.EnsureDescendant(dataRoot, Path.Combine(dataRoot, "server-cache")),
            ["server-cache-priv"] = SafePath.EnsureDescendant(dataRoot, Path.Combine(dataRoot, "server-cache-priv")),
            ["logs"] = SafePath.EnsureDescendant(fiveMAppRoot, Path.Combine(fiveMAppRoot, "logs")),
            ["crashes"] = SafePath.EnsureDescendant(fiveMAppRoot, Path.Combine(fiveMAppRoot, "crashes"))
        };
    }
}

internal sealed record FiveMLogTail(string Content, DateTimeOffset LastWriteTimeUtc);

internal sealed record QuarantinedAuthEntry(
    string RelativePath,
    bool IsDirectory,
    long Length,
    string? Sha256);

internal sealed record QuarantinedAuthItem(
    string OriginalPath,
    string QuarantinePath,
    bool IsDirectory,
    string? Sha256,
    IReadOnlyList<QuarantinedAuthEntry> Entries);

internal sealed record AuthDataRepairSnapshot(IReadOnlyList<QuarantinedAuthItem> Items);

public sealed class StaleAuthDataRepairAction : WindowsOptimizationAction
{
    private const int MaximumCapturedEntries = 4096;
    private static readonly TimeSpan MaximumDiagnosticLogAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumFutureLogSkew = TimeSpan.FromMinutes(5);
    private readonly string fiveMAppRoot;
    private readonly string rosIdPath;
    private readonly string digitalEntitlementsRoot;
    private readonly string quarantineRoot;
    private readonly string installationRoot;
    private readonly IFiveMProcessInspector processInspector;
    private static readonly string[] EntitlementFailurePhrases =
    [
        "entitlement error",
        "entitlement failed",
        "failed entitlement",
        "ros_id error",
        "ros_id failed",
        "social club authentication failed",
        "digitalentitlements error",
        "digitalentitlements failed"
    ];
    private static readonly Regex BracketedLogPrefix = new(
        @"^(?:\s*\[[^\]\r\n]{1,80}\])+\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public StaleAuthDataRepairAction(
        string fiveMAppRoot,
        string installationRoot,
        string rosIdPath,
        string expectedRosIdParent,
        string digitalEntitlementsRoot,
        string expectedDigitalEntitlementsParent,
        string quarantineRoot,
        IFiveMProcessInspector processInspector)
    {
        this.fiveMAppRoot = SafePath.Normalize(fiveMAppRoot);
        this.installationRoot = SafePath.Normalize(installationRoot);
        this.rosIdPath = SafePath.Normalize(rosIdPath);
        this.digitalEntitlementsRoot = SafePath.Normalize(digitalEntitlementsRoot);
        this.quarantineRoot = SafePath.Normalize(quarantineRoot);
        var rosIdParent = Path.GetDirectoryName(this.rosIdPath);
        var digitalEntitlementsParent = Path.GetDirectoryName(this.digitalEntitlementsRoot);
        if (!Path.GetFileName(this.rosIdPath).Equals("ros_id.dat", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(rosIdParent)
            || !SafePath.Normalize(rosIdParent).Equals(
                SafePath.Normalize(expectedRosIdParent),
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(this.digitalEntitlementsRoot).Equals(
                "DigitalEntitlements",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(digitalEntitlementsParent)
            || !SafePath.Normalize(digitalEntitlementsParent).Equals(
                SafePath.Normalize(expectedDigitalEntitlementsParent),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Os caminhos de entitlement não correspondem aos alvos allowlisted.");
        }

        SafePath.EnsureDescendant(this.installationRoot, this.fiveMAppRoot);

        this.processInspector = processInspector
            ?? throw new ArgumentNullException(nameof(processInspector));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.RepairStaleAuthData);

    public override Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (processInspector.IsRunningFrom(installationRoot))
        {
            throw new InvalidOperationException("FiveM precisa estar fechado para reparar os dados de entitlement.");
        }

        var logTail = FiveMLogTailReader.ReadLatestWithMetadata(fiveMAppRoot);
        if (logTail is null
            || logTail.LastWriteTimeUtc < context.StartedAtUtc - MaximumDiagnosticLogAge
            || logTail.LastWriteTimeUtc > context.StartedAtUtc + MaximumFutureLogSkew)
        {
            return Task.FromResult(WindowsActionApplyResult.Skipped(
                WindowsActionText.Format("ActionResults.AuthRepair.NoRecentLog")));
        }

        if (!ContainsEntitlementFailurePattern(logTail.Content))
        {
            return Task.FromResult(WindowsActionApplyResult.NoChange(
                WindowsActionText.Format("ActionResults.AuthRepair.NoPattern")));
        }

        var transactionQuarantine = SafePath.EnsureDescendant(
            quarantineRoot,
            Path.Combine(quarantineRoot, context.TransactionId.ToString("N")));
        var moved = new List<QuarantinedAuthItem>();

        try
        {
            if (File.Exists(rosIdPath))
            {
                SafePath.EnsureNoReparsePoints(rosIdPath);
                var destination = Path.Combine(transactionQuarantine, Path.GetFileName(rosIdPath));
                Directory.CreateDirectory(transactionQuarantine);
                SafePath.EnsureNoReparsePoints(transactionQuarantine);
                File.Move(rosIdPath, destination, overwrite: false);
                moved.Add(new QuarantinedAuthItem(rosIdPath, destination, false, null, []));
                moved[^1] = new QuarantinedAuthItem(
                    rosIdPath,
                    destination,
                    IsDirectory: false,
                    ComputeFileSha256(destination),
                    []);
            }

            if (Directory.Exists(digitalEntitlementsRoot))
            {
                SafePath.EnsureNoReparsePoints(digitalEntitlementsRoot);
                var destination = Path.Combine(transactionQuarantine, Path.GetFileName(digitalEntitlementsRoot));
                Directory.CreateDirectory(transactionQuarantine);
                SafePath.EnsureNoReparsePoints(transactionQuarantine);
                Directory.Move(digitalEntitlementsRoot, destination);
                moved.Add(new QuarantinedAuthItem(digitalEntitlementsRoot, destination, true, null, []));
                moved[^1] = new QuarantinedAuthItem(
                    digitalEntitlementsRoot,
                    destination,
                    IsDirectory: true,
                    Sha256: null,
                    CaptureDirectoryEntries(destination));
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            RestoreItems(moved, throwOnConflict: false);
            throw;
        }

        if (moved.Count == 0)
        {
            return Task.FromResult(WindowsActionApplyResult.Skipped(
                WindowsActionText.Format("ActionResults.AuthRepair.NoFiles")));
        }

        return Task.FromResult(WindowsActionApplyResult.ChangedWith(
            new AuthDataRepairSnapshot(moved),
            WindowsActionText.Format(
                moved.Count == 1
                    ? "ActionResults.AuthRepair.ItemMoved"
                    : "ActionResults.AuthRepair.ItemsMoved",
                moved.Count)));
    }

    public override Task CommitAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return Task.CompletedTask;
        }

        var snapshot = WindowsActionSnapshot.Deserialize<AuthDataRepairSnapshot>(snapshotJson);
        ValidateSnapshot(context, snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in snapshot.Items)
        {
            ValidateQuarantinedContent(item);
        }

        foreach (var item in snapshot.Items)
        {
            if (item.IsDirectory && Directory.Exists(item.QuarantinePath))
            {
                DeleteCapturedDirectory(item);
            }
            else if (!item.IsDirectory && File.Exists(item.QuarantinePath))
            {
                File.Delete(item.QuarantinePath);
            }
        }

        return Task.CompletedTask;
    }

    public override Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return Task.CompletedTask;
        }

        var snapshot = WindowsActionSnapshot.Deserialize<AuthDataRepairSnapshot>(snapshotJson);
        ValidateSnapshot(context, snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in snapshot.Items)
        {
            ValidateQuarantinedContent(item);
        }

        RestoreItems(snapshot.Items, throwOnConflict: true);
        return Task.CompletedTask;
    }

    private void ValidateSnapshot(WindowsActionContext context, AuthDataRepairSnapshot snapshot)
    {
        if (snapshot.Items is null || snapshot.Items.Count is 0 or > 2)
        {
            throw new InvalidDataException("O snapshot de entitlement não contém itens válidos.");
        }

        var transactionQuarantine = SafePath.EnsureDescendant(
            quarantineRoot,
            Path.Combine(quarantineRoot, context.TransactionId.ToString("N")));
        var allowed = new Dictionary<string, (string QuarantinePath, bool IsDirectory)>(
            StringComparer.OrdinalIgnoreCase)
        {
            [rosIdPath] = (Path.Combine(transactionQuarantine, Path.GetFileName(rosIdPath)), false),
            [digitalEntitlementsRoot] = (
                Path.Combine(transactionQuarantine, Path.GetFileName(digitalEntitlementsRoot)),
                true)
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Items)
        {
            var original = SafePath.Normalize(item.OriginalPath);
            if (!seen.Add(original)
                || !allowed.TryGetValue(original, out var expected)
                || expected.IsDirectory != item.IsDirectory
                || !SafePath.Normalize(item.QuarantinePath).Equals(
                    SafePath.Normalize(expected.QuarantinePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("O snapshot de entitlement contém um caminho fora da allowlist.");
            }

            ValidateCapturedEntries(item);
        }
    }

    private static void RestoreItems(
        IReadOnlyList<QuarantinedAuthItem> items,
        bool throwOnConflict)
    {
        var conflicts = new List<string>();
        foreach (var item in items.Reverse())
        {
            SafePath.EnsureNoReparsePoints(item.QuarantinePath);
            SafePath.EnsureNoReparsePoints(item.OriginalPath);
            if (item.IsDirectory)
            {
                if (Directory.Exists(item.QuarantinePath) && Directory.Exists(item.OriginalPath))
                {
                    conflicts.Add(item.OriginalPath);
                }
                else if (Directory.Exists(item.QuarantinePath))
                {
                    Directory.Move(item.QuarantinePath, item.OriginalPath);
                }
            }
            else if (File.Exists(item.QuarantinePath) && File.Exists(item.OriginalPath))
            {
                conflicts.Add(item.OriginalPath);
            }
            else if (File.Exists(item.QuarantinePath))
            {
                File.Move(item.QuarantinePath, item.OriginalPath);
            }
        }

        if (throwOnConflict && conflicts.Count > 0)
        {
            throw new IOException(
                $"Rollback preservou {conflicts.Count} item(ns) de entitlement em quarentena porque o destino foi recriado.");
        }
    }

    private static bool ContainsEntitlementFailurePattern(string logTail)
    {
        return logTail
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => BracketedLogPrefix.Replace(line, string.Empty).Trim().TrimEnd('.', '!', ':'))
            .Any(line => EntitlementFailurePhrases.Contains(line, StringComparer.OrdinalIgnoreCase));
    }

    private static void ValidateCapturedEntries(QuarantinedAuthItem item)
    {
        if (item.Entries is null
            || (!item.IsDirectory && (item.Entries.Count != 0 || string.IsNullOrWhiteSpace(item.Sha256)))
            || (item.IsDirectory && item.Sha256 is not null)
            || item.Entries.Count > MaximumCapturedEntries)
        {
            throw new InvalidDataException("O snapshot de entitlement contém um manifesto inválido.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in item.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath)
                || Path.IsPathRooted(entry.RelativePath)
                || !seen.Add(entry.RelativePath)
                || entry.Length < 0
                || (entry.IsDirectory ? entry.Sha256 is not null : string.IsNullOrWhiteSpace(entry.Sha256)))
            {
                throw new InvalidDataException("O snapshot de entitlement contém uma entrada inválida.");
            }

            SafePath.EnsureDescendant(item.QuarantinePath, Path.Combine(item.QuarantinePath, entry.RelativePath));
        }
    }

    private static void ValidateQuarantinedContent(QuarantinedAuthItem item)
    {
        SafePath.EnsureNoReparsePoints(item.QuarantinePath);
        if (item.IsDirectory)
        {
            var current = CaptureDirectoryEntries(item.QuarantinePath);
            if (!current.SequenceEqual(item.Entries))
            {
                throw new IOException("A quarentena de entitlement foi alterada depois da aplicação; o conteúdo foi preservado.");
            }
        }
        else if (!File.Exists(item.QuarantinePath)
            || !ComputeFileSha256(item.QuarantinePath).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("O item em quarentena foi alterado depois da aplicação; o conteúdo foi preservado.");
        }
    }

    private static IReadOnlyList<QuarantinedAuthEntry> CaptureDirectoryEntries(string root)
    {
        var entries = new List<QuarantinedAuthEntry>();
        CaptureDirectoryEntries(root, root, entries);
        return entries
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void CaptureDirectoryEntries(
        string root,
        string directory,
        List<QuarantinedAuthEntry> entries)
    {
        SafePath.EnsureNoReparsePoints(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (entries.Count >= MaximumCapturedEntries)
            {
                throw new InvalidDataException("A quarentena de entitlement excede o limite seguro de itens.");
            }

            SafePath.EnsureDescendant(root, path);
            SafePath.EnsureNoReparsePoints(path);
            var relativePath = Path.GetRelativePath(root, path);
            if (Directory.Exists(path))
            {
                entries.Add(new QuarantinedAuthEntry(relativePath, true, 0, null));
                CaptureDirectoryEntries(root, path, entries);
            }
            else
            {
                var info = new FileInfo(path);
                entries.Add(new QuarantinedAuthEntry(
                    relativePath,
                    false,
                    info.Length,
                    ComputeFileSha256(path)));
            }
        }
    }

    private static void DeleteCapturedDirectory(QuarantinedAuthItem item)
    {
        foreach (var entry in item.Entries.Where(entry => !entry.IsDirectory))
        {
            var path = SafePath.EnsureDescendant(
                item.QuarantinePath,
                Path.Combine(item.QuarantinePath, entry.RelativePath));
            SafePath.EnsureNoReparsePoints(path);
            if (!ComputeFileSha256(path).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Um item em quarentena mudou durante a confirmação; o conteúdo restante foi preservado.");
            }

            File.Delete(path);
        }

        foreach (var entry in item.Entries
            .Where(entry => entry.IsDirectory)
            .OrderByDescending(entry => entry.RelativePath.Count(character => character == Path.DirectorySeparatorChar)))
        {
            var path = SafePath.EnsureDescendant(
                item.QuarantinePath,
                Path.Combine(item.QuarantinePath, entry.RelativePath));
            SafePath.EnsureNoReparsePoints(path);
            Directory.Delete(path, recursive: false);
        }

        Directory.Delete(item.QuarantinePath, recursive: false);
    }

    private static string ComputeFileSha256(string path)
    {
        SafePath.EnsureNoReparsePoints(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
