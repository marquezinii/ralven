namespace Ralven.App.Services;

/// <summary>
/// A category of change inside one version's release notes. Matches the
/// vocabulary already used for <c>CHANGELOG.md</c> and GitHub Release notes
/// (see <c>AI_RULES.md</c>), minus the internal-only "Alterações técnicas"
/// section, which has no place in a panel meant for end users.
/// </summary>
public enum ReleaseNoteCategory
{
    Added,
    Improved,
    Fixed,
    Removed,
    Security
}

/// <summary>
/// One version's worth of release notes. Carries only the version number,
/// an optional release date, and which categories have content for that
/// version — never the bullet text itself. The actual, localized bullet
/// text lives in <c>Strings*.resx</c> under
/// <c>ReleaseNotes.{Version with dots replaced by underscores}.{Category}</c>
/// (for example <c>ReleaseNotes.1_4_0.Added</c>), one multi-line entry per
/// category, each line already prefixed with "• " — the same convention
/// already used by <c>Privacy.Collects.Items</c>. Keeping the bullet text in
/// resx (rather than in this file) is what makes the panel translate
/// correctly into every language the rest of the app already supports.
/// </summary>
public sealed record ReleaseNoteVersion(
    string Version,
    DateOnly? ReleaseDate,
    IReadOnlyList<ReleaseNoteCategory> Categories);

/// <summary>
/// Central, dependency-free catalog of which app versions have an in-app
/// "What's New" entry. Has no knowledge of UI, disk, or settings — it only
/// answers "does this version have release notes, and which categories".
/// </summary>
/// <remarks>
/// <para>
/// This catalog starts empty on purpose: this task ships the mechanism, not
/// the content for a version that does not exist yet (this repository never
/// invents its own next version number — see <c>AI_RULES.md</c>, "Publicação
/// oficial"). When a new version is actually published, add one entry here
/// and the matching resx bullets in all three languages:
/// </para>
/// <code>
/// new ReleaseNoteVersion(
///     "1.4.0",
///     new DateOnly(2026, 9, 1),
///     [ReleaseNoteCategory.Added, ReleaseNoteCategory.Fixed])
/// </code>
/// <para>
/// List categories only when that version actually has content for them —
/// the panel shows a category exclusively when it is listed here. Entries
/// do not need to stay forever; trimming old ones is safe because
/// <see cref="ReleaseNotesEvaluator"/> only ever looks up the single
/// version currently running.
/// </para>
/// </remarks>
public static class ReleaseNotesCatalog
{
    public static readonly IReadOnlyList<ReleaseNoteVersion> Versions = [
        new ReleaseNoteVersion(
            "1.7.1",
            new DateOnly(2026, 9, 14),
            [ReleaseNoteCategory.Fixed]),
        new ReleaseNoteVersion(
            "1.7.0",
            new DateOnly(2026, 9, 11),
            [
                ReleaseNoteCategory.Added,
                ReleaseNoteCategory.Improved,
                ReleaseNoteCategory.Fixed,
                ReleaseNoteCategory.Security
            ]),
        new ReleaseNoteVersion(
            "1.6.1",
            new DateOnly(2026, 9, 1),
            [ReleaseNoteCategory.Improved]),
        new ReleaseNoteVersion(
            "1.6.0",
            new DateOnly(2026, 9, 1),
            [
                ReleaseNoteCategory.Added,
                ReleaseNoteCategory.Improved,
                ReleaseNoteCategory.Fixed,
                ReleaseNoteCategory.Security
            ])
    ];

    public static ReleaseNoteVersion? Find(string version) =>
        Versions.FirstOrDefault(entry => entry.Version == version);
}
