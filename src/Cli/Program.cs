[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Rexo.Configuration.Tests")]

namespace Rexo.Cli;

using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Artifacts.Helm;
using Rexo.Artifacts.Docker;
using Rexo.Artifacts.NuGet;
using Rexo.Ci;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Git;
using Rexo.Policies;
using Rexo.Templating;
using Rexo.Tui;
using Rexo.Ui;
using Rexo.Versioning;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Task<int> Main(string[] args) => ExecuteAsync(args, CancellationToken.None);

    public static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var workingDir = Environment.CurrentDirectory;

        // Parse global flags
        var (cleanArgs, json, jsonFile, verbose, debug, quiet, dryRunFlag, setOverrides) = ParseGlobalFlags(args);

        // No args (or only global flags) — show help
        if (cleanArgs.Count == 0)
        {
            PrintHelp();
            return 0;
        }

        var command = cleanArgs[0];

        // Handle help early
        if (command is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        // Set up the full service graph
        var (registry, executor, config) = await CliBootstrapper.BuildServicesAsync(workingDir, debug, setOverrides, cancellationToken);
        var outputSettings = ResolveCommandOutputSettings(config, json, jsonFile, quiet);
        var dryRun = ResolveDryRun(config, dryRunFlag);

        return command switch
        {
            "version" => await RunBuiltinAsync(executor, "version", EmptyInvocation(workingDir, outputSettings, dryRun, debug), config, outputSettings, verbose, quiet, cancellationToken),
            "doctor" => await RunBuiltinAsync(executor, "doctor", EmptyInvocation(workingDir, outputSettings, dryRun, debug), config, outputSettings, verbose, quiet, cancellationToken),
            "capabilities" => await RunBuiltinAsync(executor, "capabilities", EmptyInvocation(workingDir, outputSettings, dryRun, debug), config, outputSettings, verbose, quiet, cancellationToken),
            "init" => await RunInitBuiltinAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "new" => await RunInitBuiltinAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "list" => await RunListBuiltinAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "explain" => await RunExplainAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "config" => await RunConfigSubcommandAsync(cleanArgs, executor, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "policies" => await RunPoliciesSubcommandAsync("policies", cleanArgs, executor, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "ui" => await RunUiAsync(executor, config, workingDir, cancellationToken),
            "run" => await RunConfiguredAsync(cleanArgs, executor, config, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            _ => await RunDirectAsync(command, cleanArgs, executor, config, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
        };
    }

    private static async Task<(CommandRegistry registry, DefaultCommandExecutor executor, RepoConfig? config)>
        BuildServicesAsync(string workingDir, bool debug, CancellationToken cancellationToken)
    {
        if (debug) Console.WriteLine($"[debug] Loading configuration from {workingDir}");
        // Try load config — check multiple candidate locations in priority order
        RepoConfig? config = null;
        PolicyConfig? policyConfig = null;
        var configPath = ConfigFileLocator.FindConfigPath(workingDir)
            ?? ConfigFileLocator.GetDefaultConfigPath(workingDir);
        if (File.Exists(configPath))
        {
            try
            {
                config = await RepoConfigurationLoader.LoadAsync(configPath, cancellationToken);
                if (debug) Console.WriteLine($"[debug] Loaded config: {configPath} ({config.Name})");
            }
            catch (Exception ex)
            {
                ConsoleRenderer.RenderError($"Failed to load config '{configPath}': {ex.Message}");
            }
        }

        if (config is not null)
        {
            var embeddedPolicy = LoadEmbeddedPolicyTemplate("standard", debug);
            var policyPath = ConfigFileLocator.FindPolicyPath(workingDir);
            if (policyPath is not null)
            {
                try
                {
                    policyConfig = await RepoConfigurationLoader.LoadPolicyAsync(policyPath, cancellationToken);
                    if (debug && policyConfig is not null) Console.WriteLine($"[debug] Loaded policy: {policyPath}");
                }
                catch (Exception ex)
                {
                    if (debug) Console.WriteLine($"[debug] Policy load skipped ({policyPath}): {ex.Message}");
                }
            }

            policyConfig = MergePolicies(embeddedPolicy, policyConfig);
        }

        var effectiveConfig = MergePolicyIntoEffectiveConfig(config, policyConfig);

        // Build registry
        var registry = BuiltinCommandRegistration.CreateDefault(effectiveConfig, File.Exists(configPath) ? configPath : null);
        var executor = new DefaultCommandExecutor(registry);

        if (config is not null)
        {
            // Set up provider registries
            var templateRenderer = new TemplateRenderer();
            var versionProviders = VersionProviderRegistry.CreateDefault();
            var artifactProviders = new ArtifactProviderRegistry();
            artifactProviders.Register("helm-oci", new HelmOciArtifactProvider());
            artifactProviders.Register("docker", new DockerArtifactProvider());
            artifactProviders.Register("nuget", new NuGetArtifactProvider());

            var builtinRegistry = new BuiltinRegistry();
            var configLoader = new ConfigCommandLoader(
                builtinRegistry,
                templateRenderer,
                versionProviders,
                artifactProviders);

            configLoader.LoadInto(registry, config, workingDir, executor);
            configLoader.LoadPolicyCommandsInto(registry, policyConfig ?? new PolicyConfig(), config, workingDir, executor);
        }

        return (registry, executor, effectiveConfig);
    }

    private static RepoConfig? MergePolicyIntoEffectiveConfig(RepoConfig? config, PolicyConfig? policy)
    {
        if (config is null)
        {
            return null;
        }

        if (policy is null)
        {
            return config;
        }

        var commands = new Dictionary<string, RepoCommandConfig>(StringComparer.OrdinalIgnoreCase);
        if (policy.Commands is not null)
        {
            foreach (var (name, command) in policy.Commands)
            {
                commands[name] = NormalizeCommandConfig(command);
            }
        }

        foreach (var (name, command) in config.Commands ?? [])
        {
            commands[name] = NormalizeCommandConfig(command);
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (policy.Aliases is not null)
        {
            foreach (var (alias, target) in policy.Aliases)
            {
                aliases[alias] = target;
            }
        }

        foreach (var (alias, target) in config.Aliases ?? [])
        {
            aliases[alias] = target;
        }

        return config with
        {
            Commands = commands,
            Aliases = aliases,
        };
    }

    private static PolicyConfig MergePolicies(PolicyConfig? baseline, PolicyConfig? overridePolicy)
    {
        var commands = new Dictionary<string, RepoCommandConfig>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (baseline?.Commands is { Count: > 0 })
        {
            foreach (var (name, command) in baseline.Commands)
            {
                commands[name] = NormalizeCommandConfig(command);
            }
        }

        if (overridePolicy?.Commands is { Count: > 0 })
        {
            foreach (var (name, command) in overridePolicy.Commands)
            {
                commands[name] = NormalizeCommandConfig(command);
            }
        }

        if (baseline?.Aliases is { Count: > 0 })
        {
            foreach (var (alias, target) in baseline.Aliases)
            {
                aliases[alias] = target;
            }
        }

        if (overridePolicy?.Aliases is { Count: > 0 })
        {
            foreach (var (alias, target) in overridePolicy.Aliases)
            {
                aliases[alias] = target;
            }
        }

        return new PolicyConfig(commands, aliases);
    }

    private static PolicyConfig LoadEmbeddedPolicyTemplate(string templateName, bool debug)
    {
        try
        {
            var json = EmbeddedPolicyTemplates.ReadTemplate(templateName);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var commands = new Dictionary<string, RepoCommandConfig>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("commands", out var commandsElement) && commandsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var commandProperty in commandsElement.EnumerateObject())
                {
                    var command = JsonSerializer.Deserialize<RepoCommandConfig>(commandProperty.Value.GetRawText());
                    if (command is not null)
                    {
                        commands[commandProperty.Name] = NormalizeCommandConfig(command);
                    }
                }
            }

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("aliases", out var aliasesElement) && aliasesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var aliasProperty in aliasesElement.EnumerateObject())
                {
                    var value = aliasProperty.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        aliases[aliasProperty.Name] = value;
                    }
                }
            }

            if (debug)
            {
                Console.WriteLine($"[debug] Loaded embedded policy template: {templateName}");
            }

            return new PolicyConfig(commands, aliases);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            if (debug)
            {
                Console.WriteLine($"[debug] Embedded policy '{templateName}' load failed: {ex.Message}");
            }

            return new PolicyConfig();
        }
    }

    private static RepoCommandConfig NormalizeCommandConfig(RepoCommandConfig command) =>
        new(
            command.Description,
            command.Options ?? [],
            command.Steps ?? [])
        {
            Hidden = command.Hidden,
            Args = command.Args ?? [],
            Merge = command.Merge,
            MaxParallel = command.MaxParallel,
        };

    private static async Task<int> RunBuiltinAsync(
        DefaultCommandExecutor executor,
        string command,
        CommandInvocation invocation,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, command, invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

        await WriteRunManifestAsync(result, command, invocation.WorkingDirectory, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);

        return exitCode;
    }

    private static async Task<int> RunExplainAsync(
        DefaultCommandExecutor executor,
        IReadOnlyList<string> args,
        string workingDir,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        bool debug,
        CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            ConsoleRenderer.RenderError($"Usage: {GetCliCommandName()} explain <command>");
            return 1;
        }

        // Collect multi-word command name: explain branch feature
        var commandName = string.Join(" ", args.Skip(1));
        var invocation = CreateInvocation(
            new Dictionary<string, string> { ["command"] = commandName },
            new Dictionary<string, string?>(),
            outputSettings,
            workingDir,
            dryRun,
            debug);

        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, "explain", invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

        await WriteRunManifestAsync(result, "explain", workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);

        return exitCode;
    }

    private static async Task<int> RunPoliciesSubcommandAsync(
        string commandPrefix,
        IReadOnlyList<string> args,
        DefaultCommandExecutor executor,
        string workingDir,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        bool debug,
        CancellationToken cancellationToken)
    {
        // args[0] == commandPrefix, args[1] == sub-command, args[2..] == positional args
        if (args.Count < 2)
        {
            Console.WriteLine($"Usage: rx {commandPrefix} <list|show> [name]");
            return 1;
        }

        var subCommand = $"{commandPrefix} {args[1].ToLowerInvariant()}";

        // For "<prefix> show <name>" pass the name as arg
        var parsedArgs = new Dictionary<string, string>();
        if (args.Count >= 3)
        {
            parsedArgs["name"] = args[2];
        }

        var invocation = CreateInvocation(parsedArgs, new Dictionary<string, string?>(), outputSettings, workingDir, dryRun, debug);

        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, subCommand, invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

        await WriteRunManifestAsync(result, subCommand, workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);

        return exitCode;
    }

    private static async Task<int> RunConfigSubcommandAsync(
        IReadOnlyList<string> args,
        DefaultCommandExecutor executor,
        string workingDir,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        bool debug,
        CancellationToken cancellationToken)
    {
        // args[0] == "config", args[1] == sub-command
        if (args.Count < 2)
        {
            Console.WriteLine("Usage: rx config <resolved|sources|materialize>");
            return 1;
        }

        var subCommand = $"config {args[1].ToLowerInvariant()}";
        var invocation = EmptyInvocation(workingDir, outputSettings, dryRun, debug);

        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, subCommand, invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

        await WriteRunManifestAsync(result, subCommand, workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);

        return exitCode;
    }

    private static async Task<int> RunUiAsync(
        DefaultCommandExecutor executor,
        RepoConfig? config,
        string workingDir,
        CancellationToken cancellationToken)
    {
        await RexoTuiHost.RunAsync(executor, config, workingDir, cancellationToken);
        return 0;
    }

    private static async Task<int> RunConfiguredAsync(
        IReadOnlyList<string> args,
        DefaultCommandExecutor executor,
        RepoConfig? config,
        string workingDir,
        RepoConfig? effectiveConfig,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        bool debug,
        CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            ConsoleRenderer.RenderError($"Usage: {GetCliCommandName()} run <command> [options]");
            return 1;
        }

        // args[0] == "run" — resolve using same longest-match as direct invocation
        var candidateArgs = args.Skip(1).ToList();

        for (var wordCount = candidateArgs.Count; wordCount >= 1; wordCount--)
        {
            var candidateName = string.Join(" ", candidateArgs.Take(wordCount));
            if (!executor.Registry.TryResolve(candidateName, out _)) continue;

            var remainingArgs = candidateArgs.Skip(wordCount).ToList();
            var (parsedArgs, parsedOptions) = ParseArgsAndOptions(remainingArgs);

            // Map positional args to declared arg names when config defines them
            if (config?.Commands?.TryGetValue(candidateName, out var cmdConfig) == true &&
                cmdConfig.Args is { Count: > 0 })
            {
                var argNames = cmdConfig.Args.Keys.ToArray();
                var positionalArgs = remainingArgs
                    .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                    .ToArray();

                for (var i = 0; i < Math.Min(argNames.Length, positionalArgs.Length); i++)
                {
                    parsedArgs = new Dictionary<string, string>(parsedArgs)
                    {
                        [argNames[i]] = positionalArgs[i]
                    };
                }
            }

            var invocation = CreateInvocation(parsedArgs, parsedOptions, outputSettings, workingDir, dryRun, debug);
            var startedAt = DateTimeOffset.UtcNow;
            var result = await ExecuteCommandAsync(executor, candidateName, invocation, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;

            var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

            // Write run manifest if --json-file specified
            await WriteRunManifestAsync(result, candidateName, workingDir, startedAt, completedAt, outputSettings.JsonFile, effectiveConfig, outputSettings, cancellationToken);

            return exitCode;
        }

        var requestedCommand = string.Join(" ", candidateArgs);
        ConsoleRenderer.RenderError($"Command '{requestedCommand}' not found. Run '{GetCliCommandName()} list' to see available commands.");
        return 8;
    }

    private static async Task<int> RunInitBuiltinAsync(
        DefaultCommandExecutor executor,
        IReadOnlyList<string> args,
        string workingDir,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        bool debug,
        CancellationToken cancellationToken)
    {
        var remainingArgs = args.Skip(1).ToList();
        string? mode = null;
        if (remainingArgs.Count > 0 && !remainingArgs[0].StartsWith("--", StringComparison.Ordinal))
        {
            mode = remainingArgs[0];
            remainingArgs.RemoveAt(0);
        }

        var (parsedArgs, parsedOptions) = ParseArgsAndOptions(remainingArgs);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            parsedOptions = new Dictionary<string, string?>(parsedOptions)
            {
                ["mode"] = mode,
            };
        }

        var invocation = CreateInvocation(parsedArgs, parsedOptions, outputSettings, workingDir, dryRun, debug);

        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, "init", invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

        await WriteRunManifestAsync(result, "init", workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);

        return exitCode;
    }

    private static async Task<int> RunListBuiltinAsync(
        DefaultCommandExecutor executor,
        IReadOnlyList<string> args,
        string workingDir,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        bool debug,
        CancellationToken cancellationToken)
    {
        var remainingArgs = args.Skip(1).ToList();
        var (parsedArgs, parsedOptions) = ParseArgsAndOptions(remainingArgs);
        var invocation = CreateInvocation(parsedArgs, parsedOptions, outputSettings, workingDir, dryRun, debug);

        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, "list", invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

        await WriteRunManifestAsync(result, "list", workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);

        return exitCode;
    }

    private static async Task<int> RunDirectAsync(
        string command,
        IReadOnlyList<string> args,
        DefaultCommandExecutor executor,
        RepoConfig? config,
        string workingDir,
        RepoConfig? effectiveConfig,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        bool debug,
        CancellationToken cancellationToken)
    {
        // Try multi-word command resolution: "branch feature" from ["branch", "feature", "name"]
        // Try progressively shorter command names
        for (var wordCount = args.Count; wordCount >= 1; wordCount--)
        {
            var candidateName = string.Join(" ", args.Take(wordCount));
            if (!executor.Registry.TryResolve(candidateName, out _)) continue;

            var remainingArgs = args.Skip(wordCount).ToList();
            var (parsedArgs, parsedOptions) = ParseArgsAndOptions(remainingArgs);

            // If config defines arg names, map positional args to them
            if (config?.Commands?.TryGetValue(candidateName, out var cmdConfig) == true &&
                cmdConfig.Args is { Count: > 0 })
            {
                var argNames = cmdConfig.Args.Keys.ToArray();
                var positionalArgs = remainingArgs
                    .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                    .ToArray();

                for (var i = 0; i < Math.Min(argNames.Length, positionalArgs.Length); i++)
                {
                    parsedArgs = new Dictionary<string, string>(parsedArgs)
                    {
                        [argNames[i]] = positionalArgs[i]
                    };
                }
            }

            var invocation = CreateInvocation(parsedArgs, parsedOptions, outputSettings, workingDir, dryRun, debug);

            var startedAt = DateTimeOffset.UtcNow;
            var result = await ExecuteCommandAsync(executor, candidateName, invocation, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

            await WriteRunManifestAsync(result, candidateName, workingDir, startedAt, completedAt, invocation.JsonFile, effectiveConfig, outputSettings, cancellationToken);

            return exitCode;
        }

        ConsoleRenderer.RenderError($"Command '{command}' not found. Run '{GetCliCommandName()} list' to see available commands.");
        return 8;
    }

    private static async Task<int> WriteResultAsync(
        CommandResult result,
        CommandInvocation invocation,
        bool verbose,
        bool quiet,
        CancellationToken cancellationToken)
    {
        // File output — always write if --json-file was given, independent of stdout
        if (!string.IsNullOrWhiteSpace(invocation.JsonFile))
        {
            var payload = JsonSerializer.Serialize(result, JsonOptions);
            var dir = Path.GetDirectoryName(invocation.JsonFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(invocation.JsonFile!, payload, cancellationToken);
        }

        // Stdout — suppressed entirely by --quiet
        if (!quiet && (invocation.Json || invocation.Stdout))
        {
            if (invocation.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                switch (result.Command)
                {
                    case "version":
                        ConsoleRenderer.RenderVersion(result.Message ?? "unknown");
                        break;
                    case "doctor":
                        ConsoleRenderer.RenderDoctorResult(result);
                        break;
                    case "list":
                        ConsoleRenderer.RenderList(result);
                        break;
                    case "explain":
                        ConsoleRenderer.RenderExplain(result);
                        break;
                    default:
                        ConsoleRenderer.RenderCommandResult(result);
                        break;
                }

                if (verbose && result.Steps.Count > 0)
                {
                    ConsoleRenderer.RenderStepResults(result.Steps);
                }
            }
        }

        return result.ExitCode;
    }

    private static async Task<CommandResult> ExecuteCommandAsync(
        DefaultCommandExecutor executor,
        string command,
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        // Suppress stdout/stderr during execution only when --json is set (JSON stdout mode)
        if (!invocation.Json)
        {
            return await executor.ExecuteAsync(command, invocation, cancellationToken);
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return await executor.ExecuteAsync(command, invocation, cancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task WriteRunManifestAsync(
        CommandResult result,
        string commandName,
        string workingDir,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string? jsonFile,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        CancellationToken cancellationToken)
    {
        var ciConfig = config?.Outputs?.Ci;
        var effectiveCiConfig = ciConfig ?? new RepoCiOutputsConfig { Provider = "auto" };
        var shouldWriteManifest = !string.IsNullOrWhiteSpace(jsonFile);
        var shouldEmitCi = ciConfig?.Emit ?? true;

        if (!shouldWriteManifest && !shouldEmitCi)
        {
            return;
        }

        var ciInfo = CiDetector.Detect();
        string? manifestPath = null;
        if (shouldWriteManifest && jsonFile is not null)
        {
            manifestPath = jsonFile.Replace(".json", "-manifest.json", StringComparison.OrdinalIgnoreCase);
            if (manifestPath == jsonFile) manifestPath = jsonFile + ".manifest.json";
        }

        // Compute SHA-256 of resolved config content for traceability
        string? configHash = null;
        var configPath = ConfigFileLocator.FindConfigPath(workingDir);
        if (configPath is not null)
        {
            var configBytes = await File.ReadAllBytesAsync(configPath, cancellationToken);
            var hashBytes = System.Security.Cryptography.SHA256.HashData(configBytes);
            configHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        var gitInfo = await GitDetector.DetectAsync(workingDir, cancellationToken);

        var manifest = new RunManifest
        {
            ToolVersion = GetToolVersion(),
            RepoName = Path.GetFileName(workingDir.TrimEnd(Path.DirectorySeparatorChar)),
            RepoRoot = workingDir,
            Branch = gitInfo.Branch,
            CommitSha = gitInfo.CommitSha,
            RemoteUrl = gitInfo.RemoteUrl,
            CommandExecuted = commandName,
            Success = result.Success,
            ExitCode = result.ExitCode,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ConfigHash = configHash,
            Version = result.Version,
            AssemblyVersion = result.Version?.AssemblyVersion,
            InformationalVersion = result.Version?.InformationalVersion,
            NuGetVersion = result.Version?.NuGetVersion,
            IsCi = ciInfo.IsCi,
            CiProvider = ciInfo.Provider,
            CiBuildId = ciInfo.BuildId,
            CiRunNumber = ciInfo.RunNumber,
            CiWorkflowName = ciInfo.WorkflowName,
            CiActor = ciInfo.Actor,
            CiTag = ciInfo.Tag,
            CiBuildUrl = ciInfo.BuildUrl,
            Steps = result.Steps
                .Select(s =>
                {
                    var fileOutputs = s.Outputs.TryGetValue("__fileOutputs", out var fo)
                        && fo is IReadOnlyDictionary<string, IReadOnlyList<string>> dict
                        ? dict
                        : new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

                    static string? GetStringOutput(IReadOnlyDictionary<string, object?> outputs, string key) =>
                        outputs.TryGetValue(key, out var value) ? value?.ToString() : null;

                    static bool? GetBoolOutput(IReadOnlyDictionary<string, object?> outputs, string key)
                    {
                        if (!outputs.TryGetValue(key, out var value) || value is null)
                        {
                            return null;
                        }

                        if (value is bool boolValue)
                        {
                            return boolValue;
                        }

                        return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
                    }

                    return new StepManifestEntry(s.StepId, s.Success, s.ExitCode, s.Duration.TotalMilliseconds)
                    {
                        FileOutputs = fileOutputs,
                        ExecutionMode = GetStringOutput(s.Outputs, "__executionMode"),
                        RequestedExecutionMode = GetStringOutput(s.Outputs, "__requestedExecutionMode"),
                        ContainerImage = GetStringOutput(s.Outputs, "__containerImage"),
                        ContainerWorkingDirectory = GetStringOutput(s.Outputs, "__containerWorkingDirectory"),
                        ContainerFallbackUsed = GetBoolOutput(s.Outputs, "__containerFallbackUsed"),
                        ContainerFallbackReason = GetStringOutput(s.Outputs, "__containerFallbackReason"),
                    };
                })
                .ToArray(),
            Artifacts = result.Artifacts,
            PushDecisions = result.PushDecisions,
            Errors = result.StructuredErrors
                .Select(e => e.Message)
                .Where(m => m is not null)
                .Select(m => m!)
                .ToArray(),
        };

        if (shouldWriteManifest && jsonFile is not null && manifestPath is not null)
        {
            var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
            var dir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
        }

        if (shouldEmitCi)
        {
            await EmitCiOutputsAsync(manifest, effectiveCiConfig, ciInfo, outputSettings, cancellationToken);
        }
    }

    private static async Task EmitCiOutputsAsync(
        RunManifest manifest,
        RepoCiOutputsConfig ciConfig,
        CiInfo detectedCi,
        CommandOutputSettings outputSettings,
        CancellationToken cancellationToken)
    {
        var provider = ResolveCiProvider(ciConfig.Provider, detectedCi);
        if (string.Equals(ciConfig.Provider, "auto", StringComparison.OrdinalIgnoreCase) && !detectedCi.IsCi)
        {
            // No CI context detected and provider is auto: skip silently.
            return;
        }

        if (!string.Equals(ciConfig.Provider, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ciConfig.Provider, detectedCi.Provider, StringComparison.OrdinalIgnoreCase) &&
            detectedCi.IsCi)
        {
            Console.WriteLine($"[warn] Emitting {ciConfig.Provider} syntax outside the detected {detectedCi.Provider} context for local testing.");
        }

        var emissionOptions = new CiEmissionOptions
        {
            Provider = provider,
            Prefix = string.IsNullOrWhiteSpace(ciConfig.Prefix) ? "REXO_" : ciConfig.Prefix!,
            KeyCasing = string.IsNullOrWhiteSpace(ciConfig.KeyCasing) ? "upperSnake" : ciConfig.KeyCasing!,
            Scope = ciConfig.Scope,
            IncludeStepOutputs = ciConfig.IncludeStepOutputs == true,
            EmitEmptyValues = ciConfig.EmitEmptyValues == true,
            Redact = ciConfig.Redact ?? true,
            FailOnError = ciConfig.FailOnError == true,
            MaxValueLength = ciConfig.MaxValueLength ?? 8192,
            MaxVariables = ciConfig.MaxVariables ?? 1000,
        };

        try
        {
            var payload = CiOutputEmitter.BuildPayload(manifest, emissionOptions);
            foreach (var warning in payload.Warnings)
            {
                Console.WriteLine($"[warn] {warning}");
            }

            if (string.Equals(provider, "github-actions", StringComparison.OrdinalIgnoreCase) &&
                await TryEmitGitHubActionsFileAsync(payload, ResolveGitHubActionsScope(ciConfig), cancellationToken))
            {
                return;
            }

            foreach (var line in CiOutputEmitter.FormatStdoutLines(payload))
            {
                Console.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            var message = $"CI emission failed: {ex.Message}";
            if (emissionOptions.FailOnError)
            {
                throw new InvalidOperationException(message, ex);
            }

            Console.WriteLine($"[warn] {message}");
        }

        await Task.CompletedTask;
    }

    private static string ResolveCiProvider(string? configuredProvider, CiInfo detectedCi)
    {
        if (string.IsNullOrWhiteSpace(configuredProvider) || string.Equals(configuredProvider, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return detectedCi.Provider is null or "unknown" ? "generic" : detectedCi.Provider;
        }

        return configuredProvider;
    }

    private static async Task<bool> TryEmitGitHubActionsFileAsync(CiEmissionPayload payload, string? githubScope, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(githubScope) ? "env" : githubScope;
        var envVarName = scope.ToLowerInvariant() switch
        {
            "output" => "GITHUB_OUTPUT",
            "state" => "GITHUB_STATE",
            _ => "GITHUB_ENV",
        };

        var githubFile = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(githubFile))
        {
            Console.WriteLine($"[warn] {envVarName} is not set; falling back to stdout CI emission.");
            return false;
        }

        var buffer = new System.Text.StringBuilder(payload.Variables.Count * 48);
        foreach (var (key, value) in payload.Variables)
        {
            AppendGitHubEnvironmentAssignment(buffer, key, value);
        }

        await File.AppendAllTextAsync(githubFile, buffer.ToString(), cancellationToken);
        return true;
    }

    private static string ResolveGitHubActionsScope(RepoCiOutputsConfig ciConfig)
    {
        var nestedScope = ciConfig.GitHubActions?.Scope;
        return string.IsNullOrWhiteSpace(nestedScope) ? "env" : nestedScope;
    }

    private static void AppendGitHubEnvironmentAssignment(System.Text.StringBuilder buffer, string key, string value)
    {
        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            var delimiter = $"REXO_{Guid.NewGuid():N}";
            while (value.Contains(delimiter, StringComparison.Ordinal))
            {
                delimiter = $"REXO_{Guid.NewGuid():N}";
            }

            buffer.Append(key).Append("<<").AppendLine(delimiter);
            buffer.AppendLine(value);
            buffer.AppendLine(delimiter);
            return;
        }

        buffer.Append(key).Append('=').AppendLine(value);
    }

    private static string GetToolVersion()
    {
        var assembly = typeof(Program).Assembly;
        return assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.1.0-local";
    }

    private static (IReadOnlyList<string> cleanArgs, bool json, string? jsonFile, bool verbose, bool debug, bool quiet, bool? dryRun, IReadOnlyList<string> setOverrides) ParseGlobalFlags(string[] args)
    {
        var clean = new List<string>();
        var json = false;
        string? jsonFile = null;
        var verbose = false;
        var debug = false;
        var quiet = false;
        bool? dryRun = null;
        var setOverrides = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--json-file" when i + 1 < args.Length:
                    jsonFile = args[++i];
                    break;
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "--debug":
                    debug = true;
                    verbose = true;
                    break;
                case "--quiet" or "-q":
                    quiet = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-dry-run":
                    dryRun = false;
                    break;
                case "--set" when i + 1 < args.Length:
                    setOverrides.Add(args[++i]);
                    break;
                default:
                    clean.Add(args[i]);
                    break;
            }
        }

        return (clean, json, jsonFile, verbose, debug, quiet, dryRun, setOverrides);
    }

    private static (IReadOnlyDictionary<string, string> args, IReadOnlyDictionary<string, string?> options)
        ParseArgsAndOptions(IEnumerable<string> args)
    {
        var parsedArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parsedOptions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var argList = args.ToList();

        for (var i = 0; i < argList.Count; i++)
        {
            var a = argList[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                // Check for --key value vs --key (bool flag)
                if (i + 1 < argList.Count && !argList[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    parsedOptions[key] = argList[++i];
                }
                else
                {
                    parsedOptions[key] = "true";
                }
            }
        }

        return (parsedArgs, parsedOptions);
    }

    private static void PrintHelp()
    {
        var cli = GetCliCommandName();
        Console.WriteLine($"{cli} - repository operating system");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine($"  {cli} <command> [args] [options]");
        Console.WriteLine();
        Console.WriteLine("Built-in commands:");
        Console.WriteLine("  version                     Show tool version");
        Console.WriteLine("  list                        List all available commands");
        Console.WriteLine("      --include-hidden        Also show hidden config commands");
        Console.WriteLine("  explain <command>           Explain a command (or alias)");
        Console.WriteLine("  explain version             Show version provider configuration");
        Console.WriteLine("  doctor                      Check environment and configuration");
        Console.WriteLine("  capabilities                Show runtime capability contract and supported features");
        Console.WriteLine("  init                        Create a starter rexo config");
        Console.WriteLine("  new                         Alias for init");
        Console.WriteLine("  init detect                 Preview auto detection and recommendations");
        Console.WriteLine("  init ci                     Scaffold thin CI wrappers for rx release");
        Console.WriteLine("      --provider              github|azdo|both (default: both)");
        Console.WriteLine("      --yes                   Non-interactive defaults");
        Console.WriteLine("      --stack                 auto|dotnet|node|python|go|generic");
        Console.WriteLine("      --detect                Preview detection only (no files written)");
        Console.WriteLine("      --dry-run               Alias for --detect");
        Console.WriteLine("      --schema-source         remote (default) or local");
        Console.WriteLine("      --with-policy           Also create policy.json from an embedded policy");
        Console.WriteLine("      --policy                standard|dotnet (or any embedded policy name)");
        Console.WriteLine("      --with-docker-artifact  Add starter docker artifact to generated config");
        Console.WriteLine("      --without-docker-artifact  Skip docker artifact scaffolding (non-interactive)");
        Console.WriteLine("      --with-instructions     Download rexo.instructions.md into repo");
        Console.WriteLine("      --instructions-path     Repo-relative destination");
        Console.WriteLine("      --force                 Overwrite existing config");
        Console.WriteLine("  run <command>               Run a configured command");
        Console.WriteLine("  config resolved             Show the fully-merged configuration");
        Console.WriteLine("  config sources              Show config file sources in merge order");
        Console.WriteLine("  config materialize          Write the merged config to a file");
        Console.WriteLine("  policies list               List available embedded policies");
        Console.WriteLine("  policies show <name>        Show an embedded policy");
        Console.WriteLine("  ui                          Open the interactive UI");
        Console.WriteLine("  help                        Show this help");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine("  --json                      Output as JSON");
        Console.WriteLine("  --json-file <path>          Write JSON output to file");
        Console.WriteLine("  --verbose                   Show detailed step output");
        Console.WriteLine("  --quiet                     Suppress non-essential output");
        Console.WriteLine("  --debug                     Show debug/diagnostic output");
        Console.WriteLine("  --dry-run                   Simulate external mutations without applying them");
        Console.WriteLine("  --no-dry-run                Explicitly disable dry-run when config enables it");
        Console.WriteLine("  --set <key.path=value>      Override a config value (repeatable; see docs/configuration/overrides.md)");
    }

    private static CommandInvocation EmptyInvocation(string workingDir, CommandOutputSettings outputSettings, bool dryRun, bool debug) =>
        CreateInvocation(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>(),
            outputSettings,
            workingDir,
            dryRun,
            debug);

    private static CommandInvocation CreateInvocation(
        IReadOnlyDictionary<string, string> args,
        IReadOnlyDictionary<string, string?> options,
        CommandOutputSettings outputSettings,
        string workingDir,
        bool dryRun,
        bool debug)
    {
        var mergedOptions = new Dictionary<string, string?>(options, StringComparer.OrdinalIgnoreCase)
        {
            ["dry-run"] = dryRun ? "true" : "false",
            ["debug"] = debug ? "true" : "false",
        };

        return new CommandInvocation(args, mergedOptions, outputSettings.Json, outputSettings.JsonFile, workingDir)
        {
            Stdout = outputSettings.Stdout,
        };
    }

    private static bool ResolveDryRun(RepoConfig? config, bool? cliDryRun)
    {
        if (cliDryRun.HasValue)
        {
            return cliDryRun.Value;
        }

        return config?.Runtime?.DryRun == true || config?.Runtime?.Push?.DryRun == true;
    }

    private static CommandOutputSettings ResolveCommandOutputSettings(RepoConfig? config, bool jsonFlag, string? jsonFileFlag, bool quiet)
    {
        var commandOutputs = config?.Outputs?.Command;
        var json = jsonFlag || commandOutputs?.Json == true;
        var jsonFile = !string.IsNullOrWhiteSpace(jsonFileFlag)
            ? jsonFileFlag
            : commandOutputs?.JsonFile;
        var stdout = !quiet && (commandOutputs?.Stdout ?? true);

        return new CommandOutputSettings(json, string.IsNullOrWhiteSpace(jsonFile) ? null : jsonFile, stdout);
    }

    private sealed record CommandOutputSettings(bool Json, string? JsonFile, bool Stdout);

    private static IReadOnlyList<string> DiscoverUiProjectRoots(string currentWorkingDir)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentWorkingDir,
        };

        var parent = Path.GetDirectoryName(currentWorkingDir);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        foreach (var directory in Directory.EnumerateDirectories(parent))
        {
            if (ConfigFileLocator.FindConfigPath(directory) is not null)
            {
                roots.Add(directory);
            }
        }

        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string GetCliCommandName()
    {
        var argv0 = Environment.GetCommandLineArgs().FirstOrDefault();
        var name = string.IsNullOrWhiteSpace(argv0)
            ? null
            : Path.GetFileNameWithoutExtension(argv0);

        return string.IsNullOrWhiteSpace(name) ? "rx" : name;
    }
}

