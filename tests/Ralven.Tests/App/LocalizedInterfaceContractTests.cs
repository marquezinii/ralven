using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ralven.App;
using Ralven.App.Services;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Xunit;

namespace Ralven.Tests.App;

public sealed partial class LocalizedInterfaceContractTests
{
    [Fact]
    public void LocalizedRunBindings_AreOneWay()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        var violations = Directory
            .EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => LocalizedRunWithoutOneWayPattern().Matches(File.ReadAllText(path)))
            .Select(match => match.Value)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void PublicXamlAndMessageBoxes_DoNotIntroduceHardcodedText()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        var publicAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "AutomationProperties.HelpText", "AutomationProperties.Name", "Content",
            "Header", "Text", "Title", "ToolTip"
        };
        var xamlViolations = Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants().Attributes()
                .Where(attribute => publicAttributes.Contains(attribute.Name.LocalName)
                    && !attribute.Value.StartsWith('{')
                    && attribute.Value.Any(char.IsLetter))
                .Select(attribute => $"{path}: {attribute.Name.LocalName}={attribute.Value}"))
            .ToArray();

        Assert.Empty(xamlViolations);

        var dialogViolations = new[] { "Ralven.App", "Ralven.Launcher", "Ralven.Updater" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(path => HardcodedMessageBoxPattern().IsMatch(File.ReadAllText(path)))
            .ToArray();

        Assert.Empty(dialogViolations);
    }

    [Fact]
    public void LocalizedXamlBindings_ResolveInEverySupportedLanguage()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var sources = new[]
        {
            Path.Combine(root, "src", "Ralven.App", "MainWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Controls", "UltraPanel.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "BugReportWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "PrivacyConsentWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "ReleaseNotesWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "PasswordSecurityWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "TwoFactorSecurityWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "TermsOfUseWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "OptimizationConfirmationWindow.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "OverviewPage.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "SystemPage.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "ApplicationsPage.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "GamesPage.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "FiveMPage.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "RalvenAiPage.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "ProPage.xaml"),
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "OptimizerPage.xaml")
        };
        var keys = sources
            .SelectMany(path => LocalizedKeyPattern().Matches(File.ReadAllText(path)))
            .Select(match => match.Groups["key"].Value)
            .ToSortedSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.All(SupportedLocalizations(), localization =>
                Assert.NotEqual(key, localization.GetString(key)));
        }
    }

    [Fact]
    public void Overview_LimitsFiveMToTheReadOnlyLiveMetricsTarget()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var overview = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "Pages",
            "OverviewPage.xaml"));

        Assert.Contains("Dashboard.LivePerformance.Target.FiveM", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("OptimizationScope.FiveMLegacy", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Gta", overview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LegacyCache", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralExpansion_UsesInternalCatalogsAndTrustedWindowsActions()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        var mainWindow = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.xaml"));
        var navigation = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.Navigation.xaml.cs"));
        var capture = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.Capture.xaml.cs"));
        var gamesPage = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "GamesPage.xaml"));
        var gamesPageCode = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "GamesPage.xaml.cs"));
        var fiveMPage = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "FiveMPage.xaml"));
        var fiveMPageCode = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "FiveMPage.xaml.cs"));
        var systemPage = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "SystemPage.xaml.cs"));
        var systemMarkup = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "SystemPage.xaml"));
        var applicationsView = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "ApplicationsPage.xaml"));
        var applicationsPage = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "ApplicationsPage.xaml.cs"));
        var inspector = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.Windows",
            "Infrastructure",
            "WindowsApplicationInventoryInspector.cs"));

        Assert.Contains("Tag=\"System\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Applications\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Games\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Optimizer\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("[Navigation.Games]", mainWindow, StringComparison.Ordinal);
        Assert.Contains("\"Games\" => GamesPage", navigation, StringComparison.Ordinal);
        Assert.Contains("private FiveMPage FiveMPage", navigation, StringComparison.Ordinal);
        Assert.Contains("RequestNavigateToFiveM()", gamesPageCode, StringComparison.Ordinal);
        Assert.Contains("[FiveMHub.Actions.Title]", fiveMPage, StringComparison.Ordinal);
        Assert.Contains("RequestNavigateToOptimizer(OptimizationScope.FiveMLegacy)", fiveMPageCode, StringComparison.Ordinal);
        Assert.Contains("RequestNavigateToOptimizer(OptimizationScope.GeneralWindows)", fiveMPageCode, StringComparison.Ordinal);
        Assert.Contains("RequestNavigateToHistory()", fiveMPageCode, StringComparison.Ordinal);
        Assert.Contains("Assets/ReShade.png", fiveMPage, StringComparison.Ordinal);
        Assert.Contains("[FiveMHub.ReShade.Action]", fiveMPage, StringComparison.Ordinal);
        Assert.Contains("InstallReShade_Click", fiveMPage, StringComparison.Ordinal);
        Assert.Contains("https://reshade.me/#download", fiveMPageCode, StringComparison.Ordinal);
        Assert.Contains("ExternalLauncher.TryOpen", fiveMPageCode, StringComparison.Ordinal);
        var fiveMDocument = XDocument.Parse(fiveMPage);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var reShadeButton = Assert.Single(
            fiveMDocument.Descendants(presentation + "Button"),
            button => (string?)button.Attribute("Click") == "InstallReShade_Click");
        Assert.Equal("{StaticResource PrimaryButtonStyle}", (string?)reShadeButton.Attribute("Style"));
        Assert.Contains("[Dashboard.SessionMonitor.Title]", fiveMPage, StringComparison.Ordinal);
        Assert.Contains("ToggleFiveMSessionMonitor_Click", fiveMPage, StringComparison.Ordinal);
        Assert.Contains("ToggleFiveMSessionMonitor()", fiveMPageCode, StringComparison.Ordinal);
        Assert.Contains("OptimizationScope.FiveMLegacy ? GamesNav : OptimizerNav", navigation, StringComparison.Ordinal);
        Assert.Contains("Games.FiveM.Action", gamesPage, StringComparison.Ordinal);
        Assert.Contains("FiveMGameCardSurface", gamesPage, StringComparison.Ordinal);
        Assert.Contains("Assets/FiveM.png", gamesPage, StringComparison.Ordinal);
        Assert.Contains("Height=\"570\"", gamesPage, StringComparison.Ordinal);
        Assert.Contains("\"Games\" => (Element: (UIElement)GamesPage, Nav: GamesNav)", capture, StringComparison.Ordinal);
        Assert.Contains("\"FiveM\" => (Element: (UIElement)FiveMPage, Nav: GamesNav)", capture, StringComparison.Ordinal);
        Assert.Contains("\"Optimizer\" => ConfigureOptimizerCapture(OptimizationScope.GeneralWindows, OptimizerNav)", capture, StringComparison.Ordinal);
        Assert.Contains("\"FiveMOptimizer\" => ConfigureOptimizerCapture(OptimizationScope.FiveMLegacy, GamesNav)", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("ms-settings:", systemPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", systemPage, StringComparison.Ordinal);
        Assert.Contains("RefreshSystem_Click", systemPage, StringComparison.Ordinal);
        Assert.Contains("ApplyWindowsGamingSettingsAsync", systemPage, StringComparison.Ordinal);
        Assert.Contains("RestoreWindowsGamingSettingsAsync", systemPage, StringComparison.Ordinal);
        Assert.Contains("WindowsAntivirusHealthLabel", systemMarkup, StringComparison.Ordinal);
        Assert.Contains("WindowsFirewallHealthLabel", systemMarkup, StringComparison.Ordinal);
        Assert.Contains("WindowsAutomaticUpdatesHealthLabel", systemMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CpuName}\"", systemMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GpuDetail}\"", systemMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RamLabel}\"", systemMarkup, StringComparison.Ordinal);
        Assert.Contains("RefreshWindowsSystemHealthAsync", systemPage, StringComparison.Ordinal);
        Assert.Contains("RefreshDiagnosticAsync", systemPage, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding InstalledApplications}\"", applicationsView, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding StartupItems}\"", applicationsView, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SearchText", applicationsView, StringComparison.Ordinal);
        Assert.Contains("GroupName=\"ApplicationPackageTab\"", applicationsView, StringComparison.Ordinal);
        Assert.Contains("AdvancedOptionsExpander", applicationsView, StringComparison.Ordinal);
        Assert.Contains("[Applications.TechnicalDetails]", applicationsView, StringComparison.Ordinal);
        Assert.Contains("ms-settings:appsfeatures", applicationsPage, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://downloadsandupdates", applicationsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("UninstallString", inspector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartupApproved", inspector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Arguments =", systemPage + applicationsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("runas", systemPage + applicationsPage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsGamingControls_UseNativeButtonsAndCoordinateWithOptimizerBusyState()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        var page = File.ReadAllText(Path.Combine(appDirectory, "Views", "Pages", "SystemPage.xaml"));
        var controls = File.ReadAllText(Path.Combine(appDirectory, "Themes", "Controls.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(appDirectory, "ViewModels", "MainViewModel.System.cs"));
        var mainViewModel = File.ReadAllText(Path.Combine(appDirectory, "ViewModels", "MainViewModel.cs"));
        var historyViewModel = File.ReadAllText(Path.Combine(
            appDirectory,
            "ViewModels",
            "MainViewModel.Diagnostics.cs"));
        var historyPage = File.ReadAllText(Path.Combine(
            appDirectory,
            "Views",
            "Pages",
            "HistoryPage.xaml.cs"));

        Assert.Contains("System.Gaming.Title", page, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", page, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanApplyWindowsGamingSettings}\"", page, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanRestoreWindowsGamingSettings}\"", page, StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource AppTextOnAccentBrush}\" />",
            controls,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource TextTertiaryBrush}\" />",
            controls,
            StringComparison.Ordinal);
        Assert.Contains("!IsBusy", viewModel, StringComparison.Ordinal);
        Assert.Contains("!isWindowsGamingBusy", mainViewModel, StringComparison.Ordinal);
        Assert.Contains(
            "item.Kind == AppHistoryKind.WindowsGaming",
            historyPage,
            StringComparison.Ordinal);
        Assert.Contains("System.Gaming.RestoreConfirm.Message", historyPage, StringComparison.Ordinal);
        Assert.Contains("System.Gaming.RestoreConfirm.Title", historyPage, StringComparison.Ordinal);
        Assert.Contains(
            ".Where(item => item.Kind == AppHistoryKind.Optimization)",
            historyViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AccountPlan_ShowsServerEntitlementStatesWithCompleteLocalization()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        var mainWindow = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.xaml"));
        var accountCode = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.Account.xaml.cs"));

        Assert.Contains("AccountEntitlementValueText", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AccountEntitlementRefresh_Click", mainWindow, StringComparison.Ordinal);
        Assert.Matches(
            "x:Name=\"AccountEntitlementValueText\"[^>]*AutomationProperties.LiveSetting=\"Polite\"",
            mainWindow);
        Assert.Contains("SyncAccountEntitlementAsync", accountCode, StringComparison.Ordinal);
        Assert.Contains("ClearAccountEntitlement", accountCode, StringComparison.Ordinal);

        var keys = new[]
        {
            "Account.Session.ClearFailed",
            "Settings.Account.Plan.Title",
            "Settings.Account.Plan.Free",
            "Settings.Account.Plan.FreeDetail",
            "Settings.Account.Plan.ProUntil",
            "Settings.Account.Plan.ProDetail",
            "Settings.Account.Plan.Unavailable",
            "Settings.Account.Plan.UnavailableDetail",
            "Settings.Account.Plan.Refresh",
        };
        var localizations = SupportedLocalizations();

        foreach (var localization in localizations)
        {
            foreach (var key in keys)
            {
                Assert.NotEqual(key, localization.GetString(key));
            }
        }
    }

    [Fact]
    public void AccountPlan_RejectsStaleOrForeignEntitlementResponses()
    {
        var firstUser = new FirebaseUser("uid-1", "first@example.com", true);
        var secondUser = new FirebaseUser("uid-2", "second@example.com", true);

        Assert.True(MainWindow.IsCurrentAccountEntitlementResponse(
            4,
            4,
            firstUser.Uid,
            new AuthenticationSnapshot(AuthenticationState.SignedIn, firstUser)));
        Assert.False(MainWindow.IsCurrentAccountEntitlementResponse(
            3,
            4,
            firstUser.Uid,
            new AuthenticationSnapshot(AuthenticationState.SignedIn, firstUser)));
        Assert.False(MainWindow.IsCurrentAccountEntitlementResponse(
            4,
            4,
            firstUser.Uid,
            new AuthenticationSnapshot(AuthenticationState.SignedIn, secondUser)));
        Assert.False(MainWindow.IsCurrentAccountEntitlementResponse(
            4,
            4,
            firstUser.Uid,
            new AuthenticationSnapshot(AuthenticationState.SignedOut, null)));
    }

    [Fact]
    public void AccountHeader_UsesCompactUsernameAndRejectsForeignProfileResponses()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        var mainWindowPath = Path.Combine(appDirectory, "MainWindow.xaml");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var accountCode = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.Account.xaml.cs"));
        var captureCode = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.Capture.xaml.cs"));
        var document = XDocument.Load(mainWindowPath);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var fallback = document.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "AccountFallbackIcon");
        var avatar = document.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "AccountAvatarEllipse");
        var firstUser = new FirebaseUser("uid-1", "first@example.com", true);
        var secondUser = new FirebaseUser("uid-2", "second@example.com", true);

        Assert.Same(fallback.Parent, avatar.Parent);
        Assert.Equal("{StaticResource RadiusPill}", (string?)fallback.Parent?.Parent?.Attribute("CornerRadius"));
        Assert.Matches("x:Name=\"AccountAvatarEllipse\"[^>]*Width=\"28\"[^>]*Height=\"28\"", mainWindow);
        Assert.Matches("x:Name=\"AccountLabel\"[\\s\\S]*?MaxWidth=\"200\"[\\s\\S]*?TextTrimming=\"CharacterEllipsis\"", mainWindow);
        Assert.Contains("ApplyAccountSettingsUsername(result.Username);", accountCode, StringComparison.Ordinal);
        Assert.DoesNotContain("profile?.DisplayName", accountCode, StringComparison.Ordinal);
        Assert.Contains("AccountLabel.MaxWidth = profile is null ? 200 : 120;", accountCode, StringComparison.Ordinal);
        Assert.Contains("demoMode && accountUsername is not null", captureCode, StringComparison.Ordinal);
        Assert.Equal("@ralven_user", MainWindow.FormatAccountUsername("ralven_user"));
        Assert.Empty(MainWindow.FormatAccountUsername(" "));
        Assert.True(MainWindow.IsCurrentAccountProfileResponse(firstUser.Uid, new(AuthenticationState.SignedIn, firstUser)));
        Assert.False(MainWindow.IsCurrentAccountProfileResponse(firstUser.Uid, new(AuthenticationState.SignedIn, secondUser)));
        Assert.False(MainWindow.IsCurrentAccountProfileResponse(firstUser.Uid, new(AuthenticationState.SignedOut, null)));
    }

    [Fact]
    public void AccountPlan_ProAccessExpiresAtTheServerValidityBoundary()
    {
        var validUntil = DateTimeOffset.Parse(
            "2026-09-30T12:00:00.000Z",
            CultureInfo.InvariantCulture);
        var snapshot = new AccountEntitlementSnapshot(AccountEntitlementTier.Pro, validUntil);

        Assert.True(MainWindow.IsEffectiveProEntitlement(snapshot, validUntil.AddTicks(-1)));
        Assert.False(MainWindow.IsEffectiveProEntitlement(snapshot, validUntil));
        Assert.False(MainWindow.IsEffectiveProEntitlement(
            new AccountEntitlementSnapshot(AccountEntitlementTier.Free),
            validUntil.AddTicks(-1)));
    }

    [Fact]
    public void EveryOptimizationAction_HasLocalizedReviewContent()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var resourceDirectory = Path.Combine(root, "src", "Ralven.App", "Resources");
        var resourcePaths = ResourceCatalogPaths(resourceDirectory);
        var localizedResources = resourcePaths.Select(path => XDocument
            .Load(path)
            .Descendants("data")
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (string?)element.Element("value") ?? string.Empty,
                StringComparer.Ordinal))
            .ToArray();
        var portugueseReviewContent = localizedResources[Array.FindIndex(
            resourcePaths,
            path => Path.GetFileName(path).Equals("Strings.pt-BR.resx", StringComparison.OrdinalIgnoreCase))];

        foreach (var action in ActionCatalog.Current.Actions)
        {
            foreach (var suffix in new[]
                     {
                         "Name",
                         "Description",
                         "DetectionSummary",
                         "ConfirmationSummary",
                         "UndoSummary",
                         "RiskLimitations"
                     })
            {
                var key = $"Actions.{action.Id}.{suffix}";
                Assert.All(localizedResources, resources =>
                {
                    Assert.True(resources.TryGetValue(key, out var value));
                    Assert.False(string.IsNullOrWhiteSpace(value));
                });
                Assert.All(SupportedLocalizations(), localization =>
                    Assert.NotEqual(key, localization.GetString(key)));
            }

            Assert.Equal(action.DetectionSummary, portugueseReviewContent[$"Actions.{action.Id}.DetectionSummary"]);
            Assert.Equal(action.ConfirmationSummary, portugueseReviewContent[$"Actions.{action.Id}.ConfirmationSummary"]);
            Assert.Equal(action.UndoSummary, portugueseReviewContent[$"Actions.{action.Id}.UndoSummary"]);
            Assert.Equal(action.RiskLimitations, portugueseReviewContent[$"Actions.{action.Id}.RiskLimitations"]);
        }
    }

    [Fact]
    public void BugReportCodeBehind_LocalizationKeysResolve()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "BugReportWindow.xaml.cs"));
        var keys = LocalizedCodeKeyPattern()
            .Matches(source)
            .Select(match => match.Groups["key"].Value)
            .ToSortedSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.All(SupportedLocalizations(), localization =>
                Assert.NotEqual(key, localization.GetString(key)));
        }
    }

    [Fact]
    public void BugReportWindow_UsesTheDefinedComboBoxStyle()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "BugReportWindow.xaml"));
        var controls = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Themes",
            "Controls.xaml"));

        Assert.Contains("Style=\"{StaticResource DialogSelectorStyle}\"", window, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource SettingsComboBoxStyle}\"", controls, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingsComboBoxStyle\"", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("FormComboBoxStyle", window, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivacyConsentCodeBehind_LocalizationKeysResolve()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "PrivacyConsentWindow.xaml.cs"));
        var keys = LocalizedCodeKeyPattern()
            .Matches(source)
            .Select(match => match.Groups["key"].Value)
            .ToSortedSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.All(SupportedLocalizations(), localization =>
                Assert.NotEqual(key, localization.GetString(key)));
        }
    }

    [Fact]
    public void ReleaseNotesCodeBehind_LocalizationKeysResolve()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "ReleaseNotesWindow.xaml.cs"));
        var keys = LocalizedCodeKeyPattern()
            .Matches(source)
            .Select(match => match.Groups["key"].Value)
            .ToSortedSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.All(SupportedLocalizations(), localization =>
                Assert.NotEqual(key, localization.GetString(key)));
        }
    }

    [Fact]
    public void ReleaseNotesWindow_IsCompactAndFixed()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "ReleaseNotesWindow.xaml"));

        var window = document.Root!;
        Assert.Equal("NoResize", (string?)window.Attribute("ResizeMode"));
        Assert.Equal("440", (string?)window.Attribute("Height"));
    }

    [Fact]
    public void PrivacyConsentWindow_CanOnlyCloseAfterContinue()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "PrivacyConsentWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "PrivacyConsentWindow.xaml.cs"));

        Assert.DoesNotContain("Click=\"Close_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = !confirmedByUser;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("confirmedByUser = true;", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Optimizer_SeparatesPreparationProgressAndResults()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var pageDirectory = Path.Combine(root, "src", "Ralven.App", "Views", "Pages");
        var optimizer = File.ReadAllText(Path.Combine(pageDirectory, "OptimizerPage.xaml"))
            + File.ReadAllText(Path.Combine(pageDirectory, "OptimizerPage.xaml.cs"));
        var ultraPanel = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Controls", "UltraPanel.xaml"));

        Assert.Contains("IsOptimizerIdle", optimizer, StringComparison.Ordinal);
        Assert.Contains("IsBusy", optimizer, StringComparison.Ordinal);
        Assert.Contains("IsReportAvailable", optimizer, StringComparison.Ordinal);
        Assert.Contains("PlannedActions", optimizer, StringComparison.Ordinal);
        Assert.Contains("GroupedPlannedAdjustments", optimizer, StringComparison.Ordinal);
        Assert.Contains("GroupedInformationalPlanActions", optimizer, StringComparison.Ordinal);
        Assert.Contains("AutomaticAnalysisHeader", optimizer, StringComparison.Ordinal);
        Assert.Contains("IsExpanded=\"False\"", optimizer, StringComparison.Ordinal);
        Assert.Contains("<Expander", optimizer, StringComparison.Ordinal);
        Assert.Contains("DetectionSummary", optimizer, StringComparison.Ordinal);
        Assert.Contains("ConfirmationSummary", optimizer, StringComparison.Ordinal);
        Assert.Contains("UndoSummary", optimizer, StringComparison.Ordinal);
        Assert.Contains("RiskLimitations", optimizer, StringComparison.Ordinal);
        // O trilho único (SpectrumSelector) mantém os quatro perfis no mesmo
        // sistema visual; o sinal de
        // "recomendado" chega via RecommendedIndex, calculado a partir das
        // mesmas três propriedades do ViewModel.
        Assert.Contains("SpectrumSelector", optimizer, StringComparison.Ordinal);
        Assert.Contains("Option3Label=\"{Binding [Ultra.Development.Name]", optimizer, StringComparison.Ordinal);
        Assert.DoesNotContain("ExclusiveLabel=", optimizer, StringComparison.Ordinal);
        Assert.Contains("[Ultra.Exclusive]", ultraPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectUltra_Click", optimizer, StringComparison.Ordinal);
        Assert.Contains("RecommendedIndex", optimizer, StringComparison.Ordinal);
        Assert.Contains("IsLightRecommended", optimizer, StringComparison.Ordinal);
        Assert.Contains("IsBalancedRecommended", optimizer, StringComparison.Ordinal);
        Assert.Contains("IsAggressiveRecommended", optimizer, StringComparison.Ordinal);
        Assert.Contains(": -1;", optimizer, StringComparison.Ordinal);
        // O ledger continua acessível, mas sob disclosure para não competir
        // com a etapa atual e a imediatamente anterior.
        Assert.Contains("StepLedger", optimizer, StringComparison.Ordinal);
        Assert.Contains("[Optimizer.ExecutionDetails]", optimizer, StringComparison.Ordinal);
        Assert.Contains("[Optimizer.ResultDetails]", optimizer, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivityLog", optimizer, StringComparison.Ordinal);
        Assert.Contains("Binding ProgressPercent, Mode=OneWay", optimizer, StringComparison.Ordinal);
        Assert.Contains("PreviousProgressHeadline", optimizer, StringComparison.Ordinal);
        Assert.Contains("ProgressHeadline, Mode=OneWay", optimizer, StringComparison.Ordinal);
        Assert.Contains("ElapsedTimeLabel", optimizer, StringComparison.Ordinal);
        Assert.Contains("RemainingTimeLabel", optimizer, StringComparison.Ordinal);
        Assert.Contains("ReportLines", optimizer, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_FocusesOnStatusInsteadOfDuplicatingOptimizerControls()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var dashboard = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Views",
            "Pages",
            "OverviewPage.xaml"));

        Assert.Contains("StreamingReadinessItems", dashboard, StringComparison.Ordinal);
        Assert.Contains("Dashboard.LivePerformance.Title", dashboard, StringComparison.Ordinal);
        // O histórico ao vivo é um gráfico 2D leve e selecionável.
        Assert.Contains("controls:LivePerformanceChart", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("PerformanceScene3D", dashboard, StringComparison.Ordinal);
        Assert.Contains("CpuValues=\"{Binding SelectedLiveMetricSeries}\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("LiveMetricsTarget_Checked", dashboard, StringComparison.Ordinal);
        Assert.Contains("LiveMetric_Checked", dashboard, StringComparison.Ordinal);
        Assert.Contains("IsLivePerformancePaused", dashboard, StringComparison.Ordinal);
        Assert.Contains("NetworkUsageLabel", dashboard, StringComparison.Ordinal);
        Assert.Contains("IsLivePerformanceUnavailable", dashboard, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", dashboard, StringComparison.Ordinal);
        // Redesign "prancheta técnica": a geometria 3D (CoreVisual) e o anel
        // ArcProgress foram removidos do produto por decisão de design — a
        // profundidade passou a vir de camadas, traço e material. A prontidão
        // é lida numa escala graduada, não num medidor decorativo, e o teste
        // trava essa decisão para que nenhum controle 3D volte por descuido.
        Assert.DoesNotContain("controls:CoreVisual", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("controls:ArcProgress", dashboard, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding ReadinessScore, Mode=OneWay}\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsCpuLiveMetricSelected, Mode=OneWay}\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("Dashboard.OpenOptimizer", dashboard, StringComparison.Ordinal);
        Assert.Contains("Dashboard.SystemOverview", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupName=\"Profile\"", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("StartOptimization_Click", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfilePresentationBenefits", dashboard, StringComparison.Ordinal);

        // A faixa de indicadores explica a recomendação com números úteis da
        // própria varredura local, sem puxar dados do fluxo separado de FiveM.
        Assert.Contains("PerformancePressureLabel", dashboard, StringComparison.Ordinal);
        Assert.Contains("LogicalProcessorLabel", dashboard, StringComparison.Ordinal);
        Assert.Contains("AvailableMemoryLabel", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyCacheLabel", dashboard, StringComparison.Ordinal);
        // Média e pico saem das mesmas amostras desenhadas no gráfico.
        Assert.Contains("CpuTrendLabel", dashboard, StringComparison.Ordinal);
        Assert.Contains("GpuTrendLabel", dashboard, StringComparison.Ordinal);
        // O fim da página mostra a última execução real, com estado próprio
        // quando ainda não existe histórico.
        Assert.Contains("LastOptimizationTitle", dashboard, StringComparison.Ordinal);
        Assert.Contains("HasLastOptimization", dashboard, StringComparison.Ordinal);
        Assert.Contains("OpenHistory_Click", dashboard, StringComparison.Ordinal);
        // Todos os ícones da Visão geral são vetores do dicionário próprio; a
        // fonte de glifos não é mais usada nesta página.
        Assert.DoesNotContain("Segoe MDL2 Assets", dashboard, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "ViewModels", "MainViewModel.Diagnostics.cs"));
        Assert.Contains("> 75 => localization.GetString(\"Dashboard.Readiness.Excellent\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("> 50 => localization.GetString(\"Dashboard.Readiness.Good\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("> 25 => localization.GetString(\"Dashboard.Readiness.Average\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("> 5 => localization.GetString(\"Dashboard.Readiness.Poor\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("_ => localization.GetString(\"Dashboard.Readiness.VeryPoor\")", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationalPages_PreserveScrollingBusyStateAndNativeProfileSelection()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        var pageDirectory = Path.Combine(appDirectory, "Views", "Pages");
        var games = File.ReadAllText(Path.Combine(pageDirectory, "GamesPage.xaml"));
        var fiveM = File.ReadAllText(Path.Combine(pageDirectory, "FiveMPage.xaml"));
        var history = File.ReadAllText(Path.Combine(pageDirectory, "HistoryPage.xaml"));
        var selector = XDocument.Load(Path.Combine(appDirectory, "Controls", "SpectrumSelector.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", games, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", games, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", fiveM, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", fiveM, StringComparison.Ordinal);
        Assert.Contains("DataContext.IsBusy", history, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=\"{Binding CanRollback}\"", history, StringComparison.Ordinal);

        var options = selector.Descendants(presentation + "RadioButton").ToArray();
        Assert.Equal(4, options.Length);
        Assert.All(options, option => Assert.Equal("SpectrumOptions", (string?)option.Attribute("GroupName")));
        Assert.Contains(options, option =>
            (string?)option.Attribute(x + "Name") == "Option3Button"
            && option.ToString().Contains("ProAccentGradientBrush", StringComparison.Ordinal));
        Assert.DoesNotContain(selector.Descendants(presentation + "Button"), element =>
            ((string?)element.Attribute(x + "Name"))?.StartsWith("Option", StringComparison.Ordinal) == true);
        var track = Assert.Single(selector.Descendants(presentation + "Grid"), element =>
            (string?)element.Attribute(x + "Name") == "TrackHost");
        Assert.Equal("False", (string?)track.Attribute("Focusable"));
    }

    [Fact]
    public void FluentInteractionStyles_KeepListsStableAndKeyboardFocusVisible()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Themes",
            "Controls.xaml"));
        var typography = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Themes",
            "Typography.xaml"));
        var pageDirectory = Path.Combine(root, "src", "Ralven.App", "Views", "Pages");
        var overview = File.ReadAllText(Path.Combine(pageDirectory, "OverviewPage.xaml"));
        var optimizer = File.ReadAllText(Path.Combine(pageDirectory, "OptimizerPage.xaml"));

        // ScaleTransform é banido de listas (já causou itens se deslocando sob
        // o ponteiro), mas é uma exceção deliberada e isolada no press do
        // botão primário — nunca dentro de um estilo de linha/lista.
        //
        // A verificação roda sobre a marcação SEM comentários: um comentário
        // que explica por que a exceção existe não é uma ocorrência do
        // recurso, e travar o texto dos comentários proibia justamente
        // documentar a regra ao lado dela.
        var styleMarkup = WithoutXmlComments(styles);
        var baseButtonStyle = styleMarkup[styleMarkup.IndexOf("x:Key=\"ButtonBaseStyle\"", StringComparison.Ordinal)..styleMarkup.IndexOf("x:Key=\"PrimaryButtonStyle\"", StringComparison.Ordinal)];
        Assert.Contains("Property=\"Height\" Value=\"36\"", baseButtonStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"MinHeight\" Value=\"36\"", baseButtonStyle, StringComparison.Ordinal);
        Assert.Contains("FocusVisualStyle\" Value=\"{StaticResource KeyboardFocusVisual}\"", baseButtonStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"FocusRing\"", baseButtonStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"IsKeyboardFocused\"", baseButtonStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource TextTertiaryBrush}\"", baseButtonStyle, StringComparison.Ordinal);
        Assert.Contains("RelativeSource={RelativeSource AncestorType=Button}", baseButtonStyle, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource ButtonBaseStyle}\"", styleMarkup, StringComparison.Ordinal);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource Surface3Brush}\"", styleMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("ScaleTransform", styleMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("ScaleTransform", WithoutXmlComments(overview), StringComparison.Ordinal);
        Assert.DoesNotContain("ScaleTransform", WithoutXmlComments(optimizer), StringComparison.Ordinal);
        Assert.Contains("x:Key=\"KeyboardFocusVisual\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"ScrollBar\">", styles, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("DropShadowEffect Color=\"#000000\" BlurRadius=\"5\"", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"3\" Height=\"3\"", styles, StringComparison.Ordinal);
        // A fonte oficial é incorporada e declarada uma vez em Typography.xaml;
        // os fallbacks existem apenas para design-time e builds parciais.
        Assert.Contains("/Ralven;component/Assets/Fonts/#Inter", typography, StringComparison.Ordinal);
        Assert.DoesNotContain("DropShadowEffect Color=\"#000000\" BlurRadius=\"10\"", styles, StringComparison.Ordinal);
        var icons = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Themes",
            "Icons.xaml"));

        Assert.Contains("x:Key=\"IconCheck\"", icons, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IconClose\"", icons, StringComparison.Ordinal);
        Assert.DoesNotContain("{StaticResource IconCheck}", optimizer, StringComparison.Ordinal);
        Assert.DoesNotContain("{StaticResource IconClose}", optimizer, StringComparison.Ordinal);
    }

    [Fact]
    public void ResxCatalogs_HaveNoDuplicateKeys()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var resourceDirectory = Path.Combine(root, "src", "Ralven.App", "Resources");
        foreach (var path in ResourceCatalogPaths(resourceDirectory))
        {
            var document = XDocument.Load(path);
            var duplicateKeys = document
                .Descendants("data")
                .Select(element => (string?)element.Attribute("name"))
                .Where(name => name is not null)
                .GroupBy(name => name!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();

            Assert.Empty(duplicateKeys);
        }
    }

    [Fact]
    public void PublicBrandName_IsConsistentlyRalven()
    {
        Assert.Equal("Ralven", ProductIdentity.DisplayName);
        Assert.Equal("Ralven", ProductIdentity.Name);

        var root = TestHelpers.FindRepositoryRoot();
        var resourceDirectory = Path.Combine(root, "src", "Ralven.App", "Resources");
        foreach (var path in ResourceCatalogPaths(resourceDirectory))
        {
            var document = XDocument.Load(path);
            var values = document.Descendants("value").Select(element => element.Value);

            Assert.Contains(values, value => value == ProductIdentity.DisplayName);
        }

        // A página de retorno do OAuth deixou de ser uma string dentro do cliente
        // e passou a ser o recurso incorporado abaixo; a marca que este teste
        // protege continua exatamente a mesma.
        var oauth = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Resources",
            "oauth-callback.html"));
        Assert.Contains("<title>Ralven</title>", oauth, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Ralven\"", oauth, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralSettings_ExposeAppBehaviorAndPrivacyChoices()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var document = XDocument.Load(
            Path.Combine(root, "src", "Ralven.App", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var checkBoxBindings = document
            .Descendants(presentation + "CheckBox")
            .Select(element => (string?)element.Attribute("IsChecked"))
            .ToArray();

        Assert.Equal(
            new[]
            {
                "{Binding LaunchAtStartup}",
                "{Binding StartMinimized}",
                "{Binding MinimizeToTrayOnClose}",
                "{Binding CheckForUpdates}",
                "{Binding NotifyWhenUpdateAvailable}",
                "{Binding ShareOptionalReports}"
            },
            checkBoxBindings);

        var radioBindings = document
            .Descendants(presentation + "RadioButton")
            .Select(element => (string?)element.Attribute("IsChecked"))
            .Where(value => value is not null)
            .ToArray();

        Assert.DoesNotContain("{Binding IsCloseAppOnCloseSelected, Mode=OneWay}", radioBindings);
        Assert.DoesNotContain("{Binding IsMinimizeToTrayOnCloseSelected, Mode=OneWay}", radioBindings);
    }

    [Fact]
    public void SettingsSelectors_UseThemedControlAndItemTemplates()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Themes",
            "Controls.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var selectorStyle = Assert.Single(
            document.Descendants(presentation + "Style"),
            element => (string?)element.Attribute(xaml + "Key") == "SettingsComboBoxStyle");

        Assert.Contains(selectorStyle.Descendants(presentation + "ControlTemplate"), template =>
            (string?)template.Attribute("TargetType") == "ComboBox");
        Assert.Contains(selectorStyle.Descendants(presentation + "Style"), style =>
            (string?)style.Attribute("TargetType") == "ComboBoxItem");
        Assert.Contains(selectorStyle.Descendants(presentation + "Popup"), popup =>
            (string?)popup.Attribute(xaml + "Name") == "PART_Popup");
        Assert.All(
            selectorStyle.Descendants(presentation + "Border")
                .Where(border => border.Attribute("CornerRadius") is not null),
            border => Assert.Equal("{StaticResource RadiusMd}", (string?)border.Attribute("CornerRadius")));
    }

    [Fact]
    public void BugReportAndCopyright_AreInSettingsInsteadOfAGlobalFooter()
    {
        // O rodapé global ("Relatar um bug · © ano") foi removido do shell —
        // as duas informações agora moram em Configurações, perto de onde já
        // fazem sentido (Ferramentas e Sobre), e usam a mesma chave de
        // localização de antes.
        var root = TestHelpers.FindRepositoryRoot();
        var mainWindowPath = Path.Combine(root, "src", "Ralven.App", "MainWindow.xaml");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var document = XDocument.Load(mainWindowPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.DoesNotContain("Grid.Row=\"2\"", mainWindow, StringComparison.Ordinal);
        Assert.Single(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "ReportBug_Click");
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("Brand.FooterCopyright", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ReleaseNotesLinkButton_UsesLinkButtonStyleInsteadOfTheDefaultButtonChrome()
    {
        // Regression guard: this button previously set Background/BorderThickness
        // manually but kept the default Button ControlTemplate, so WPF still
        // painted its default blue focus/hover chrome around it -- the same
        // bug already fixed once for the "Reportar um bug" link. Using the
        // shared LinkButtonStyle (a bare ContentPresenter template, no focus
        // visual) is what actually removes it.
        var root = TestHelpers.FindRepositoryRoot();
        var document = XDocument.Load(
            Path.Combine(root, "src", "Ralven.App", "Views", "Pages", "OverviewPage.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var releaseNotesButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "OpenReleaseNotes_Click");

        Assert.Equal("{StaticResource LinkButtonStyle}", (string?)releaseNotesButton.Attribute("Style"));
    }

    [Fact]
    public void MainWindow_MaximizesToTheCurrentMonitorWorkArea()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "MainWindow.xaml"));
        var source = TestHelpers.ReadMainWindowSource();

        Assert.Contains("WindowState=\"Maximized\"", markup, StringComparison.Ordinal);
        Assert.Contains("WmGetMinMaxInfo", source, StringComparison.Ordinal);
        Assert.Contains("WindowMessageHook", source, StringComparison.Ordinal);
        Assert.Contains("MonitorFromWindow", source, StringComparison.Ordinal);
        Assert.Contains("GetMonitorInfo", source, StringComparison.Ordinal);
        Assert.Contains("minMaxInfo.MaxSize", source, StringComparison.Ordinal);
        Assert.Contains("WindowState = WindowState.Maximized", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ButtonStyles_InheritTheSharedChromeAndKeepVisibleFocus()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Themes",
            "Controls.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var styles = document.Descendants(presentation + "Style")
            .Where(element => element.Attribute(xaml + "Key") is not null)
            .ToDictionary(element => (string)element.Attribute(xaml + "Key")!, StringComparer.Ordinal);
        var baseStyle = styles["ButtonBaseStyle"];

        Assert.Contains(baseStyle.Descendants(presentation + "ControlTemplate"), template =>
            (string?)template.Attribute("TargetType") == "Button");
        Assert.Contains(baseStyle.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FocusVisualStyle"
            && (string?)setter.Attribute("Value") == "{StaticResource KeyboardFocusVisual}");

        foreach (var key in new[]
                 {
                     "PrimaryButtonStyle",
                     "SecondaryButtonStyle",
                     "DangerGhostButtonStyle",
                     "LinkButtonStyle",
                     "IconButtonStyle"
                 })
        {
            Assert.Equal("{StaticResource ButtonBaseStyle}", (string?)styles[key].Attribute("BasedOn"));
        }

        Assert.Equal("{StaticResource SecondaryButtonStyle}", (string?)styles["ProviderButtonStyle"].Attribute("BasedOn"));
        Assert.Contains(styles["LinkButtonStyle"].Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver");
        Assert.Contains(styles["LinkButtonStyle"].Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsPressed");
    }

    [Fact]
    public void ButtonDeclarations_UseTheSharedSystemInsteadOfDefaultChrome()
    {
        var appDirectory = Path.Combine(TestHelpers.FindRepositoryRoot(), "src", "Ralven.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (var path in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path);
            foreach (var button in document.Descendants(presentation + "Button"))
            {
                var hasDeclaredStyle = button.Attribute("Style") is not null
                    || button.Element(presentation + "Button.Style") is not null;

                Assert.True(hasDeclaredStyle, $"{Path.GetRelativePath(appDirectory, path)} contém um Button sem estilo compartilhado.");
            }
        }
    }

    [Fact]
    public void SettingsAndWindowChrome_UseTheRefinedSpacingAndHoverContracts()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "MainWindow.xaml"));
        var controls = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "Themes",
            "Controls.xaml"));

        Assert.DoesNotContain("Text=\"{Binding [Settings.Subtitle]", mainWindow, StringComparison.Ordinal);
        Assert.Contains("<ui:TitleBar", mainWindow, StringComparison.Ordinal);

        // O seletor reserva folga à direita para o chevron: sem ela o valor
        // selecionado passa por baixo da seta em idiomas de rótulo longo. O
        // teste trava a REGRA (folga direita > folga esquerda, e o suficiente
        // para o glifo), não um valor de padding específico, que muda sempre
        // que a altura do controle é reajustada.
        var comboPadding = ThicknessOf(SettingsComboBoxPadding(controls));
        Assert.True(
            comboPadding.Right >= 30,
            $"SettingsComboBoxStyle reserva apenas {comboPadding.Right}px à direita; o chevron precisa de pelo menos 30.");
        Assert.True(
            comboPadding.Right > comboPadding.Left,
            "SettingsComboBoxStyle precisa de mais folga à direita que à esquerda: a seta mora naquele lado.");

        Assert.Contains("Content=\"{TemplateBinding SelectionBoxItem}\"", controls, StringComparison.Ordinal);
        Assert.Contains("LocalizationCatalog.SupportedLanguages", TestHelpers.ReadMainWindowSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionBadge_RemovesProtectionStatusAndShowsTheInstalledVersion()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "MainWindow.xaml"));
        Assert.DoesNotContain("Safety.Active", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Safety.SnapshotRollback", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon=\"{ui:SymbolIcon Shield24}\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource FieldSurface}\" Padding=\"12,8\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource HeroAccentRule}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource AccentWashBrush}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"{StaticResource RadiusXs}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding [Sidebar.Version], Source={StaticResource LocalizedStrings}, Mode=OneWay}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AppVersion, Mode=OneWay}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OverlineText}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource BodyStrongText}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding AboutVersionDeveloper}\"", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheCleanup_RequiresExplicitLocalizedConfirmation()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var settingsCode = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Ralven.App",
            "MainWindow.Settings.xaml.cs"));

        Assert.Contains("OptimizationConfirmationWindow.Confirm", settingsCode, StringComparison.Ordinal);
        foreach (var key in new[] { "Settings.Cache.Confirm.Title", "Settings.Cache.Confirm.Message" })
        {
            foreach (var culture in new[] { "en-US", "pt-BR", "es" })
            {
                var localization = new LocalizationService(CultureInfo.GetCultureInfo(culture));
                Assert.NotEqual(key, localization.GetString(key));
            }
        }
    }

    /// <summary>
    /// Remove comentários XML da marcação antes de uma verificação textual.
    /// Sem isto, um comentário que explica por que uma regra existe conta
    /// como violação dela — o que na prática proíbe documentar a decisão
    /// junto do código que a implementa.
    /// </summary>
    private static string WithoutXmlComments(string markup)
    {
        return XmlCommentPattern().Replace(markup, string.Empty);
    }

    private static LocalizationService[] SupportedLocalizations() =>
        LocalizationCatalog.SupportedLanguages
            .Select(language => new LocalizationService(CultureInfo.GetCultureInfo(language.CultureName)))
            .ToArray();

    private static string[] ResourceCatalogPaths(string resourceDirectory) =>
        Directory.GetFiles(resourceDirectory, "Strings*.resx")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Extrai o valor de <c>Padding</c> declarado por <c>SettingsComboBoxStyle</c>
    /// em <c>Themes/Controls.xaml</c>.
    /// </summary>
    private static string SettingsComboBoxPadding(string controls)
    {
        var document = XDocument.Parse(controls);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = Assert.Single(
            document.Descendants(presentation + "Style"),
            element => (string?)element.Attribute(xaml + "Key") == "SettingsComboBoxStyle");
        var padding = style
            .Elements(presentation + "Setter")
            .FirstOrDefault(setter => (string?)setter.Attribute("Property") == "Padding");

        Assert.NotNull(padding);
        var value = (string?)padding!.Attribute("Value");
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>Interpreta um <c>Thickness</c> XAML de quatro componentes.</summary>
    private static (double Left, double Top, double Right, double Bottom) ThicknessOf(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(4, parts.Length);
        var numbers = parts
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();
        return (numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex XmlCommentPattern();

    [GeneratedRegex(@"<Run\b[^>]*\bText=""\{Binding \[[^]]+\], Source=\{StaticResource LocalizedStrings\}(?![^""]*\bMode=OneWay)", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizedRunWithoutOneWayPattern();

    [GeneratedRegex(@"\[\s*(?<key>[A-Za-z0-9_.-]+)\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizedKeyPattern();

    [GeneratedRegex(@"\b(?:T|F)\(""(?<key>[A-Za-z0-9_.-]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizedCodeKeyPattern();

    [GeneratedRegex(@"MessageBox\.Show\(\s*""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex HardcodedMessageBoxPattern();
}
