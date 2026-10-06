[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Rexo.Configuration.Tests")]

namespace Rexo.Cli;

using System.Text.Json;
using System.Security.Cryptography;
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
        var (cleanArgs, json, jsonFile, verbose, debug, quiet, dryRunFlag, nonInteractive, color, setOverrides) = ParseGlobalFlags(args);
        ConsoleRenderer.ConfigureColors(color);

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
        CommandRegistry registry;
        DefaultCommandExecutor executor;
        RepoConfig? config;
        ConfigProvenanceSnapshot provenance;
        try
        {
            (registry, executor, config, provenance) = await CliBootstrapper.BuildServicesAsync(
                workingDir,
                debug,
                setOverrides,
                cancellationToken,
                updatePolicies: command == "update",
                requirePolicyLock: command == "restore",
                captureConfigProvenance: command == "config" &&
                    (cleanArgs.Count > 1 &&
                     cleanArgs[1].Equals("explain", StringComparison.OrdinalIgnoreCase) ||
                     cleanArgs.Count > 2 &&
                     cleanArgs[1].Equals("resolved", StringComparison.OrdinalIgnoreCase) &&
                     cleanArgs.Skip(2).Any(argument =>
                         argument.Equals("--provenance", StringComparison.OrdinalIgnoreCase))),
                registerConfigCommands: command is not ("doctor" or "check"));
        }
        catch (Exception ex) when ((command is "restore" or "update") && ex is not OperationCanceledException)
        {
            ConsoleRenderer.RenderError($"Policy {command} failed: {ex.Message}");
            return 9;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleRenderer.RenderError($"Failed to initialize Rexo for '{command}': {ex.Message}");
            return 9;
        }

        var outputSettings = ResolveCommandOutputSettings(config, json, jsonFile, quiet);
        var dryRun = ResolveDryRun(config, dryRunFlag);

        return command switch
        {
            "version" => await RunBuiltinAsync(executor, "version", EmptyInvocation(workingDir, outputSettings, dryRun, debug), config, outputSettings, verbose, quiet, cancellationToken),
            "doctor" => await RunBuiltinAsync(executor, "doctor", EmptyInvocation(workingDir, outputSettings, dryRun, debug), config, outputSettings, verbose, quiet, cancellationToken),
            "check" => await RunDirectAsync(command, cleanArgs, executor, config, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "capabilities" => await RunBuiltinAsync(executor, "capabilities", EmptyInvocation(workingDir, outputSettings, dryRun, debug), config, outputSettings, verbose, quiet, cancellationToken),
            "init" => await RunInitBuiltinAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, nonInteractive, cancellationToken),
            "new" => await RunInitBuiltinAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, nonInteractive, cancellationToken),
            "list" => await RunListBuiltinAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "explain" => await RunExplainAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "graph" => await RunGraphAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "completion" => await RunCompletionAsync(executor, cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "restore" => await RunPolicyLockCommandAsync(
                update: false, workingDir, config, outputSettings, verbose, quiet, dryRun, cancellationToken),
            "update" => await RunPolicyLockCommandAsync(
                update: true, workingDir, config, outputSettings, verbose, quiet, dryRun, cancellationToken),
            "promote" => await RunPromoteAsync(cleanArgs, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "config" when cleanArgs.Count > 1 && cleanArgs[1].Equals("explain", StringComparison.OrdinalIgnoreCase) =>
                await RunConfigExplainAsync(cleanArgs, workingDir, config, provenance, outputSettings, verbose, quiet, cancellationToken),
            "config" => await RunConfigSubcommandAsync(cleanArgs, executor, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, provenance, cancellationToken),
            "policies" => await RunPoliciesSubcommandAsync("policies", cleanArgs, executor, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            "ui" => await RunUiAsync(executor, config, workingDir, nonInteractive, cancellationToken),
            "run" => await RunConfiguredAsync(cleanArgs, executor, config, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
            _ => await RunDirectAsync(command, cleanArgs, executor, config, workingDir, config, outputSettings, verbose, quiet, dryRun, debug, cancellationToken),
        };
    }

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

    private static async Task<int> RunGraphAsync(
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
            ConsoleRenderer.RenderError($"Usage: {GetCliCommandName()} graph <command> [--format text|json|mermaid]");
            return 1;
        }

        var commandWords = args.Skip(1).TakeWhile(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        var commandName = string.Join(" ", commandWords);
        var optionArgs = args.Skip(1).Skip(commandName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var (_, parsedOptions) = ParseArgsAndOptions(optionArgs);
        var invocation = CreateInvocation(
            new Dictionary<string, string> { ["command"] = commandName },
            parsedOptions,
            outputSettings,
            workingDir,
            dryRun,
            debug);
        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, "graph", invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);
        await WriteRunManifestAsync(result, "graph", workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);
        return exitCode;
    }

    private static async Task<int> RunCompletionAsync(
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
            ConsoleRenderer.RenderError($"Usage: {GetCliCommandName()} completion <bash|zsh|fish|powershell>");
            return 1;
        }

        var invocation = CreateInvocation(
            new Dictionary<string, string> { ["shell"] = args[1] },
            new Dictionary<string, string?>(),
            outputSettings,
            workingDir,
            dryRun,
            debug);
        var startedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteCommandAsync(executor, "completion", invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);
        await WriteRunManifestAsync(result, "completion", workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);
        return exitCode;
    }

    private static async Task<int> RunPolicyLockCommandAsync(
        bool update,
        string workingDir,
        RepoConfig? config,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var command = update ? "update" : "restore";
        var sources = GetPolicySources(config);
        string message;
        try
        {
            if (update)
            {
                if (sources.Count == 0)
                {
                    ConsoleRenderer.RenderError("No policy sources are configured to lock.");
                    return 1;
                }

                var lockPath = await PolicySourceLoader.UpdateLockfileAsync(
                    sources,
                    workingDir,
                    cancellationToken,
                    dryRun);
                message = dryRun
                    ? $"Dry run: would update the policy lockfile for {sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} policy source(s) at '{lockPath}'."
                    : $"Locked {sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} policy source(s) in '{lockPath}'.";
            }
            else
            {
                var lockfile = await PolicySourceLoader.ReadLockfileAsync(workingDir, cancellationToken);
                message = sources.Count == 0
                    ? "No remote policy sources are configured."
                    : lockfile is null
                        ? $"Resolved {sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} policy source(s); no lockfile is present."
                        : $"Restored and verified {lockfile.Policies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} locked policy source(s).";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleRenderer.RenderError($"Policy {command} failed: {ex.Message}");
            return 9;
        }

        var invocation = EmptyInvocation(workingDir, outputSettings, dryRun, debug: false);
        var startedAt = DateTimeOffset.UtcNow;
        var result = CommandResult.Ok(command, message);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);
        await WriteRunManifestAsync(result, command, workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);
        return exitCode;
    }

    private static async Task<int> RunPromoteAsync(
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
        var startedAt = DateTimeOffset.UtcNow;
        CommandResult result;
        if (args.Count < 3 || config is null)
        {
            result = CommandResult.Fail(
                "promote",
                1,
                "Usage: rx promote <run-manifest.json> <environment>; define environments.<name>.path in configuration.");
        }
        else
        {
            try
            {
                var message = await ArtifactPromotionService.PromoteAsync(
                    args[1],
                    args[2],
                    config,
                    workingDir,
                    dryRun,
                    cancellationToken);
                result = CommandResult.Ok("promote", message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = CommandResult.Fail("promote", 9, $"Promotion failed: {ex.Message}");
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        var invocation = EmptyInvocation(workingDir, outputSettings, dryRun, debug);
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);
        await WriteRunManifestAsync(
            result,
            "promote",
            workingDir,
            startedAt,
            completedAt,
            invocation.JsonFile,
            config,
            outputSettings,
            cancellationToken);
        return exitCode;
    }

    private static IReadOnlyList<string> GetPolicySources(RepoConfig? config)
    {
        var sources = config?.PolicySources?.ToList() ?? [];
        var environmentSources = Environment.GetEnvironmentVariable("REXO_POLICY_SOURCES");
        if (!string.IsNullOrWhiteSpace(environmentSources))
        {
            sources.AddRange(environmentSources.Split(
                [';', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        ConfigProvenanceSnapshot provenance,
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
        var includeProvenance = subCommand == "config resolved" &&
            args.Skip(2).Any(argument => argument.Equals("--provenance", StringComparison.OrdinalIgnoreCase));
        var result = includeProvenance
            ? CreateConfigResolvedWithProvenance(config, provenance, workingDir)
            : await ExecuteCommandAsync(executor, subCommand, invocation, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var exitCode = await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);

        await WriteRunManifestAsync(result, subCommand, workingDir, startedAt, completedAt, invocation.JsonFile, config, outputSettings, cancellationToken);

        return exitCode;
    }

    private static CommandResult CreateConfigResolvedWithProvenance(
        RepoConfig? config,
        ConfigProvenanceSnapshot provenance,
        string workingDirectory)
    {
        if (config is null)
        {
            return CommandResult.Fail("config resolved", 1, "No rexo configuration loaded.");
        }

        var effectiveConfig = RedactConfigNode(
            System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(config)));
        var repositoryFiles = provenance.RepositoryLayers
            .Select(layer => Path.GetRelativePath(workingDirectory, layer.Reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceLayers = new List<string>();
        if (provenance.PolicyConfig is not null)
        {
            sourceLayers.Add("merged policy sources");
        }

        sourceLayers.AddRange(repositoryFiles.Select(path => $"repository file: {path}"));
        sourceLayers.AddRange(provenance.SetOverrides.Select(overrideValue =>
        {
            var separator = overrideValue.IndexOf('=', StringComparison.Ordinal);
            var path = separator > 0 ? overrideValue[..separator] : "<invalid>";
            return $"CLI --set override: {path}";
        }));
        var outputs = new Dictionary<string, object?>
        {
            ["effectiveConfig"] = effectiveConfig,
            ["sourceLayers"] = sourceLayers,
            ["repositoryFiles"] = repositoryFiles,
            ["repositoryFileAttributionAvailable"] = provenance.RepositoryLayers.Count > 0,
            ["policySourceAttributionAvailable"] = false,
            ["sensitiveValuesRedacted"] = true,
        };
        var message = JsonSerializer.Serialize(new
        {
            effectiveConfig,
            provenance = new
            {
                sourceLayers,
                repositoryFiles,
                cliOverridePaths = provenance.SetOverrides.Select(overrideValue =>
                {
                    var separator = overrideValue.IndexOf('=', StringComparison.Ordinal);
                    return separator > 0 ? overrideValue[..separator] : "<invalid>";
                }),
                mergedPolicySourceGroupAvailable = provenance.PolicyConfig is not null,
                repositoryFileAttributionAvailable = provenance.RepositoryLayers.Count > 0,
                policySourceAttributionAvailable = false,
                note = "Repository files are listed as declaration sources; exact field-level merge ownership and individual policy-source attribution are not available.",
            },
        }, JsonOptions);
        return new CommandResult("config resolved", true, 0, message, outputs);
    }

    private static async Task<int> RunConfigExplainAsync(
        IReadOnlyList<string> args,
        string workingDir,
        RepoConfig? config,
        ConfigProvenanceSnapshot provenance,
        CommandOutputSettings outputSettings,
        bool verbose,
        bool quiet,
        CancellationToken cancellationToken)
    {
        if (args.Count < 3 || config is null)
        {
            ConsoleRenderer.RenderError("Usage: rx config explain <property.path>; an effective config must be loaded.");
            return 1;
        }

        var propertyPath = args[2];
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(config));
        var value = ResolveConfigProperty(document.RootElement, propertyPath);
        var isSensitive = propertyPath
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(IsSensitiveConfigProperty);
        var safeValue = value is null
            ? null
            : isSensitive
                ? System.Text.Json.Nodes.JsonValue.Create("***")
                : RedactConfigNode(System.Text.Json.Nodes.JsonNode.Parse(value.Value.GetRawText()));
        var valueJson = safeValue is null
            ? "<not found>"
            : JsonSerializer.Serialize(safeValue, JsonOptions);
        var sourceLayers = GetConfigSourceLayers(propertyPath, provenance, workingDir);
        var source = string.Join(" -> ", sourceLayers);
        var message = $"Property: {propertyPath}\nEffective value: {valueJson}\nApplicable declarations: {source}\n" +
            "Note: repository file contributors are listed; merged policy sources are reported as a group because exact per-source field attribution is not available.";
        var result = new CommandResult("config explain", true, 0, message, new Dictionary<string, object?>
        {
            ["path"] = propertyPath,
            ["value"] = safeValue,
            ["sourceLayers"] = sourceLayers,
            ["provenanceAvailable"] = sourceLayers.Count > 0,
            ["repositoryFileAttributionAvailable"] = provenance.RepositoryLayers.Count > 0,
            ["policySourceAttributionAvailable"] = false,
        });
        var invocation = EmptyInvocation(workingDir, outputSettings, dryRun: false, debug: false);
        return await WriteResultAsync(result, invocation, verbose, quiet, cancellationToken);
    }

    private static IReadOnlyList<string> GetConfigSourceLayers(
        string propertyPath,
        ConfigProvenanceSnapshot provenance,
        string workingDirectory)
    {
        var sources = new List<string>();
        if (provenance.PolicyConfig is not null &&
            ResolveSerializedProperty(provenance.PolicyConfig, propertyPath) is not null)
        {
            sources.Add("merged policy sources");
        }

        if (provenance.RepositoryConfig is not null &&
            ResolveSerializedProperty(provenance.RepositoryConfig, propertyPath) is not null)
        {
            var contributingFiles = provenance.RepositoryLayers
                .Where(layer => ResolveConfigProperty(layer.Document, propertyPath) is not null)
                .Select(layer => Path.GetRelativePath(workingDirectory, layer.Reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (contributingFiles.Length > 0)
            {
                sources.AddRange(contributingFiles.Select(path => $"repository file: {path}"));
            }
            else if (provenance.ConfigPath is not null)
            {
                sources.Add($"repository-derived configuration ({Path.GetRelativePath(workingDirectory, provenance.ConfigPath)})");
            }
            else
            {
                sources.Add("repository-derived configuration");
            }
        }

        if (provenance.SetOverrides.Any(overrideValue => IsOverrideForPath(overrideValue, propertyPath)))
        {
            sources.Add("CLI --set override");
        }

        if (sources.Count == 0)
        {
            sources.Add("Rexo defaults or runtime-derived value");
        }

        return sources;
    }

    private static JsonElement? ResolveSerializedProperty<T>(T value, string propertyPath)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return ResolveConfigProperty(document.RootElement, propertyPath);
    }

    private static bool IsOverrideForPath(string overrideValue, string propertyPath)
    {
        var separator = overrideValue.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var overridePath = overrideValue[..separator];
        return propertyPath.Equals(overridePath, StringComparison.OrdinalIgnoreCase) ||
            propertyPath.StartsWith(overridePath + ".", StringComparison.OrdinalIgnoreCase) ||
            overridePath.StartsWith(propertyPath + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement? ResolveConfigProperty(JsonElement root, string propertyPath)
    {
        var current = root;
        foreach (var segment in propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                var property = current.EnumerateObject()
                    .FirstOrDefault(candidate => candidate.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (property.Name is null)
                {
                    return null;
                }

                current = property.Value;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
                index >= 0 &&
                index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            return null;
        }

        return current.Clone();
    }

    private static bool IsSensitiveConfigProperty(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("apikey", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("privatekey", StringComparison.Ordinal);
    }

    private static System.Text.Json.Nodes.JsonNode? RedactConfigNode(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(property => property.Key).ToArray())
            {
                if (IsSensitiveConfigProperty(key))
                {
                    obj[key] = "***";
                }
                else
                {
                    RedactConfigNode(obj[key]);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                RedactConfigNode(array[index]);
            }
        }

        return node;
    }

    private static async Task<int> RunUiAsync(
        DefaultCommandExecutor executor,
        RepoConfig? config,
        string workingDir,
        bool nonInteractive,
        CancellationToken cancellationToken)
    {
        if (nonInteractive)
        {
            ConsoleRenderer.RenderError("The interactive UI cannot run with --non-interactive.");
            return 1;
        }

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
        bool nonInteractive,
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

        if (nonInteractive)
        {
            parsedOptions = new Dictionary<string, string?>(parsedOptions)
            {
                ["non-interactive"] = "true",
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
        if (shouldWriteManifest)
        {
            manifestPath = jsonFile!.Replace(".json", "-manifest.json", StringComparison.OrdinalIgnoreCase);
            if (manifestPath == jsonFile) manifestPath = jsonFile + ".manifest.json";
        }

        var configHash = config is null ? null : CanonicalConfigHasher.Compute(config);
        var policyLockPath = Path.Combine(workingDir, ".rexo", "rexo.lock.yaml");
        string? policyLockHash = null;
        if (File.Exists(policyLockPath))
        {
            await using var lockStream = File.OpenRead(policyLockPath);
            policyLockHash = Convert.ToHexString(await SHA256.HashDataAsync(lockStream, cancellationToken)).ToLowerInvariant();
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
            PolicyLockHash = policyLockHash,
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
                        ContainerFallbackPolicy = GetStringOutput(s.Outputs, "__containerFallbackPolicy"),
                        ContainerFallbackAllowed = GetBoolOutput(s.Outputs, "__containerFallbackAllowed"),
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

        if (manifestPath is not null)
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

    private static (IReadOnlyList<string> cleanArgs, bool json, string? jsonFile, bool verbose, bool debug, bool quiet, bool? dryRun, bool nonInteractive, bool? color, IReadOnlyList<string> setOverrides) ParseGlobalFlags(string[] args)
    {
        var clean = new List<string>();
        var json = false;
        string? jsonFile = null;
        var verbose = false;
        var debug = false;
        var quiet = false;
        bool? dryRun = null;
        var nonInteractive = false;
        bool? color = null;
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
                case "--non-interactive":
                    nonInteractive = true;
                    break;
                case "--color":
                    color = true;
                    break;
                case "--no-color":
                    color = false;
                    break;
                case "--set" when i + 1 < args.Length:
                    setOverrides.Add(args[++i]);
                    break;
                default:
                    clean.Add(args[i]);
                    break;
            }
        }

        return (clean, json, jsonFile, verbose, debug, quiet, dryRun, nonInteractive, color, setOverrides);
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
        Console.WriteLine("  check [--strict]            Safely assess repository readiness");
        Console.WriteLine("  graph <command>             Show effective command steps (text|json|mermaid)");
        Console.WriteLine("  completion <shell>          Generate bash, zsh, fish, or PowerShell completions");
        Console.WriteLine("  restore                     Verify configured policy sources against the lockfile");
        Console.WriteLine("  update                      Resolve and refresh the policy lockfile");
        Console.WriteLine("  promote <manifest> <env>    Promote a verified local file artifact without rebuilding");
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
        Console.WriteLine("      --format                yaml (default) or json");
        Console.WriteLine("      --with-policy           Also create a policy file in .rexo/ from an embedded policy");
        Console.WriteLine("      --policy                standard|dotnet (or any embedded policy name)");
        Console.WriteLine("      --with-docker-artifact  Add starter docker artifact to generated config");
        Console.WriteLine("      --without-docker-artifact  Skip docker artifact scaffolding (non-interactive)");
        Console.WriteLine("      --with-instructions     Download rexo.instructions.md into repo");
        Console.WriteLine("      --instructions-path     Repo-relative destination");
        Console.WriteLine("      --force                 Overwrite existing config");
        Console.WriteLine("  run <command>               Run a configured command");
        Console.WriteLine("  config resolved [--provenance]  Show the merged config and optional source files");
        Console.WriteLine("  config sources              Show config file sources in merge order");
        Console.WriteLine("  config explain <path>       Show an effective property value (sensitive values redacted)");
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
        Console.WriteLine("  --non-interactive           Disable interactive prompts");
        Console.WriteLine("  --color / --no-color        Force color output on or off (NO_COLOR disables by default)");
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
