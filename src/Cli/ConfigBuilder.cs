namespace Rexo.Cli;

using System.Text.Json;
using System.Text.Json.Nodes;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Policies;

/// <summary>
/// Handles config and policy loading, parsing, and merging.
/// Manages embedded policies, local policies, and effective config construction.
/// </summary>
internal static class ConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static async Task<RepoConfig?> LoadConfigAsync(string workingDir, bool debug, CancellationToken cancellationToken)
    {
        var configPath = ConfigFileLocator.FindConfigPath(workingDir)
            ?? ConfigFileLocator.GetDefaultConfigPath(workingDir);

        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, cancellationToken);
            if (debug) Console.WriteLine($"[debug] Loaded config: {configPath} ({config.Name})");
            return config;
        }
        catch (Exception ex)
        {
            Ui.ConsoleRenderer.RenderError($"Failed to load config '{configPath}': {ex.Message}");
            return null;
        }
    }

    public static async Task<PolicyConfig> LoadAndMergePoliciesAsync(RepoConfig config, string workingDir, bool debug, CancellationToken cancellationToken)
    {
        // Layer 1: config-declared policy sources (opt-in, version-controlled in rexo.json)
        var configSources = (IReadOnlyList<string>?)config.PolicySources ?? [];
        var configRemotePolicies = await PolicySourceLoader.LoadPoliciesFromSourcesAsync(configSources, workingDir, debug, cancellationToken);

        // Layer 2: env-declared policy sources (org-enforced, always wins over config-declared)
        var envRemotePolicies = await PolicySourceLoader.LoadPoliciesFromEnvironmentAsync(workingDir, debug, cancellationToken);

        // Layer 3: local policy file (repo-local tweaks, always highest remote priority)
        var localPolicy = await LoadLocalPolicyAsync(workingDir, debug, cancellationToken);

        // Merge order: config sources < env sources < local policy
        var remotePolicies = MergePolicies(configRemotePolicies, envRemotePolicies);
        return MergePolicies(remotePolicies, localPolicy);
    }

    private static async Task<PolicyConfig> LoadLocalPolicyAsync(string workingDir, bool debug, CancellationToken cancellationToken)
    {
        var policyPath = ConfigFileLocator.FindPolicyPath(workingDir);
        if (policyPath is null)
        {
            return new PolicyConfig();
        }

        try
        {
            var policyConfig = await RepoConfigurationLoader.LoadPolicyAsync(policyPath, cancellationToken);
            if (debug && policyConfig is not null) Console.WriteLine($"[debug] Loaded policy: {policyPath}");
            return policyConfig ?? new PolicyConfig();
        }
        catch (Exception ex)
        {
            if (debug) Console.WriteLine($"[debug] Policy load skipped ({policyPath}): {ex.Message}");
            return new PolicyConfig();
        }
    }

    public static PolicyConfig MergePolicies(PolicyConfig? baseline, PolicyConfig? overridePolicy)
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

    public static RepoConfig? MergePolicyIntoEffectiveConfig(RepoConfig? config, PolicyConfig? policy)
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

    /// <summary>
    /// Applies <c>--set key.path=value</c> CLI overrides to the effective config.
    /// This is the final (highest-priority) layer of the config merge pipeline.
    /// Paths are dotted JSON property names (case-insensitive).
    /// Values are parsed as JSON booleans/numbers/null when applicable; otherwise treated as strings.
    /// Returns the effective config and a list of warnings for malformed overrides.
    /// </summary>
    public static (RepoConfig? config, IReadOnlyList<string> warnings) ApplySetOverridesWithWarnings(
        RepoConfig? config,
        IReadOnlyList<string> setOverrides)
    {
        var warnings = new List<string>();

        if (config is null || setOverrides.Count == 0)
        {
            return (config, warnings);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            return (config, warnings);
        }

        foreach (var setOverride in setOverrides)
        {
            var eqIdx = setOverride.IndexOf('=', StringComparison.Ordinal);
            if (eqIdx < 1)
            {
                warnings.Add($"Malformed --set override '{setOverride}': must be in format key.path=value");
                continue;
            }

            var path = setOverride[..eqIdx];
            if (string.IsNullOrWhiteSpace(path))
            {
                warnings.Add($"Malformed --set override '{setOverride}': key path cannot be empty");
                continue;
            }

            var rawValue = setOverride[(eqIdx + 1)..];
            SetAtPath(node, path, rawValue);
        }

        var result = JsonSerializer.Deserialize<RepoConfig>(node.ToJsonString(), JsonOptions) ?? config;
        return (result, warnings);
    }

    /// <summary>
    /// Applies <c>--set key.path=value</c> CLI overrides to the effective config (legacy signature for backward compatibility).
    /// </summary>
    [System.Obsolete("Use ApplySetOverridesWithWarnings instead to get warnings about malformed overrides")]
    public static RepoConfig? ApplySetOverrides(RepoConfig? config, IReadOnlyList<string> setOverrides)
    {
        var (result, _) = ApplySetOverridesWithWarnings(config, setOverrides);
        return result;
    }

    private static void SetAtPath(JsonNode root, string dotPath, string rawValue)
    {
        var parts = dotPath.Split('.');
        var current = root.AsObject();

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            var existingKey = current
                .Select(kv => kv.Key)
                .FirstOrDefault(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase));

            if (existingKey is null)
            {
                current[part] = new JsonObject();
                current = current[part]!.AsObject();
            }
            else if (current[existingKey] is JsonObject childObj)
            {
                current = childObj;
            }
            else
            {
                // Replace null or scalar intermediate node with an object
                current[existingKey] = new JsonObject();
                current = current[existingKey]!.AsObject();
            }
        }

        var leafPart = parts[^1];
        var existingLeafKey = current
            .Select(kv => kv.Key)
            .FirstOrDefault(k => string.Equals(k, leafPart, StringComparison.OrdinalIgnoreCase));
        var actualKey = existingLeafKey ?? leafPart;

        // Parse value: try as JSON literal (true/false/null/number), else treat as string
        JsonNode? jsonValue;
        try
        {
            jsonValue = JsonNode.Parse(rawValue);
        }
        catch (JsonException)
        {
            jsonValue = JsonValue.Create(rawValue);
        }

        current[actualKey] = jsonValue;
    }
}
