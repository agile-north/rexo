namespace Rexo.Integration.Tests;

using System.Text.RegularExpressions;

public sealed class DocumentationLinkTests
{
    private static readonly Regex MarkdownLinkPattern = new(
        """!?\[[^\]]*\]\(([^)]+)\)""",
        RegexOptions.CultureInvariant);

    [Fact]
    public void RepositoryMarkdownRelativeLinksResolveToExistingPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var markdownFiles = Directory.EnumerateFiles(repositoryRoot, "*.md", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var missingLinks = new List<string>();

        foreach (var markdownFile in markdownFiles)
        {
            var markdown = File.ReadAllText(markdownFile);
            foreach (Match match in MarkdownLinkPattern.Matches(markdown))
            {
                var target = match.Groups[1].Value.Trim();
                var separator = target.IndexOfAny([' ', '\t', '\r', '\n']);
                if (separator >= 0)
                {
                    target = target[..separator];
                }

                target = target.Trim('<', '>');
                if (string.IsNullOrWhiteSpace(target) ||
                    target.StartsWith('#') ||
                    Uri.TryCreate(target, UriKind.Absolute, out _))
                {
                    continue;
                }

                var localPath = Uri.UnescapeDataString(target.Split(['#', '?'], 2)[0]);
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    continue;
                }

                var resolvedPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(markdownFile)!,
                    localPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                {
                    missingLinks.Add($"{Path.GetRelativePath(repositoryRoot, markdownFile)}: {target}");
                }
            }
        }

        Assert.True(missingLinks.Count == 0, $"Missing local Markdown links:{Environment.NewLine}{string.Join(Environment.NewLine, missingLinks)}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "solution.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
