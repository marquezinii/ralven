using Ralven.App.Services;
using Ralven.App.ViewModels;
using Xunit;

namespace Ralven.Tests.App;

/// <summary>
/// Exercises the wiring between <see cref="MainViewModel"/> and
/// <see cref="ReleaseNotesEvaluator"/>: that settings loaded during
/// <see cref="MainViewModel.InitializeAsync"/> produce a decision, and that
/// <see cref="MainViewModel.ConfirmReleaseNotesSeenAsync"/> persists through
/// the existing settings mechanism. <see cref="ReleaseNotesCatalog.Versions"/>
/// is versioned with the public release. Tests that need a particular version
/// continue to use <see cref="ReleaseNotesEvaluatorTests"/>, which accepts it
/// as an explicit parameter instead of reading the running assembly.
/// </summary>
public sealed class MainViewModelReleaseNotesTests
{
    [Fact]
    public async Task InitializeAsync_NewInstallation_ComputesADecisionThatNeverShows()
    {
        var service = new FakeAppOptimizationService(new AppSettings(), settingsFileExists: false);
        var viewModel = new MainViewModel(service);

        await viewModel.InitializeAsync();

        var decision = viewModel.PendingReleaseNotes;
        Assert.NotNull(decision);
        Assert.False(decision!.ShouldShow);
        Assert.True(decision.ShouldRecordSilently);
    }

    [Fact]
    public async Task InitializeAsync_ExistingInstallation_OffersCurrentReleaseNotes()
    {
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "0.1.0" };
        var service = new FakeAppOptimizationService(settings, settingsFileExists: true);
        var viewModel = new MainViewModel(service);

        await viewModel.InitializeAsync();

        var decision = viewModel.PendingReleaseNotes;
        Assert.NotNull(decision);
        Assert.True(decision!.ShouldShow);
        Assert.False(decision.ShouldRecordSilently);
        Assert.Equal("1.7.1", decision.Entry!.Version);
    }

    [Fact]
    public async Task ConfirmReleaseNotesSeenAsync_PersistsTheGivenVersionThroughTheExistingSettingsPath()
    {
        var service = new FakeAppOptimizationService(new AppSettings(), settingsFileExists: false);
        var viewModel = new MainViewModel(service);
        await viewModel.InitializeAsync();

        await viewModel.ConfirmReleaseNotesSeenAsync("1.9.0");

        Assert.NotNull(service.SavedSettings);
        Assert.Equal("1.9.0", service.SavedSettings!.LastSeenReleaseNotesVersion);
        Assert.Null(viewModel.PendingReleaseNotes);
    }

    [Fact]
    public async Task ConfirmReleaseNotesSeenAsync_PreservesEveryOtherExistingSetting()
    {
        var oldSettings = new AppSettings
        {
            Language = AppLanguagePreference.English,
            Theme = AppThemePreference.Dark,
            MinimizeToTrayOnClose = false,
            LaunchAtStartup = true,
            CheckForUpdates = false,
            LastSeenReleaseNotesVersion = "1.0.0"
        };
        var service = new FakeAppOptimizationService(oldSettings, settingsFileExists: true);
        var viewModel = new MainViewModel(service);
        await viewModel.InitializeAsync();

        await viewModel.ConfirmReleaseNotesSeenAsync("1.9.0");

        Assert.Equal(AppLanguagePreference.English, service.SavedSettings!.Language);
        Assert.Equal(AppThemePreference.Dark, service.SavedSettings.Theme);
        Assert.False(service.SavedSettings.CheckForUpdates);
        Assert.Equal("1.9.0", service.SavedSettings.LastSeenReleaseNotesVersion);
    }

    [Fact]
    public async Task ConfirmReleaseNotesSeenAsync_SurvivesAcrossANewViewModelInstance_LikeARealRestart()
    {
        // Simulates persistence surviving a restart: the same in-memory
        // "disk" (FakeAppOptimizationService.SavedSettings promoted to the
        // next load) is handed to a brand-new MainViewModel, mirroring how a
        // real settings.json file would be read back on the next launch.
        var firstRunService = new FakeAppOptimizationService(new AppSettings(), settingsFileExists: false);
        var firstRunViewModel = new MainViewModel(firstRunService);
        await firstRunViewModel.InitializeAsync();
        await firstRunViewModel.ConfirmReleaseNotesSeenAsync("1.9.0");

        var secondRunService = new FakeAppOptimizationService(
            firstRunService.SavedSettings!,
            settingsFileExists: true);
        var secondRunViewModel = new MainViewModel(secondRunService);
        await secondRunViewModel.InitializeAsync();

        Assert.False(secondRunViewModel.PendingReleaseNotes!.ShouldShow);
    }
}
