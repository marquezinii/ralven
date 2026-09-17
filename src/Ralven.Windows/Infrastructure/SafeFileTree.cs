namespace Ralven.Windows.Infrastructure;

public sealed record SafeFileEntry(
    string FullPath,
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record SafeFileEnumerationResult(
    IReadOnlyList<SafeFileEntry> Files,
    IReadOnlyList<string> SkippedReparsePoints,
    IReadOnlyList<string> SkippedInaccessiblePaths);

public static class SafePath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string EnsureDescendant(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{candidate}' must be below '{root}'.");
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{candidate}' is outside the allowed root '{root}'.");
        }

        return normalizedCandidate;
    }

    public static string EnsureNoReparsePoints(string path)
    {
        var normalized = Normalize(path);
        string? current = normalized;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException
                    or IOException
                    or System.Security.SecurityException)
                {
                    throw new IOException(
                        $"Could not validate path '{current}' for reparse points.",
                        exception);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Path '{current}' contains a reparse point.");
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return normalized;
    }
}

public sealed class SafeFileTree
{
    public SafeFileEnumerationResult EnumerateFiles(
        string root,
        Func<SafeFileEntry, bool> predicate,
        IReadOnlySet<string>? excludedTopLevelNames = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var normalizedRoot = SafePath.Normalize(root);
        if (!Directory.Exists(normalizedRoot))
        {
            return new SafeFileEnumerationResult([], [], []);
        }

        SafePath.EnsureNoReparsePoints(normalizedRoot);
        var rootInfo = new DirectoryInfo(normalizedRoot);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Cleanup root '{normalizedRoot}' is a reparse point.");
        }

        var files = new List<SafeFileEntry>();
        var reparsePoints = new List<string>();
        var inaccessible = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or System.Security.SecurityException)
            {
                inaccessible.Add(directory.FullName);
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException
                    or IOException
                    or System.Security.SecurityException)
                {
                    inaccessible.Add(entry.FullName);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reparsePoints.Add(entry.FullName);
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    if (directory.FullName.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                        && excludedTopLevelNames?.Contains(childDirectory.Name) == true)
                    {
                        continue;
                    }

                    SafePath.EnsureDescendant(normalizedRoot, childDirectory.FullName);
                    pending.Push(childDirectory);
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    continue;
                }

                SafePath.EnsureDescendant(normalizedRoot, file.FullName);
                var safeEntry = new SafeFileEntry(
                    file.FullName,
                    Path.GetRelativePath(normalizedRoot, file.FullName),
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
                if (predicate(safeEntry))
                {
                    files.Add(safeEntry);
                }
            }
        }

        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            left.RelativePath,
            right.RelativePath));
        return new SafeFileEnumerationResult(files, reparsePoints, inaccessible);
    }

    public void DeleteEmptyDirectoriesBottomUp(string root)
    {
        var normalizedRoot = SafePath.Normalize(root);
        if (!Directory.Exists(normalizedRoot))
        {
            return;
        }

        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            directories.Add(current);
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                SafePath.EnsureDescendant(normalizedRoot, child);
                pending.Push(child);
            }
        }

        foreach (var directory in directories
            .OrderByDescending(path => path.Length)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
    }
}
