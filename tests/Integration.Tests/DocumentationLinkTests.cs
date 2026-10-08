namespace Rexo.Integration.Tests;

using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using System.Text.RegularExpressions;

[Collection("IntegrationSequential")]
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

    [Fact]
    public void ReadmeWorkflowBadgesAndPackageDocumentationLinksResolveToRepositoryFiles()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Join(root, "README.md"));
        var badgeMatches = Regex.Matches(readme, @"https://github\.com/agile-north/rexo/actions/workflows/([^/?\s)]+)");
        Assert.NotEmpty(badgeMatches);
        foreach (Match match in badgeMatches)
        {
            Assert.True(File.Exists(Path.Join(root, ".github", "workflows", match.Groups[1].Value)), match.Value);
        }

        var packageReadme = File.ReadAllText(Path.Join(root, "src", "Cli", "PACKAGE_README.md"));
        var docMatches = Regex.Matches(packageReadme, @"https://github\.com/agile-north/rexo/blob/__REXO_DOC_TAG__/([^>\s)]+)");
        Assert.NotEmpty(docMatches);
        foreach (Match match in docMatches)
        {
            Assert.True(File.Exists(Path.Join(root, match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar))), match.Value);
        }
    }

    [Theory]
    [InlineData("0.0.0-docs-test")]
    [InlineData(null)]
    public async Task PackedReadmePreservesTemplateFormattingAndResolvesVersionedLinks(string? version)
    {
        var root = FindRepositoryRoot();
        var output = Path.Join(Path.GetTempPath(), $"rexo-docs-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new List<string>
            {
                "pack", Path.Join("src", "Cli", "Cli.csproj"), "-c", configuration,
                "--no-build", "--no-restore", "--output", output, "-v", "quiet",
            })
            {
                start.ArgumentList.Add(argument);
            }
            if (version is not null)
            {
                start.ArgumentList.Add($"-p:Version={version}");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet pack.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            var diagnostics = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, diagnostics);

            using var archive = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(output, "*.nupkg")));
            var nuspec = Assert.Single(archive.Entries, item => item.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var nuspecStream = nuspec.Open();
            var metadata = XDocument.Load(nuspecStream);
            var packageVersion = Assert.Single(metadata.Descendants(), node => node.Name.LocalName == "version").Value;
            if (version is not null)
            {
                Assert.Equal(version, packageVersion);
            }
            var entry = archive.GetEntry("PACKAGE_README.generated.md");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry.Open());
            var actual = await reader.ReadToEndAsync(timeout.Token);
            var expected = (await File.ReadAllTextAsync(Path.Join(root, "src", "Cli", "PACKAGE_README.md"), timeout.Token))
                .Replace("__REXO_DOC_TAG__", $"v{packageVersion}", StringComparison.Ordinal)
                .Replace("__REXO_SCHEMA_TAG__", "schema/v1.0", StringComparison.Ordinal);
            Assert.Equal(expected, actual);
            Assert.DoesNotContain("__REXO_", actual, StringComparison.Ordinal);

            Assert.Equal(entry.FullName, Assert.Single(metadata.Descendants(), node => node.Name.LocalName == "readme").Value);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
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
