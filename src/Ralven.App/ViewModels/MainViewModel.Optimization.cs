using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Windows.Threading;
using Ralven.App.Services;
using Ralven.Contracts;
using Ralven.Windows.Diagnostics;
using Ralven.Core.Catalog;
using Ralven.Core.Planning;

namespace Ralven.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool storeHagsExperiment;

    public bool IsStoreCertificationCandidate => Ralven.UpdateRuntime.PackageIdentity.IsPackaged
        && optimizationScope == OptimizationScope.FiveMLegacy;

    public bool StoreHagsExperiment
    {
        get => storeHagsExperiment;
        set
        {
            if (IsBusy || isPersonalBusy) return;
            if (SetProperty(ref storeHagsExperiment, value)) RefreshPlan();
        }
    }
    public OptimizationScope OptimizationScope => optimizationScope;

    public bool IsGeneralWindowsOptimization => optimizationScope == OptimizationScope.GeneralWindows;

    public string OptimizerTitle => localization.GetString(IsGeneralWindowsOptimization
        ? "Optimizer.General.Title"
        : "Optimizer.FiveM.Title");

    public string OptimizerSubtitle => localization.GetString(IsGeneralWindowsOptimization
        ? "Optimizer.General.Subtitle"
        : "Optimizer.FiveM.Subtitle");

    public bool CanRevertLastOptimization => ComparisonRegressionSuspected
        && !IsBusy
        && !isWindowsGamingBusy
        && !isPersonalBusy
        && lastTransactionId is { } id
        && HistoryItems.Any(item => item.TransactionId == id && item.CanRollback);

    public bool IsGtaVBenchmarkRunning { get => isGtaVBenchmarkRunning; private set => SetProperty(ref isGtaVBenchmarkRunning, value); }

    public string GtaVBenchmarkStatusLabel { get => gtaVBenchmarkStatusLabel; private set => SetProperty(ref gtaVBenchmarkStatusLabel, value); }

    public bool CanRunGtaVBenchmark => !IsBusy
        && !isWindowsGamingBusy
        && !isPersonalBusy
        && !IsGtaVBenchmarkRunning;

    public string ProfilePresentationBenefits { get => profilePresentationBenefits; private set => SetProperty(ref profilePresentationBenefits, value); }

    public string ProfilePresentationImpact { get => profilePresentationImpact; private set => SetProperty(ref profilePresentationImpact, value); }

    public bool IsLightSelected
    {
        get => !IsUltraSelected && selectedProfile == OptimizationProfile.Light;
        set { if (value) SelectProfile(OptimizationProfile.Light); }
    }

    public bool IsBalancedSelected
    {
        get => !IsUltraSelected && selectedProfile == OptimizationProfile.Balanced;
        set { if (value) SelectProfile(OptimizationProfile.Balanced); }
    }

    public bool IsAggressiveSelected
    {
        get => !IsUltraSelected && selectedProfile == OptimizationProfile.Aggressive;
        set { if (value) SelectProfile(OptimizationProfile.Aggressive); }
    }

    private OptimizationProfile? RecommendedProfile => diagnostic?.RecommendedProfile;

    public bool IsLightRecommended => RecommendedProfile == OptimizationProfile.Light;

    public bool IsBalancedRecommended => RecommendedProfile == OptimizationProfile.Balanced;

    public bool IsAggressiveRecommended => RecommendedProfile == OptimizationProfile.Aggressive;

    public int SelectedActionCount => PlannedAdjustments.Count;

    public int AutomaticAnalysisCount => InformationalPlannedActions.Count;

    public bool HasPlannedAdjustments => SelectedActionCount > 0;

    public bool HasAutomaticAnalysis => AutomaticAnalysisCount > 0;

    public bool HasPlannedActions => currentPlan?.Actions.Count > 0;

    public string ElevationLabel => localization.GetString(
        currentPlan?.RequiresElevation == true
            ? "Plan.Elevation.UacAtRun"
            : "Plan.Elevation.None");

    public string PlanSummary => !HasPlannedActions
        ? localization.GetString("Plan.Empty.Summary")
        : currentPlan?.ContainsNonReversibleActions == true
        ? localization.GetString("Plan.Safety.Mixed")
        : localization.GetString("Plan.Safety.Reversible");

    public string PlanHeader => localization.Format(
        "Plan.ActionsCatalog",
        SelectedActionCount);

    public string AutomaticAnalysisHeader => localization.Format(
        "Optimizer.AutomaticAnalysis.Header",
        AutomaticAnalysisCount);

    public string PlanNoticesText => !HasPlannedActions
        ? string.Empty
        : currentPlan?.Notices.Count > 0
        ? string.Join("  •  ", currentPlan.Notices.Select(LocalizeNotice))
        : string.Empty;

    public string EmptyPlanMessage => diagnostic is null
        ? localization.GetString(diagnosticFailed
            ? "Plan.Empty.DiagnosticUnavailable"
            : "Plan.Empty.DiagnosticInProgress")
        : currentPlan?.Blocks.Any(block => block.Code == PlanBlockCode.EnhancedNotSupported) == true
            ? localization.GetString("Diagnosis.EnhancedUnsupported")
        : IsGeneralWindowsOptimization
            ? localization.GetString("Plan.Empty.GeneralNoSafeActions")
            : diagnostic.Edition == FiveMEdition.Legacy
            ? localization.GetString("Plan.Empty.NoSafeActions")
            : localization.GetString("Plan.Empty.LegacyRequired");

    public string SelectedProfileLabel
    {
        get
        {
            var upper = SelectedProfileName.ToUpper(localization.CurrentCulture);
            return !IsUltraSelected && selectedProfile == RecommendedProfile
                ? $"{upper} • {localization.GetString("Profiles.RecommendedBadge")}"
                : upper;
        }
    }

    /// <summary>
    /// True when the "Recomendado" mark should render as its own badge next
    /// to <see cref="SelectedProfileName"/>, instead of text concatenated
    /// into the all-caps <see cref="SelectedProfileLabel"/> heading.
    /// </summary>
    public bool IsSelectedProfileRecommended => !IsUltraSelected && selectedProfile == RecommendedProfile;

    /// <summary>
    /// Posição do perfil selecionado na escala Leve → Médio → Agressivo, de 0 a 1.
    /// Não é uma estimativa de ganho nem uma medida de FPS: é só o nível
    /// escolhido, exposto para que a cena 3D e o anel do Otimizador reajam de
    /// forma visível quando o usuário troca de perfil.
    /// </summary>
    public double ProfileIntensity => selectedProfile switch
    {
        OptimizationProfile.Light => 0.34,
        OptimizationProfile.Aggressive => 1,
        _ => 0.67
    };

    public double ProfileIntensityPercent => ProfileIntensity * 100;

    public string SafetySummary => currentPlan?.RequiresElevation == true
        ? localization.GetString("Plan.Elevation.OnePrompt")
        : localization.GetString("Plan.Elevation.CurrentUser");

    public string SelectedProfileName => IsUltraSelected ? localization.GetString("Ultra.Name") : ProfileName(selectedProfile);

    public void PrepareNewOptimization()
    {
        if (!CanEditPersonalPreferences)
        {
            return;
        }

        ApplyReport(null);
        ApplyComparison(null);
        lastTransactionId = null;
        StepLedger.Clear();
        RefreshPlan();
    }

    public void SetOptimizationScope(OptimizationScope scope)
    {
        if (IsBusy || isPersonalBusy || optimizationScope == scope)
        {
            return;
        }

        optimizationScope = scope;
        OnPropertyChanged(nameof(IsStoreCertificationCandidate));
        isUltraSelected = false;
        RefreshUltraPresentation();
        ApplyReport(null);
        ApplyComparison(null);
        lastTransactionId = null;
        StepLedger.Clear();
        OnPropertyChanged(nameof(OptimizationScope));
        OnPropertyChanged(nameof(IsGeneralWindowsOptimization));
        OnPropertyChanged(nameof(OptimizerTitle));
        OnPropertyChanged(nameof(OptimizerSubtitle));
        OnPropertyChanged(nameof(EmptyPlanMessage));
        RefreshPlan();
    }

    public void SelectProfile(OptimizationProfile profile)
    {
        if (IsBusy || isPersonalBusy || (selectedProfile == profile && !isUltraSelected))
        {
            return;
        }

        profileInitializedFromDiagnostic = true;
        isUltraSelected = false;
        selectedProfile = profile;
        RefreshUltraPresentation();
        OnPropertyChanged(nameof(IsLightSelected));
        OnPropertyChanged(nameof(IsBalancedSelected));
        OnPropertyChanged(nameof(IsAggressiveSelected));
        OnPropertyChanged(nameof(SelectedProfileLabel));
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(IsSelectedProfileRecommended));
        OnPropertyChanged(nameof(ProfileIntensity));
        OnPropertyChanged(nameof(ProfileIntensityPercent));
        RefreshPlan();
    }

    public async Task StartOptimizationAsync()
    {
        if (!TryPrepareOptimizationRun())
        {
            return;
        }

        operationCancellation = new CancellationTokenSource();
        var progress = new Progress<AppProgressUpdate>(ApplyProgress);
        var completedSuccessfully = false;
        var telemetryEventName = TelemetryEventNames.OptimizationFailed;
        string? telemetryErrorCategory = null;
        BugCode? telemetryBugCode = null;
        try
        {
            // currentPlan é garantido não-nulo aqui: TryPrepareOptimizationRun
            // só retorna true quando CanStart é true (e CanStart exige plano).
            var result = await service.ExecuteAsync(currentPlan!, progress, operationCancellation.Token);
            completedSuccessfully = result.Succeeded;
            telemetryEventName = result.WasCancelled
                ? TelemetryEventNames.OptimizationCancelled
                : result.Succeeded ? TelemetryEventNames.OptimizationCompleted : TelemetryEventNames.OptimizationFailed;
            if (!result.Succeeded)
            {
                telemetryErrorCategory = result.FailureErrorCategory ?? "unexpected";
                telemetryBugCode = result.FailureBugCode;
                if (telemetryBugCode is null && result.Report is not null)
                {
                    var failures = result.Report.Lines
                        .Where(line => line.Outcome is ActionExecutionOutcome.Failed or ActionExecutionOutcome.RollbackFailed)
                        .ToArray();
                    telemetryBugCode = failures.Length > 1
                        ? BugCode.APP_OPT_PARTIAL_FAILURE
                        : failures.Length == 1
                            ? BugCodeClassifier.ClassifyOptimizationException(
                                new InvalidOperationException(),
                                failures[0].ActionId)
                            : BugCode.APP_OPT_ACTION_EXECUTION;
                }
            }
            await HandleOptimizationResultAsync(result);
        }
        catch (OperationCanceledException)
        {
            telemetryEventName = TelemetryEventNames.OptimizationCancelled;
            telemetryErrorCategory = "cancelled";
            telemetryBugCode = BugCode.APP_OPT_CANCELLED;
            HandleOptimizationCancelled();
        }
        catch (Exception exception)
        {
            telemetryEventName = TelemetryEventNames.OptimizationFailed;
            telemetryErrorCategory = TelemetryErrorClassifier.ClassifyException(exception);
            telemetryBugCode = BugCodeClassifier.ClassifyException(exception, "optimization");
            if (exception is ProAccessRequiredException)
            {
                SetProAccess(false);
                UltraStatus = localization.GetString("Ultra.AccessRequired");
                FinalizeHeadline(UltraStatus);
            }
            else
            {
                HandleOptimizationFailed();
            }
        }
        finally
        {
            FinalizeOptimizationRun(completedSuccessfully, telemetryEventName, telemetryErrorCategory, telemetryBugCode);
        }
        await ObservePersonalPcAsync();
    }

    private bool TryPrepareOptimizationRun()
    {
        // Recria o plano no clique para que o nonce e o timestamp aceitos pelo
        // broker elevado nunca fiquem antigos enquanto a janela permanece aberta.
        RefreshPlan();
        if (!CanStart || currentPlan is null)
        {
            ProgressHeadline = OptimizationScope == OptimizationScope.FiveMLegacy && diagnostic?.IsFiveMRunning == true
                ? localization.GetString("Plan.CloseFiveM")
                : OptimizationScope == OptimizationScope.FiveMLegacy && diagnostic?.GtaVIsRunning == true
                    ? localization.GetString("Plan.CloseGtaV")
                    : localization.GetString("Plan.Unavailable");
            return false;
        }

        IsBusy = true;
        ProgressPercent = 0;
        ClearProgressHistory();
        StartOperationTiming();
        TrackOptimizationStartedTelemetry();
        StepLedger.Clear();
        ApplyReport(null);
        ApplyComparison(null);
        lastTransactionId = null;
        return true;
    }

    private async Task HandleOptimizationResultAsync(AppOptimizationResult result)
    {
        ProgressPercent = result.Succeeded ? 100 : ProgressPercent;
        FinalizeHeadline(result.Succeeded
            ? localization.GetString("Status.OptimizationCompleted")
            : result.Summary);
        ApplyReport(result.Report);
        lastTransactionId = result.TransactionId;
        ApplyComparison(result.Comparison);
        ApplyHistory(await service.LoadHistoryAsync());
    }

    private void HandleOptimizationCancelled()
    {
        FinalizeHeadline(localization.GetString("Status.SafeCancellation.Headline"));
    }

    private void HandleOptimizationFailed()
    {
        FinalizeHeadline(localization.GetString("Status.CouldNotComplete"));
    }

    private void FinalizeOptimizationRun(
        bool completedSuccessfully,
        string telemetryEventName,
        string? telemetryErrorCategory,
        BugCode? bugCode = null)
    {
        var executionTime = operationStopwatch?.Elapsed ?? TimeSpan.Zero;
        StopOperationTiming(completedSuccessfully);
        TrackOptimizationTelemetry(
            telemetryEventName,
            executionTime,
            telemetryErrorCategory,
            bugCode,
            currentPlan?.PlanId);
        // operationCancellation foi atribuído antes do try em StartOptimizationAsync.
        operationCancellation!.Dispose();
        operationCancellation = null;
        IsBusy = false;
    }

    public void CancelOptimization()
    {
        if (operationCancellation is null)
        {
            return;
        }

        operationCancellation.Cancel();
        RaiseCommandState();
    }

    public async Task RunGtaVBenchmarkAsync()
    {
        if (!CanRunGtaVBenchmark)
        {
            return;
        }

        IsGtaVBenchmarkRunning = true;
        GtaVBenchmarkStatusLabel = localization.GetString("GtaVBenchmark.Running");
        RaiseCommandState();
        var stopwatch = Stopwatch.StartNew();
        var telemetryEventName = TelemetryEventNames.GtaVBenchmarkFailed;
        string? telemetryErrorCategory = "unexpected";
        try
        {
            var result = await service.RunGtaVBenchmarkAsync(3);
            GtaVBenchmarkStatusLabel = DescribeGtaVBenchmarkResult(result);
            telemetryEventName = result.Succeeded
                ? TelemetryEventNames.GtaVBenchmarkCompleted
                : TelemetryEventNames.GtaVBenchmarkFailed;
            telemetryErrorCategory = result.Succeeded ? null : ClassifyBenchmarkFailure(result.FailureReason);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            GtaVBenchmarkStatusLabel = localization.Format("GtaVBenchmark.Error", localization.DescribeException(exception));
            telemetryErrorCategory = TelemetryErrorClassifier.ClassifyException(exception);
        }
        finally
        {
            stopwatch.Stop();
            TrackGtaVBenchmarkTelemetry(telemetryEventName, stopwatch.Elapsed, telemetryErrorCategory);
            IsGtaVBenchmarkRunning = false;
            RaiseCommandState();
        }
    }

    private static string? ClassifyBenchmarkFailure(string? reason) => reason switch
    {
        "benchmark-did-not-exit-in-time" => "timeout",
        "profile-folder-not-found" or "benchmark-output-file-not-found" => "io",
        "benchmark-output-file-not-recognized" => "invalid-data",
        "gtav-not-detected" or "gtav-still-running" or "gta-executable-not-found" => null,
        _ => "unexpected"
    };

    private string DescribeGtaVBenchmarkResult(AppGtaVBenchmarkResult result)
    {
        if (!result.Succeeded || result.Median is null)
        {
            var reasonKey = result.FailureReason switch
            {
                "gtav-not-detected" => "GtaVBenchmark.Failure.NotDetected",
                "gtav-still-running" => "GtaVBenchmark.Failure.StillRunning",
                "gta-executable-not-found" => "GtaVBenchmark.Failure.NotDetected",
                "profile-folder-not-found" => "GtaVBenchmark.Failure.OutputNotFound",
                "benchmark-output-file-not-found" => "GtaVBenchmark.Failure.OutputNotFound",
                "benchmark-output-file-not-recognized" => "GtaVBenchmark.Failure.OutputNotRecognized",
                "benchmark-did-not-exit-in-time" => "GtaVBenchmark.Failure.Timeout",
                _ => "GtaVBenchmark.Failure.Generic"
            };
            return localization.GetString(reasonKey);
        }

        return localization.Format(
            "GtaVBenchmark.Result",
            result.Median.AverageFps,
            result.Median.MinimumFps,
            result.Median.OnePercentLowFps,
            result.Median.PointOnePercentLowFps,
            result.Iterations.Count);
    }

    public async Task<bool> RevertLastOptimizationAsync()
    {
        if (lastTransactionId is not { } id)
        {
            return false;
        }

        var item = HistoryItems.FirstOrDefault(candidate => candidate.TransactionId == id);
        return item is not null && await RollbackAsync(item);
    }

    public async Task<bool> RollbackAsync(HistoryDisplayItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsBusy || isWindowsGamingBusy || !item.CanRollback)
        {
            return false;
        }

        operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        ClearProgressHistory();
        StartOperationTiming();
        var progress = new Progress<AppProgressUpdate>(ApplyProgress);
        var completedSuccessfully = false;
        var rolledBackWindowsGamingTransaction = false;
        try
        {
            var restored = await service.RollbackAsync(item.TransactionId, progress, operationCancellation.Token);
            completedSuccessfully = restored;
            if (restored && windowsGamingTransactionId == item.TransactionId)
            {
                windowsGamingTransactionId = null;
                rolledBackWindowsGamingTransaction = true;
            }
            ApplyHistory(await service.LoadHistoryAsync());
            return restored;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            FinalizeHeadline(localization.GetString("Status.CouldNotRestore"));
            return false;
        }
        finally
        {
            StopOperationTiming(completedSuccessfully);
            operationCancellation.Dispose();
            operationCancellation = null;
            IsBusy = false;
            if (rolledBackWindowsGamingTransaction)
            {
                await RefreshWindowsGamingSettingsAsync();
            }
        }
    }

    private void RefreshPlan()
    {
        currentPlan = BuildPlan(
            selectedProfile,
            optimizationScope,
            IsUltraSelected ? personalPreferences : null);

        PlannedActions.Clear();
        PlannedAdjustments.Clear();
        InformationalPlannedActions.Clear();
        foreach (var action in currentPlan.Actions)
        {
            var displayItem = ToDisplayItem(action.Metadata);
            PlannedActions.Add(displayItem);
            (action.Metadata.Risk == ActionRisk.Informational
                ? InformationalPlannedActions
                : PlannedAdjustments).Add(displayItem);
        }

        OnPropertyChanged(nameof(SelectedActionCount));
        OnPropertyChanged(nameof(AutomaticAnalysisCount));
        OnPropertyChanged(nameof(HasPlannedAdjustments));
        OnPropertyChanged(nameof(HasAutomaticAnalysis));
        OnPropertyChanged(nameof(HasPlannedActions));
        OnPropertyChanged(nameof(ElevationLabel));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(PlanHeader));
        OnPropertyChanged(nameof(AutomaticAnalysisHeader));
        OnPropertyChanged(nameof(PlanNoticesText));
        OnPropertyChanged(nameof(EmptyPlanMessage));
        OnPropertyChanged(nameof(SafetySummary));
        OnPropertyChanged(nameof(AboutVersionDeveloper));
        OnPropertyChanged(nameof(OptimizerTitle));
        OnPropertyChanged(nameof(OptimizerSubtitle));
        RefreshProfilePresentation();
        RaiseCommandState();
    }

    private OptimizationPlanDto BuildPlan(
        OptimizationProfile profile,
        OptimizationScope scope,
        PersonalOptimizationPreferencesDto? preferences = null)
    {
        var edition = scope == OptimizationScope.FiveMLegacy
            ? diagnostic?.Edition ?? FiveMEdition.Unknown
            : FiveMEdition.Unknown;
        var options = new OptimizationOptionsDto
        {
            CleanUserTemporaryFiles = true,
            TemporaryFileMinimumAgeDays = profile switch
            {
                OptimizationProfile.Light => 30,
                OptimizationProfile.Balanced => 14,
                _ => 7
            },
            RemoveOldFiveMCrashDumps = scope == OptimizationScope.FiveMLegacy,
            DiagnosticRetentionDays = profile == OptimizationProfile.Aggressive ? 7 : 14,
            ServerCacheRepair = CacheRepairPolicy.Off,
            ServerCacheThresholdGiB = 8,
            EnableGameMode = true,
            PreferHighPerformanceGpu = scope == OptimizationScope.FiveMLegacy,
            DisableBackgroundCapture = true,
            UseSessionPerformancePowerPlan = profile != OptimizationProfile.Light,
            ToggleHagsExperiment = IsStoreCertificationCandidate && StoreHagsExperiment,
            ApplyLegacyGraphicsPreset = scope == OptimizationScope.FiveMLegacy,
            ApplyGtaVGraphicsPreset = scope == OptimizationScope.FiveMLegacy
                && diagnostic?.GtaVDetected == true,
            ReduceWindowsVisualEffects = profile == OptimizationProfile.Aggressive,
            ReduceMenuShowDelay = profile != OptimizationProfile.Light
        };

        return PlanBuilder.Build(
            new OptimizationPlanRequestDto
            {
                Profile = profile,
                Scope = scope,
                Edition = edition,
                Options = options,
                PersonalPreferences = preferences
            },
            PlanBuildContext.New(TimeProvider.System));
    }

    public RalvenAiPcContext? CreateRalvenAiContext()
    {
        if (diagnostic is null)
        {
            return null;
        }

        var profiles = Enum.GetValues<OptimizationProfile>()
            .Select(profile => new RalvenAiProfileContext(
                profile.ToString().ToLowerInvariant(),
                BuildPlan(profile, OptimizationScope.GeneralWindows).Actions
                    .Select(action => new RalvenAiActionContext(
                        action.Metadata.Id,
                        action.Metadata.Name,
                        action.Metadata.ExpectedImpact,
                        action.Metadata.Risk.ToString(),
                        action.Metadata.Reversibility is ActionReversibility.ReadOnly
                            or ActionReversibility.FullyReversible
                            or ActionReversibility.SessionScoped))
                    .ToArray()))
            .ToArray();

        return new RalvenAiPcContext(
            diagnostic.CpuName,
            diagnostic.GpuName,
            diagnostic.TotalMemoryGiB,
            diagnostic.AvailableMemoryGiB,
            diagnostic.LogicalProcessorCount,
            diagnostic.FreeDiskGiB,
            diagnostic.OsLabel,
            diagnostic.SystemArchitecture,
            diagnostic.ReadinessScore,
            diagnostic.PerformancePressure.ToString().ToLowerInvariant(),
            diagnostic.RecommendedProfile.ToString().ToLowerInvariant(),
            profiles);
    }

    private void RefreshProfilePresentation()
    {
        var presentation = ProfilePresentationProvider.For(selectedProfile, optimizationScope);
        ProfilePresentationBenefits = localization.GetString(
            $"Profiles.Presentation.{optimizationScope}.{selectedProfile}.Benefits");
        ProfilePresentationImpact = localization.GetString($"Profiles.Presentation.Impact.{presentation.ImpactLevel}");
        if (IsUltraSelected)
        {
            ProfilePresentationBenefits = localization.GetString("Ultra.Description");
            ProfilePresentationImpact = localization.GetString($"Risk.{currentPlan?.MaximumRisk ?? ActionRisk.Informational}");
        }
    }

    private ActionDisplayItem ToDisplayItem(ActionMetadataDto action)
    {
        var icon = action.Category switch
        {
            ActionCategory.Safety => "\uEA18",
            ActionCategory.Storage => "\uE958",
            ActionCategory.WindowsGaming => "\uE7FC",
            ActionCategory.Power => "\uE945",
            ActionCategory.Appearance => "\uE790",
            ActionCategory.FiveMGraphics => "\uE7F8",
            _ => "\uE946"
        };
        var risk = action.Risk switch
        {
            ActionRisk.Informational => localization.GetString("Risk.Informational"),
            ActionRisk.Low => localization.GetString("Risk.Low"),
            ActionRisk.Moderate => localization.GetString("Risk.Moderate"),
            ActionRisk.High => localization.GetString("Risk.HighReversible"),
            _ => action.Risk.ToString().ToUpperInvariant()
        };
        var riskBrushKey = action.Risk switch
        {
            ActionRisk.Informational => "TextTertiaryBrush",
            ActionRisk.Low => "InfoBaseBrush",
            ActionRisk.Moderate => "WarningBaseBrush",
            ActionRisk.High => "DangerBaseBrush",
            _ => "TextTertiaryBrush"
        };
        var requiresElevation = action.RequiredPrivilege == RequiredPrivilege.Administrator;
        var privilege = requiresElevation
            ? localization.GetString("Privilege.RequiresUac")
            : action.Reversibility is ActionReversibility.Irreversible or ActionReversibility.RebuildableData
                ? localization.GetString("Privilege.PermanentCleanup")
                : localization.GetString("Privilege.Reversible");
        var primaryCaution = action.Reversibility is ActionReversibility.Irreversible or ActionReversibility.RebuildableData
            ? privilege
            : string.Empty;
        var categoryLabel = action.Category switch
        {
            ActionCategory.Safety => localization.GetString("Category.Safety"),
            ActionCategory.Storage => localization.GetString("Category.Storage"),
            ActionCategory.WindowsGaming => localization.GetString("Category.WindowsGaming"),
            ActionCategory.Power => localization.GetString("Category.Power"),
            ActionCategory.Appearance => localization.GetString("Category.Appearance"),
            ActionCategory.FiveMGraphics => localization.GetString("Category.FiveMGraphics"),
            _ => action.Category.ToString()
        };
        // Cada texto da ação cai para o conteúdo do catálogo quando a chave de
        // localização correspondente não existe no idioma atual.
        string Localized(string suffix, string fallback) =>
            localization.GetStringOrFallback($"Actions.{action.Id}.{suffix}", fallback);
        return new ActionDisplayItem(
            action.Id,
            Localized("Name", action.Name),
            Localized("Description", action.Description),
            Localized("DetectionSummary", action.DetectionSummary),
            Localized("ConfirmationSummary", action.ConfirmationSummary),
            Localized("UndoSummary", action.UndoSummary),
            Localized("RiskLimitations", action.RiskLimitations),
            icon,
            risk,
            riskBrushKey,
            privilege,
            primaryCaution,
            categoryLabel);
    }

    private string LocalizeNotice(PlanNoticeDto notice) => notice.Code switch
    {
        "diagnostics-removal-is-permanent" => localization.Format(
            "Plan.Notice.DiagnosticsRetention",
            currentPlan?.Options.DiagnosticRetentionDays ?? 14),
        "server-cache-will-be-rebuilt" => localization.GetString("Plan.Notice.ServerCacheRepair"),
        "performance-power-requires-ac" => localization.GetString("Plan.Notice.AcPower"),
        "aggressive-windows-prioritizes-performance" => localization.GetString("Plan.Notice.AggressiveWindows"),
        "aggressive-prioritizes-performance" => localization.GetString("Plan.Notice.AggressiveVisual"),
        _ => notice.Message
    };

    private string ProfileName(OptimizationProfile profile) => profile switch
    {
        OptimizationProfile.Light => localization.GetString("Profiles.Light.Name"),
        OptimizationProfile.Balanced => localization.GetString("Profiles.Balanced.Name"),
        OptimizationProfile.Aggressive => localization.GetString("Profiles.Aggressive.Name"),
        _ => profile.ToString()
    };
}
