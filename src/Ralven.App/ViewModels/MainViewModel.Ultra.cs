using System.Collections.ObjectModel;
using System.Windows.Threading;
using Ralven.App.Services;
using Ralven.Contracts;
using Ralven.Core.Planning;

namespace Ralven.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly PersonalWorkspaceService personalWorkspaceService;
    private readonly CancellationTokenSource personalLifetime = new();
    private CancellationTokenSource? personalOperation;
    private DispatcherTimer? personalTrackingTimer;
    private PersonalWorkspace personalWorkspace = new();
    private PersonalOptimizationPreferencesDto personalPreferences = new();
    private bool isUltraSelected;
    private bool hasProAccess;
    private bool hasRalvenAiAccess;
    private bool isPersonalBusy;
    private bool refreshingUltra;
    private string ultraStatus = string.Empty;
    private string measurementContext = string.Empty;
    private int selectedMeasurementIndex = -1;
    private int baselineMeasurementIndex = -1;

    public bool ShowPersonalUpgrade => !hasProAccess;
    public bool HasPersonalMeasurements => personalWorkspace.Measurements.Count > 0;
    public bool HasNoPersonalMeasurements => !HasPersonalMeasurements;
    public bool HasPersonalRecords => personalWorkspace.Measurements.Count > 0 || personalWorkspace.Changes.Count > 0;
    public string PersonalHistorySummary => localization.Format("Personal.History.Count", personalWorkspace.Measurements.Count, personalWorkspace.Changes.Count);
    public string PersonalChangeSummary => personalWorkspace.Changes.LastOrDefault() is { } change
        ? localization.Format("Personal.Tracking.Latest", localization.GetString($"Ultra.Change.{change.Kind}"), change.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture))
        : localization.GetString("Ultra.Tracking.NoChanges");
    public IReadOnlyList<string> PersonalMeasurementChoices => personalWorkspace.Measurements.Select(item =>
        $"{item.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture)} · {item.Context} · {localization.GetString($"Ultra.Usage.{item.Usage}")}").ToArray();
    public ObservableCollection<PersonalMetricDisplay> PersonalMetricRows { get; } = [];
    public int SelectedPersonalMeasurementIndex
    {
        get => selectedMeasurementIndex;
        set
        {
            if (refreshingUltra || value < 0 || value >= personalWorkspace.Measurements.Count || selectedMeasurementIndex == value) return;
            selectedMeasurementIndex = value;
            baselineMeasurementIndex = FindBaseline(value);
            RefreshPersonalComparison();
        }
    }
    public int BaselinePersonalMeasurementIndex
    {
        get => baselineMeasurementIndex;
        set
        {
            if (refreshingUltra || value < 0 || value >= personalWorkspace.Measurements.Count || baselineMeasurementIndex == value) return;
            baselineMeasurementIndex = value;
            RefreshPersonalComparison();
        }
    }

    private int FindBaseline(int selected) => selected < 0 ? -1 :
        Enumerable.Range(0, selected).LastOrDefault(index => PersonalTimelineAnalysis.CanCompare(
            personalWorkspace.Measurements[index], personalWorkspace.Measurements[selected]), -1);

    public Task<string> ExportPersonalWorkspaceAsync(CancellationToken cancellationToken = default) =>
        personalWorkspaceService.ExportAsync(cancellationToken);

    public void ReportPersonalExportResult(bool succeeded) =>
        UltraStatus = localization.GetString(succeeded ? "Personal.Export.Saved" : "Personal.Export.Failed");


    public bool IsUltraSelected => isUltraSelected && IsGeneralWindowsOptimization;
    public bool HasProAccess => hasProAccess;
    public bool HasRalvenAiAccess => hasRalvenAiAccess;
    public bool IsRalvenAiAvailable => ProFeatureAvailability.Enabled && hasRalvenAiAccess;
    public bool IsPersonalBusy => isPersonalBusy;
    public bool CanEditPersonalPreferences => !IsBusy && !isPersonalBusy && !isWindowsGamingBusy;
    public bool CanSavePersonalProfile => hasProAccess && CanEditPersonalPreferences;
    public bool CanUsePersonalTools => CanSavePersonalProfile && !isInitializing && diagnostic is not null && !diagnosticFailed;
    public bool CanCheckPersonalTracking => CanUsePersonalTools && personalWorkspace.TrackingEnabled;
    public bool CanStopPersonalTracking => personalWorkspace.TrackingEnabled && CanEditPersonalPreferences;
    public string UltraStatus { get => ultraStatus; private set => SetProperty(ref ultraStatus, value); }
    public string PersonalUsageDetail => localization.GetString($"Ultra.Usage.{personalPreferences.Usage}.Detail");
    public string PersonalRecommendationSummary => diagnostic switch
    {
        null => localization.GetString("Ultra.Smart.NeedsDiagnosis"),
        { PerformancePressure: PerformancePressureLevel.High } => localization.GetString("Ultra.Smart.HighPressure"),
        { StreamingSoftware.Applications: var applications } when applications.Any(item => item.IsDetected) =>
            localization.GetString("Ultra.Smart.StreamingDetected"),
        _ => localization.GetString("Ultra.Smart.Ready")
    };
    public string MeasurementContext { get => measurementContext; set => SetProperty(ref measurementContext, value); }
    public IReadOnlyList<string> PersonalUsageLabels => Enum.GetValues<PersonalUsage>()
        .Select(usage => localization.GetString($"Ultra.Usage.{usage}")).ToArray();
    public ObservableCollection<string> PersonalChanges { get; } = [];
    public ObservableCollection<string> PersonalMeasurements { get; } = [];

    public int PersonalUsageIndex
    {
        get => (int)personalPreferences.Usage;
        set
        {
            if (refreshingUltra || !CanEditPersonalPreferences || value == (int)personalPreferences.Usage || !Enum.IsDefined((PersonalUsage)value)) return;
            personalPreferences = personalWorkspace.Profiles.FirstOrDefault(profile => profile.Usage == (PersonalUsage)value)
                ?? RecommendPersonalPreferences((PersonalUsage)value);
            RefreshUltraPresentation();
            RefreshPlan();
        }
    }

    public bool PersonalPreserveAppearance
    {
        get => personalPreferences.PreserveAppearance;
        set => UpdatePersonalPreferences(personalPreferences with { PreserveAppearance = value });
    }

    public bool PersonalPreserveCapture
    {
        get => personalPreferences.PreserveBackgroundCapture;
        set => UpdatePersonalPreferences(personalPreferences with { PreserveBackgroundCapture = value });
    }

    public bool PersonalAllowPerformancePower
    {
        get => personalPreferences.AllowPerformancePower;
        set => UpdatePersonalPreferences(personalPreferences with { AllowPerformancePower = value });
    }

    public bool PersonalCleanTemporaryFiles
    {
        get => personalPreferences.CleanOldTemporaryFiles;
        set => UpdatePersonalPreferences(personalPreferences with { CleanOldTemporaryFiles = value });
    }

    public bool PersonalUseConsistentPointerResponse
    {
        get => personalPreferences.UseConsistentPointerResponse;
        set => UpdatePersonalPreferences(personalPreferences with { UseConsistentPointerResponse = value });
    }

    public string PersonalTrackingSummary => personalWorkspace.LastObservation is { } observed
        ? localization.Format(personalWorkspace.TrackingEnabled && hasProAccess ? "Ultra.Tracking.Active" : "Ultra.Tracking.Paused",
            observed.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture))
        : localization.GetString("Ultra.Tracking.Empty");

    public string PersonalComparisonSummary
    {
        get
        {
            if (selectedMeasurementIndex < 0 || selectedMeasurementIndex >= personalWorkspace.Measurements.Count)
                return localization.GetString("Ultra.Measure.Empty");
            var latest = personalWorkspace.Measurements[selectedMeasurementIndex];
            if (baselineMeasurementIndex < 0 || baselineMeasurementIndex >= selectedMeasurementIndex)
                return localization.GetString("Ultra.Measure.NeedMatch");
            var previous = personalWorkspace.Measurements[baselineMeasurementIndex];
            if (!PersonalTimelineAnalysis.CanCompare(previous, latest)) return localization.GetString("Ultra.Measure.NeedMatch");
            return localization.Format("Ultra.Measure.Difference",
                previous.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture),
                latest.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture),
                Difference(previous.CpuPercent, latest.CpuPercent), Difference(previous.GpuPercent, latest.GpuPercent),
                Difference(previous.MemoryPercent, latest.MemoryPercent), Difference(previous.DiskPercent, latest.DiskPercent));
        }
    }

    public void SelectUltra()
    {
        if (!IsGeneralWindowsOptimization || !CanEditPersonalPreferences) return;
        var enteringUltra = !isUltraSelected;
        isUltraSelected = true;
        selectedProfile = OptimizationProfile.Aggressive;
        profileInitializedFromDiagnostic = true;
        if (enteringUltra && personalWorkspace.Profiles.All(profile => profile.Usage != personalPreferences.Usage))
        {
            personalPreferences = RecommendPersonalPreferences(personalPreferences.Usage);
        }
        ApplyReport(null);
        RefreshUltraPresentation();
        RefreshPlan();
    }

    public void SetProAccess(bool available)
    {
        hasProAccess = available;
        if (!available)
        {
            SetRalvenAiAccess(false);
        }
        RefreshUltraPresentation();
        RaiseCommandState();
    }

    public void SetRalvenAiAccess(bool available)
    {
        SetProperty(ref hasRalvenAiAccess, available && hasProAccess);
        OnPropertyChanged(nameof(IsRalvenAiAvailable));
    }

    private void UpdatePersonalPreferences(PersonalOptimizationPreferencesDto preferences)
    {
        if (refreshingUltra || !CanEditPersonalPreferences || personalPreferences == preferences) return;
        personalPreferences = preferences;
        RefreshUltraPresentation();
        RefreshPlan();
    }

    public void ApplyPersonalRecommendation()
    {
        if (!CanEditPersonalPreferences) return;
        UpdatePersonalPreferences(RecommendPersonalPreferences(personalPreferences.Usage));
        UltraStatus = localization.GetString("Ultra.Smart.Applied");
    }

    private PersonalOptimizationPreferencesDto RecommendPersonalPreferences(PersonalUsage usage) =>
        PersonalOptimizationPolicy.Recommend(
            usage,
            diagnostic?.PerformancePressure == PerformancePressureLevel.High,
            diagnostic?.StreamingSoftware.Applications.Any(item => item.IsDetected) == true);

    private async Task InitializePersonalWorkspaceAsync()
    {
        try
        {
            personalWorkspace = await personalWorkspaceService.LoadAsync(personalLifetime.Token);
            personalPreferences = personalWorkspace.Profiles.FirstOrDefault(profile => profile.Usage == personalPreferences.Usage)
                ?? personalPreferences;
            RefreshUltraPresentation();
            personalTrackingTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
            personalTrackingTimer.Tick += PersonalTrackingTimer_Tick;
            personalTrackingTimer.Start();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            UltraStatus = localization.GetString("Ultra.Storage.Error");
        }
    }

    private async void PersonalTrackingTimer_Tick(object? sender, EventArgs e) => await ObservePersonalPcAsync();

    public Task SavePersonalProfileAsync() => RunPersonalOperationAsync(async cancellationToken =>
    {
        personalWorkspace = await personalWorkspaceService.SaveProfileAsync(personalPreferences, cancellationToken);
        UltraStatus = localization.GetString("Ultra.Profile.Saved");
    });

    public Task StartPersonalTrackingAsync() => RunPersonalOperationAsync(async cancellationToken =>
    {
        var observation = await CapturePersonalObservationAsync(cancellationToken);
        personalWorkspace = await personalWorkspaceService.SetTrackingAsync(true, observation, cancellationToken);
        UltraStatus = localization.GetString("Ultra.Tracking.Started");
    });

    public Task StopPersonalTrackingAsync() => RunPersonalOperationAsync(async cancellationToken =>
    {
        personalWorkspace = await personalWorkspaceService.SetTrackingAsync(false, null, cancellationToken);
        UltraStatus = localization.GetString("Ultra.Tracking.Stopped");
    });

    public async Task ObservePersonalPcAsync()
    {
        if (!personalWorkspace.TrackingEnabled || !CanUsePersonalTools || personalLifetime.IsCancellationRequested) return;
        await RunPersonalOperationAsync(async cancellationToken =>
        {
            personalWorkspace = await personalWorkspaceService.ObserveAsync(
                await CapturePersonalObservationAsync(cancellationToken), cancellationToken);
        });
    }

    public Task MeasurePersonalSessionAsync() => RunPersonalOperationAsync(async cancellationToken =>
    {
        var observation = await CapturePersonalObservationAsync(cancellationToken);
        var progress = new Progress<int>(count => UltraStatus = localization.Format("Ultra.Measure.Progress", count));
        personalWorkspace = await personalWorkspaceService.MeasureAsync(
            personalPreferences.Usage, MeasurementContext, observation, progress, cancellationToken);
        selectedMeasurementIndex = -1;
        UltraStatus = localization.GetString("Ultra.Measure.Saved");
    });

    public void CancelPersonalOperation() => personalOperation?.Cancel();

    private async Task RunPersonalOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (!CanEditPersonalPreferences || personalLifetime.IsCancellationRequested) return;
        isPersonalBusy = true;
        personalOperation = CancellationTokenSource.CreateLinkedTokenSource(personalLifetime.Token);
        RefreshUltraPresentation();
        RaiseCommandState();
        try { await operation(personalOperation.Token); }
        catch (OperationCanceledException) { UltraStatus = localization.GetString("Ultra.Operation.Cancelled"); }
        catch (ProAccessRequiredException)
        {
            SetProAccess(false);
            UltraStatus = localization.GetString("Ultra.AccessRequired");
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            UltraStatus = exception is ArgumentException
                ? localization.GetString("Ultra.Measure.ContextRequired")
                : localization.GetString("Ultra.Operation.Failed");
        }
        finally
        {
            personalOperation.Dispose();
            personalOperation = null;
            isPersonalBusy = false;
            RefreshUltraPresentation();
            RaiseCommandState();
        }
    }

    private Task<PcObservation> CapturePersonalObservationAsync(CancellationToken cancellationToken) =>
        personalWorkspaceService.CaptureObservationAsync(
            diagnostic ?? throw new InvalidOperationException("The PC has not been diagnosed."), windowsGamingControls, cancellationToken);

    private string Metric(double? value) => value is { } number
        ? number.ToString("0.0", localization.CurrentCulture) + "%"
        : localization.GetString("Ultra.Unavailable");

    private string Difference(double? previous, double? current) => previous is { } first && current is { } second
        ? (second - first).ToString("+0.0;-0.0;0.0", localization.CurrentCulture)
        : localization.GetString("Ultra.Unavailable");

    private void RefreshPersonalComparison()
    {
        OnPropertyChanged(nameof(SelectedPersonalMeasurementIndex));
        OnPropertyChanged(nameof(BaselinePersonalMeasurementIndex));
        OnPropertyChanged(nameof(PersonalComparisonSummary));
        PersonalMetricRows.Clear();
        if (selectedMeasurementIndex < 0 || selectedMeasurementIndex >= personalWorkspace.Measurements.Count) return;
        var current = personalWorkspace.Measurements[selectedMeasurementIndex];
        var baseline = baselineMeasurementIndex >= 0 && baselineMeasurementIndex < selectedMeasurementIndex
            ? personalWorkspace.Measurements[baselineMeasurementIndex] : null;
        if (baseline is not null && !PersonalTimelineAnalysis.CanCompare(baseline, current)) baseline = null;
        void Add(string name, double? before, double? after) => PersonalMetricRows.Add(new(
            localization.GetString(name), Metric(after), Metric(before), before.HasValue && after.HasValue
                ? localization.Format("Personal.Measure.Delta", Difference(before, after))
                : localization.GetString("Ultra.Unavailable")));
        Add("Personal.Metric.Cpu", baseline?.CpuPercent, current.CpuPercent);
        Add("Personal.Metric.Gpu", baseline?.GpuPercent, current.GpuPercent);
        Add("Personal.Metric.Memory", baseline?.MemoryPercent, current.MemoryPercent);
        Add("Personal.Metric.Disk", baseline?.DiskPercent, current.DiskPercent);
    }

    private void RefreshUltraPresentation()
    {
        refreshingUltra = true;
        foreach (var property in new[]
        {
            nameof(IsUltraSelected), nameof(HasProAccess), nameof(IsRalvenAiAvailable), nameof(IsPersonalBusy), nameof(CanEditPersonalPreferences),
            nameof(CanSavePersonalProfile), nameof(CanUsePersonalTools), nameof(CanCheckPersonalTracking), nameof(CanStopPersonalTracking),
            nameof(PersonalUsageLabels), nameof(PersonalUsageIndex), nameof(PersonalUsageDetail), nameof(PersonalPreserveAppearance),
            nameof(PersonalPreserveCapture), nameof(PersonalAllowPerformancePower), nameof(PersonalCleanTemporaryFiles),
            nameof(PersonalUseConsistentPointerResponse), nameof(PersonalRecommendationSummary),
            nameof(PersonalTrackingSummary), nameof(PersonalComparisonSummary), nameof(SelectedProfileName),
            nameof(SelectedProfileLabel), nameof(IsSelectedProfileRecommended), nameof(IsLightSelected), nameof(IsBalancedSelected), nameof(IsAggressiveSelected)
        }) OnPropertyChanged(property);
        refreshingUltra = false;
        foreach (var property in new[] { nameof(ShowPersonalUpgrade), nameof(HasPersonalRecords), nameof(HasPersonalMeasurements), nameof(HasNoPersonalMeasurements), nameof(PersonalHistorySummary), nameof(PersonalChangeSummary) })
            OnPropertyChanged(property);
        if (selectedMeasurementIndex < 0 || selectedMeasurementIndex >= personalWorkspace.Measurements.Count)
        {
            selectedMeasurementIndex = personalWorkspace.Measurements.Count - 1;
            baselineMeasurementIndex = FindBaseline(selectedMeasurementIndex);
        }
        refreshingUltra = true;
        OnPropertyChanged(nameof(PersonalMeasurementChoices));
        refreshingUltra = false;
        RefreshPersonalComparison();
        PersonalChanges.Clear();
        var associations = PersonalTimelineAnalysis.FindAssociations(personalWorkspace.Changes, personalWorkspace.Measurements)
            .ToDictionary(association => association.Change);
        foreach (var change in personalWorkspace.Changes.Reverse())
        {
            var line = change.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture)
                + " · " + localization.GetString($"Ultra.Change.{change.Kind}");
            if (change.PreviousValue is not null || change.CurrentValue is not null)
                line += " " + localization.Format("Personal.Change.Evidence",
                    change.PreviousValue ?? localization.GetString("Ultra.Unavailable"),
                    change.CurrentValue ?? localization.GetString("Ultra.Unavailable"));
            if (associations.TryGetValue(change, out var association) && association.SymptomSummary is not null)
                line += " " + localization.Format("Personal.Change.Association", association.SymptomSummary);
            PersonalChanges.Add(line);
        }
        if (PersonalChanges.Count == 0) PersonalChanges.Add(localization.GetString("Ultra.Tracking.NoChanges"));
        PersonalMeasurements.Clear();
        foreach (var measurement in personalWorkspace.Measurements.Reverse())
            PersonalMeasurements.Add(localization.Format("Ultra.Measure.Record",
                measurement.Context, localization.GetString($"Ultra.Usage.{measurement.Usage}"),
                measurement.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture),
                Metric(measurement.CpuPercent), Metric(measurement.GpuPercent), Metric(measurement.MemoryPercent), Metric(measurement.DiskPercent)));
    }
}

public sealed record PersonalMetricDisplay(string Name, string Current, string Baseline, string Difference);
