namespace Rexo.Configuration;

using System.Collections;
using System.Text.Json;
using NJsonSchema;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Policies;
using YamlDotNet.Serialization;

public sealed partial class RepoConfigurationLoader
{
    public const string DisableSchemaValidationEnvVar = "REXO_DISABLE_SCHEMA_VALIDATION";
    public const string RawGitHubBaseUrl = "https://raw.githubusercontent.com/agile-north/rexo/";
    public const string SupportedRexoSchemaPath = "rexo.schema.json";
    public const string SupportedRexoSchemaPathInRexoFolder = ".rexo/rexo.schema.json";
    public const string SupportedPolicySchemaPath = "policy.schema.json";
    public const string SupportedPolicySchemaPathInRexoFolder = ".rexo/policy.schema.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new RepoCommandConfigJsonConverter() },
    };

    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder().Build();

    public static async Task<RepoConfig> LoadAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Configuration file was not found.", configPath);
        }

        var jsonText = await ReadAsJsonAsync(configPath, cancellationToken);
        if (!IsSchemaValidationDisabled())
        {
            ValidateMetadata(configPath, jsonText);
            await ValidateSchemaAsync(configPath, jsonText, cancellationToken);
        }

        var config = JsonSerializer.Deserialize<RepoConfig>(jsonText, JsonOptions);

        if (config is null)
        {
            throw new InvalidOperationException("Configuration file is empty or invalid.");
        }

        // Resolve extends chain (breadth-first, child overrides base)
        if (config.Extends is { Count: > 0 })
        {
            config = await ResolveExtendsAsync(config, configPath, [], cancellationToken);
        }

        // Apply environment overlay if REXO_OVERLAY is set (overlay wins over everything)
        var overlayEnvPath = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        if (!string.IsNullOrEmpty(overlayEnvPath))
        {
            var configDir = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
            var overlayPath = Path.IsPathRooted(overlayEnvPath)
                ? overlayEnvPath
                : Path.GetFullPath(Path.Combine(configDir, overlayEnvPath));

            if (File.Exists(overlayPath))
            {
                var overlayJson = await ReadAsJsonAsync(overlayPath, cancellationToken);
                var overlayConfig = JsonSerializer.Deserialize<RepoConfig>(overlayJson, JsonOptions);
                if (overlayConfig is not null)
                {
                    config = MergeConfigs(config, overlayConfig);
                }
            }
        }

        ValidateCapabilities(config.Capabilities, "configuration", configPath);

        return config;
    }

    private static async Task<RepoConfig> ResolveExtendsAsync(
        RepoConfig config,
        string configPath,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        var configDir = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
        _ = visited.Add(Path.GetFullPath(configPath));

        // Load and merge all base configs (first listed = lowest priority)
        RepoConfig? merged = null;
        foreach (var extendPath in config.Extends!)
        {
            // Handle embedded: URI scheme — loads a named embedded policy template as a base config
            if (extendPath.StartsWith("embedded:", StringComparison.OrdinalIgnoreCase))
            {
                var templateName = extendPath["embedded:".Length..];
                var templateJson = EmbeddedPolicyTemplates.ReadTemplate(templateName);
                var policyConfig = JsonSerializer.Deserialize<PolicyConfig>(templateJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Embedded template '{templateName}' is empty or invalid.");

                var embeddedBase = new RepoConfig(
                    Name: templateName,
                    Commands: policyConfig.Commands is { Count: > 0 } ? policyConfig.Commands : null,
                    Aliases: policyConfig.Aliases is { Count: > 0 } ? policyConfig.Aliases : null);

                merged = merged is null ? embeddedBase : MergeConfigs(merged, embeddedBase, policyMerge: true);
                continue;
            }

            var basePath = Path.IsPathRooted(extendPath)
                ? extendPath
                : Path.GetFullPath(Path.Combine(configDir, extendPath));

            if (visited.Contains(basePath))
            {
                throw new InvalidOperationException(
                    $"Circular 'extends' reference detected for '{basePath}'.");
            }

            if (!File.Exists(basePath))
            {
                throw new FileNotFoundException(
                    $"Extended config file not found: '{basePath}'.", basePath);
            }

            var baseJsonText = await ReadAsJsonAsync(basePath, cancellationToken);
            if (!IsSchemaValidationDisabled())
            {
                ValidateMetadata(basePath, baseJsonText);
                await ValidateSchemaAsync(basePath, baseJsonText, cancellationToken);
            }
            var baseConfig = JsonSerializer.Deserialize<RepoConfig>(baseJsonText, JsonOptions)
                ?? throw new InvalidOperationException($"Extended config '{basePath}' is empty or invalid.");

            if (baseConfig.Extends is { Count: > 0 })
            {
                baseConfig = await ResolveExtendsAsync(baseConfig, basePath, visited, cancellationToken);
            }

            merged = merged is null ? baseConfig : MergeConfigs(merged, baseConfig);
        }

        // Child config takes priority over merged base
        return merged is null ? config : MergeConfigs(merged, config);
    }

    public static async Task<PolicyConfig?> LoadPolicyAsync(string policyPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(policyPath))
        {
            throw new FileNotFoundException("Policy file was not found.", policyPath);
        }

        var policyJson = await ReadAsJsonAsync(policyPath, cancellationToken);
        ValidatePolicyMetadata(policyPath, policyJson);
        await ValidatePolicySchemaAsync(policyPath, policyJson, cancellationToken);
        var policyConfig = JsonSerializer.Deserialize<PolicyConfig>(policyJson, JsonOptions);
        if (policyConfig is not null)
        {
            ValidateCapabilities(policyConfig.Capabilities, "policy", policyPath);
        }

        return policyConfig;
    }

    public static async Task<string> ReadEmbeddedRexoSchemaJsonAsync(CancellationToken cancellationToken)
    {
        var asm = typeof(RepoConfigurationLoader).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedRexoSchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema resource '{EmbeddedRexoSchemaResourceName}' was not found in the assembly.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public static async Task<string> ReadEmbeddedPolicySchemaJsonAsync(CancellationToken cancellationToken)
    {
        var asm = typeof(RepoConfigurationLoader).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedPolicySchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema resource '{EmbeddedPolicySchemaResourceName}' was not found in the assembly.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Merges <paramref name="child"/> on top of <paramref name="base"/>:
    /// child wins for scalar properties; collections are combined (child appended after base).
    /// </summary>
    private static RepoConfig MergeConfigs(RepoConfig @base, RepoConfig child, bool policyMerge = false) =>
        new(
            Name: string.IsNullOrEmpty(child.Name) ? @base.Name : child.Name,
            Commands: MergeCommandDictionaries(@base.Commands, child.Commands, policyMerge),
            Aliases: MergeDictionaries(@base.Aliases, child.Aliases))
        {
            Schema = child.Schema ?? @base.Schema,
            SchemaVersion = child.SchemaVersion ?? @base.SchemaVersion,
            Description = child.Description ?? @base.Description,
            Version = child.Version ?? @base.Version,
            Extends = null, // consumed — do not propagate
            PolicySources = MergeLists(@base.PolicySources, child.PolicySources, child.MergeStrategy),
            Versioning = child.Versioning ?? @base.Versioning,
            Artifacts = MergeLists(@base.Artifacts, child.Artifacts, child.MergeStrategy),
            Runtime = child.Runtime ?? @base.Runtime,
            Outputs = MergeOutputsConfig(@base.Outputs, child.Outputs),
            Settings = MergeDictionaries(@base.Settings, child.Settings),
            Vars = MergeDictionaries(@base.Vars, child.Vars),
            Capabilities = MergeCapabilities(@base.Capabilities, child.Capabilities, child.MergeStrategy),
            MergeStrategy = child.MergeStrategy ?? @base.MergeStrategy,
        };

    private static RepoCapabilityConfig? MergeCapabilities(
        RepoCapabilityConfig? @base,
        RepoCapabilityConfig? child,
        string? strategy)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoCapabilityConfig(
            ContractVersion: child.ContractVersion ?? @base.ContractVersion,
            Required: MergeLists(@base.Required?.ToList(), child.Required?.ToList(), strategy)?.ToArray());
    }

    private static RepoOutputsConfig? MergeOutputsConfig(RepoOutputsConfig? @base, RepoOutputsConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoOutputsConfig
        {
            Emit = child.Emit ?? @base.Emit,
            Command = MergeCommandOutputsConfig(@base.Command, child.Command),
            Ci = MergeCiOutputsConfig(@base.Ci, child.Ci),
            Root = child.Root ?? @base.Root,
            Tests = MergeTestOutputsConfig(@base.Tests, child.Tests),
            Analysis = MergeAnalysisOutputsConfig(@base.Analysis, child.Analysis),
            Security = MergeSecurityOutputsConfig(@base.Security, child.Security),
            Packages = child.Packages ?? @base.Packages,
            Manifests = MergeManifestOutputsConfig(@base.Manifests, child.Manifests),
            Logs = child.Logs ?? @base.Logs,
            Temp = child.Temp ?? @base.Temp,
        };
    }

    private static RepoManifestOutputsConfig? MergeManifestOutputsConfig(
        RepoManifestOutputsConfig? @base,
        RepoManifestOutputsConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoManifestOutputsConfig
        {
            Path = child.Path ?? @base.Path,
            CommandMode = child.CommandMode ?? @base.CommandMode,
            CommandDetail = child.CommandDetail ?? @base.CommandDetail,
        };
    }

    private static RepoCommandOutputConfig? MergeCommandOutputsConfig(
        RepoCommandOutputConfig? @base,
        RepoCommandOutputConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoCommandOutputConfig
        {
            Stdout = child.Stdout ?? @base.Stdout,
            Json = child.Json ?? @base.Json,
            JsonFile = child.JsonFile ?? @base.JsonFile,
        };
    }

    private static RepoCiOutputsConfig? MergeCiOutputsConfig(
        RepoCiOutputsConfig? @base,
        RepoCiOutputsConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoCiOutputsConfig
        {
            Emit = child.Emit ?? @base.Emit,
            Provider = child.Provider ?? @base.Provider,
            GitHubActions = MergeGitHubActionsCiOutputsConfig(@base.GitHubActions, child.GitHubActions),
            Prefix = child.Prefix ?? @base.Prefix,
            KeyCasing = child.KeyCasing ?? @base.KeyCasing,
            Scope = child.Scope ?? @base.Scope,
            IncludeStepOutputs = child.IncludeStepOutputs ?? @base.IncludeStepOutputs,
            EmitEmptyValues = child.EmitEmptyValues ?? @base.EmitEmptyValues,
            Redact = child.Redact ?? @base.Redact,
            FailOnError = child.FailOnError ?? @base.FailOnError,
            MaxValueLength = child.MaxValueLength ?? @base.MaxValueLength,
            MaxVariables = child.MaxVariables ?? @base.MaxVariables,
        };
    }

    private static RepoGitHubActionsCiOutputsConfig? MergeGitHubActionsCiOutputsConfig(
        RepoGitHubActionsCiOutputsConfig? @base,
        RepoGitHubActionsCiOutputsConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoGitHubActionsCiOutputsConfig
        {
            Scope = child.Scope ?? @base.Scope,
        };
    }

    private static RepoTestOutputPathsConfig? MergeTestOutputsConfig(
        RepoTestOutputPathsConfig? @base,
        RepoTestOutputPathsConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoTestOutputPathsConfig
        {
            Results = child.Results ?? @base.Results,
            Coverage = child.Coverage ?? @base.Coverage,
            Reports = child.Reports ?? @base.Reports,
        };
    }

    private static RepoAnalysisOutputPathsConfig? MergeAnalysisOutputsConfig(
        RepoAnalysisOutputPathsConfig? @base,
        RepoAnalysisOutputPathsConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoAnalysisOutputPathsConfig
        {
            Reports = child.Reports ?? @base.Reports,
            Sarif = child.Sarif ?? @base.Sarif,
        };
    }

    private static RepoSecurityOutputPathsConfig? MergeSecurityOutputsConfig(
        RepoSecurityOutputPathsConfig? @base,
        RepoSecurityOutputPathsConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;

        return new RepoSecurityOutputPathsConfig
        {
            Audit = child.Audit ?? @base.Audit,
            Reports = child.Reports ?? @base.Reports,
            Sarif = child.Sarif ?? @base.Sarif,
        };
    }

    private static Dictionary<string, RepoCommandConfig>? MergeCommandDictionaries(
        Dictionary<string, RepoCommandConfig>? @base,
        Dictionary<string, RepoCommandConfig>? child,
        bool policyMerge = false)
    {
        if (@base is null or { Count: 0 }) return child;
        if (child is null or { Count: 0 }) return @base;

        var result = new Dictionary<string, RepoCommandConfig>(@base);
        foreach (var kvp in child)
        {
            if (result.TryGetValue(kvp.Key, out var baseCmd))
            {
                result[kvp.Key] = MergeCommandConfigs(kvp.Key, baseCmd, kvp.Value, policyMerge);
            }
            else
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    private static RepoCommandConfig MergeCommandConfigs(
        string commandName,
        RepoCommandConfig baseCmd,
        RepoCommandConfig childCmd,
        bool policyMerge = false)
    {
        var effectiveMode = childCmd.MergeConfig?.Mode ?? childCmd.Merge;
        var effectiveStepOps = childCmd.MergeConfig?.Steps ?? childCmd.StepOps;

        if (effectiveStepOps is not null)
        {
            var baseForOperations = BuildBaseCommandForStepOperations(baseCmd, childCmd, effectiveMode, effectiveStepOps);
            return ApplyStepOperations(baseForOperations, effectiveStepOps);
        }

        var explicitMerge = effectiveMode;

        if (explicitMerge is not null)
        {
            return explicitMerge.ToUpperInvariant() switch
            {
                "LAYER" => baseCmd,
                "REPLACE" => childCmd,
                "APPEND" => MergeCommandAppend(baseCmd, childCmd),
                "PREPEND" => MergeCommandPrepend(baseCmd, childCmd),
                "WRAP" => MergeCommandWrap(commandName, baseCmd, childCmd),
                _ => childCmd,
            };
        }

        if (policyMerge)
        {
            // Auto-detect composition mode based on self-referential continuation steps.
            var childHasSelfRef = childCmd.Steps.Exists(s =>
                string.Equals(s.Command, commandName, StringComparison.OrdinalIgnoreCase));

            if (childHasSelfRef)
            {
                return MergeCommandWrap(commandName, baseCmd, childCmd);
            }

            var baseHasSelfRef = baseCmd.Steps.Exists(s =>
                string.Equals(s.Command, commandName, StringComparison.OrdinalIgnoreCase));

            if (baseHasSelfRef)
            {
                return MergeCommandBaseWrap(commandName, baseCmd, childCmd);
            }

            // Neither side has a self-ref: base layer wins (child layer is suppressed).
            return baseCmd;
        }

        // Default (non-policy, no explicit merge): child replaces base.
        return childCmd;
    }

    private static RepoCommandConfig BuildBaseCommandForStepOperations(
        RepoCommandConfig baseCmd,
        RepoCommandConfig childCmd,
        string? effectiveMode,
        RepoCommandStepOpsConfig effectiveStepOps)
    {
        return new RepoCommandConfig(
            Description: childCmd.Description ?? baseCmd.Description,
            Options: MergeDictionaries(baseCmd.Options, childCmd.Options) ?? [],
            Steps: baseCmd.Steps ?? [])
        {
            Hidden = childCmd.Hidden ?? baseCmd.Hidden,
            Args = MergeDictionaries(baseCmd.Args, childCmd.Args),
            MergeConfig = childCmd.MergeConfig,
            Merge = effectiveMode,
            MaxParallel = childCmd.MaxParallel ?? baseCmd.MaxParallel,
            StepOps = effectiveStepOps,
        };
    }

    private static RepoCommandConfig ApplyStepOperations(
        RepoCommandConfig command,
        RepoCommandStepOpsConfig operations)
    {
        var steps = new List<RepoStepConfig>(command.Steps ?? []);

        // Deterministic order: remove -> replace -> prepend -> append
        if (operations.Remove is { Length: > 0 })
        {
            var ids = new HashSet<string>(operations.Remove, StringComparer.OrdinalIgnoreCase);
            _ = steps.RemoveAll(step => step.Id is not null && ids.Contains(step.Id));
        }

        if (operations.Replace is { Count: > 0 })
        {
            foreach (var replacement in operations.Replace)
            {
                var replacementIndex = steps.FindIndex(step =>
                    string.Equals(step.Id, replacement.Id, StringComparison.OrdinalIgnoreCase));

                if (replacementIndex < 0)
                {
                    continue;
                }

                var replacementStep = replacement.Step.Id is null
                    ? replacement.Step with { Id = replacement.Id }
                    : replacement.Step;

                steps[replacementIndex] = replacementStep;
            }
        }

        if (operations.Prepend is { Count: > 0 })
        {
            var prepended = new List<RepoStepConfig>(operations.Prepend);
            prepended.AddRange(steps);
            steps = prepended;
        }

        if (operations.Append is { Count: > 0 })
        {
            steps.AddRange(operations.Append);
        }

        return command with { Steps = steps };
    }

    private static RepoCommandConfig MergeCommandAppend(RepoCommandConfig baseCmd, RepoCommandConfig childCmd)
    {
        var steps = new List<RepoStepConfig>(baseCmd.Steps);
        steps.AddRange(childCmd.Steps);
        return baseCmd with { Steps = steps };
    }

    private static RepoCommandConfig MergeCommandPrepend(RepoCommandConfig baseCmd, RepoCommandConfig childCmd)
    {
        var steps = new List<RepoStepConfig>(childCmd.Steps);
        steps.AddRange(baseCmd.Steps);
        return childCmd with { Steps = steps };
    }

    private static RepoCommandConfig MergeCommandWrap(
        string commandName,
        RepoCommandConfig baseCmd,
        RepoCommandConfig childCmd)
    {
        var continuationIdx = childCmd.Steps.FindIndex(s =>
            string.Equals(s.Command, commandName, StringComparison.OrdinalIgnoreCase));

        if (continuationIdx < 0)
        {
            // No continuation marker: fallback — child steps first, base steps after.
            var fallback = new List<RepoStepConfig>(childCmd.Steps);
            fallback.AddRange(baseCmd.Steps);
            return childCmd with { Steps = fallback };
        }

        var steps = new List<RepoStepConfig>(childCmd.Steps.Count - 1 + baseCmd.Steps.Count);
        for (var i = 0; i < childCmd.Steps.Count; i++)
        {
            if (i == continuationIdx)
            {
                steps.AddRange(baseCmd.Steps);
            }
            else
            {
                steps.Add(childCmd.Steps[i]);
            }
        }

        return childCmd with { Steps = steps };
    }

    private static RepoCommandConfig MergeCommandBaseWrap(
        string commandName,
        RepoCommandConfig baseCmd,
        RepoCommandConfig childCmd)
    {
        var continuationIdx = baseCmd.Steps.FindIndex(s =>
            string.Equals(s.Command, commandName, StringComparison.OrdinalIgnoreCase));

        if (continuationIdx < 0)
        {
            // No continuation marker in base: fallback — base steps first, child steps after.
            var fallback = new List<RepoStepConfig>(baseCmd.Steps);
            fallback.AddRange(childCmd.Steps);
            return baseCmd with { Steps = fallback };
        }

        var steps = new List<RepoStepConfig>(baseCmd.Steps.Count - 1 + childCmd.Steps.Count);
        for (var i = 0; i < baseCmd.Steps.Count; i++)
        {
            if (i == continuationIdx)
            {
                steps.AddRange(childCmd.Steps);
            }
            else
            {
                steps.Add(baseCmd.Steps[i]);
            }
        }

        return baseCmd with { Steps = steps };
    }

    private static Dictionary<TKey, TValue> MergeDictionaries<TKey, TValue>(
        Dictionary<TKey, TValue>? @base,
        Dictionary<TKey, TValue>? child)
        where TKey : notnull
    {
        if (@base is null or { Count: 0 }) return child ?? [];
        if (child is null or { Count: 0 }) return @base;

        var result = new Dictionary<TKey, TValue>(@base);
        foreach (var kvp in child)
        {
            result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    /// <summary>
    /// Merges two lists according to the specified strategy.
    /// <list type="bullet">
    ///   <item><c>replace</c> — child list discards base; only child entries are kept.</item>
    ///   <item><c>prepend</c> — child entries are inserted before base entries.</item>
    ///   <item>Default (<c>union</c>) — child entries are appended after base entries.</item>
    /// </list>
    /// </summary>
    private static List<T>? MergeLists<T>(List<T>? @base, List<T>? child, string? strategy = null)
    {
        if (@base is null or { Count: 0 }) return child;
        if (child is null or { Count: 0 }) return @base;

        if (string.Equals(strategy, "replace", StringComparison.OrdinalIgnoreCase))
        {
            return child;
        }

        if (string.Equals(strategy, "prepend", StringComparison.OrdinalIgnoreCase))
        {
            var prepended = new List<T>(child);
            prepended.AddRange(@base);
            return prepended;
        }

        // Default: union (append child after base)
        var merged = new List<T>(@base);
        merged.AddRange(child);
        return merged;
    }

    private static void ValidateMetadata(string configPath, string jsonText)
    {
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        if (!root.TryGetProperty("$schema", out var schemaProp) || schemaProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Configuration must define a '$schema' string property.");
        }

        var schema = schemaProp.GetString();
        var allowedSchemaValues = new[]
        {
            SupportedRexoSchemaUri,
            SupportedRexoSchemaPath,
            "./" + SupportedRexoSchemaPath,
            "../" + SupportedRexoSchemaPath,
            SupportedRexoSchemaPathInRexoFolder,
        };

        if (string.IsNullOrWhiteSpace(schema) || !allowedSchemaValues.Contains(schema, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported '$schema' value '{schema}'. Expected one of: {string.Join(", ", allowedSchemaValues)}");
        }

        if (!root.TryGetProperty("schemaVersion", out var versionProp) || versionProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Configuration must define a 'schemaVersion' string property.");
        }

        var version = versionProp.GetString();
        if (!string.Equals(version, SupportedSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported schemaVersion '{version}'. Supported version is '{SupportedSchemaVersion}'.");
        }
    }

    private static async Task ValidateSchemaAsync(string configPath, string jsonText, CancellationToken cancellationToken)
    {
        var configDirectory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Could not determine config directory.");

        cancellationToken.ThrowIfCancellationRequested();

        string schemaJson;
        JsonSchema schema;
        var schemaPath = FindLocalSchemaPath(configDirectory, SupportedRexoSchemaPath);
        if (schemaPath is not null)
        {
            schemaJson = await File.ReadAllTextAsync(schemaPath, cancellationToken);
            schema = await ParseSchemaWithCompatibilityAsync(schemaJson, cancellationToken);
        }
        else
        {
            // Fall back to the embedded schema so the tool works when installed globally.
            schemaJson = await ReadEmbeddedRexoSchemaJsonAsync(cancellationToken);
            schema = await ParseSchemaWithCompatibilityAsync(schemaJson, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var errors = ValidateWithCompatibilityFallback(schema, schemaJson, jsonText, cancellationToken);
        if (errors.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                errors.Select(e => $"- {e.Path}: {e.Kind} ({e.Property})"));

            throw new InvalidOperationException(
                "Configuration does not match the declared schema. Validation errors:" +
                Environment.NewLine +
                details);
        }
    }

    private static void ValidatePolicyMetadata(string policyPath, string jsonText)
    {
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        if (!root.TryGetProperty("$schema", out var schemaProp) || schemaProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Policy must define a '$schema' string property.");
        }

        var schema = schemaProp.GetString();
        var allowedSchemaValues = new[]
        {
            SupportedPolicySchemaUri,
            SupportedPolicySchemaPath,
            "./" + SupportedPolicySchemaPath,
            "../" + SupportedPolicySchemaPath,
            SupportedPolicySchemaPathInRexoFolder,
        };

        if (string.IsNullOrWhiteSpace(schema) || !allowedSchemaValues.Contains(schema, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported policy '$schema' value '{schema}'. Expected one of: {string.Join(", ", allowedSchemaValues)}");
        }

        if (!root.TryGetProperty("schemaVersion", out var versionProp) || versionProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Policy must define a 'schemaVersion' string property.");
        }

        var version = versionProp.GetString();
        if (!string.Equals(version, SupportedSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported policy schemaVersion '{version}'. Supported version is '{SupportedSchemaVersion}'.");
        }
    }

    private static async Task ValidatePolicySchemaAsync(string policyPath, string jsonText, CancellationToken cancellationToken)
    {
        var policyDirectory = Path.GetDirectoryName(policyPath)
            ?? throw new InvalidOperationException("Could not determine policy directory.");

        cancellationToken.ThrowIfCancellationRequested();

        string schemaJson;
        JsonSchema schema;
        var schemaPath = FindLocalSchemaPath(policyDirectory, SupportedPolicySchemaPath);
        if (schemaPath is not null)
        {
            schemaJson = await File.ReadAllTextAsync(schemaPath, cancellationToken);
            schema = await ParseSchemaWithCompatibilityAsync(schemaJson, cancellationToken);
        }
        else
        {
            schemaJson = await ReadEmbeddedPolicySchemaJsonAsync(cancellationToken);
            schema = await ParseSchemaWithCompatibilityAsync(schemaJson, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var errors = ValidateWithCompatibilityFallback(schema, schemaJson, jsonText, cancellationToken);
        if (errors.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                errors.Select(e => $"- {e.Path}: {e.Kind} ({e.Property})"));

            throw new InvalidOperationException(
                "Policy does not match the declared schema. Validation errors:" +
                Environment.NewLine +
                details);
        }
    }

    private static string? FindLocalSchemaPath(string baseDirectory, string schemaFileName)
    {
        string[] candidates =
        [
            Path.Combine(baseDirectory, schemaFileName),
            Path.Combine(baseDirectory, "..", schemaFileName),
            Path.Combine(baseDirectory, ".rexo", schemaFileName),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static ICollection<NJsonSchema.Validation.ValidationError> ValidateWithCompatibilityFallback(
        JsonSchema schema,
        string schemaJson,
        string jsonText,
        CancellationToken cancellationToken)
    {
        try
        {
            return schema.Validate(jsonText);
        }
        catch (Exception ex) when (NeedsDefsCompatibilityFallback(ex.Message))
        {
            var normalizedSchemaJson = NormalizeSchemaForLegacyDefinitions(schemaJson);
            var normalizedSchema = JsonSchema.FromJsonAsync(normalizedSchemaJson, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            cancellationToken.ThrowIfCancellationRequested();
            return normalizedSchema.Validate(jsonText);
        }
    }

    private static async Task<JsonSchema> ParseSchemaWithCompatibilityAsync(
        string schemaJson,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSchema.FromJsonAsync(schemaJson, CancellationToken.None);
        }
        catch (Exception ex) when (NeedsDefsCompatibilityFallback(ex.Message))
        {
            var normalizedSchemaJson = NormalizeSchemaForLegacyDefinitions(schemaJson);
            return await JsonSchema.FromJsonAsync(normalizedSchemaJson, CancellationToken.None);
        }
    }

    private static bool NeedsDefsCompatibilityFallback(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("#/$defs/", StringComparison.Ordinal);

    private static string NormalizeSchemaForLegacyDefinitions(string schemaJson)
    {
        // NJsonSchema may require draft-07 style definitions in some execution paths.
        return schemaJson
            .Replace("\"$defs\"", "\"definitions\"", StringComparison.Ordinal)
            .Replace("#/$defs/", "#/definitions/", StringComparison.Ordinal)
            .Replace(
                "https://json-schema.org/draft/2020-12/schema",
                "http://json-schema.org/draft-07/schema#",
                StringComparison.Ordinal);
    }

    private static async Task<string> ReadAsJsonAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (!IsYamlPath(path))
        {
            return text;
        }

        using var reader = new StringReader(text);
        var yamlObject = YamlDeserializer.Deserialize(reader);
        var normalized = NormalizeYamlObject(yamlObject);
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static bool IsYamlPath(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static void ValidateCapabilities(RepoCapabilityConfig? capabilities, string sourceKind, string sourcePath)
    {
        if (capabilities is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(capabilities.ContractVersion) &&
            !string.Equals(capabilities.ContractVersion, RuntimeCapabilityCatalog.ContractVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[{ErrorCodes.CapabilityContractMismatch}] {sourceKind} '{sourcePath}' declares capabilities.contractVersion '{capabilities.ContractVersion}', " +
                $"but runtime supports '{RuntimeCapabilityCatalog.ContractVersion}'.");
        }

        if (capabilities.Required is null || capabilities.Required.Length == 0)
        {
            return;
        }

        var unsupported = capabilities.Required
            .Where(capability => !RuntimeCapabilityCatalog.IsSupported(capability))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(capability => capability, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unsupported.Count == 0)
        {
            return;
        }

        var supportedList = string.Join(", ", RuntimeCapabilityCatalog.SupportedCapabilities.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        throw new InvalidOperationException(
            $"[{ErrorCodes.CapabilityRequirementNotSupported}] {sourceKind} '{sourcePath}' requires unsupported capabilities: {string.Join(", ", unsupported)}. " +
            $"Supported capabilities: {supportedList}");
    }

    private static object? NormalizeYamlObject(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string stringValue:
                if (bool.TryParse(stringValue, out var boolValue))
                {
                    return boolValue;
                }

                return stringValue;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal:
                return value;
            case IDictionary dictionary:
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = entry.Key?.ToString() ?? string.Empty;
                    result[key] = NormalizeYamlObject(entry.Value);
                }

                return result;
            }
            case IEnumerable enumerable when value is not string:
            {
                var list = new List<object?>();
                foreach (var item in enumerable)
                {
                    list.Add(NormalizeYamlObject(item));
                }

                return list;
            }
            default:
                return value.ToString();
        }
    }

    private static bool IsSchemaValidationDisabled()
    {
        var value = Environment.GetEnvironmentVariable(DisableSchemaValidationEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            _ => bool.TryParse(value, out var parsed) && parsed,
        };
    }
}
