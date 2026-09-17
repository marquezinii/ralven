using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Windows.Infrastructure;
using Microsoft.Win32;

namespace Ralven.Windows.Actions;

public sealed record RegistryMutation(
    RegistryAddress Address,
    RegistryValueState DesiredValue);

internal sealed record RegistryMutationSnapshotEntry(
    RegistryAddress Address,
    RegistryValueState PreviousValue,
    RegistryValueState AppliedValue);

internal sealed record RegistryMutationSnapshot(
    IReadOnlyList<RegistryMutationSnapshotEntry> Entries);

public abstract class AllowlistedRegistryAction : WindowsOptimizationAction
{
    protected AllowlistedRegistryAction(IRegistryStore registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    protected IRegistryStore Registry { get; }

    protected abstract IReadOnlyList<RegistryMutation> GetMutations();

    protected virtual RegistryValueState? ResolveDesiredValue(
        RegistryMutation mutation,
        RegistryValueState previousValue)
    {
        return mutation.DesiredValue;
    }

    protected virtual void ValidateCurrentValueForApply(
        RegistryMutation mutation,
        RegistryValueState currentValue)
    {
    }

    protected virtual void ValidateMutationSafety(WindowsActionContext context)
    {
    }

    protected static void EnsureFiveMStopped(
        IFiveMProcessInspector processInspector,
        string? installationRoot)
    {
        var isRunning = string.IsNullOrWhiteSpace(installationRoot)
            ? processInspector.IsAnyRunning()
            : processInspector.IsRunningFrom(installationRoot);
        if (isRunning)
        {
            throw new InvalidOperationException(
                "FiveM must be closed before Windows gaming settings can be changed.");
        }
    }

    protected virtual bool IsAllowedRollbackEntry(
        RegistryAddress address,
        RegistryValueState previousValue,
        RegistryValueState appliedValue,
        IReadOnlyList<RegistryMutation> currentMutations)
    {
        var key = CanonicalAddress(address);
        var mutation = currentMutations.FirstOrDefault(candidate =>
            CanonicalAddress(candidate.Address).Equals(key, StringComparison.OrdinalIgnoreCase));
        return mutation is not null
            && ResolveDesiredValue(mutation, previousValue) is { } expectedAppliedValue
            && Equivalent(appliedValue, expectedAppliedValue);
    }

    public override Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        var applied = new List<RegistryMutationSnapshotEntry>();
        try
        {
            foreach (var mutation in GetMutations())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previous = Registry.Read(mutation.Address);
                ValidateCurrentValueForApply(mutation, previous);
                var desired = ResolveDesiredValue(mutation, previous);
                if (desired is null || Equivalent(previous, desired))
                {
                    continue;
                }

                ValidateMutationSafety(context);
                var snapshotEntry = new RegistryMutationSnapshotEntry(
                    mutation.Address,
                    previous,
                    desired);
                // Record before writing: a registry provider may mutate the value and
                // still throw, in which case immediate recovery must know what to restore.
                applied.Add(snapshotEntry);
                Registry.Write(mutation.Address, desired);
                if (!Equivalent(Registry.Read(mutation.Address), desired))
                {
                    throw new IOException(
                        "O Windows não confirmou a configuração de registro solicitada.");
                }
            }
        }
        catch (Exception applyException)
        {
            try
            {
                RestoreEntries(
                    applied,
                    requireAppliedValue: false,
                    context with { IsImmediateFailureRecovery = true });
            }
            catch (Exception recoveryException)
            {
                throw new AggregateException(
                    "A alteração de registro falhou e o estado anterior não pôde ser confirmado.",
                    applyException,
                    recoveryException);
            }

            throw;
        }

        if (applied.Count == 0)
        {
            return Task.FromResult(WindowsActionApplyResult.NoChange(
                WindowsActionText.Format("ActionResults.Registry.NoChange")));
        }

        return Task.FromResult(WindowsActionApplyResult.ChangedWith(
            new RegistryMutationSnapshot(applied),
            WindowsActionText.Format("ActionResults.Registry.Applied", applied.Count)));
    }

    public override Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        var snapshot = WindowsActionSnapshot.Deserialize<RegistryMutationSnapshot>(snapshotJson);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRollbackSnapshot(snapshot);
        RestoreEntries(snapshot.Entries, requireAppliedValue: true, context);
        return Task.CompletedTask;
    }

    private void ValidateRollbackSnapshot(RegistryMutationSnapshot snapshot)
    {
        if (snapshot.Entries is null || snapshot.Entries.Count == 0)
        {
            throw new InvalidDataException(
                "O snapshot de registro não contém nenhuma alteração para restaurar.");
        }

        var currentMutations = GetMutations();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Entries)
        {
            var key = CanonicalAddress(entry.Address);
            if (!seen.Add(key)
                || !IsAllowedRollbackEntry(
                    entry.Address,
                    entry.PreviousValue,
                    entry.AppliedValue,
                    currentMutations))
            {
                throw new InvalidDataException(
                    "O snapshot de registro contém um endereço ou valor fora da allowlist desta ação.");
            }
        }
    }

    private static string CanonicalAddress(RegistryAddress address)
    {
        return $"{(int)address.Hive}|{address.SubKey.Trim('\\')}|{address.ValueName}";
    }

    private void RestoreEntries(
        IEnumerable<RegistryMutationSnapshotEntry> entries,
        bool requireAppliedValue,
        WindowsActionContext context)
    {
        var orderedEntries = entries.Reverse().ToArray();
        if (requireAppliedValue)
        {
            var conflicts = orderedEntries
                .Where(entry => !Equivalent(Registry.Read(entry.Address), entry.AppliedValue))
                .Select(entry => entry.Address)
                .ToArray();
            if (conflicts.Length > 0)
            {
                throw new IOException(
                    $"Rollback recusou sobrescrever {conflicts.Length} valor(es) de registro alterado(s) depois da otimização.");
            }
        }

        var failures = new List<Exception>();
        var restorationAttempts = new List<RegistryMutationSnapshotEntry>();
        foreach (var entry in orderedEntries)
        {
            try
            {
                ValidateMutationSafety(context);
                var current = Registry.Read(entry.Address);
                if (!requireAppliedValue && Equivalent(current, entry.PreviousValue))
                {
                    continue;
                }

                if (!Equivalent(current, entry.AppliedValue))
                {
                    throw new IOException(
                        $"A restauração recusou sobrescrever '{entry.Address.ValueName}' porque o valor mudou depois da tentativa de aplicação.");
                }

                if (requireAppliedValue)
                {
                    restorationAttempts.Add(entry);
                }

                if (entry.PreviousValue.Exists)
                {
                    Registry.Write(entry.Address, entry.PreviousValue);
                }
                else
                {
                    Registry.Delete(entry.Address);
                }

                if (!Equivalent(Registry.Read(entry.Address), entry.PreviousValue))
                {
                    throw new IOException(
                        $"O Windows não confirmou a restauração de '{entry.Address.ValueName}'.");
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                if (requireAppliedValue)
                {
                    failures.AddRange(CompensateRestoredEntries(restorationAttempts, context));
                    break;
                }
            }
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Uma ou mais configurações de registro não puderam ser restauradas.",
                failures);
        }
    }

    private IReadOnlyList<Exception> CompensateRestoredEntries(
        IEnumerable<RegistryMutationSnapshotEntry> restoredEntries,
        WindowsActionContext context)
    {
        var failures = new List<Exception>();
        foreach (var entry in restoredEntries.Reverse())
        {
            try
            {
                ValidateMutationSafety(context with { IsImmediateFailureRecovery = true });
                var current = Registry.Read(entry.Address);
                if (Equivalent(current, entry.AppliedValue))
                {
                    continue;
                }

                if (!Equivalent(current, entry.PreviousValue))
                {
                    throw new IOException(
                        $"A compensação recusou sobrescrever '{entry.Address.ValueName}' porque o valor mudou durante o rollback.");
                }

                Registry.Write(entry.Address, entry.AppliedValue);
                if (!Equivalent(Registry.Read(entry.Address), entry.AppliedValue))
                {
                    throw new IOException(
                        $"O Windows não confirmou a compensação de '{entry.Address.ValueName}'.");
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    protected static bool Equivalent(RegistryValueState left, RegistryValueState right)
    {
        return left.Exists == right.Exists
            && left.Kind == right.Kind
            && string.Equals(left.StringValue, right.StringValue, StringComparison.Ordinal)
            && left.NumericValue == right.NumericValue
            && string.Equals(left.BinaryBase64Value, right.BinaryBase64Value, StringComparison.Ordinal)
            && SequenceEqual(left.MultiStringValue, right.MultiStringValue);
    }

    protected static bool IsMissingOrDwordBoolean(RegistryValueState value)
    {
        return !value.Exists
            || (value.Kind == RegistryValueKind.DWord
                && value.NumericValue is 0 or 1);
    }

    private static bool SequenceEqual(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && left.SequenceEqual(right, StringComparer.Ordinal);
    }
}

/// <summary>
/// Base das configurações de jogo do Windows que o Ralven alterna em um único
/// valor DWORD booleano do HKCU do próprio usuário, sempre com o FiveM parado.
/// As regras que precisam valer igualmente para todas elas — recusar um valor
/// existente fora de 0/1, exigir o jogo fechado (exceto na recuperação
/// imediata de falha) e limitar o rollback a um estado ausente ou booleano —
/// ficam aqui para que uma ação nova não possa nascer com apenas parte delas.
/// </summary>
public abstract class GameBooleanRegistryAction : AllowlistedRegistryAction
{
    private readonly IFiveMProcessInspector processInspector;
    private readonly string? installationRoot;
    private readonly RegistryAddress address;
    private readonly int desiredValue;
    private readonly string unsupportedValueMessage;

    protected GameBooleanRegistryAction(
        IRegistryStore registry,
        IFiveMProcessInspector processInspector,
        string? installationRoot,
        RegistryAddress address,
        int desiredValue,
        string unsupportedValueMessage)
        : base(registry)
    {
        this.processInspector = processInspector
            ?? throw new ArgumentNullException(nameof(processInspector));
        this.installationRoot = installationRoot;
        this.address = address;
        this.desiredValue = desiredValue;
        this.unsupportedValueMessage = unsupportedValueMessage;
    }

    protected override IReadOnlyList<RegistryMutation> GetMutations()
    {
        return [new RegistryMutation(address, RegistryValueState.FromDword(desiredValue))];
    }

    protected override void ValidateCurrentValueForApply(
        RegistryMutation mutation,
        RegistryValueState currentValue)
    {
        if (currentValue.Exists && !IsMissingOrDwordBoolean(currentValue))
        {
            throw new InvalidDataException(unsupportedValueMessage);
        }
    }

    protected override void ValidateMutationSafety(WindowsActionContext context)
    {
        if (!context.IsImmediateFailureRecovery)
        {
            EnsureFiveMStopped(processInspector, installationRoot);
        }
    }

    protected override bool IsAllowedRollbackEntry(
        RegistryAddress address,
        RegistryValueState previousValue,
        RegistryValueState appliedValue,
        IReadOnlyList<RegistryMutation> currentMutations)
    {
        return IsMissingOrDwordBoolean(previousValue)
            && base.IsAllowedRollbackEntry(
                address,
                previousValue,
                appliedValue,
                currentMutations);
    }
}

public sealed class GameModeRegistryAction : GameBooleanRegistryAction
{
    internal static readonly RegistryAddress Address = new(
        RegistryHive.CurrentUser,
        @"Software\Microsoft\GameBar",
        "AutoGameModeEnabled");

    public GameModeRegistryAction(
        IRegistryStore registry,
        IFiveMProcessInspector processInspector,
        string? installationRoot = null)
        : base(
            registry,
            processInspector,
            installationRoot,
            Address,
            desiredValue: 1,
            "Game Mode has an unsupported registry value and will not be overwritten.")
    {
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.EnableGameMode);
}

public sealed class GameDvrRegistryAction : GameBooleanRegistryAction
{
    internal static readonly RegistryAddress HistoricalCaptureAddress = new(
        RegistryHive.CurrentUser,
        @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
        "HistoricalCaptureEnabled");

    public GameDvrRegistryAction(
        IRegistryStore registry,
        IFiveMProcessInspector processInspector,
        string? installationRoot = null)
        : base(
            registry,
            processInspector,
            installationRoot,
            HistoricalCaptureAddress,
            desiredValue: 0,
            "Historical capture has an unsupported registry value and will not be overwritten.")
    {
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.DisableBackgroundCapture);
}

public sealed class GpuPreferenceRegistryAction : AllowlistedRegistryAction
{
    private readonly IReadOnlyList<RegistryAddress> addresses;
    private readonly string fiveMExecutable;
    private readonly string fiveMRuntimeDirectory;

    public GpuPreferenceRegistryAction(
        IRegistryStore registry,
        string fiveMExecutablePath,
        string fiveMInstallationRoot)
        : base(registry)
    {
        fiveMExecutable = Path.GetFullPath(fiveMExecutablePath);
        if (!Path.GetExtension(fiveMExecutable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "GPU preference target must be an executable.",
                nameof(fiveMExecutablePath));
        }

        _ = SafePath.EnsureDescendant(fiveMInstallationRoot, fiveMExecutable);
        fiveMRuntimeDirectory = RuntimeSubprocessRoot(fiveMInstallationRoot);
        var targets = new List<string> { fiveMExecutable };
        AddFiveMRuntimeTargets(targets, fiveMInstallationRoot);
        addresses = targets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(target => new RegistryAddress(
                RegistryHive.CurrentUser,
                @"Software\Microsoft\DirectX\UserGpuPreferences",
                target))
            .ToArray();
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.PreferHighPerformanceGpu);

    protected override IReadOnlyList<RegistryMutation> GetMutations()
    {
        return addresses
            .Select(address => new RegistryMutation(
                address,
                RegistryValueState.FromString("GpuPreference=2;")))
            .ToArray();
    }

    protected override RegistryValueState? ResolveDesiredValue(
        RegistryMutation mutation,
        RegistryValueState previousValue)
    {
        return BuildGpuPreferenceValue(previousValue) is { } desired
            ? RegistryValueState.FromString(desired)
            : null;
    }

    protected override bool IsAllowedRollbackEntry(
        RegistryAddress address,
        RegistryValueState previousValue,
        RegistryValueState appliedValue,
        IReadOnlyList<RegistryMutation> currentMutations)
    {
        if (address.Hive != RegistryHive.CurrentUser
            || !address.SubKey.Equals(
                @"Software\Microsoft\DirectX\UserGpuPreferences",
                StringComparison.OrdinalIgnoreCase)
            || BuildGpuPreferenceValue(previousValue) is not { } expectedAppliedValue
            || !Equivalent(
                appliedValue,
                RegistryValueState.FromString(expectedAppliedValue)))
        {
            return false;
        }

        string target;
        try
        {
            if (!Path.IsPathFullyQualified(address.ValueName))
            {
                return false;
            }

            target = Path.GetFullPath(address.ValueName);
            if (!target.Equals(address.ValueName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }

        if (target.Equals(fiveMExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(target);
        var parent = Path.GetDirectoryName(target);
        if (parent?.Equals(fiveMRuntimeDirectory, StringComparison.OrdinalIgnoreCase) == true
            && IsKnownFiveMRendererName(fileName))
        {
            return true;
        }

        return false;
    }

    private static string? BuildGpuPreferenceValue(RegistryValueState current)
    {
        if (!current.Exists)
        {
            return "GpuPreference=2;";
        }

        if (current.Kind != RegistryValueKind.String
            || string.IsNullOrWhiteSpace(current.StringValue)
            || current.StringValue.IndexOfAny(['\r', '\n']) >= 0)
        {
            return null;
        }

        var output = new List<string>();
        var foundPreference = false;
        foreach (var rawSegment in current.StringValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Trim();
            var separator = segment.IndexOf('=');
            if (separator <= 0
                || separator == segment.Length - 1
                || segment.IndexOf('=', separator + 1) >= 0)
            {
                return null;
            }

            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (key.Length is 0 or > 64
                || value.Length is 0 or > 128
                || key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
            {
                return null;
            }

            if (key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase))
            {
                if (foundPreference)
                {
                    return null;
                }

                output.Add("GpuPreference=2");
                foundPreference = true;
            }
            else
            {
                output.Add($"{key}={value}");
            }
        }

        if (!foundPreference)
        {
            output.Add("GpuPreference=2");
        }

        return string.Join(';', output) + ";";
    }

    /// <summary>
    /// Pasta dos subprocessos do runtime do FiveM, composta em dois pontos
    /// deste arquivo a partir dos mesmos quatro segmentos.
    /// </summary>
    private static string RuntimeSubprocessRoot(string installationRoot) => Path.Combine(
        SafePath.Normalize(installationRoot),
        "FiveM.app",
        "data",
        "cache",
        "subprocess");

    private static void AddFiveMRuntimeTargets(ICollection<string> targets, string installationRoot)
    {
        var normalizedRoot = SafePath.Normalize(installationRoot);
        var searchRoot = RuntimeSubprocessRoot(normalizedRoot);
        try
        {
            if (!Directory.Exists(searchRoot)
                || HasReparsePointInRuntimePath(normalizedRoot, searchRoot))
            {
                return;
            }

            foreach (var candidate in Directory
                         .EnumerateFiles(searchRoot, "FiveM*_GTAProcess.exe", SearchOption.TopDirectoryOnly)
                         .Take(64))
            {
                if ((new FileInfo(candidate).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var fileName = Path.GetFileName(candidate);
                if (IsKnownFiveMRendererName(fileName))
                {
                    targets.Add(SafePath.EnsureDescendant(installationRoot, candidate));
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            // The stable FiveM launcher target is still applied when runtime discovery is unavailable.
        }
    }

    private static bool IsKnownFiveMRendererName(string fileName)
    {
        if (fileName.Equals("FiveM_GTAProcess.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.StartsWith("FiveM_b", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith("_GTAProcess.exe", StringComparison.OrdinalIgnoreCase)
            && fileName[7..^15].Length > 0
            && fileName[7..^15].All(char.IsAsciiDigit);
    }

    private static bool HasReparsePointInRuntimePath(string installationRoot, string searchRoot)
    {
        var current = new DirectoryInfo(searchRoot);
        var normalizedInstallationRoot = SafePath.Normalize(installationRoot);
        while (current is not null
               && current.FullName.StartsWith(
                   normalizedInstallationRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }
}

/// <summary>
/// Toggles Hardware-Accelerated GPU Scheduling (HAGS) between its default
/// (1) and enabled (2) states, always flipping to whichever the machine is
/// NOT currently using -- this is the "test on/off" experiment from the
/// graphics optimizations backlog (see docs/graphics-optimizations-backlog.md),
/// never a one-directional "always enable". Writing
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode</c>
/// requires elevation on virtually every machine and therefore always runs
/// through the typed broker. Requires a Windows restart to take effect --
/// reported via <see cref="ActionMetadataDto.RequiresRestart"/>, never
/// silently assumed to apply immediately.
/// </summary>
public sealed class HagsToggleAction : AllowlistedRegistryAction
{
    private const int DisabledValue = 1;
    private const int EnabledValue = 2;

    private static readonly RegistryAddress Address = new(
        RegistryHive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
        "HwSchMode");

    public HagsToggleAction(IRegistryStore registry)
        : base(registry)
    {
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.ToggleHags);

    protected override IReadOnlyList<RegistryMutation> GetMutations()
    {
        // The desired value here is only a placeholder; ResolveDesiredValue
        // below decides the real target based on the current state.
        return [new RegistryMutation(Address, RegistryValueState.FromDword(EnabledValue))];
    }

    protected override RegistryValueState? ResolveDesiredValue(
        RegistryMutation mutation,
        RegistryValueState previousValue)
    {
        var current = previousValue.Exists && previousValue.Kind == RegistryValueKind.DWord
            ? previousValue.NumericValue ?? DisabledValue
            : DisabledValue;
        var flipped = current == EnabledValue ? DisabledValue : EnabledValue;
        return RegistryValueState.FromDword((int)flipped);
    }

    protected override void ValidateCurrentValueForApply(
        RegistryMutation mutation,
        RegistryValueState currentValue)
    {
        if (!IsSupportedState(currentValue))
        {
            throw new InvalidDataException(
                "HAGS has an unsupported registry value and will not be overwritten.");
        }
    }

    protected override bool IsAllowedRollbackEntry(
        RegistryAddress address,
        RegistryValueState previousValue,
        RegistryValueState appliedValue,
        IReadOnlyList<RegistryMutation> currentMutations)
    {
        return IsSupportedState(previousValue)
            && base.IsAllowedRollbackEntry(
                address,
                previousValue,
                appliedValue,
                currentMutations);
    }

    private static bool IsSupportedState(RegistryValueState value)
    {
        return !value.Exists
            || (value.Kind == RegistryValueKind.DWord
                && value.NumericValue is DisabledValue or EnabledValue);
    }
}

/// <summary>
/// Toggles the "Disable fullscreen optimizations" compatibility flag for
/// FiveM and (when detected) standalone GTA V, per
/// <c>HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers</c>
/// -- the same per-user, per-executable registry location the
/// Compatibility tab of an .exe's Properties dialog writes to. This is a
/// community-documented convention, not an officially published Microsoft
/// API, so the action is always fully reversible (the exact previous
/// string is restored byte-for-byte on rollback) regardless of whether the
/// flag format assumption holds on a given Windows build. Existing,
/// unrelated compatibility flags for the same executable are preserved.
/// </summary>
public sealed class FullscreenOptimizationsRegistryAction : AllowlistedRegistryAction
{
    private const string DisableFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
    private static readonly RegistryHive Hive = RegistryHive.CurrentUser;
    private static readonly string SubKeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    private readonly IReadOnlyList<RegistryAddress> addresses;

    public FullscreenOptimizationsRegistryAction(
        IRegistryStore registry,
        string fiveMExecutablePath,
        string? gtaVExecutablePath)
        : base(registry)
    {
        var targets = new List<string> { ValidateExecutable(fiveMExecutablePath, nameof(fiveMExecutablePath)) };
        if (!string.IsNullOrWhiteSpace(gtaVExecutablePath))
        {
            targets.Add(ValidateExecutable(gtaVExecutablePath, nameof(gtaVExecutablePath)));
        }

        addresses = targets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(target => new RegistryAddress(Hive, SubKeyPath, target))
            .ToArray();
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.ToggleFullscreenOptimizations);

    protected override IReadOnlyList<RegistryMutation> GetMutations()
    {
        return addresses
            .Select(address => new RegistryMutation(address, RegistryValueState.FromString(DisableFlag)))
            .ToArray();
    }

    protected override RegistryValueState? ResolveDesiredValue(
        RegistryMutation mutation,
        RegistryValueState previousValue)
    {
        return ToggleFlag(previousValue) is { } desired
            ? RegistryValueState.FromString(desired)
            : null;
    }

    /// <summary>
    /// Adds the disable-fullscreen-optimizations flag if absent, or removes
    /// it if present -- this is the "toggle on/off" behavior the
    /// experiment needs, always preserving every other flag token already
    /// set for that executable. Returns null when the existing value's
    /// shape is not a plain space-separated token list this action
    /// recognizes (never guesses at a format it cannot safely round-trip).
    /// </summary>
    private static string? ToggleFlag(RegistryValueState current)
    {
        if (!current.Exists)
        {
            return DisableFlag;
        }

        if (current.Kind != RegistryValueKind.String
            || current.StringValue is null
            || (current.StringValue.Length > 0
                && string.IsNullOrWhiteSpace(current.StringValue))
            || current.StringValue.IndexOfAny(['\r', '\n']) >= 0)
        {
            return null;
        }

        var tokens = current.StringValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        foreach (var token in tokens)
        {
            if (token.Length > 64 || !token.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '~' or '_' or '.'))
            {
                return null;
            }
        }

        if (tokens.RemoveAll(token => token.Equals(DisableFlag, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            tokens.Add(DisableFlag);
        }

        // Empty is a legitimate outcome here: it means the disable flag was
        // the only token present and this call is toggling it back off.
        return tokens.Count == 0 ? string.Empty : string.Join(' ', tokens);
    }

    private static string ValidateExecutable(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Target must be an executable.", parameterName);
        }

        return fullPath;
    }
}
