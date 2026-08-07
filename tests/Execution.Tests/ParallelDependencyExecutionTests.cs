namespace Rexo.Execution.Tests;

using Rexo.Artifacts;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class ParallelDependencyExecutionTests
{
    [Fact]
    public async Task ParallelDependsOnAllowsFanInOrdering()
    {
        var (executor, builtins) = CreateExecutorWithCommand(
            [
                new RepoStepConfig(Id: "a", Uses: "builtin:a", Parallel: true),
                new RepoStepConfig(Id: "b", Uses: "builtin:b", Parallel: true),
                new RepoStepConfig(Id: "c", Uses: "builtin:c", Parallel: true, DependsOn: ["a", "b"]),
            ],
            maxParallel: 3);

        builtins.Register("builtin:a", static (step, _, _) =>
            Task.FromResult(new StepResult(step.Id ?? "a", true, 0, TimeSpan.Zero, new Dictionary<string, object?>())));
        builtins.Register("builtin:b", static (step, _, _) =>
            Task.FromResult(new StepResult(step.Id ?? "b", true, 0, TimeSpan.Zero, new Dictionary<string, object?>())));
        builtins.Register("builtin:c", static (step, ctx, _) =>
        {
            var hasA = ctx.CompletedSteps.ContainsKey("a");
            var hasB = ctx.CompletedSteps.ContainsKey("b");
            var success = hasA && hasB;
            return Task.FromResult(new StepResult(
                step.Id ?? "c",
                success,
                success ? 0 : 1,
                TimeSpan.Zero,
                new Dictionary<string, object?>
                {
                    ["hasA"] = hasA.ToString(),
                    ["hasB"] = hasB.ToString(),
                }));
        });

        var result = await ExecuteAsync(executor, "dep-test");

        Assert.True(result.Success);
        Assert.Equal(3, result.Steps.Count);

        var stepC = result.Steps.Single(s => s.StepId == "c");
        Assert.True(stepC.Success);
        Assert.Equal("True", stepC.Outputs["hasA"]);
        Assert.Equal("True", stepC.Outputs["hasB"]);
    }

    [Fact]
    public async Task ParallelDependsOnDetectsCycleAndFails()
    {
        var (executor, builtins) = CreateExecutorWithCommand(
            [
                new RepoStepConfig(Id: "a", Uses: "builtin:a", Parallel: true, DependsOn: ["b"]),
                new RepoStepConfig(Id: "b", Uses: "builtin:b", Parallel: true, DependsOn: ["a"]),
            ],
            maxParallel: 2);

        builtins.Register("builtin:a", static (step, _, _) =>
            Task.FromResult(new StepResult(step.Id ?? "a", true, 0, TimeSpan.Zero, new Dictionary<string, object?>())));
        builtins.Register("builtin:b", static (step, _, _) =>
            Task.FromResult(new StepResult(step.Id ?? "b", true, 0, TimeSpan.Zero, new Dictionary<string, object?>())));

        var result = await ExecuteAsync(executor, "dep-test");

        Assert.False(result.Success);
        var failure = Assert.Single(result.Steps);
        Assert.False(failure.Success);
        Assert.Contains("deadlock/cycle", failure.Outputs["error"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlwaysRunStepsExecuteAfterHardFailure()
    {
        var (executor, builtins) = CreateExecutorWithCommand(
            [
                new RepoStepConfig(Id: "fail", Uses: "builtin:fail"),
                new RepoStepConfig(Id: "report", Uses: "builtin:report", AlwaysRun: true),
                new RepoStepConfig(Id: "skip", Uses: "builtin:skip"),
            ],
            maxParallel: null);

        var calls = new List<string>();
        builtins.Register("builtin:fail", static (_, _, _) =>
            Task.FromResult(new StepResult("fail", false, 17, TimeSpan.Zero, new Dictionary<string, object?>())));
        builtins.Register("builtin:report", (_, _, _) =>
        {
            calls.Add("report");
            return Task.FromResult(new StepResult("report", true, 0, TimeSpan.Zero, new Dictionary<string, object?>()));
        });
        builtins.Register("builtin:skip", (_, _, _) =>
        {
            calls.Add("skip");
            return Task.FromResult(new StepResult("skip", true, 0, TimeSpan.Zero, new Dictionary<string, object?>()));
        });

        var result = await ExecuteAsync(executor, "dep-test");

        Assert.False(result.Success);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(["report"], calls);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("fail", result.Steps[0].StepId);
        Assert.Equal("report", result.Steps[1].StepId);
    }

    private static (DefaultCommandExecutor Executor, BuiltinRegistry Builtins) CreateExecutorWithCommand(
        IReadOnlyList<RepoStepConfig> steps,
        int? maxParallel)
    {
        var builtins = new BuiltinRegistry();
        var loader = new ConfigCommandLoader(
            builtins,
            new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(),
            new ArtifactProviderRegistry());

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["dep-test"] = new RepoCommandConfig(
                    Description: "dependency test",
                    Options: new Dictionary<string, RepoOptionConfig>(),
                    Steps: [.. steps])
                {
                    MaxParallel = maxParallel,
                },
            },
            Aliases: new Dictionary<string, string>());

        var repoRoot = Path.GetTempPath();
        loader.LoadInto(registry, config, repoRoot, executor);
        return (executor, builtins);
    }

    private static Task<CommandResult> ExecuteAsync(DefaultCommandExecutor executor, string commandName) =>
        executor.ExecuteAsync(
            commandName,
            new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: Path.GetTempPath()),
            CancellationToken.None);
}
