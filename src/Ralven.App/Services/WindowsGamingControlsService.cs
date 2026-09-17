using System.IO;
using Ralven.Contracts;
using Ralven.Windows.Actions;
using Ralven.Windows.Engine;
using Ralven.Windows.Infrastructure;

namespace Ralven.App.Services;

public enum WindowsGamingControlsBlockReason
{
    None = 0,
    FiveMRunning = 1,
    ProcessInspectionUnavailable = 2
}

public sealed record WindowsGamingControlsResult(
    Guid TransactionId,
    bool Succeeded,
    bool Changed,
    WindowsGamingSettingsDto Settings,
    WindowsGamingControlsBlockReason BlockReason);

public sealed record WindowsGamingRestoreResult(
    bool Succeeded,
    WindowsGamingControlsBlockReason BlockReason);

public sealed class WindowsGamingControlsService
{
    private readonly bool demoMode;
    private readonly WindowsGamingSettingsInspector? inspector;
    private readonly WindowsTransactionEngine? engine;
    private readonly IFiveMProcessInspector? processInspector;
    private readonly IReadOnlyList<IWindowsOptimizationAction> actions = [];
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public WindowsGamingControlsService(bool demoMode = false)
    {
        this.demoMode = demoMode;
        if (demoMode)
        {
            return;
        }

        var registry = new WindowsRegistryStore();
        var fiveMProcessInspector = new WindowsFiveMProcessInspector();
        inspector = new WindowsGamingSettingsInspector(registry);
        processInspector = fiveMProcessInspector;
        actions = CreateActions(registry, fiveMProcessInspector);
        var journalStore = new JsonWindowsTransactionJournalStore(AppDataPaths.Combine("Transactions"));
        engine = new WindowsTransactionEngine(
            new WindowsActionCatalog(actions),
            journalStore);
    }

    internal WindowsGamingControlsService(
        IRegistryStore registry,
        IWindowsTransactionJournalStore journalStore,
        IFiveMProcessInspector processInspector)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(processInspector);
        inspector = new WindowsGamingSettingsInspector(registry);
        this.processInspector = processInspector;
        actions = CreateActions(registry, processInspector);
        engine = new WindowsTransactionEngine(new WindowsActionCatalog(actions), journalStore);
    }

    public Task<WindowsGamingSettingsDto> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(demoMode
            ? new WindowsGamingSettingsDto(
                WindowsGamingSettingState.Enabled,
                WindowsGamingSettingState.Disabled)
            : inspector!.Inspect());
    }

    public WindowsGamingControlsBlockReason GetMutationBlockReason()
    {
        if (demoMode)
        {
            return WindowsGamingControlsBlockReason.None;
        }

        return GetMutationBlockReason(processInspector!);
    }

    internal static WindowsGamingControlsBlockReason GetMutationBlockReason(
        IFiveMProcessInspector processInspector)
    {
        ArgumentNullException.ThrowIfNull(processInspector);

        try
        {
            return processInspector.IsAnyRunning()
                ? WindowsGamingControlsBlockReason.FiveMRunning
                : WindowsGamingControlsBlockReason.None;
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return WindowsGamingControlsBlockReason.ProcessInspectionUnavailable;
        }
    }

    public async Task<WindowsGamingControlsResult> ApplyAsync(
        CancellationToken cancellationToken = default)
    {
        if (demoMode)
        {
            throw new InvalidOperationException("Demo mode cannot change Windows settings.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = inspector!.Inspect();
            if (!CanChange(before))
            {
                return new WindowsGamingControlsResult(
                    Guid.Empty,
                    false,
                    false,
                    before,
                    WindowsGamingControlsBlockReason.None);
            }

            var blockReason = GetMutationBlockReason();
            if (blockReason != WindowsGamingControlsBlockReason.None)
            {
                return new WindowsGamingControlsResult(
                    Guid.Empty,
                    false,
                    false,
                    before,
                    blockReason);
            }

            var transactionId = Guid.NewGuid();
            var result = await engine!.ExecuteAsync(
                actions,
                new WindowsActionContext
                {
                    TransactionId = transactionId,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    IsElevated = false
                },
                new WindowsTransactionOptions
                {
                    IncludeStandardUserActions = true,
                    IncludeAdministratorActions = false,
                    RollbackOnFailure = true
                },
                cancellationToken).ConfigureAwait(false);

            var changed = result.ChangedActionIds.Count > 0;
            var after = inspector.Inspect();
            if (result.State == TransactionState.Committed && IsDesired(after))
            {
                return new WindowsGamingControlsResult(
                    transactionId,
                    true,
                    changed,
                    after,
                    WindowsGamingControlsBlockReason.None);
            }

            if (result.State == TransactionState.Committed && changed)
            {
                await engine.RollbackAsync(
                    transactionId,
                    isElevated: false,
                    new WindowsRollbackOptions
                    {
                        IncludeStandardUserActions = true,
                        IncludeAdministratorActions = false
                    },
                    CancellationToken.None).ConfigureAwait(false);
                after = inspector.Inspect();
            }

            return new WindowsGamingControlsResult(
                transactionId,
                false,
                changed,
                after,
                GetMutationBlockReason());
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<WindowsGamingRestoreResult> RestoreAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        if (demoMode || transactionId == Guid.Empty)
        {
            return new WindowsGamingRestoreResult(
                false,
                WindowsGamingControlsBlockReason.None);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var blockReason = GetMutationBlockReason();
            if (blockReason != WindowsGamingControlsBlockReason.None)
            {
                return new WindowsGamingRestoreResult(false, blockReason);
            }

            var result = await engine!.RollbackAsync(
                transactionId,
                isElevated: false,
                new WindowsRollbackOptions
                {
                    IncludeStandardUserActions = true,
                    IncludeAdministratorActions = false
                },
                cancellationToken).ConfigureAwait(false);
            var succeeded = result.State == TransactionState.RolledBack;
            return new WindowsGamingRestoreResult(
                succeeded,
                succeeded
                    ? WindowsGamingControlsBlockReason.None
                    : GetMutationBlockReason());
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static IWindowsOptimizationAction[] CreateActions(
        IRegistryStore registry,
        IFiveMProcessInspector processInspector)
    {
        return
        [
            new GameModeRegistryAction(registry, processInspector),
            new GameDvrRegistryAction(registry, processInspector)
        ];
    }

    private static bool CanChange(WindowsGamingSettingsDto settings)
    {
        return settings.GameMode != WindowsGamingSettingState.Unavailable
            && settings.BackgroundCapture != WindowsGamingSettingState.Unavailable;
    }

    private static bool IsDesired(WindowsGamingSettingsDto settings)
    {
        return settings.GameMode == WindowsGamingSettingState.Enabled
            && settings.BackgroundCapture == WindowsGamingSettingState.Disabled;
    }
}
