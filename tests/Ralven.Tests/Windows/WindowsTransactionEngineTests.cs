using System.Linq;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Windows.Actions;
using Ralven.Windows.Engine;
using Ralven.Windows.Infrastructure;
using Xunit;

namespace Ralven.Tests.Windows;

public sealed class WindowsTransactionEngineTests
{
    [Fact]
    public async Task PersonalJournalCannotResumeAsAnotherRoutineAndStillAllowsRollback()
    {
        var action = new TestGameModeAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(new WindowsActionCatalog([action]), journals);
        var context = Context(Guid.NewGuid(), elevated: false) with
        {
            Profile = OptimizationProfile.Aggressive,
            PersonalUsage = PersonalUsage.Work
        };
        await engine.ExecuteAsync([action], context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PersonalUsage.Work, journals.Get(context.TransactionId).PersonalUsage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(
            [action], context with { PersonalUsage = PersonalUsage.Gaming },
            cancellationToken: TestContext.Current.CancellationToken));

        var rollback = await engine.RollbackAsync(context.TransactionId, isElevated: false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TransactionState.RolledBack, rollback.State);
    }

    [Fact]
    public async Task NewJournal_PersistsThePlanProfile()
    {
        var action = new TestGameModeAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);
        var transactionId = Guid.NewGuid();

        _ = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false) with
            {
                Profile = OptimizationProfile.Aggressive
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OptimizationProfile.Aggressive, journals.Get(transactionId).Profile);
    }

    [Fact]
    public async Task InterruptedJournal_IsFinalizedHonestlyAndCanBeReadAgain()
    {
        var action = new TestGameModeAction();
        var journals = new InMemoryJournalStore();
        var transactionId = Guid.NewGuid();
        await journals.SaveAsync(
            InterruptedJournal(transactionId, action, ActionJournalState.Applying),
            TestContext.Current.CancellationToken);
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);

        var first = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.CommittedWithErrors, first.State);
        Assert.Equal(TransactionState.CommittedWithErrors, second.State);
        Assert.Equal(0, action.ApplyCount);
        var entry = Assert.Single(journals.Get(transactionId).Actions);
        Assert.Equal(ActionJournalState.Failed, entry.State);
        Assert.Equal(ActionExecutionOutcome.Failed, entry.Outcome);
    }

    [Fact]
    public async Task InterruptedReversibleAction_WithDurableSnapshotCanStillRollback()
    {
        var action = new TestGameModeAction();
        var journals = new InMemoryJournalStore();
        var transactionId = Guid.NewGuid();
        var journal = InterruptedJournal(transactionId, action, ActionJournalState.Committing);
        journal.Actions[0].Changed = true;
        journal.Actions[0].SnapshotJson = "{}";
        await journals.SaveAsync(journal, TestContext.Current.CancellationToken);
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);

        _ = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);
        var rollback = await engine.RollbackAsync(
            transactionId,
            isElevated: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.RolledBack, rollback.State);
        Assert.Equal(1, action.RollbackCount);
    }

    [Fact]
    public async Task InterruptedRebuildableApply_BeforeCommitCanStillRollback()
    {
        var action = new TestRebuildableAction();
        var journals = new InMemoryJournalStore();
        var transactionId = Guid.NewGuid();
        var journal = InterruptedJournal(transactionId, action, ActionJournalState.Applied);
        journal.Actions[0].Changed = true;
        journal.Actions[0].SnapshotJson = "{}";
        await journals.SaveAsync(journal, TestContext.Current.CancellationToken);
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);

        _ = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);
        var rollback = await engine.RollbackAsync(
            transactionId,
            isElevated: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.RolledBack, rollback.State);
        Assert.Equal(1, action.RollbackCount);
        Assert.True(journals.Get(transactionId).Actions[0].RollbackSafeAfterInterruption);
    }

    [Fact]
    public async Task InterruptedRebuildableCommit_IsNotReportedAsRollbackable()
    {
        var action = new TestRebuildableAction();
        var journals = new InMemoryJournalStore();
        var transactionId = Guid.NewGuid();
        var journal = InterruptedJournal(transactionId, action, ActionJournalState.Committing);
        journal.Actions[0].Changed = true;
        journal.Actions[0].SnapshotJson = "{}";
        await journals.SaveAsync(journal, TestContext.Current.CancellationToken);
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);

        _ = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);
        var rollback = await engine.RollbackAsync(
            transactionId,
            isElevated: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.CommittedWithErrors, rollback.State);
        Assert.Equal(0, action.RollbackCount);
        Assert.False(journals.Get(transactionId).Actions[0].RollbackSafeAfterInterruption);
    }

    [Fact]
    public async Task TwoPhaseExecution_PreservesJournalAndCommitsAdministratorAction()
    {
        var standard = new TestGameModeAction();
        var administrator = new TestPowerAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([standard, administrator]),
            journals);
        var transactionId = Guid.NewGuid();

        var first = await engine.ExecuteAsync(
            [standard, administrator],
            Context(transactionId, elevated: false), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.AwaitingElevation, first.State);
        Assert.Equal([standard.Metadata.Id], first.AppliedActionIds);
        Assert.Equal([administrator.Metadata.Id], first.DeferredAdministratorActionIds);
        Assert.Equal(1, standard.ApplyCount);
        Assert.Equal(0, administrator.ApplyCount);
        Assert.Equal(2, journals.Get(transactionId).Actions.Count);

        var second = await engine.ExecuteAsync(
            [administrator],
            Context(transactionId, elevated: true),
            new WindowsTransactionOptions
            {
                IncludeStandardUserActions = false,
                IncludeAdministratorActions = true
            }, cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.Committed, second.State);
        Assert.Equal([administrator.Metadata.Id], second.AppliedActionIds);
        Assert.Empty(second.DeferredAdministratorActionIds);
        Assert.Equal(1, administrator.ApplyCount);
        var journal = journals.Get(transactionId);
        Assert.True(journal.WasElevated);
        Assert.Equal(2, journal.Actions.Count);
        Assert.All(journal.Actions, entry =>
            Assert.Equal(ActionJournalState.Committed, entry.State));

        var repeated = await engine.ExecuteAsync(
            [administrator],
            Context(transactionId, elevated: true), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(TransactionState.Committed, repeated.State);
        Assert.Empty(repeated.AppliedActionIds);
        Assert.Equal(1, administrator.ApplyCount);
    }

    [Fact]
    public async Task PowerPlanAlreadyActive_CompletesWithoutRequestingElevationOrWriting()
    {
        var powerPlans = new FakePowerPlanController();
        powerPlans.ActiveScheme = powerPlans.PerformanceScheme;
        var action = new SessionPerformancePowerPlanAction(
            powerPlans,
            new FakePowerStatusProvider());
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);
        var transactionId = Guid.NewGuid();

        var result = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false),
            new WindowsTransactionOptions { IsolateFailures = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.Committed, result.State);
        Assert.Empty(result.DeferredAdministratorActionIds);
        Assert.Empty(result.ChangedActionIds);
        Assert.Equal(0, powerPlans.PerformanceActivationCount);
        var entry = Assert.Single(journals.Get(transactionId).Actions);
        Assert.Equal(ActionJournalState.Committed, entry.State);
        Assert.Equal(ActionExecutionOutcome.Verified, entry.Outcome);
    }

    [Fact]
    public async Task MarkAdministratorPhaseFailedAsync_PreservesAlreadyCommittedStandardActions()
    {
        var standard = new TestGameModeAction();
        var administrator = new TestPowerAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([standard, administrator]),
            journals);
        var transactionId = Guid.NewGuid();

        var first = await engine.ExecuteAsync(
            [standard, administrator],
            Context(transactionId, elevated: false), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(TransactionState.AwaitingElevation, first.State);

        var result = await engine.MarkAdministratorPhaseFailedAsync(
            transactionId,
            "O Windows recusou a elevação.", cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.CommittedWithErrors, result.State);
        var journal = journals.Get(transactionId);
        Assert.Equal(ActionJournalState.Committed, journal.Actions[0].State);
        Assert.Equal(0, standard.RollbackCount);
        Assert.Equal(ActionJournalState.Failed, journal.Actions[1].State);
        Assert.Equal(ActionExecutionOutcome.Failed, journal.Actions[1].Outcome);
        Assert.Equal("O Windows recusou a elevação.", journal.Actions[1].OutcomeReason);
    }

    [Fact]
    public async Task MarkAdministratorPhaseFailedAsync_FinalizesIntermediateAdministratorEntries()
    {
        var power = new TestPowerAction();
        var hags = new TestHagsAction();
        var journals = new InMemoryJournalStore();
        var transactionId = Guid.NewGuid();
        await journals.SaveAsync(new WindowsTransactionJournal
        {
            TransactionId = transactionId,
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            WasElevated = true,
            State = TransactionState.Applying,
            Actions =
            [
                Entry(power, 1, ActionJournalState.Applying),
                Entry(hags, 2, ActionJournalState.Applied) with
                {
                    Changed = true,
                    SnapshotJson = "{}"
                }
            ]
        }, TestContext.Current.CancellationToken);
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([power, hags]),
            journals);

        var result = await engine.MarkAdministratorPhaseFailedAsync(
            transactionId,
            "resultado administrativo não confirmado",
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.CommittedWithErrors, result.State);
        Assert.All(journals.Get(transactionId).Actions, entry =>
        {
            Assert.Equal(ActionJournalState.Failed, entry.State);
            Assert.Equal(ActionExecutionOutcome.Failed, entry.Outcome);
        });
    }

    [Fact]
    public async Task TerminalLegacyJournal_BackfillsAndPersistsProfile()
    {
        var action = new TestGameModeAction();
        var journals = new InMemoryJournalStore();
        var transactionId = Guid.NewGuid();
        var journal = InterruptedJournal(transactionId, action, ActionJournalState.Applying);
        journal.State = TransactionState.CommittedWithErrors;
        journal.Actions[0].State = ActionJournalState.Failed;
        await journals.SaveAsync(journal, TestContext.Current.CancellationToken);
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);

        _ = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false) with
            {
                Profile = OptimizationProfile.Aggressive
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OptimizationProfile.Aggressive, journals.Get(transactionId).Profile);
    }

    [Fact]
    public async Task IsolatedExecution_AdministratorActionAlwaysDefersToTheElevatedBroker()
    {
        var standard = new TestGameModeAction();
        var administrator = new TestPowerAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([standard, administrator]),
            journals);
        var transactionId = Guid.NewGuid();

        var result = await engine.ExecuteAsync(
            [standard, administrator],
            Context(transactionId, elevated: false),
            new WindowsTransactionOptions
            {
                IncludeStandardUserActions = true,
                IncludeAdministratorActions = false,
                IsolateFailures = true
            }, cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.AwaitingElevation, result.State);
        Assert.Equal([administrator.Metadata.Id], result.DeferredAdministratorActionIds);
        Assert.Equal(0, administrator.ApplyCount);
        Assert.Equal(
            ActionJournalState.Committed,
            journals.Get(transactionId).Actions.Single(entry => entry.ActionId == standard.Metadata.Id).State);
        Assert.Equal(
            ActionJournalState.DeferredPrivilege,
            journals.Get(transactionId).Actions.Single(entry => entry.ActionId == administrator.Metadata.Id).State);
    }

    [Fact]
    public async Task Rollback_CompletesInStandardAndElevatedPhases()
    {
        var standard = new TestGameModeAction();
        var administrator = new TestPowerAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([standard, administrator]),
            journals);
        var transactionId = Guid.NewGuid();
        _ = await engine.ExecuteAsync(
            [standard, administrator],
            Context(transactionId, elevated: false), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);
        _ = await engine.ExecuteAsync(
            [administrator],
            Context(transactionId, elevated: true), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        var standardRollback = await engine.RollbackAsync(
            transactionId,
            isElevated: false, cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.AwaitingElevationRollback, standardRollback.State);
        Assert.Equal([administrator.Metadata.Id], standardRollback.DeferredAdministratorActionIds);
        Assert.Equal(1, standard.RollbackCount);
        Assert.Equal(0, administrator.RollbackCount);

        var elevatedRollback = await engine.RollbackAsync(
            transactionId,
            isElevated: true, cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.RolledBack, elevatedRollback.State);
        Assert.Empty(elevatedRollback.DeferredAdministratorActionIds);
        Assert.Equal(1, standard.RollbackCount);
        Assert.Equal(1, administrator.RollbackCount);
        Assert.All(journals.Get(transactionId).Actions, entry =>
            Assert.Equal(ActionJournalState.RolledBack, entry.State));
    }

    [Fact]
    public async Task ElevatedAdminOnlyRollback_NeverExecutesStandardSnapshots()
    {
        var standard = new TestGameModeAction();
        var administrator = new TestPowerAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([standard, administrator]),
            journals);
        var transactionId = Guid.NewGuid();
        _ = await engine.ExecuteAsync(
            [standard, administrator],
            Context(transactionId, elevated: false), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);
        _ = await engine.ExecuteAsync(
            [administrator],
            Context(transactionId, elevated: true), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        var result = await engine.RollbackAsync(
            transactionId,
            isElevated: true,
            new WindowsRollbackOptions
            {
                IncludeStandardUserActions = false,
                IncludeAdministratorActions = true
            }, cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.AwaitingStandardRollback, result.State);
        Assert.Equal(0, standard.RollbackCount);
        Assert.Equal(1, administrator.RollbackCount);
        Assert.Equal(
            ActionJournalState.Committed,
            journals.Get(transactionId).Actions[0].State);
    }

    [Fact]
    public async Task Rollback_RejectsTamperedPrivilegeMetadataBeforeExecutingSnapshots()
    {
        var standard = new TestGameModeAction();
        var administrator = new TestPowerAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([standard, administrator]),
            journals);
        var transactionId = Guid.NewGuid();
        _ = await engine.ExecuteAsync(
            [standard, administrator],
            Context(transactionId, elevated: false), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        var journal = journals.Get(transactionId);
        journal.Actions[0] = journal.Actions[0] with
        {
            RequiredPrivilege = RequiredPrivilege.Administrator
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RollbackAsync(
            transactionId,
            isElevated: true,
            new WindowsRollbackOptions
            {
                IncludeStandardUserActions = false,
                IncludeAdministratorActions = true
            }, cancellationToken: global::Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(0, standard.RollbackCount);
        Assert.Equal(0, administrator.RollbackCount);
    }

    [Fact]
    public async Task FailedApply_RollsBackAlreadyAppliedActionsInReverseTransaction()
    {
        var standard = new TestGameModeAction();
        var failing = new TestFailingCaptureAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([standard, failing]),
            journals);
        var transactionId = Guid.NewGuid();

        var result = await engine.ExecuteAsync(
            [standard, failing],
            Context(transactionId, elevated: false), cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.NotNull(result.Error);
        Assert.Equal(1, standard.RollbackCount);
        Assert.Equal(ActionJournalState.RolledBack, journals.Get(transactionId).Actions[0].State);
        Assert.Equal(ActionJournalState.Failed, journals.Get(transactionId).Actions[1].State);
    }

    [Fact]
    public async Task StrictExecution_WhenRebuildableCommitFails_DoesNotClaimRolledBack()
    {
        var action = new TestCommitFailingRebuildableAction();
        var journals = new InMemoryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);
        var transactionId = Guid.NewGuid();

        var result = await engine.ExecuteAsync(
            [action],
            Context(transactionId, elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.CommittedWithErrors, result.State);
        Assert.Equal(ActionJournalState.Failed, journals.Get(transactionId).Actions[0].State);
        Assert.Equal(0, action.RollbackCount);
    }

    [Fact]
    public async Task StrictExecution_InvalidChangedOutcome_CompensatesDurableSnapshot()
    {
        var action = new TestInvalidOutcomeAction();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            new InMemoryJournalStore());

        var result = await engine.ExecuteAsync(
            [action],
            Context(Guid.NewGuid(), elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, action.RollbackCount);
    }

    [Fact]
    public async Task IsolatedExecution_WhenJournalFailsAfterWrite_RollsBackWithoutPersistence()
    {
        var action = new TestGameModeAction();
        var journals = new FailAfterChangedEntryJournalStore();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteAsync(
            [action],
            Context(Guid.NewGuid(), elevated: false),
            new WindowsTransactionOptions
            {
                IncludeStandardUserActions = true,
                IncludeAdministratorActions = false,
                IsolateFailures = true
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, action.RollbackCount);
    }

    [Fact]
    public async Task IsolatedCancellation_WhenJournalFailsAfterWrite_RollsBackWithoutPersistence()
    {
        using var cancellation = new CancellationTokenSource();
        var action = new TestGameModeAction();
        var journals = new CancelAndFailAfterChangedEntryJournalStore(cancellation);
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            journals);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(
            [action],
            Context(Guid.NewGuid(), elevated: false),
            new WindowsTransactionOptions
            {
                IncludeStandardUserActions = true,
                IncludeAdministratorActions = false,
                IsolateFailures = true
            },
            cancellation.Token));

        Assert.Equal(1, action.RollbackCount);
    }

    [Fact]
    public async Task Execute_RejectsCallerSuppliedImmediateRecoveryContext()
    {
        var action = new TestGameModeAction();
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            new InMemoryJournalStore());
        var forgedContext = Context(Guid.NewGuid(), elevated: false) with
        {
            IsImmediateFailureRecovery = true
        };

        await Assert.ThrowsAsync<ArgumentException>(() => engine.ExecuteAsync(
            [action],
            forgedContext,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, action.ApplyCount);
    }

    [Fact]
    public async Task StrictExecution_WhenCommittingSaveFails_RestoresTheAppliedRegistryValue()
    {
        var registry = new FakeRegistryStore();
        registry.Write(GameModeRegistryAction.Address, RegistryValueState.FromDword(0));
        var action = new GameModeRegistryAction(registry, new FakeProcessInspector());
        var engine = new WindowsTransactionEngine(
            new WindowsActionCatalog([action]),
            new FailOnCommittingJournalStore());

        var result = await engine.ExecuteAsync(
            [action],
            Context(Guid.NewGuid(), elevated: false),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.NotNull(result.Error);
        Assert.Equal(0, registry.Read(GameModeRegistryAction.Address).NumericValue);
    }

    private static WindowsActionContext Context(Guid transactionId, bool elevated)
    {
        return new WindowsActionContext
        {
            TransactionId = transactionId,
            StartedAtUtc = DateTimeOffset.UtcNow,
            IsElevated = elevated
        };
    }

    private static WindowsTransactionJournal InterruptedJournal(
        Guid transactionId,
        TestAction action,
        ActionJournalState actionState)
    {
        return new WindowsTransactionJournal
        {
            TransactionId = transactionId,
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            WasElevated = false,
            State = actionState == ActionJournalState.Committing
                ? TransactionState.Committing
                : TransactionState.Applying,
            Actions =
            [
                new WindowsActionJournalEntry
                {
                    Sequence = 1,
                    ActionId = action.Metadata.Id,
                    Version = action.Metadata.Version,
                    RequiredPrivilege = action.Metadata.RequiredPrivilege,
                    Reversibility = action.Metadata.Reversibility,
                    State = actionState
                }
            ]
        };
    }

    private static WindowsActionJournalEntry Entry(
        TestAction action,
        int sequence,
        ActionJournalState state)
    {
        return new WindowsActionJournalEntry
        {
            Sequence = sequence,
            ActionId = action.Metadata.Id,
            Version = action.Metadata.Version,
            RequiredPrivilege = action.Metadata.RequiredPrivilege,
            Reversibility = action.Metadata.Reversibility,
            State = state
        };
    }

    private abstract class TestAction : WindowsOptimizationAction
    {
        public int ApplyCount { get; private set; }

        public int CommitCount { get; private set; }

        public int RollbackCount { get; private set; }

        protected virtual bool Fails => false;

        public override Task<WindowsActionApplyResult> ApplyAsync(
            WindowsActionContext context,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            if (Fails)
            {
                throw new InvalidOperationException("simulated failure");
            }

            return Task.FromResult(WindowsActionApplyResult.ChangedWith(
                new Dictionary<string, string> { ["previous"] = "value" }));
        }

        public override Task CommitAsync(
            WindowsActionContext context,
            string? snapshotJson,
            CancellationToken cancellationToken)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public override Task RollbackAsync(
            WindowsActionContext context,
            string? snapshotJson,
            CancellationToken cancellationToken)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestGameModeAction : TestAction
    {
        public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
            OptimizationActionIds.EnableGameMode);
    }

    private sealed class TestPowerAction : TestAction
    {
        public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
            OptimizationActionIds.EnableSessionPerformancePowerPlan);

        public override Task<WindowsActionApplyResult> ApplyAsync(
            WindowsActionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.IsElevated)
            {
                throw new UnauthorizedAccessException("simulated elevation requirement");
            }

            return base.ApplyAsync(context, cancellationToken);
        }
    }

    private sealed class TestHagsAction : TestAction
    {
        public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
            OptimizationActionIds.ToggleHags);
    }

    private class TestRebuildableAction : TestAction
    {
        public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
            OptimizationActionIds.RepairLegacyServerCache);
    }

    private sealed class TestCommitFailingRebuildableAction : TestRebuildableAction
    {
        public override Task CommitAsync(
            WindowsActionContext context,
            string? snapshotJson,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("simulated destructive commit failure");
        }
    }

    private sealed class TestInvalidOutcomeAction : TestAction
    {
        public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
            OptimizationActionIds.EnableGameMode);

        public override Task<WindowsActionApplyResult> ApplyAsync(
            WindowsActionContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WindowsActionApplyResult
            {
                Changed = true,
                Outcome = ActionExecutionOutcome.Verified,
                SnapshotJson = "{}"
            });
        }
    }

    private sealed class TestFailingCaptureAction : TestAction
    {
        protected override bool Fails => true;

        public override ActionMetadataDto Metadata { get; } = WindowsActionMetadata.For(
            OptimizationActionIds.DisableBackgroundCapture);
    }

    private sealed class FailAfterChangedEntryJournalStore : IWindowsTransactionJournalStore
    {
        private bool persistentlyFailing;

        public Task SaveAsync(
            WindowsTransactionJournal journal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (persistentlyFailing || journal.Actions.Any(entry => entry.Changed))
            {
                persistentlyFailing = true;
                throw new IOException("Simulated persistent journal failure after a write.");
            }

            return Task.CompletedTask;
        }

        public Task<WindowsTransactionJournal?> LoadAsync(
            Guid transactionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WindowsTransactionJournal?>(null);
        }
    }

    private sealed class FailOnCommittingJournalStore : IWindowsTransactionJournalStore
    {
        private bool persistentlyFailing;

        public Task SaveAsync(
            WindowsTransactionJournal journal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (persistentlyFailing || journal.Actions.Any(entry =>
                    entry.State == ActionJournalState.Committing))
            {
                persistentlyFailing = true;
                throw new IOException("Simulated persistent failure while committing.");
            }

            return Task.CompletedTask;
        }

        public Task<WindowsTransactionJournal?> LoadAsync(
            Guid transactionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WindowsTransactionJournal?>(null);
        }
    }

    private sealed class CancelAndFailAfterChangedEntryJournalStore(
        CancellationTokenSource cancellation) : IWindowsTransactionJournalStore
    {
        private bool persistentlyFailing;

        public Task SaveAsync(
            WindowsTransactionJournal journal,
            CancellationToken cancellationToken)
        {
            if (!persistentlyFailing && journal.Actions.Any(entry => entry.Changed))
            {
                persistentlyFailing = true;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (persistentlyFailing)
            {
                throw new IOException("Simulated persistent journal failure after cancellation.");
            }

            return Task.CompletedTask;
        }

        public Task<WindowsTransactionJournal?> LoadAsync(
            Guid transactionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WindowsTransactionJournal?>(null);
        }
    }
}
