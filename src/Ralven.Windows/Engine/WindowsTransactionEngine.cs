using Ralven.Contracts;
using Ralven.Windows.Actions;
using Ralven.Windows.Diagnostics;

namespace Ralven.Windows.Engine;

public sealed record WindowsTransactionOptions
{
    public bool IncludeStandardUserActions { get; init; } = true;

    public bool IncludeAdministratorActions { get; init; } = true;

    /// <summary>
    /// Strict mode only: when a genuine failure occurs, roll back every action
    /// already applied in this run and mark the whole transaction failed.
    /// Ignored when <see cref="IsolateFailures"/> is true.
    /// </summary>
    public bool RollbackOnFailure { get; init; } = true;

    /// <summary>
    /// When true, each action is executed as an isolated mini-transaction:
    /// verify → apply → commit, rolling back only itself on failure while
    /// unrelated safe actions keep running. Actions whose prerequisite did not
    /// succeed are skipped; a failed critical action aborts the remaining run.
    /// </summary>
    public bool IsolateFailures { get; init; }
}

public sealed record WindowsRollbackOptions
{
    /// <summary>
    /// Desfaz somente a fase de usuário padrão. É a opção usada por todo
    /// chamador que não dispõe do broker elevado, e era escrita por extenso em
    /// cada um deles. Seguro compartilhar: as propriedades são <c>init</c>.
    /// </summary>
    public static WindowsRollbackOptions StandardUserOnly { get; } = new()
    {
        IncludeStandardUserActions = true,
        IncludeAdministratorActions = false
    };

    public bool IncludeStandardUserActions { get; init; } = true;

    public bool IncludeAdministratorActions { get; init; } = true;
}

public sealed record WindowsTransactionResult
{
    public required Guid TransactionId { get; init; }

    public required TransactionState State { get; init; }

    public required IReadOnlyList<string> AppliedActionIds { get; init; }

    public required IReadOnlyList<string> ChangedActionIds { get; init; }

    public required IReadOnlyList<string> DeferredAdministratorActionIds { get; init; }

    public string? Error { get; init; }
}

public sealed class WindowsTransactionEngine
{
    private readonly WindowsActionCatalog catalog;
    private readonly IWindowsTransactionJournalStore journalStore;
    private readonly WindowsActionTextResolver actionText;
    private readonly SemaphoreSlim executionGate = new(1, 1);

    public WindowsTransactionEngine(
        WindowsActionCatalog catalog,
        IWindowsTransactionJournalStore journalStore,
        WindowsActionTextResolver? actionText = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        this.actionText = actionText
            ?? WindowsActionResources.ForCulture(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
    }

    public async Task<WindowsTransactionResult> ExecuteAsync(
        IEnumerable<IWindowsOptimizationAction> requestedActions,
        WindowsActionContext context,
        WindowsTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WindowsTransactionOptions();
        var actions = ValidateExecutionRequest(requestedActions, context);

        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var textScope = WindowsActionText.Use(actionText);
            var journal = await LoadOrCreateJournalAsync(actions, context, cancellationToken)
                .ConfigureAwait(false);
            if (await FinalizeInterruptedJournalAsync(journal).ConfigureAwait(false))
            {
                return CreateResult(
                    journal,
                    journal.Actions
                        .Where(entry => entry.State == ActionJournalState.Committed)
                        .Where(entry => entry.Outcome == ActionExecutionOutcome.Applied)
                        .Select(entry => entry.ActionId)
                        .ToArray(),
                    GetDeferredAdministratorIds(journal),
                    journal.Error);
            }

            if (journal.State == TransactionState.CommittedWithErrors
                && !(context.IsElevated
                    && options.IncludeAdministratorActions
                    && !options.IncludeStandardUserActions
                    && GetDeferredAdministratorIds(journal).Count > 0))
            {
                return CreateResult(journal, [], GetDeferredAdministratorIds(journal), journal.Error);
            }

            var selected = BuildSelectedActions(actions, journal, context, options);

            if (selected.Count == 0)
            {
                return await CompleteWithNoSelectedActionsAsync(journal, cancellationToken)
                    .ConfigureAwait(false);
            }

            journal.State = TransactionState.Applying;
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

            if (options.IsolateFailures)
            {
                try
                {
                    return await ExecuteIsolatedAsync(journal, selected, context, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Also finalize cancellation between actions, outside the
                    // individual action's compensation boundary.
                    await FinalizeCancelledIsolatedRunAsync(journal).ConfigureAwait(false);
                    throw;
                }
            }

            var applied = new List<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)>();
            try
            {
                await ApplyAndCommitAsync(journal, selected, applied, context, cancellationToken)
                    .ConfigureAwait(false);
                return CreateResult(
                    journal,
                    applied.Select(item => item.Action.Metadata.Id).ToArray(),
                    GetDeferredAdministratorIds(journal),
                    null);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                return await HandleExecutionFailureAsync(
                    journal,
                    applied,
                    options,
                    context,
                    exception).ConfigureAwait(false);
            }
        }
        finally
        {
            executionGate.Release();
        }
    }

    private IWindowsOptimizationAction[] ValidateExecutionRequest(
        IEnumerable<IWindowsOptimizationAction> requestedActions,
        WindowsActionContext context)
    {
        ArgumentNullException.ThrowIfNull(requestedActions);
        ArgumentNullException.ThrowIfNull(context);
        if (context.TransactionId == Guid.Empty)
        {
            throw new ArgumentException("TransactionId cannot be empty.", nameof(context));
        }

        if (context.IsImmediateFailureRecovery)
        {
            throw new ArgumentException(
                "Immediate failure recovery can only be initiated by the transaction engine.",
                nameof(context));
        }

        var actions = requestedActions.ToArray();
        if (actions.Length == 0)
        {
            throw new ArgumentException("At least one action is required.", nameof(requestedActions));
        }

        foreach (var action in actions)
        {
            catalog.Validate(action);
        }

        if (actions.Select(action => action.Metadata.Id).Distinct(StringComparer.Ordinal).Count()
            != actions.Length)
        {
            throw new ArgumentException("A transaction cannot contain duplicate action IDs.", nameof(requestedActions));
        }

        return actions;
    }

    private async Task<WindowsTransactionJournal> LoadOrCreateJournalAsync(
        IReadOnlyList<IWindowsOptimizationAction> actions,
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        var journal = await journalStore.LoadAsync(context.TransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (journal is null)
        {
            journal = CreateJournal(actions, context);
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateExistingJournal(journal, actions);
            if (journal.PersonalUsage != context.PersonalUsage)
            {
                throw new InvalidOperationException("The transaction belongs to a different personal plan.");
            }
            if (journal.Profile is not null
                && context.Profile is not null
                && journal.Profile != context.Profile)
            {
                throw new InvalidOperationException(
                    $"Transaction '{journal.TransactionId}' was created for profile '{journal.Profile}', not '{context.Profile}'.");
            }

            var journalChanged = false;
            if (journal.Profile is null && context.Profile is not null)
            {
                journal.Profile = context.Profile;
                journalChanged = true;
            }

            if (context.IsElevated && !journal.WasElevated)
            {
                journal.WasElevated = true;
                journalChanged = true;
            }

            if (journalChanged)
            {
                await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            }
        }

        return journal;
    }

    private static IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)>
        BuildSelectedActions(
            IReadOnlyList<IWindowsOptimizationAction> actions,
            WindowsTransactionJournal journal,
            WindowsActionContext context,
            WindowsTransactionOptions options)
    {
        var entriesById = journal.Actions.ToDictionary(
            entry => entry.ActionId,
            StringComparer.Ordinal);
        var selected = new List<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)>();
        foreach (var action in actions)
        {
            var entry = entriesById[action.Metadata.Id];
            if (entry.State == ActionJournalState.SkippedPrivilege)
            {
                entry.State = ActionJournalState.DeferredPrivilege;
            }

            if (entry.State == ActionJournalState.Committed)
            {
                continue;
            }

            if (entry.State is not (ActionJournalState.Pending
                or ActionJournalState.DeferredPrivilege))
            {
                throw new InvalidOperationException(
                    $"Action '{entry.ActionId}' cannot resume from state '{entry.State}'.");
            }

            var isAdministratorAction =
                action.Metadata.RequiredPrivilege == RequiredPrivilege.Administrator;
            // An action opted into "attempt without elevation first"
            // still gets a shot in the standard-user phase even though
            // it is otherwise gated on IsElevated -- most Windows
            // configurations allow it, and only a genuine access-denied
            // result (see the isolated-execution catch below) defers it
            // to the elevated broker phase.
            var attemptWithoutElevationFirst = isAdministratorAction
                && action.Metadata.AttemptWithoutElevationFirst
                && !context.IsElevated;
            var include = isAdministratorAction
                ? (options.IncludeAdministratorActions && context.IsElevated)
                    || (attemptWithoutElevationFirst && options.IncludeStandardUserActions)
                : options.IncludeStandardUserActions;
            if (!include)
            {
                if (isAdministratorAction)
                {
                    entry.State = ActionJournalState.DeferredPrivilege;
                }

                continue;
            }

            selected.Add((action, entry));
        }

        return selected;
    }

    private async Task<bool> FinalizeInterruptedJournalAsync(WindowsTransactionJournal journal)
    {
        if (journal.State is not (TransactionState.Applying or TransactionState.Committing))
        {
            return false;
        }

        var reason = WindowsActionText.Format("ActionResults.Engine.InterruptedAction");
        foreach (var entry in journal.Actions)
        {
            if (entry.State is ActionJournalState.Applying
                or ActionJournalState.Applied
                or ActionJournalState.Committing)
            {
                entry.RollbackSafeAfterInterruption =
                    entry.State == ActionJournalState.Applied
                    && entry.Reversibility == ActionReversibility.RebuildableData;
                MarkTerminal(entry, ActionJournalState.Failed, ActionExecutionOutcome.Failed, reason);
                entry.Error ??= reason;
            }
            else if (entry.State is ActionJournalState.Pending
                or ActionJournalState.DeferredPrivilege)
            {
                MarkTerminal(
                    entry,
                    ActionJournalState.Skipped,
                    ActionExecutionOutcome.NotRun,
                    WindowsActionText.Format("ActionResults.Engine.NotRunAfterInterruption"));
            }
        }

        journal.State = TransactionState.CommittedWithErrors;
        journal.Error = reason;
        await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async Task<WindowsTransactionResult> CompleteWithNoSelectedActionsAsync(
        WindowsTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        journal.State = DetermineSuccessfulState(journal);
        await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        return CreateResult(journal, [], GetDeferredAdministratorIds(journal), null);
    }

    /// <summary>
    /// Devolve a ação à fase do broker por exigir elevação neste computador.
    /// Não é falha: a entrada volta ao journal como se nunca tivesse sido
    /// tentada.
    /// </summary>
    /// <remarks>
    /// Os dois caminhos de execução repetiam este recuo e divergiam: o estrito
    /// não limpava <c>CompletedAtUtc</c>. Como o journal é recarregado entre
    /// execuções, uma entrada retomada podia acabar marcada como adiada e, ao
    /// mesmo tempo, carregando o instante de conclusão de uma tentativa
    /// anterior.
    /// </remarks>
    private static void DeferToBrokerPhase(WindowsActionJournalEntry entry)
    {
        entry.State = ActionJournalState.DeferredPrivilege;
        entry.Error = null;
        entry.StartedAtUtc = null;
        entry.CompletedAtUtc = null;
    }

    private async Task ApplyAndCommitAsync(
        WindowsTransactionJournal journal,
        IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> selected,
        List<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> applied,
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        var totalWeight = selected.Sum(item => Math.Max(1, item.Action.Metadata.ProgressWeight));
        var completedWeight = 0;

        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Entry.State = ActionJournalState.Applying;
            item.Entry.StartedAtUtc = DateTimeOffset.UtcNow;
            item.Entry.Error = null;
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

            context.Progress?.Report(new WindowsActionProgress(
                context.TransactionId,
                item.Action.Metadata.Id,
                WindowsActionText.Format("ActionResults.Engine.Applying", item.Action.Metadata.Name),
                completedWeight,
                totalWeight));

            WindowsActionApplyResult result;
            try
            {
                result = await item.Action.ApplyAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (
                !context.IsElevated
                && item.Action.Metadata.RequiredPrivilege == RequiredPrivilege.Administrator
                && item.Action.Metadata.AttemptWithoutElevationFirst)
            {
                DeferToBrokerPhase(item.Entry);
                await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            item.Entry.Changed = result.Changed;
            item.Entry.SnapshotJson = result.SnapshotJson;
            item.Entry.Messages.AddRange(result.Messages);
            if (result.Changed
                && !string.IsNullOrWhiteSpace(result.SnapshotJson)
                && !applied.Any(appliedItem => ReferenceEquals(appliedItem.Entry, item.Entry)))
            {
                // Apply already returned a durable compensation snapshot. It
                // must remain recoverable even if its semantic outcome is
                // rejected below.
                applied.Add(item);
            }

            item.Entry.Outcome = ValidateApplyOutcome(result);
            if (item.Entry.Outcome == ActionExecutionOutcome.Skipped)
            {
                item.Entry.State = ActionJournalState.Skipped;
                item.Entry.OutcomeReason = result.Messages.LastOrDefault();
                item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
                completedWeight += Math.Max(1, item.Action.Metadata.ProgressWeight);
                await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
                continue;
            }

            item.Entry.State = ActionJournalState.Applied;
            item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
            completedWeight += Math.Max(1, item.Action.Metadata.ProgressWeight);
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

            context.Progress?.Report(new WindowsActionProgress(
                context.TransactionId,
                item.Action.Metadata.Id,
                WindowsActionText.Format("ActionResults.Engine.StepCompleted"),
                completedWeight,
                totalWeight));
        }

        journal.State = TransactionState.Committing;
        await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

        foreach (var item in OrderForCommit(applied))
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Entry.State = ActionJournalState.Committing;
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            await item.Action.CommitAsync(
                context,
                item.Entry.SnapshotJson,
                cancellationToken).ConfigureAwait(false);
            item.Entry.State = ActionJournalState.Committed;
            item.Entry.Outcome = item.Entry.Changed
                ? ActionExecutionOutcome.Applied
                : item.Entry.Outcome;
            item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        }

        journal.State = DetermineSuccessfulState(journal);
        journal.Error = null;
        await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WindowsTransactionResult> HandleExecutionFailureAsync(
        WindowsTransactionJournal journal,
        IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> applied,
        WindowsTransactionOptions options,
        WindowsActionContext context,
        Exception exception)
    {
        var recoveryErrors = new List<Exception>();
        journal.Error = exception.ToString();
        journal.State = TransactionState.Failed;
        var current = journal.Actions.LastOrDefault(entry =>
            entry.State is ActionJournalState.Applying
                or ActionJournalState.Committing);
        var currentWasCommitting = current?.State == ActionJournalState.Committing;
        if (current is not null)
        {
            current.State = ActionJournalState.Failed;
            current.Outcome = ActionExecutionOutcome.Failed;
            current.Error = exception.ToString();
            current.CompletedAtUtc = DateTimeOffset.UtcNow;
        }

        await TrySaveDuringRecoveryAsync(journal, recoveryErrors).ConfigureAwait(false);
        if (options.RollbackOnFailure)
        {
            var recoveryContext = context with { IsImmediateFailureRecovery = true };
            var rollbackCandidates = applied
                .Where(item => CanRecoverAppliedAction(
                    item.Entry,
                    currentWasCommitting && ReferenceEquals(item.Entry, current)))
                .ToArray();
            var recoverySuccessState = DetermineFailureRecoveryState(journal, rollbackCandidates);
            try
            {
                await RollbackAppliedAsync(
                    journal,
                    rollbackCandidates,
                    recoveryContext,
                    recoverySuccessState,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException) when (rollbackException is not StackOverflowException)
            {
                recoveryErrors.Add(rollbackException);
                recoveryErrors.AddRange(await RollbackWithoutPersistenceAsync(
                    rollbackCandidates,
                    recoveryContext).ConfigureAwait(false));

                journal.State = rollbackCandidates.All(item =>
                    item.Entry.State == ActionJournalState.RolledBack)
                    ? DetermineFailureRecoveryState(journal, rollbackCandidates)
                    : TransactionState.RollbackFailed;
            }
        }

        if (recoveryErrors.Count > 0)
        {
            var errorCountBeforeFinalSave = recoveryErrors.Count;
            var combined = new AggregateException([exception, .. recoveryErrors]);
            journal.Error = combined.ToString();
            await TrySaveDuringRecoveryAsync(journal, recoveryErrors).ConfigureAwait(false);
            if (recoveryErrors.Count > errorCountBeforeFinalSave)
            {
                combined = new AggregateException([exception, .. recoveryErrors]);
                journal.Error = combined.ToString();
            }
        }

        return CreateResult(
            journal,
            applied.Select(item => item.Action.Metadata.Id).ToArray(),
            GetDeferredAdministratorIds(journal),
            recoveryErrors.Count == 0
                ? WindowsActionText.Format("ActionResults.Engine.ActionFailed")
                : WindowsActionText.Format("ActionResults.Engine.RollbackFailed"));
    }

    private async Task TrySaveDuringRecoveryAsync(
        WindowsTransactionJournal journal,
        ICollection<Exception> recoveryErrors)
    {
        try
        {
            await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception persistenceException) when (persistenceException is not StackOverflowException)
        {
            recoveryErrors.Add(persistenceException);
        }
    }

    private static async Task<IReadOnlyList<Exception>> RollbackWithoutPersistenceAsync(
        IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> applied,
        WindowsActionContext context)
    {
        var rollbackErrors = new List<Exception>();
        foreach (var item in applied.Reverse().Where(item =>
                     item.Entry.State != ActionJournalState.RolledBack))
        {
            try
            {
                item.Entry.State = ActionJournalState.RollingBack;
                await item.Action.RollbackAsync(
                    context,
                    item.Entry.SnapshotJson,
                    CancellationToken.None).ConfigureAwait(false);
                item.Entry.State = ActionJournalState.RolledBack;
                item.Entry.Outcome = ActionExecutionOutcome.RolledBack;
                item.Entry.Error = null;
                item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
            }
            catch (Exception rollbackException) when (rollbackException is not StackOverflowException)
            {
                item.Entry.State = ActionJournalState.RollbackFailed;
                item.Entry.Outcome = ActionExecutionOutcome.RollbackFailed;
                item.Entry.Error = rollbackException.ToString();
                item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
                rollbackErrors.Add(rollbackException);
            }
        }

        return rollbackErrors;
    }

    public async Task<WindowsTransactionResult> RollbackAsync(
        Guid transactionId,
        bool isElevated,
        WindowsRollbackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("Transaction ID cannot be empty.", nameof(transactionId));
        }

        options ??= new WindowsRollbackOptions();
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var textScope = WindowsActionText.Use(actionText);
            var journal = await journalStore.LoadAsync(transactionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new FileNotFoundException($"Transaction journal '{transactionId}' was not found.");
            ValidateJournalForRollback(journal, transactionId);
            journal.WasElevated |= isElevated;
            var context = new WindowsActionContext
            {
                TransactionId = transactionId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                IsElevated = isElevated
            };

            var rollback = new List<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)>();
            var deferredAdministratorIds = new List<string>();
            foreach (var entry in journal.Actions)
            {
                if (!CanRollback(entry))
                {
                    continue;
                }

                if (entry.RequiredPrivilege == RequiredPrivilege.Administrator
                    && (!options.IncludeAdministratorActions || !isElevated))
                {
                    deferredAdministratorIds.Add(entry.ActionId);
                    continue;
                }

                if (entry.RequiredPrivilege == RequiredPrivilege.StandardUser
                    && !options.IncludeStandardUserActions)
                {
                    continue;
                }

                rollback.Add((catalog.GetRequired(entry.ActionId, entry.Version), entry));
            }

            var selectedIds = rollback
                .Select(item => item.Entry.ActionId)
                .ToHashSet(StringComparer.Ordinal);
            var hasRemainingStandardActions = journal.Actions.Any(entry =>
                    CanRollback(entry)
                    && entry.RequiredPrivilege == RequiredPrivilege.StandardUser
                    && !selectedIds.Contains(entry.ActionId));
            var successState = deferredAdministratorIds.Count > 0
                ? TransactionState.AwaitingElevationRollback
                : hasRemainingStandardActions
                    ? TransactionState.AwaitingStandardRollback
                    : journal.Actions.Any(entry =>
                        entry.Outcome == ActionExecutionOutcome.Failed
                        && !selectedIds.Contains(entry.ActionId))
                        ? TransactionState.CommittedWithErrors
                        : TransactionState.RolledBack;
            await RollbackAppliedAsync(
                journal,
                rollback,
                context,
                successState,
                cancellationToken).ConfigureAwait(false);

            return CreateResult(
                journal,
                rollback.Select(item => item.Action.Metadata.Id).ToArray(),
                deferredAdministratorIds,
                journal.State == TransactionState.RollbackFailed
                    ? WindowsActionText.Format("ActionResults.Engine.RollbackFailed")
                    : null);
        }
        finally
        {
            executionGate.Release();
        }
    }

    /// <summary>
    /// Marks every still-pending/deferred administrator action as failed
    /// without touching any already-committed standard-user action --
    /// used when the elevated broker phase itself fails or is cancelled, so
    /// a non-critical administrative failure (e.g. the high-performance
    /// power plan) never rolls back independent changes that already
    /// succeeded. The transaction settles as
    /// <see cref="TransactionState.CommittedWithErrors"/> instead of
    /// being torn down.
    /// </summary>
    public async Task<WindowsTransactionResult> MarkAdministratorPhaseFailedAsync(
        Guid transactionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = await journalStore.LoadAsync(transactionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new FileNotFoundException($"Transaction journal '{transactionId}' was not found.");

            foreach (var entry in journal.Actions.Where(entry =>
                         entry.RequiredPrivilege == RequiredPrivilege.Administrator
                         && entry.State is (ActionJournalState.Pending
                             or ActionJournalState.DeferredPrivilege
                             or ActionJournalState.Applying
                             or ActionJournalState.Applied
                             or ActionJournalState.Committing)))
            {
                entry.RollbackSafeAfterInterruption =
                    entry.State == ActionJournalState.Applied
                    && entry.Reversibility == ActionReversibility.RebuildableData;
                MarkTerminal(entry, ActionJournalState.Failed, ActionExecutionOutcome.Failed, reason);
                entry.Error = reason;
            }

            journal.State = TransactionState.CommittedWithErrors;
            journal.Error = reason;
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

            return CreateResult(journal, [], [], reason);
        }
        finally
        {
            executionGate.Release();
        }
    }

    private static IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)>
        OrderForCommit(
            IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> applied)
    {
        return applied
            .OrderBy(item => item.Action.Metadata.Reversibility is
                ActionReversibility.Irreversible or ActionReversibility.RebuildableData
                ? 1
                : 0)
            .ThenBy(item => item.Entry.Sequence)
            .ToArray();
    }

    private static bool CanRollback(WindowsActionJournalEntry entry)
    {
        if (!entry.Changed
            || string.IsNullOrWhiteSpace(entry.SnapshotJson)
            || entry.State is ActionJournalState.RolledBack
                or ActionJournalState.Pending
                or ActionJournalState.DeferredPrivilege
                or ActionJournalState.SkippedPrivilege
                or ActionJournalState.Skipped)
        {
            return false;
        }

        if (entry.Reversibility == ActionReversibility.Irreversible)
        {
            return false;
        }

        if (entry.Reversibility == ActionReversibility.RebuildableData
            && entry.State is ActionJournalState.Committed or ActionJournalState.Failed)
        {
            return entry.State == ActionJournalState.Failed
                && entry.RollbackSafeAfterInterruption;
        }

        return true;
    }

    private static TransactionState DetermineFailureRecoveryState(
        WindowsTransactionJournal journal,
        IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> rollbackCandidates)
    {
        var hasUnrecoverableChangedFailure = journal.Actions.Any(entry =>
            entry.State == ActionJournalState.Failed
            && entry.Changed
            && !rollbackCandidates.Any(candidate => ReferenceEquals(candidate.Entry, entry)));
        return hasUnrecoverableChangedFailure
            ? TransactionState.CommittedWithErrors
            : TransactionState.RolledBack;
    }

    private static bool CanRecoverAppliedAction(
        WindowsActionJournalEntry entry,
        bool commitStarted = false)
    {
        if (!entry.Changed
            || string.IsNullOrWhiteSpace(entry.SnapshotJson)
            || entry.State == ActionJournalState.RolledBack)
        {
            return false;
        }

        if (entry.Reversibility == ActionReversibility.Irreversible)
        {
            return false;
        }

        return entry.Reversibility != ActionReversibility.RebuildableData
            || (!commitStarted && entry.State != ActionJournalState.Committed);
    }

    private static ActionExecutionOutcome ValidateApplyOutcome(WindowsActionApplyResult result)
    {
        if ((result.Changed && result.Outcome != ActionExecutionOutcome.Applied)
            || (!result.Changed && result.Outcome is not (
                ActionExecutionOutcome.Verified or ActionExecutionOutcome.Skipped)))
        {
            throw new InvalidOperationException(
                $"Action returned invalid outcome '{result.Outcome}' for Changed={result.Changed}.");
        }

        return result.Outcome;
    }

    private void ValidateJournalForRollback(
        WindowsTransactionJournal journal,
        Guid requestedTransactionId)
    {
        if (journal.TransactionId != requestedTransactionId)
        {
            throw new InvalidDataException("The transaction journal ID does not match its requested file.");
        }

        if (journal.Actions.Count == 0
            || journal.Actions.Select(entry => entry.ActionId)
                .Distinct(StringComparer.Ordinal).Count() != journal.Actions.Count)
        {
            throw new InvalidDataException("The transaction journal action list is invalid.");
        }

        for (var index = 0; index < journal.Actions.Count; index++)
        {
            var entry = journal.Actions[index];
            if (entry.Sequence != index + 1)
            {
                throw new InvalidDataException("The transaction journal action order is invalid.");
            }

            var registered = catalog.GetRequired(entry.ActionId, entry.Version);
            if (entry.RequiredPrivilege != registered.Metadata.RequiredPrivilege
                || entry.Reversibility != registered.Metadata.Reversibility)
            {
                throw new InvalidDataException(
                    $"Journal metadata for action '{entry.ActionId}' does not match the allowlist.");
            }
        }
    }

    private static WindowsTransactionJournal CreateJournal(
        IReadOnlyList<IWindowsOptimizationAction> actions,
        WindowsActionContext context)
    {
        return new WindowsTransactionJournal
        {
            TransactionId = context.TransactionId,
            SchemaVersion = 1,
            CreatedAtUtc = context.StartedAtUtc,
            UpdatedAtUtc = context.StartedAtUtc,
            WasElevated = context.IsElevated,
            Profile = context.Profile,
            PersonalUsage = context.PersonalUsage,
            State = TransactionState.Created,
            Actions = actions.Select((action, index) => new WindowsActionJournalEntry
            {
                Sequence = index + 1,
                ActionId = action.Metadata.Id,
                Version = action.Metadata.Version,
                RequiredPrivilege = action.Metadata.RequiredPrivilege,
                Reversibility = action.Metadata.Reversibility,
                State = ActionJournalState.Pending
            }).ToList()
        };
    }

    private static void ValidateExistingJournal(
        WindowsTransactionJournal journal,
        IReadOnlyList<IWindowsOptimizationAction> requestedActions)
    {
        if (journal.State is TransactionState.Failed
            or TransactionState.RollbackFailed
            or TransactionState.RollingBack
            or TransactionState.RolledBack
            or TransactionState.AwaitingElevationRollback
            or TransactionState.AwaitingStandardRollback)
        {
            throw new InvalidOperationException(
                $"Transaction '{journal.TransactionId}' cannot resume from state '{journal.State}'.");
        }

        if (journal.Actions.Select(entry => entry.ActionId)
            .Distinct(StringComparer.Ordinal).Count() != journal.Actions.Count)
        {
            throw new InvalidOperationException("The existing journal contains duplicate action IDs.");
        }

        var entries = journal.Actions.ToDictionary(entry => entry.ActionId, StringComparer.Ordinal);
        foreach (var action in requestedActions)
        {
            if (!entries.TryGetValue(action.Metadata.Id, out var entry))
            {
                throw new InvalidOperationException(
                    $"Action '{action.Metadata.Id}' was not initialized in this transaction.");
            }

            if (entry.Version != action.Metadata.Version
                || entry.RequiredPrivilege != action.Metadata.RequiredPrivilege
                || entry.Reversibility != action.Metadata.Reversibility)
            {
                throw new InvalidOperationException(
                    $"Action '{action.Metadata.Id}' does not match its initialized journal entry.");
            }
        }
    }

    private static TransactionState DetermineSuccessfulState(
        WindowsTransactionJournal journal)
    {
        return journal.Actions.Any(entry => entry.State is
            ActionJournalState.Pending or ActionJournalState.DeferredPrivilege)
            ? TransactionState.AwaitingElevation
            : DetermineIsolatedFinalState(journal);
    }

    private static IReadOnlyList<string> GetDeferredAdministratorIds(
        WindowsTransactionJournal journal)
    {
        return journal.Actions
            .Where(entry => entry.RequiredPrivilege == RequiredPrivilege.Administrator)
            .Where(entry => entry.State is
                ActionJournalState.Pending or ActionJournalState.DeferredPrivilege)
            .OrderBy(entry => entry.Sequence)
            .Select(entry => entry.ActionId)
            .ToArray();
    }

    private static WindowsTransactionResult CreateResult(
        WindowsTransactionJournal journal,
        IReadOnlyList<string> appliedActionIds,
        IReadOnlyList<string> deferredAdministratorActionIds,
        string? error)
    {
        return new WindowsTransactionResult
        {
            TransactionId = journal.TransactionId,
            State = journal.State,
            AppliedActionIds = appliedActionIds,
            ChangedActionIds = journal.Actions
                .Where(entry => entry.Changed)
                .OrderBy(entry => entry.Sequence)
                .Select(entry => entry.ActionId)
                .ToArray(),
            DeferredAdministratorActionIds = deferredAdministratorActionIds,
            Error = error
        };
    }

    private async Task<WindowsTransactionResult> ExecuteIsolatedAsync(
        WindowsTransactionJournal journal,
        IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> selected,
        WindowsActionContext context,
        CancellationToken cancellationToken)
    {
        var entriesById = journal.Actions.ToDictionary(entry => entry.ActionId, StringComparer.Ordinal);
        var applied = new List<string>();
        var totalWeight = selected.Sum(item => Math.Max(1, item.Action.Metadata.ProgressWeight));
        var totalSteps = selected.Count;
        var completedWeight = 0;
        var step = 0;
        var aborted = false;

        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            step++;
            var weight = Math.Max(1, item.Action.Metadata.ProgressWeight);

            if (aborted)
            {
                completedWeight += weight;
                await RecordSkippedActionAsync(
                    journal, item, context, step, totalSteps, completedWeight, totalWeight,
                    ActionExecutionOutcome.NotRun,
                    WindowsActionText.Format("ActionResults.Engine.NotRunAfterCriticalFailure"))
                    .ConfigureAwait(false);
                continue;
            }

            var unmet = FindUnmetPrerequisite(item.Action.Metadata, entriesById);
            if (unmet is not null)
            {
                completedWeight += weight;
                await RecordSkippedActionAsync(
                    journal, item, context, step, totalSteps, completedWeight, totalWeight,
                    ActionExecutionOutcome.Skipped,
                    WindowsActionText.Format("ActionResults.Engine.UnmetPrerequisite", unmet))
                    .ConfigureAwait(false);
                continue;
            }

            await BeginItemApplicationAsync(
                journal, item, context, step, totalSteps, completedWeight, totalWeight,
                cancellationToken).ConfigureAwait(false);
            var result = await ApplyIsolatedItemAsync(journal, item, context, applied, cancellationToken)
                .ConfigureAwait(false);

            completedWeight += weight;
            if (result == IsolatedItemResult.FailedCritical)
            {
                aborted = true;
            }

            ReportStep(context, item, step, totalSteps, completedWeight, totalWeight,
                ReportOutcomeFor(result));
        }

        if (aborted)
        {
            AbortRemainingEntries(journal);
        }

        journal.State = DetermineIsolatedFinalState(journal);
        journal.Error = journal.Actions.Any(entry => entry.Outcome is
            ActionExecutionOutcome.Failed
            or ActionExecutionOutcome.RolledBack
            or ActionExecutionOutcome.RollbackFailed)
            ? WindowsActionText.Format("ActionResults.Engine.IncompleteActions")
            : null;
        await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);

        return CreateResult(journal, applied, GetDeferredAdministratorIds(journal), journal.Error);
    }

    private async Task RecordSkippedActionAsync(
        WindowsTransactionJournal journal,
        (IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry) item,
        WindowsActionContext context,
        int step,
        int totalSteps,
        int completedWeight,
        int totalWeight,
        ActionExecutionOutcome outcome,
        string reason)
    {
        MarkTerminal(item.Entry, ActionJournalState.Skipped, outcome, reason);
        await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
        ReportStep(context, item, step, totalSteps, completedWeight, totalWeight, outcome);
    }

    private async Task BeginItemApplicationAsync(
        WindowsTransactionJournal journal,
        (IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry) item,
        WindowsActionContext context,
        int step,
        int totalSteps,
        int completedWeight,
        int totalWeight,
        CancellationToken cancellationToken)
    {
        item.Entry.State = ActionJournalState.Applying;
        item.Entry.StartedAtUtc = DateTimeOffset.UtcNow;
        item.Entry.Error = null;
        await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        ReportStep(context, item, step, totalSteps, completedWeight, totalWeight,
            ActionExecutionOutcome.Pending);
    }

    private async Task<IsolatedItemResult> ApplyIsolatedItemAsync(
        WindowsTransactionJournal journal,
        (IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry) item,
        WindowsActionContext context,
        List<string> applied,
        CancellationToken cancellationToken)
    {
        var commitStarted = false;
        try
        {
            var result = await item.Action.ApplyAsync(context, cancellationToken)
                .ConfigureAwait(false);
            item.Entry.Changed = result.Changed;
            item.Entry.SnapshotJson = result.SnapshotJson;
            item.Entry.Messages.AddRange(result.Messages);
            item.Entry.Outcome = ValidateApplyOutcome(result);

            if (result.Changed)
            {
                item.Entry.State = ActionJournalState.Committing;
                await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
                commitStarted = true;
                await item.Action.CommitAsync(context, item.Entry.SnapshotJson, cancellationToken)
                    .ConfigureAwait(false);
                item.Entry.State = ActionJournalState.Committed;
                item.Entry.Outcome = ActionExecutionOutcome.Applied;
                applied.Add(item.Action.Metadata.Id);
            }
            else if (item.Entry.Outcome == ActionExecutionOutcome.Skipped)
            {
                item.Entry.State = ActionJournalState.Skipped;
                item.Entry.OutcomeReason = result.Messages.LastOrDefault();
            }
            else
            {
                item.Entry.State = ActionJournalState.Committed;
                item.Entry.Outcome = ActionExecutionOutcome.Verified;
            }

            item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            return item.Entry.Changed
                ? IsolatedItemResult.Applied
                : item.Entry.Outcome == ActionExecutionOutcome.Skipped
                    ? IsolatedItemResult.Skipped
                    : IsolatedItemResult.Verified;
        }
        catch (OperationCanceledException cancellationException) when (
            cancellationToken.IsCancellationRequested)
        {
            var recoveryErrors = await RecoverIsolatedItemAsync(
                journal,
                item,
                context,
                commitStarted,
                persistEntryBeforeRollback: false).ConfigureAwait(false);
            if (recoveryErrors.Count > 0)
            {
                item.Entry.Error = new AggregateException(
                    [cancellationException, .. recoveryErrors]).ToString();
            }

            throw;
        }
        catch (UnauthorizedAccessException) when (
            !context.IsElevated
            && item.Action.Metadata.RequiredPrivilege == RequiredPrivilege.Administrator
            && item.Action.Metadata.AttemptWithoutElevationFirst)
        {
            DeferToBrokerPhase(item.Entry);
            await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
            return IsolatedItemResult.Deferred;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            item.Entry.Error = exception.ToString();
            item.Entry.State = ActionJournalState.Failed;
            item.Entry.Outcome = ActionExecutionOutcome.Failed;
            item.Entry.OutcomeReason = WindowsActionText.Format("ActionResults.Engine.ActionFailed");
            item.Entry.BugCode = BugCodeClassifier.ClassifyOptimizationException(
                exception, item.Action.Metadata.Id);
            item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
            var recoveryErrors = await RecoverIsolatedItemAsync(
                journal,
                item,
                context,
                commitStarted,
                persistEntryBeforeRollback: true).ConfigureAwait(false);
            if (recoveryErrors.Count > 0)
            {
                item.Entry.Error = new AggregateException(
                    [exception, .. recoveryErrors]).ToString();
                await TrySaveDuringRecoveryAsync(journal, recoveryErrors).ConfigureAwait(false);
            }

            return item.Action.Metadata.IsCritical
                ? IsolatedItemResult.FailedCritical
                : IsolatedItemResult.Failed;
        }
    }

    /// <summary>
    /// Recuperação de uma ação isolada que não pôde concluir: tenta o rollback
    /// persistido da própria ação e, se o pipeline de rollback falhar, cai
    /// para o rollback sem persistência marcado como recuperação imediata. A
    /// lista devolvida fica vazia quando a recuperação foi limpa; o chamador é
    /// quem decide como reportar os erros acumulados.
    /// </summary>
    private async Task<List<Exception>> RecoverIsolatedItemAsync(
        WindowsTransactionJournal journal,
        (IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry) item,
        WindowsActionContext context,
        bool commitStarted,
        bool persistEntryBeforeRollback)
    {
        try
        {
            if (persistEntryBeforeRollback)
            {
                await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
            }

            await IsolatedRollbackSelfAsync(journal, item, context, commitStarted)
                .ConfigureAwait(false);
            return [];
        }
        catch (Exception rollbackPipelineException) when (
            rollbackPipelineException is not StackOverflowException)
        {
            return
            [
                rollbackPipelineException,
                .. await RollbackWithoutPersistenceAsync(
                    [item],
                    context with { IsImmediateFailureRecovery = true }).ConfigureAwait(false)
            ];
        }
    }

    private void AbortRemainingEntries(WindowsTransactionJournal journal)
    {
        foreach (var entry in journal.Actions.Where(entry =>
                     entry.State is ActionJournalState.Pending
                         or ActionJournalState.DeferredPrivilege))
        {
            MarkTerminal(entry, ActionJournalState.Skipped,
                ActionExecutionOutcome.NotRun,
                WindowsActionText.Format("ActionResults.Engine.NotRunAfterCriticalFailure"));
        }
    }

    private static ActionExecutionOutcome ReportOutcomeFor(IsolatedItemResult result)
    {
        return result switch
        {
            IsolatedItemResult.Applied => ActionExecutionOutcome.Applied,
            IsolatedItemResult.Verified => ActionExecutionOutcome.Verified,
            IsolatedItemResult.Skipped => ActionExecutionOutcome.Skipped,
            IsolatedItemResult.Deferred => ActionExecutionOutcome.Skipped,
            _ => ActionExecutionOutcome.Failed
        };
    }

    private enum IsolatedItemResult
    {
        Applied,
        Verified,
        Skipped,
        Deferred,
        Failed,
        FailedCritical
    }

    private async Task IsolatedRollbackSelfAsync(
        WindowsTransactionJournal journal,
        (IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry) item,
        WindowsActionContext context,
        bool commitStarted)
    {
        if (!item.Entry.Changed || string.IsNullOrWhiteSpace(item.Entry.SnapshotJson))
        {
            return;
        }

        if (!CanRecoverAppliedAction(item.Entry, commitStarted))
        {
            item.Entry.State = ActionJournalState.Failed;
            item.Entry.Outcome = ActionExecutionOutcome.Failed;
            item.Entry.OutcomeReason = WindowsActionText.Format(
                "ActionResults.Engine.RollbackUnsafeAfterInterruption");
            item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
            await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            item.Entry.State = ActionJournalState.RollingBack;
            await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
            await item.Action.RollbackAsync(
                context with { IsImmediateFailureRecovery = true },
                item.Entry.SnapshotJson,
                CancellationToken.None)
                .ConfigureAwait(false);
            item.Entry.State = ActionJournalState.RolledBack;
            item.Entry.Outcome = ActionExecutionOutcome.RolledBack;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            item.Entry.State = ActionJournalState.RollbackFailed;
            item.Entry.Outcome = ActionExecutionOutcome.RollbackFailed;
            item.Entry.Error = exception.ToString();
        }

        await journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A cancellation during the isolated-failures loop used to leave
    /// <c>journal.State</c> stuck at <see cref="TransactionState.Applying"/>
    /// forever (only <see cref="DetermineIsolatedFinalState"/> — reached at
    /// the bottom of the normal loop — ever advanced it past that point).
    /// A journal persisted in that state is not one of the terminal states
    /// <see cref="ValidateExistingJournal"/> rejects, so a later run with the
    /// same transaction ID would try to resume it and hit an
    /// <see cref="InvalidOperationException"/> because the cancelled action's
    /// entry state no longer matches what resume expects — permanently
    /// wedging the transaction until the journal file was deleted by hand.
    /// This mirrors the same "mark remaining as skipped, compute the final
    /// state, persist" sequence used when a critical failure aborts the run.
    /// </summary>
    private async Task<IReadOnlyList<Exception>> FinalizeCancelledIsolatedRunAsync(
        WindowsTransactionJournal journal)
    {
        foreach (var entry in journal.Actions.Where(entry =>
                     entry.State is ActionJournalState.Pending
                         or ActionJournalState.DeferredPrivilege
                     || (entry.State == ActionJournalState.Applying && !entry.Changed)))
        {
            MarkTerminal(entry, ActionJournalState.Skipped,
                ActionExecutionOutcome.NotRun,
                WindowsActionText.Format("ActionResults.Engine.NotRunAfterCancellation"));
        }

        journal.State = TransactionState.CommittedWithErrors;
        journal.Error = WindowsActionText.Format("ActionResults.Engine.Cancelled");
        var recoveryErrors = new List<Exception>();
        await TrySaveDuringRecoveryAsync(journal, recoveryErrors).ConfigureAwait(false);
        return recoveryErrors;
    }

    private static string? FindUnmetPrerequisite(
        ActionMetadataDto metadata,
        IReadOnlyDictionary<string, WindowsActionJournalEntry> entriesById)
    {
        foreach (var prerequisiteId in metadata.Prerequisites)
        {
            if (!entriesById.TryGetValue(prerequisiteId, out var entry)
                || entry.Outcome is not (ActionExecutionOutcome.Verified or ActionExecutionOutcome.Applied))
            {
                return prerequisiteId;
            }
        }

        return null;
    }

    private static void MarkTerminal(
        WindowsActionJournalEntry entry,
        ActionJournalState state,
        ActionExecutionOutcome outcome,
        string reason)
    {
        entry.State = state;
        entry.Outcome = outcome;
        entry.OutcomeReason = reason;
        entry.CompletedAtUtc = DateTimeOffset.UtcNow;
    }

    private static TransactionState DetermineIsolatedFinalState(
        WindowsTransactionJournal journal)
    {
        if (journal.Actions.Any(entry => entry.Outcome is
            ActionExecutionOutcome.Failed
            or ActionExecutionOutcome.RolledBack
            or ActionExecutionOutcome.RollbackFailed))
        {
            return TransactionState.CommittedWithErrors;
        }

        return journal.Actions.Any(entry => entry.State is
            ActionJournalState.Pending or ActionJournalState.DeferredPrivilege)
            ? TransactionState.AwaitingElevation
            : TransactionState.Committed;
    }

    private static void ReportStep(
        WindowsActionContext context,
        (IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry) item,
        int step,
        int totalSteps,
        int completedWeight,
        int totalWeight,
        ActionExecutionOutcome outcome)
    {
        context.Progress?.Report(new WindowsActionProgress(
            context.TransactionId,
            item.Action.Metadata.Id,
            item.Action.Metadata.Name,
            completedWeight,
            totalWeight,
            step,
            totalSteps,
            outcome));
    }

    private async Task RollbackAppliedAsync(
        WindowsTransactionJournal journal,
        IReadOnlyList<(IWindowsOptimizationAction Action, WindowsActionJournalEntry Entry)> applied,
        WindowsActionContext context,
        TransactionState successState,
        CancellationToken cancellationToken)
    {
        journal.State = TransactionState.RollingBack;
        await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        var rollbackErrors = new List<Exception>();

        foreach (var item in applied.Reverse())
        {
            try
            {
                item.Entry.State = ActionJournalState.RollingBack;
                await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
                await item.Action.RollbackAsync(
                    context,
                    item.Entry.SnapshotJson,
                    cancellationToken).ConfigureAwait(false);
                item.Entry.State = ActionJournalState.RolledBack;
                item.Entry.Outcome = ActionExecutionOutcome.RolledBack;
                item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                item.Entry.State = ActionJournalState.RollbackFailed;
                item.Entry.Outcome = ActionExecutionOutcome.RollbackFailed;
                item.Entry.Error = exception.ToString();
                rollbackErrors.Add(exception);
            }

            await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        }

        if (rollbackErrors.Count == 0)
        {
            journal.State = successState;
        }
        else
        {
            journal.State = TransactionState.RollbackFailed;
            journal.Error = new AggregateException(rollbackErrors).ToString();
        }

        await journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
    }
}
