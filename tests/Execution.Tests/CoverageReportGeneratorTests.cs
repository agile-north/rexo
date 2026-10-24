namespace Rexo.Execution.Tests;

using System.Globalization;

public sealed class CoverageReportGeneratorTests : IDisposable
{
    private const string Cobertura = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="0.5" version="1.9" timestamp="1">
          <sources><source>/src/</source></sources>
          <packages><package name="App"><classes><class name="App.Calc" filename="Calc.cs">
            <lines><line number="1" hits="1" branch="false"/></lines>
          </class></classes></package></packages>
        </coverage>
        """;

    private const string JsonSummary = """
        {"summary":{"coveredlines":3,"uncoveredlines":1,"coverablelines":4,"linecoverage":75,
        "coveredbranches":1,"totalbranches":4,"branchcoverage":25}}
        """;

    private readonly string _root;

    public CoverageReportGeneratorTests()
    {
        _root = Path.Join(Path.GetTempPath(), "rexo-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Write(string name, string content)
    {
        var path = Path.Join(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("cobertura.xml", Cobertura, "cobertura")]
    [InlineData("opencover.xml", "<?xml version=\"1.0\"?><CoverageSession><Summary/><Modules/></CoverageSession>", "opencover")]
    [InlineData("lcov.info", "TN:\nSF:src/app.js\nDA:1,1\nLF:1\nLH:1\nend_of_record\n", "lcov")]
    [InlineData("jacoco.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><!DOCTYPE report PUBLIC \"-//JACOCO//DTD Report 1.1//EN\" \"report.dtd\"><report name=\"app\"><sessioninfo id=\"x\" start=\"1\" dump=\"2\"/><package name=\"a/b\"/></report>", "jacoco")]
    [InlineData("clover.xml", "<?xml version=\"1.0\"?><coverage generated=\"1\" clover=\"4.4.1\"><project timestamp=\"1\"><metrics/></project></coverage>", "clover")]
    [InlineData("other.xml", "<?xml version=\"1.0\"?><results><modules/></results>", null)]
    [InlineData("text.txt", "hello world\n", null)]
    [InlineData("partial.info", "SF:src/app.js\nDA:1,1\n", null)]
    [InlineData("empty.xml", "  \n", null)]
    public void DetectFormatRecognisesSupportedFormats(string name, string content, string? expected) =>
        Assert.Equal(expected, CoverageReportGenerator.DetectFormat(Write(name, content)));

    [Fact]
    public void DetectFormatThrowsForMalformedXml()
    {
        var path = Write("broken.xml", "<coverage line-rate=\"1\"><packages></coverage>");
        Assert.Throws<InvalidOperationException>(() => CoverageReportGenerator.DetectFormat(path));
    }

    [Fact]
    public void DetectFormatDoesNotExpandInternalEntities()
    {
        var path = Write("entities.xml",
            "<?xml version=\"1.0\"?><!DOCTYPE coverage [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;\">]>" +
            "<coverage line-rate=\"1\">&b;</coverage>");
        Assert.Throws<InvalidOperationException>(() => CoverageReportGenerator.DetectFormat(path));
    }

    [Fact]
    public async Task GenerateAsyncRunsPinnedContainerAndReadsSummary()
    {
        var input = Write(Path.Join("tests", "coverage.cobertura.xml"), Cobertura);
        GitHubReleaseCommand? captured = null;
        var result = await CoverageReportGenerator.GenerateAsync(
            [input, Path.Join("tests", "coverage.cobertura.xml")], _root, "coverage-report", CoverageReportGenerator.DefaultImage,
            "+App*;-App.Tests", "", "-**/Generated/*", "html", CancellationToken.None,
            (command, _) =>
            {
                captured = command;
                WriteOutputs(Path.Join(_root, "coverage-report"), JsonSummary);
                return Task.FromResult(new ShellRunResult(0, "", ""));
            });

        Assert.Equal(75m, result.LineCoverage);
        Assert.Equal(25m, result.BranchCoverage);
        Assert.Equal("# Summary", result.Markdown);
        Assert.Equal(Path.Join(_root, "coverage-report"), result.OutputDirectory);
        Assert.NotNull(captured);
        Assert.Equal("docker", captured.FileName);
        Assert.Empty(captured.Environment);
        var args = captured.Arguments;
        Assert.Contains(CoverageReportGenerator.DefaultImage, args);
        Assert.Contains("--read-only", args);
        Assert.Contains($"type=bind,source={input},target=/rexo/in/0/coverage,readonly", args, StringComparer.Ordinal);
        Assert.Single(args, arg => arg.StartsWith("type=bind,source=", StringComparison.Ordinal) && arg.EndsWith(",readonly", StringComparison.Ordinal));
        Assert.Contains($"type=bind,source={Path.Join(_root, "coverage-report")},target=/rexo/out", args);
        Assert.DoesNotContain(args, arg => arg.Contains($"source={_root},", StringComparison.Ordinal));
        Assert.Contains("-reporttypes:Html;MarkdownSummaryGithub;JsonSummary", args);
        Assert.Contains("-assemblyfilters:+App*;-App.Tests", args);
        Assert.Contains("-filefilters:-**/Generated/*", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("-classfilters:", StringComparison.Ordinal));
        var script = args[args.ToList().IndexOf("-c") + 1];
        Assert.Contains(CoverageReportGenerator.ReportGeneratorPackageSha512, script, StringComparison.Ordinal);
        Assert.Contains("--version " + CoverageReportGenerator.ReportGeneratorVersion, script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsyncReportsUnavailableMetricsAsNull()
    {
        var input = Write("lcov.info", "SF:a.js\nDA:1,0\nend_of_record\n");
        var result = await Generate([input], (_, _) =>
        {
            WriteOutputs(Path.Join(_root, "out"),
                "{\"summary\":{\"coveredlines\":0,\"coverablelines\":0,\"coveredbranches\":0,\"totalbranches\":0,\"branchcoverage\":null}}");
            return Task.FromResult(new ShellRunResult(0, "", ""));
        });
        Assert.Null(result.LineCoverage);
        Assert.Null(result.BranchCoverage);
    }

    [Fact]
    public async Task GenerateAsyncComputesExactPercentages()
    {
        var input = Write("c.xml", Cobertura);
        var result = await Generate([input], (_, _) =>
        {
            WriteOutputs(Path.Join(_root, "out"),
                "{\"summary\":{\"coveredlines\":2,\"coverablelines\":3,\"totalbranches\":0}}");
            return Task.FromResult(new ShellRunResult(0, "", ""));
        });
        Assert.Equal(200m / 3m, result.LineCoverage);
        Assert.Null(result.BranchCoverage);
    }

    [Fact]
    public async Task GenerateAsyncStaleOutputsDoNotSatisfyGeneration()
    {
        var input = Write("c.xml", Cobertura);
        var output = Path.Join(_root, "out");
        WriteOutputs(output, JsonSummary);
        File.WriteAllText(Path.Join(output, CoverageReportGenerator.MarkerFileName), "");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Generate([input], (_, _) => Task.FromResult(new ShellRunResult(0, "", ""))));
        Assert.Contains("SummaryGithub.md", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, "index.html")));
    }

    [Fact]
    public async Task GenerateAsyncRefusesForeignNonEmptyOutputDirectory()
    {
        var input = Write("c.xml", Cobertura);
        var keep = Write(Path.Join("out", "keep.txt"), "user data");
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Generate([input], (_, _) => { calls++; return Task.FromResult(new ShellRunResult(0, "", "")); }));
        Assert.Equal(0, calls);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task GenerateAsyncFailsExplicitlyOnGeneratorFailure()
    {
        var input = Write("c.xml", Cobertura);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Generate([input], (_, _) => Task.FromResult(new ShellRunResult(3, "", "sha512sum: FAILED"))));
        Assert.Contains("exit 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("sha512sum: FAILED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsyncFailsWhenDockerIsMissing()
    {
        var input = Write("c.xml", Cobertura);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Generate([input], (_, _) => throw new FileNotFoundException("missing", "docker")));
        Assert.Contains("Docker", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0", "+*", "Html")]
    [InlineData("mcr.microsoft.com/dotnet/sdk@sha256:abc", "+*", "Html")]
    [InlineData(CoverageReportGenerator.DefaultImage, "App*", "Html")]
    [InlineData(CoverageReportGenerator.DefaultImage, "+App\"", "Html")]
    [InlineData(CoverageReportGenerator.DefaultImage, "+*", "Html;Unknown")]
    [InlineData(CoverageReportGenerator.DefaultImage, "+*", "MarkdownSummaryGithub;JsonSummary")]
    [InlineData(CoverageReportGenerator.DefaultImage, "+*", "Html;-rm")]
    public async Task GenerateAsyncRejectsUnsafeOptions(string image, string filters, string types)
    {
        var input = Write("c.xml", Cobertura);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CoverageReportGenerator.GenerateAsync(
            [input], _root, "out", image, filters, "", "", types, CancellationToken.None,
            (_, _) => throw new InvalidOperationException("must not run")));
        Assert.False(Directory.Exists(Path.Join(_root, "out")));
    }

    [Fact]
    public async Task GenerateAsyncRejectsUnsupportedInputsAndUnsafeOutputs()
    {
        var unknown = Write("unknown.xml", "<results/>");
        var input = Write("c.xml", Cobertura);
        var nested = Write(Path.Join("out", "c.xml"), Cobertura);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate([unknown], Unexpected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate([], Unexpected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate([nested], Unexpected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate([Path.Join("..", "c.xml")], Unexpected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CoverageReportGenerator.GenerateAsync(
            [input], _root, ".", CoverageReportGenerator.DefaultImage, "", "", "", null, CancellationToken.None, Unexpected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CoverageReportGenerator.GenerateAsync(
            [input], _root, Path.GetTempPath(), CoverageReportGenerator.DefaultImage, "", "", "", null, CancellationToken.None, Unexpected));
    }

    [Fact]
    public async Task GenerateAsyncRegeneratesRexoOwnedOutputAndRejectsInvalidSummary()
    {
        var input = Write("c.xml", Cobertura);
        var output = Path.Join(_root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Join(output, CoverageReportGenerator.MarkerFileName), "");
        File.WriteAllText(Path.Join(output, "stale.html"), "old");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate([input], (_, _) =>
        {
            Assert.False(File.Exists(Path.Join(output, "stale.html")));
            WriteOutputs(output, "{\"summary\":{\"coveredlines\":5,\"coverablelines\":4}}");
            return Task.FromResult(new ShellRunResult(0, "", ""));
        }));
        Assert.Contains("coveredlines", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsyncMountsDefaultSourceDirectoryReadOnly()
    {
        var input = Write("c.xml", Cobertura);
        Write(Path.Join("src", "App", "Calc.cs"), "class Calc {}");
        IReadOnlyList<string>? args = null;
        await Generate([input], (command, _) =>
        {
            args = command.Arguments;
            WriteOutputs(Path.Join(_root, "out"), JsonSummary);
            return Task.FromResult(new ShellRunResult(0, "", ""));
        });
        Assert.NotNull(args);
        Assert.Contains($"type=bind,source={Path.Join(_root, "src")},target=/rexo-source/0,readonly", args);
        Assert.Contains("-sourcedirs:/rexo-source/0", args);
        Assert.DoesNotContain(args, arg => arg.Contains($"source={_root},", StringComparison.Ordinal));
        Assert.DoesNotContain(args, arg => arg.Contains(".git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsyncSkipsDefaultSourceWhenAbsentOrContainingGitMetadata()
    {
        var input = Write("c.xml", Cobertura);
        var args = await CaptureArgs([input], null);
        Assert.DoesNotContain(args, arg => arg.StartsWith("-sourcedirs:", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Join(_root, "src", ".git"));
        args = await CaptureArgs([input], null);
        Assert.DoesNotContain(args, arg => arg.Contains("/rexo-source/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsyncMountsExplicitSourceDirectoriesAndDeduplicates()
    {
        var input = Write("c.xml", Cobertura);
        Write(Path.Join("lib", "a.cs"), "");
        Write(Path.Join("tools", "b.cs"), "");
        Write(Path.Join("src", "c.cs"), "");
        var lib = Path.Join(_root, "lib");
        var args = await CaptureArgs([input], ["lib", Path.Join("tools", "."), lib]);
        Assert.Contains($"type=bind,source={lib},target=/rexo-source/0,readonly", args);
        Assert.Contains($"type=bind,source={Path.Join(_root, "tools")},target=/rexo-source/1,readonly", args);
        Assert.Contains("-sourcedirs:/rexo-source/0;/rexo-source/1", args);
        Assert.DoesNotContain(args, arg => arg.Contains("/rexo-source/2", StringComparison.Ordinal));
        Assert.DoesNotContain(args, arg => arg.Contains(Path.Join(_root, "src"), StringComparison.Ordinal));

        args = await CaptureArgs([input], []);
        Assert.DoesNotContain(args, arg => arg.Contains("/rexo-source/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsyncRejectsUnsafeSourceDirectories()
    {
        var input = Write("c.xml", Cobertura);
        Directory.CreateDirectory(Path.Join(_root, "repo-like", ".git"));
        Directory.CreateDirectory(Path.Join(_root, "out", "nested"));
        File.WriteAllText(Path.Join(_root, "out", CoverageReportGenerator.MarkerFileName), "");
        string[][] cases =
        [
            ["."], [_root], ["missing"], [Path.Join("..", "elsewhere")], [Path.GetTempPath()],
            ["repo-like"], ["out"], [Path.Join("out", "nested")],
        ];
        foreach (var sources in cases)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => CoverageReportGenerator.GenerateAsync(
                [input], _root, "out", CoverageReportGenerator.DefaultImage, "", "", "", null,
                CancellationToken.None, Unexpected, sources));
        }
    }

    private async Task<IReadOnlyList<string>> CaptureArgs(IReadOnlyList<string> files, IReadOnlyList<string>? sources)
    {
        IReadOnlyList<string> args = [];
        await CoverageReportGenerator.GenerateAsync(files, _root, "out", CoverageReportGenerator.DefaultImage,
            "", "", "", null, CancellationToken.None, (command, _) =>
            {
                args = command.Arguments;
                WriteOutputs(Path.Join(_root, "out"), JsonSummary);
                return Task.FromResult(new ShellRunResult(0, "", ""));
            }, sources);
        return args;
    }
    private Task<CoverageReportSummary> Generate(
        IReadOnlyList<string> files, Func<GitHubReleaseCommand, CancellationToken, Task<ShellRunResult>> run) =>
        CoverageReportGenerator.GenerateAsync(files, _root, "out", CoverageReportGenerator.DefaultImage,
            "", "", "", null, CancellationToken.None, run);

    private static Task<ShellRunResult> Unexpected(GitHubReleaseCommand command, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"unexpected run {command.FileName}"));

    private static void WriteOutputs(string output, string json)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Join(output, "SummaryGithub.md"), "# Summary");
        File.WriteAllText(Path.Join(output, "Summary.json"), json);
        File.WriteAllText(Path.Join(output, "index.html"), "<html></html>");
    }
}

