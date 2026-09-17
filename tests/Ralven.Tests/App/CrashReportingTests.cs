using Ralven.App.Services;
using Sentry;
using Xunit;

namespace Ralven.Tests.App;

public sealed class AppEnvironmentTests
{
    [Fact]
    public void Resolve_ExplicitDevelopmentVariable_ReturnsDevelopmentRegardlessOfBuildConfiguration()
    {
        var result = AppEnvironment.Resolve(_ => "Development");

        Assert.Equal(AppRuntimeEnvironment.Development, result);
    }

    [Fact]
    public void Resolve_ExplicitProductionVariable_ReturnsProductionRegardlessOfBuildConfiguration()
    {
        var result = AppEnvironment.Resolve(_ => "Production");

        Assert.Equal(AppRuntimeEnvironment.Production, result);
    }

    [Theory]
    [InlineData("development")]
    [InlineData("DEVELOPMENT")]
    [InlineData("DeVeLoPmEnT")]
    public void Resolve_VariableIsCaseInsensitive(string value)
    {
        Assert.Equal(AppRuntimeEnvironment.Development, AppEnvironment.Resolve(_ => value));
    }

    [Fact]
    public void Resolve_UnrecognizedVariableValue_FallsBackToBuildConfigurationDefault()
    {
        var result = AppEnvironment.Resolve(_ => "not-a-real-environment");

#if DEBUG
        Assert.Equal(AppRuntimeEnvironment.Development, result);
#else
        Assert.Equal(AppRuntimeEnvironment.Production, result);
#endif
    }

    [Fact]
    public void Resolve_NoVariableSet_FallsBackToBuildConfigurationDefault()
    {
        var result = AppEnvironment.Resolve(_ => null);

#if DEBUG
        Assert.Equal(AppRuntimeEnvironment.Development, result);
#else
        Assert.Equal(AppRuntimeEnvironment.Production, result);
#endif
    }
}

public sealed class RemoteServicesOptionsLoaderTests : IDisposable
{
    private readonly string tempDirectory;

    public RemoteServicesOptionsLoaderTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "RalvenTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDirectory, "Config"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp directory does not fail the test.
        }
    }

    private void WriteConfigFile(string fileName, string json) =>
        File.WriteAllText(Path.Combine(tempDirectory, "Config", fileName), json);

    [Fact]
    public void Load_DevelopmentFileExists_ReturnsItsDsnAndEnvironment()
    {
        WriteConfigFile(
            "appsettings.Development.json",
            """{ "environment": "Development", "sentryDsn": "https://key@example.ingest.sentry.io/1" }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Development, tempDirectory);

        Assert.Equal("Development", options.Environment);
        Assert.Equal("https://key@example.ingest.sentry.io/1", options.SentryDsn);
    }

    [Fact]
    public void Load_ProductionFileExists_ReturnsItsDsnAndEnvironment()
    {
        WriteConfigFile(
            "appsettings.Production.json",
            """{ "environment": "Production", "sentryDsn": "https://key@example.ingest.sentry.io/2" }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Production, tempDirectory);

        Assert.Equal("Production", options.Environment);
        Assert.Equal("https://key@example.ingest.sentry.io/2", options.SentryDsn);
    }

    [Fact]
    public void Load_NoEnvironmentSpecificFile_FallsBackToBaseAppSettings()
    {
        WriteConfigFile("appsettings.json", """{ "environment": "Production", "sentryDsn": null }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Development, tempDirectory);

        Assert.Equal("Development", options.Environment);
        Assert.Null(options.SentryDsn);
    }

    [Fact]
    public void Load_NoConfigFilesAtAll_ReturnsASafeFallbackWithNoDsn()
    {
        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Production, tempDirectory);

        Assert.Null(options.SentryDsn);
        Assert.Equal("Production", options.Environment);
    }

    [Fact]
    public void Load_MalformedJson_NeverThrowsAndFallsBackSafely()
    {
        WriteConfigFile("appsettings.Development.json", "{ not valid json");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Development, tempDirectory);

        Assert.Null(options.SentryDsn);
    }

    [Fact]
    public void Load_UsesTheRuntimeEnvironmentInsteadOfTheConfigValue()
    {
        WriteConfigFile(
            "appsettings.Production.json",
            """{ "environment": "Development", "telemetryEndpoint": "https://api.vemryx.com/telemetry" }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Production, tempDirectory);

        Assert.Equal("Production", options.Environment);
    }

    [Fact]
    public void Load_LocalOverlayExists_FillsInValuesTheTrackedFileLeavesNull()
    {
        WriteConfigFile(
            "appsettings.Development.json",
            """{ "environment": "Development", "firebaseApiKey": "tracked-key", "googleOAuthClientId": null, "googleOAuthClientSecret": null }""");
        WriteConfigFile(
            "appsettings.Development.local.json",
            """{ "googleOAuthClientId": "local-client-id", "googleOAuthClientSecret": "local-secret" }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Development, tempDirectory);

        // The tracked, committed value survives...
        Assert.Equal("tracked-key", options.FirebaseApiKey);
        // ...and the overlay-only value, absent from the tracked file, comes through.
        Assert.Equal("local-client-id", options.GoogleOAuthClientId);
        Assert.Equal("local-secret", options.GoogleOAuthClientSecret);
    }

    [Fact]
    public void Load_TrackedTotpCapability_IsDisabledUnlessExplicitlyEnabled()
    {
        WriteConfigFile(
            "appsettings.Production.json",
            """{ "environment": "Production", "firebaseTotpEnabled": true }""");

        var enabled = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Production, tempDirectory);
        File.Delete(Path.Combine(tempDirectory, "Config", "appsettings.Production.json"));
        var fallback = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Production, tempDirectory);

        Assert.True(enabled.FirebaseTotpEnabled);
        Assert.False(fallback.FirebaseTotpEnabled);
    }

    [Fact]
    public void Load_LocalOverlayHasANullField_DoesNotClearTheTrackedValue()
    {
        WriteConfigFile(
            "appsettings.Development.json",
            """{ "environment": "Development", "firebaseApiKey": "tracked-key" }""");
        WriteConfigFile("appsettings.Development.local.json", """{ "googleOAuthClientId": "local-client-id" }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Development, tempDirectory);

        Assert.Equal("tracked-key", options.FirebaseApiKey);
        Assert.Equal("local-client-id", options.GoogleOAuthClientId);
    }

    [Fact]
    public void Load_NoLocalOverlayFile_ReturnsTheTrackedConfigUnchanged()
    {
        WriteConfigFile(
            "appsettings.Development.json",
            """{ "environment": "Development", "firebaseApiKey": "tracked-key" }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Development, tempDirectory);

        Assert.Equal("tracked-key", options.FirebaseApiKey);
        Assert.Null(options.GoogleOAuthClientId);
    }

    [Fact]
    public void Load_MalformedLocalOverlay_FallsBackToTheTrackedConfigInsteadOfThrowing()
    {
        WriteConfigFile(
            "appsettings.Development.json",
            """{ "environment": "Development", "firebaseApiKey": "tracked-key" }""");
        WriteConfigFile("appsettings.Development.local.json", "{ not valid json");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Development, tempDirectory);

        Assert.Equal("tracked-key", options.FirebaseApiKey);
    }

    /// <summary>
    /// The overlay applies by environment name: a Development local file must
    /// never leak into a Production load, which is what would happen if the
    /// overlay path were computed without the environment segment.
    /// </summary>
    [Fact]
    public void Load_LocalOverlayIsScopedToItsOwnEnvironment()
    {
        WriteConfigFile("appsettings.Production.json", """{ "environment": "Production" }""");
        WriteConfigFile("appsettings.Development.local.json", """{ "googleOAuthClientId": "dev-only" }""");

        var options = RemoteServicesOptionsLoader.Load(AppRuntimeEnvironment.Production, tempDirectory);

        Assert.Null(options.GoogleOAuthClientId);
    }
}

public sealed class TelemetryEndpointPolicyTests
{
    [Fact]
    public void TryCreate_ProductionWorkerEndpoint_IsAccepted()
    {
        var accepted = TelemetryEndpointPolicy.TryCreate(
            "https://api.vemryx.com/telemetry",
            AppRuntimeEnvironment.Production,
            out var endpoint,
            out var error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal("https://api.vemryx.com/telemetry", endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://api.vemryx.com/telemetry")]
    [InlineData("https://telemetry.example.test/telemetry")]
    [InlineData("https://api.vemryx.com/other")]
    [InlineData("https://api.vemryx.com/telemetry?redirect=1")]
    public void TryCreate_UnsafeProductionEndpoint_IsRejected(string value)
    {
        var accepted = TelemetryEndpointPolicy.TryCreate(
            value,
            AppRuntimeEnvironment.Production,
            out _,
            out var error);

        Assert.False(accepted);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public sealed class FirebaseAuthConfigurationTests
{
    [Fact]
    public void AccountProfileEndpoint_ProductionAcceptsOnlyTheOfficialRoute()
    {
        Assert.True(AccountProfileEndpointPolicy.TryCreate(
            "https://api.vemryx.com/account/profile",
            AppRuntimeEnvironment.Production,
            out var endpoint));
        Assert.Equal("https://api.vemryx.com/account/profile", endpoint.AbsoluteUri);

        Assert.False(AccountProfileEndpointPolicy.TryCreate(
            "https://attacker.example/account/profile",
            AppRuntimeEnvironment.Production,
            out _));
        Assert.False(AccountProfileEndpointPolicy.TryCreate(
            "https://api.vemryx.com/account/profile?redirect=1",
            AppRuntimeEnvironment.Production,
            out _));
    }

    [Fact]
    public void AccountProfileEndpoint_DevelopmentAllowsAnotherHttpsOrigin()
    {
        Assert.True(AccountProfileEndpointPolicy.TryCreate(
            "https://localhost.example/account/profile",
            AppRuntimeEnvironment.Development,
            out _));
    }

    [Fact]
    public void TryGetApiKey_AcceptsOnlyPublicApiKeySyntax()
    {
        var accepted = FirebaseAuthConfiguration.TryGetApiKey(
            "test-firebase-api-key-1234567890", out var apiKey);

        Assert.True(accepted);
        Assert.Equal("test-firebase-api-key-1234567890", apiKey);
        Assert.False(FirebaseAuthConfiguration.TryGetApiKey("https://example.test", out _));
    }
}

public sealed class CrashReportingHolderTests
{
    [Fact]
    public void SentryConfiguration_IsPrivateAndFlushesOnShutdown()
    {
        var sentryOptions = new SentryOptions();

        SentryCrashReportingService.Configure(
            sentryOptions,
            new RemoteServicesOptions
            {
                SentryDsn = "https://key@example.ingest.sentry.io/1",
                Environment = "Production",
            },
            "2.0.0");

        Assert.Equal("Production", sentryOptions.Environment);
        Assert.Equal("ralven@2.0.0", sentryOptions.Release);
        Assert.Equal(TimeSpan.FromSeconds(5), sentryOptions.ShutdownTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), sentryOptions.FlushTimeout);
        Assert.False(sentryOptions.SendDefaultPii);
        Assert.False(sentryOptions.IsEnvironmentUser);
        Assert.False(sentryOptions.AutoSessionTracking);
        Assert.False(sentryOptions.CaptureFailedRequests);
        Assert.Equal(0.0, sentryOptions.TracesSampleRate);
    }

    [Fact]
    public void NoOpCrashReportingService_NeverInitializesAndNeverThrows()
    {
        var service = NoOpCrashReportingService.Instance;

        service.Initialize(new RemoteServicesOptions { SentryDsn = "https://x@y.ingest.sentry.io/1", Environment = "Production" }, "1.0.0");
        service.CaptureException(new InvalidOperationException("test"));
        service.Shutdown();

        Assert.False(service.IsInitialized);
    }

    [Fact]
    public void Current_DefaultsToNoOpAndCanBeReplaced()
    {
        var original = CrashReporting.Current;
        try
        {
            Assert.Same(NoOpCrashReportingService.Instance, original);

            var fake = new FakeCrashReportingService();
            CrashReporting.Current = fake;

            Assert.Same(fake, CrashReporting.Current);
        }
        finally
        {
            CrashReporting.Current = original;
        }
    }

    [Fact]
    public void Current_SetToNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CrashReporting.Current = null!);
    }

    private sealed class FakeCrashReportingService : ICrashReportingService
    {
        public bool IsInitialized => false;

        public void Initialize(RemoteServicesOptions options, string appVersion)
        {
        }

        public void CaptureException(Exception exception)
        {
        }

        public void Shutdown()
        {
        }
    }
}

public sealed class CrashReportingLifecycleTests
{
    private static readonly RemoteServicesOptions Options = new()
    {
        SentryDsn = "https://x@y.ingest.sentry.io/1",
        Environment = "Production"
    };

    [Fact]
    public void InitializeIfAuthorized_WhenUnauthorized_DoesNotCreateTheRemoteService()
    {
        var created = false;

        var service = CrashReportingLifecycle.InitializeIfAuthorized(
            isAuthorized: false,
            Options,
            "1.0.0",
            () =>
            {
                created = true;
                return new RecordingCrashReportingService();
            });

        Assert.Same(NoOpCrashReportingService.Instance, service);
        Assert.False(created);
    }

    [Fact]
    public void InitializeIfAuthorized_WhenStartupFails_ReturnsTheInertService()
    {
        var failingService = new RecordingCrashReportingService { ThrowOnInitialize = true };

        var service = CrashReportingLifecycle.InitializeIfAuthorized(
            isAuthorized: true,
            Options,
            "1.0.0",
            () => failingService);

        Assert.Same(NoOpCrashReportingService.Instance, service);
        Assert.Equal(1, failingService.InitializeCalls);
        Assert.Equal(1, failingService.ShutdownCalls);
    }

    [Fact]
    public void InitializeIfAuthorized_WhenStartupFailsFatally_Rethrows()
    {
        Assert.Throws<OutOfMemoryException>(() =>
            CrashReportingLifecycle.InitializeIfAuthorized(
                isAuthorized: true,
                Options,
                "1.0.0",
                () => throw new OutOfMemoryException()));
    }

    [Fact]
    public void TryShutdown_WhenShutdownFails_DoesNotThrow()
    {
        var failingService = new RecordingCrashReportingService { ThrowOnShutdown = true };

        var exception = Record.Exception(() => CrashReportingLifecycle.TryShutdown(failingService));

        Assert.Null(exception);
        Assert.Equal(1, failingService.ShutdownCalls);
    }

    private sealed class RecordingCrashReportingService : ICrashReportingService
    {
        public bool ThrowOnInitialize { get; init; }

        public bool ThrowOnShutdown { get; init; }

        public int InitializeCalls { get; private set; }

        public int ShutdownCalls { get; private set; }

        public bool IsInitialized { get; private set; }

        public void Initialize(RemoteServicesOptions options, string appVersion)
        {
            InitializeCalls++;
            if (ThrowOnInitialize)
            {
                throw new InvalidOperationException("startup failure");
            }

            IsInitialized = true;
        }

        public void CaptureException(Exception exception)
        {
        }

        public void Shutdown()
        {
            ShutdownCalls++;
            if (ThrowOnShutdown)
            {
                throw new InvalidOperationException("shutdown failure");
            }

            IsInitialized = false;
        }
    }
}

public sealed class CrashReportSanitizerTests
{
    [Fact]
    public void Sanitize_OverridesServerNameToANonIdentifyingValue()
    {
        var sentryEvent = new SentryEvent { ServerName = Environment.MachineName };

        var result = CrashReportSanitizer.Sanitize(sentryEvent);

        Assert.Equal("ralven-client", result.ServerName);
    }

    [Fact]
    public void Sanitize_ClearsAnyAutoPopulatedUserData()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.User.Id = "some-id";
        sentryEvent.User.Username = Environment.UserName;
        sentryEvent.User.Email = "someone@example.com";
        sentryEvent.User.IpAddress = "203.0.113.1";

        var result = CrashReportSanitizer.Sanitize(sentryEvent);

        Assert.Null(result.User.Id);
        Assert.Null(result.User.Username);
        Assert.Null(result.User.Email);
        Assert.Null(result.User.IpAddress);
    }

    [Fact]
    public void Sanitize_ScrubsAUserProfilePathFromTheEventMessage()
    {
        var sentryEvent = new SentryEvent
        {
            Message = new SentryMessage
            {
                Message = @"Could not read C:\Users\someuser\AppData\settings.xml"
            }
        };

        var result = CrashReportSanitizer.Sanitize(sentryEvent);

        Assert.DoesNotContain("someuser", result.Message!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", result.Message!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_ScrubsPathsFromEveryThreadStackTrace()
    {
        var firstFrame = new SentryStackFrame { FileName = @"C:\Users\first\one.cs" };
        var secondFrame = new SentryStackFrame { AbsolutePath = @"C:\Users\second\two.cs" };
        var sentryEvent = new SentryEvent
        {
            SentryThreads =
            [
                new SentryThread { Stacktrace = new SentryStackTrace { Frames = [firstFrame] } },
                new SentryThread { Stacktrace = new SentryStackTrace { Frames = [secondFrame] } },
            ],
        };

        CrashReportSanitizer.Sanitize(sentryEvent);

        Assert.DoesNotContain("first", firstFrame.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("second", secondFrame.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", firstFrame.FileName, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", secondFrame.AbsolutePath, StringComparison.Ordinal);
    }
}

public sealed class CentralizedConfigurationGuardTests
{
    // Fragments of the identifiers the user supplied, kept split across
    // concatenated literals here so this guard test itself never becomes a
    // second place those values are typed out in full.
    private static readonly string SentryDsnFragment =
        "1e8d9183066a2ffcb265709" + "fbb17ec5a";

    private static readonly string D1DatabaseIdFragment =
        "fe276121-a71a-4ba4-ab62-" + "81cccdf601c6";

    [Fact]
    public void ProductionDiagnosticsConfiguration_IsReleaseReady()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var options = RemoteServicesOptionsLoader.Load(
            AppRuntimeEnvironment.Production,
            Path.Combine(root, "src", "Ralven.App"));

        Assert.Equal("Production", options.Environment);
        Assert.True(TelemetryEndpointPolicy.TryCreate(
            options.TelemetryEndpoint,
            AppRuntimeEnvironment.Production,
            out _,
            out _));
        Assert.True(Uri.TryCreate(options.SentryDsn, UriKind.Absolute, out var sentryDsn));
        Assert.Equal(Uri.UriSchemeHttps, sentryDsn.Scheme);
        Assert.EndsWith(".sentry.io", sentryDsn.Host, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(sentryDsn.UserInfo));
        Assert.True(long.TryParse(sentryDsn.Segments[^1].Trim('/'), out _));
    }

    [Fact]
    public void SentryDsn_OnlyAppearsInsideTheConfigJsonFiles_NeverInSourceCode()
    {
        var root = FindRepositoryRoot();
        var configDirectory = Path.Combine(root, "src", "Ralven.App", "Config");
        var allowedFiles = new[]
        {
            Path.Combine(configDirectory, "appsettings.Development.json"),
            Path.Combine(configDirectory, "appsettings.Production.json"),
        };

        foreach (var sourceFile in EnumerateAppSourceFiles(root))
        {
            var content = File.ReadAllText(sourceFile);
            if (content.Contains(SentryDsnFragment, StringComparison.Ordinal))
            {
                Assert.Contains(sourceFile, allowedFiles);
            }
        }
    }

    [Fact]
    public void D1DatabaseId_OnlyAppearsInsideTheWorkerWranglerConfig_NeverInDotNetSourceCode()
    {
        var root = FindRepositoryRoot();

        foreach (var sourceFile in EnumerateAppSourceFiles(root))
        {
            var content = File.ReadAllText(sourceFile);
            Assert.DoesNotContain(D1DatabaseIdFragment, content, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> EnumerateAppSourceFiles(string root)
    {
        var appDirectory = Path.Combine(root, "src", "Ralven.App");
        return Directory.EnumerateFiles(appDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
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

        throw new DirectoryNotFoundException("Ralven repository root was not found.");
    }
}
