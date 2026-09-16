using System.Security.Cryptography;
using Xunit;

namespace Ralven.Tests.App;

public sealed class PublicExposureHardeningTests
{
    [Fact]
    public void StableRelease_RequiresExactOriginMainBeforeSecretBearingSteps()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        var provenanceGuard = workflow.IndexOf(
            "Stable release tag must point to the current origin/main commit.",
            StringComparison.Ordinal);
        var firstSecretReference = workflow.IndexOf("${{ secrets.", StringComparison.Ordinal);

        Assert.True(provenanceGuard >= 0, "Stable releases must validate the trusted origin/main commit.");
        Assert.True(
            firstSecretReference > provenanceGuard,
            "The provenance guard must run before any workflow secret is referenced.");
        Assert.Contains("refs/remotes/origin/main", workflow, StringComparison.Ordinal);
        Assert.Contains("trusted_commit: ${{ steps.release.outputs.trusted_commit }}", workflow, StringComparison.Ordinal);
        Assert.Equal(2, workflow.Split("ref: ${{ needs.build_audit.outputs.trusted_commit }}", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ReleaseSigning_IsIsolatedFromBuildAndProductionPublishing()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var buildEnd = workflow.IndexOf("  sign_release:", StringComparison.Ordinal);
        var publishStart = workflow.IndexOf("  publish:", buildEnd, StringComparison.Ordinal);

        Assert.True(buildEnd > 0, "The clean audit and protected signing jobs must remain separate.");
        Assert.True(publishStart > buildEnd, "The signing job must complete before publishing.");
        Assert.DoesNotContain("SIGNING_PRIVATE_KEY", workflow[..buildEnd], StringComparison.Ordinal);
        Assert.Contains("environment: release-signing", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: production", workflow, StringComparison.Ordinal);
        Assert.Contains("BROKER_INTEGRITY_SIGNING_PRIVATE_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("RELEASE_SIGNING_PRIVATE_KEY", workflow[buildEnd..publishStart], StringComparison.Ordinal);
    }

    [Fact]
    public void StableRelease_PreparesAndProvesDiagnosticsBeforePublication()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        var prepare = workflow.IndexOf("- name: Prepare production diagnostics backend", StringComparison.Ordinal);
        var migrate = workflow.IndexOf("wrangler d1 migrations apply", prepare, StringComparison.Ordinal);
        var deploy = workflow.IndexOf("wrangler deploy", migrate, StringComparison.Ordinal);
        var smoke = workflow.IndexOf("Test-ProductionDiagnostics.ps1", deploy, StringComparison.Ordinal);
        var publish = workflow.IndexOf("- name: Create public release", smoke, StringComparison.Ordinal);
        var feed = workflow.IndexOf("- name: Publish signed stable feed", publish, StringComparison.Ordinal);

        Assert.True(prepare >= 0);
        Assert.True(migrate > prepare);
        Assert.True(deploy > migrate);
        Assert.True(smoke > deploy);
        Assert.True(publish > smoke);
        Assert.True(feed > publish);
    }

    [Fact]
    public void StableRelease_RequiresAccountSecretsBeforeWorkerDeployment()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        var secretPreflight = workflow.IndexOf("wrangler secret list --format json", StringComparison.Ordinal);
        var deploy = workflow.IndexOf("wrangler deploy", StringComparison.Ordinal);

        Assert.True(secretPreflight >= 0, "Production releases must inspect Worker secrets.");
        Assert.True(deploy > secretPreflight, "Account secrets must be checked before Worker deployment.");
        Assert.Contains("FIREBASE_WEB_API_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("FIREBASE_ADMIN_CLIENT_EMAIL", workflow, StringComparison.Ordinal);
        Assert.Contains("FIREBASE_ADMIN_PRIVATE_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("MFA_RECOVERY_CODE_HMAC_SECRET", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSmoke_ExercisesCompleteSyntheticAccountLifecycle()
    {
        var root = FindRepositoryRoot();
        var smoke = File.ReadAllText(Path.Combine(root, "scripts", "Test-ProductionDiagnostics.ps1"));

        Assert.Contains("accounts:signUp", smoke, StringComparison.Ordinal);
        Assert.Contains("accounts:signInWithPassword", smoke, StringComparison.Ordinal);
        Assert.Contains("securetoken.googleapis.com/v1/token", smoke, StringComparison.Ordinal);
        Assert.Contains("accountProfileEndpoint", smoke, StringComparison.Ordinal);
        Assert.Contains("Authorization = \"Bearer $firebaseIdToken\"", smoke, StringComparison.Ordinal);
        Assert.Contains("email-verification-required", smoke, StringComparison.Ordinal);
        Assert.Contains("accountDeleteEndpoint", smoke, StringComparison.Ordinal);
        Assert.Contains("Deleted account token remained usable", smoke, StringComparison.Ordinal);
        Assert.Contains("accounts:delete", smoke, StringComparison.Ordinal);
        Assert.Contains("finally", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionTotpActivation_RequiresTheGuardedAccountUi()
    {
        var root = FindRepositoryRoot();
        var config = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Config", "appsettings.Production.json"));
        var accountUi = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "MainWindow.Account.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "MainWindow.xaml"));

        Assert.Contains("\"firebaseTotpEnabled\": true", config, StringComparison.Ordinal);
        Assert.Contains("remoteServicesOptions.FirebaseTotpEnabled", accountUi, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AccountSettingsMfaPanel\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void StableRelease_HardensAndSmokeTestsTheRuntimeBeforePublication()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        var hardenedBuild = workflow.IndexOf("Build-Installer.ps1 -Version $env:ASSET_VERSION -Harden", StringComparison.Ordinal);
        var finalizeBroker = workflow.IndexOf("Finalize-BrokerIntegrity.ps1", hardenedBuild, StringComparison.Ordinal);
        var structuralScan = workflow.IndexOf("Test-NoUnobfuscatedAssemblies.ps1", finalizeBroker, StringComparison.Ordinal);
        var runtimeSmoke = workflow.IndexOf("Test-HardenedRuntime.ps1", structuralScan, StringComparison.Ordinal);
        var installerTest = workflow.IndexOf("Test-Installer.ps1", runtimeSmoke, StringComparison.Ordinal);
        var publish = workflow.IndexOf("- name: Create public release", installerTest, StringComparison.Ordinal);

        Assert.True(hardenedBuild >= 0);
        Assert.True(finalizeBroker > hardenedBuild);
        Assert.True(structuralScan > finalizeBroker);
        Assert.True(runtimeSmoke > finalizeBroker);
        Assert.True(installerTest > runtimeSmoke);
        Assert.True(publish > installerTest);
        Assert.Contains("OBFUSCATION_MAP_ENCRYPTION_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("name: Ralven-protected-maps-", workflow, StringComparison.Ordinal);
        Assert.Contains("private/obfuscation-maps/$env:RELEASE_TAG", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("path: artifacts/obfuscation-maps/**", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Obfuscation_PreservesDurableRollbackSnapshotsAndRevalidatesFinalPackages()
    {
        var root = FindRepositoryRoot();
        var configuration = File.ReadAllText(Path.Combine(root, "build", "obfuscation", "Ralven.Obfuscar.xml"));
        var invocation = File.ReadAllText(Path.Combine(root, "scripts", "Invoke-Obfuscation.ps1"));
        var finalizer = File.ReadAllText(Path.Combine(root, "scripts", "Finalize-BrokerIntegrity.ps1"));
        var smoke = File.ReadAllText(Path.Combine(root, "scripts", "Test-HardenedRuntime.ps1"));
        string[] durableTypes =
        [
            "QuarantinedFileSnapshot",
            "CleanupScopeSnapshot",
            "CleanupActionSnapshot",
            "TerminatedProcessSnapshot",
            "QuarantinedAuthEntry",
            "QuarantinedAuthItem",
            "AuthDataRepairSnapshot",
            "CommandLineSnapshot",
            "SafeXmlSettingsSnapshot",
            "PointerAccelerationSnapshot",
            "PowerPlanSnapshot",
            "PciExpressAspmSnapshot",
            "RegistryMutationSnapshotEntry",
            "RegistryMutationSnapshot",
            "VisualEffectsSnapshot",
            "MenuShowDelaySnapshot",
        ];

        foreach (var type in durableTypes)
        {
            Assert.Contains($"<SkipType name=\"Ralven.Windows.Actions.{type}\"", configuration, StringComparison.Ordinal);
            Assert.Contains($"'Ralven.Windows.Actions.{type}'", invocation, StringComparison.Ordinal);
        }
        Assert.Contains("type rule in configuration", invocation, StringComparison.Ordinal);
        Assert.Contains("-SkipPortableBuild", finalizer, StringComparison.Ordinal);
        Assert.Contains("-Harden", finalizer, StringComparison.Ordinal);
        Assert.Contains("'RalvenAi'", smoke, StringComparison.Ordinal);
        Assert.Contains("'Settings'", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void StableRelease_PublishesVersionedArtifactsBeforeSignedVemryxFeeds()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        var versioned = workflow.IndexOf("- name: Publish versioned release artifacts to Vemryx", StringComparison.Ordinal);
        var createRelease = workflow.IndexOf("- name: Create public release", versioned, StringComparison.Ordinal);
        var stableFeed = workflow.IndexOf("- name: Publish signed stable feed to Vemryx", createRelease, StringComparison.Ordinal);
        var runtimeFeed = workflow.IndexOf("https://vemryx.com/Ralven/releases/runtime-manifest.json", stableFeed, StringComparison.Ordinal);
        var installerFeed = workflow.IndexOf("https://vemryx.com/Ralven/releases/installer-manifest.json", stableFeed, StringComparison.Ordinal);
        var verifyExactManifest = workflow.IndexOf("$published -ne $expected", installerFeed, StringComparison.Ordinal);

        Assert.True(versioned >= 0);
        Assert.True(createRelease > versioned);
        Assert.True(stableFeed > createRelease);
        Assert.True(runtimeFeed > stableFeed);
        Assert.True(installerFeed > stableFeed);
        Assert.True(verifyExactManifest > installerFeed);
        Assert.Contains("ralven-releases/releases/$env:RELEASE_TAG", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("release-assets.githubusercontent.com", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeTrustAnchors_AreSeparatedByPurpose()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Ralven.App.csproj"));
        var updater = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Services", "SignedManifestUpdateService.cs"));
        var broker = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Services", "BrokerIntegrityVerifier.cs"));

        Assert.Contains("Assets/update-manifest-public-key.pem", project, StringComparison.Ordinal);
        Assert.Contains("Assets/broker-integrity-public-key.pem", project, StringComparison.Ordinal);
        Assert.Contains("Assets.update-manifest-public-key.pem", updater, StringComparison.Ordinal);
        Assert.Contains("Assets.broker-integrity-public-key.pem", broker, StringComparison.Ordinal);

        var updateKey = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Assets", "update-manifest-public-key.pem"));
        var brokerKey = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Assets", "broker-integrity-public-key.pem"));
        Assert.NotEqual(updateKey, brokerKey);

        using var brokerVerifier = ECDsa.Create();
        brokerVerifier.ImportFromPem(brokerKey);
        Assert.Equal(32, brokerVerifier.ExportParameters(includePrivateParameters: false).Q.X!.Length);
    }

    [Fact]
    public void PublicDistribution_UsesOnlyTheVemryxSiteAndSignedFeeds()
    {
        var root = FindRepositoryRoot();
        var updater = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "Services", "SignedManifestUpdateService.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "MainWindow.xaml.cs"));

        Assert.Contains("https://vemryx.com/Ralven/", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("api.github.com", updater, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GitHubReleaseUpdateService", mainWindow, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, ".github", "workflows", "pages.yml")));
    }

    [Fact]
    public void FatalStartupPresentationFailure_InvalidatesHealthBeforeShowingError()
    {
        var root = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "App.xaml.cs"));
        var fatalHandler = appSource[appSource.IndexOf("private static void ShowFatalError", StringComparison.Ordinal)..];
        var invalidate = fatalHandler.IndexOf("InvalidateStartupHealthIfPending()", StringComparison.Ordinal);
        Assert.True(invalidate >= 0 && invalidate < fatalHandler.IndexOf("ErrorDialog.ShowFatal", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_DoesNotWriteRawExceptionsToDeveloperSpecificPath()
    {
        var root = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(root, "src", "Ralven.App", "App.xaml.cs"));

        Assert.DoesNotContain(@"C:\Projetos\ralven-debug.log", appSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ralven.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
