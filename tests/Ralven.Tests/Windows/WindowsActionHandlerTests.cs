using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Windows.Actions;
using Ralven.Windows.Infrastructure;
using Microsoft.Win32;
using System.Xml.Linq;
using Xunit;

namespace Ralven.Tests.Windows;

public sealed class WindowsActionHandlerTests
{
    [Fact]
    public async Task GraphicsPreset_ChangesAllowlistedNodesAndRollbackRestoresExactFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installation = temporaryDirectory.Combine("FiveM");
        Directory.CreateDirectory(installation);
        var settings = temporaryDirectory.Combine("CitizenFX", "gta5_settings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        const string original = "<Settings><graphics><ShadowQuality value=\"3\"/><MSAA value=\"4\"/><ScreenWidth value=\"1920\"/></graphics></Settings>";
        File.WriteAllText(settings, original);
        var action = new LegacyGraphicsPresetAction(
            settings,
            installation,
            OptimizationProfile.Balanced,
            new FakeProcessInspector());
        var context = Context();

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.True(result.Changed);
        var updated = File.ReadAllText(settings);
        Assert.Contains("ShadowQuality value=\"1\"", updated, StringComparison.Ordinal);
        Assert.Contains("MSAA value=\"0\"", updated, StringComparison.Ordinal);
        Assert.Contains("ScreenWidth value=\"1920\"", updated, StringComparison.Ordinal);
        Assert.Equal(OptimizationActionIds.ApplyBalancedLegacyGraphics, action.Metadata.Id);

        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);

        Assert.Equal(original, File.ReadAllText(settings));
    }

    [Fact]
    public async Task GraphicsRollback_RefusesToOverwriteNewerUserEdit()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installation = temporaryDirectory.Combine("FiveM");
        Directory.CreateDirectory(installation);
        var settings = temporaryDirectory.Combine("CitizenFX", "gta5_settings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "<Settings><graphics><ShadowQuality value=\"3\"/></graphics></Settings>");
        var action = new LegacyGraphicsPresetAction(
            settings,
            installation,
            OptimizationProfile.Aggressive,
            new FakeProcessInspector());
        var context = Context();
        var result = await action.ApplyAsync(context, CancellationToken.None);
        File.WriteAllText(settings, "<Settings><graphics><ShadowQuality value=\"2\"/></graphics></Settings>");

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));
        Assert.Contains("value=\"2\"", File.ReadAllText(settings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphicsPreset_OnlyChangesDirectGraphicsChildren()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installation = temporaryDirectory.Combine("FiveM");
        var settings = temporaryDirectory.Combine("CitizenFX", "gta5_settings.xml");
        Directory.CreateDirectory(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(
            settings,
            "<Settings><graphics><MSAA value=\"4\"/></graphics>"
            + "<profile><MSAA value=\"4\"/></profile></Settings>");
        var action = new LegacyGraphicsPresetAction(
            settings,
            installation,
            OptimizationProfile.Light,
            new FakeProcessInspector());

        var result = await action.ApplyAsync(Context(), CancellationToken.None);

        Assert.True(result.Changed);
        var document = XDocument.Load(settings);
        Assert.Equal("0", document.Root!.Element("graphics")!.Element("MSAA")!.Attribute("value")!.Value);
        Assert.Equal("4", document.Root.Element("profile")!.Element("MSAA")!.Attribute("value")!.Value);
    }

    [Fact]
    public async Task RegistryRollback_RestoresMissingValue()
    {
        var registry = new FakeRegistryStore();
        var action = new GameModeRegistryAction(registry, new FakeProcessInspector());
        var context = Context();

        var result = await action.ApplyAsync(context, CancellationToken.None);
        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);

        var address = new RegistryAddress(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\GameBar",
            "AutoGameModeEnabled");
        Assert.False(registry.Read(address).Exists);
    }

    [Fact]
    public async Task RegistryRollback_PreservesValueChangedAfterOptimization()
    {
        var registry = new FakeRegistryStore();
        var address = new RegistryAddress(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\GameBar",
            "AutoGameModeEnabled");
        registry.Write(address, RegistryValueState.FromDword(0));
        var action = new GameModeRegistryAction(registry, new FakeProcessInspector());
        var context = Context();
        var result = await action.ApplyAsync(context, CancellationToken.None);
        registry.Write(address, RegistryValueState.FromDword(2));

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));

        Assert.Equal(2, registry.Read(address).NumericValue);
    }

    [Fact]
    public async Task RegistryRollback_RejectsSnapshotOutsideActionAllowlist()
    {
        var registry = new FakeRegistryStore();
        var action = new GameModeRegistryAction(registry, new FakeProcessInspector());
        var context = Context();
        var maliciousAddress = new RegistryAddress(
            RegistryHive.CurrentUser,
            @"Software\RalvenTests\OutsideAllowlist",
            "UnexpectedValue");
        var tampered = WindowsActionSnapshot.Serialize(
            new RegistryMutationSnapshot(
            [
                new RegistryMutationSnapshotEntry(
                    maliciousAddress,
                    RegistryValueState.FromDword(9),
                    RegistryValueState.FromDword(1))
            ]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            action.RollbackAsync(context, tampered, CancellationToken.None));

        Assert.False(registry.Read(maliciousAddress).Exists);
    }

    [Fact]
    public async Task RegistryRollback_RejectsEmptySnapshot()
    {
        var registry = new FakeRegistryStore();
        var action = new GameModeRegistryAction(registry, new FakeProcessInspector());
        var emptySnapshot = WindowsActionSnapshot.Serialize(
            new RegistryMutationSnapshot([]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            action.RollbackAsync(Context(), emptySnapshot, CancellationToken.None));
    }

    [Fact]
    public async Task GameModeAction_WhenFiveMIsRunning_DoesNotWrite()
    {
        var registry = new FakeRegistryStore();
        registry.Write(GameModeRegistryAction.Address, RegistryValueState.FromDword(0));
        var action = new GameModeRegistryAction(
            registry,
            new FakeProcessInspector(running: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.ApplyAsync(Context(), CancellationToken.None));

        Assert.Equal(0, registry.Read(GameModeRegistryAction.Address).NumericValue);
    }

    [Fact]
    public async Task GameModeRollback_WhenFiveMStarts_PreservesTheAppliedValue()
    {
        var registry = new FakeRegistryStore();
        registry.Write(GameModeRegistryAction.Address, RegistryValueState.FromDword(0));
        var processInspector = new FakeProcessInspector();
        var action = new GameModeRegistryAction(registry, processInspector);
        var context = Context();
        var applied = await action.ApplyAsync(context, CancellationToken.None);
        processInspector.Running = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.RollbackAsync(context, applied.SnapshotJson, CancellationToken.None));

        Assert.Equal(1, registry.Read(GameModeRegistryAction.Address).NumericValue);
    }

    [Fact]
    public async Task GameDvrAction_DisablesOnlyHistoricalBackgroundCapture()
    {
        var registry = new FakeRegistryStore();
        var historical = new RegistryAddress(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "HistoricalCaptureEnabled");
        var manualCapture = new RegistryAddress(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "AppCaptureEnabled");
        var legacyToggle = new RegistryAddress(
            RegistryHive.CurrentUser,
            @"System\GameConfigStore",
            "GameDVR_Enabled");
        registry.Write(historical, RegistryValueState.FromDword(1));
        registry.Write(manualCapture, RegistryValueState.FromDword(1));
        registry.Write(legacyToggle, RegistryValueState.FromDword(1));

        var result = await new GameDvrRegistryAction(registry, new FakeProcessInspector())
            .ApplyAsync(Context(), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(0, registry.Read(historical).NumericValue);
        Assert.Equal(1, registry.Read(manualCapture).NumericValue);
        Assert.Equal(1, registry.Read(legacyToggle).NumericValue);
    }

    [Fact]
    public async Task GpuPreferenceAction_IncludesKnownFiveMRendererFromCanonicalCache()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.Combine("FiveM");
        var rendererDirectory = Path.Combine(root, "FiveM.app", "data", "cache", "subprocess");
        Directory.CreateDirectory(rendererDirectory);
        var launcher = Path.Combine(root, "FiveM.exe");
        var renderer = Path.Combine(rendererDirectory, "FiveM_b3258_GTAProcess.exe");
        var decoy = Path.Combine(rendererDirectory, "FiveM_bbad_GTAProcess.exe");
        File.WriteAllText(launcher, string.Empty);
        File.WriteAllText(renderer, string.Empty);
        File.WriteAllText(decoy, string.Empty);
        var registry = new FakeRegistryStore();
        var subKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        var launcherAddress = new RegistryAddress(
            RegistryHive.CurrentUser,
            subKey,
            launcher);
        registry.Write(
            launcherAddress,
            RegistryValueState.FromString("GpuPreference=1;AutoHDREnable=2097;"));
        var action = new GpuPreferenceRegistryAction(registry, launcher, root);
        var context = Context();

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(
            "GpuPreference=2;AutoHDREnable=2097;",
            registry.Read(launcherAddress).StringValue);
        Assert.Equal(
            "GpuPreference=2;",
            registry.Read(new RegistryAddress(RegistryHive.CurrentUser, subKey, renderer)).StringValue);
        Assert.False(registry.Read(
            new RegistryAddress(RegistryHive.CurrentUser, subKey, decoy)).Exists);

        File.Delete(renderer);
        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);
        Assert.Equal(
            "GpuPreference=1;AutoHDREnable=2097;",
            registry.Read(launcherAddress).StringValue);
        Assert.False(registry.Read(
            new RegistryAddress(RegistryHive.CurrentUser, subKey, renderer)).Exists);
    }

    [Fact]
    public async Task HagsToggle_FlipsToTheOppositeStateAndRollbackRestoresOriginal()
    {
        var registry = new FakeRegistryStore();
        var address = new RegistryAddress(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode");
        registry.Write(address, RegistryValueState.FromDword(1));
        var action = new HagsToggleAction(registry);
        var context = Context();

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(2, registry.Read(address).NumericValue);

        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);
        Assert.Equal(1, registry.Read(address).NumericValue);
    }

    [Fact]
    public async Task HagsToggle_FlipsBackToDisabledWhenCurrentlyEnabled()
    {
        var registry = new FakeRegistryStore();
        var address = new RegistryAddress(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode");
        registry.Write(address, RegistryValueState.FromDword(2));
        var action = new HagsToggleAction(registry);

        var result = await action.ApplyAsync(Context(), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(1, registry.Read(address).NumericValue);
    }

    [Fact]
    public async Task HagsToggle_TreatsMissingValueAsDefaultAndFlipsToEnabled()
    {
        var registry = new FakeRegistryStore();
        var address = new RegistryAddress(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode");
        var action = new HagsToggleAction(registry);

        var result = await action.ApplyAsync(Context(), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(2, registry.Read(address).NumericValue);
    }

    [Fact]
    public async Task FullscreenOptimizations_TogglesFlagForFiveMAndGtaVPreservingOtherFlags()
    {
        var registry = new FakeRegistryStore();
        const string subKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        var fiveM = @"C:\FiveM\FiveM.exe";
        var gtaV = @"C:\Games\GTAV\GTA5.exe";
        var fiveMAddress = new RegistryAddress(RegistryHive.CurrentUser, subKey, fiveM);
        registry.Write(fiveMAddress, RegistryValueState.FromString("HIGHDPIAWARE"));
        var action = new FullscreenOptimizationsRegistryAction(registry, fiveM, gtaV);
        var context = Context();

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(
            "HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE",
            registry.Read(fiveMAddress).StringValue);
        Assert.Equal(
            "DISABLEDXMAXIMIZEDWINDOWEDMODE",
            registry.Read(new RegistryAddress(RegistryHive.CurrentUser, subKey, gtaV)).StringValue);

        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);
        Assert.Equal("HIGHDPIAWARE", registry.Read(fiveMAddress).StringValue);
        Assert.False(registry.Read(new RegistryAddress(RegistryHive.CurrentUser, subKey, gtaV)).Exists);
    }

    [Fact]
    public async Task FullscreenOptimizations_TogglesBackOffWhenAlreadyDisabled()
    {
        var registry = new FakeRegistryStore();
        const string subKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        var fiveM = @"C:\FiveM\FiveM.exe";
        var fiveMAddress = new RegistryAddress(RegistryHive.CurrentUser, subKey, fiveM);
        registry.Write(fiveMAddress, RegistryValueState.FromString("DISABLEDXMAXIMIZEDWINDOWEDMODE"));
        var action = new FullscreenOptimizationsRegistryAction(registry, fiveM, gtaVExecutablePath: null);

        var context = Context();
        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(string.Empty, registry.Read(fiveMAddress).StringValue);

        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);
        Assert.Equal("DISABLEDXMAXIMIZEDWINDOWEDMODE", registry.Read(fiveMAddress).StringValue);
    }

    [Fact]
    public async Task SessionPowerPlan_RequiresAcAndRestoresPreviousScheme()
    {
        var controller = new FakePowerPlanController();
        var previous = controller.ActiveScheme;
        var powerStatus = new FakePowerStatusProvider(isOnAcPower: false);
        var action = new SessionPerformancePowerPlanAction(controller, powerStatus);
        var context = Context(elevated: true);

        var batteryResult = await action.ApplyAsync(context, CancellationToken.None);
        Assert.False(batteryResult.Changed);
        Assert.Equal(ActionExecutionOutcome.Skipped, batteryResult.Outcome);
        Assert.Equal(previous, controller.ActiveScheme);

        powerStatus.OnAcPower = true;
        var result = await action.ApplyAsync(context, CancellationToken.None);
        Assert.True(result.Changed);
        Assert.Equal(controller.PerformanceScheme, controller.ActiveScheme);

        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);
        Assert.Equal(previous, controller.ActiveScheme);
    }

    [Fact]
    public async Task SessionPowerPlan_AlreadyActiveWithoutElevation_ReturnsNoChangeWithoutActivation()
    {
        var controller = new FakePowerPlanController();
        controller.ActiveScheme = controller.PerformanceScheme;
        var action = new SessionPerformancePowerPlanAction(controller, new FakePowerStatusProvider());
        var context = Context(elevated: false);

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ActionExecutionOutcome.Verified, result.Outcome);
        Assert.Equal(controller.PerformanceScheme, controller.ActiveScheme);
        Assert.Equal(0, controller.PerformanceActivationCount);
    }

    [Fact]
    public async Task SessionPowerPlan_DifferentSchemeWithoutElevation_DefersWithoutMutation()
    {
        var controller = new FakePowerPlanController();
        var previous = controller.ActiveScheme;
        var action = new SessionPerformancePowerPlanAction(controller, new FakePowerStatusProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => action.ApplyAsync(Context(elevated: false), CancellationToken.None));

        Assert.Equal(previous, controller.ActiveScheme);
        Assert.Equal(0, controller.PerformanceActivationCount);
    }

    [Fact]
    public async Task SessionPowerPlan_AccessDeniedWhileElevated_ThrowsUnauthorizedAccessWithoutTouchingTheScheme()
    {
        var controller = new FakePowerPlanController { DenyAccess = true };
        var previous = controller.ActiveScheme;
        var action = new SessionPerformancePowerPlanAction(controller, new FakePowerStatusProvider());
        var context = Context(elevated: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => action.ApplyAsync(context, CancellationToken.None));

        Assert.Equal(previous, controller.ActiveScheme);
    }

    [Fact]
    public async Task SessionPowerPlan_SchemeUnavailable_ReturnsNoChangeInsteadOfThrowing()
    {
        var controller = new FakePowerPlanController { PerformanceAvailable = false };
        var previous = controller.ActiveScheme;
        var action = new SessionPerformancePowerPlanAction(controller, new FakePowerStatusProvider());
        var context = Context(elevated: true);

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ActionExecutionOutcome.Skipped, result.Outcome);
        Assert.Equal(previous, controller.ActiveScheme);
    }

    [Fact]
    public async Task SessionPowerPlan_RollbackVerifiesTheRestoredScheme()
    {
        var controller = new FakePowerPlanController();
        var action = new SessionPerformancePowerPlanAction(controller, new FakePowerStatusProvider());
        var context = Context(elevated: true);
        var result = await action.ApplyAsync(context, CancellationToken.None);
        controller.IgnoreNextActivation = true;

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));
    }

    [Fact]
    public async Task SessionPowerPlan_RollbackRefusesConcurrentSchemeChange()
    {
        var controller = new FakePowerPlanController();
        var action = new SessionPerformancePowerPlanAction(controller, new FakePowerStatusProvider());
        var context = Context(elevated: true);
        var result = await action.ApplyAsync(context, CancellationToken.None);
        var newerScheme = Guid.NewGuid();
        controller.ActiveScheme = newerScheme;

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));

        Assert.Equal(newerScheme, controller.ActiveScheme);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionPowerPlan_MutateThenFailureRestoresPreviousScheme(bool cancel)
    {
        var controller = new FakePowerPlanController
        {
            CancelAfterPerformanceActivation = cancel,
            ThrowAfterPerformanceActivation = !cancel
        };
        var previous = controller.ActiveScheme;
        var action = new SessionPerformancePowerPlanAction(controller, new FakePowerStatusProvider());

        await Assert.ThrowsAnyAsync<Exception>(() => action.ApplyAsync(Context(), CancellationToken.None));

        Assert.Equal(previous, controller.ActiveScheme);
    }

    [Fact]
    public async Task PciExpressPowerManagement_SetsOffAndRollbackRestoresPreviousPolicy()
    {
        var previous = new PciExpressAspmPolicy(1, 2);
        var controller = new FakePowerPlanController { AspmPolicyState = previous };
        var action = new PciExpressPowerManagementAction(controller);
        var context = Context();

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(new PciExpressAspmPolicy(0, 0), controller.AspmPolicyState);

        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);
        Assert.Equal(previous, controller.AspmPolicyState);
    }

    [Fact]
    public async Task PciExpressPowerManagement_SuccessReturnsSnapshotWithoutRedundantPostWriteRead()
    {
        var controller = new FakePowerPlanController
        {
            AspmPolicyState = new PciExpressAspmPolicy(1, 2),
            ThrowOnAspmReadAfterSet = true
        };
        var action = new PciExpressPowerManagementAction(controller);

        var result = await action.ApplyAsync(Context(), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.NotNull(result.SnapshotJson);
        Assert.Equal(new PciExpressAspmPolicy(0, 0), controller.AspmPolicyState);
    }

    [Fact]
    public async Task PciExpressPowerManagement_NoChangeWhenAlreadyOff()
    {
        var controller = new FakePowerPlanController { AspmPolicy = 0 };
        var action = new PciExpressPowerManagementAction(controller);

        var result = await action.ApplyAsync(Context(), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ActionExecutionOutcome.Verified, result.Outcome);
    }

    [Fact]
    public async Task PciExpressPowerManagement_NoChangeWhenNotExposed()
    {
        var controller = new FakePowerPlanController { AspmPolicy = null };
        var action = new PciExpressPowerManagementAction(controller);

        var result = await action.ApplyAsync(Context(), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ActionExecutionOutcome.Skipped, result.Outcome);
        Assert.Contains(result.Messages, message => message.Contains("não expõe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PciExpressPowerManagement_SetFailureIsVisible()
    {
        var controller = new FakePowerPlanController { AspmPolicy = 1, AspmSetShouldFail = true };
        var action = new PciExpressPowerManagementAction(controller);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.ApplyAsync(Context(), CancellationToken.None));

        Assert.Equal(1, controller.AspmPolicy);
    }

    [Fact]
    public async Task PciExpressPowerManagement_RollbackRefusesConcurrentChange()
    {
        var controller = new FakePowerPlanController
        {
            AspmPolicyState = new PciExpressAspmPolicy(1, 2)
        };
        var action = new PciExpressPowerManagementAction(controller);
        var context = Context();
        var result = await action.ApplyAsync(context, CancellationToken.None);
        controller.AspmPolicyState = new PciExpressAspmPolicy(0, 1);

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));

        Assert.Equal(new PciExpressAspmPolicy(0, 1), controller.AspmPolicyState);
    }

    [Fact]
    public async Task PciExpressPowerManagement_RollbackRefusesDifferentActiveScheme()
    {
        var controller = new FakePowerPlanController
        {
            AspmPolicyState = new PciExpressAspmPolicy(1, 2)
        };
        var action = new PciExpressPowerManagementAction(controller);
        var result = await action.ApplyAsync(Context(), CancellationToken.None);
        controller.ActiveScheme = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(
            () => action.RollbackAsync(Context(), result.SnapshotJson, CancellationToken.None));

        Assert.Equal(new PciExpressAspmPolicy(0, 0), controller.AspmPolicyState);
    }

    [Fact]
    public async Task PciExpressPowerManagement_LegacySnapshotWithoutSchemeAndBothPoliciesFailsClosed()
    {
        var controller = new FakePowerPlanController { AspmPolicyState = new PciExpressAspmPolicy(0, 0) };
        var action = new PciExpressPowerManagementAction(controller);

        await Assert.ThrowsAsync<InvalidDataException>(() => action.RollbackAsync(
            Context(),
            "{\"previousPolicy\":1}",
            CancellationToken.None));

        Assert.Equal(new PciExpressAspmPolicy(0, 0), controller.AspmPolicyState);
    }

    [Fact]
    public void MousePollingRateGuidance_MentionsHighCpuLoadAndCpuPercentAboveThreshold()
    {
        var message = MousePollingRateGuidanceAction.Classify(92);

        Assert.Contains("92%", message, StringComparison.Ordinal);
        Assert.Contains("1000 Hz", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MousePollingRateGuidance_StillOrientsWhenCpuIsNotUnderHeavyLoad()
    {
        var message = MousePollingRateGuidanceAction.Classify(20);

        Assert.DoesNotContain("carga alta agora", message, StringComparison.Ordinal);
        Assert.Contains("1000 Hz", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MousePollingRateGuidance_HandlesMissingCpuReadingGracefully()
    {
        var message = MousePollingRateGuidanceAction.Classify(null);

        Assert.Contains("1000 Hz", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisualEffectsRollback_PreservesNewerUserChoice()
    {
        var controller = new FakeVisualEffectsController();
        var action = new VisualEffectsAction(controller);
        var context = Context();
        var result = await action.ApplyAsync(context, CancellationToken.None);
        controller.State = new VisualEffectsState(true, false, true);

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));
        Assert.Equal(new VisualEffectsState(true, false, true), controller.State);
    }

    [Fact]
    public void VisualEffectsBooleanParameter_UsesWin32BooleanValueInsteadOfAddress()
    {
        Assert.Equal(IntPtr.Zero, WindowsVisualEffectsController.BooleanParameter(false));
        Assert.Equal(new IntPtr(1), WindowsVisualEffectsController.BooleanParameter(true));
    }

    [Fact]
    public async Task VisualEffectsApply_PostconditionFailureRestoresPreviousSettings()
    {
        var controller = new FakeVisualEffectsController { IgnoreNextStateSet = true };
        var action = new VisualEffectsAction(controller);

        await Assert.ThrowsAsync<IOException>(() =>
            action.ApplyAsync(Context(), CancellationToken.None));

        Assert.Equal(new VisualEffectsState(true, true, true), controller.State);
    }

    [Fact]
    public async Task VisualEffectsApply_ReportsWhenFailedApplyCannotBeRestored()
    {
        var controller = new FakeVisualEffectsController();
        var partialState = new VisualEffectsState(true, false, false);
        controller.StateSetResults.Enqueue(partialState);
        controller.StateSetResults.Enqueue(null);
        var action = new VisualEffectsAction(controller);

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            action.ApplyAsync(Context(), CancellationToken.None));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Equal(partialState, controller.State);
    }

    [Fact]
    public async Task MenuShowDelayApply_VerifiesAndRollsBack()
    {
        var controller = new FakeVisualEffectsController();
        var action = new MenuShowDelayAction(controller, Text);
        var context = Context();

        var result = await action.ApplyAsync(context, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(100, controller.MenuShowDelay);
        Assert.Equal("ActionResults.MenuShowDelay.Applied", Assert.Single(result.Messages));

        await action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None);

        Assert.Equal(400, controller.MenuShowDelay);
    }

    [Fact]
    public async Task MenuShowDelayApply_DoesNotIncreaseFasterExistingValue()
    {
        var controller = new FakeVisualEffectsController { MenuShowDelay = 75 };
        var action = new MenuShowDelayAction(controller, Text);

        var result = await action.ApplyAsync(Context(), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(75, controller.MenuShowDelay);
        Assert.Equal("ActionResults.MenuShowDelay.AlreadyOptimized", Assert.Single(result.Messages));
    }

    [Fact]
    public async Task MenuShowDelayRollback_PreservesNewerUserChoice()
    {
        var controller = new FakeVisualEffectsController();
        var action = new MenuShowDelayAction(controller, Text);
        var context = Context();
        var result = await action.ApplyAsync(context, CancellationToken.None);
        controller.MenuShowDelay = 75;

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));
        Assert.Equal(75, controller.MenuShowDelay);
    }

    [Fact]
    public async Task MenuShowDelayApply_PostconditionFailureRestoresPreviousValue()
    {
        var controller = new FakeVisualEffectsController { IgnoreNextMenuShowDelaySet = true };
        var action = new MenuShowDelayAction(controller, Text);

        await Assert.ThrowsAsync<IOException>(() =>
            action.ApplyAsync(Context(), CancellationToken.None));

        Assert.Equal(400, controller.MenuShowDelay);
    }

    [Fact]
    public async Task MenuShowDelayApply_ReportsWhenFailedApplyCannotBeRestored()
    {
        var controller = new FakeVisualEffectsController();
        controller.MenuShowDelaySetResults.Enqueue(101);
        controller.MenuShowDelaySetResults.Enqueue(null);
        var action = new MenuShowDelayAction(controller, Text);

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            action.ApplyAsync(Context(), CancellationToken.None));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Equal(101, controller.MenuShowDelay);
    }

    [Fact]
    public async Task MenuShowDelayRollback_RejectsSnapshotOutsideAllowlist()
    {
        var action = new MenuShowDelayAction(new FakeVisualEffectsController(), Text);
        var snapshot = WindowsActionSnapshot.Serialize(new MenuShowDelaySnapshot(400, 99));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            action.RollbackAsync(Context(), snapshot, CancellationToken.None));
    }

    [Fact]
    public async Task MenuShowDelayRollback_VerifiesRestoredValue()
    {
        var controller = new FakeVisualEffectsController();
        var action = new MenuShowDelayAction(controller, Text);
        var context = Context();
        var result = await action.ApplyAsync(context, CancellationToken.None);
        controller.IgnoreNextMenuShowDelaySet = true;

        await Assert.ThrowsAsync<IOException>(() =>
            action.RollbackAsync(context, result.SnapshotJson, CancellationToken.None));
        Assert.Equal(100, controller.MenuShowDelay);
    }

    private static WindowsActionContext Context(bool elevated = false)
    {
        return new WindowsActionContext
        {
            TransactionId = Guid.NewGuid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            IsElevated = elevated
        };
    }

    private static string Text(string key, params object?[] _) => key;
}
