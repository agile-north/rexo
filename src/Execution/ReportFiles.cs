namespace Rexo.Execution;

using System.Text.RegularExpressions;

internal static class ReportFiles
{
    public static string Resolve(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("A report path must not be empty.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Join(fullRoot, path));
        var relative = Path.GetRelativePath(fullRoot, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Report paths must remain inside the repository.");
        var current = fullRoot;
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A report repository root must not be a symbolic link.");
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Report paths must not traverse symbolic links.");
        }
        return full;
    }

    public static IReadOnlyList<string> Discover(string root, string patterns, string excludes, string outputDirectory)
    {
        var matches = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var exclusions = Split(excludes).Select(GlobRegex).ToArray();
        foreach (var pattern in Split(patterns))
        {
            var normalized = pattern.Replace('\\', '/');
            var wildcard = normalized.IndexOfAny(['*', '?']);
            var prefix = wildcard < 0 ? normalized : normalized[..wildcard];
            var slash = prefix.LastIndexOf('/');
            var search = Resolve(root, slash < 0 ? "." : prefix[..slash]);
            if (!Directory.Exists(search)) continue;
            var regex = GlobRegex(Path.GetRelativePath(root,
                Resolve(root, normalized)).Replace('\\', '/'));
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = normalized[(slash + 1)..].Contains('/', StringComparison.Ordinal) ||
                    normalized.EndsWith("**", StringComparison.Ordinal),
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            foreach (var file in Directory.EnumerateFiles(search, "*", enumeration))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!regex.IsMatch(relative) || exclusions.Any(exclusion => exclusion.IsMatch(relative))) continue;
                var full = Resolve(root, file);
                if (full.StartsWith(outputDirectory + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                if (new FileInfo(full).Length > 64 * 1024 * 1024)
                    throw new InvalidOperationException("An individual report exceeds the 64 MiB input limit.");
                matches.Add(full);
                if (matches.Count > 10000) throw new InvalidOperationException("Report discovery exceeded 10,000 files.");
            }
        }
        return matches.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Split(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static Regex GlobRegex(string glob)
    {
        var escaped = Regex.Escape(glob.Replace('\\', '/'));
        escaped = escaped.Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new Regex($"\\A{escaped}\\z", RegexOptions.CultureInvariant |
            (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
    }
}
