namespace Rexo.Execution;

using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Rexo.Configuration;
using Rexo.Ci;
using Rexo.Configuration.Models;
using Rexo.Core.Environment;
using Rexo.Core.Models;
using Rexo.Execution.Secrets;
using Rexo.Git;
using Rexo.Policies;

public static class BuiltinCommandRegistration
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient HttpClient = new();
    private static readonly string[] InitTemplateChoices = ["dotnet", "node", "python", "go", "java", "ruby", "generic", "blank"];
    private static readonly string[] InitSchemaSourceChoices = ["remote", "local"];
    private static readonly string[] InitYesNoChoices = ["yes", "no"];
    private const string DefaultInstructionsPath = ".github/instructions/rexo.instructions.md";
    private const string InstructionsTemplateUrl = RepoConfigurationLoader.RawGitHubBaseUrl + "release/next/docs/rexo.instructions.md";

    public static CommandRegistry CreateDefault(RepoConfig? config = null, string? configPath = null)
    {
        var registry = new CommandRegistry();

        registry.Register("version", (_, _) =>
            Task.FromResult(CommandResult.Ok("version", GetVersion())));

        registry.Register("doctor", async (invocation, ct) =>
            await RunDoctorAsync(invocation, config, ct));

        registry.Register("secrets doctor", async (invocation, ct) =>
            await RunSecretsDoctorAsync(invocation, config, ct));

        registry.Register("secrets preflight", async (invocation, ct) =>
            await RunSecretsDoctorAsync(invocation, config, ct));

        registry.Register("capabilities", (_, _) =>
            Task.FromResult(RunCapabilities()));

        registry.Register("list", (invocation, _) =>
            Task.FromResult(RunList(invocation, registry, config)));

        registry.Register("explain", (invocation, ct) =>
            Task.FromResult(RunExplain(invocation, config)));

        registry.Register("config resolved", (_, _) =>
            Task.FromResult(RunConfigResolved(config)));

        registry.Register("config sources", (invocation, _) =>
            Task.FromResult(RunConfigSources(invocation, configPath, config)));

        registry.Register("config materialize", async (invocation, ct) =>
            await RunConfigMaterializeAsync(invocation, config, ct));

        registry.Register("init", async (invocation, ct) =>
            await RunInitAsync(invocation, ct));

        registry.Register("new", async (invocation, ct) =>
            await RunInitAsync(invocation, ct));

        registry.Register("explain version", (_, _) =>
            Task.FromResult(RunExplainVersion(config)));

        registry.Register("policies list", (_, _) =>
            Task.FromResult(RunTemplatesList()));

        registry.Register("policies show", (invocation, _) =>
            Task.FromResult(RunTemplatesShow(invocation)));

        return registry;
    }

    private static string GetVersion()
    {
        var assembly = typeof(BuiltinCommandRegistration).Assembly;
        var infoVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        return infoVersion ?? assembly.GetName().Version?.ToString() ?? "0.1.0-local";
    }

    private static async Task<CommandResult> RunDoctorAsync(
        CommandInvocation invocation,
        RepoConfig? config,
        CancellationToken cancellationToken)
    {
        var checks = new List<(string Name, bool Passed, string? Detail)>();

        // Git
        var gitInfo = await GitDetector.DetectAsync(invocation.WorkingDirectory, cancellationToken);
        checks.Add(("git", gitInfo.Branch is not null, gitInfo.Branch ?? "not a git repo or git not found"));

        // dotnet
        var dotnetOk = await IsToolAvailableAsync("dotnet", "--version", cancellationToken);
        checks.Add(("dotnet", dotnetOk.ok, dotnetOk.version));

        // docker (only if docker artifacts configured)
        var needsDocker = config?.Artifacts?.Any(a => a.Type == "docker") == true;
        if (needsDocker)
        {
            var dockerOk = await IsToolAvailableAsync("docker", "--version", cancellationToken);
            checks.Add(("docker", dockerOk.ok, dockerOk.version));
        }

        // helm (only if helm or helm-oci artifacts configured)
        var needsHelm = config?.Artifacts?.Any(a => a.Type is "helm-oci" or "helm") == true;
        if (needsHelm)
        {
            var helmOk = await IsToolAvailableAsync("helm", "version --short", cancellationToken);
            checks.Add(("helm", helmOk.ok, helmOk.ok ? helmOk.version : "not found — required for helm/helm-oci artifacts"));
        }

        // docker compose (only if docker-compose artifacts configured)
        var needsDockerCompose = config?.Artifacts?.Any(a => a.Type == "docker-compose") == true;
        if (needsDockerCompose)
        {
            var dcOk = await IsToolAvailableAsync("docker", "compose version", cancellationToken);
            checks.Add(("docker compose", dcOk.ok, dcOk.ok ? dcOk.version : "not found — required for docker-compose artifacts"));
        }

        // npm (only if npm artifacts configured)
        var needsNpm = config?.Artifacts?.Any(a => a.Type == "npm") == true;
        if (needsNpm)
        {
            var npmOk = await IsToolAvailableAsync("npm", "--version", cancellationToken);
            checks.Add(("npm", npmOk.ok, npmOk.ok ? npmOk.version : "not found — required for npm artifacts"));
        }

        // python (only if pypi artifacts configured)
        var needsPython = config?.Artifacts?.Any(a => a.Type == "pypi") == true;
        if (needsPython)
        {
            var pythonOk = await IsToolAvailableAsync("python", "--version", cancellationToken);
            if (!pythonOk.ok)
                pythonOk = await IsToolAvailableAsync("python3", "--version", cancellationToken);
            checks.Add(("python", pythonOk.ok, pythonOk.ok ? pythonOk.version : "not found — required for pypi artifacts"));
        }

        // mvn (only if maven artifacts configured)
        var needsMaven = config?.Artifacts?.Any(a => a.Type == "maven") == true;
        if (needsMaven)
        {
            var mvnOk = await IsToolAvailableAsync("mvn", "--version", cancellationToken);
            checks.Add(("mvn", mvnOk.ok, mvnOk.ok ? mvnOk.version : "not found — required for maven artifacts"));
        }

        // gradle (only if gradle artifacts configured)
        var needsGradle = config?.Artifacts?.Any(a => a.Type == "gradle") == true;
        if (needsGradle)
        {
            var gradleOk = await IsToolAvailableAsync("gradle", "--version", cancellationToken);
            checks.Add(("gradle", gradleOk.ok, gradleOk.ok ? gradleOk.version : "not found — required for gradle artifacts (or use Gradle wrapper)"));
        }

        // gem (only if rubygems artifacts configured)
        var needsGem = config?.Artifacts?.Any(a => a.Type == "rubygems") == true;
        if (needsGem)
        {
            var gemOk = await IsToolAvailableAsync("gem", "--version", cancellationToken);
            checks.Add(("gem", gemOk.ok, gemOk.ok ? gemOk.version : "not found — required for rubygems artifacts"));
        }

        // terraform (only if terraform artifacts configured)
        var needsTerraform = config?.Artifacts?.Any(a => a.Type == "terraform") == true;
        if (needsTerraform)
        {
            var tfOk = await IsToolAvailableAsync("terraform", "--version", cancellationToken);
            checks.Add(("terraform", tfOk.ok, tfOk.ok ? tfOk.version : "not found — required for terraform artifacts"));
        }

        // version provider tool (only if an external tool is required)
        var versionProvider = config?.Versioning?.Provider;
        switch (versionProvider?.ToLowerInvariant())
        {
            case "gitversion":
                {
                    var gvOk = await IsToolAvailableAsync("gitversion", "/version", cancellationToken);
                    if (!gvOk.ok)
                        gvOk = await IsToolAvailableAsync("dotnet-gitversion", "/version", cancellationToken);
                    checks.Add(("gitversion", gvOk.ok, gvOk.ok ? gvOk.version : "not found — install via 'dotnet tool install GitVersion.Tool'"));
                    break;
                }
            case "minver":
                {
                    var minverOk = await IsToolAvailableAsync("dotnet", "minver --version", cancellationToken);
                    checks.Add(("minver", minverOk.ok, minverOk.ok ? minverOk.version : "not found — install via 'dotnet tool install minver-cli'"));
                    break;
                }
            case "nbgv":
                {
                    var nbgvOk = await IsToolAvailableAsync("nbgv", "--version", cancellationToken);
                    checks.Add(("nbgv", nbgvOk.ok, nbgvOk.ok ? nbgvOk.version : "not found — install via 'dotnet tool install nbgv'"));
                    break;
                }
        }

        // config file
        var configPath = ConfigFileLocator.FindConfigPath(invocation.WorkingDirectory);
        checks.Add((
            "config",
            configPath is not null,
            configPath is not null ? $"found ({Path.GetFileName(configPath)})" : "not found (expected rexo.json/rexo.yml in root or .rexo/)"));

        if (configPath is not null)
        {
            var configDirectory = Path.GetDirectoryName(configPath) ?? invocation.WorkingDirectory;
            string[] schemaPathCandidates =
            [
                Path.Combine(configDirectory, RepoConfigurationLoader.SupportedRexoSchemaPath),
                Path.Combine(configDirectory, "..", RepoConfigurationLoader.SupportedRexoSchemaPath),
                Path.Combine(configDirectory, ".rexo", RepoConfigurationLoader.SupportedRexoSchemaPath),
            ];
            var schemaPath = schemaPathCandidates.FirstOrDefault(File.Exists);

            checks.Add(schemaPath is not null
                ? ("schema", true, $"local rexo schema ({Path.GetFullPath(schemaPath)})")
                : ("schema", true, "embedded fallback (no local rexo.schema.json found)"));
        }

        // CI context
        var ciInfo = CiDetector.Detect();
        if (ciInfo.IsCi)
        {
            checks.Add(("ci", true, $"Running in CI ({ciInfo.Provider})"));
        }

        var allPassed = checks.All(c => c.Passed);
        var lines = checks.Select(c => $"  [{(c.Passed ? "OK" : "FAIL")}] {c.Name}: {c.Detail ?? "ok"}");
        var message = $"Doctor results:\n{string.Join("\n", lines)}";

        return new CommandResult("doctor", allPassed, allPassed ? 0 : 9, message,
            new Dictionary<string, object?>());
    }

    private static CommandResult RunList(
        CommandInvocation invocation,
        CommandRegistry registry,
        RepoConfig? config)
    {
        var includeHidden = invocation.Options.TryGetValue("include-hidden", out var includeHiddenValue) &&
            string.Equals(includeHiddenValue, "true", StringComparison.OrdinalIgnoreCase);

        var lines = new List<string>();
        lines.Add("Built-in commands:");
        lines.Add("  version              Show the version of repo");
        lines.Add("  list                 List all available commands");
        lines.Add("  explain <command>    Explain a command (or alias)");
        lines.Add("  explain version      Show version provider configuration");
        lines.Add("  doctor               Check environment and configuration");
        lines.Add("  secrets doctor       Validate and summarize configured secrets");
        lines.Add("  secrets preflight    Alias for secrets doctor");
        lines.Add("  capabilities         Show runtime capability contract and supported features");
        lines.Add("  init                 Create a starter rexo config");
        lines.Add("  init detect          Preview auto detection and recommendations");
        lines.Add("  new                  Alias for init");
        lines.Add("  run <command>        Run a config-defined command");
        lines.Add("  config resolved      Show the fully-merged configuration");
        lines.Add("  config sources       Show config file sources in merge order");
        lines.Add("  config materialize   Write the merged config to a file");
        lines.Add("  policies list        List available embedded policies");
        lines.Add("  policies show        Show an embedded policy's JSON");
        lines.Add("  help                 Show help");
        lines.Add("  ui                   Open the interactive UI");

        if (config is not null && config.Commands?.Count > 0)
        {
            var visibleCommands = config.Commands
                .Where(entry => includeHidden || entry.Value.Hidden != true)
                .ToList();

            if (visibleCommands.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Config-defined commands:");
                foreach (var (name, cmd) in visibleCommands)
                {
                    var desc = cmd.Description ?? string.Empty;
                lines.Add($"  {name,-20} {desc}");
            }
            }
        }

        if (config is not null && config.Aliases?.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Aliases:");
            foreach (var (alias, target) in config.Aliases!)
            {
                lines.Add($"  {alias,-20} -> {target}");
            }
        }

        return CommandResult.Ok("list", string.Join("\n", lines));
    }

    private static CommandResult RunCapabilities()
    {
        var capabilities = RuntimeCapabilityCatalog.SupportedCapabilities
            .OrderBy(capability => capability, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lines = new List<string>
        {
            "Runtime capabilities:",
            $"  contractVersion: {RuntimeCapabilityCatalog.ContractVersion}",
            "  supported:",
        };

        lines.AddRange(capabilities.Select(capability => $"    - {capability}"));

        return new CommandResult(
            "capabilities",
            true,
            0,
            string.Join(Environment.NewLine, lines),
            new Dictionary<string, object?>
            {
                ["contractVersion"] = RuntimeCapabilityCatalog.ContractVersion,
                ["supported"] = capabilities,
            });
    }

    private static CommandResult RunExplain(CommandInvocation invocation, RepoConfig? config)
    {
        // The command name is passed as the first arg
        if (!invocation.Args.TryGetValue("command", out var commandName) || string.IsNullOrEmpty(commandName))
        {
            return CommandResult.Fail("explain", 1, "Usage: repo explain <command>");
        }

        // Check built-ins (including sub-commands)
        var builtins = new[] { "version", "list", "explain", "doctor", "secrets", "secrets doctor", "secrets preflight", "capabilities", "init", "new", "run", "help", "ui",
            "config", "config resolved", "config sources", "config materialize",
            "explain version", "policies", "policies list", "policies show" };
        if (builtins.Contains(commandName, StringComparer.OrdinalIgnoreCase))
        {
            return CommandResult.Ok("explain", $"Built-in command: {commandName}\n  This is a built-in command that is always available.");
        }

        // Check aliases — resolve to target command and show its details
        if (config?.Aliases?.TryGetValue(commandName, out var aliasTarget) == true && aliasTarget is not null)
        {
            var aliasLines = new List<string> { $"Alias: {commandName}  →  {aliasTarget}" };
            if (config.Commands?.TryGetValue(aliasTarget, out var aliasCmd) == true && aliasCmd is not null)
            {
                aliasLines.Add($"Command: {aliasTarget} (via alias)");
                if (!string.IsNullOrEmpty(aliasCmd.Description))
                    aliasLines.Add($"  Description: {aliasCmd.Description}");
                if (aliasCmd.Args is { Count: > 0 })
                {
                    aliasLines.Add("  Arguments:");
                    foreach (var (argName, argCfg) in aliasCmd.Args)
                    {
                        var req = argCfg.Required ? "required" : "optional";
                        aliasLines.Add($"    {argName} ({req}): {argCfg.Description ?? string.Empty}");
                    }
                }
                if (aliasCmd.Options.Count > 0)
                {
                    aliasLines.Add("  Options:");
                    foreach (var (optName, optCfg) in aliasCmd.Options)
                    {
                        var def = optCfg.Default is not null
                            ? $" [default: {FormatOptionDefault(optCfg.Default.Value)}]"
                            : string.Empty;
                        aliasLines.Add($"    --{optName} ({optCfg.Type}){def}");
                    }
                }
                if (aliasCmd.Steps.Count > 0)
                {
                    aliasLines.Add($"  Steps ({aliasCmd.Steps.Count} total):");
                    var aliasStepIndex = 0;
                    foreach (var step in aliasCmd.Steps)
                    {
                        aliasStepIndex++;
                        var stepType = step switch
                        {
                            { Run: not null } => "run",
                            { Uses: not null } => "uses",
                            { Command: not null } => "command",
                            _ => "unknown",
                        };
                        var stepBody = step switch
                        {
                            { Run: not null } => step.Run,
                            { Uses: not null } => step.Uses,
                            { Command: not null } => step.Command,
                            _ => string.Empty,
                        };
                        var id = step.Id is not null ? $"[{step.Id}] " : $"[step-{aliasStepIndex}] ";
                        aliasLines.Add($"    {id}{stepType}: {stepBody}");
                        if (step.When is not null)
                            aliasLines.Add($"        when: {step.When}");
                        if (step.WhenExists == true)
                            aliasLines.Add($"        whenExists: true");
                    }
                }
            }
            return CommandResult.Ok("explain", string.Join("\n", aliasLines));
        }

        // Check config commands
        if (config?.Commands?.TryGetValue(commandName, out var cmd) == true && cmd is not null)
        {
            var lines = new List<string>();
            lines.Add($"Command: {commandName}");
            if (!string.IsNullOrEmpty(cmd.Description))
                lines.Add($"  Description: {cmd.Description}");

            // Show extends chain so user understands which layers contributed
            if (config.Extends is { Count: > 0 })
            {
                lines.Add($"  Layers (extends): {string.Join(", ", config.Extends)}");
            }

            if (!string.IsNullOrEmpty(cmd.Merge))
                lines.Add($"  Layer merge: {cmd.Merge}");

            if (cmd.MaxParallel.HasValue)
                lines.Add($"  Max parallel steps: {cmd.MaxParallel.Value}");

            if (cmd.Args is { Count: > 0 })
            {
                lines.Add("  Arguments:");
                foreach (var (argName, argCfg) in cmd.Args)
                {
                    var req = argCfg.Required ? "required" : "optional";
                    lines.Add($"    {argName} ({req}): {argCfg.Description ?? string.Empty}");
                }
            }

            if (cmd.Options.Count > 0)
            {
                lines.Add("  Options:");
                foreach (var (optName, optCfg) in cmd.Options)
                {
                    var def = optCfg.Default is not null
                        ? $" [default: {FormatOptionDefault(optCfg.Default.Value)}]"
                        : string.Empty;
                    var allowed = optCfg.Allowed is { Length: > 0 }
                        ? $" [allowed: {string.Join(", ", optCfg.Allowed)}]"
                        : string.Empty;
                    lines.Add($"    --{optName} ({optCfg.Type}){def}{allowed}");
                }
            }

            if (cmd.Steps.Count > 0)
            {
                lines.Add($"  Steps ({cmd.Steps.Count} total):");
                var stepIndex = 0;
                foreach (var step in cmd.Steps)
                {
                    stepIndex++;
                    var stepType = step switch
                    {
                        { Run: not null } => "run",
                        { Uses: not null } => "uses",
                        { Command: not null } => "command",
                        _ => "unknown",
                    };
                    var stepBody = step switch
                    {
                        { Run: not null } => step.Run,
                        { Uses: not null } => step.Uses,
                        { Command: not null } => step.Command,
                        _ => string.Empty,
                    };
                    var id = step.Id is not null ? $"[{step.Id}] " : $"[step-{stepIndex}] ";
                    var desc = step.Description is not null ? $" — {step.Description}" : string.Empty;
                    lines.Add($"    {id}{stepType}: {stepBody}{desc}");

                    if (step.When is not null)
                        lines.Add($"        when: {step.When}");
                    if (step.WhenExists == true)
                    {
                        var isSelfRef = string.Equals(step.Command, commandName, StringComparison.OrdinalIgnoreCase);
                        var whenExistsNote = isSelfRef
                            ? "        whenExists: true  [layer continuation — skips when no inner layer contributes steps]"
                            : $"        whenExists: true  [calls '{step.Command}' if it exists]";
                        lines.Add(whenExistsNote);
                    }
                    if (step.Parallel == true)
                        lines.Add($"        parallel: true");
                    if (step.DependsOn is { Length: > 0 })
                        lines.Add($"        dependsOn: {string.Join(", ", step.DependsOn)}");
                    if (step.ContinueOnError == true)
                        lines.Add($"        continueOnError: true");
                    if (step.AlwaysRun == true)
                        lines.Add($"        alwaysRun: true");
                    if (step.OutputPattern is not null)
                        lines.Add($"        outputPattern: {step.OutputPattern}");
                    if (step.OutputFile is not null)
                        lines.Add($"        outputFile: {step.OutputFile}");
                }
            }

            // Push eligibility information
            if (config.Runtime?.Push is not null)
            {
                lines.Add("  Push rules:");
                var push = config.Runtime.Push;
                if (push.Enabled is not null)
                {
                    lines.Add($"    enabled: {push.Enabled.Value.ToString().ToLowerInvariant()}");
                }
                if (push.NoPushInPullRequest is not null)
                {
                    lines.Add($"    noPushInPullRequest: {push.NoPushInPullRequest.Value.ToString().ToLowerInvariant()}");
                }
                if (push.RequireCleanWorkingTree is not null)
                {
                    lines.Add($"    requireCleanWorkingTree: {push.RequireCleanWorkingTree.Value.ToString().ToLowerInvariant()}");
                }
                if (push.Branches is { Length: > 0 })
                {
                    lines.Add($"    branches: [{string.Join(", ", push.Branches)}]");
                }
            }

            // Version provider info
            if (config.Versioning is not null)
            {
                lines.Add($"  Version provider: {config.Versioning.Provider}");
                if (config.Versioning.Fallback is not null)
                    lines.Add($"    Fallback: {config.Versioning.Fallback}");
            }

            return CommandResult.Ok("explain", string.Join("\n", lines));
        }

        return CommandResult.Fail("explain", 8, $"Command '{commandName}' not found.");
    }

    private static async Task<CommandResult> RunSecretsDoctorAsync(
        CommandInvocation invocation,
        RepoConfig? config,
        CancellationToken cancellationToken)
    {
        if (config?.Secrets?.Items is not { Count: > 0 } items)
        {
            return CommandResult.Ok("secrets doctor", "Secrets doctor:\n  No configured secrets found.");
        }

        var fileEnvironment = RepositoryEnvironmentFiles.Load(invocation.WorkingDirectory);
        var resolver = new ConfigSecretResolver(config, fileEnvironment, SecretProviderRegistry.CreateDefault());
        var preflight = await resolver.PreflightRequiredAsync(cancellationToken);

        var lines = new List<string> { "Secrets doctor:" };
        foreach (var (name, definition) in items.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var required = definition.Required
                ?? config.Secrets?.Defaults?.Required
                ?? true;
            var exposeInTemplates = definition.ExposeInTemplates ?? true;

            var resolved = preflight.Metadata.TryGetValue(name, out var metadata);
            var provider = resolved
                ? metadata!.Provider
                : ResolveSecretProviderDisplay(config, definition);
            var source = resolved ? metadata!.Source : "none";
            var status = resolved
                ? "resolved"
                : (required ? "missing-required" : "unresolved-optional");

            var details = new List<string>
            {
                $"provider={provider}",
                $"required={(required ? "yes" : "no")}",
                $"templates={(exposeInTemplates ? "yes" : "no")}",
                $"source={source}",
            };

            if (!string.IsNullOrWhiteSpace(definition.MapToEnv))
            {
                details.Add($"mapToEnv={definition.MapToEnv}");
            }

            lines.Add($"  [{(resolved ? "OK" : "WARN")}] {name}: {status} ({string.Join(", ", details)})");
        }

        if (preflight.Errors.Count > 0)
        {
            lines.Add("  Errors:");
            foreach (var error in preflight.Errors)
            {
                var source = string.IsNullOrWhiteSpace(error.Source) ? "secrets" : error.Source;
                lines.Add($"    - [{error.Code}] {source}: {error.Message}");
            }
        }

        var message = string.Join(Environment.NewLine, lines);
        var result = new CommandResult(
            "secrets doctor",
            preflight.Success,
            preflight.Success ? 0 : 9,
            message,
            new Dictionary<string, object?>
            {
                ["secretsTotal"] = items.Count,
                ["resolvedCount"] = preflight.Metadata.Count,
                ["errorCount"] = preflight.Errors.Count,
            })
        {
            StructuredErrors = preflight.Errors,
        };

        return result;
    }

    private static string ResolveSecretProviderDisplay(RepoConfig config, RepoSecretConfig item)
    {
        if (!string.IsNullOrWhiteSpace(item.Provider))
        {
            return item.Provider;
        }

        if (!string.IsNullOrWhiteSpace(item.ProviderRef)
            && config.Secrets?.Providers is { Count: > 0 }
            && config.Secrets.Providers.TryGetValue(item.ProviderRef, out var provider)
            && !string.IsNullOrWhiteSpace(provider?.Type))
        {
            return provider.Type;
        }

        return config.Secrets?.Defaults?.Provider ?? "env";
    }

    private static string FormatOptionDefault(System.Text.Json.JsonElement value) =>
        value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => value.ToString(),
        };

    private static CommandResult RunConfigResolved(RepoConfig? config)
    {
        if (config is null)
        {
            return CommandResult.Fail("config resolved", 1, "No rexo configuration loaded.");
        }

        var options = IndentedJsonOptions;
        var json = JsonSerializer.Serialize(config, options);
        return CommandResult.Ok("config resolved", json);
    }

    private static CommandResult RunConfigSources(CommandInvocation invocation, string? configPath, RepoConfig? config)
    {
        var lines = new List<string> { "Configuration sources (in merge order):" };

        var resolvedPath = configPath ?? ConfigFileLocator.GetDefaultConfigPath(invocation.WorkingDirectory);
        var exists = File.Exists(resolvedPath);
        lines.Add($"  [config]  [{(exists ? "loaded" : "not found")}] {resolvedPath}");

        if (config?.PolicySources is { Count: > 0 } configPolicySources)
        {
            foreach (var src in configPolicySources)
            {
                lines.Add($"  [policy-source:config] {src}");
            }
        }

        var envPolicySources = Environment.GetEnvironmentVariable("REXO_POLICY_SOURCES");
        if (!string.IsNullOrWhiteSpace(envPolicySources))
        {
            foreach (var src in envPolicySources.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                lines.Add($"  [policy-source:env]    {src}");
            }
        }

        var policyPath = ConfigFileLocator.FindPolicyPath(invocation.WorkingDirectory);
        if (policyPath is not null)
        {
            lines.Add($"  [policy]  {policyPath}");
        }

        var overlayEnvPath = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        if (!string.IsNullOrEmpty(overlayEnvPath))
        {
            lines.Add($"  [overlay] REXO_OVERLAY={overlayEnvPath}");
        }

        return CommandResult.Ok("config sources", string.Join("\n", lines));
    }

    private static async Task<CommandResult> RunConfigMaterializeAsync(
        CommandInvocation invocation,
        RepoConfig? config,
        CancellationToken cancellationToken)
    {
        if (config is null)
        {
            return CommandResult.Fail("config materialize", 1, "No rexo configuration loaded.");
        }

        var materialized = new List<string>();
        var workingDir = invocation.WorkingDirectory;

        // If using gitversion provider, write GitVersion.yml if absent
        if (string.Equals(config.Versioning?.Provider, "gitversion", StringComparison.OrdinalIgnoreCase))
        {
            var gvPath = Path.Combine(workingDir, "GitVersion.yml");
            if (!File.Exists(gvPath))
            {
                const string gvContent = """
                    mode: ContinuousDeployment
                    branches: {}
                    ignore:
                      sha: []
                    """;
                await File.WriteAllTextAsync(gvPath, gvContent, cancellationToken);
                materialized.Add(gvPath);
                Console.WriteLine($"  Materialized: {gvPath}");
            }
        }

        var message = materialized.Count > 0
            ? $"Materialized {materialized.Count} file(s): {string.Join(", ", materialized)}"
            : "Nothing to materialize.";

        return CommandResult.Ok("config materialize", message);
    }

    private static CommandResult RunTemplatesList()
    {
        var names = Rexo.Policies.EmbeddedPolicyTemplates.TemplateNames;
        var lines = new List<string> { "Embedded policy templates:" };
        foreach (var name in names)
        {
            lines.Add($"  {name}");
        }

        return new CommandResult("policies list", true, 0, string.Join("\n", lines),
            new Dictionary<string, object?> { ["templates"] = names });
    }

    private static CommandResult RunTemplatesShow(CommandInvocation invocation)
    {
        if (!invocation.Args.TryGetValue("name", out var templateName) || string.IsNullOrWhiteSpace(templateName))
        {
            return CommandResult.Fail("policies show", 1, "Usage: rx policies show <name>");
        }

        string json;
        try
        {
            json = Rexo.Policies.EmbeddedPolicyTemplates.ReadTemplate(templateName);
        }
        catch (ArgumentException)
        {
            var available = string.Join(", ", Rexo.Policies.EmbeddedPolicyTemplates.TemplateNames);
            return CommandResult.Fail("policies show", 1,
                $"Policy '{templateName}' not found. Available policies: {available}");
        }

        return CommandResult.Ok("policies show", json);
    }

    private static CommandResult RunExplainVersion(RepoConfig? config)
    {
        if (config?.Versioning is null)
        {
            return CommandResult.Ok("explain version",
                "No versioning configuration found in rexo configuration.\n" +
                "Available providers: auto, fixed, env, git, gitversion, minver, nbgv");
        }

        var v = config.Versioning;
        var lines = new List<string>
        {
            "Versioning configuration:",
            $"  Provider:  {v.Provider}",
        };

        if (!string.IsNullOrEmpty(v.Fallback))
            lines.Add($"  Fallback:  {v.Fallback}");

        if (v.Settings is { Count: > 0 })
        {
            lines.Add("  Settings:");
            foreach (var (k, val) in v.Settings)
                lines.Add($"    {k}: {val}");
        }

        lines.Add(string.Empty);
        lines.Add("Available providers: auto, fixed, env, git, gitversion, minver, nbgv");

        return CommandResult.Ok("explain version", string.Join("\n", lines));
    }

    private static async Task<(bool ok, string? version)> IsToolAvailableAsync(
        string tool,
        string versionArg,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(tool, versionArg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return (false, null);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode == 0, output.Trim().Split('\n')[0].Trim());
        }
        catch (Exception)
        {
            return (false, "not found");
        }
    }

    private static async Task<CommandResult> RunInitAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        var workingDir = invocation.WorkingDirectory;
        var options = invocation.Options;

        var initMode = ReadOption(options, "mode") ?? ReadOption(options, "subcommand");
        if (!string.IsNullOrWhiteSpace(initMode) && initMode.Equals("ci", StringComparison.OrdinalIgnoreCase))
        {
            return await RunInitCiAsync(invocation, cancellationToken);
        }

        var detection = DetectTemplate(workingDir);
        var detectedTemplate = detection.Template;

        if ((!string.IsNullOrWhiteSpace(initMode) &&
            (initMode.Equals("detect", StringComparison.OrdinalIgnoreCase) ||
             initMode.Equals("preview", StringComparison.OrdinalIgnoreCase))) ||
            IsTrue(options, "detect") ||
            IsTrue(options, "dry-run"))
        {
            return RunInitDetect(invocation, detection);
        }

        var force = IsTrue(options, "force");
        var nonInteractive = IsTrue(options, "yes") || IsTrue(options, "non-interactive");

        var existingConfig = ConfigFileLocator.FindConfigPath(workingDir);
        if (existingConfig is not null && !force)
        {
            return CommandResult.Fail(
                "init",
                1,
                $"Configuration already exists at '{existingConfig}'. Use --force to overwrite.");
        }

        var requestedLocation = ReadOption(options, "location");
        var requestedTemplate = ReadOption(options, "stack");
        var template = requestedTemplate ?? detectedTemplate;
        var autoTemplateRequested = string.IsNullOrWhiteSpace(requestedTemplate) || requestedTemplate.Equals("auto", StringComparison.OrdinalIgnoreCase);
        var schemaSource = ReadOption(options, "schema-source") ?? "remote";
        var withPolicy = IsTrue(options, "with-policy");
        var policyTemplate = ReadOption(options, "policy");
        var instructionsPathOption = ReadOption(options, "instructions-path");
        var withInstructions = IsTrue(options, "with-instructions") || !string.IsNullOrWhiteSpace(instructionsPathOption);
        var withDockerArtifact = IsTrue(options, "with-docker-artifact");
        var withoutDockerArtifact = IsTrue(options, "without-docker-artifact");
        var wantArtifacts = false; // set by wizard or implied by detection

        if (withDockerArtifact && withoutDockerArtifact)
        {
            return CommandResult.Fail("init", 1, "Use either --with-docker-artifact or --without-docker-artifact, not both.");
        }

        if (withoutDockerArtifact)
        {
            withDockerArtifact = false;
        }
        else if (detection.HasDockerfile && !options.ContainsKey("with-docker-artifact"))
        {
            // Dockerfile repositories default to scaffolding a docker artifact.
            withDockerArtifact = true;
        }

        if (!string.IsNullOrWhiteSpace(requestedLocation) &&
            !requestedLocation.Equals(".rexo", StringComparison.OrdinalIgnoreCase) &&
            !requestedLocation.Equals("rexo", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Fail(
                "init",
                1,
                "Invalid --location value. 'init' always creates .rexo/rexo.json; root location must be set up manually.");
        }

        if (!nonInteractive)
        {
            Console.WriteLine("Rexo init");
            Console.WriteLine($"Detected repository template: {detectedTemplate}");
            if (detectedTemplate.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Detected .NET project kind: {(detection.DotnetLibrary ? "library" : "app/service")}");
            }

            if (detection.HasDockerfile)
            {
                Console.WriteLine("Detected Dockerfile(s): yes (consider adding a docker artifact after init)");
            }

            template = PromptChoice(
                "Choose starter template:",
                InitTemplateChoices,
                detectedTemplate);

            schemaSource = PromptChoice(
                "Schema source?",
                InitSchemaSourceChoices,
                "remote");

            // Ask whether the repo will publish artifacts. This drives whether
            // embedded:standard lifecycle commands are included in the scaffold.
            var anyArtifactsDetected = detection.HasDockerfile || detection.HasDockerCompose ||
                detection.HasPomXml || detection.HasBuildGradle || detection.HasGemfile ||
                detection.HasTerraform || detection.HasHelmChart;
            var wantArtifactsAnswer = PromptChoice(
                "Will this repo build and publish artifacts? (Docker images, packages, charts, etc.)",
                InitYesNoChoices,
                anyArtifactsDetected ? "yes" : "no");
            wantArtifacts = wantArtifactsAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase);

            if (wantArtifacts && detection.HasDockerfile)
            {
                var addDockerArtifactAnswer = PromptChoice(
                    "Dockerfile detected. Add starter docker artifact config?",
                    InitYesNoChoices,
                    "yes");
                withDockerArtifact = addDockerArtifactAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
            else if (!wantArtifacts)
            {
                // User explicitly said no artifacts — suppress any file-based detection too.
                withDockerArtifact = false;
            }

            var createPolicyAnswer = PromptChoice(
                "Create a starter policy file? (embedded policy is used automatically if none exists)",
                InitYesNoChoices,
                "no");
            withPolicy = createPolicyAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase);

            var createInstructionsAnswer = PromptChoice(
                "Download AI instructions file into this repo?",
                InitYesNoChoices,
                "no");
            withInstructions = createInstructionsAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase);

            if (withPolicy)
            {
                var available = EmbeddedPolicyTemplates.TemplateNames;
                var defaultPolicyTemplate = SelectDefaultPolicyTemplate(template, detection, available, autoTemplateRequested)
                    ?? "standard";

                Console.WriteLine("Tip: run 'rx policies list' and 'rx policies show <name>' to inspect available policies.");

                policyTemplate = PromptChoice(
                    "Choose policy template:",
                    available,
                    defaultPolicyTemplate);
            }

            if (withInstructions)
            {
                instructionsPathOption ??= DefaultInstructionsPath;
                Console.Write($"Instructions path [{instructionsPathOption}] > ");
                var inputPath = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(inputPath))
                {
                    instructionsPathOption = inputPath;
                }
            }
        }
        else if (template.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            template = detectedTemplate;
        }

        if (withPolicy && string.IsNullOrWhiteSpace(policyTemplate))
        {
            var available = EmbeddedPolicyTemplates.TemplateNames;
            policyTemplate = SelectDefaultPolicyTemplate(template, detection, available, autoTemplateRequested);
        }

        template = NormalizeTemplate(template);
        if (template is null)
        {
            return CommandResult.Fail("init", 1, "Invalid --stack value. Use auto|dotnet|node|python|go|java|ruby|generic|blank.");
        }

        schemaSource = NormalizeSchemaSource(schemaSource);
        if (schemaSource is null)
        {
            return CommandResult.Fail("init", 1, "Invalid --schema-source value. Use local|remote.");
        }

        if (withPolicy && string.IsNullOrWhiteSpace(policyTemplate))
        {
            return CommandResult.Fail(
                "init",
                1,
                template!.Equals("blank", StringComparison.OrdinalIgnoreCase)
                    ? "The blank template has no default policy. Specify --policy explicitly, or omit --with-policy."
                    : "No policies are available to initialize.");
        }

        if (withPolicy && !EmbeddedPolicyTemplates.TemplateNames.Contains(policyTemplate!, StringComparer.OrdinalIgnoreCase))
        {
            return CommandResult.Fail(
                "init",
                1,
                $"Invalid --policy value '{policyTemplate}'. Available: {string.Join(", ", EmbeddedPolicyTemplates.TemplateNames)}");
        }

        var configDir = Path.Combine(workingDir, ".rexo");

        string? instructionsTargetPath = null;
        if (withInstructions)
        {
            var relativeInstructionsPath = string.IsNullOrWhiteSpace(instructionsPathOption)
                ? DefaultInstructionsPath
                : instructionsPathOption;

            // Normalize separators so traversal checks behave consistently on all OSes.
            relativeInstructionsPath = relativeInstructionsPath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(relativeInstructionsPath))
            {
                return CommandResult.Fail("init", 1, "Invalid --instructions-path value. Use a repository-relative path.");
            }

            instructionsTargetPath = Path.GetFullPath(Path.Combine(workingDir, relativeInstructionsPath));
            var repoRoot = Path.GetFullPath(workingDir + Path.DirectorySeparatorChar);
            if (!instructionsTargetPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("init", 1, "Invalid --instructions-path value. Path must remain within the repository.");
            }

            if (File.Exists(instructionsTargetPath) && !force)
            {
                return CommandResult.Fail(
                    "init",
                    1,
                    $"Instructions file already exists at '{instructionsTargetPath}'. Use --force to overwrite.");
            }
        }

        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, "rexo.json");
        if (File.Exists(configPath) && !force)
        {
            return CommandResult.Fail("init", 1, $"Target config already exists at '{configPath}'. Use --force to overwrite.");
        }

        var repoName = Path.GetFileName(workingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var schemaValue = schemaSource.Equals("local", StringComparison.OrdinalIgnoreCase)
            ? RepoConfigurationLoader.SupportedRexoSchemaPath
            : RepoConfigurationLoader.SupportedRexoSchemaUri;
        var configJson = BuildStarterConfigJson(
            repoName,
            template,
            schemaValue,
            withPolicy ? policyTemplate : null,
            withDockerArtifact,
            wantArtifacts,
            detection);

        string? rexoSchemaPath = null;
        string? policySchemaPath = null;
        if (schemaSource.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            rexoSchemaPath = Path.Combine(workingDir, ".rexo", RepoConfigurationLoader.SupportedRexoSchemaPath);
            if (File.Exists(rexoSchemaPath) && !force)
            {
                return CommandResult.Fail("init", 1, $"Target schema already exists at '{rexoSchemaPath}'. Use --force to overwrite.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(rexoSchemaPath)!);
            var embeddedRexoSchemaJson = await RepoConfigurationLoader.ReadEmbeddedRexoSchemaJsonAsync(cancellationToken);
            await File.WriteAllTextAsync(rexoSchemaPath, embeddedRexoSchemaJson, cancellationToken);

            if (withPolicy)
            {
                policySchemaPath = Path.Combine(workingDir, ".rexo", RepoConfigurationLoader.SupportedPolicySchemaPath);
                if (File.Exists(policySchemaPath) && !force)
                {
                    return CommandResult.Fail("init", 1, $"Target schema already exists at '{policySchemaPath}'. Use --force to overwrite.");
                }

                var embeddedPolicySchemaJson = await RepoConfigurationLoader.ReadEmbeddedPolicySchemaJsonAsync(cancellationToken);
                await File.WriteAllTextAsync(policySchemaPath, embeddedPolicySchemaJson, cancellationToken);
            }
        }

        await File.WriteAllTextAsync(configPath, configJson, cancellationToken);

        string? policyPath = null;
        if (withPolicy)
        {
            policyPath = Path.Combine(configDir, "policy.json");
            if (File.Exists(policyPath) && !force)
            {
                return CommandResult.Fail(
                    "init",
                    1,
                    $"Target policy already exists at '{policyPath}'. Use --force to overwrite.");
            }

            var policySchemaValue = schemaSource.Equals("local", StringComparison.OrdinalIgnoreCase)
                ? RepoConfigurationLoader.SupportedPolicySchemaPath
                : RepoConfigurationLoader.SupportedPolicySchemaUri;
            var policyJson = EmbeddedPolicyTemplates.ReadTemplate(policyTemplate!);
            policyJson = ApplySchemaMetadata(policyJson, policySchemaValue);
            await File.WriteAllTextAsync(policyPath, policyJson, cancellationToken);
        }

        if (withInstructions)
        {
            var instructionsDirectory = Path.GetDirectoryName(instructionsTargetPath!);
            if (!string.IsNullOrWhiteSpace(instructionsDirectory))
            {
                Directory.CreateDirectory(instructionsDirectory);
            }

            string instructionsContent;
            try
            {
                instructionsContent = await HttpClient.GetStringAsync(InstructionsTemplateUrl, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                return CommandResult.Fail("init", 1, $"Failed to download instructions template: {ex.Message}");
            }

            await File.WriteAllTextAsync(instructionsTargetPath!, instructionsContent, cancellationToken);
        }

        var lines = new List<string>
        {
            $"Initialized Rexo config: {configPath}",
            $"Template: {template}",
            $"Schema source: {schemaSource}",
            rexoSchemaPath is not null ? $"Initialized schema: {rexoSchemaPath}" : "Schema file: not created (remote URL)",
            policySchemaPath is not null ? $"Initialized schema: {policySchemaPath}" : "Policy schema file: not created",
            withPolicy ? $"Policy template: {policyTemplate}" : "Policy template: none",
            withPolicy ? $"Initialized policy: {policyPath}" : "Policy file: not created",
            withInstructions ? $"Initialized instructions: {instructionsTargetPath}" : "Instructions file: not created",
            withDockerArtifact ? "Initialized docker artifact: yes" : "Initialized docker artifact: no",
            detection.HasDockerfile ? "Packaging hint: Dockerfile detected. Consider adding a docker artifact to .rexo/rexo.json." : "Packaging hint: none detected",
            "Policy template tips: run 'rx policies list' and 'rx policies show <name>'",
            "Next steps:",
            "  1. Review and edit rexo.json for your workflow.",
            "  2. Run 'rx list' and then 'rx build' (or your configured command).",
            "  Docs: https://github.com/agile-north/rexo/blob/release/next/docs/CONFIGURATION.md",
        };

        return CommandResult.Ok("init", string.Join(Environment.NewLine, lines));
    }

    private static CommandResult RunInitDetect(CommandInvocation invocation, InitDetection detection)
    {
        var options = invocation.Options;
        var requestedTemplate = ReadOption(options, "stack");
        var template = string.IsNullOrWhiteSpace(requestedTemplate) || requestedTemplate.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? detection.Template
            : requestedTemplate;

        var normalizedTemplate = NormalizeTemplate(template);
        if (normalizedTemplate is null)
        {
            return CommandResult.Fail("init", 1, "Invalid --stack value. Use auto|dotnet|node|python|go|generic|blank.");
        }

        var available = EmbeddedPolicyTemplates.TemplateNames;
        var recommendedPolicyTemplate = SelectDefaultPolicyTemplate(
            normalizedTemplate,
            detection,
            available,
            autoTemplateRequested: string.IsNullOrWhiteSpace(requestedTemplate) || requestedTemplate.Equals("auto", StringComparison.OrdinalIgnoreCase));
        var detectContract = BuildInitDetectContract(
            requestedTemplate,
            normalizedTemplate,
            detection,
            recommendedPolicyTemplate,
            available);

        var lines = new List<string>
        {
            "Init detection preview:",
            $"  detectedTemplate: {detection.Template}",
            $"  resolvedTemplate: {normalizedTemplate}",
            $"  dotnetProjectKind: {(detection.Template.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? (detection.DotnetLibrary ? "library" : "app/service") : "n/a")}",
            $"  hasDockerfile: {(detection.HasDockerfile ? $"yes ({detection.PrimaryDockerfileRelativePath})" : "no")}",
        };

        // Ecosystem signals found
        var ecosystemSignals = new List<string>();
        if (detection.HasDockerfile)
            ecosystemSignals.Add($"Dockerfile ({detection.PrimaryDockerfileRelativePath})");
        if (detection.HasPackageJson)
            ecosystemSignals.Add("package.json (Node.js)");
        if (detection.HasPomXml)
            ecosystemSignals.Add("pom.xml (Maven/Java)");
        if (detection.HasBuildGradle)
            ecosystemSignals.Add("build.gradle (Gradle/Java)");
        if (detection.HasGemfile)
            ecosystemSignals.Add("Gemfile (Ruby)");
        if (detection.HasTerraform)
            ecosystemSignals.Add("*.tf (Terraform)");
        if (detection.HasHelmChart)
            ecosystemSignals.Add("Chart.yaml (Helm)");
        if (detection.HasDockerCompose)
            ecosystemSignals.Add("docker-compose.yml");

        if (ecosystemSignals.Count > 0)
        {
            lines.Add("  detectedSignals:");
            foreach (var signal in ecosystemSignals)
                lines.Add($"    - {signal}");
        }
        else
        {
            lines.Add("  detectedSignals: (none)");
        }

        lines.Add($"  recommendedPolicyTemplate: {recommendedPolicyTemplate ?? "none"}");
        lines.Add("  tips: run 'rx policies list' and 'rx policies show <name>'");

        return new CommandResult(
            "init detect",
            true,
            0,
            string.Join(Environment.NewLine, lines),
            new Dictionary<string, object?>
            {
                ["contractVersion"] = detectContract.ContractVersion,
                ["detectedTemplate"] = detection.Template,
                ["resolvedTemplate"] = normalizedTemplate,
                ["dotnetProjectKind"] = detection.Template.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? (detection.DotnetLibrary ? "library" : "app/service")
                    : null,
                ["hasDockerfile"] = detection.HasDockerfile,
                ["recommendedPolicyTemplate"] = recommendedPolicyTemplate,
                ["availablePolicyTemplates"] = available,
                ["detection"] = detectContract.Detection,
                ["recommendations"] = detectContract.Recommendations,
            });
    }

    private static InitDetectContract BuildInitDetectContract(
        string? requestedTemplate,
        string resolvedTemplate,
        InitDetection detection,
        string? recommendedPolicyTemplate,
        IReadOnlyList<string> availablePolicyTemplates)
    {
        var signals = new List<string>
        {
            $"template-detected:{detection.Template}",
            detection.HasDockerfile ? "dockerfile:present" : "dockerfile:absent",
            detection.Template.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"dotnet-kind:{(detection.DotnetLibrary ? "library" : "app-service")}" : "dotnet-kind:n/a",
        };

        var recommendations = new List<InitRecommendation>();

        if (string.IsNullOrWhiteSpace(requestedTemplate) || requestedTemplate.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            recommendations.Add(new InitRecommendation(
                "starter-template",
                resolvedTemplate,
                0.95,
                [
                    $"Auto detection selected '{resolvedTemplate}'.",
                    $"Signals: {string.Join(", ", signals)}",
                ]));
        }
        else
        {
            recommendations.Add(new InitRecommendation(
                "starter-template",
                resolvedTemplate,
                1.0,
                [
                    $"User explicitly requested template '{requestedTemplate}'.",
                ]));
        }

        if (!string.IsNullOrWhiteSpace(recommendedPolicyTemplate))
        {
            var reasons = new List<string>
            {
                $"'{recommendedPolicyTemplate}' is available in embedded policy templates.",
            };

            recommendations.Add(new InitRecommendation(
                "policy-template",
                recommendedPolicyTemplate,
                recommendedPolicyTemplate.Equals("standard", StringComparison.OrdinalIgnoreCase) ? 0.65 : 0.9,
                reasons));
        }

        recommendations.Add(new InitRecommendation(
            "docker-artifact",
            detection.HasDockerfile ? "consider-enable" : "not-recommended",
            detection.HasDockerfile ? 0.85 : 0.5,
            detection.HasDockerfile
                ? ["Dockerfile detected. Starter docker artifact can speed setup."]
                : ["No Dockerfile detected. Docker artifact is optional."]));

        var detectionPayload = new InitDetectionPayload(
            detection.Template,
            resolvedTemplate,
            detection.Template.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? (detection.DotnetLibrary ? "library" : "app/service")
                : null,
            detection.HasDockerfile,
            signals,
            availablePolicyTemplates);

        return new InitDetectContract("1.1", detectionPayload, recommendations);
    }

    private static async Task<CommandResult> RunInitCiAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        var workingDir = invocation.WorkingDirectory;
        var options = invocation.Options;
        var force = IsTrue(options, "force");

        var provider = NormalizeCiProvider(ReadOption(options, "provider") ?? "both");
        if (provider is null)
        {
            return CommandResult.Fail("init", 1, "Invalid --provider value. Use github|azdo|both.");
        }

        var createdFiles = new List<string>();

        if (provider is "github" or "both")
        {
            var githubPath = Path.Combine(workingDir, ".github", "workflows", "rexo-release.yml");
            if (File.Exists(githubPath) && !force)
            {
                return CommandResult.Fail("init", 1, $"Target CI file already exists at '{githubPath}'. Use --force to overwrite.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(githubPath)!);
            await File.WriteAllTextAsync(githubPath, BuildGitHubActionsCiTemplate(), cancellationToken);
            createdFiles.Add(githubPath);
        }

        if (provider is "azdo" or "both")
        {
            var azdoPath = Path.Combine(workingDir, ".azuredevops", "rexo-release.yml");
            if (File.Exists(azdoPath) && !force)
            {
                return CommandResult.Fail("init", 1, $"Target CI file already exists at '{azdoPath}'. Use --force to overwrite.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(azdoPath)!);
            await File.WriteAllTextAsync(azdoPath, BuildAzureDevOpsCiTemplate(), cancellationToken);
            createdFiles.Add(azdoPath);
        }

        var lines = new List<string>
        {
            $"Initialized CI scaffolding for provider: {provider}",
            "Generated files:",
        };

        lines.AddRange(createdFiles.Select(path => $"  - {path}"));
        lines.Add("Next steps:");
        lines.Add("  1. Ensure a dotnet tool manifest includes rx (dotnet tool restore succeeds)." );
        lines.Add("  2. Configure registry/feed credentials in CI secrets/variables." );
        lines.Add("  3. Enable pipeline trigger rules for your release branches." );

        return CommandResult.Ok("init", string.Join(Environment.NewLine, lines));
    }

    private static InitDetection DetectTemplate(string workingDir)
    {
        var dockerfileCandidates = Directory
            .EnumerateFiles(workingDir, "Dockerfile", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workingDir, path).Replace('\\', '/'))
            .OrderBy(path => path.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasDockerfile = dockerfileCandidates.Count > 0;
        var primaryDockerfile = hasDockerfile ? dockerfileCandidates[0] : null;

        // Detect all ecosystem signals (independent of each other)
        var hasPyproject = File.Exists(Path.Combine(workingDir, "pyproject.toml"));
        var hasRequirements = File.Exists(Path.Combine(workingDir, "requirements.txt"));
        var hasSetupPy = Directory.EnumerateFiles(workingDir, "*.py", SearchOption.TopDirectoryOnly).Any();
        var isPython = hasPyproject || hasRequirements || hasSetupPy;

        var isGo = File.Exists(Path.Combine(workingDir, "go.mod"));

        var csprojFiles = Directory.EnumerateFiles(workingDir, "*.csproj", SearchOption.AllDirectories).ToList();
        var isDotnet = Directory.EnumerateFiles(workingDir, "*.sln", SearchOption.TopDirectoryOnly).Any()
            || csprojFiles.Count > 0;
        var dotnetLibrary = isDotnet && csprojFiles.Count > 0 && csprojFiles.All(IsLibraryProject);

        var hasPackageJson = File.Exists(Path.Combine(workingDir, "package.json"));
        var isNode = hasPackageJson;

        var hasPomXml = File.Exists(Path.Combine(workingDir, "pom.xml"));
        var hasBuildGradle = File.Exists(Path.Combine(workingDir, "build.gradle"))
            || File.Exists(Path.Combine(workingDir, "build.gradle.kts"));
        var hasGemfile = File.Exists(Path.Combine(workingDir, "Gemfile"))
            || Directory.EnumerateFiles(workingDir, "*.gemspec", SearchOption.TopDirectoryOnly).Any();
        var hasTerraform = Directory.EnumerateFiles(workingDir, "*.tf", SearchOption.TopDirectoryOnly).Any();
        var hasHelmChart = File.Exists(Path.Combine(workingDir, "Chart.yaml"));
        var hasDockerCompose = File.Exists(Path.Combine(workingDir, "docker-compose.yml"))
            || File.Exists(Path.Combine(workingDir, "docker-compose.yaml"));

        // Determine primary template (ordered by priority)
        string template;
        if (isPython)
        {
            template = "python";
        }
        else if (isGo)
        {
            template = "go";
        }
        else if (isDotnet)
        {
            template = "dotnet";
        }
        else if (isNode)
        {
            template = "node";
        }
        else if (hasPomXml || hasBuildGradle)
        {
            template = "java";
        }
        else if (hasGemfile)
        {
            template = "ruby";
        }
        else
        {
            template = "generic";
        }

        return new InitDetection(
            Template: template,
            DotnetLibrary: dotnetLibrary,
            HasDockerfile: hasDockerfile,
            PrimaryDockerfileRelativePath: primaryDockerfile,
            HasPackageJson: hasPackageJson,
            HasPomXml: hasPomXml,
            HasBuildGradle: hasBuildGradle,
            HasGemfile: hasGemfile,
            HasTerraform: hasTerraform,
            HasHelmChart: hasHelmChart,
            HasDockerCompose: hasDockerCompose);
    }

    private static bool IsLibraryProject(string csprojPath)
    {
        try
        {
            var document = XDocument.Load(csprojPath);
            var sdk = document.Root?.Attribute("Sdk")?.Value ?? string.Empty;
            if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
                sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var outputType = document
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("OutputType", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            if (!string.IsNullOrWhiteSpace(outputType))
            {
                var normalized = outputType.Trim();
                if (normalized.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("WinExe", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("AppContainerExe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            // Fall back to app/service classification when project metadata cannot be parsed.
            return false;
        }
    }

    private static string? SelectDefaultPolicyTemplate(
        string template,
        InitDetection detection,
        IReadOnlyList<string> available,
        bool autoTemplateRequested)
    {
        if (available.Count == 0)
        {
            return null;
        }

        if (template.Equals("blank", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (template.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && autoTemplateRequested)
        {
            if (available.Contains(template, StringComparer.OrdinalIgnoreCase))
            {
                return template;
            }
        }

        if (available.Contains(template, StringComparer.OrdinalIgnoreCase))
        {
            return template;
        }

        if (available.Contains("standard", StringComparer.OrdinalIgnoreCase))
        {
            return "standard";
        }

        return available[0];
    }

    private sealed record InitDetection(
        string Template,
        bool DotnetLibrary,
        bool HasDockerfile,
        string? PrimaryDockerfileRelativePath,
        bool HasPackageJson = false,
        bool HasPomXml = false,
        bool HasBuildGradle = false,
        bool HasGemfile = false,
        bool HasTerraform = false,
        bool HasHelmChart = false,
        bool HasDockerCompose = false);

    private sealed record InitDetectContract(
        string ContractVersion,
        InitDetectionPayload Detection,
        IReadOnlyList<InitRecommendation> Recommendations);

    private sealed record InitDetectionPayload(
        string DetectedTemplate,
        string ResolvedTemplate,
        string? DotnetProjectKind,
        bool HasDockerfile,
        IReadOnlyList<string> Signals,
        IReadOnlyList<string> AvailablePolicyTemplates);

    private sealed record InitRecommendation(
        string Kind,
        string Value,
        double Confidence,
        IReadOnlyList<string> Reasons);

    private static string? NormalizeCiProvider(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "github" or "azdo" or "both"
                        ? normalized
                        : null;
        }

    private static string BuildGitHubActionsCiTemplate() =>
        string.Join(
            Environment.NewLine,
            [
                "name: rexo-release",
                string.Empty,
                "on:",
                "  push:",
                "    branches:",
                "      - main",
                "      - release/*",
                string.Empty,
                "jobs:",
                "  release:",
                "    runs-on: ubuntu-latest",
                string.Empty,
                "    steps:",
                "      - name: Checkout",
                "        uses: actions/checkout@v4",
                string.Empty,
                "      - name: Setup .NET",
                "        uses: actions/setup-dotnet@v4",
                "        with:",
                "          dotnet-version: '10.0.x'",
                string.Empty,
                "      - name: Restore tools",
                "        run: dotnet tool restore",
                string.Empty,
                "      - name: Release",
                "        run: dotnet tool run rx -- release --push --json-file artifacts/manifests/release.json",
            ]);

    private static string BuildAzureDevOpsCiTemplate() =>
        string.Join(
            Environment.NewLine,
            [
                "trigger:",
                "  branches:",
                "    include:",
                "      - main",
                "      - release/*",
                string.Empty,
                "pool:",
                "  vmImage: ubuntu-latest",
                string.Empty,
                "steps:",
                "  - task: UseDotNet@2",
                "    inputs:",
                "      packageType: sdk",
                "      version: 10.0.x",
                string.Empty,
                "  - script: dotnet tool restore",
                "    displayName: Restore tools",
                string.Empty,
                "  - script: dotnet tool run rx -- release --push --json-file artifacts/manifests/release.json",
                "    displayName: Release",
            ]);

    private static string? NormalizeTemplate(string value)
    {
        var known = new[] { "dotnet", "node", "python", "go", "java", "ruby", "generic", "blank" };
        return known.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : null;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string?> options, string key)
    {
        if (!options.TryGetValue(key, out var value)) return false;
        return string.IsNullOrEmpty(value) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadOption(IReadOnlyDictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) ? value : null;

    private static string PromptChoice(string prompt, IReadOnlyList<string> choices, string defaultChoice)
    {
        Console.WriteLine(prompt);
        Console.WriteLine($"  Choices: {string.Join(", ", choices)}");
        Console.Write($"  [{defaultChoice}] > ");
        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input))
        {
            return defaultChoice;
        }

        var match = choices.FirstOrDefault(c => c.Equals(input, StringComparison.OrdinalIgnoreCase));
        return match ?? defaultChoice;
    }

    private static string BuildStarterConfigJson(
        string repoName,
        string template,
        string schemaValue,
        string? policyTemplate,
        bool withDockerArtifact,
        bool wantArtifacts,
        InitDetection detection)
    {
        var commands = template switch
        {
            "dotnet" => new Dictionary<string, object>
            {
                ["local build"] = new
                {
                    description = "Restore and build the solution locally",
                    steps = new object[]
                    {
                        new { id = "restore", run = "dotnet restore" },
                        new { id = "build", run = "dotnet build -c Release --no-restore" },
                    },
                },
            },
            "node" => new Dictionary<string, object>
            {
                ["local build"] = new
                {
                    description = "Install and build locally",
                    steps = new object[]
                    {
                        new { run = "npm ci" },
                        new { run = "npm run build" },
                    },
                },
                ["local test"] = new
                {
                    description = "Run tests locally",
                    steps = new object[]
                    {
                        new { run = "npm test" },
                    },
                },
            },
            "python" => new Dictionary<string, object>
            {
                ["local build"] = new
                {
                    description = "Install dependencies and run a quick syntax pass locally",
                    steps = new object[]
                    {
                        new { run = "python -m pip install -r requirements.txt" },
                        new { run = "python -m compileall ." },
                    },
                },
                ["local test"] = new
                {
                    description = "Run tests locally",
                    steps = new object[]
                    {
                        new { run = "python -m pytest" },
                    },
                },
            },
            "go" => new Dictionary<string, object>
            {
                ["local build"] = new
                {
                    description = "Download modules and build locally",
                    steps = new object[]
                    {
                        new { run = "go mod download" },
                        new { run = "go build ./..." },
                    },
                },
                ["local test"] = new
                {
                    description = "Run tests locally",
                    steps = new object[]
                    {
                        new { run = "go test ./..." },
                    },
                },
            },
            "blank" => new Dictionary<string, object>
            {
                ["hello"] = new
                {
                    description = "Starter command — replace with your workflow",
                    steps = new object[]
                    {
                        new { run = "echo Hello from Rexo!" },
                    },
                },
            },
            "java" => new Dictionary<string, object>
            {
                ["local build"] = new
                {
                    description = "Build the Java project locally",
                    steps = new object[]
                    {
                        new { run = "mvn package -DskipTests" },
                    },
                },
                ["local test"] = new
                {
                    description = "Run tests locally",
                    steps = new object[]
                    {
                        new { run = "mvn test" },
                    },
                },
            },
            "ruby" => new Dictionary<string, object>
            {
                ["local build"] = new
                {
                    description = "Install dependencies locally",
                    steps = new object[]
                    {
                        new { run = "bundle install" },
                    },
                },
                ["local test"] = new
                {
                    description = "Run tests locally",
                    steps = new object[]
                    {
                        new { run = "bundle exec rake spec" },
                    },
                },
            },
            _ => new Dictionary<string, object>
            {
                ["local build"] = new
                {
                    description = "Starter build command — replace with your real build steps",
                    steps = new object[]
                    {
                        new { run = "echo TODO: replace with real build command" },
                    },
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(policyTemplate) &&
            !template.Equals("blank", StringComparison.OrdinalIgnoreCase))
        {
            commands = RenameCollidingStarterCommands(commands, policyTemplate);
        }

        // Determine whether any artifacts will be scaffolded (blank never gets artifacts).
        // This drives the extends decision: artifacts need lifecycle commands to be useful,
        // so standard policy is added automatically when artifacts are present.
        var isBlank = template.Equals("blank", StringComparison.OrdinalIgnoreCase);
        var hasArtifacts = !isBlank && (
            wantArtifacts ||           // explicit user intent from wizard
            withDockerArtifact ||
            detection.HasDockerCompose ||
            detection.HasPomXml ||
            detection.HasBuildGradle ||
            detection.HasGemfile ||
            detection.HasTerraform ||
            detection.HasHelmChart);

        // extends rules:
        //   blank → never (policy-free by default)
        //   --with-policy → always (explicit opt-in, stack policyTemplate on standard if needed)
        //   artifacts detected → embedded:standard added automatically (lifecycle needed to drive them)
        //   no artifacts → omit (pure command-alias scaffold; user adds policy when ready)
        var needsStandard = !isBlank && (!string.IsNullOrWhiteSpace(policyTemplate) || hasArtifacts);
        string[]? extendsValue = needsStandard
            ? (!string.IsNullOrWhiteSpace(policyTemplate) &&
               !policyTemplate.Equals("standard", StringComparison.OrdinalIgnoreCase)
                ? [$"embedded:{policyTemplate}", "embedded:standard"]
                : ["embedded:standard"])
            : null;

        var doc = new Dictionary<string, object?>
        {
            ["$schema"] = schemaValue,
            ["schemaVersion"] = "1.0",
            ["name"] = string.IsNullOrWhiteSpace(repoName) ? "my-repo" : repoName,
            ["description"] = "Generated by rx init",
            ["versioning"] = new { provider = "auto", fallback = "0.1.0" },
            ["commands"] = commands,
        };

        if (extendsValue is { Length: > 0 })
        {
            doc["extends"] = extendsValue;
        }

        // Collect artifacts to scaffold based on what was detected and what was requested.
        // blank template intentionally omits artifacts — the user adds them explicitly.
        if (!isBlank)
        {
            var artifacts = new List<object>();

            if (withDockerArtifact)
            {
                artifacts.Add(BuildDockerArtifactTemplate(detection));
            }

            if (detection.HasDockerCompose)
            {
                artifacts.Add(new Dictionary<string, object> { ["type"] = "docker-compose" });
            }

            if (detection.HasPomXml)
            {
                artifacts.Add(new Dictionary<string, object> { ["type"] = "maven" });
            }

            if (detection.HasBuildGradle)
            {
                artifacts.Add(new Dictionary<string, object> { ["type"] = "gradle" });
            }

            if (detection.HasGemfile)
            {
                artifacts.Add(new Dictionary<string, object> { ["type"] = "rubygems" });
            }

            if (detection.HasTerraform)
            {
                artifacts.Add(new Dictionary<string, object> { ["type"] = "terraform" });
            }

            if (detection.HasHelmChart)
            {
                artifacts.Add(new Dictionary<string, object> { ["type"] = "helm" });
            }

            if (artifacts.Count > 0)
            {
                doc["artifacts"] = artifacts.ToArray();
            }
        }

        return JsonSerializer.Serialize(doc, IndentedJsonOptions);
    }

    private static Dictionary<string, object> RenameCollidingStarterCommands(
        Dictionary<string, object> starterCommands,
        string policyTemplate)
    {
        var reservedNames = GetPolicyReservedNames(policyTemplate);
        if (reservedNames.Count == 0)
        {
            return starterCommands;
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (commandName, commandDefinition) in starterCommands)
        {
            var effectiveName = commandName;
            if (reservedNames.Contains(commandName))
            {
                effectiveName = GenerateNonConflictingStarterCommandName(commandName, reservedNames, result.Keys);
            }

            result[effectiveName] = commandDefinition;
        }

        return result;
    }

    private static Dictionary<string, object> BuildDockerArtifactTemplate(InitDetection detection)
    {
        var artifact = new Dictionary<string, object>
        {
            ["type"] = "docker",
        };

        var settings = BuildDockerArtifactSettings(detection);
        if (settings is not null)
        {
            artifact["settings"] = settings;
        }

        return artifact;
    }

    private static Dictionary<string, object>? BuildDockerArtifactSettings(InitDetection detection)
    {
        if (!detection.HasDockerfile)
        {
            return null;
        }

        var dockerfilePath = detection.PrimaryDockerfileRelativePath;
        if (string.IsNullOrWhiteSpace(dockerfilePath) ||
            dockerfilePath.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            // Root Dockerfile + '.' context are provider defaults; omit settings entirely.
            return null;
        }

        var settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["dockerfile"] = dockerfilePath,
        };

        var contextPath = Path.GetDirectoryName(dockerfilePath)?.Replace('\\', '/') ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(contextPath))
        {
            settings["context"] = contextPath;
        }

        return settings;
    }

    private static HashSet<string> GetPolicyReservedNames(string policyTemplate)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(EmbeddedPolicyTemplates.ReadTemplate(policyTemplate));
        var root = document.RootElement;

        if (root.TryGetProperty("commands", out var commandsElement) && commandsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in commandsElement.EnumerateObject())
            {
                _ = reserved.Add(property.Name);
            }
        }

        if (root.TryGetProperty("aliases", out var aliasesElement) && aliasesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in aliasesElement.EnumerateObject())
            {
                _ = reserved.Add(property.Name);
            }
        }

        return reserved;
    }

    private static string GenerateNonConflictingStarterCommandName(
        string commandName,
        HashSet<string> reservedNames,
        IEnumerable<string> existingNames)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var candidates = new[]
        {
            $"local {commandName}",
            $"starter {commandName}",
            $"repo {commandName}",
        };

        foreach (var candidate in candidates)
        {
            if (!reservedNames.Contains(candidate) && !existing.Contains(candidate))
            {
                return candidate;
            }
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"local {commandName} {suffix}";
            if (!reservedNames.Contains(candidate) && !existing.Contains(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static string ApplySchemaMetadata(string jsonText, string schemaValue)
    {
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;

        var node = new Dictionary<string, object?>
        {
            ["$schema"] = schemaValue,
            ["schemaVersion"] = RepoConfigurationLoader.SupportedSchemaVersion,
        };

        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("$schema") || property.NameEquals("schemaVersion"))
            {
                continue;
            }

            node[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), IndentedJsonOptions);
        }

        return JsonSerializer.Serialize(node, IndentedJsonOptions);
    }

    private static string? NormalizeSchemaSource(string value)
    {
        if (value.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        if (value.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            return "remote";
        }

        return null;
    }
}

