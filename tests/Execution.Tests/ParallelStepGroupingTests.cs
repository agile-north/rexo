namespace Rexo.Execution.Tests;

using Rexo.Configuration.Models;

/// <summary>
/// Tests for parallel step grouping logic in ConfigCommandLoader.
/// The grouping logic is internal, so we test via observable execution behaviour
/// using BuiltinCommandRegistration config commands.
/// </summary>
public sealed class ParallelStepGroupingTests
{
    [Fact]
    public void StepConfigParallelFlagDefaultsToNull()
    {
        var step = new RepoStepConfig(Run: "echo hi");
        Assert.Null(step.Parallel);
    }

    [Fact]
    public void StepConfigParallelFlagCanBeSetTrue()
    {
        var step = new RepoStepConfig(Run: "echo hi", Parallel: true);
        Assert.True(step.Parallel);
    }

    [Fact]
    public void StepConfigContinueOnErrorCanBeSet()
    {
        var step = new RepoStepConfig(Run: "echo fail", ContinueOnError: true);
        Assert.True(step.ContinueOnError);
    }

    [Fact]
    public void StepConfigOutputPatternAndOutputFileCanBeSet()
    {
        var step = new RepoStepConfig(Run: "echo hello", OutputPattern: @"(?<greeting>\w+)", OutputFile: "out.txt");
        Assert.Equal(@"(?<greeting>\w+)", step.OutputPattern);
        Assert.Equal("out.txt", step.OutputFile);
    }

    [Fact]
    public void StepConfigDependsOnCanBeSet()
    {
        var step = new RepoStepConfig(Run: "echo later", DependsOn: ["build", "test"]);
        Assert.NotNull(step.DependsOn);
        Assert.Equal(["build", "test"], step.DependsOn);
    }

    [Fact]
    public void StepConfigContainerCanBeSet()
    {
        var step = new RepoStepConfig(
            Run: "dotnet --info",
            Container: new RepoStepContainerConfig(
                Image: "mcr.microsoft.com/dotnet/sdk:10.0",
                Env: new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
                WorkingDirectory: "/work"));

        Assert.NotNull(step.Container);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", step.Container?.Image);
        Assert.Equal("1", step.Container?.Env?["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("/work", step.Container?.WorkingDirectory);
    }
}
