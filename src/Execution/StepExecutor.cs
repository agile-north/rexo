namespace Rexo.Execution;

using System.Collections;
using System.Text.RegularExpressions;
using Rexo.Ci;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;

public sealed class StepExecutor : IStepExecutor
{
    private readonly ICommandExecutor _commandExecutor;
    private readonly ITemplateRenderer _templateRenderer;
    private readonly BuiltinRegistry _builtinRegistry;

    public StepExecutor(
        ICommandExecutor commandExecutor,
        ITemplateRenderer templateRenderer,
        BuiltinRegistry builtinRegistry)
    {
        _commandExecutor = commandExecutor;
        _templateRenderer = templateRenderer;
        _builtinRegistry = builtinRegistry;
    }

    public async Task<StepResult> ExecuteAsync(
        StepDefinition stepDefinition,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var stepId = stepDefinition.Id ?? GenerateStepId(stepDefinition);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stepContext = ApplyWithOverrides(stepDefinition, context);

        // Evaluate when condition — skip if condition is falsy
        if (!string.IsNullOrEmpty(stepDefinition.When))
        {
            var condition = _templateRenderer.Render(stepDefinition.When, stepContext);
            if (!IsTruthy(condition))
            {
                sw.Stop();
                return new StepResult(
                    stepId,
                    true,
                    0,
                    sw.Elapsed,
                    new Dictionary<string, object?> { ["skipped"] = "true" });
            }
        }

        StepResult result;

        if (!string.IsNullOrEmpty(stepDefinition.Run))
        {
            result = await ExecuteRunAsync(stepId, stepDefinition.Run, stepDefinition, stepContext, sw, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(stepDefinition.Uses))
        {
            result = await ExecuteUsesAsync(stepId, stepDefinition.Uses, stepDefinition, stepContext, sw, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(stepDefinition.Command))
        {
            result = await ExecuteCommandStepAsync(stepId, stepDefinition, stepContext, sw, cancellationToken);
        }
        else
        {
            sw.Stop();
            result = new StepResult(
                stepId,
                false,
                1,
                sw.Elapsed,
                new Dictionary<string, object?> { ["error"] = "Step has no run, uses, or command." });
        }

        return result;
    }

    private async Task<StepResult> ExecuteRunAsync(
        string stepId,
        string run,
        StepDefinition stepDefinition,
        ExecutionContext context,
        System.Diagnostics.Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var command = _templateRenderer.Render(run, context);
        var secrets = SecretMasker.CollectSecretValues();
        var env = BuildNativeRunEnvironment(context);
        ShellRunResult shellResult;
        var executionMetadata = new RunExecutionMetadata("native", "native", null, null, false, null);
        var debugEnabled = IsDebugEnabled(context);

        if (stepDefinition.Container is { Image.Length: > 0 } container)
        {
            var containerResult = await ExecuteContainerizedRunWithFallbackAsync(
                command,
                container,
                context,
                secrets,
                debugEnabled,
                cancellationToken);
            shellResult = containerResult.Result;
            executionMetadata = containerResult.Metadata;
        }
        else
        {
            Console.WriteLine($"  > [native] {command}");

            shellResult = await ShellRunner.RunAsync(
                command,
                context.RepositoryRoot,
                environment: env,
                onStdout: line => Console.WriteLine($"    {SecretMasker.Mask(line, secrets)}"),
                cancellationToken: cancellationToken);
        }

        sw.Stop();

        if (!string.IsNullOrEmpty(shellResult.Stderr))
        {
            Console.Error.WriteLine(SecretMasker.Mask(shellResult.Stderr, secrets));
        }

        var maskedStdout = SecretMasker.Mask(shellResult.Stdout, secrets);
        var outputs = new Dictionary<string, object?>
        {
            ["stdout"] = maskedStdout,
            ["stderr"] = SecretMasker.Mask(shellResult.Stderr, secrets),
            ["__executionMode"] = executionMetadata.ExecutionMode,
            ["__requestedExecutionMode"] = executionMetadata.RequestedExecutionMode,
            ["__containerImage"] = executionMetadata.ContainerImage,
            ["__containerWorkingDirectory"] = executionMetadata.ContainerWorkingDirectory,
            ["__containerFallbackUsed"] = executionMetadata.FallbackUsed,
            ["__containerFallbackReason"] = executionMetadata.FallbackReason,
        };

        // Extract named groups from stdout via OutputPattern regex
        if (!string.IsNullOrEmpty(stepDefinition.OutputPattern) && !string.IsNullOrEmpty(maskedStdout))
        {
            try
            {
                var match = Regex.Match(maskedStdout, stepDefinition.OutputPattern, RegexOptions.Multiline);
                if (match.Success)
                {
                    foreach (Group group in match.Groups)
                    {
                        if (!int.TryParse(group.Name, out _))
                        {
                            outputs[group.Name] = group.Value;
                        }
                    }
                }
            }
            catch (ArgumentException)
            {
                // invalid regex — skip extraction
            }
        }

        // Write stdout to OutputFile if specified
        if (!string.IsNullOrEmpty(stepDefinition.OutputFile) && !string.IsNullOrEmpty(maskedStdout))
        {
            var outputFilePath = Path.IsPathRooted(stepDefinition.OutputFile)
                ? stepDefinition.OutputFile
                : Path.Combine(context.RepositoryRoot, stepDefinition.OutputFile);
            var dir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputFilePath, maskedStdout, cancellationToken);
            outputs["outputFile"] = outputFilePath;
        }

        return new StepResult(
            stepId,
            shellResult.ExitCode == 0,
            shellResult.ExitCode,
            sw.Elapsed,
            outputs);
    }

    private static async Task<ContainerRunResult> ExecuteContainerizedRunWithFallbackAsync(
        string command,
        StepContainerDefinition container,
        ExecutionContext context,
        IReadOnlySet<string> secrets,
        bool debugEnabled,
        CancellationToken cancellationToken)
    {
        var containerWorkingDirectory = string.IsNullOrWhiteSpace(container.WorkingDirectory)
            ? "/work"
            : container.WorkingDirectory;
        Console.WriteLine($"  > [container] image={container.Image} workdir={containerWorkingDirectory} mount=/work");
        Console.WriteLine($"  > [container:{container.Image}] {command}");

        var dockerArgs = BuildContainerRunArgs(command, container, context);
        if (debugEnabled)
        {
            Console.WriteLine($"[debug] Container invocation: docker {string.Join(" ", dockerArgs.Select(QuoteForDebug))}");
            Console.WriteLine($"[debug] Container env materialization: host+file+runtime+container.env (effective={BuildContainerEnvironment(context, container).Count}, file={context.FileEnvironment.Count}, containerOverrides={(container.Env?.Count ?? 0)})");
        }

        try
        {
            var result = await ShellRunner.RunProcessAsync(
                "docker",
                dockerArgs,
                context.RepositoryRoot,
                onStdout: line => Console.WriteLine($"    {SecretMasker.Mask(line, secrets)}"),
                cancellationToken: cancellationToken);

            return new ContainerRunResult(
                result,
                new RunExecutionMetadata(
                    "container",
                    "container",
                    container.Image,
                    containerWorkingDirectory,
                    false,
                    null));
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("  ! [container] Docker runtime not found; falling back to native execution.");
            Console.WriteLine($"  > [native:fallback] {command}");
            var env = BuildNativeRunEnvironment(context);
            var fallbackResult = await ShellRunner.RunAsync(
                command,
                context.RepositoryRoot,
                environment: env,
                onStdout: line => Console.WriteLine($"    {SecretMasker.Mask(line, secrets)}"),
                cancellationToken: cancellationToken);

            return new ContainerRunResult(
                fallbackResult,
                new RunExecutionMetadata(
                    "native",
                    "container",
                    container.Image,
                    containerWorkingDirectory,
                    true,
                    "docker-not-found"));
        }
    }

    private static IReadOnlyList<string> BuildContainerRunArgs(
        string command,
        StepContainerDefinition container,
        ExecutionContext context)
    {
        var args = new List<string>
        {
            "run",
            "--rm",
            "-v",
            $"{context.RepositoryRoot}:/work",
            "-w",
            string.IsNullOrWhiteSpace(container.WorkingDirectory) ? "/work" : container.WorkingDirectory,
        };

        foreach (var envVar in BuildContainerEnvironment(context, container))
        {
            args.Add("-e");
            args.Add(envVar.Key + "=" + envVar.Value);
        }

        args.Add(container.Image);
        args.Add("/bin/sh");
        args.Add("-c");
        args.Add(command);

        return args;
    }

    private static Dictionary<string, string> BuildContainerEnvironment(
        ExecutionContext context,
        StepContainerDefinition container)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        foreach (var (key, value) in context.FileEnvironment)
        {
            env[key] = value;
        }

        foreach (var (key, value) in BuildRexoStepEnvironment(context))
        {
            env[key] = value;
        }

        if (container.Env is { Count: > 0 })
        {
            foreach (var (key, value) in container.Env)
            {
                env[key] = value;
            }
        }

        return env;
    }

    private static Dictionary<string, string?> BuildNativeRunEnvironment(ExecutionContext context)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in context.FileEnvironment)
        {
            env[key] = value;
        }

        foreach (var (key, value) in BuildRexoStepEnvironment(context))
        {
            env[key] = value;
        }

        return env;
    }

    private static Dictionary<string, string> BuildRexoStepEnvironment(ExecutionContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var commandName = context.CommandCallStack.Count > 0
            ? context.CommandCallStack[^1]
            : string.Empty;

        var manifest = new RunManifest
        {
            RepoName = Path.GetFileName(context.RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar)),
            RepoRoot = context.RepositoryRoot,
            Branch = context.Branch,
            CommitSha = context.CommitSha,
            RemoteUrl = context.RemoteUrl,
            IsCi = context.IsCi,
            CiProvider = context.CiProvider,
            CiBuildId = context.CiBuildId,
            CiRunNumber = context.CiRunNumber,
            CiWorkflowName = context.CiWorkflowName,
            CiActor = context.CiActor,
            CiTag = context.CiTag,
            CiBuildUrl = context.CiBuildUrl,
            CommandExecuted = commandName,
            Success = context.CompletedSteps.Values.All(step => step.Success),
            ExitCode = context.CompletedSteps.Values.LastOrDefault()?.ExitCode ?? 0,
            StartedAt = now,
            CompletedAt = now,
            Version = context.Version,
            Steps = context.CompletedSteps.Values
                .Select(step => new StepManifestEntry(step.StepId, step.Success, step.ExitCode, step.Duration.TotalMilliseconds))
                .ToArray(),
            Artifacts = AggregateArtifacts(context),
            PushDecisions = AggregatePushDecisions(context),
        };

        var payload = CiOutputEmitter.BuildPayload(
            manifest,
            new CiEmissionOptions
            {
                Provider = "generic",
                Prefix = context.CiVariablePrefix,
                KeyCasing = "upperSnake",
                IncludeStepOutputs = true,
                EmitEmptyValues = true,
                Redact = false,
                MaxValueLength = 8192,
                MaxVariables = 1000,
            });

        return new Dictionary<string, string>(payload.Variables, StringComparer.Ordinal);
    }

    private static IReadOnlyList<PushDecision> AggregatePushDecisions(ExecutionContext context)
    {
        var decisions = new List<PushDecision>();
        foreach (var step in context.CompletedSteps.Values)
        {
            if (!step.Outputs.TryGetValue("__pushDecisions", out var decisionsObj) || decisionsObj is null)
            {
                continue;
            }

            if (decisionsObj is IEnumerable<PushDecision> stepDecisions)
            {
                foreach (var decision in stepDecisions)
                {
                    decisions.Add(decision);
                }
            }
        }

        return decisions;
    }

    private static IReadOnlyList<ArtifactManifestEntry> AggregateArtifacts(ExecutionContext context)
    {
        var artifacts = new List<ArtifactManifestEntry>();
        foreach (var step in context.CompletedSteps.Values)
        {
            if (!step.Outputs.TryGetValue("__artifacts", out var artifactsObj) || artifactsObj is null)
            {
                continue;
            }

            if (artifactsObj is IEnumerable<ArtifactManifestEntry> stepArtifacts)
            {
                foreach (var artifact in stepArtifacts)
                {
                    artifacts.Add(artifact);
                }
            }
        }

        return artifacts;
    }

    private async Task<StepResult> ExecuteUsesAsync(
        string stepId,
        string uses,
        StepDefinition stepDefinition,
        ExecutionContext context,
        System.Diagnostics.Stopwatch sw,
        CancellationToken cancellationToken)
    {
        if (_builtinRegistry.TryResolve(uses, out var builtin) && builtin is not null)
        {
            var result = await builtin(stepDefinition, context, cancellationToken);
            sw.Stop();
            return result with { StepId = stepId, Duration = sw.Elapsed };
        }

        sw.Stop();
        return new StepResult(
            stepId,
            false,
            1,
            sw.Elapsed,
            new Dictionary<string, object?> { ["error"] = $"Unknown builtin: '{uses}'" });
    }

    private async Task<StepResult> ExecuteCommandStepAsync(
        string stepId,
        StepDefinition stepDefinition,
        ExecutionContext context,
        System.Diagnostics.Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var commandName = stepDefinition.Command ?? string.Empty;
        var currentCommandName = context.CommandCallStack.Count > 0
            ? context.CommandCallStack[context.CommandCallStack.Count - 1]
            : "";
        var isCurrentCommand = string.Equals(commandName, currentCommandName, StringComparison.OrdinalIgnoreCase);

        // Same-name continuation check: if this step calls the same command as the currently executing one,
        // it's a layer continuation marker (e.g., inside test, calling {command: "test"}).
        // This is allowed and should continue to lower layers (if any).
        // Different-name calls always start fresh from the top layer of the target command.

        // Self-referential continuation step (whenExists=true): this is a layer composition
        // marker that was NOT expanded at compile time (no inner layers contributed steps).
        // Skip gracefully — there is no inner-layer content.
        if (stepDefinition.WhenExists && isCurrentCommand)
        {
            sw.Stop();
            return new StepResult(
                stepId,
                true,
                0,
                sw.Elapsed,
                new Dictionary<string, object?>
                {
                    ["skipped"] = true,
                    ["skipReason"] = "no-layer-content",
                    ["command"] = commandName,
                });
        }

        // Cross-command cycle detection: detect cycles like build -> release -> build.
        // Same-name continuations are allowed (test -> test is valid); only different-name cycles are errors.
        if (!isCurrentCommand && context.CommandCallStack.Contains(commandName, StringComparer.OrdinalIgnoreCase))
        {
            sw.Stop();
            var cyclePath = string.Join(" -> ", context.CommandCallStack.Append(commandName));
            return new StepResult(
                stepId,
                false,
                9,
                sw.Elapsed,
                new Dictionary<string, object?>
                {
                    ["error"] = $"REXO-CMD-CYCLE: Circular command reference detected\n\nPath:\n  {cyclePath}",
                    ["errorCode"] = Rexo.Core.Models.ErrorCodes.CommandCycle,
                });
        }

        var invocation = new CommandInvocation(
            context.Args,
            context.Options,
            false,
            null,
            context.RepositoryRoot)
        {
            CallStack = context.CommandCallStack,
        };

        var result = await _commandExecutor.ExecuteAsync(commandName, invocation, cancellationToken);

        if (stepDefinition.WhenExists && IsCommandMissingResult(result))
        {
            sw.Stop();
            return new StepResult(
                stepId,
                true,
                0,
                sw.Elapsed,
                new Dictionary<string, object?>
                {
                    ["message"] = $"Skipping optional command '{commandName}' because it does not exist.",
                    ["skipped"] = true,
                    ["skipReason"] = "command-not-found",
                    ["command"] = commandName,
                });
        }

        sw.Stop();

        var outputs = new Dictionary<string, object?> { ["message"] = result.Message };
        if (result.Version is not null)
        {
            outputs["__version"] = result.Version;
        }

        if (result.Artifacts.Count > 0)
        {
            outputs["__artifacts"] = result.Artifacts.ToList();
        }

        if (result.PushDecisions.Count > 0)
        {
            outputs["__pushDecisions"] = result.PushDecisions.ToList();
        }

        return new StepResult(
            stepId,
            result.Success,
            result.ExitCode,
            sw.Elapsed,
            outputs);
    }

    private static bool IsCommandMissingResult(CommandResult result) =>
        result.ExitCode == 8 &&
        !string.IsNullOrEmpty(result.Message) &&
        result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value == "1" ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(value) &&
         !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
         value != "0" &&
         !value.Equals("no", StringComparison.OrdinalIgnoreCase));

    private ExecutionContext ApplyWithOverrides(StepDefinition stepDefinition, ExecutionContext context)
    {
        if (stepDefinition.With is not { Count: > 0 })
        {
            return context;
        }

        var mergedOptions = new Dictionary<string, string?>(context.Options, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, templateValue) in stepDefinition.With)
        {
            mergedOptions[key] = _templateRenderer.Render(templateValue, context);
        }

        return context with { Options = mergedOptions };
    }

    private static string GenerateStepId(StepDefinition step)
    {
        if (!string.IsNullOrEmpty(step.Run)) return $"run-{Sanitize(step.Run)}";
        if (!string.IsNullOrEmpty(step.Uses)) return $"uses-{Sanitize(step.Uses)}";
        if (!string.IsNullOrEmpty(step.Command)) return $"cmd-{Sanitize(step.Command)}";
        return Guid.NewGuid().ToString("N")[..8];
    }

    private static string Sanitize(string value)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
                value.ToLowerInvariant().Replace("builtin:", ""),
                @"[^a-z0-9]+",
                "-")
            .Trim('-');

        if (sanitized.Length == 0)
        {
            return "step";
        }

        return sanitized[..Math.Min(20, sanitized.Length)];
    }

    private static bool IsDebugEnabled(ExecutionContext context) =>
        context.Options.TryGetValue("debug", out var debugValue) &&
        string.Equals(debugValue, "true", StringComparison.OrdinalIgnoreCase);

    private static string QuoteForDebug(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record ContainerRunResult(ShellRunResult Result, RunExecutionMetadata Metadata);

    private sealed record RunExecutionMetadata(
        string ExecutionMode,
        string RequestedExecutionMode,
        string? ContainerImage,
        string? ContainerWorkingDirectory,
        bool FallbackUsed,
        string? FallbackReason);
}
