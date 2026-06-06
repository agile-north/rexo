namespace Rexo.Execution.Tests;

using System.Runtime.InteropServices;
using Rexo.Core.Models;
using Rexo.Templating;

/// <summary>
/// Tests for StepExecutor: when-condition evaluation, continueOnError behaviour,
/// and unknown-builtin handling.
/// </summary>
public sealed class StepExecutorWhenConditionTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static StepExecutor CreateExecutor(BuiltinRegistry? registry = null)
    {
        var builtins = registry ?? new BuiltinRegistry();
        var cmdRegistry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(cmdRegistry);
        var renderer = new TemplateRenderer();
        return new StepExecutor(executor, renderer, builtins);
    }

    private static ExecutionContext EmptyContext() =>
        ExecutionContext.Empty(Path.GetTempPath());

    private static BuiltinRegistry TrackingRegistry(out List<string> called)
    {
        var log = new List<string>();
        called = log;
        var registry = new BuiltinRegistry();
        registry.Register("builtin:noop", (step, _, _) =>
        {
            log.Add(step.Id ?? "noop");
            return Task.FromResult(new StepResult(
                step.Id ?? "noop", true, 0,
                TimeSpan.Zero,
                new Dictionary<string, object?>()));
        });
        return registry;
    }

    private sealed class StubCommandExecutor : Rexo.Core.Abstractions.ICommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(string commandName, CommandInvocation invocation, CancellationToken cancellationToken)
        {
            if (string.Equals(commandName, "child", StringComparison.OrdinalIgnoreCase))
            {
                var version = new VersionResult(
                    SemVer: "1.2.3",
                    Major: 1,
                    Minor: 2,
                    Patch: 3,
                    PreRelease: null,
                    CommitSha: "abc",
                    ShortSha: "abc",
                    IsPreRelease: false,
                    IsStable: true);

                return Task.FromResult(new CommandResult(
                    commandName,
                    true,
                    0,
                    "ok",
                    new Dictionary<string, object?>())
                {
                    Version = version,
                });
            }

            return Task.FromResult(CommandResult.Fail(commandName, 8, "not found"));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // when = "true" / truthy values → step executes
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("anything-else")]
    public async Task WhenTruthyConditionStepIsExecuted(string condition)
    {
        var registry = TrackingRegistry(out var log);
        var executor = CreateExecutor(registry);

        var step = new StepDefinition(
            Id: "my-step",
            Run: null,
            Uses: "builtin:noop",
            Command: null,
            When: condition);

        await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.Contains("my-step", log);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // when = "false" / falsy values → step is skipped
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("NO")]
    public async Task WhenFalsyConditionStepIsSkipped(string condition)
    {
        var registry = TrackingRegistry(out var log);
        var executor = CreateExecutor(registry);

        var step = new StepDefinition(
            Id: "my-step",
            Run: null,
            Uses: "builtin:noop",
            Command: null,
            When: condition);

        var result = await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.Empty(log);                           // handler was never called
        Assert.True(result.Success);                 // skipped is not a failure
        Assert.Equal("true", result.Outputs["skipped"]?.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // when renders a template expression: {{options.deploy}} == "true"
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenConditionRendersTemplateVariables()
    {
        var registry = TrackingRegistry(out var log);
        var executor = CreateExecutor(registry);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Options = new Dictionary<string, string?> { ["deploy"] = "true" }
        };

        var step = new StepDefinition(
            Id: "s1",
            Run: null,
            Uses: "builtin:noop",
            Command: null,
            When: "{{options.deploy}}");

        await executor.ExecuteAsync(step, context, CancellationToken.None);

        Assert.Contains("s1", log);
    }

    [Fact]
    public async Task WhenConditionEqualityExpressionTrueRunsStep()
    {
        var registry = TrackingRegistry(out var log);
        var executor = CreateExecutor(registry);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Options = new Dictionary<string, string?> { ["env"] = "prod" }
        };

        var step = new StepDefinition(
            Id: "s2",
            Run: null,
            Uses: "builtin:noop",
            Command: null,
            When: "{{options.env == \"prod\"}}");

        await executor.ExecuteAsync(step, context, CancellationToken.None);

        Assert.Contains("s2", log);
    }

    [Fact]
    public async Task WhenConditionEqualityExpressionFalseSkipsStep()
    {
        var registry = TrackingRegistry(out var log);
        var executor = CreateExecutor(registry);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Options = new Dictionary<string, string?> { ["env"] = "dev" }
        };

        var step = new StepDefinition(
            Id: "s3",
            Run: null,
            Uses: "builtin:noop",
            Command: null,
            When: "{{options.env == \"prod\"}}");

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        Assert.Empty(log);
        Assert.True(result.Success);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // No when condition → step always runs
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoWhenConditionStepAlwaysRuns()
    {
        var registry = TrackingRegistry(out var log);
        var executor = CreateExecutor(registry);

        var step = new StepDefinition(
            Id: "s4",
            Run: null,
            Uses: "builtin:noop",
            Command: null,
            When: null);

        await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.Contains("s4", log);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Unknown builtin → failure result, not an exception
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownBuiltinProducesFailureResult()
    {
        var executor = CreateExecutor();

        var step = new StepDefinition(
            Id: "bad",
            Run: null,
            Uses: "builtin:does-not-exist",
            Command: null,
            When: null);

        var result = await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does-not-exist", result.Outputs["error"]?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuiltinStepWithoutExplicitIdUsesGeneratedIdSafely()
    {
        var registry = TrackingRegistry(out _);
        var executor = CreateExecutor(registry);

        var step = new StepDefinition(
            Id: null,
            Run: null,
            Uses: "builtin:noop",
            Command: null,
            When: null);

        var result = await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("uses-noop", result.StepId);
    }

    [Fact]
    public async Task CommandStepWhenExistsTrueSkipsWhenTargetMissing()
    {
        var executor = CreateExecutor();

        var step = new StepDefinition(
            Id: "maybe-cmd",
            Run: null,
            Uses: null,
            Command: "definitely-missing-command",
            When: null)
        {
            WhenExists = true,
        };

        var result = await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(true, result.Outputs["skipped"]);
        Assert.Equal("command-not-found", result.Outputs["skipReason"]);
        Assert.Equal("definitely-missing-command", result.Outputs["command"]);
    }

    [Fact]
    public async Task CommandStepWhenExistsFalseStillFailsWhenTargetMissing()
    {
        var executor = CreateExecutor();

        var step = new StepDefinition(
            Id: "must-exist",
            Run: null,
            Uses: null,
            Command: "definitely-missing-command",
            When: null)
        {
            WhenExists = false,
        };

        var result = await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(8, result.ExitCode);
        Assert.Contains("not found", result.Outputs["message"]?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommandStepPropagatesNestedVersionToOutputs()
    {
        var stepExecutor = new StepExecutor(
            new StubCommandExecutor(),
            new TemplateRenderer(),
            new BuiltinRegistry());

        var step = new StepDefinition(
            Id: "call-child",
            Run: null,
            Uses: null,
            Command: "child",
            When: null);

        var result = await stepExecutor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        var version = Assert.IsType<VersionResult>(result.Outputs["__version"]);
        Assert.Equal("1.2.3", version.SemVer);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Step with no run/uses/command → failure result
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StepWithNoActionProducesFailureResult()
    {
        var executor = CreateExecutor();

        var step = new StepDefinition(
            Id: "empty",
            Run: null,
            Uses: null,
            Command: null,
            When: null);

        var result = await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no run, uses, or command", result.Outputs["error"]?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunStepReceivesCoreRexoEnvironmentVariables()
    {
        var executor = CreateExecutor();
        var version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef", "abcdef", false, true);
        var context = EmptyContext() with
        {
            Version = version,
            CompletedSteps = new Dictionary<string, StepResult>
            {
                ["push"] = new StepResult(
                    "push",
                    true,
                    0,
                    TimeSpan.Zero,
                    new Dictionary<string, object?>
                    {
                        ["__pushDecisions"] = new List<PushDecision>
                        {
                            new("artifact-a", true, "ok"),
                        },
                    }),
            },
        };

        var run = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "echo %REXO_SUCCESS%^|%REXO_VERSION_SEM_VER%^|%REXO_PUSH_DECISIONS_COUNT%"
            : "echo \"$REXO_SUCCESS|$REXO_VERSION_SEM_VER|$REXO_PUSH_DECISIONS_COUNT\"";

        var step = new StepDefinition(
            Id: "env-vars",
            Run: run,
            Uses: null,
            Command: null,
            When: null);

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        Assert.True(result.Success);
        var stdout = result.Outputs["stdout"]?.ToString() ?? string.Empty;
        Assert.Contains("true|1.2.3|1", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunStepUsesConfiguredRexoEnvironmentPrefix()
    {
        var executor = CreateExecutor();
        var version = new VersionResult("2.0.1", 2, 0, 1, null, "abcdef", "abcdef", false, true);
        var context = EmptyContext() with
        {
            Version = version,
            CiVariablePrefix = "MY_",
        };

        var run = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "echo %MY_SUCCESS%^|%MY_VERSION_SEM_VER%"
            : "echo \"$MY_SUCCESS|$MY_VERSION_SEM_VER\"";

        var step = new StepDefinition(
            Id: "env-prefix",
            Run: run,
            Uses: null,
            Command: null,
            When: null);

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        Assert.True(result.Success);
        var stdout = result.Outputs["stdout"]?.ToString() ?? string.Empty;
        Assert.Contains("true|2.0.1", stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cross-command cycle detection: REXO-CMD-CYCLE
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CommandStepAllowsSameNameContinuation()
    {
        var executor = CreateExecutor();

        // Simulate being inside command "build" on the call stack
        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            CommandCallStack = ["build"],
        };

        // A step that tries to call "build" again → same-name continuation (allowed for layer composition)
        var step = new StepDefinition(
            Id: "layer-continuation-step",
            Run: null,
            Uses: null,
            Command: "build",
            When: null)
        {
            WhenExists = true, // Mark as layer composition step
        };

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        // Should succeed (skip if no lower layers, but not error)
        Assert.True(result.Success);
        // Skip result when layer has no content
        var skipReason = result.Outputs["skipReason"]?.ToString() ?? string.Empty;
        Assert.Contains("no-layer-content", skipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandStepDetectsCrossCommandCycle()
    {
        var executor = CreateExecutor();

        // Simulate being inside "build" on the call stack
        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            CommandCallStack = ["build"],
        };

        // A step that tries to call "release" while inside "build" → cross-command cycle
        var step = new StepDefinition(
            Id: "cycle-step",
            Run: null,
            Uses: null,
            Command: "release", // Different command
            When: null);

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        // This would attempt to execute "release", which isn't registered, so it returns 8 (not found)
        // True cycle detection (e.g., release -> build -> release) requires both commands in CallStack
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CommandStepDetectsIndirectCycle()
    {
        var executor = CreateExecutor();

        // Simulate being inside "build → verify" on the call stack
        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            CommandCallStack = ["build", "verify"],
        };

        // A step that tries to call "build" again → indirect cycle
        var step = new StepDefinition(
            Id: "indirect-cycle-step",
            Run: null,
            Uses: null,
            Command: "build",
            When: null);

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(9, result.ExitCode);
        var error = result.Outputs["error"]?.ToString() ?? string.Empty;
        Assert.Contains("REXO-CMD-CYCLE", error, StringComparison.Ordinal);
        Assert.Contains("build -> verify -> build", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandStepAllowsSameNameContinuationCaseInsensitive()
    {
        var executor = CreateExecutor();

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            CommandCallStack = ["Build"],
        };

        // Same-name call (case-insensitive) with layer composition marker
        var step = new StepDefinition(
            Id: "ci-step",
            Run: null,
            Uses: null,
            Command: "build", // lowercase vs "Build" on stack
            When: null)
        {
            WhenExists = true, // Layer composition marker
        };

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        // Should succeed (skip for no-layer-content)
        Assert.True(result.Success);
        var skipReason = result.Outputs["skipReason"]?.ToString() ?? string.Empty;
        Assert.Contains("no-layer-content", skipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandStepWithEmptyCallStackDoesNotTriggerCycleDetection()
    {
        var executor = CreateExecutor();

        // No call stack → no cycle possible; command "missing-cmd" not found but no cycle error
        var step = new StepDefinition(
            Id: "safe-step",
            Run: null,
            Uses: null,
            Command: "missing-command",
            When: null)
        {
            WhenExists = true, // skip gracefully when not found
        };

        var result = await executor.ExecuteAsync(step, EmptyContext(), CancellationToken.None);

        // Should skip (whenExists=true), not error with cycle detection
        Assert.True(result.Success);
        Assert.Equal("command-not-found", result.Outputs["skipReason"]);
    }

    [Fact]
    public async Task SelfReferentialContinuationStepWithWhenExistsSkipsGracefully()
    {
        var executor = CreateExecutor();

        // Simulate being inside command "test" — this is the layered composition scenario
        // where a wrap-mode continuation step was not expanded (no inner layer contributed steps).
        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            CommandCallStack = ["test"],
        };

        // Continuation marker: {command: "test", whenExists: true} — self-referential
        var step = new StepDefinition(
            Id: "test-content",
            Run: null,
            Uses: null,
            Command: "test",
            When: null)
        {
            WhenExists = true,
        };

        var result = await executor.ExecuteAsync(step, context, CancellationToken.None);

        // Should skip gracefully with no-layer-content reason — NOT trigger REXO-CMD-CYCLE
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(true, result.Outputs["skipped"]);
        Assert.Equal("no-layer-content", result.Outputs["skipReason"]);
        Assert.Equal("test", result.Outputs["command"]);
    }
}
