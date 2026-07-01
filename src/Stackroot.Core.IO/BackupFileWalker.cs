using Microsoft.Extensions.FileSystemGlobbing;

namespace Stackroot.Core.IO;

public static class BackupFileWalker
{
    public static IEnumerable<string> EnumerateFiles(
        string root, bool skipSymbolicLinks, IReadOnlyList<string> ignorePatterns)
    {
        var matcher = BuildMatcher(ignorePatterns);
        return Walk(root, root, skipSymbolicLinks, matcher);
    }

    /// <summary>
    /// Finds entries under <paramref name="root"/> that would be excluded by <paramref name="ignorePatterns"/>
    /// (i.e. would NOT be included if this root were backed up now). Used to warn before a restore replaces
    /// the directory, since anything excluded from the backup is otherwise lost permanently.
    /// </summary>
    public static IEnumerable<string> EnumerateExcludedPaths(string root, IReadOnlyList<string> ignorePatterns)
    {
        var matcher = BuildMatcher(ignorePatterns);
        if (matcher is null || !Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in WalkExcluded(root, root, matcher))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> WalkExcluded(string root, string dir, Matcher matcher)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            var isDirectory = File.GetAttributes(entry).HasFlag(FileAttributes.Directory);

            if (!matcher.Match([relative]).HasMatches)
            {
                yield return isDirectory ? relative + "/" : relative;
                continue;
            }

            if (isDirectory)
            {
                foreach (var nested in WalkExcluded(root, entry, matcher))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<string> Walk(string root, string dir, bool skipSymbolicLinks, Matcher? matcher)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (skipSymbolicLinks && isReparsePoint)
            {
                continue;
            }

            if (matcher is not null)
            {
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (!matcher.Match([relative]).HasMatches)
                {
                    continue;
                }
            }

            if (isDirectory)
            {
                foreach (var file in Walk(root, entry, skipSymbolicLinks, matcher))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static Matcher? BuildMatcher(IReadOnlyList<string> ignorePatterns)
    {
        if (ignorePatterns.Count == 0)
        {
            return null;
        }

        var matcher = new Matcher();
        matcher.AddInclude("**/*");
        foreach (var pattern in ignorePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var normalized = pattern.Trim().TrimEnd('/');
            if (!normalized.StartsWith("/", StringComparison.Ordinal) && !normalized.StartsWith("**/", StringComparison.Ordinal))
            {
                normalized = "**/" + normalized;
            }
            normalized = normalized.TrimStart('/');

            matcher.AddExclude(normalized);
            matcher.AddExclude(normalized + "/**");
        }

        return matcher;
    }
}
