using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Windows;
using Ralven.Windows.Actions;
using Ralven.Windows.Diagnostics;
using Ralven.Windows.Engine;
using Ralven.Windows.Infrastructure;

namespace Ralven.App.Services;

public sealed partial class AppOptimizationService : IAppOptimizationService
{
    private readonly string appDataDirectory;
    private readonly string journalDirectory;
    private readonly string logsDirectory;
    private readonly string settingsPath;
    private readonly string fiveMInstallationCachePath;
    private readonly JsonSerializerOptions indentedJson;
    private readonly ElevatedBrokerClient brokerClient;
    private readonly ILocalizationService localization;
    private readonly DemoModeSimulator demoSimulator;
    private readonly ResourceComparisonCapture resourceComparison;
    private readonly bool demoMode;
    private readonly bool useSyntheticDiagnostic;
    private readonly Func<Guid, bool> administratorReceiptExists;
    private string? detectedLegacyRoot;

    public AppOptimizationService(
        bool demoMode = false,
        bool useSyntheticDiagnostic = false,
        ILocalizationService? localization = null)
        : this(demoMode, useSyntheticDiagnostic, localization, appDataDirectoryOverride: null, null)
    {
    }

    internal AppOptimizationService(
        string appDataDirectory,
        ILocalizationService? localization = null,
        Func<Guid, bool>? administratorReceiptExists = null)
        : this(
            demoMode: false,
            useSyntheticDiagnostic: false,
            localization,
            appDataDirectory,
            administratorReceiptExists)
    {
    }

    private AppOptimizationService(
        bool demoMode,
        bool useSyntheticDiagnostic,
        ILocalizationService? localization,
        string? appDataDirectoryOverride,
        Func<Guid, bool>? administratorReceiptExists)
    {
        this.demoMode = demoMode;
        this.useSyntheticDiagnostic = useSyntheticDiagnostic;
        this.localization = localization ?? LocalizationService.Current;
        this.administratorReceiptExists = administratorReceiptExists ?? HasAdministratorReceipt;
        appDataDirectory = appDataDirectoryOverride is null
            ? AppDataPaths.Root
            : Path.GetFullPath(appDataDirectoryOverride);
        journalDirectory = Path.Combine(appDataDirectory, "Transactions");
        logsDirectory = Path.Combine(appDataDirectory, "Logs");
        settingsPath = Path.Combine(appDataDirectory, "settings.json");
        fiveMInstallationCachePath = Path.Combine(appDataDirectory, "fivem-installation.json");
        indentedJson = new JsonSerializerOptions(RalvenJson.Options) { WriteIndented = true };
        brokerClient = new ElevatedBrokerClient(appDataDirectory, localization);
        demoSimulator = new DemoModeSimulator(this.localization);
        resourceComparison = new ResourceComparisonCapture(this.localization);
    }

    public string LogsDirectory => logsDirectory;

    internal Func<CancellationToken, Task<bool>> AuthorizePro { private get; init; } = _ => Task.FromResult(false);

    public async Task<AppOptimizationResult> ExecuteAsync(
        OptimizationPlanDto plan,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.PersonalPreferences is not null && !await AuthorizePro(cancellationToken).ConfigureAwait(false))
        {
            throw new ProAccessRequiredException(localization.GetString("Ultra.AccessRequired"));
        }
        if (demoMode)
        {
            return await demoSimulator.SimulatePlanAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ExecutePlanCoreAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CreateResultFromJournalAsync(
                plan.PlanId,
                plan.Profile,
                succeeded: false,
                wasCancelled: true,
                localization.GetString("Status.SafeCancellation.Headline"),
                CancellationToken.None,
                failureBugCode: BugCode.APP_OPT_CANCELLED,
                failureErrorCategory: "cancelled").ConfigureAwait(false);
        }
    }

    public Task<bool> RollbackAsync(
        Guid transactionId,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (demoMode)
        {
            throw new InvalidOperationException(
                localization.GetString("Runtime.DemoHistoryDisabled"));
        }

        return RollbackCoreAsync(transactionId, progress, cancellationToken);
    }

    private async Task<AppOptimizationResult> ExecutePlanCoreAsync(
        OptimizationPlanDto plan,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportPreparing(progress);

        var beforeSnapshot = resourceComparison.TryCaptureSnapshot();
        var runtime = CreateRuntimeForPlan(plan);
        var localResult = await ExecuteLocalPhaseAsync(
            runtime,
            plan,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (localResult.State is not (
            TransactionState.Committed
            or TransactionState.CommittedWithErrors
            or TransactionState.AwaitingElevation))
        {
            return await CreateResultFromJournalAsync(
                plan.PlanId,
                plan.Profile,
                succeeded: false,
                wasCancelled: false,
                localResult.Error ?? localization.GetString("Runtime.LocalChangesReverted"),
                cancellationToken).ConfigureAwait(false);
        }

        if (localResult.DeferredAdministratorActionIds.Count > 0)
        {
            var elevatedResult = await ExecuteElevatedPhaseAsync(
                runtime,
                plan,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (elevatedResult is not null)
            {
                return elevatedResult;
            }
        }

        // O sucesso final é decidido pelo relatório do journal: uma run com
        // qualquer ação falhada nunca é reportada como totalmente concluída.
        var runSucceeded = await LoadFinalRunSucceededAsync(plan, cancellationToken).ConfigureAwait(false);

        ReportCompletion(progress, runSucceeded);

        var comparison = await resourceComparison.CaptureComparisonAsync(beforeSnapshot).ConfigureAwait(false);

        var result = await CreateResultFromJournalAsync(
            plan.PlanId,
            plan.Profile,
            succeeded: runSucceeded,
            wasCancelled: false,
            $"{localization.GetString(runSucceeded ? "Runtime.PlanCompleted" : "Runtime.PlanCompletedWithErrors")}. "
                + localization.GetString(
                    runSucceeded ? "Runtime.PlanCompletedDetail" : "Runtime.PlanCompletedWithErrorsDetail"),
            cancellationToken).ConfigureAwait(false);
        return comparison is null ? result : result with { Comparison = comparison };
    }

    private void ReportPreparing(IProgress<AppProgressUpdate> progress)
    {
        progress.Report(new AppProgressUpdate
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = AppProgressKind.Preparing,
            Percent = 2,
            Headline = localization.GetString("Runtime.ValidatingPlan"),
            Detail = localization.GetString("Runtime.ValidatingPlanDetail")
        });
    }

    private async Task<WindowsTransactionResult> ExecuteLocalPhaseAsync(
        WindowsOptimizationRuntime runtime,
        OptimizationPlanDto plan,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var actionProgress = new InlineProgress<WindowsActionProgress>(update =>
        {
            var percent = update.TotalWeight > 0
                ? 5d + (65d * update.CompletedWeight / update.TotalWeight)
                : 5d;
            var actionName = GetLocalizedActionName(update.ActionId);
            progress.Report(new AppProgressUpdate
            {
                Timestamp = DateTimeOffset.UtcNow,
                Kind = AppProgressKind.Applying,
                Percent = Math.Clamp(percent, 5, 70),
                Headline = actionName,
                Detail = localization.Format(DetailKeyFor(update.Outcome), actionName),
                ActionId = update.ActionId,
                CompletedSteps = update.CompletedSteps,
                TotalSteps = update.TotalSteps,
                Outcome = update.Outcome
            });
        });
        var context = new WindowsActionContext
        {
            TransactionId = plan.PlanId,
            StartedAtUtc = DateTimeOffset.UtcNow,
            IsElevated = false,
            Progress = actionProgress
        };
        return await runtime.ExecuteAsync(
            plan,
            context,
            new WindowsTransactionOptions
            {
                IncludeStandardUserActions = true,
                IncludeAdministratorActions = false,
                IsolateFailures = true
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executa a fase administrativa no broker elevado. Retorna um resultado
    /// final quando a fase termina (cancelada, falhou ou foi rejeitada pelo
    /// UAC); retorna <see langword="null"/> quando a fase elevada concluiu com
    /// sucesso e a orquestração deve prosseguir.
    /// </summary>
    private async Task<AppOptimizationResult?> ExecuteElevatedPhaseAsync(
        WindowsOptimizationRuntime runtime,
        OptimizationPlanDto plan,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new AppProgressUpdate
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = AppProgressKind.Preparing,
            Percent = 71,
            Headline = localization.GetString("Runtime.WindowsConfirmation"),
            Detail = localization.GetString("Runtime.WindowsConfirmationDetail")
        });

        var adminProgress = new InlineProgress<AppProgressUpdate>(update => progress.Report(
            update.ActionId is null
                ? update
                : update with { Headline = GetLocalizedActionName(update.ActionId) }));

        ElevatedBrokerResult elevated;
        try
        {
            elevated = await brokerClient.ExecuteAsync(plan, adminProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var reason = localization.GetString("Runtime.AdminConfirmationCancelled");
            await runtime.Engine.MarkAdministratorPhaseFailedAsync(
                plan.PlanId,
                reason,
                CancellationToken.None).ConfigureAwait(false);
            return await CreateResultFromJournalAsync(
                plan.PlanId,
                plan.Profile,
                succeeded: false,
                wasCancelled: true,
                localization.GetString("Runtime.UacCancelledPreserved"),
                CancellationToken.None,
                failureBugCode: BugCode.APP_OPT_CANCELLED,
                failureErrorCategory: "cancelled").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var reason = localization.Format(
                "Runtime.BrokerResultUnconfirmed",
                localization.DescribeException(exception));
            await runtime.Engine.MarkAdministratorPhaseFailedAsync(
                plan.PlanId,
                reason,
                CancellationToken.None).ConfigureAwait(false);
            return await CreateResultFromJournalAsync(
                plan.PlanId,
                plan.Profile,
                succeeded: false,
                wasCancelled: false,
                localization.Format("Runtime.AdminPhaseFailedPreserved", reason),
                CancellationToken.None,
                failureBugCode: BugCodeClassifier.ClassifyBrokerException(exception),
                failureErrorCategory: TelemetryErrorClassifier.ClassifyException(exception)).ConfigureAwait(false);
        }

        if (!elevated.Succeeded)
        {
            // A falha (ou cancelamento do UAC) da fase administrativa não
            // é motivo para desfazer as ações de usuário padrão já
            // confirmadas -- isso é o que causava várias etapas
            // aparentemente "quebradas" quando só o plano de energia
            // falhava. Só a própria ação administrativa é marcada como
            // falha; o restante permanece Committed.
            await runtime.Engine.MarkAdministratorPhaseFailedAsync(
                plan.PlanId,
                elevated.Message,
                CancellationToken.None).ConfigureAwait(false);
            var summary = elevated.WasCancelled
                ? localization.GetString("Runtime.UacCancelledPreserved")
                : localization.Format("Runtime.AdminPhaseFailedPreserved", elevated.Message);

            return await CreateResultFromJournalAsync(
                plan.PlanId,
                plan.Profile,
                succeeded: false,
                wasCancelled: elevated.WasCancelled,
                summary,
                CancellationToken.None,
                failureBugCode: BugCodeClassifier.ClassifyBrokerFailure(
                    elevated.ErrorCode,
                    elevated.WasCancelled),
                failureErrorCategory: TelemetryErrorClassifier.ClassifyBrokerFailure(
                    elevated.ErrorCode,
                    elevated.WasCancelled)).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> LoadFinalRunSucceededAsync(
        OptimizationPlanDto plan,
        CancellationToken cancellationToken)
    {
        var finalJournal = await LoadJournalAsync(plan.PlanId, cancellationToken).ConfigureAwait(false);
        var finalReport = finalJournal is null
            ? null
            : OptimizationReportBuilder.Build(finalJournal, plan.Profile);
        return finalReport?.Succeeded ?? true;
    }

    private void ReportCompletion(IProgress<AppProgressUpdate> progress, bool runSucceeded)
    {
        progress.Report(new AppProgressUpdate
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = runSucceeded ? AppProgressKind.Completed : AppProgressKind.Warning,
            Percent = 100,
            Headline = localization.GetString(
                        runSucceeded ? "Runtime.PlanCompleted" : "Runtime.PlanCompletedWithErrors"),
            Detail = localization.GetString(
                        runSucceeded ? "Runtime.PlanCompletedDetail" : "Runtime.PlanCompletedWithErrorsDetail")
        });
    }

    private static string DetailKeyFor(ActionExecutionOutcome outcome) => outcome switch
    {
        ActionExecutionOutcome.Verified => "Runtime.ActionVerified",
        ActionExecutionOutcome.Applied => "Runtime.ActionCompleted",
        ActionExecutionOutcome.Skipped => "Runtime.ActionSkipped",
        ActionExecutionOutcome.Failed => "Runtime.ActionFailed",
        ActionExecutionOutcome.RolledBack => "Runtime.ActionRolledBack",
        _ => "Runtime.ApplyingAction"
    };

    private Task<bool> RollbackCoreAsync(
        Guid transactionId,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken) => RollbackCoreAsync(
            transactionId,
            progress,
            CreateRuntimeForDetectedInstallation().Engine,
            token => ExecuteElevatedRollbackAsync(transactionId, progress, token),
            cancellationToken);

    internal async Task<bool> RollbackCoreAsync(
        Guid transactionId,
        IProgress<AppProgressUpdate> progress,
        WindowsTransactionEngine engine,
        Func<CancellationToken, Task<bool>> rollbackAdministrator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new AppProgressUpdate
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = AppProgressKind.RollingBack,
            Percent = 5,
            Headline = localization.GetString("Runtime.PreparingRestore"),
            Detail = localization.Format("Runtime.ValidatingTransaction", transactionId.ToString("N"))
        });

        // Execution commits user changes before the administrator phase.
        // Restore that phase first so ASPM sees its original power scheme.
        var journal = await LoadJournalAsync(transactionId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Transaction journal '{transactionId}' was not found.");
        if (journal.Actions.Any(action => action.RequiredPrivilege == RequiredPrivilege.Administrator
                && CanOfferRollback(action))
            && !await rollbackAdministrator(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var localResult = await engine.RollbackAsync(
            transactionId,
            isElevated: false,
            new WindowsRollbackOptions
            {
                IncludeStandardUserActions = true,
                IncludeAdministratorActions = false
            },
            cancellationToken).ConfigureAwait(false);
        if (localResult.State == TransactionState.RollbackFailed)
        {
            return HandleRollbackFailure(
                new WindowsFiveMProcessInspector(),
                localization,
                progress);
        }

        if (localResult.State != TransactionState.RolledBack)
        {
            progress.Report(new AppProgressUpdate
            {
                Timestamp = DateTimeOffset.UtcNow,
                Kind = AppProgressKind.Warning,
                Percent = 100,
                Headline = localization.GetString("Status.CouldNotRestore"),
                Detail = localization.GetString("Runtime.RestoreIncomplete")
            });
            return false;
        }

        progress.Report(new AppProgressUpdate
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = AppProgressKind.Completed,
            Percent = 100,
            Headline = localization.GetString("Runtime.RestoreCompleted"),
            Detail = localization.GetString("Runtime.RestoreCompletedDetail")
        });
        return true;
    }

    /// <summary>
    /// Delega o rollback administrativo ao broker elevado. Retorna
    /// <see langword="false"/> quando o usuário cancela a confirmação do UAC
    /// (a transação permanece aguardando restauração) e lança quando o broker
    /// falha por outro motivo.
    /// </summary>
    private async Task<bool> ExecuteElevatedRollbackAsync(
        Guid transactionId,
        IProgress<AppProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new AppProgressUpdate
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = AppProgressKind.RollingBack,
            Percent = 70,
            Headline = localization.GetString("Runtime.ConfirmRestore"),
            Detail = localization.GetString("Runtime.ConfirmRestoreDetail")
        });
        var elevated = await brokerClient.RollbackAsync(
            transactionId,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (elevated.Succeeded)
        {
            return true;
        }

        if (elevated.WasCancelled)
        {
            progress.Report(new AppProgressUpdate
            {
                Timestamp = DateTimeOffset.UtcNow,
                Kind = AppProgressKind.Warning,
                Percent = 72,
                Headline = localization.GetString("Runtime.AdminRestorePending"),
                Detail = localization.GetString("Runtime.AdminRestorePendingDetail")
            });
            return false;
        }

        throw new InvalidOperationException(elevated.Message);
    }

    private WindowsOptimizationRuntime CreateRuntimeForDetectedInstallation()
    {
        var environment = WindowsOptimizationEnvironment.DetectDefault() with
        {
            JournalDirectory = journalDirectory
        };
        var root = detectedLegacyRoot;
        if (FiveMInstallationLocator.TryValidateLegacyCandidate(
                root,
                FiveMInstallationSource.Cache,
                out var installation))
        {
            var gtaV = GtaVLocator.Detect(installation.Root);
            environment = environment with
            {
                FiveMInstallationRoot = installation.Root,
                FiveMAppRoot = installation.AppRoot,
                FiveMExecutablePath = installation.ExecutablePath,
                GtaVInstallationRoot = gtaV.InstallationRoot,
                GtaVExecutablePath = gtaV.ExecutablePath,
                GtaVGraphicsSettingsPath = gtaV.GraphicsSettingsPath
            };
        }

        return WindowsOptimizationRuntime.Create(
            environment,
            WindowsOptimizationDependencies.CreateDefault(environment, FormatWindowsActionText));
    }

    internal WindowsOptimizationRuntime CreateRuntimeForPlan(OptimizationPlanDto plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Scope == OptimizationScope.GeneralWindows)
        {
            var environment = WindowsOptimizationEnvironment.DetectDefault() with
            {
                JournalDirectory = journalDirectory
            };
            return WindowsOptimizationRuntime.Create(
                environment,
                WindowsOptimizationDependencies.CreateDefault(environment, FormatWindowsActionText));
        }

        if (plan.Scope != OptimizationScope.FiveMLegacy
            || string.IsNullOrWhiteSpace(detectedLegacyRoot))
        {
            throw new InvalidOperationException(
                "A detected FiveM Legacy installation is required for this optimization scope.");
        }

        return CreateRuntimeForDetectedInstallation();
    }

    private string FormatWindowsActionText(string key, params object?[] arguments)
    {
        var appText = localization.GetString(key);
        return appText != key
            ? string.Format(localization.CurrentCulture, appText, arguments)
            : WindowsActionResources.ForCulture(localization.CurrentCulture)(key, arguments);
    }

    internal static bool HandleRollbackFailure(
        IFiveMProcessInspector processInspector,
        ILocalizationService localization,
        IProgress<AppProgressUpdate> progress)
    {
        ArgumentNullException.ThrowIfNull(processInspector);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(progress);

        var blockReason = WindowsGamingControlsService.GetMutationBlockReason(processInspector);
        if (blockReason != WindowsGamingControlsBlockReason.None)
        {
            var detailKey = blockReason == WindowsGamingControlsBlockReason.FiveMRunning
                ? "Runtime.RestoreBlockedFiveM"
                : "Runtime.RestoreProcessCheckFailed";
            progress.Report(new AppProgressUpdate
            {
                Timestamp = DateTimeOffset.UtcNow,
                Kind = AppProgressKind.Warning,
                Percent = 100,
                Headline = localization.GetString(detailKey),
                Detail = localization.GetString(detailKey)
            });
            return false;
        }

        throw new InvalidOperationException(localization.GetString("Runtime.RollbackConflict"));
    }

    private string GetLocalizedActionName(ActionMetadataDto action)
    {
        return GetLocalizedActionName(action.Id, action.Name);
    }

    private string GetLocalizedActionName(string actionId)
    {
        var fallback = ActionCatalog.Current.TryGet(actionId, out var definition)
            ? definition!.Name
            : actionId;
        return GetLocalizedActionName(actionId, fallback);
    }

    private string GetLocalizedActionName(string actionId, string fallback)
    {
        return localization.GetStringOrFallback($"Actions.{actionId}.Name", fallback);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> callback;

        public InlineProgress(Action<T> callback)
        {
            this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Report(T value) => callback(value);
    }
}
