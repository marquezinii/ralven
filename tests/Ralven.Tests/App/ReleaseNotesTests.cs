using System.Globalization;
using System.Text.Json;
using Ralven.App.Services;
using Ralven.App.Views;
using Ralven.Contracts;
using Xunit;

namespace Ralven.Tests.App;

public sealed class ReleaseNotesCatalogTests
{
    [Fact]
    public void Versions_ContainsTheCurrentRelease()
    {
        var entry = ReleaseNotesCatalog.Versions[0];

        Assert.Equal("1.7.1", entry.Version);
        Assert.Equal([ReleaseNoteCategory.Fixed], entry.Categories);
    }

    [Fact]
    public void Find_UnknownVersion_ReturnsNull()
    {
        Assert.Null(ReleaseNotesCatalog.Find("999.0.0"));
    }

    [Fact]
    public void Find_KnownVersion_ReturnsTheMatchingEntry()
    {
        var entry = ReleaseNotesCatalog.Find("1.7.1");

        Assert.NotNull(entry);
        Assert.Equal("1.7.1", entry!.Version);
    }

    [Fact]
    public void Versions_HaveLocalizedSummaryAndCategoryText()
    {
        foreach (var culture in new[] { "en-US", "pt-BR", "es-ES", "fr-FR" })
        {
            var localization = new LocalizationService(CultureInfo.GetCultureInfo(culture));
            Assert.NotEqual("ReleaseNotes.Summary", localization.GetString("ReleaseNotes.Summary"));

            foreach (var entry in ReleaseNotesCatalog.Versions)
            {
                foreach (var category in entry.Categories)
                {
                    var key = $"ReleaseNotes.{entry.Version.Replace('.', '_')}.{category}";
                    Assert.NotEqual(key, localization.GetString(key));
                }
            }
        }
    }
}

public sealed class ReleaseNotesEvaluatorTests
{
    private static readonly IReadOnlyList<ReleaseNoteVersion> CatalogWithOneEight =
    [
        new ReleaseNoteVersion("1.8.0", new DateOnly(2026, 9, 1), [ReleaseNoteCategory.Added, ReleaseNoteCategory.Fixed])
    ];

    [Fact]
    public void Evaluate_NewInstallationWithoutSettingsFile_NeverShowsButRecordsBaselineSilently()
    {
        var settings = new AppSettings();

        var decision = ReleaseNotesEvaluator.Evaluate(
            settings,
            settingsFileExistedBeforeLoad: false,
            currentVersion: "1.8.0",
            catalog: CatalogWithOneEight);

        Assert.False(decision.ShouldShow);
        Assert.True(decision.ShouldRecordSilently);
        Assert.Null(decision.Entry);
    }

    [Fact]
    public void Evaluate_ExistingInstallationUpgradingIntoFirstVersionWithThisFeature_ShowsCurrentVersionNotes()
    {
        // LastSeenReleaseNotesVersion is null both for a brand-new install
        // and for an existing install predating this field — the file
        // already existing on disk is what tells them apart.
        var settings = new AppSettings { LastSeenReleaseNotesVersion = null };

        var decision = ReleaseNotesEvaluator.Evaluate(
            settings,
            settingsFileExistedBeforeLoad: true,
            currentVersion: "1.8.0",
            catalog: CatalogWithOneEight);

        Assert.True(decision.ShouldShow);
        Assert.False(decision.ShouldRecordSilently);
        Assert.NotNull(decision.Entry);
        Assert.Equal("1.8.0", decision.Entry!.Version);
    }

    [Fact]
    public void Evaluate_VersionCurrentlyRunningNeverSeenBefore_ShowsThatVersionsNotes()
    {
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "1.7.2" };

        var decision = ReleaseNotesEvaluator.Evaluate(
            settings,
            settingsFileExistedBeforeLoad: true,
            currentVersion: "1.8.0",
            catalog: CatalogWithOneEight);

        Assert.True(decision.ShouldShow);
        Assert.Equal("1.8.0", decision.Entry!.Version);
    }

    [Fact]
    public void Evaluate_CurrentVersionAlreadySeen_DoesNotShowAgain()
    {
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "1.8.0" };

        var decision = ReleaseNotesEvaluator.Evaluate(
            settings,
            settingsFileExistedBeforeLoad: true,
            currentVersion: "1.8.0",
            catalog: CatalogWithOneEight);

        Assert.False(decision.ShouldShow);
        Assert.False(decision.ShouldRecordSilently);
        Assert.Null(decision.Entry);
    }

    [Fact]
    public void Evaluate_MultipleLaunchesOnTheSameVersion_KeepsNotShowingEveryTime()
    {
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "1.8.0" };

        var first = ReleaseNotesEvaluator.Evaluate(settings, true, "1.8.0", CatalogWithOneEight);
        var second = ReleaseNotesEvaluator.Evaluate(settings, true, "1.8.0", CatalogWithOneEight);

        Assert.False(first.ShouldShow);
        Assert.False(second.ShouldShow);
    }

    [Fact]
    public void Evaluate_RunningAnOlderVersionThanLastSeen_NeverShowsNotesGoingBackwards()
    {
        // Downgrade/rollback scenario: the notes for 1.8.0 were already
        // shown; running 1.7.2 afterwards must not show them again.
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "1.8.0" };
        var catalog = new[]
        {
            new ReleaseNoteVersion("1.7.2", null, [ReleaseNoteCategory.Fixed])
        };

        var decision = ReleaseNotesEvaluator.Evaluate(settings, true, "1.7.2", catalog);

        Assert.False(decision.ShouldShow);
        Assert.False(decision.ShouldRecordSilently);
    }

    [Fact]
    public void Evaluate_UpgradeToAVersionWithNoCatalogEntry_ShowsNothingButAdvancesTheBaseline()
    {
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "1.7.2" };

        var decision = ReleaseNotesEvaluator.Evaluate(
            settings,
            settingsFileExistedBeforeLoad: true,
            currentVersion: "1.8.0",
            catalog: []);

        Assert.False(decision.ShouldShow);
        Assert.True(decision.ShouldRecordSilently);
        Assert.Null(decision.Entry);
    }

    [Fact]
    public void Evaluate_MalformedCurrentVersion_NeverShowsAndNeverRecords()
    {
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "1.7.2" };

        var decision = ReleaseNotesEvaluator.Evaluate(
            settings,
            settingsFileExistedBeforeLoad: true,
            currentVersion: "not-a-version",
            catalog: CatalogWithOneEight);

        Assert.False(decision.ShouldShow);
        Assert.False(decision.ShouldRecordSilently);
    }

    [Fact]
    public void Evaluate_MalformedLastSeenVersion_IsTreatedAsNeverSeenAndStillShows()
    {
        var settings = new AppSettings { LastSeenReleaseNotesVersion = "corrupted-value" };

        var decision = ReleaseNotesEvaluator.Evaluate(
            settings,
            settingsFileExistedBeforeLoad: true,
            currentVersion: "1.8.0",
            catalog: CatalogWithOneEight);

        Assert.True(decision.ShouldShow);
    }
}

public sealed class ReleaseNotesWindowCategoryOrderingTests
{
    [Fact]
    public void OrderCategories_ReturnsOnlyListedCategories_InTheFixedDisplayOrder()
    {
        var ordered = ReleaseNotesWindow.OrderCategories(
            [ReleaseNoteCategory.Security, ReleaseNoteCategory.Added, ReleaseNoteCategory.Fixed]);

        Assert.Equal(
            [ReleaseNoteCategory.Added, ReleaseNoteCategory.Fixed, ReleaseNoteCategory.Security],
            ordered);
    }

    [Fact]
    public void OrderCategories_SingleCategory_ReturnsOnlyThatOne()
    {
        var ordered = ReleaseNotesWindow.OrderCategories([ReleaseNoteCategory.Improved]);

        Assert.Equal([ReleaseNoteCategory.Improved], ordered);
    }

    [Fact]
    public void OrderCategories_AllFiveCategories_ReturnsAllInOrder()
    {
        var ordered = ReleaseNotesWindow.OrderCategories(
            [ReleaseNoteCategory.Removed, ReleaseNoteCategory.Security, ReleaseNoteCategory.Added,
                ReleaseNoteCategory.Fixed, ReleaseNoteCategory.Improved]);

        Assert.Equal(
            [
                ReleaseNoteCategory.Added, ReleaseNoteCategory.Improved, ReleaseNoteCategory.Fixed,
                ReleaseNoteCategory.Removed, ReleaseNoteCategory.Security
            ],
            ordered);
    }

    [Fact]
    public void OrderCategories_NoCategories_ReturnsEmpty()
    {
        Assert.Empty(ReleaseNotesWindow.OrderCategories([]));
    }
}

public sealed class AppSettingsReleaseNotesSerializationTests
{
    private static readonly JsonSerializerOptions Options = RalvenJson.Options;

    [Fact]
    public void Deserialize_OldJsonWithoutTheField_DefaultsToNull()
    {
        const string json = """
        {
          "language": "automatic",
          "theme": "system",
          "shareAnonymousTelemetry": true
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options)!;

        Assert.Null(settings.LastSeenReleaseNotesVersion);
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_PreservesTheVersion()
    {
        var original = new AppSettings { LastSeenReleaseNotesVersion = "1.8.0" };

        var json = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.Equal("1.8.0", roundTripped!.LastSeenReleaseNotesVersion);
    }

    [Fact]
    public void RoundTrip_WithNullVersion_StaysNull()
    {
        var original = new AppSettings { LastSeenReleaseNotesVersion = null };

        var json = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.Null(roundTripped!.LastSeenReleaseNotesVersion);
    }
}
