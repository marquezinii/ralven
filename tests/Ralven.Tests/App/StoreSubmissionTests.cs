using Ralven.App.Services;
using Ralven.App.ViewModels;
using Ralven.Contracts;
using Xunit;

namespace Ralven.Tests.App;

public sealed class StoreSubmissionTests
{
    [Fact]
    public void NoUpdateService_DisablesManualUpdateEntryPoint()
    {
        using var model = new MainViewModel(
            new FakeAppOptimizationService(new AppSettings(), settingsFileExists: false),
            startupRegistration: new SessionStartupRegistrationService());
        Assert.False(model.CanCheckForUpdatesManually);
    }

    [Fact]
    public async Task WebCandidate_HagsOptInDoesNotChangeWebPlan()
    {
        Assert.False(Ralven.UpdateRuntime.PackageIdentity.IsPackaged);
        using var model = new MainViewModel(
            new FakeAppOptimizationService(new AppSettings(), settingsFileExists: false, fiveMRoot: @"C:\FiveM"),
            startupRegistration: new SessionStartupRegistrationService());
        await model.InitializeAsync(startBackgroundServices: false);
        model.SetOptimizationScope(OptimizationScope.FiveMLegacy);
        Assert.False(model.IsStoreCertificationCandidate);
        model.SelectProfile(OptimizationProfile.Aggressive);
        var originalIds = model.PlannedActions.Select(action => action.Id).ToArray();
        Assert.NotEmpty(originalIds);
        model.StoreHagsExperiment = true;
        Assert.Equal(originalIds, model.PlannedActions.Select(action => action.Id).ToArray());
    }
}
