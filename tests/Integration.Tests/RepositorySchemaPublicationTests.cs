namespace Rexo.Integration.Tests;

using Rexo.Configuration;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Templating;
using ExecutionContext = Rexo.Core.Models.ExecutionContext;

public sealed class RepositorySchemaPublicationTests
{
    [Theory]
    [InlineData(true, true, true, false, false, "true")]
    [InlineData(false, true, true, false, false, "false")]
    [InlineData(true, false, true, false, false, "false")]
    [InlineData(true, true, false, false, false, "false")]
    [InlineData(true, true, true, true, false, "false")]
    [InlineData(true, true, true, false, true, "false")]
    public async Task SchemaGateRequiresConfirmedPublicationOutsidePullRequestsAndDryRuns(
        bool pushed, bool allowed, bool push, bool pullRequest, bool dryRun, string expected)
    {
        var (step, _) = await LoadSchemaStepAsync();
        var context = PublicationContext(RepositoryRoot(), pushed, allowed) with
        {
            IsPullRequest = pullRequest,
            Options = new Dictionary<string, string?>
            {
                ["push"] = push ? "true" : "false",
                ["dry-run"] = dryRun ? "true" : "false",
            },
        };
        Assert.Equal(expected, new TemplateRenderer().Render(step.When!, context));
        Assert.Equal("false", new TemplateRenderer().Render(step.When!,
            context with { CompletedSteps = new Dictionary<string, StepResult>() }));
    }

    [Fact]
    public async Task SchemaStepUpdatesOnlyConfiguredBranchToExactCommitUsingLocalRemote()
    {
        var (step, branch) = await LoadSchemaStepAsync();
        var root = Path.Join(Path.GetTempPath(), $"rexo-schema-publish-{Guid.NewGuid():N}");
        var working = Path.Join(root, "work");
        var remote = Path.Join(root, "remote.git");
        Directory.CreateDirectory(working);
        try
        {
            await GitAsync(root, "init", "--bare", remote);
            await GitAsync(working, "init", "--initial-branch=main");
            await GitAsync(working, "remote", "add", "origin", remote);
            await File.WriteAllTextAsync(Path.Join(working, "schema.json"), "{}");
            await GitAsync(working, "add", "schema.json");
            await GitAsync(working, "-c", "user.name=Rexo Test", "-c", "user.email=test@example.invalid",
                "commit", "-m", "First schema");
            var target = await GitAsync(working, "rev-parse", "HEAD");
            await File.WriteAllTextAsync(Path.Join(working, "schema.json"), """{"updated":true}""");
            await GitAsync(working, "-c", "user.name=Rexo Test", "-c", "user.email=test@example.invalid",
                "commit", "-am", "Second schema");
            var newer = await GitAsync(working, "rev-parse", "HEAD");
            await GitAsync(working, "push", "origin", $"HEAD:refs/heads/{branch}");

            var context = PublicationContext(working, true, true) with
            {
                CommitSha = target,
                Options = new Dictionary<string, string?> { ["push"] = "true" },
                ResolvedVars = new Dictionary<string, object?> { ["schemaBranch"] = branch },
            };
            var executor = new StepExecutor(new DefaultCommandExecutor(new CommandRegistry()),
                new TemplateRenderer(), new BuiltinRegistry());
            var result = await executor.ExecuteAsync(step, context, CancellationToken.None);
            Assert.True(result.Success, result.Outputs.GetValueOrDefault("stderr")?.ToString());
            Assert.Equal(target, await GitAsync(root, "--git-dir", remote, "rev-parse", $"refs/heads/{branch}"));
            Assert.Equal(newer, await GitAsync(working, "rev-parse", "HEAD"));
            Assert.Equal($"refs/heads/{branch}", await GitAsync(root, "--git-dir", remote,
                "for-each-ref", "--format=%(refname)"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static ExecutionContext PublicationContext(string root, bool pushed, bool allowed) =>
        ExecutionContext.Empty(root).WithStep(new StepResult("push-artifacts", true, 0, TimeSpan.Zero,
            new Dictionary<string, object?>
            {
                ["__artifacts"] = new List<ArtifactManifestEntry>
                {
                    new("nuget", "Rexo.Cli", true, pushed, []),
                },
                ["__pushDecisions"] = new List<PushDecision>
                {
                    new("Rexo.Cli", allowed, allowed ? "allowed" : "blocked"),
                },
            }));

    private static async Task<(StepDefinition Step, string Branch)> LoadSchemaStepAsync()
    {
        var config = await RepoConfigurationLoader.LoadAsync(
            Path.Join(RepositoryRoot(), ".rexo", "rexo.yaml"), CancellationToken.None);
        var step = Assert.Single(config.Commands!["release"].Steps, candidate => candidate.Id == "publish-schema");
        var branch = config.Vars!["schemaBranch"].GetString()!;
        return (new StepDefinition(step.Id, step.Run, step.Uses, step.Command, step.When), branch);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "solution.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var result = await ShellRunner.RunProcessAsync("git", arguments, directory,
            cancellationToken: CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return result.Stdout.Trim();
    }
}
