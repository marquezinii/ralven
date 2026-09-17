using System.Collections.ObjectModel;
using Ralven.App.Services;
using Ralven.Contracts;
using Ralven.Windows.Diagnostics;
using Ralven.Windows.Infrastructure;

namespace Ralven.App.ViewModels;

internal sealed record InstalledApplicationDisplayItem(
    string Name,
    string Publisher,
    string Version,
    string EstimatedSize,
    string Scope);

internal sealed record StartupApplicationDisplayItem(
    string Name,
    string Source,
    string Scope);

internal sealed record ApplicationPackageDisplayItem(
    WindowsApplicationPackage Package,
    string Name,
    string PackageId,
    string Version,
    string Source,
    string ActionAutomationName);

internal sealed class ApplicationUpdateDisplayItem : BindableBase
{
    private readonly Action<ApplicationUpdateDisplayItem, bool> selectionChanged;
    private bool isSelected;

    public ApplicationUpdateDisplayItem(
        WindowsApplicationPackage package,
        bool isSelected,
        bool isIgnored,
        string source,
        string updateAutomationName,
        string ignoreAutomationName,
        string ignoreActionLabel,
        Action<ApplicationUpdateDisplayItem, bool> selectionChanged)
    {
        Package = package;
        this.isSelected = isSelected && !isIgnored;
        IsIgnored = isIgnored;
        Source = source;
        UpdateAutomationName = updateAutomationName;
        IgnoreAutomationName = ignoreAutomationName;
        IgnoreActionLabel = ignoreActionLabel;
        this.selectionChanged = selectionChanged;
    }

    public WindowsApplicationPackage Package { get; }

    public string Name => Package.Name;

    public string PackageId => Package.PackageId;

    public string InstalledVersion => Package.Version;

    public string AvailableVersion => Package.AvailableVersion ?? string.Empty;

    public string Source { get; }

    public bool IsIgnored { get; }

    public bool CanSelect => !IsIgnored;

    public string UpdateAutomationName { get; }

    public string IgnoreAutomationName { get; }

    public string IgnoreActionLabel { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            value &= !IsIgnored;
            if (SetProperty(ref isSelected, value))
            {
                selectionChanged(this, value);
            }
        }
    }
}

internal sealed class ApplicationsPageViewModel : BindableBase, IDisposable
{
    private readonly IWindowsApplicationInventoryInspector inspector;
    private readonly IWindowsApplicationPackageService packageService;
    private readonly IApplicationUpdateIgnoreStore ignoreStore;
    private readonly ILocalizationService localization;
    private readonly HashSet<string> ignoredUpdateKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedUpdateKeys = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WindowsInstalledApplication> installedApplications = [];
    private IReadOnlyList<WindowsStartupItem> startupItems = [];
    private IReadOnlyList<WindowsApplicationPackage> managedPackages = [];
    private IReadOnlyList<WindowsApplicationPackage> discoveredPackages = [];
    private IReadOnlyList<WindowsApplicationPackage> applicationUpdates = [];
    private WindowsApplicationInventorySnapshot? inventorySnapshot;
    private WindowsApplicationPackageSnapshot? managedSnapshot;
    private WindowsApplicationPackageSnapshot? discoverSnapshot;
    private WindowsApplicationPackageSnapshot? updateSnapshot;
    private string searchText = string.Empty;
    private string discoverQuery = string.Empty;
    private string inventoryStatusMessage;
    private string inventoryObservedAtLabel = string.Empty;
    private string managedPackagesStatusMessage;
    private string discoverStatusMessage;
    private string applicationUpdateStatusMessage;
    private string applicationUpdatesObservedAtLabel = string.Empty;
    private bool isInventoryLoading;
    private bool isLoadingManagedPackages;
    private bool isSearchingPackages;
    private bool isCheckingApplicationUpdates;
    private bool isPackageOperationRunning;
    private bool inventoryUnavailable;
    private bool showIgnoredUpdates;
    private bool showTechnicalDetails;
    private bool ignoreStoreLoaded;
    private bool ignoreStoreUnavailable;
    private BugCode? inventoryBugCode;
    private bool disposed;

    public ApplicationsPageViewModel(
        IWindowsApplicationInventoryInspector inspector,
        ILocalizationService? localization = null)
        : this(
            inspector,
            new WinGetApplicationPackageService(),
            new JsonApplicationUpdateIgnoreStore(),
            localization)
    {
    }

    public ApplicationsPageViewModel(
        IWindowsApplicationInventoryInspector inspector,
        IWindowsApplicationPackageService packageService,
        IApplicationUpdateIgnoreStore ignoreStore,
        ILocalizationService? localization = null)
    {
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        this.packageService = packageService ?? throw new ArgumentNullException(nameof(packageService));
        this.ignoreStore = ignoreStore ?? throw new ArgumentNullException(nameof(ignoreStore));
        this.localization = localization ?? LocalizationService.Current;
        inventoryStatusMessage = this.localization.GetString("Applications.Inventory.Status.Loading");
        managedPackagesStatusMessage = this.localization.GetString("Applications.Packages.Status.Loading");
        discoverStatusMessage = this.localization.GetString("Applications.Discover.Status.Ready");
        applicationUpdateStatusMessage = this.localization.GetString("Applications.Updates.Status.Checking");
        this.localization.LanguageChanged += Localization_LanguageChanged;
    }

    public ObservableCollection<InstalledApplicationDisplayItem> InstalledApplications { get; } = [];

    public ObservableCollection<StartupApplicationDisplayItem> StartupItems { get; } = [];

    public ObservableCollection<ApplicationPackageDisplayItem> ManagedPackages { get; } = [];

    public ObservableCollection<ApplicationPackageDisplayItem> DiscoveredPackages { get; } = [];

    public ObservableCollection<ApplicationUpdateDisplayItem> ApplicationUpdates { get; } = [];

    public int InstalledApplicationCount => installedApplications.Count;

    public int StartupItemCount => startupItems.Count;

    public int ManagedPackageCount => managedPackages.Count;

    public int ApplicationUpdateCount => applicationUpdates.Count(update => !IsIgnored(update));

    public int IgnoredApplicationUpdateCount => applicationUpdates.Count(IsIgnored);

    public int SelectedApplicationUpdateCount => selectedUpdateKeys.Count;

    public bool IsInventoryLoading => isInventoryLoading;

    public bool IsSearchingPackages => isSearchingPackages;

    public bool IsPackageOperationRunning => isPackageOperationRunning;

    public bool IsBusy => !CanRefreshApplications;

    public bool CanRefreshApplications => !isInventoryLoading
        && !isLoadingManagedPackages
        && !isSearchingPackages
        && !isCheckingApplicationUpdates
        && !isPackageOperationRunning;

    public bool CanSearchPackages => CanRunPackageOperation
        && discoverQuery.Trim().Length is >= 2 and <= 100;

    public bool CanRunPackageOperation => !isPackageOperationRunning
        && !isSearchingPackages
        && !isCheckingApplicationUpdates
        && !isLoadingManagedPackages;

    public bool CanUpdateSelected => CanRunPackageOperation
        && SelectedApplicationUpdateCount > 0;

    public bool HasInstalledApplications => InstalledApplications.Count > 0;

    public bool HasStartupItems => StartupItems.Count > 0;

    public bool HasManagedPackages => ManagedPackages.Count > 0;

    public bool HasDiscoveredPackages => DiscoveredPackages.Count > 0;

    public bool HasApplicationUpdates => ApplicationUpdates.Count > 0;

    public bool ShowInstalledEmptyState => (inventorySnapshot is not null || inventoryUnavailable)
        && !isInventoryLoading
        && !HasInstalledApplications;

    public bool ShowStartupEmptyState => (inventorySnapshot is not null || inventoryUnavailable)
        && !isInventoryLoading
        && !HasStartupItems;

    public bool ShowManagedPackagesEmptyState => managedSnapshot is not null
        && !isLoadingManagedPackages
        && !HasManagedPackages;

    public bool ShowDiscoveredPackagesEmptyState => discoverSnapshot is not null
        && !isSearchingPackages
        && !HasDiscoveredPackages;

    public bool ShowApplicationUpdatesEmptyState => updateSnapshot is not null
        && !isCheckingApplicationUpdates
        && !HasApplicationUpdates;

    public bool IsAllVisibleUpdatesSelected
    {
        get
        {
            var selectable = ApplicationUpdates.Where(item => !item.IsIgnored).ToArray();
            return selectable.Length > 0 && selectable.All(item => item.IsSelected);
        }
        set
        {
            foreach (var item in ApplicationUpdates.Where(item => !item.IsIgnored))
            {
                item.IsSelected = value;
            }

            OnPropertyChanged();
        }
    }

    public bool ShowIgnoredUpdates
    {
        get => showIgnoredUpdates;
        set
        {
            if (SetProperty(ref showIgnoredUpdates, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool ShowTechnicalDetails
    {
        get => showTechnicalDetails;
        set => SetProperty(ref showTechnicalDetails, value);
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public string DiscoverQuery
    {
        get => discoverQuery;
        set
        {
            if (SetProperty(ref discoverQuery, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSearchPackages));
            }
        }
    }

    public string InventoryStatusMessage
    {
        get => inventoryStatusMessage;
        private set => SetProperty(ref inventoryStatusMessage, value);
    }

    public string InventoryObservedAtLabel
    {
        get => inventoryObservedAtLabel;
        private set => SetProperty(ref inventoryObservedAtLabel, value);
    }

    public string ManagedPackagesStatusMessage
    {
        get => managedPackagesStatusMessage;
        private set => SetProperty(ref managedPackagesStatusMessage, value);
    }

    public string DiscoverStatusMessage
    {
        get => discoverStatusMessage;
        private set => SetProperty(ref discoverStatusMessage, value);
    }

    public string ApplicationUpdateStatusMessage
    {
        get => applicationUpdateStatusMessage;
        private set => SetProperty(ref applicationUpdateStatusMessage, value);
    }

    public string ApplicationUpdatesObservedAtLabel
    {
        get => applicationUpdatesObservedAtLabel;
        private set => SetProperty(ref applicationUpdatesObservedAtLabel, value);
    }

    public string InstalledEmptyMessage => GetInventoryEmptyMessage(
        inventorySnapshot?.InstalledApplicationsComplete,
        installedApplications.Count > 0,
        "Applications.Inventory.Empty.Installed",
        "Applications.Inventory.NoMatches.Installed",
        "Applications.Inventory.Incomplete.Installed",
        "Applications.Inventory.Unavailable.Installed");

    public string StartupEmptyMessage => GetInventoryEmptyMessage(
        inventorySnapshot?.StartupItemsComplete,
        startupItems.Count > 0,
        "Applications.Inventory.Empty.Startup",
        "Applications.Inventory.NoMatches.Startup",
        "Applications.Inventory.Incomplete.Startup",
        "Applications.Inventory.Unavailable.Startup");

    public string ManagedPackagesEmptyMessage => GetPackageEmptyMessage(
        managedSnapshot,
        managedPackages.Count > 0,
        "Applications.Packages.Empty.Current",
        "Applications.Packages.Empty.NoMatches");

    public string DiscoveredPackagesEmptyMessage => GetPackageEmptyMessage(
        discoverSnapshot,
        hasUnfilteredItems: false,
        "Applications.Discover.Empty.NoResults",
        "Applications.Discover.Empty.NoResults");

    public string ApplicationUpdatesEmptyMessage
    {
        get
        {
            if (updateSnapshot is { IsWinGetAvailable: false })
            {
                return localization.GetString("Applications.Updates.Empty.WinGetUnavailable");
            }

            if (updateSnapshot?.UnavailableSources.Count == 2)
            {
                return localization.GetString("Applications.Updates.Empty.Unavailable");
            }

            if (!showIgnoredUpdates && applicationUpdates.Count > 0
                && applicationUpdates.All(IsIgnored))
            {
                return localization.GetString("Applications.Updates.Empty.AllIgnored");
            }

            return applicationUpdates.Count > 0
                ? localization.GetString("Applications.Updates.Empty.NoMatches")
                : localization.GetString("Applications.Updates.Empty.Current");
        }
    }

    public IReadOnlyList<ApplicationUpdateDisplayItem> GetSelectedUpdates() => ApplicationUpdates
        .Where(item => item.IsSelected && !item.IsIgnored)
        .ToArray();

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (isInventoryLoading)
        {
            return;
        }

        SetInventoryLoading(true);
        InventoryStatusMessage = localization.GetString("Applications.Inventory.Status.Loading");
        await RunGuardedAsync(
            async () =>
            {
                inventorySnapshot = await inspector.InspectAsync(cancellationToken);
                inventoryUnavailable = false;
                installedApplications = inventorySnapshot.InstalledApplications;
                startupItems = inventorySnapshot.StartupItems;
                ApplyInventoryStatus();
                inventoryBugCode = null;
            },
            exception =>
            {
                inventoryUnavailable = true;
                inventoryBugCode = BugCodeClassifier.ClassifyException(exception, "app-inventory");
                InventoryStatusMessage = OptimizationFailureMessageFormatter.AppendCode(
                    localization.GetString("Applications.Inventory.Status.Unavailable"),
                    inventoryBugCode,
                    code => localization.Format("Report.ErrorCodeSuffix", code))!;
                installedApplications = [];
                startupItems = [];
                ApplyFilter();
            },
            () => SetInventoryLoading(false),
            cancellationToken);
    }

    public async Task RefreshManagedPackagesAsync(CancellationToken cancellationToken = default)
    {
        if (isLoadingManagedPackages || isPackageOperationRunning)
        {
            return;
        }

        SetLoadingManagedPackages(true);
        ManagedPackagesStatusMessage = localization.GetString("Applications.Packages.Status.Loading");
        await RunGuardedAsync(
            async () =>
            {
                managedSnapshot = await packageService.ListInstalledAsync(cancellationToken);
                managedPackages = managedSnapshot.Packages;
                ManagedPackagesStatusMessage = GetSnapshotStatus(
                    managedSnapshot,
                    managedPackages.Count > 0
                        ? "Applications.Packages.Status.Ready"
                        : "Applications.Packages.Status.Empty");
                ApplyFilter();
            },
            _ =>
            {
                managedSnapshot = UnavailablePackageSnapshot();
                managedPackages = [];
                ManagedPackagesStatusMessage = localization.GetString("Applications.Packages.Status.Unavailable");
                ApplyFilter();
            },
            () => SetLoadingManagedPackages(false),
            cancellationToken);
    }

    public async Task CheckApplicationUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (isCheckingApplicationUpdates || isPackageOperationRunning)
        {
            return;
        }

        SetCheckingApplicationUpdates(true);
        ApplicationUpdateStatusMessage = localization.GetString("Applications.Updates.Status.Checking");
        await RunGuardedAsync(
            async () =>
            {
                await EnsureIgnoreStoreLoadedAsync(cancellationToken);
                updateSnapshot = await packageService.CheckUpdatesAsync(cancellationToken);
                applicationUpdates = updateSnapshot.Packages;
                selectedUpdateKeys.RemoveWhere(key => !applicationUpdates.Any(update =>
                    PackageKey(update).Equals(key, StringComparison.OrdinalIgnoreCase)));
                ApplyApplicationUpdateStatus();
            },
            _ =>
            {
                updateSnapshot = UnavailablePackageSnapshot();
                applicationUpdates = [];
                selectedUpdateKeys.Clear();
                ApplicationUpdatesObservedAtLabel = string.Empty;
                ApplicationUpdateStatusMessage = localization.GetString("Applications.Updates.Status.Unavailable");
                ApplyFilter();
            },
            () => SetCheckingApplicationUpdates(false),
            cancellationToken);
    }

    public async Task SearchPackagesAsync(CancellationToken cancellationToken = default)
    {
        var query = discoverQuery.Trim();
        if (!CanSearchPackages || query.Length is < 2 or > 100)
        {
            return;
        }

        SetSearchingPackages(true);
        DiscoverStatusMessage = localization.GetString("Applications.Discover.Status.Searching");
        await RunGuardedAsync(
            async () =>
            {
                discoverSnapshot = await packageService.SearchAsync(query, cancellationToken);
                discoveredPackages = discoverSnapshot.Packages;
                DiscoverStatusMessage = GetSnapshotStatus(
                    discoverSnapshot,
                    discoveredPackages.Count > 0
                        ? "Applications.Discover.Status.Results"
                        : "Applications.Discover.Status.NoResults");
                ApplyDiscoveredPackages();
            },
            _ =>
            {
                discoverSnapshot = UnavailablePackageSnapshot();
                discoveredPackages = [];
                DiscoverStatusMessage = localization.GetString("Applications.Discover.Status.Unavailable");
                ApplyDiscoveredPackages();
            },
            () => SetSearchingPackages(false),
            cancellationToken);
    }

    public Task InstallPackageAsync(
        ApplicationPackageDisplayItem item,
        CancellationToken cancellationToken = default) => ExecutePackageAsync(
        WindowsApplicationPackageOperation.Install,
        item,
        cancellationToken);

    public Task UninstallPackageAsync(
        ApplicationPackageDisplayItem item,
        CancellationToken cancellationToken = default) => ExecutePackageAsync(
        WindowsApplicationPackageOperation.Uninstall,
        item,
        cancellationToken);

    public Task UpdateApplicationAsync(
        ApplicationUpdateDisplayItem item,
        CancellationToken cancellationToken = default) => UpdateApplicationsAsync(
        [item],
        cancellationToken);

    public Task UpdateSelectedApplicationsAsync(
        CancellationToken cancellationToken = default) => UpdateApplicationsAsync(
        GetSelectedUpdates(),
        cancellationToken);

    public async Task SetUpdateIgnoredAsync(
        ApplicationUpdateDisplayItem item,
        bool ignored,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanRunPackageOperation)
        {
            return;
        }

        SetPackageOperationRunning(true);
        try
        {
            await EnsureIgnoreStoreLoadedAsync(cancellationToken);
            var key = PackageKey(item.Package);
            var changed = ignored
                ? ignoredUpdateKeys.Add(key)
                : ignoredUpdateKeys.Remove(key);
            if (!changed)
            {
                return;
            }

            try
            {
                await ignoreStore.SaveAsync(ignoredUpdateKeys, cancellationToken);
                ignoreStoreUnavailable = false;
                if (ignored)
                {
                    selectedUpdateKeys.Remove(key);
                }

                ApplicationUpdateStatusMessage = localization.Format(
                    ignored
                        ? "Applications.Updates.Status.Ignored"
                        : "Applications.Updates.Status.Restored",
                    item.Name);
                ApplyFilter();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RestoreIgnoredPreference(key, ignored);
                ApplyFilter();
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                RestoreIgnoredPreference(key, ignored);
                ApplicationUpdateStatusMessage = localization.GetString(
                    "Applications.Updates.Status.IgnoreSaveFailed");
                ApplyFilter();
            }
        }
        finally
        {
            SetPackageOperationRunning(false);
        }
    }

    private void RestoreIgnoredPreference(string key, bool ignored)
    {
        if (ignored)
        {
            ignoredUpdateKeys.Remove(key);
        }
        else
        {
            ignoredUpdateKeys.Add(key);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        localization.LanguageChanged -= Localization_LanguageChanged;
    }

    private async Task ExecutePackageAsync(
        WindowsApplicationPackageOperation operation,
        ApplicationPackageDisplayItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var source = operation == WindowsApplicationPackageOperation.Install
            ? discoveredPackages
            : managedPackages;
        var current = source.FirstOrDefault(package => SamePackage(package, item.Package));
        if (!CanRunPackageOperation || current is null)
        {
            return;
        }

        SetPackageOperationRunning(true);
        SetOperationStatus(operation, "Running", current.Name);
        await RunGuardedAsync(
            async () =>
            {
                var result = await packageService.ExecuteAsync(operation, current, cancellationToken);
                SetOperationResultStatus(operation, current.Name, result);
                if (result.Outcome is WindowsApplicationPackageOutcome.Succeeded
                    or WindowsApplicationPackageOutcome.RebootRequired)
                {
                    if (operation == WindowsApplicationPackageOperation.Install)
                    {
                        managedPackages = managedPackages
                            .Append(current with { AvailableVersion = null })
                            .DistinctBy(PackageKey, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }
                    else
                    {
                        managedPackages = managedPackages
                            .Where(package => !SamePackage(package, current))
                            .ToArray();
                        applicationUpdates = applicationUpdates
                            .Where(package => !SamePackage(package, current))
                            .ToArray();
                        selectedUpdateKeys.Remove(PackageKey(current));
                    }

                    ApplyFilter();
                }
            },
            _ => SetOperationStatus(operation, "Failed", current.Name, localization.GetString("Common.Unknown")),
            () => SetPackageOperationRunning(false),
            cancellationToken);
    }

    /// <summary>
    /// Executa <paramref name="body"/> tratando cancelamento como saída
    /// silenciosa e qualquer outra falha por <paramref name="onFailure"/>,
    /// sempre encerrando com <paramref name="complete"/>.
    /// </summary>
    /// <remarks>
    /// Os cinco carregamentos desta página repetiam o mesmo par de
    /// <c>catch</c>, incluindo a exclusão de <see cref="OutOfMemoryException"/>,
    /// <see cref="StackOverflowException"/> e <c>AccessViolationException</c>.
    /// Com o filtro em um lugar só, um novo carregamento não pode esquecê-lo.
    /// As operações cujo cancelamento precisa restaurar estado ou publicar
    /// mensagem própria continuam com o tratamento explícito delas.
    /// </remarks>
    private static async Task RunGuardedAsync(
        Func<Task> body,
        Action<Exception> onFailure,
        Action complete,
        CancellationToken cancellationToken)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            onFailure(exception);
        }
        finally
        {
            complete();
        }
    }

    private async Task UpdateApplicationsAsync(
        IReadOnlyList<ApplicationUpdateDisplayItem> items,
        CancellationToken cancellationToken)
    {
        if (!CanRunPackageOperation || items.Count == 0)
        {
            return;
        }

        var packages = items
            .Select(item => applicationUpdates.FirstOrDefault(package => SamePackage(package, item.Package)))
            .Where(package => package is not null && !IsIgnored(package))
            .Cast<WindowsApplicationPackage>()
            .DistinctBy(PackageKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packages.Length == 0)
        {
            return;
        }

        SetPackageOperationRunning(true);
        var succeeded = 0;
        var failed = 0;
        try
        {
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetOperationStatus(WindowsApplicationPackageOperation.Update, "Running", package.Name);
                WindowsApplicationPackageResult result;
                try
                {
                    result = await packageService.ExecuteAsync(
                        WindowsApplicationPackageOperation.Update,
                        package,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not (
                    OperationCanceledException or OutOfMemoryException or StackOverflowException
                    or AccessViolationException))
                {
                    failed++;
                    SetOperationStatus(
                        WindowsApplicationPackageOperation.Update,
                        "Failed",
                        package.Name,
                        localization.GetString("Common.Unknown"));
                    continue;
                }

                if (result.Outcome is WindowsApplicationPackageOutcome.Succeeded
                    or WindowsApplicationPackageOutcome.RebootRequired
                    or WindowsApplicationPackageOutcome.NoLongerAvailable)
                {
                    succeeded++;
                    applicationUpdates = applicationUpdates
                        .Where(current => !SamePackage(current, package))
                        .ToArray();
                    selectedUpdateKeys.Remove(PackageKey(package));
                }
                else
                {
                    failed++;
                }

                if (packages.Length == 1)
                {
                    SetOperationResultStatus(
                        WindowsApplicationPackageOperation.Update,
                        package.Name,
                        result);
                }
            }

            if (packages.Length > 1)
            {
                ApplicationUpdateStatusMessage = localization.Format(
                    failed == 0
                        ? "Applications.Updates.Status.BatchSucceeded"
                        : "Applications.Updates.Status.BatchPartial",
                    succeeded,
                    failed);
            }

            ApplyFilter();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ApplicationUpdateStatusMessage = localization.GetString(
                "Applications.Updates.Status.Cancelled");
        }
        finally
        {
            SetPackageOperationRunning(false);
        }
    }

    private async Task EnsureIgnoreStoreLoadedAsync(CancellationToken cancellationToken)
    {
        if (ignoreStoreLoaded)
        {
            return;
        }

        try
        {
            var values = await ignoreStore.LoadAsync(cancellationToken);
            ignoredUpdateKeys.UnionWith(values);
            ignoreStoreUnavailable = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            ignoredUpdateKeys.Clear();
            ignoreStoreUnavailable = true;
        }
        ignoreStoreLoaded = true;
    }

    private void ApplyInventoryStatus()
    {
        var current = inventorySnapshot!;
        InventoryStatusMessage = localization.GetString(current.IsPartial
            ? "Applications.Inventory.Status.Partial"
            : "Applications.Inventory.Status.Ready");
        InventoryObservedAtLabel = FormatObservedAt(current.ObservedAtUtc);
        ApplyFilter();
    }

    private void ApplyApplicationUpdateStatus()
    {
        var current = updateSnapshot!;
        ApplicationUpdateStatusMessage = GetSnapshotStatus(
            current,
            current.Packages.Count > 0
                ? "Applications.Updates.Status.Available"
                : "Applications.Updates.Status.Current");
        if (ignoreStoreUnavailable
            && current.IsWinGetAvailable
            && current.UnavailableSources.Count < 2)
        {
            ApplicationUpdateStatusMessage = localization.GetString(
                "Applications.Updates.Status.IgnoreLoadFailed");
        }
        ApplicationUpdatesObservedAtLabel = current.IsWinGetAvailable
            ? FormatObservedAt(current.ObservedAtUtc)
            : string.Empty;
        ApplyFilter();
    }

    private string GetSnapshotStatus(
        WindowsApplicationPackageSnapshot snapshot,
        string readyKey)
    {
        if (!snapshot.IsWinGetAvailable)
        {
            return localization.GetString("Applications.Packages.Status.WinGetUnavailable");
        }

        if (snapshot.UnavailableSources.Count == 2)
        {
            return localization.GetString("Applications.Packages.Status.Unavailable");
        }

        return localization.GetString(snapshot.IsPartial
            ? "Applications.Packages.Status.Partial"
            : readyKey);
    }

    private void SetOperationResultStatus(
        WindowsApplicationPackageOperation operation,
        string name,
        WindowsApplicationPackageResult result)
    {
        var suffix = result.Outcome switch
        {
            WindowsApplicationPackageOutcome.Succeeded => "Succeeded",
            WindowsApplicationPackageOutcome.RebootRequired => "RebootRequired",
            WindowsApplicationPackageOutcome.NoLongerAvailable => "NoLongerAvailable",
            WindowsApplicationPackageOutcome.Cancelled => "Cancelled",
            WindowsApplicationPackageOutcome.WinGetUnavailable => "WinGetUnavailable",
            _ => "Failed"
        };
        SetOperationStatus(operation, suffix, name, FormatExitCode(result.ExitCode));
    }

    private void SetOperationStatus(
        WindowsApplicationPackageOperation operation,
        string suffix,
        string name,
        string? detail = null)
    {
        var key = $"Applications.Packages.Operation.{operation}.{suffix}";
        var message = detail is null
            ? localization.Format(key, name)
            : localization.Format(key, name, detail);
        if (operation == WindowsApplicationPackageOperation.Update)
        {
            ApplicationUpdateStatusMessage = message;
        }
        else if (operation == WindowsApplicationPackageOperation.Install)
        {
            DiscoverStatusMessage = message;
        }
        else
        {
            ManagedPackagesStatusMessage = message;
        }
    }

    private void ApplyFilter()
    {
        var query = searchText.Trim();
        ReplaceWith(
            InstalledApplications,
            installedApplications
                .Where(application => MatchesInstalled(application, query))
                .Select(CreateDisplayItem));
        ReplaceWith(
            StartupItems,
            startupItems
                .Where(item => Matches(item.Name, query))
                .Select(CreateDisplayItem));
        ReplaceWith(
            ManagedPackages,
            managedPackages
                .Where(package => MatchesPackage(package, query))
                .Select(package => CreatePackageDisplayItem(package, "Uninstall")));
        ReplaceWith(
            ApplicationUpdates,
            applicationUpdates
                .Where(update => MatchesPackage(update, query))
                .Where(update => showIgnoredUpdates || !IsIgnored(update))
                .Select(CreateUpdateDisplayItem));
        ApplyDiscoveredPackages();
        NotifyAllState();
    }

    private void ApplyDiscoveredPackages()
    {
        ReplaceWith(
            DiscoveredPackages,
            discoveredPackages.Select(package => CreatePackageDisplayItem(package, "Install")));
        OnPropertyChanged(nameof(HasDiscoveredPackages));
        OnPropertyChanged(nameof(ShowDiscoveredPackagesEmptyState));
        OnPropertyChanged(nameof(DiscoveredPackagesEmptyMessage));
    }

    private string GetInventoryEmptyMessage(
        bool? inventoryComplete,
        bool hasUnfilteredItems,
        string emptyKey,
        string noMatchesKey,
        string incompleteKey,
        string unavailableKey)
    {
        if (inventoryUnavailable)
        {
            return localization.GetString(unavailableKey);
        }

        var key = inventoryComplete switch
        {
            null => unavailableKey,
            false => incompleteKey,
            _ when hasUnfilteredItems => noMatchesKey,
            _ => emptyKey
        };
        return localization.GetString(key);
    }

    private string GetPackageEmptyMessage(
        WindowsApplicationPackageSnapshot? snapshot,
        bool hasUnfilteredItems,
        string emptyKey,
        string noMatchesKey)
    {
        if (snapshot is { IsWinGetAvailable: false })
        {
            return localization.GetString("Applications.Packages.Empty.WinGetUnavailable");
        }

        if (snapshot?.UnavailableSources.Count == 2)
        {
            return localization.GetString("Applications.Packages.Empty.Unavailable");
        }

        return localization.GetString(hasUnfilteredItems ? noMatchesKey : emptyKey);
    }

    private InstalledApplicationDisplayItem CreateDisplayItem(WindowsInstalledApplication application) =>
        new(
            application.DisplayName,
            ValueOrUnknown(application.Publisher),
            ValueOrUnknown(application.DisplayVersion),
            FormatSize(application.EstimatedSizeBytes),
            DescribeScope(application.Scope));

    private StartupApplicationDisplayItem CreateDisplayItem(WindowsStartupItem item) => new(
        item.Name,
        localization.GetString(item.Source switch
        {
            WindowsStartupItemSource.RegistryRun => "Applications.Inventory.Source.RegistryRun",
            WindowsStartupItemSource.RegistryRunOnce => "Applications.Inventory.Source.RegistryRunOnce",
            _ => "Applications.Inventory.Source.StartupFolder"
        }),
        DescribeScope(item.Scope));

    private ApplicationPackageDisplayItem CreatePackageDisplayItem(
        WindowsApplicationPackage package,
        string operation) => new(
        package,
        package.Name,
        package.PackageId,
        package.Version,
        DescribeSource(package.Source),
        localization.Format($"Applications.Packages.{operation}.ActionFor", package.Name));

    private ApplicationUpdateDisplayItem CreateUpdateDisplayItem(WindowsApplicationPackage package)
    {
        var ignored = IsIgnored(package);
        return new ApplicationUpdateDisplayItem(
            package,
            selectedUpdateKeys.Contains(PackageKey(package)),
            ignored,
            DescribeSource(package.Source),
            localization.Format("Applications.Updates.ActionFor", package.Name),
            localization.Format(
                ignored
                    ? "Applications.Updates.Restore.ActionFor"
                    : "Applications.Updates.Ignore.ActionFor",
                package.Name),
            localization.GetString(ignored
                ? "Applications.Updates.Restore.Action"
                : "Applications.Updates.Ignore.Action"),
            UpdateSelectionChanged);
    }

    private void UpdateSelectionChanged(ApplicationUpdateDisplayItem item, bool selected)
    {
        var key = PackageKey(item.Package);
        if (selected)
        {
            selectedUpdateKeys.Add(key);
        }
        else
        {
            selectedUpdateKeys.Remove(key);
        }

        OnPropertyChanged(nameof(SelectedApplicationUpdateCount));
        OnPropertyChanged(nameof(CanUpdateSelected));
        OnPropertyChanged(nameof(IsAllVisibleUpdatesSelected));
    }

    private string DescribeScope(WindowsApplicationScope scope) => localization.GetString(
        scope == WindowsApplicationScope.LocalMachine
            ? "Applications.Inventory.Scope.LocalMachine"
            : "Applications.Inventory.Scope.CurrentUser");

    private string DescribeSource(string source) => localization.GetString(
        source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            ? "Applications.Packages.Source.MicrosoftStore"
            : "Applications.Packages.Source.WinGet");

    private string ValueOrUnknown(string? value) => string.IsNullOrWhiteSpace(value)
        ? localization.GetString("Common.Unknown")
        : value;

    private string FormatSize(long? bytes)
    {
        if (bytes is not > 0)
        {
            return localization.GetString("Common.Unknown");
        }

        const double kibibyte = 1024d;
        const double mebibyte = 1024d * kibibyte;
        const double gibibyte = 1024d * mebibyte;
        return bytes.Value switch
        {
            >= (long)gibibyte => $"{(bytes.Value / gibibyte).ToString("N1", localization.CurrentCulture)} GB",
            >= (long)mebibyte => $"{(bytes.Value / mebibyte).ToString("N0", localization.CurrentCulture)} MB",
            _ => $"{(bytes.Value / kibibyte).ToString("N0", localization.CurrentCulture)} KB"
        };
    }

    private string FormatObservedAt(DateTimeOffset observedAtUtc) => localization.Format(
        "Applications.Inventory.Status.UpdatedAt",
        observedAtUtc.ToLocalTime().ToString("t", localization.CurrentCulture));

    private string FormatExitCode(int? exitCode) => exitCode is null
        ? localization.GetString("Common.Unknown")
        : $"0x{exitCode.Value:X8}";

    private static bool MatchesInstalled(WindowsInstalledApplication application, string query) =>
        Matches(application.DisplayName, query)
        || Matches(application.Publisher, query)
        || Matches(application.DisplayVersion, query);

    private static bool MatchesPackage(WindowsApplicationPackage package, string query) =>
        Matches(package.Name, query)
        || Matches(package.PackageId, query)
        || Matches(package.Version, query)
        || Matches(package.AvailableVersion, query)
        || Matches(package.Source, query);

    private static bool Matches(string? value, string query) => query.Length == 0
        || value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool SamePackage(
        WindowsApplicationPackage left,
        WindowsApplicationPackage right) => PackageKey(left).Equals(
        PackageKey(right),
        StringComparison.OrdinalIgnoreCase);

    private bool IsIgnored(WindowsApplicationPackage package) => ignoredUpdateKeys.Contains(
        PackageKey(package));

    private static string PackageKey(WindowsApplicationPackage package) =>
        $"{package.Source}|{package.PackageId}";

    private static void ReplaceWith<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void SetInventoryLoading(bool value)
    {
        if (isInventoryLoading == value)
        {
            return;
        }

        isInventoryLoading = value;
        OnPropertyChanged(nameof(IsInventoryLoading));
        NotifyBusyState();
    }

    private void SetLoadingManagedPackages(bool value)
    {
        if (isLoadingManagedPackages == value)
        {
            return;
        }

        isLoadingManagedPackages = value;
        NotifyBusyState();
        OnPropertyChanged(nameof(ShowManagedPackagesEmptyState));
    }

    private void SetSearchingPackages(bool value)
    {
        if (isSearchingPackages == value)
        {
            return;
        }

        isSearchingPackages = value;
        OnPropertyChanged(nameof(IsSearchingPackages));
        NotifyBusyState();
        OnPropertyChanged(nameof(ShowDiscoveredPackagesEmptyState));
    }

    private void SetCheckingApplicationUpdates(bool value)
    {
        if (isCheckingApplicationUpdates == value)
        {
            return;
        }

        isCheckingApplicationUpdates = value;
        NotifyBusyState();
        OnPropertyChanged(nameof(ShowApplicationUpdatesEmptyState));
    }

    private void SetPackageOperationRunning(bool value)
    {
        if (isPackageOperationRunning == value)
        {
            return;
        }

        isPackageOperationRunning = value;
        OnPropertyChanged(nameof(IsPackageOperationRunning));
        NotifyBusyState();
    }

    private void NotifyBusyState()
    {
        OnPropertyChanged(nameof(CanRefreshApplications));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanSearchPackages));
        OnPropertyChanged(nameof(CanRunPackageOperation));
        OnPropertyChanged(nameof(CanUpdateSelected));
    }

    private void NotifyAllState()
    {
        OnPropertyChanged(nameof(InstalledApplicationCount));
        OnPropertyChanged(nameof(StartupItemCount));
        OnPropertyChanged(nameof(ManagedPackageCount));
        OnPropertyChanged(nameof(ApplicationUpdateCount));
        OnPropertyChanged(nameof(IgnoredApplicationUpdateCount));
        OnPropertyChanged(nameof(SelectedApplicationUpdateCount));
        OnPropertyChanged(nameof(HasInstalledApplications));
        OnPropertyChanged(nameof(HasStartupItems));
        OnPropertyChanged(nameof(HasManagedPackages));
        OnPropertyChanged(nameof(HasApplicationUpdates));
        OnPropertyChanged(nameof(ShowInstalledEmptyState));
        OnPropertyChanged(nameof(ShowStartupEmptyState));
        OnPropertyChanged(nameof(ShowManagedPackagesEmptyState));
        OnPropertyChanged(nameof(ShowApplicationUpdatesEmptyState));
        OnPropertyChanged(nameof(InstalledEmptyMessage));
        OnPropertyChanged(nameof(StartupEmptyMessage));
        OnPropertyChanged(nameof(ManagedPackagesEmptyMessage));
        OnPropertyChanged(nameof(ApplicationUpdatesEmptyMessage));
        OnPropertyChanged(nameof(IsAllVisibleUpdatesSelected));
        OnPropertyChanged(nameof(CanUpdateSelected));
    }

    private static WindowsApplicationPackageSnapshot UnavailablePackageSnapshot() => new(
        [],
        DateTimeOffset.UtcNow,
        IsWinGetAvailable: true,
        ["winget", "msstore"]);

    private void Localization_LanguageChanged(object? sender, AppLanguageChangedEventArgs e)
    {
        OnPropertyChanged(nameof(InstalledEmptyMessage));
        OnPropertyChanged(nameof(StartupEmptyMessage));
        OnPropertyChanged(nameof(ManagedPackagesEmptyMessage));
        OnPropertyChanged(nameof(DiscoveredPackagesEmptyMessage));
        OnPropertyChanged(nameof(ApplicationUpdatesEmptyMessage));

        if (inventoryUnavailable)
        {
            InventoryStatusMessage = localization.GetString("Applications.Inventory.Status.Unavailable");
        }
        else if (inventorySnapshot is not null)
        {
            ApplyInventoryStatus();
        }

        if (!isPackageOperationRunning)
        {
            if (managedSnapshot is not null)
            {
                ManagedPackagesStatusMessage = GetSnapshotStatus(
                    managedSnapshot,
                    managedPackages.Count > 0
                        ? "Applications.Packages.Status.Ready"
                        : "Applications.Packages.Status.Empty");
            }

            if (discoverSnapshot is not null)
            {
                DiscoverStatusMessage = GetSnapshotStatus(
                    discoverSnapshot,
                    discoveredPackages.Count > 0
                        ? "Applications.Discover.Status.Results"
                        : "Applications.Discover.Status.NoResults");
            }

            if (updateSnapshot is not null)
            {
                ApplyApplicationUpdateStatus();
            }
        }

        ApplyFilter();
    }
}

internal sealed class SyntheticWindowsApplicationInventoryInspector
    : IWindowsApplicationInventoryInspector
{
    public Task<WindowsApplicationInventorySnapshot> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WindowsApplicationInventorySnapshot(
            [
                new WindowsInstalledApplication(
                    "FiveM", "1.0", "Cfx.re", null,
                    WindowsApplicationScope.CurrentUser, WindowsApplicationArchitecture.X64),
                new WindowsInstalledApplication(
                    "OBS Studio", "32.0", "OBS Project", null,
                    WindowsApplicationScope.LocalMachine, WindowsApplicationArchitecture.X64),
                new WindowsInstalledApplication(
                    "Ralven", typeof(SyntheticWindowsApplicationInventoryInspector).Assembly.GetName().Version?.ToString(3), null, null,
                    WindowsApplicationScope.CurrentUser, WindowsApplicationArchitecture.X64)
            ],
            [
                new WindowsStartupItem(
                    "Ralven", "CurrentUser:RegistryRun", WindowsStartupItemSource.RegistryRun,
                    WindowsApplicationScope.CurrentUser),
                new WindowsStartupItem(
                    "Game launcher", "CurrentUser:StartupFolder", WindowsStartupItemSource.StartupFolder,
                    WindowsApplicationScope.CurrentUser)
            ],
            DateTimeOffset.UtcNow,
            InstalledApplicationsComplete: true,
            StartupItemsComplete: true));
    }

    public Task<WindowsApplicationInventorySnapshot> InspectStartupAsync(
        CancellationToken cancellationToken = default) => InspectAsync(cancellationToken);
}

internal sealed class SyntheticWindowsApplicationPackageService
    : IWindowsApplicationPackageService
{
    private static readonly WindowsApplicationPackage[] Installed =
    [
        new("OBSProject.OBSStudio", "OBS Studio", "32.0", null, "winget"),
        new("VideoLAN.VLC", "VLC media player", "3.0.21", null, "winget"),
        new("9NZVDKPMR9RD", "Quick Assist", "2.0", null, "msstore")
    ];

    public Task<WindowsApplicationPackageSnapshot> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot(
        [
            new("Discord.Discord", "Discord", "1.0.9209", null, "winget"),
            new("XP9KHM4BK9FZ7Q", "Spotify Music", "1.0", null, "msstore")
        ]));
    }

    public Task<WindowsApplicationPackageSnapshot> ListInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot(Installed));
    }

    public Task<WindowsApplicationPackageSnapshot> CheckUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot(
        [
            Installed[0] with { AvailableVersion = "32.1.2" },
            Installed[1] with { AvailableVersion = "3.0.22" },
            Installed[2] with { AvailableVersion = "2.1" }
        ]));
    }

    public Task<WindowsApplicationPackageResult> ExecuteAsync(
        WindowsApplicationPackageOperation operation,
        WindowsApplicationPackage package,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WindowsApplicationPackageResult(
            WindowsApplicationPackageOutcome.Succeeded,
            ExitCode: 0));
    }

    private static WindowsApplicationPackageSnapshot Snapshot(
        IReadOnlyList<WindowsApplicationPackage> packages) => new(
        packages,
        DateTimeOffset.UtcNow,
        IsWinGetAvailable: true,
        UnavailableSources: []);
}
