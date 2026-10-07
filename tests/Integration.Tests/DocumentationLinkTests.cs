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

        foreach (var (markdownFile, markdown) in markdownFiles
            .Select(path => (Path: path, Markdown: File.ReadAllText(path))))
        {
            foreach (var linkTarget in MarkdownLinkPattern.Matches(markdown)
                .Select(match => NormalizeLinkTarget(match.Groups[1].Value)))
            {
                if (string.IsNullOrWhiteSpace(linkTarget) ||
                    linkTarget.StartsWith('#') ||
                    Uri.TryCreate(linkTarget, UriKind.Absolute, out _))
                {
                    continue;
                }

                var localPath = Uri.UnescapeDataString(linkTarget.Split(['#', '?'], 2)[0]);
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    continue;
                }

                var resolvedPath = Path.GetFullPath(Path.Join(
                    Path.GetDirectoryName(markdownFile)!,
                    localPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                {
                    missingLinks.Add($"{Path.GetRelativePath(repositoryRoot, markdownFile)}: {linkTarget}");
                }
            }
        }

        Assert.True(missingLinks.Count == 0, $"Missing local Markdown links:{Environment.NewLine}{string.Join(Environment.NewLine, missingLinks)}");
    }

    [Theory]
    [InlineData(" docs/README.md ", "docs/README.md")]
    [InlineData("docs/README.md \"Title\"", "docs/README.md")]
    [InlineData("<docs/README.md>", "docs/README.md")]
    [InlineData("docs/README.md\t\"Title\"", "docs/README.md")]
    [InlineData("#section", "#section")]
    public void LinkTargetNormalizationPreservesPathsAndRemovesTitles(string input, string expected) =>
        Assert.Equal(expected, NormalizeLinkTarget(input));

    private static string NormalizeLinkTarget(string value)
    {
        var target = value.Trim();
        var separator = target.IndexOfAny([' ', '\t', '\r', '\n']);
        return (separator >= 0 ? target[..separator] : target).Trim('<', '>');
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "solution.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
