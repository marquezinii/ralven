using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Windows.Infrastructure;

namespace Ralven.Windows.Actions;

internal sealed record PowerPlanSnapshot(Guid PreviousScheme, Guid AppliedScheme);

public interface IPowerStatusProvider
{
    bool IsOnAcPower();

    /// <summary>
    /// True when Windows Battery Saver is currently active -- read from the
    /// same <c>GetSystemPowerStatus</c> call as <see cref="IsOnAcPower"/>,
    /// documented by Microsoft as bit 0 of <c>SystemStatusFlag</c>.
    /// </summary>
    bool IsBatterySaverActive();
}

public sealed class WindowsPowerStatusProvider : IPowerStatusProvider
{
    public bool IsOnAcPower() => GetStatus().AcLineStatus == 1;

    public bool IsBatterySaverActive() => (GetStatus().SystemStatusFlag & 1) == 1;

    private static SystemPowerStatus GetStatus()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
}

/// <summary>
/// Result of attempting to switch to the high-performance power scheme.
/// Distinguishes "Windows genuinely refused because of a permission/policy
/// restriction" from "this computer simply doesn't expose that scheme" --
/// only the former should ever prompt for elevation.
/// </summary>
public enum PowerPlanActivationOutcome
{
    Activated,
    AccessDenied,
    SchemeUnavailable,
    Failed
}

public sealed record PciExpressAspmPolicy(int AcPolicy, int DcPolicy);

public sealed record PciExpressAspmState(Guid SchemeId, PciExpressAspmPolicy Policy);

public interface IPowerPlanController
{
    Guid PerformanceSchemeId { get; }

    Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken);

    Task<PowerPlanActivationOutcome> TryActivatePerformanceSchemeAsync(CancellationToken cancellationToken);

    Task ActivateSchemeAsync(Guid schemeId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the PCI Express Link State Power Management policy of the
    /// active scheme (0 = Off, 1 = Moderate, 2 = Maximum power savings).
    /// Null when the setting is not exposed on this machine (some
    /// motherboards/chipsets do not surface it).
    /// </summary>
    Task<PciExpressAspmState?> GetPciExpressAspmPolicyAsync(CancellationToken cancellationToken);

    Task SetPciExpressAspmPolicyAsync(
        Guid schemeId,
        PciExpressAspmPolicy expectedCurrent,
        PciExpressAspmPolicy desired,
        CancellationToken cancellationToken);
}

public sealed partial class PowerCfgController : IPowerPlanController
{
    private static readonly Guid PerformanceScheme =
        new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private readonly ICommandRunner commandRunner;
    private readonly string powerCfgPath;
    private readonly Func<Guid, PciExpressAspmPolicy?> readAspmPolicy;

    public PowerCfgController(ICommandRunner commandRunner)
        : this(commandRunner, ReadAspmPolicy)
    {
    }

    internal PowerCfgController(
        ICommandRunner commandRunner,
        Func<Guid, PciExpressAspmPolicy?> readAspmPolicy)
    {
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.readAspmPolicy = readAspmPolicy ?? throw new ArgumentNullException(nameof(readAspmPolicy));
        powerCfgPath = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "powercfg.exe"));
        if (!File.Exists(powerCfgPath))
        {
            throw new FileNotFoundException("The Windows powercfg executable was not found.", powerCfgPath);
        }
    }

    public Guid PerformanceSchemeId => PerformanceScheme;

    public async Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            powerCfgPath,
            ["/GETACTIVESCHEME"],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"powercfg failed while reading the active scheme (exit {result.ExitCode}).");
        }

        var match = PowerSchemeGuidRegex().Match(result.StandardOutput);
        return match.Success && Guid.TryParse(match.Value, out var scheme)
            ? scheme
            : throw new InvalidOperationException("powercfg did not return a valid active scheme GUID.");
    }

    // ERROR_ACCESS_DENIED. powercfg.exe surfaces this both as this exit code
    // and (locale-dependently) in stderr text, so the exit code is checked
    // first as the reliable signal and the text patterns are a fallback for
    // older/odd builds that don't set it.
    private const int AccessDeniedExitCode = 5;

    public async Task<PowerPlanActivationOutcome> TryActivatePerformanceSchemeAsync(
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            powerCfgPath,
            ["/SETACTIVE", "SCHEME_MIN"],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return PowerPlanActivationOutcome.Activated;
        }

        if (LooksLikeAccessDenied(result))
        {
            return PowerPlanActivationOutcome.AccessDenied;
        }

        return result.ExitCode == 2
            ? PowerPlanActivationOutcome.SchemeUnavailable
            : PowerPlanActivationOutcome.Failed;
    }

    private static bool LooksLikeAccessDenied(CommandResult result)
    {
        if (result.ExitCode == AccessDeniedExitCode)
        {
            return true;
        }

        var text = result.StandardError + result.StandardOutput;
        return text.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("acesso negado", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ActivateSchemeAsync(
        Guid schemeId,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            powerCfgPath,
            ["/SETACTIVE", schemeId.ToString("D")],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"powercfg failed while restoring scheme {schemeId:D} (exit {result.ExitCode}).");
        }
    }

    // SUB_PCIEXPRESS / ASPM_POLICY, documented Windows power setting GUIDs.
    private const string PciExpressSubgroupGuid = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string AspmPolicySettingGuid = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    public async Task<PciExpressAspmState?> GetPciExpressAspmPolicyAsync(
        CancellationToken cancellationToken)
    {
        var activeScheme = await GetActiveSchemeAsync(cancellationToken).ConfigureAwait(false);
        var policy = readAspmPolicy(activeScheme);
        return policy is null ? null : new PciExpressAspmState(activeScheme, policy);
    }

    private static PciExpressAspmPolicy? ReadAspmPolicy(Guid activeScheme)
    {
        var subgroup = new Guid(PciExpressSubgroupGuid);
        var setting = new Guid(AspmPolicySettingGuid);
        var acStatus = PowerReadACValueIndex(
            IntPtr.Zero,
            in activeScheme,
            in subgroup,
            in setting,
            out var acValue);
        var dcStatus = PowerReadDCValueIndex(
            IntPtr.Zero,
            in activeScheme,
            in subgroup,
            in setting,
            out var dcValue);

        const int SettingNotFound = 2;
        if (acStatus != 0 && acStatus != SettingNotFound)
        {
            throw new Win32Exception(acStatus, "Windows failed while reading the AC PCI Express policy.");
        }

        if (dcStatus != 0 && dcStatus != SettingNotFound)
        {
            throw new Win32Exception(dcStatus, "Windows failed while reading the DC PCI Express policy.");
        }

        if (acStatus == SettingNotFound || dcStatus == SettingNotFound)
        {
            return null;
        }

        if (!IsValidAspmPolicy(acValue) || !IsValidAspmPolicy(dcValue))
        {
            throw new InvalidDataException("Windows returned an unsupported PCI Express policy value.");
        }

        return new PciExpressAspmPolicy(acValue, dcValue);
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern int PowerReadACValueIndex(
        IntPtr rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupOfPowerSettings,
        in Guid powerSetting,
        out int acValueIndex);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern int PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupOfPowerSettings,
        in Guid powerSetting,
        out int dcValueIndex);

    public async Task SetPciExpressAspmPolicyAsync(
        Guid schemeId,
        PciExpressAspmPolicy expectedCurrent,
        PciExpressAspmPolicy desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedCurrent);
        ArgumentNullException.ThrowIfNull(desired);
        if (schemeId == Guid.Empty)
        {
            throw new ArgumentException("The power scheme GUID cannot be empty.", nameof(schemeId));
        }

        ValidateAspmPolicy(expectedCurrent);
        ValidateAspmPolicy(desired);

        var activeScheme = await GetActiveSchemeAsync(cancellationToken).ConfigureAwait(false);
        var current = readAspmPolicy(schemeId);
        if (activeScheme != schemeId || current != expectedCurrent)
        {
            throw new IOException("The active power scheme or PCI Express policy changed before it could be updated.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await SetAspmPolicyRawAsync(schemeId, desired, CancellationToken.None).ConfigureAwait(false);
            var appliedScheme = await GetActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
            var applied = readAspmPolicy(schemeId);
            if (appliedScheme != schemeId || applied != desired)
            {
                throw new IOException("Windows did not apply the requested PCI Express power management values.");
            }
        }
        catch (Exception applyException)
        {
            try
            {
                await CompensateAspmPolicyAsync(schemeId, expectedCurrent, desired).ConfigureAwait(false);
            }
            catch (Exception restoreException)
            {
                throw new AggregateException(
                    "Applying and restoring PCI Express power management both failed.",
                    applyException,
                    restoreException);
            }

            throw;
        }
    }

    private async Task SetAspmPolicyRawAsync(
        Guid schemeId,
        PciExpressAspmPolicy policy,
        CancellationToken cancellationToken)
    {
        await EnsureActiveSchemeAsync(schemeId, cancellationToken).ConfigureAwait(false);
        var apply = await commandRunner.RunAsync(
            powerCfgPath,
            ["/setacvalueindex", schemeId.ToString("D"), PciExpressSubgroupGuid, AspmPolicySettingGuid,
                policy.AcPolicy.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!apply.Succeeded)
        {
            throw new InvalidOperationException("powercfg failed while updating the AC PCI Express policy.");
        }

        await EnsureActiveSchemeAsync(schemeId, cancellationToken).ConfigureAwait(false);
        apply = await commandRunner.RunAsync(
            powerCfgPath,
            ["/setdcvalueindex", schemeId.ToString("D"), PciExpressSubgroupGuid, AspmPolicySettingGuid,
                policy.DcPolicy.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!apply.Succeeded)
        {
            throw new InvalidOperationException("powercfg failed while updating the DC PCI Express policy.");
        }

        await EnsureActiveSchemeAsync(schemeId, cancellationToken).ConfigureAwait(false);
        apply = await commandRunner.RunAsync(
            powerCfgPath,
            ["/S", schemeId.ToString("D")],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!apply.Succeeded)
        {
            throw new InvalidOperationException("powercfg failed while applying the PCI Express policy.");
        }
    }

    private async Task CompensateAspmPolicyAsync(
        Guid schemeId,
        PciExpressAspmPolicy previous,
        PciExpressAspmPolicy attempted)
    {
        var activeScheme = await GetActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
        var current = readAspmPolicy(schemeId)
            ?? throw new IOException("The PCI Express policy could not be read before compensation.");
        var restoreAc = current.AcPolicy == attempted.AcPolicy && current.AcPolicy != previous.AcPolicy;
        var restoreDc = current.DcPolicy == attempted.DcPolicy && current.DcPolicy != previous.DcPolicy;
        var acConflict = current.AcPolicy != previous.AcPolicy && !restoreAc;
        var dcConflict = current.DcPolicy != previous.DcPolicy && !restoreDc;

        if (restoreAc)
        {
            await SetAspmValueAsync("/setacvalueindex", schemeId, previous.AcPolicy).ConfigureAwait(false);
        }

        if (restoreDc)
        {
            await SetAspmValueAsync("/setdcvalueindex", schemeId, previous.DcPolicy).ConfigureAwait(false);
        }

        if ((restoreAc || restoreDc) && activeScheme == schemeId)
        {
            await ApplySchemeAsync(schemeId).ConfigureAwait(false);
        }

        var restored = readAspmPolicy(schemeId);
        if (acConflict || dcConflict || restored != previous)
        {
            throw new IOException("PCI Express power management changed concurrently; compensation preserved newer values.");
        }
    }

    private async Task EnsureActiveSchemeAsync(Guid expected, CancellationToken cancellationToken)
    {
        if (await GetActiveSchemeAsync(cancellationToken).ConfigureAwait(false) != expected)
        {
            throw new IOException("The active power scheme changed during the PCI Express update.");
        }
    }

    private async Task SetAspmValueAsync(string operation, Guid schemeId, int value)
    {
        var result = await commandRunner.RunAsync(
            powerCfgPath,
            [operation, schemeId.ToString("D"), PciExpressSubgroupGuid, AspmPolicySettingGuid,
                value.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("powercfg failed while compensating the PCI Express policy.");
        }
    }

    private async Task ApplySchemeAsync(Guid schemeId)
    {
        var result = await commandRunner.RunAsync(
            powerCfgPath,
            ["/S", schemeId.ToString("D")],
            TimeSpan.FromSeconds(10),
            CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("powercfg failed while applying the compensated PCI Express policy.");
        }
    }

    private static void ValidateAspmPolicy(PciExpressAspmPolicy policy)
    {
        if (!IsValidAspmPolicy(policy.AcPolicy) || !IsValidAspmPolicy(policy.DcPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }

    private static bool IsValidAspmPolicy(int value) => value is >= 0 and <= 2;

    [GeneratedRegex(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant)]
    private static partial Regex PowerSchemeGuidRegex();
}

public sealed class SessionPerformancePowerPlanAction : WindowsOptimizationAction
{
    private readonly IPowerPlanController controller;
    private readonly IPowerStatusProvider powerStatus;

    public SessionPerformancePowerPlanAction(
        IPowerPlanController controller,
        IPowerStatusProvider powerStatus)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.powerStatus = powerStatus ?? throw new ArgumentNullException(nameof(powerStatus));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.EnableSessionPerformancePowerPlan);

    public override async Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        if (!powerStatus.IsOnAcPower())
        {
            return WindowsActionApplyResult.Skipped(
                WindowsActionText.Format("ActionResults.PerformancePowerPlan.OnBattery"));
        }

        var previous = await controller.GetActiveSchemeAsync(cancellationToken).ConfigureAwait(false);
        if (previous == controller.PerformanceSchemeId)
        {
            return WindowsActionApplyResult.NoChange(
                WindowsActionText.Format("ActionResults.PerformancePowerPlan.AlreadyActive"));
        }

        if (!context.IsElevated)
        {
            // Standard-user execution is a read-only preflight. A change still
            // goes through the broker so its authoritative receipt is preserved.
            throw new UnauthorizedAccessException("O modo de energia da sessão requer elevação.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        PowerPlanActivationOutcome outcome;
        try
        {
            outcome = await controller.TryActivatePerformanceSchemeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception applyException)
        {
            await CompensateFailedActivationAsync(previous, applyException).ConfigureAwait(false);
            throw;
        }

        if (outcome != PowerPlanActivationOutcome.Activated)
        {
            await CompensateFailedActivationAsync(previous, applyException: null).ConfigureAwait(false);
        }

        if (outcome == PowerPlanActivationOutcome.AccessDenied)
        {
            throw new UnauthorizedAccessException(
                "O Windows recusou a troca do plano de energia mesmo com privilégios administrativos.");
        }

        if (outcome == PowerPlanActivationOutcome.SchemeUnavailable)
        {
            return WindowsActionApplyResult.Skipped(
                WindowsActionText.Format("ActionResults.PerformancePowerPlan.Unavailable"));
        }

        if (outcome == PowerPlanActivationOutcome.Failed)
        {
            throw new InvalidOperationException("Windows failed while activating the performance power plan.");
        }

        Guid applied;
        try
        {
            applied = await controller.GetActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception applyException)
        {
            try
            {
                await RestoreSchemeAndVerifyAsync(previous).ConfigureAwait(false);
            }
            catch (Exception restoreException)
            {
                throw new AggregateException(
                    "Applying and restoring the performance power plan both failed.",
                    applyException,
                    restoreException);
            }

            throw;
        }

        if (applied == previous)
        {
            return WindowsActionApplyResult.NoChange(
                WindowsActionText.Format("ActionResults.PerformancePowerPlan.AlreadyActive"));
        }

        return WindowsActionApplyResult.ChangedWith(
            new PowerPlanSnapshot(previous, applied),
            WindowsActionText.Format("ActionResults.PerformancePowerPlan.Applied"));
    }

    public override async Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        if (!context.IsElevated)
        {
            throw new UnauthorizedAccessException("Restaurar o plano de energia requer elevação.");
        }

        var snapshot = WindowsActionSnapshot.Deserialize<PowerPlanSnapshot>(snapshotJson);
        if (snapshot.PreviousScheme == Guid.Empty
            || snapshot.AppliedScheme == Guid.Empty
            || snapshot.PreviousScheme == snapshot.AppliedScheme)
        {
            throw new InvalidDataException("The power plan snapshot contains unsupported values.");
        }

        var current = await controller.GetActiveSchemeAsync(cancellationToken).ConfigureAwait(false);
        if (current != snapshot.AppliedScheme)
        {
            throw new IOException(
                "O plano de energia mudou depois da otimização; o rollback preservou a escolha mais recente.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await controller.ActivateSchemeAsync(snapshot.PreviousScheme, CancellationToken.None)
            .ConfigureAwait(false);
        var restored = await controller.GetActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
        if (restored != snapshot.PreviousScheme)
        {
            throw new IOException("Windows did not restore the previous power plan.");
        }
    }

    private async Task RestoreSchemeAndVerifyAsync(Guid scheme)
    {
        await controller.ActivateSchemeAsync(scheme, CancellationToken.None).ConfigureAwait(false);
        var restored = await controller.GetActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
        if (restored != scheme)
        {
            throw new IOException("Windows did not restore the previous power plan after apply failed.");
        }
    }

    private async Task CompensateFailedActivationAsync(Guid previous, Exception? applyException)
    {
        try
        {
            var current = await controller.GetActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
            if (current != previous)
            {
                await RestoreSchemeAndVerifyAsync(previous).ConfigureAwait(false);
            }
        }
        catch (Exception restoreException)
        {
            throw applyException is null
                ? new AggregateException("The failed power-plan activation could not be compensated.", restoreException)
                : new AggregateException(
                    "Applying and restoring the performance power plan both failed.",
                    applyException,
                    restoreException);
        }
    }
}

internal sealed record PciExpressAspmSnapshot(
    Guid SchemeId,
    PciExpressAspmPolicy Previous,
    PciExpressAspmPolicy Applied);

/// <summary>
/// Sets PCI Express Link State Power Management (ASPM) on the active power
/// scheme to "Off" (0) to reduce link-latency spikes during gaming,
/// documented and fully reversible via <c>powercfg /Q</c> and
/// <c>/set{a,d}cvalueindex</c> -- the same official mechanism
/// <see cref="SessionPerformancePowerPlanAction"/> already relies on for
/// scheme changes. Never touches any setting outside this one, documented
/// power-setting GUID pair.
/// </summary>
public sealed class PciExpressPowerManagementAction : WindowsOptimizationAction
{
    private static readonly PciExpressAspmPolicy OffPolicy = new(0, 0);

    private readonly IPowerPlanController controller;

    public PciExpressPowerManagementAction(IPowerPlanController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
        OptimizationActionIds.AdjustPciExpressPowerManagement);

    public override async Task<WindowsActionApplyResult> ApplyAsync(
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        var previous = await controller.GetPciExpressAspmPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return WindowsActionApplyResult.Skipped(
                WindowsActionText.Format("ActionResults.PciPower.Unavailable"));
        }

        if (previous.Policy == OffPolicy)
        {
            return WindowsActionApplyResult.NoChange(
                WindowsActionText.Format("ActionResults.PciPower.AlreadyOff"));
        }

        await controller.SetPciExpressAspmPolicyAsync(
                previous.SchemeId,
                previous.Policy,
                OffPolicy,
                cancellationToken)
            .ConfigureAwait(false);

        return WindowsActionApplyResult.ChangedWith(
            new PciExpressAspmSnapshot(previous.SchemeId, previous.Policy, OffPolicy),
            WindowsActionText.Format("ActionResults.PciPower.Applied"));
    }

    public override async Task RollbackAsync(
        WindowsActionContext context,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        PciExpressAspmSnapshot snapshot;
        try
        {
            snapshot = WindowsActionSnapshot.Deserialize<PciExpressAspmSnapshot>(snapshotJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The PCI Express snapshot does not prove the exact scheme and both AC/DC policies; rollback was refused.",
                exception);
        }
        if (snapshot.SchemeId == Guid.Empty
            || snapshot.Previous is null
            || snapshot.Applied != OffPolicy
            || snapshot.Previous == snapshot.Applied
            || !IsValidPolicy(snapshot.Previous))
        {
            throw new InvalidDataException("The PCI Express power management snapshot is outside the action allowlist.");
        }

        var current = await controller.GetPciExpressAspmPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || current.SchemeId != snapshot.SchemeId || current.Policy != snapshot.Applied)
        {
            throw new IOException(
                "PCI Express power management changed after optimization; rollback refused to overwrite newer settings.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await controller.SetPciExpressAspmPolicyAsync(
                snapshot.SchemeId,
                snapshot.Applied,
                snapshot.Previous,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static bool IsValidPolicy(PciExpressAspmPolicy policy) =>
        policy.AcPolicy is >= 0 and <= 2 && policy.DcPolicy is >= 0 and <= 2;
}
