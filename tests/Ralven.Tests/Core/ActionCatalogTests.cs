using Ralven.Contracts;
using Ralven.Core.Catalog;
using Xunit;

namespace Ralven.Tests.Core;

public sealed class ActionCatalogTests
{
    [Fact]
    public void CurrentCatalog_HasStableUniqueDefinitions()
    {
        var catalog = ActionCatalog.Current;

        Assert.Equal(22, ActionCatalog.CurrentVersion);
        Assert.NotEmpty(catalog.Actions);

        Assert.All(catalog.Actions, action =>
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Id));
            Assert.Equal(action.Id.Trim(), action.Id);
            Assert.Equal(action.Id.ToLowerInvariant(), action.Id);
            Assert.True(action.Version > 0);
            Assert.False(string.IsNullOrWhiteSpace(action.Name));
            Assert.False(string.IsNullOrWhiteSpace(action.Description));
            Assert.False(string.IsNullOrWhiteSpace(action.ExpectedImpact));
            Assert.True(action.ProgressWeight > 0);
            Assert.NotEmpty(action.SupportedProfiles);
            Assert.Equal(action.SupportedProfiles.Count, action.SupportedProfiles.Distinct().Count());
            Assert.NotEmpty(action.SupportedScopes);
            Assert.Equal(action.SupportedScopes.Count, action.SupportedScopes.Distinct().Count());

            Assert.False(string.IsNullOrWhiteSpace(action.DetectionSummary));
            Assert.False(string.IsNullOrWhiteSpace(action.ConfirmationSummary));
            Assert.False(string.IsNullOrWhiteSpace(action.UndoSummary));
            Assert.False(string.IsNullOrWhiteSpace(action.RiskLimitations));
            Assert.NotEqual(SupportedWindowsVersions.None, action.SupportedWindows);

            Assert.All(action.Prerequisites, prerequisiteId =>
                Assert.True(catalog.TryGet(prerequisiteId, out _)));
        });
    }

    [Fact]
    public void GeneralWindowsScope_IsAnExplicitFailClosedAllowlist()
    {
        var generalIds = ActionCatalog.Current.Actions
            .Where(action => action.Supports(OptimizationScope.GeneralWindows))
            .Select(action => action.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expected = new[]
        {
            OptimizationActionIds.DiagnoseBottleneck,
            OptimizationActionIds.DetectOverlaysAndCaptureSoftware,
            OptimizationActionIds.DiagnoseNetworkHealth,
            OptimizationActionIds.DiagnoseThermalThrottling,
            OptimizationActionIds.DiagnosePagefileCommit,
            OptimizationActionIds.DetectGpuVendor,
            OptimizationActionIds.DiagnoseCpuDetails,
            OptimizationActionIds.DiagnoseGpuDetails,
            OptimizationActionIds.DiagnoseRamDetails,
            OptimizationActionIds.DiagnoseStorageHealth,
            OptimizationActionIds.DiagnoseDriverVersions,
            OptimizationActionIds.DiagnoseDisplayConfiguration,
            OptimizationActionIds.DiagnoseSessionSettings,
            OptimizationActionIds.DiagnoseThrottlingSignal,
            OptimizationActionIds.DiagnoseResourceUsage,
            OptimizationActionIds.DiagnosePciLink,
            OptimizationActionIds.DiagnoseHardwareStability,
            OptimizationActionIds.ClassifyBottleneck,
            OptimizationActionIds.CleanUserTemporaryFiles,
            OptimizationActionIds.EnableGameMode,
            OptimizationActionIds.DisableBackgroundCapture,
            OptimizationActionIds.EnableSessionPerformancePowerPlan,
            OptimizationActionIds.AdjustPciExpressPowerManagement,
            OptimizationActionIds.ReduceWindowsVisualEffects,
            OptimizationActionIds.ReduceMenuShowDelay,
            OptimizationActionIds.GuideDriverReinstall,
            OptimizationActionIds.DiagnoseHybridLaptop,
            OptimizationActionIds.GuideMousePollingRate,
            OptimizationActionIds.DiagnoseWindowsSecurityHealth,
            OptimizationActionIds.DiagnoseStartupLoad,
            OptimizationActionIds.DiagnoseTrimStatus,
            OptimizationActionIds.DiagnoseMouseAcceleration,
            OptimizationActionIds.DisableMouseAcceleration
        }.Order(StringComparer.Ordinal);

        Assert.Equal(expected, generalIds);

        Assert.All(ActionCatalog.Current.Actions, action =>
            Assert.True(action.Supports(OptimizationScope.FiveMLegacy)));
    }

    [Fact]
    public void CurrentCatalog_DefinesEveryPublishedActionIdExactlyOnce()
    {
        var publishedIds = typeof(OptimizationActionIds)
            .GetFields()
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var catalogIds = ActionCatalog.Current.Actions
            .Select(action => action.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(publishedIds, catalogIds);
    }

    [Theory]
    [InlineData(
        OptimizationActionIds.ApplyLightGtaVGraphics,
        OptimizationProfile.Light,
        ActionRisk.Low)]
    [InlineData(
        OptimizationActionIds.ApplyBalancedGtaVGraphics,
        OptimizationProfile.Balanced,
        ActionRisk.Moderate)]
    [InlineData(
        OptimizationActionIds.ApplyAggressiveGtaVGraphics,
        OptimizationProfile.Aggressive,
        ActionRisk.High)]
    public void GtaVGraphicsDefinitions_AreProfileSpecificAndReversible(
        string actionId,
        OptimizationProfile profile,
        ActionRisk risk)
    {
        var definition = ActionCatalog.Current.GetRequired(actionId);

        Assert.Equal([profile], definition.SupportedProfiles);
        Assert.Equal(risk, definition.Risk);
        Assert.Equal(ActionCategory.FiveMGraphics, definition.Category);
        Assert.Equal(ActionReversibility.FullyReversible, definition.Reversibility);
        Assert.Equal(RequiredPrivilege.StandardUser, definition.RequiredPrivilege);
        Assert.True(definition.RequiresFiveMStopped);
    }

    [Fact]
    public void SupportsWindows_GatesByDetectedVersionAndFailsOpenWhenUnknown()
    {
        var action = ActionCatalog.Current.GetRequired(OptimizationActionIds.EnableGameMode);

        Assert.True(action.SupportsWindows(SupportedWindowsVersions.Windows10));
        Assert.True(action.SupportsWindows(SupportedWindowsVersions.Windows11));
        // Quando a versão não é detectada, a ação permanece elegível (fail-open).
        Assert.True(action.SupportsWindows(SupportedWindowsVersions.None));
    }

    [Fact]
    public void GetRequired_RejectsUnknownActionIds()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            ActionCatalog.Current.GetRequired("custom.command.from-untrusted-input"));
    }

    [Fact]
    public void ElevatedActions_AreExplicitlyMarkedAndReversible()
    {
        var elevated = ActionCatalog.Current.Actions
            .Where(action => action.RequiredPrivilege == RequiredPrivilege.Administrator)
            .ToArray();

        Assert.Equal(2, elevated.Length);
        Assert.All(elevated, action =>
            Assert.Equal(ActionReversibility.FullyReversible, action.Reversibility));

        var powerAction = Assert.Single(elevated, action =>
            action.Id == OptimizationActionIds.EnableSessionPerformancePowerPlan);
        Assert.True(powerAction.RequiresAcPower);
        Assert.True(powerAction.AttemptWithoutElevationFirst);

        var hagsAction = Assert.Single(elevated, action =>
            action.Id == OptimizationActionIds.ToggleHags);
        Assert.True(hagsAction.RequiresRestart);
        Assert.False(hagsAction.AttemptWithoutElevationFirst);
    }

    [Fact]
    public void InformationalActions_AreReadOnly()
    {
        var informational = ActionCatalog.Current.Actions
            .Where(action => action.Risk == ActionRisk.Informational);

        Assert.NotEmpty(informational);
        Assert.All(informational, action =>
            Assert.Equal(ActionReversibility.ReadOnly, action.Reversibility));
    }

    [Fact]
    public void AggressiveProfile_DoesNotIntroduceUnknownOrUnsafeExecutionDescriptors()
    {
        var publicProperties = typeof(OptimizationActionDefinition)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        var unsafeExecutionProperties = new[]
        {
            "Command",
            "CommandLine",
            "Script",
            "Arguments",
            "ExecutablePath",
            "WorkingDirectory"
        };

        Assert.Empty(publicProperties.Intersect(unsafeExecutionProperties, StringComparer.OrdinalIgnoreCase));
    }
}
