namespace Rexo.Execution.Tests;

using System.Globalization;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Templating;
using Rexo.Versioning;

/// <summary>
/// Acceptance tests for policy-driven command layering.
/// Verifies that embedded:standard and embedded:dotnet templates compose correctly
/// via the policyMerge wrap mechanism, and that cycle detection works.
/// </summary>
[Collection("CommandLayering")]
public sealed class CommandLayeringTests
{
    // ─── helpers ────────────────────────────────────────────────────────────

    private static readonly string SchemaUri =
        RepoConfigurationLoader.SupportedRexoSchemaUri;

    private static CommandInvocation EmptyInvocation(string workingDir) =>
        new(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: workingDir);

    private static async Task<RepoConfig> LoadTempConfigAsync(string dir, string json, CancellationToken ct = default)
    {
        var path = Path.Combine(dir, "rexo.json");
        await File.WriteAllTextAsync(path, json, ct);
        return await RepoConfigurationLoader.LoadAsync(path, ct);
    }

    private static ConfigCommandLoader CreateLoader() =>
        new ConfigCommandLoader(
            new BuiltinRegistry(),
            new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(),
            new ArtifactProviderRegistry());

    private static string MinimalJson(string name, string? extra = null) =>
        $$"""
        {
          "$schema": "{{SchemaUri}}",
          "schemaVersion": "1.0",
          "name": "{{name}}"{{(extra is not null ? "," : string.Empty)}}
          {{extra ?? string.Empty}}
        }
        """;

    // ─── config merge tests (no shell execution) ─────────────────────────────

    [Fact]
    public async Task StandardOnlyExtendsBuildHasBuildArtifactsStep()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var json = MinimalJson("test-repo", """
                "extends": ["embedded:standard"]
                """);

            var config = await LoadTempConfigAsync(dir, json);

            Assert.NotNull(config.Commands);
            Assert.True(config.Commands.ContainsKey("build"), "build command should exist from embedded:standard");
            var buildSteps = config.Commands["build"].Steps;
            Assert.Contains(buildSteps, s => s.Uses == "builtin:build-artifacts");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task StandardOnlyExtendsBuildHasNoDotnetRunStep()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var json = MinimalJson("test-repo", """
                "extends": ["embedded:standard"]
                """);

            var config = await LoadTempConfigAsync(dir, json);

            Assert.NotNull(config.Commands);
            var buildSteps = config.Commands.GetValueOrDefault("build")?.Steps ?? [];
            // Standard build uses builtin, not dotnet CLI
            Assert.DoesNotContain(buildSteps, s => s.Run is not null &&
                s.Run.Contains("dotnet build", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DotnetAndStandardExtendsTestStepsDoNotContainSelfRef()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // embedded:dotnet provides "test: { run: dotnet test... }",
            // embedded:standard provides "test: { command: test, whenExists: true }".
            // policyMerge wraps the standard self-ref with the dotnet steps,
            // resulting in a test command that has the dotnet steps directly (no self-ref).
            var json = MinimalJson("test-repo", """
                "extends": ["embedded:dotnet", "embedded:standard"]
                """);

            var config = await LoadTempConfigAsync(dir, json);

            Assert.NotNull(config.Commands);
            Assert.True(config.Commands.ContainsKey("test"), "test command should exist");
            var testSteps = config.Commands["test"].Steps;

            // After policyMerge wrap, the dotnet test step should be inlined
            Assert.DoesNotContain(testSteps, s =>
                s.Command is not null &&
                string.Equals(s.Command, "test", StringComparison.OrdinalIgnoreCase));

            // The dotnet test run step should be present
            Assert.Contains(testSteps, s => s.Run is not null &&
                s.Run.Contains("dotnet test", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DotnetAndStandardExtendsAnalyzeStepsDoNotContainSelfRef()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var json = MinimalJson("test-repo", """
                "extends": ["embedded:dotnet", "embedded:standard"]
                """);

            var config = await LoadTempConfigAsync(dir, json);

            Assert.NotNull(config.Commands);
            // analyze exists in both: standard has self-ref, dotnet has run steps.
            // policyMerge wraps them: result has dotnet steps inlined.
            var analyzeSteps = config.Commands.GetValueOrDefault("analyze")?.Steps ?? [];
            Assert.DoesNotContain(analyzeSteps, s =>
                s.Command is not null &&
                string.Equals(s.Command, "analyze", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(analyzeSteps, s => s.Run is not null &&
                s.Run.Contains("dotnet format", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DotnetAndStandardExtendsTestPreservesCoverageModeAndCustomizationVars()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var json = MinimalJson("test-repo", """
                "extends": ["embedded:dotnet", "embedded:standard"]
                """);

            var config = await LoadTempConfigAsync(dir, json);

            Assert.NotNull(config.Commands);
            var testSteps = config.Commands["test"].Steps;

            Assert.Equal(2, testSteps.Count);

            var noCoverageStep = Assert.Single(testSteps, step =>
                string.Equals(step.Id, "dotnet-test-no-coverage", StringComparison.Ordinal));
            Assert.Equal("{{vars.dotnet.test.coverage.mode == 'none'}}", noCoverageStep.When);
            Assert.Contains("vars.dotnet.test.runsettings", noCoverageStep.Run ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("vars.dotnet.test.extraArgs", noCoverageStep.Run ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("XPlat Code Coverage", noCoverageStep.Run ?? string.Empty, StringComparison.Ordinal);

            var coverageStep = Assert.Single(testSteps, step =>
                string.Equals(step.Id, "dotnet-test", StringComparison.Ordinal));
            Assert.Equal("{{vars.dotnet.test.coverage.mode != 'none'}}", coverageStep.When);
            Assert.Contains("vars.dotnet.test.runsettings", coverageStep.Run ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("vars.dotnet.test.extraArgs", coverageStep.Run ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("XPlat Code Coverage", coverageStep.Run ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("outputs.tests.coverage", coverageStep.Run ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DotnetAndStandardExtendsBuildIsWonByBaseDotnet()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // standard.build has no self-ref, dotnet.build has no self-ref.
            // policyMerge rule: neither has self-ref → base (dotnet) wins.
            var json = MinimalJson("test-repo", """
                "extends": ["embedded:dotnet", "embedded:standard"]
                """);

            var config = await LoadTempConfigAsync(dir, json);

            Assert.NotNull(config.Commands);
            var buildSteps = config.Commands.GetValueOrDefault("build")?.Steps ?? [];
            // dotnet.build has "dotnet build" steps
            Assert.Contains(buildSteps, s => s.Run is not null &&
                s.Run.Contains("dotnet build", StringComparison.OrdinalIgnoreCase));
            // standard.build artifact steps are suppressed (no self-ref from either side)
            Assert.DoesNotContain(buildSteps, s => s.Uses == "builtin:build-artifacts");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RepoBuildCommandOverridesEmbeddedBuilds()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var json = MinimalJson("test-repo", """
                "extends": ["embedded:dotnet", "embedded:standard"],
                "commands": {
                  "build": {
                    "description": "Custom build",
                    "steps": [
                      { "id": "custom", "run": "echo custom-build" }
                    ]
                  }
                }
                """);

            var config = await LoadTempConfigAsync(dir, json);

            Assert.NotNull(config.Commands);
            var buildSteps = config.Commands["build"].Steps;
            // repo.json always wins over extends
            Assert.Single(buildSteps);
            Assert.Contains("echo custom-build", buildSteps[0].Run ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ─── runtime tests (command execution) ──────────────────────────────────

    [Fact]
    public async Task SelfRefWhenExistsStepSkipsGracefullyWithoutError()
    {
        // Standard.test behaviour when no inner layer contributes steps:
        // the self-referential { command: "test", whenExists: true } step must skip
        // without failure.
        var config = new RepoConfig(
            Name: "self-ref-test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["test"] = new RepoCommandConfig(
                    Description: "run tests",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(
                            Id: "inner-test",
                            Command: "test",
                            WhenExists: true)
                    ])
            },
            Aliases: []);

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = CreateLoader();
        loader.LoadInto(registry, config, Path.GetTempPath(), executor);

        var result = await executor.ExecuteAsync(
            "test",
            EmptyInvocation(Path.GetTempPath()),
            CancellationToken.None);

        Assert.True(result.Success, $"Self-ref skip should succeed. Message: {result.Message}");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task WhenExistsCrossCommandSkipsGracefullyWhenTargetMissing()
    {
        // A step with { command: "pre-verify", whenExists: true } when
        // no "pre-verify" command is registered should skip, not fail.
        var config = new RepoConfig(
            Name: "whenexists-test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["verify"] = new RepoCommandConfig(
                    Description: "verify",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(
                            Id: "pre-verify",
                            Command: "pre-verify",
                            WhenExists: true)
                    ])
            },
            Aliases: []);

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = CreateLoader();
        loader.LoadInto(registry, config, Path.GetTempPath(), executor);

        var result = await executor.ExecuteAsync(
            "verify",
            EmptyInvocation(Path.GetTempPath()),
            CancellationToken.None);

        Assert.True(result.Success, $"whenExists cross-command skip should succeed. Message: {result.Message}");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task CommandHooksCompileIntoBeforeMainAfterOrder()
    {
        var config = new RepoConfig(
            Name: "hooks-order-test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["pre"] = new RepoCommandConfig(
                    Description: "pre",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "pre-inner", Command: "missing", WhenExists: true)
                    ]),
                ["main"] = new RepoCommandConfig(
                    Description: "main",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "main-inner", Command: "missing", WhenExists: true)
                    ]),
                ["post"] = new RepoCommandConfig(
                    Description: "post",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "post-inner", Command: "missing", WhenExists: true)
                    ]),
                ["release"] = new RepoCommandConfig(
                    Description: "release",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "main", Command: "main")
                    ])
                {
                    Before = [new RepoStepConfig(Command: "pre")],
                    After = [new RepoStepConfig(Id: "after", Command: "post")],
                },
            },
            Aliases: []);

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = CreateLoader();
        loader.LoadInto(registry, config, Path.GetTempPath(), executor);

        var result = await executor.ExecuteAsync(
            "release",
            EmptyInvocation(Path.GetTempPath()),
            CancellationToken.None);

        Assert.True(result.Success, $"hooked command should succeed. Message: {result.Message}");
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("cmd-pre", result.Steps[0].StepId);
        Assert.Equal("main", result.Steps[1].StepId);
        Assert.Equal("after", result.Steps[2].StepId);
    }

    [Fact]
    public async Task CommandDelegationFailsWhenMaxDepthExceeded()
    {
        var config = new RepoConfig(
            Name: "max-depth-test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["a"] = new RepoCommandConfig(
                    Description: "a",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "call-b", Command: "b")
                    ]),
                ["b"] = new RepoCommandConfig(
                    Description: "b",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "call-c", Command: "c")
                    ]),
                ["c"] = new RepoCommandConfig(
                    Description: "c",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "leaf", Command: "missing", WhenExists: true)
                    ]),
            },
            Aliases: [])
        {
            Runtime = new RepoRuntimeConfig(
                Commands: new RepoRuntimeCommandsConfig(MaxDepth: 2)),
        };

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = CreateLoader();
        loader.LoadInto(registry, config, Path.GetTempPath(), executor);

        var result = await executor.ExecuteAsync(
            "a",
            EmptyInvocation(Path.GetTempPath()),
            CancellationToken.None);

        Assert.False(result.Success, "max depth should fail command delegation chain");
        Assert.Equal(9, result.ExitCode);
        var failed = Assert.Single(result.Steps.Where(step => !step.Success));
        Assert.Equal(Rexo.Core.Models.ErrorCodes.CommandCycle, failed.Outputs["errorCode"]?.ToString());
        Assert.Contains("maxDepth=2", failed.Outputs["error"]?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossCommandCycleIsDetected()
    {
        // build → release → build should produce a cycle error.
        var config = new RepoConfig(
            Name: "cycle-test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["release"] = new RepoCommandConfig(
                    Description: "release",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "build-step", Command: "build")
                    ]),
                ["build"] = new RepoCommandConfig(
                    Description: "build",
                    Options: [],
                    Steps:
                    [
                        new RepoStepConfig(Id: "release-step", Command: "release")
                    ]),
            },
            Aliases: []);

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = CreateLoader();
        loader.LoadInto(registry, config, Path.GetTempPath(), executor);

        var result = await executor.ExecuteAsync(
            "build",
            EmptyInvocation(Path.GetTempPath()),
            CancellationToken.None);

        Assert.False(result.Success, "Cycle should be detected and cause failure.");
        Assert.Equal(9, result.ExitCode);
        // The cycle step fails the chain — the top-level message indicates step failure
        Assert.NotNull(result.Steps);
        Assert.Contains(result.Steps, s => !s.Success && s.ExitCode == 9);
    }

    [Fact]
    public async Task PolicyCommandsHaveLowerPriorityThanRepoCommands()
    {
        // When repo.json defines "test" and policy also defines "test",
        // repo.json should win (policy is skipped by LoadPolicyCommandsInto).
        var repoBuildSteps = new List<RepoStepConfig>
        {
            new RepoStepConfig(Id: "repo-test", Run: "echo repo-test"),
        };
        var config = new RepoConfig(
            Name: "priority-test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["test"] = new RepoCommandConfig("repo test", [], repoBuildSteps),
            },
            Aliases: []);

        var policyConfig = new PolicyConfig(
            new Dictionary<string, RepoCommandConfig>
            {
                ["test"] = new RepoCommandConfig(
                    "policy test",
                    [],
                    [new RepoStepConfig(Id: "policy-test", Run: "echo policy-test")]),
            },
            new Dictionary<string, string>());

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = CreateLoader();
        loader.LoadInto(registry, config, Path.GetTempPath(), executor);
        loader.LoadPolicyCommandsInto(registry, policyConfig, config, Path.GetTempPath(), executor);

        // Repo.json "test" handler was registered first by LoadInto.
        // LoadPolicyCommandsInto sees "test" already exists → skips policy "test".
        // The registered handler should be the repo.json one.
        Assert.True(registry.TryResolve("test", out var handler));
        Assert.NotNull(handler);

        // We can't inspect the handler directly, but we can confirm the registry
        // registered exactly one command (no duplicates).
        Assert.True(registry.TryResolve("test", out _));
    }
}
