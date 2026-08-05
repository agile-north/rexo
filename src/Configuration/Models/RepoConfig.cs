namespace Rexo.Configuration.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record RepoConfig(
    string Name,
    Dictionary<string, RepoCommandConfig>? Commands,
    Dictionary<string, string>? Aliases)
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }
    public string? SchemaVersion { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public List<string>? Extends { get; init; }

    /// <summary>
    /// Policy sources to load and merge before the local policy file and env-var sources.
    /// Supports the same source types as <c>REXO_POLICY_SOURCES</c>: HTTP/HTTPS, git+, nuget:, and local paths.
    /// Env-var sources (<c>REXO_POLICY_SOURCES</c>) always win over config-declared sources.
    /// </summary>
    public List<string>? PolicySources { get; init; }

    public RepoVersioningConfig? Versioning { get; init; }
    public List<RepoArtifactConfig>? Artifacts { get; init; }
    public RepoRuntimeConfig? Runtime { get; init; }

    /// <summary>Output path contract resolved by Rexo. Defaults are applied when omitted.</summary>
    public RepoOutputsConfig? Outputs { get; init; }

    /// <summary>Toolchain-specific settings available to policy commands via <c>{{settings.*}}</c>.</summary>
    public Dictionary<string, JsonElement>? Settings { get; init; }

    /// <summary>Free-form template variable bag available as <c>{{vars.*}}</c> in step run strings. Supports arbitrary nesting.</summary>
    public Dictionary<string, JsonElement>? Vars { get; init; }

    /// <summary>
    /// First-class secret configuration. Resolved secrets are available as <c>{{secrets.*}}</c>
    /// during command execution.
    /// </summary>
    public RepoSecretsConfig? Secrets { get; init; }

    /// <summary>Declares runtime capability requirements and contract compatibility expectations.</summary>
    public RepoCapabilityConfig? Capabilities { get; init; }

    /// <summary>
    /// Controls how list fields are merged when combining configs via <c>extends</c>.
    /// <list type="bullet">
    ///   <item><c>union</c> (default) — child list entries are appended after base entries.</item>
    ///   <item><c>replace</c> — child list replaces the base list entirely; base entries are discarded.</item>
    ///   <item><c>prepend</c> — child list entries are inserted before base entries.</item>
    /// </list>
    /// </summary>
    public string? MergeStrategy { get; init; }
}

public sealed record RepoCommandConfig(
    string? Description,
    Dictionary<string, RepoOptionConfig> Options,
    List<RepoStepConfig> Steps)
{
    /// <summary>When true, omit this command from default discovery surfaces like list/help.</summary>
    public bool? Hidden { get; init; }

    public Dictionary<string, RepoArgConfig>? Args { get; init; }

    /// <summary>
    /// Unified command merge envelope.
    /// When present, this takes precedence over legacy <c>Merge</c> and <c>StepOps</c> fields.
    /// </summary>
    public RepoCommandMergeConfig? MergeConfig { get; init; }

    /// <summary>
    /// Legacy scalar merge mode.
    /// Prefer <c>MergeConfig.Mode</c>.
    /// </summary>
    public string? Merge { get; init; }

    /// <summary>
    /// Legacy step operation container.
    /// Prefer <c>MergeConfig.Steps</c>.
    /// </summary>
    public RepoCommandStepOpsConfig? StepOps { get; init; }

    /// <summary>Maximum number of steps to run concurrently within a parallel group.</summary>
    public int? MaxParallel { get; init; }

    /// <summary>Optional command hooks that run before the command steps.</summary>
    public List<RepoStepConfig>? Before { get; init; }

    /// <summary>Optional command hooks that run after the command steps.</summary>
    public List<RepoStepConfig>? After { get; init; }

    /// <summary>Maximum command delegation depth allowed for this command invocation chain.</summary>
    public int? MaxDepth { get; init; }
}

public sealed record RepoArgConfig(
    bool Required,
    string? Description = null);

public sealed record RepoOptionConfig(
    string Type,
    JsonElement? Default = null,
    string[]? Allowed = null);

public sealed record RepoStepConfig(
    string? Id = null,
    string? Command = null,
    string? Uses = null,
    string? Run = null,
    string? When = null,
    Dictionary<string, string>? With = null,
    string? Description = null,
    bool? WhenExists = null,
    bool? ContinueOnError = null,
    bool? Parallel = null,
    string[]? DependsOn = null,
    string? OutputPattern = null,
    string? OutputFile = null,
    Dictionary<string, string[]>? Outputs = null,
    RepoStepContainerConfig? Container = null);

public sealed record RepoStepContainerConfig(
    string Image,
    Dictionary<string, string>? Env = null,
    string? WorkingDirectory = null,
    string? Entrypoint = null,
    string? Dockerfile = null,
    string? Context = null,
    RepoStepContainerBuildConfig? Build = null);

public sealed record RepoStepContainerBuildConfig(
    string? Target = null,
    Dictionary<string, string>? Args = null);

public sealed record RepoCommandMergeConfig(
    string? Mode = null,
    RepoCommandStepOpsConfig? Steps = null);

public sealed record RepoCommandStepOpsConfig(
    string[]? Remove = null,
    List<RepoStepReplaceConfig>? Replace = null,
    List<RepoStepConfig>? Prepend = null,
    List<RepoStepConfig>? Append = null);

public sealed record RepoStepReplaceConfig(
    string Id,
    RepoStepConfig Step);

public sealed record RepoVersioningConfig(
    string Provider,
    string? Fallback = null,
    Dictionary<string, string>? Settings = null);

public sealed record RepoArtifactConfig(
    string Type,
    string? Name = null,
    Dictionary<string, JsonElement>? Settings = null);

public sealed record RepoOutputsConfig
{
    /// <summary>When false, Rexo does not collect or write any output files (manifests, step outputs). Default: <c>true</c>.</summary>
    public bool? Emit { get; init; }

    /// <summary>Defaults for command stdout and JSON output. CLI flags still override these values.</summary>
    public RepoCommandOutputConfig? Command { get; init; }

    /// <summary>CI-native manifest emission settings.</summary>
    public RepoCiOutputsConfig? Ci { get; init; }

    /// <summary>Root artifacts directory. Default: <c>artifacts</c>.</summary>
    public string? Root { get; init; }

    /// <summary>Test output paths.</summary>
    public RepoTestOutputPathsConfig? Tests { get; init; }

    /// <summary>Analysis output paths.</summary>
    public RepoAnalysisOutputPathsConfig? Analysis { get; init; }

    /// <summary>Security output paths.</summary>
    public RepoSecurityOutputPathsConfig? Security { get; init; }

    /// <summary>Package output directory. Default: <c>~/packages</c> (relative to <c>outputs.root</c>).</summary>
    public string? Packages { get; init; }

    /// <summary>Manifest-related output settings.</summary>
    public RepoManifestOutputsConfig? Manifests { get; init; }

    /// <summary>Log output directory. Default: <c>~/logs</c> (relative to <c>outputs.root</c>).</summary>
    public string? Logs { get; init; }

    /// <summary>Temporary scratch directory. Default: <c>~/tmp</c> (relative to <c>outputs.root</c>).</summary>
    public string? Temp { get; init; }
}

public sealed record RepoManifestOutputsConfig
{
    /// <summary>Manifest output directory. Default: <c>~/manifests</c> (relative to <c>outputs.root</c>).</summary>
    public string? Path { get; init; }

    /// <summary>Command manifest file strategy. One of <c>single</c> (default) or <c>perCommand</c>.</summary>
    public string? CommandMode { get; init; }

    /// <summary>Command manifest detail level. One of <c>summary</c> (default) or <c>verbose</c>.</summary>
    public string? CommandDetail { get; init; }
}

public sealed record RepoCommandOutputConfig
{
    /// <summary>When true, command results are written to stdout by default. Default: <c>true</c>.</summary>
    public bool? Stdout { get; init; }

    /// <summary>When true, command results are rendered as JSON by default. Default: <c>false</c>.</summary>
    public bool? Json { get; init; }

    /// <summary>Optional default file path for machine-readable command results.</summary>
    public string? JsonFile { get; init; }
}

public sealed record RepoCiOutputsConfig
{
    /// <summary>When true, emit CI-native variables after command execution. Default: <c>true</c>.</summary>
    public bool? Emit { get; init; }

    /// <summary>CI provider selection. Default: <c>auto</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>Provider-specific GitHub Actions settings.</summary>
    [JsonPropertyName("github-actions")]
    public RepoGitHubActionsCiOutputsConfig? GitHubActions { get; init; }

    /// <summary>Prefix applied to emitted variable names. Default: <c>REXO_</c>.</summary>
    public string? Prefix { get; init; }

    /// <summary>Key casing format for emitted variables. Default: <c>upperSnake</c>.</summary>
    public string? KeyCasing { get; init; }

    /// <summary>Emission scope. Accepts <c>safe</c>, <c>full</c>, or an object with <c>mode</c>, <c>include</c>, and <c>exclude</c> masks.</summary>
    public JsonElement? Scope { get; init; }

    /// <summary>When true, emit raw step outputs in addition to the safe summary payload. Default: <c>false</c>.</summary>
    public bool? IncludeStepOutputs { get; init; }

    /// <summary>When true, emit variables even when the normalized value is empty/null. Default: <c>false</c>.</summary>
    public bool? EmitEmptyValues { get; init; }

    /// <summary>When true, redact sensitive values before emission. Default: <c>true</c>.</summary>
    public bool? Redact { get; init; }

    /// <summary>When true, CI emission errors fail the command. Default: <c>false</c>.</summary>
    public bool? FailOnError { get; init; }

    /// <summary>Maximum emitted value length. Default: <c>8192</c>.</summary>
    public int? MaxValueLength { get; init; }

    /// <summary>Maximum number of emitted variables. Default: <c>1000</c>.</summary>
    public int? MaxVariables { get; init; }
}

public sealed record RepoGitHubActionsCiOutputsConfig
{
    /// <summary>GitHub Actions file target. One of <c>env</c>, <c>output</c>, or <c>state</c>. Default: <c>env</c>.</summary>
    public string? Scope { get; init; }
}

public sealed record RepoTestOutputPathsConfig
{
    /// <summary>Test result files directory. Default: <c>~/tests</c> (relative to <c>outputs.root</c>).</summary>
    public string? Results { get; init; }

    /// <summary>Coverage output directory. Default: <c>~/coverage</c> (relative to <c>outputs.root</c>).</summary>
    public string? Coverage { get; init; }

    /// <summary>Test report output directory. Default: <c>~/tests/reports</c> (relative to <c>outputs.root</c>).</summary>
    public string? Reports { get; init; }
}

public sealed record RepoAnalysisOutputPathsConfig
{
    /// <summary>Analysis report output directory. Default: <c>~/analysis</c> (relative to <c>outputs.root</c>).</summary>
    public string? Reports { get; init; }

    /// <summary>SARIF output directory. Default: <c>~/analysis/sarif</c> (relative to <c>outputs.root</c>).</summary>
    public string? Sarif { get; init; }

}

public sealed record RepoSecurityOutputPathsConfig
{
    /// <summary>Full file path for the npm/security audit JSON output. Default: <c>~/security/audit.json</c> (relative to <c>outputs.root</c>).</summary>
    public string? Audit { get; init; }

    /// <summary>Security report output directory. Default: <c>~/security</c> (relative to <c>outputs.root</c>).</summary>
    public string? Reports { get; init; }

    /// <summary>SARIF output directory for security findings. Default: <c>~/security/sarif</c> (relative to <c>outputs.root</c>).</summary>
    public string? Sarif { get; init; }
}

public sealed record RepoRuntimeConfig(
    bool? DryRun = null,
    RepoPushConfig? Push = null,
    RepoRuntimeCommandsConfig? Commands = null);

public sealed record RepoSecretsConfig
{
    /// <summary>Global secret defaults applied when individual entries omit values.</summary>
    public RepoSecretDefaultsConfig? Defaults { get; init; }

    /// <summary>Named provider definitions referenced by secrets via <c>providerRef</c>.</summary>
    public Dictionary<string, RepoSecretProviderConfig>? Providers { get; init; }

    /// <summary>Named secret entries available as <c>{{secrets.&lt;name&gt;}}</c>.</summary>
    public Dictionary<string, RepoSecretConfig>? Items { get; init; }
}

public sealed record RepoSecretDefaultsConfig
{
    /// <summary>Default provider type (for example <c>env</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>Ordered provider chain evaluated by runtime-aware resolution.</summary>
    public IReadOnlyList<RepoSecretProviderRouteConfig>? ProviderChain { get; init; }

    /// <summary>When true (default), fall back to environment lookup after configured providers fail.</summary>
    public bool? FallbackToEnvironment { get; init; }

    /// <summary>When true, stop at the first provider error instead of falling back to the next candidate.</summary>
    public bool? StopOnFirstError { get; init; }

    /// <summary>Default cache behavior.</summary>
    public RepoSecretCacheConfig? Cache { get; init; }

    /// <summary>Default required behavior for secret entries.</summary>
    public bool? Required { get; init; }
}

public sealed record RepoSecretProviderConfig
{
    /// <summary>Provider type (for example <c>env</c>, <c>1password</c>, <c>exec</c>).</summary>
    public string? Type { get; init; }

    /// <summary>Provider auth values or references, interpreted by provider implementation.</summary>
    public Dictionary<string, string>? Auth { get; init; }

    /// <summary>Provider settings available to provider implementations.</summary>
    public Dictionary<string, JsonElement>? Settings { get; init; }

    /// <summary>Default cache behavior for this provider.</summary>
    public RepoSecretCacheConfig? Cache { get; init; }
}

public sealed record RepoSecretConfig
{
    /// <summary>Reference to a named provider under <c>secrets.providers</c>.</summary>
    public string? ProviderRef { get; init; }

    /// <summary>Inline provider type override.</summary>
    public string? Provider { get; init; }

    /// <summary>Ordered provider chain evaluated by runtime-aware resolution.</summary>
    public IReadOnlyList<RepoSecretProviderRouteConfig>? ProviderChain { get; init; }

    /// <summary>When true (default), fall back to environment lookup after configured providers fail.</summary>
    public bool? FallbackToEnvironment { get; init; }

    /// <summary>When true, stop at the first provider error instead of falling back to the next candidate.</summary>
    public bool? StopOnFirstError { get; init; }

    /// <summary>Provider selector/key/path for the secret value.</summary>
    public string? Selector { get; init; }

    /// <summary>Environment variable name used by env provider.</summary>
    public string? Env { get; init; }

    /// <summary>When true (default), unresolved secret fails command execution.</summary>
    public bool? Required { get; init; }

    /// <summary>When true (default), secret is exposed in templates under <c>secrets.*</c>.</summary>
    public bool? ExposeInTemplates { get; init; }

    /// <summary>Optional runtime environment variable mapping for command steps.</summary>
    public string? MapToEnv { get; init; }

    /// <summary>Per-secret cache override.</summary>
    public RepoSecretCacheConfig? Cache { get; init; }

    /// <summary>Per-secret provider settings.</summary>
    public Dictionary<string, JsonElement>? Settings { get; init; }
}

public sealed record RepoSecretProviderRouteConfig
{
    /// <summary>Reference to a named provider under <c>secrets.providers</c>.</summary>
    public string? ProviderRef { get; init; }

    /// <summary>Inline provider type override.</summary>
    public string? Provider { get; init; }

    /// <summary>Selector override for this route.</summary>
    public string? Selector { get; init; }

    /// <summary>Environment variable override for this route.</summary>
    public string? Env { get; init; }

    /// <summary>Runtime filter for the candidate. Use <c>ci</c>, <c>local</c>, or a CI provider name such as <c>github-actions</c>.</summary>
    public string? Runtime { get; init; }
}

public sealed record RepoSecretCacheConfig
{
    /// <summary>Enable per-command in-memory cache.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Optional TTL for cached values in seconds.</summary>
    public int? TtlSeconds { get; init; }
}

/// <summary>Per-command execution policy defaults. Controls delegation depth and collision fallback behavior.</summary>
public sealed record RepoRuntimeCommandsConfig(
    int? MaxDepth = null,
    string? DefaultMergeMode = null);

public sealed record RepoPushConfig(
    bool? DryRun = null,
    bool? Enabled = null,
    bool? NoPushInPullRequest = null,
    bool? RequireCleanWorkingTree = null,
    string[]? Branches = null);

public sealed record RepoCapabilityConfig(
    string? ContractVersion = null,
    string[]? Required = null);

/// <summary>
/// A partial config document used to inject commands and aliases from a policy file
/// (e.g. <c>policy.json</c>) after policy schema validation.
/// </summary>
public sealed record PolicyConfig(
    Dictionary<string, RepoCommandConfig>? Commands = null,
    Dictionary<string, string>? Aliases = null)
{
    /// <summary>Declares runtime capability requirements for this policy.</summary>
    public RepoCapabilityConfig? Capabilities { get; init; }
}
