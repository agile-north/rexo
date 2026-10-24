namespace Rexo.Execution.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Rexo.Configuration;
using Rexo.Core.Models;
using Rexo.Policies;
using Rexo.Artifacts;
using Rexo.Templating;
using Rexo.Versioning;
using Xunit;
using ExecutionContext = Rexo.Core.Models.ExecutionContext;

public sealed class GitHubReportTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"rexo-reports-{Guid.NewGuid():N}");

    public GitHubReportTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("github-tests", "reports tests")]
    [InlineData("github-coverage", "reports coverage")]
    public async Task PoliciesAreDiscoverableAndAppendAlwaysRunSteps(string policy, string command)
    {
        Assert.Contains(policy, EmbeddedPolicyTemplates.TemplateNames);
        var file = Path.Join(_root, "rexo.json");
        await File.WriteAllTextAsync(file, $$"""
            {
              "$schema":"rexo.schema.json","schemaVersion":"1.0","name":"reports",
              "extends":["embedded:standard","embedded:dotnet","embedded:{{policy}}"]
            }
            """);
        var config = await RepoConfigurationLoader.LoadAsync(file, CancellationToken.None);
        Assert.True(config.Commands!.ContainsKey(command));
        Assert.Equal(policy, config.Commands["test"].Steps[^1].Id);
        Assert.True(config.Commands["test"].Steps[^1].AlwaysRun);
        if (policy == "github-coverage")
            Assert.Equal(CoverageReportGenerator.DefaultImage,
                config.Vars!["githubCoverage"].GetProperty("image").GetString());
    }

    [Fact]
    public async Task FailedTestsStillGenerateSummaryAndReturnFailedStep()
    {
        Write("results/results.xml", """
            <testsuite><testcase name="passing"/><testcase name="broken"><failure message="expected &lt;value&gt;">Details</failure></testcase></testsuite>
            """);
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-tests", Context(), false, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(1, result.Outputs["failed"]);
        Assert.Equal("failure", result.Outputs["conclusion"]);
        var summary = await File.ReadAllTextAsync(Path.Join(_root, "results", "github-report", "SummaryGithub.md"));
        Assert.Contains("| 2 | 1 | 1 | 0 |", summary, StringComparison.Ordinal);
        Assert.Contains("expected &lt;value&gt;", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothReportingPoliciesRunAfterFailureAndPreserveDeclaredOutputs(bool invalidReport)
    {
        var file = Path.Join(_root, "rexo.json");
        await File.WriteAllTextAsync(file, """
            {
              "$schema":"rexo.schema.json","schemaVersion":"1.0","name":"reports",
              "extends":["embedded:standard","embedded:github-tests","embedded:github-coverage"],
              "vars":{"githubTests":{"publish":"never"},"githubCoverage":{"publish":"never"}},
              "outputs":{"ci":{"emit":false}},
              "commands":{"test":{"merge":"prepend","steps":[
                {"id":"runner","uses":"builtin:test-runner","outputs":{"testResults":["custom/*.xml"]}}
              ]}}
            }
            """);
        var config = await RepoConfigurationLoader.LoadAsync(file, CancellationToken.None);
        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var builtins = new BuiltinRegistry();
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), new ArtifactProviderRegistry());
        loader.LoadInto(registry, config, _root, executor);
        builtins.Register("builtin:test-runner", (_, _, _) =>
        {
            Write("custom/junit.xml", invalidReport
                ? """<assemblies><assembly><collection><test name="broken" result="banana"/></collection></assembly></assemblies>"""
                : "<testsuite><testcase name=\"works\"/></testsuite>");
            return Task.FromResult(new StepResult("runner", false, 1, TimeSpan.Zero, new Dictionary<string, object?>()));
        });
        var result = await executor.ExecuteAsync("test", new CommandInvocation(
            new Dictionary<string, string>(), new Dictionary<string, string?>(), false, null, _root), CancellationToken.None);
        Assert.False(result.Success);
        var tests = Assert.Single(result.Steps, step => step.StepId == "github-tests");
        Assert.False(tests.Success);
        if (invalidReport)
            Assert.Contains("banana", Assert.IsType<string>(tests.Outputs["error"]), StringComparison.Ordinal);
        else
            Assert.Equal(1, tests.Outputs["total"]);
        Assert.Equal("failure", tests.Outputs["conclusion"]);
        Assert.Contains(result.Steps, step => step.StepId == "github-coverage");
        Assert.True(Assert.Single(result.Steps, step => step.StepId == "runner").Outputs.ContainsKey("__fileOutputs"));
    }

    [Fact]
    public void AnnotationPathsMapBuildRootsWithoutEscapingRepository()
    {
        Write("src/code.cs", "namespace Test;");
        Assert.Equal("src/code.cs", GitHubReportBuiltinModule.AnnotationPath(_root, "/build/work", "/build/work/src/code.cs"));
        Assert.Equal("src/code.cs", GitHubReportBuiltinModule.AnnotationPath(_root, "", Path.Join(_root, "src", "code.cs")));
        Assert.Null(GitHubReportBuiltinModule.AnnotationPath(_root, "", "../../outside.cs"));
        Assert.Null(GitHubReportBuiltinModule.AnnotationPath(_root, "/build/work", "/another/work/src/code.cs"));
    }

    [Theory]
    [InlineData("warn", true)]
    [InlineData("fail", false)]
    public async Task MissingReportsNeverBecomeAPassingCheck(string behavior, bool success)
    {
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-tests",
            Context(new() { ["missingReports"] = behavior }), false, CancellationToken.None);
        Assert.Equal(success, result.Success);
        Assert.Equal(success ? "neutral" : "failure", result.Outputs["conclusion"]);
        Assert.Equal(0, result.Outputs["reportCount"]);
    }

    [Fact]
    public async Task AutoDiscoverySkipsUnrelatedXmlAndExplicitSelectionRejectsIt()
    {
        Write("results/unrelated.xml", "<coverage/>");
        var auto = await GitHubReportBuiltinModule.ExecuteAsync("github-tests", Context(), false, CancellationToken.None);
        Assert.True(auto.Success);
        var explicitSelection = await GitHubReportBuiltinModule.ExecuteAsync("github-tests",
            Context(new() { ["paths"] = "results/unrelated.xml" }), false, CancellationToken.None);
        Assert.False(explicitSelection.Success);
        Assert.Contains("Unsupported", Assert.IsType<string>(explicitSelection.Outputs["error"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish", "sometimes")]
    [InlineData("checks", "yes")]
    [InlineData("scope", "bad/scope")]
    [InlineData("retentionDays", 0)]
    [InlineData("outputDirectory", "../escape")]
    public async Task InvalidOverridesFailExplicitly(string setting, object value)
    {
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-tests",
            Context(new() { [setting] = value }), false, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("failed", result.Outputs["status"]);
    }

    [Fact]
    public async Task DryRunDoesNotReadReportsWriteSummaryOrPublish()
    {
        Write("results/bad.xml", "bad");
        using var client = new HttpClient(new RejectNetworkHandler());
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-tests",
            Context(new() { ["publish"] = "always" }) with { IsDryRun = true }, false,
            CancellationToken.None, client);
        Assert.True(result.Success);
        Assert.Equal("skipped", result.Outputs["status"]);
        Assert.False(Directory.Exists(Path.Join(_root, "results", "github-report")));
    }

    [Fact]
    public void GlobsSupportWildcardsExclusionsAndDoNotRediscoverGeneratedReports()
    {
        Write("results/net10/a.xml", "<testsuite/>");
        Write("results/net9/a.xml", "<testsuite/>");
        Write("results/github-report/generated.xml", "<testsuite/>");
        var matches = ReportFiles.Discover(_root, "results/*/*.xml", "results/net9/**",
            Path.Join(_root, "results", "github-report"));
        Assert.Single(matches);
        Assert.Equal(Path.Join(_root, "results", "net10", "a.xml"), matches[0]);
        Assert.Throws<InvalidOperationException>(() => ReportFiles.Resolve(_root, "../outside.xml"));
    }

    [Fact]
    public async Task CheckApiPublishesMaskedSummaryAndBatchesAnnotations()
    {
        var handler = new CheckHandler();
        using var client = new HttpClient(handler);
        var context = Context(new() { ["publish"] = "always", ["scope"] = "linux-net10" });
        var options = new GitHubReportOptions(context, false);
        var annotations = Enumerable.Range(1, 51).Select(index => new GitHubReportAnnotation(
            "src/file.cs", index, "broken", "dummy-token failure")).ToArray();
        var publication = await GitHubReportCheckPublisher.PublishAsync(context, options, "Rexo tests",
            "dummy-token summary", "failure", annotations, CancellationToken.None, client);
        Assert.Equal("https://github.com/owner/repo/runs/1", publication.Url);
        Assert.Equal("published", publication.Status);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Patch, handler.Requests[2].Method);
        using var first = JsonDocument.Parse(handler.Requests[1].Body);
        using var second = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal(50, first.RootElement.GetProperty("output").GetProperty("annotations").GetArrayLength());
        Assert.Equal(1, second.RootElement.GetProperty("output").GetProperty("annotations").GetArrayLength());
        Assert.DoesNotContain("dummy-token", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal("Rexo tests (linux-net10)", first.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ApiFailuresAreExplicitAndDoNotExposeResponseSecrets()
    {
        using var client = new HttpClient(new CheckHandler { Fail = true });
        var context = Context(new() { ["publish"] = "always" });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReportCheckPublisher.PublishAsync(context, new(context, false), "Tests", "summary", "success",
                [], CancellationToken.None, client));
        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalRerunsUpdateWithoutDuplicatingAnnotations()
    {
        var handler = new CheckHandler { KeepCheck = true };
        using var client = new HttpClient(handler);
        var context = Context(new() { ["publish"] = "always" });
        var options = new GitHubReportOptions(context, false);
        GitHubReportAnnotation[] annotations = [new("src/test.cs", 1, "broken", "failure")];
        await GitHubReportCheckPublisher.PublishAsync(context, options, "Tests", "summary", "failure",
            annotations, CancellationToken.None, client);
        await GitHubReportCheckPublisher.PublishAsync(context, options, "Tests", "summary", "failure",
            annotations, CancellationToken.None, client);
        Assert.Equal(HttpMethod.Patch, handler.Requests[^1].Method);
        using var update = JsonDocument.Parse(handler.Requests[^1].Body);
        Assert.Empty(update.RootElement.GetProperty("output").GetProperty("annotations").EnumerateArray());
    }

    [Fact]
    public async Task MissingCredentialsSkipChecksWithoutTouchingNetwork()
    {
        using var client = new HttpClient(new RejectNetworkHandler());
        var context = Context(new() { ["publish"] = "always" }) with
        {
            MappedSecretEnvironment = new Dictionary<string, string> { ["GH_TOKEN"] = "", ["GITHUB_TOKEN"] = "" },
        };
        var result = await GitHubReportCheckPublisher.PublishAsync(context, new(context, false),
            "Tests", "summary", "success", [], CancellationToken.None, client);
        Assert.Equal("skipped", result.Status);
        Assert.Null(result.Url);
        Assert.Contains("token", result.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedAnnotationRunsAreSupersededAfterReplacement()
    {
        var handler = new CheckHandler { KeepCheck = true };
        using var client = new HttpClient(handler);
        var context = Context(new() { ["publish"] = "always" });
        var options = new GitHubReportOptions(context, false);
        await GitHubReportCheckPublisher.PublishAsync(context, options, "Tests", "failed", "failure",
            [new("src/test.cs", 1, "broken", "failure")], CancellationToken.None, client);
        await GitHubReportCheckPublisher.PublishAsync(context, options, "Tests", "passed", "success",
            [], CancellationToken.None, client);
        Assert.Equal(HttpMethod.Post, handler.Requests[^2].Method);
        Assert.Equal(HttpMethod.Patch, handler.Requests[^1].Method);
        using var superseded = JsonDocument.Parse(handler.Requests[^1].Body);
        Assert.Equal("neutral", superseded.RootElement.GetProperty("conclusion").GetString());
        Assert.Equal("Superseded report", superseded.RootElement.GetProperty("output").GetProperty("title").GetString());
    }

    [Fact]
    public async Task PreviewShowsDiscoveryWithoutGenerationWritesOrPublishing()
    {
        Write("results/tests.xml", "<testsuite><testcase name=\"passes\"/></testsuite>");
        using var client = new HttpClient(new RejectNetworkHandler());
        var context = Context(new() { ["publish"] = "always" }) with
        {
            Options = new Dictionary<string, string?> { ["report-preview"] = "true" },
        };
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-tests", context, false, CancellationToken.None, client);
        Assert.True(result.Success);
        Assert.Equal("preview", result.Outputs["status"]);
        Assert.Equal(1, result.Outputs["reportCount"]);
        var detected = JsonSerializer.Serialize(result.Outputs["detectedReports"]);
        Assert.Contains("results/tests.xml", detected, StringComparison.Ordinal);
        Assert.Contains("junit", detected, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_root, "results", "github-report")));
    }

    [Theory]
    [InlineData("auto", true, "skipped")]
    [InlineData("required", false, "failed")]
    [InlineData("never", true, "skipped")]
    public async Task ArtifactModeDistinguishesUnavailableFromRequiredUploads(string mode, bool success, string status)
    {
        Write("results/tests.xml", "<testsuite><testcase name=\"passes\"/></testsuite>");
        var context = Context(new() { ["publish"] = "always", ["checks"] = false, ["artifactUpload"] = mode }) with
        {
            FileEnvironment = new Dictionary<string, string>
            {
                ["GITHUB_ACTIONS"] = "true",
                ["ACTIONS_RUNTIME_TOKEN"] = "",
                ["ACTIONS_RESULTS_URL"] = "",
            },
        };
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-tests", context, false, CancellationToken.None);
        Assert.Equal(success, result.Success);
        Assert.Equal(status, result.Outputs["artifactStatus"]);
        Assert.Equal("skipped", result.Outputs["checkStatus"]);
        Assert.Equal("Checks are disabled.", result.Outputs["checkSkipReason"]);
    }

    [Theory]
    [InlineData("75", "25", true)]
    [InlineData("80", "", false)]
    [InlineData("", "30", false)]
    public async Task CoverageThresholdsUseGeneratedCounters(string line, string branch, bool success)
    {
        Write("coverage/coverage.xml", """<coverage line-rate="0.75"><packages/></coverage>""");
        var context = Context(new()
        {
            ["minimumLineCoverage"] = line, ["minimumBranchCoverage"] = branch, ["sourceDirectories"] = "none",
        });
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-coverage", context, true, CancellationToken.None,
            run: FakeCoverageAsync);
        Assert.Equal(success, result.Success);
        Assert.Equal(75m, result.Outputs["lineCoverage"]);
        Assert.Equal(25m, result.Outputs["branchCoverage"]);
        Assert.True(result.Outputs.ContainsKey("__fileOutputs"));
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("", "80")]
    public async Task ConfiguredThresholdFailsWhenCoverageReportsAreMissing(string line, string branch)
    {
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-coverage",
            Context(new() { ["minimumLineCoverage"] = line, ["minimumBranchCoverage"] = branch }), true,
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("failure", result.Outputs["conclusion"]);
        Assert.Equal(0, result.Outputs["reportCount"]);
        Assert.Null(result.Outputs["lineCoverage"]);
        Assert.Null(result.Outputs["branchCoverage"]);
        Assert.Contains("unavailable", Assert.IsType<string>(result.Outputs["error"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCoverageSummaryDoesNotPreventLaterValidConversion()
    {
        var first = await GitHubReportBuiltinModule.ExecuteAsync("github-coverage", Context(), true, CancellationToken.None);
        Assert.True(first.Success);
        Write("coverage/coverage.xml", "<coverage><packages/></coverage>");
        var second = await GitHubReportBuiltinModule.ExecuteAsync("github-coverage", Context(), true,
            CancellationToken.None, run: FakeCoverageAsync);
        Assert.True(second.Success, second.Outputs.GetValueOrDefault("error")?.ToString());
        Assert.Equal(75m, second.Outputs["lineCoverage"]);
    }

    private Task<ShellRunResult> FakeCoverageAsync(GitHubReleaseCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal("docker", command.FileName);
        Write("coverage/github-report/SummaryGithub.md", "# Coverage: 75%");
        Write("coverage/github-report/index.html", "<html>Coverage</html>");
        Write("coverage/github-report/Summary.json",
            """{"summary":{"coveredlines":3,"coverablelines":4,"coveredbranches":1,"totalbranches":4}}""");
        return Task.FromResult(new ShellRunResult(0, "", ""));
    }

    [Fact]
    public async Task PublicationErrorsCanWarnButCannotHideTestFailures()
    {
        Write("results/results.xml", "<testsuite><testcase name=\"broken\"><failure>failure</failure></testcase></testsuite>");
        using var client = new HttpClient(new CheckHandler { Fail = true });
        var result = await GitHubReportBuiltinModule.ExecuteAsync("github-tests",
            Context(new() { ["publish"] = "always", ["publishErrors"] = "warn", ["uploadArtifact"] = false }),
            false, CancellationToken.None, client);
        Assert.False(result.Success);
        Assert.True(result.Outputs.ContainsKey("publishWarning"));
    }

    [Fact]
    public async Task CancellationIsNotReportedAsSuccessfulSkip()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubReportBuiltinModule.ExecuteAsync("github-tests", Context(), false, cancellation.Token));
    }

    private ExecutionContext Context(Dictionary<string, object?>? settings = null)
    {
        settings ??= new();
        settings.TryAdd("publish", "never");
        settings.TryAdd("jobSummary", false);
        settings.TryAdd("scope", "unit");
        return new ExecutionContext(_root, "main", new string('a', 40), new Dictionary<string, object?>())
        {
            RemoteUrl = "https://github.com/owner/repo.git",
            ResolvedVars = new Dictionary<string, object?> { ["githubTests"] = settings, ["githubCoverage"] = settings },
            ResolvedOutputs = new Dictionary<string, object?>
            {
                ["tests"] = new Dictionary<string, object?> { ["results"] = "results", ["coverage"] = "coverage" },
            },
            MappedSecretEnvironment = new Dictionary<string, string> { ["GH_TOKEN"] = "dummy-token", ["GITHUB_TOKEN"] = "dummy-token" },
        };
    }

    private void Write(string path, string text)
    {
        var full = Path.Join(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected network access.");
    }

    private sealed class CheckHandler : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public bool KeepCheck { get; init; }
        private Dictionary<string, object?>? _existing;
        public List<(HttpMethod Method, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var response = """{"id":1,"html_url":"https://github.com/owner/repo/runs/1"}""";
            if (request.Method == HttpMethod.Get)
                response = JsonSerializer.Serialize(new { check_runs = _existing is null ? [] : new[] { _existing } });
            if (KeepCheck && request.Method == HttpMethod.Post)
            {
                using var payload = JsonDocument.Parse(Requests[^1].Body);
                _existing = new()
                {
                    ["id"] = 1, ["name"] = payload.RootElement.GetProperty("name").GetString(),
                    ["external_id"] = payload.RootElement.GetProperty("external_id").GetString(),
                };
            }
            return new HttpResponseMessage(Fail ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent(Fail ? "secret-body" : response,
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
