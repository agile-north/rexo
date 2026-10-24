namespace Rexo.Execution;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rexo.Ci;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;

public sealed class StepExecutor : IStepExecutor, IDisposable
{
    private const string ContainerSourceHashLabel = "rexo.container.sourceHash";
    private const string DockerCommandEnvVar = "REXO_DOCKER_COMMAND";

    private readonly ICommandExecutor _commandExecutor;
    private readonly ITemplateRenderer _templateRenderer;
    private readonly BuiltinRegistry _builtinRegistry;
    private readonly ISecretResolver? _secretResolver;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);

    public StepExecutor(
        ICommandExecutor commandExecutor,
        ITemplateRenderer templateRenderer,
        BuiltinRegistry builtinRegistry,
        ISecretResolver? secretResolver = null)
    {
        _commandExecutor = commandExecutor;
        _templateRenderer = templateRenderer;
        _builtinRegistry = builtinRegistry;
        _secretResolver = secretResolver;
    }

    public void Dispose() => _credentialGate.Dispose();

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
            var builtinContext = stepContext with
            {
                CliEchoMode = ResolveEchoMode(stepDefinition.Echo, stepContext),
            };
            result = await ExecuteUsesAsync(stepId, stepDefinition.Uses, stepDefinition, builtinContext, sw, cancellationToken);
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
        var runtimeSecrets = context.ResolvedSecrets.Values
            .Concat(context.MappedSecretEnvironment.Values);
        var secrets = new HashSet<string>(SecretMasker.CollectSecretValues(runtimeSecrets), StringComparer.Ordinal);
        ShellRunResult shellResult;
        var executionMetadata = new RunExecutionMetadata("native", "native", null, null, false, null, false, null);
        var debugEnabled = IsDebugEnabled(context);
        var echoMode = ResolveEchoMode(stepDefinition.Echo, context);
        var displayStepId = CreateDisplayStepId(stepDefinition, secrets);

        if (stepDefinition.Container is { Image.Length: > 0 } container)
        {
            var containerResult = await ExecuteContainerizedRunWithFallbackAsync(
                displayStepId,
                command,
                container,
                stepDefinition,
                context,
                secrets,
                debugEnabled,
                echoMode,
                cancellationToken);
            shellResult = containerResult.Result;
            executionMetadata = containerResult.Metadata;
        }
        else
        {
            if (echoMode != "none")
            {
                Console.WriteLine($"  Step: {displayStepId}");
                Console.WriteLine("  Execution: native");
            }

            if (echoMode == "full")
            {
                Console.WriteLine($"  > [native] {SecretMasker.Mask(command, secrets)}");
            }

            shellResult = await RunNativeAsync(
                command,
                stepDefinition,
                context,
                secrets,
                cancellationToken);
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
            ["__containerFallbackPolicy"] = executionMetadata.FallbackPolicy,
            ["__containerFallbackAllowed"] = executionMetadata.FallbackAllowed,
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
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(
                    $"  Warning: could not extract outputs for step '{stepId}': {SecretMasker.Mask(ex.Message, secrets)}");
            }
        }

        // Write stdout to OutputFile if specified
        if (!string.IsNullOrEmpty(stepDefinition.OutputFile) && !string.IsNullOrEmpty(maskedStdout))
        {
            var renderedOutputFile = _templateRenderer.Render(stepDefinition.OutputFile, context);
            var outputFilePath = Path.GetFullPath(
                Path.IsPathRooted(renderedOutputFile)
                    ? renderedOutputFile
                    : Path.Join(context.RepositoryRoot, renderedOutputFile));
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

    private async Task<ShellRunResult> RunNativeAsync(
        string command,
        StepDefinition step,
        ExecutionContext context,
        HashSet<string> secrets,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = RenderIdentityField(step.User, "user", context);
            var domain = RenderIdentityField(step.Domain, "domain", context);
            var passwordSecret = RenderIdentityField(step.PasswordSecret, "passwordSecret", context);
            string? password = null;
            if (user is null && (domain is not null || passwordSecret is not null))
            {
                throw new InvalidOperationException("Host domain/passwordSecret require a user.");
            }

            if (!OperatingSystem.IsWindows() && (domain is not null || passwordSecret is not null))
            {
                throw new PlatformNotSupportedException("Host domain and passwordSecret are supported only on Windows.");
            }

            if (OperatingSystem.IsWindows() && user is not null)
            {
                if (passwordSecret is null || _secretResolver is null)
                {
                    throw new InvalidOperationException("Windows host user execution requires a passwordSecret referencing secrets.items.");
                }

                // The resolver maintains mutable provider/cache state shared by parallel steps.
                await _credentialGate.WaitAsync(cancellationToken);
                try
                {
                    password = await _secretResolver.GetSecretValueAsync(passwordSecret, cancellationToken);
                }
                finally
                {
                    _credentialGate.Release();
                }

                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException($"Host password secret '{passwordSecret}' is undefined, empty, or could not be resolved.");
                }

                secrets.Add(password);
            }

            return await ShellRunner.RunAsync(
                command,
                context.RepositoryRoot,
                environment: BuildNativeRunEnvironment(context),
                onStdout: line => Console.WriteLine($"    {SecretMasker.Mask(line, secrets)}"),
                cancellationToken: cancellationToken,
                user: user,
                domain: domain,
                password: password);
        }
        catch (Exception ex) when (
            (step.User is not null || step.Domain is not null || step.PasswordSecret is not null) &&
            ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new ShellRunResult(1, string.Empty, SecretMasker.Mask($"Host user execution failed: {ex.Message}", secrets));
        }
    }

    private string? RenderIdentityField(string? value, string name, ExecutionContext context)
    {
        if (value is null)
        {
            return null;
        }

        var rendered = _templateRenderer.Render(value, context);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            throw new InvalidOperationException($"Host {name} must not be empty or whitespace.");
        }

        return rendered;
    }

    private async Task<ContainerRunResult> ExecuteContainerizedRunWithFallbackAsync(
        string stepId,
        string command,
        StepContainerDefinition container,
        StepDefinition step,
        ExecutionContext context,
        HashSet<string> secrets,
        bool debugEnabled,
        string echoMode,
        CancellationToken cancellationToken)
    {
        if (container.User is not null && string.IsNullOrWhiteSpace(container.User))
        {
            return new ContainerRunResult(
                new ShellRunResult(1, string.Empty, "Container user must not be empty or whitespace."),
                new RunExecutionMetadata("container", "container", container.Image, container.WorkingDirectory, false, container.Fallback, false, null));
        }

        var containerWorkingDirectory = string.IsNullOrWhiteSpace(container.WorkingDirectory)
            ? "/work"
            : container.WorkingDirectory;

        if (echoMode != "none")
        {
            Console.WriteLine($"  Step: {SecretMasker.Mask(stepId, secrets)}");
            Console.WriteLine($"  Container: {SecretMasker.Mask(container.Image, secrets)}");
        }

        if (echoMode == "full")
        {
            Console.WriteLine($"  > [container:{SecretMasker.Mask(container.Image, secrets)}] {SecretMasker.Mask(command, secrets)}");
        }

        try
        {
            var prepareResult = await EnsureContainerImageAsync(
                container,
                context,
                secrets,
                debugEnabled,
                echoMode,
                cancellationToken);

            if (prepareResult is not null)
            {
                if (IsDockerRuntimeUnavailable(prepareResult.Stderr))
                {
                    return await HandleContainerRuntimeUnavailableAsync(
                        stepId,
                        command,
                        container,
                        step,
                        context,
                        secrets,
                        containerWorkingDirectory,
                        "docker-daemon-unavailable",
                        prepareResult.Stderr,
                        echoMode,
                        cancellationToken);
                }

                return new ContainerRunResult(
                    prepareResult,
                    new RunExecutionMetadata(
                        "container",
                        "container",
                        container.Image,
                        containerWorkingDirectory,
                        container.Fallback == "host",
                        container.Fallback,
                        false,
                        "container-image-prepare-failed"));
            }

            var containerEnvironment = BuildContainerEnvironment(context, container);
            var dockerArgs = BuildContainerRunArgs(command, container, context, containerEnvironment);
            if (debugEnabled)
            {
                Console.WriteLine($"[debug] Container invocation: docker {string.Join(" ", dockerArgs.Select(arg => QuoteForDebug(SecretMasker.Mask(arg, secrets))))}");
                Console.WriteLine($"[debug] Container env materialization: host+file+runtime+container.env (effective={containerEnvironment.Count}, file={context.FileEnvironment.Count}, containerOverrides={(container.Env?.Count ?? 0)})");
            }

            var result = await ShellRunner.RunProcessAsync(
                ResolveDockerCommand(),
                dockerArgs,
                context.RepositoryRoot,
                onStdout: line => Console.WriteLine($"    {SecretMasker.Mask(line, secrets)}"),
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0 && IsDockerRuntimeUnavailable(result.Stderr))
            {
                return await HandleContainerRuntimeUnavailableAsync(
                    stepId,
                    command,
                    container,
                    step,
                    context,
                    secrets,
                    containerWorkingDirectory,
                    "docker-daemon-unavailable",
                    result.Stderr,
                    echoMode,
                    cancellationToken);
            }

            return new ContainerRunResult(
                result,
                new RunExecutionMetadata(
                    "container",
                    "container",
                    container.Image,
                    containerWorkingDirectory,
                    container.Fallback == "host",
                    container.Fallback,
                    false,
                    null));
        }
        catch (FileNotFoundException)
        {
            return await HandleContainerRuntimeUnavailableAsync(
                stepId,
                command,
                container,
                step,
                context,
                secrets,
                containerWorkingDirectory,
                "docker-not-found",
                "Docker executable was not found.",
                echoMode,
                cancellationToken);
        }
    }

    private async Task<ContainerRunResult> HandleContainerRuntimeUnavailableAsync(
        string stepId,
        string command,
        StepContainerDefinition container,
        StepDefinition step,
        ExecutionContext context,
        HashSet<string> secrets,
        string containerWorkingDirectory,
        string reason,
        string runtimeError,
        string echoMode,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("[container] Docker runtime unavailable.");
        Console.Error.WriteLine($"[container] fallback policy: {container.Fallback}");
        if (container.Fallback != "host")
        {
            var message =
                $"Container execution is required for step '{stepId}' (image '{container.Image}'). " +
                $"{SecretMasker.Mask(runtimeError, secrets)} " +
                "Install or start Docker, or explicitly set fallback: host.";
            Console.Error.WriteLine($"[error] {message}");
            return new ContainerRunResult(
                new ShellRunResult(127, string.Empty, message),
                new RunExecutionMetadata(
                    "container",
                    "container",
                    container.Image,
                    containerWorkingDirectory,
                    false,
                    container.Fallback,
                    false,
                    reason));
        }

        if (echoMode == "full")
        {
            Console.WriteLine($"  > [native:fallback] {SecretMasker.Mask(command, secrets)}");
        }
        var fallbackResult = await RunNativeAsync(
            command,
            step,
            context,
            secrets,
            cancellationToken);

        return new ContainerRunResult(
            fallbackResult,
            new RunExecutionMetadata(
                "native",
                "container",
                container.Image,
                containerWorkingDirectory,
                true,
                container.Fallback,
                true,
                reason));
    }

    private static bool IsDockerRuntimeUnavailable(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.StartsWith("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || error.StartsWith("error during connect:", StringComparison.OrdinalIgnoreCase)
            || (error.Contains("docker_engine", StringComparison.OrdinalIgnoreCase)
                && error.Contains("The system cannot find the file specified", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> BuildContainerRunArgs(
        string command,
        StepContainerDefinition container,
        ExecutionContext context,
        IReadOnlyDictionary<string, string> containerEnvironment)
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

        if (container.User is not null)
        {
            args.Add("--user");
            args.Add(container.User);
        }

        if (!string.IsNullOrWhiteSpace(container.Entrypoint))
        {
            args.Add("--entrypoint");
            args.Add(container.Entrypoint);
        }

        foreach (var envVar in containerEnvironment)
        {
            args.Add("-e");
            args.Add(envVar.Key + "=" + envVar.Value);
        }

        args.Add(container.Image);

        if (!string.IsNullOrWhiteSpace(container.Entrypoint))
        {
            // When entrypoint is explicitly overridden, pass the rendered run command as one
            // argument to the entrypoint to avoid hidden shell behavior.
            args.Add(command);
        }
        else
        {
            args.Add("/bin/sh");
            args.Add("-c");
            args.Add(command);
        }

        return args;
    }

    private static async Task<ShellRunResult?> EnsureContainerImageAsync(
        StepContainerDefinition container,
        ExecutionContext context,
        IReadOnlySet<string> secrets,
        bool debugEnabled,
        string echoMode,
        CancellationToken cancellationToken)
    {
        if (container.InlineDockerfile is null)
        {
            if (string.IsNullOrWhiteSpace(container.Dockerfile))
            {
                return null;
            }

            return await EnsureContainerImageFromPathAsync(
                container,
                context,
                secrets,
                debugEnabled,
                echoMode,
                inlineSource: false,
                cancellationToken);
        }

        var temporaryDockerfilePath = Path.Join(
            Path.GetTempPath(),
            $"rexo-{Guid.NewGuid():N}.Dockerfile");
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var file = new FileStream(temporaryDockerfilePath, fileOptions))
            {
                var contents = System.Text.Encoding.UTF8.GetBytes(container.InlineDockerfile);
                await file.WriteAsync(contents, cancellationToken);
            }

            return await EnsureContainerImageFromPathAsync(
                container with { Dockerfile = temporaryDockerfilePath, InlineDockerfile = null },
                context,
                secrets,
                debugEnabled,
                echoMode,
                inlineSource: true,
                cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(temporaryDockerfilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"[warn] Could not remove temporary Dockerfile '{temporaryDockerfilePath}': {SecretMasker.Mask(ex.Message, secrets)}");
            }
        }
    }

    private static async Task<ShellRunResult?> EnsureContainerImageFromPathAsync(
        StepContainerDefinition container,
        ExecutionContext context,
        IReadOnlySet<string> secrets,
        bool debugEnabled,
        string echoMode,
        bool inlineSource,
        CancellationToken cancellationToken)
    {
        var dockerfilePath = Path.IsPathRooted(container.Dockerfile)
            ? container.Dockerfile
            : Path.GetFullPath(Path.Join(context.RepositoryRoot, container.Dockerfile));
        var buildContextPath = string.IsNullOrWhiteSpace(container.Context)
            ? context.RepositoryRoot
            : (Path.IsPathRooted(container.Context)
                ? container.Context
                : Path.GetFullPath(Path.Join(context.RepositoryRoot, container.Context)));

        if (!File.Exists(dockerfilePath))
        {
            return new ShellRunResult(
                2,
                string.Empty,
                $"Container dockerfile not found: '{dockerfilePath}'.");
        }

        if (!Directory.Exists(buildContextPath))
        {
            return new ShellRunResult(
                2,
                string.Empty,
                $"Container build context directory not found: '{buildContextPath}'.");
        }

        var sourceHash = await ComputeContainerSourceHashAsync(
            dockerfilePath,
            buildContextPath,
            container.Build,
            inlineSource,
            cancellationToken);
        var inspectArgs = new List<string>
        {
            "image",
            "inspect",
            container.Image,
            "--format",
            $"{{{{ index .Config.Labels \"{ContainerSourceHashLabel}\" }}}}",
        };

        if (debugEnabled)
        {
            Console.WriteLine($"[debug] Container image inspect: docker {string.Join(" ", inspectArgs.Select(arg => QuoteForDebug(SecretMasker.Mask(arg, secrets))))}");
        }

        var inspectResult = await ShellRunner.RunProcessAsync(
            ResolveDockerCommand(),
            inspectArgs,
            context.RepositoryRoot,
            onStdout: line => Console.WriteLine($"    {SecretMasker.Mask(line, secrets)}"),
            cancellationToken: cancellationToken);

        var existingHash = inspectResult.ExitCode == 0
            ? inspectResult.Stdout.Trim()
            : string.Empty;

        if (inspectResult.ExitCode == 0 && string.Equals(existingHash, sourceHash, StringComparison.Ordinal))
        {
            if (debugEnabled)
            {
                Console.WriteLine($"[debug] Container image '{container.Image}' source hash matches ({sourceHash}); skipping build.");
            }

            return null;
        }

        var reason = inspectResult.ExitCode == 0
            ? "source hash changed"
            : "image missing or not inspectable";
        if (echoMode != "none")
        {
            Console.WriteLine($"  > [container:build] Building image '{SecretMasker.Mask(container.Image, secrets)}' ({reason}).");
        }

        var buildArgs = new List<string>
        {
            "build",
            "-f",
            dockerfilePath,
            "-t",
            container.Image,
            "--label",
            $"{ContainerSourceHashLabel}={sourceHash}",
        };

        if (!string.IsNullOrWhiteSpace(container.Build?.Target))
        {
            buildArgs.Add("--target");
            buildArgs.Add(container.Build.Target);
        }

        if (container.Build?.Args is { Count: > 0 } buildArgMap)
        {
            foreach (var (name, value) in buildArgMap.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                buildArgs.Add("--build-arg");
                buildArgs.Add(name + "=" + value);
            }
        }

        buildArgs.Add(buildContextPath);

        if (debugEnabled)
        {
            Console.WriteLine($"[debug] Container image build: docker {string.Join(" ", buildArgs.Select(arg => QuoteForDebug(SecretMasker.Mask(arg, secrets))))}");
        }

        var buildResult = await ShellRunner.RunProcessAsync(
            ResolveDockerCommand(),
            buildArgs,
            context.RepositoryRoot,
            onStdout: line => Console.WriteLine($"    {SecretMasker.Mask(line, secrets)}"),
            cancellationToken: cancellationToken);

        return buildResult.ExitCode == 0 ? null : buildResult;
    }

    private static async Task<string> ComputeContainerSourceHashAsync(
        string dockerfilePath,
        string buildContextPath,
        StepContainerBuildDefinition? build,
        bool inlineSource,
        CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        buffer.Append("dockerfile=");
        buffer.AppendLine(inlineSource ? "inline" : NormalizePath(dockerfilePath));
        buffer.Append("context=");
        buffer.AppendLine(NormalizePath(buildContextPath));
        buffer.Append("buildTarget=");
        buffer.AppendLine(build?.Target ?? string.Empty);

        if (build?.Args is { Count: > 0 } buildArgs)
        {
            foreach (var (name, value) in buildArgs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                buffer.Append("buildArg=");
                buffer.Append(name);
                buffer.Append('=');
                buffer.AppendLine(value);
            }
        }

        var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath, cancellationToken);
        buffer.Append("dockerfileContent=");
        buffer.AppendLine(dockerfileContent);

        var dockerIgnorePath = Path.Join(buildContextPath, ".dockerignore");
        if (File.Exists(dockerIgnorePath))
        {
            var dockerIgnoreContent = await File.ReadAllTextAsync(dockerIgnorePath, cancellationToken);
            buffer.Append("dockerignore=");
            buffer.AppendLine(dockerIgnoreContent);
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(buffer.ToString()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string ResolveDockerCommand()
    {
        var configured = Environment.GetEnvironmentVariable(DockerCommandEnvVar);
        return string.IsNullOrWhiteSpace(configured) ? "docker" : configured;
    }

    private static Dictionary<string, string> BuildContainerEnvironment(
        ExecutionContext context,
        StepContainerDefinition container)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in context.FileEnvironment)
        {
            env[key] = value;
        }

        foreach (var (key, value) in context.MappedSecretEnvironment)
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

        foreach (var (key, value) in context.MappedSecretEnvironment)
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
                    var existingIndex = artifacts.FindIndex(existing =>
                        existing.Type.Equals(artifact.Type, StringComparison.OrdinalIgnoreCase) &&
                        existing.Name.Equals(artifact.Name, StringComparison.OrdinalIgnoreCase));
                    if (existingIndex < 0)
                    {
                        artifacts.Add(artifact);
                        continue;
                    }

                    var existing = artifacts[existingIndex];
                    artifacts[existingIndex] = existing with
                    {
                        Built = existing.Built || artifact.Built,
                        Pushed = existing.Pushed || artifact.Pushed,
                        Tags = artifact.Tags.Count > 0 ? artifact.Tags : existing.Tags,
                        ContentSha256 = artifact.ContentSha256 ?? existing.ContentSha256,
                        Location = artifact.Location ?? existing.Location,
                    };
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

        var projectedDepth = context.CommandCallStack.Count + 1;
        if (projectedDepth > context.MaxCommandDepth)
        {
            sw.Stop();
            var path = string.Join(" -> ", context.CommandCallStack.Append(commandName));
            return new StepResult(
                stepId,
                false,
                9,
                sw.Elapsed,
                new Dictionary<string, object?>
                {
                    ["error"] =
                        $"REXO-CMD-CYCLE: Maximum command delegation depth exceeded (maxDepth={context.MaxCommandDepth})\n\nPath:\n  {path}",
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
            MaxCommandDepth = context.MaxCommandDepth,
            EchoMode = context.CliEchoMode ?? stepDefinition.Echo,
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

        if (!result.Success)
        {
            var propagated = false;

            if (result.StructuredErrors is { Count: > 0 })
            {
                var error = result.StructuredErrors[0];
                outputs["errorCode"] = error.Code;
                outputs["error"] = error.Message;
                propagated = true;
            }

            if (!propagated)
            {
                var failedInnerStep = result.Steps.FirstOrDefault(step => !step.Success);
                if (failedInnerStep is not null)
                {
                    if (failedInnerStep.Outputs.TryGetValue("errorCode", out var errorCode))
                    {
                        outputs["errorCode"] = errorCode;
                        propagated = true;
                    }

                    if (failedInnerStep.Outputs.TryGetValue("error", out var errorMessage))
                    {
                        outputs["error"] = errorMessage;
                        propagated = true;
                    }
                }
            }

            if (!propagated && !string.IsNullOrWhiteSpace(result.Message))
            {
                outputs["error"] = result.Message;
            }
        }

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

    private static string CreateDisplayStepId(StepDefinition step, IReadOnlySet<string> secrets)
    {
        if (step.Id is not null)
        {
            return SecretMasker.Mask(step.Id, secrets);
        }

        if (!string.IsNullOrEmpty(step.Run))
        {
            return $"run-{Sanitize(SecretMasker.Mask(step.Run, secrets))}";
        }

        if (!string.IsNullOrEmpty(step.Uses))
        {
            return $"uses-{Sanitize(SecretMasker.Mask(step.Uses, secrets))}";
        }

        if (!string.IsNullOrEmpty(step.Command))
        {
            return $"cmd-{Sanitize(SecretMasker.Mask(step.Command, secrets))}";
        }

        return GenerateStepId(step);
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

    private static string ResolveEchoMode(string? stepEcho, ExecutionContext context) =>
        (context.CliEchoMode ?? stepEcho ?? context.CommandEchoMode ?? context.RuntimeEchoMode ?? "summary")
            .Trim()
            .ToLowerInvariant();

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
        bool FallbackAllowed,
        string? FallbackPolicy,
        bool FallbackUsed,
        string? FallbackReason);
}
