namespace Rexo.Execution;

using System.Text.Json;
using System.Text.RegularExpressions;
using Rexo.Ci;
using Rexo.Configuration.Models;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;
using Rexo.Execution.Secrets;
using Rexo.Versioning;

public sealed class ConfigCommandLoader
{
    private const string DefaultOutputRoot = "artifacts";
    private const int DefaultMaxCommandDepth = 5;
    internal static readonly System.Text.Json.JsonSerializerOptions IndentedJsonOptions =
        new() { WriteIndented = true };

    private readonly BuiltinRegistry _builtinRegistry;
    private readonly ITemplateRenderer _templateRenderer;
    private readonly VersionProviderRegistry _versionProviders;
    private readonly Artifacts.ArtifactProviderRegistry _artifactProviders;
    private readonly SecretProviderRegistry _secretProviders;

    internal VersionProviderRegistry VersionProviders => _versionProviders;
    internal Artifacts.ArtifactProviderRegistry ArtifactProviders => _artifactProviders;

    /// <summary>Per-invocation cache for idempotent builtins (builtin:validate, builtin:resolve-version).</summary>
    internal Dictionary<string, StepResult> RunCache { get; } = new(StringComparer.Ordinal);

    public ConfigCommandLoader(
        BuiltinRegistry builtinRegistry,
        ITemplateRenderer templateRenderer,
        VersionProviderRegistry versionProviders,
        Artifacts.ArtifactProviderRegistry artifactProviders,
        SecretProviderRegistry? secretProviders = null)
    {
        _builtinRegistry = builtinRegistry;
        _templateRenderer = templateRenderer;
        _versionProviders = versionProviders;
        _artifactProviders = artifactProviders;
        _secretProviders = secretProviders ?? SecretProviderRegistry.CreateDefault();
    }

    public void LoadInto(
        CommandRegistry registry,
        RepoConfig config,
        string repositoryRoot,
        ICommandExecutor commandExecutor)
    {
        RegisterBuiltins(config, repositoryRoot);

        foreach (var (commandName, commandConfig) in config.Commands ?? [])
        {
            var name = commandName;
            var cmd = commandConfig;

            registry.Register(name, (invocation, ct) =>
                ExecuteConfigCommandAsync(name, cmd, config, invocation, repositoryRoot, commandExecutor, ct));
        }

        foreach (var (alias, target) in config.Aliases ?? [])
        {
            var aliasName = alias;
            var targetName = target;

            registry.Register(aliasName, (invocation, ct) =>
                registry.TryResolve(targetName, out var handler) && handler is not null
                    ? handler(invocation, ct)
                    : Task.FromResult(CommandResult.Fail(aliasName, 8, $"Alias target '{targetName}' not found.")));
        }
    }

    /// <summary>
    /// Loads commands and aliases from a policy definition into the registry.
    /// Policy commands have lower priority than config commands: a policy command is only
    /// registered when no command with the same name already exists.
    /// </summary>
    public void LoadPolicyCommandsInto(
        CommandRegistry registry,
        PolicyConfig policy,
        RepoConfig config,
        string repositoryRoot,
        ICommandExecutor commandExecutor)
    {
        if (policy.Commands is { Count: > 0 })
        {
            foreach (var (commandName, commandConfig) in policy.Commands)
            {
                if (registry.TryResolve(commandName, out _)) continue; // repo.json wins

                var name = commandName;
                var cmd = commandConfig;
                registry.Register(name, (invocation, ct) =>
                    ExecuteConfigCommandAsync(name, cmd, config, invocation, repositoryRoot, commandExecutor, ct));
            }
        }

        if (policy.Aliases is { Count: > 0 })
        {
            foreach (var (alias, target) in policy.Aliases)
            {
                if (registry.TryResolve(alias, out _)) continue; // repo.json wins

                var aliasName = alias;
                var targetName = target;
                registry.Register(aliasName, (invocation, ct) =>
                    registry.TryResolve(targetName, out var handler) && handler is not null
                        ? handler(invocation, ct)
                        : Task.FromResult(CommandResult.Fail(aliasName, 8, $"Alias target '{targetName}' not found.")));
            }
        }
    }

    private void RegisterBuiltins(RepoConfig config, string repositoryRoot)
    {
        var context = new ConfigBuiltinModuleContext(this, config, repositoryRoot);
        IConfigBuiltinModule[] modules =
        [
            new VersionBuiltinModule(),
            new ArtifactBuiltinModule(),
            new DockerBuiltinModule(),
            new UtilityBuiltinModule(),
            new ConfigBuiltinModule(),
        ];

        foreach (var module in modules)
        {
            module.Register(_builtinRegistry, context);
        }
    }

    private async Task<CommandResult> ExecuteConfigCommandAsync(
        string commandName,
        RepoCommandConfig commandConfig,
        RepoConfig config,
        CommandInvocation invocation,
        string repositoryRoot,
        ICommandExecutor commandExecutor,
        CancellationToken cancellationToken)
    {
        var emitRuntimeFiles = ShouldEmitRuntimeFiles(config);
        var outputRoot = ResolveOutputRoot(config);
        var ciVariablePrefix = ResolveCiVariablePrefix(config);
        var normalizedCommandConfig = NormalizeCommandConfig(commandConfig);
        var globalMaxDepth = config.Runtime?.Commands?.MaxDepth ?? DefaultMaxCommandDepth;
        var invocationMaxDepth = invocation.MaxCommandDepth ?? int.MaxValue;
        var effectiveMaxDepth = Math.Min(
            invocationMaxDepth,
            normalizedCommandConfig.MaxDepth ?? globalMaxDepth);
        var gitInfo = await Git.GitDetector.DetectAsync(repositoryRoot, cancellationToken);
        var ciInfo = CiDetector.Detect();
        var fileEnvironment = RepositoryEnvironmentFiles.Load(repositoryRoot);
        var secretResolver = new ConfigSecretResolver(config, fileEnvironment, _secretProviders);
        var secretPreflight = await secretResolver.PreflightRequiredAsync(cancellationToken);
        if (!secretPreflight.Success)
        {
            var firstError = secretPreflight.Errors.Count > 0
                ? secretPreflight.Errors[0]
                : null;
            return new CommandResult(
                commandName,
                false,
                9,
                firstError?.Message ?? "Secret preflight failed.",
                new Dictionary<string, object?>
                {
                    ["error"] = firstError?.Message ?? "Secret preflight failed.",
                    ["errorCode"] = firstError?.Code ?? ErrorCodes.ConfigSchemaInvalid,
                })
            {
                StructuredErrors = secretPreflight.Errors,
            };
        }

        var context = new ExecutionContext(
            repositoryRoot,
            gitInfo.Branch,
            gitInfo.CommitSha,
            new Dictionary<string, object?>())
        {
            ShortSha = gitInfo.ShortSha,
            RemoteUrl = gitInfo.RemoteUrl,
            IsCi = ciInfo.IsCi,
            CiProvider = ciInfo.Provider,
            IsPullRequest = ciInfo.IsPullRequest,
            IsCleanWorkingTree = gitInfo.IsClean,
            CiBuildId = ciInfo.BuildId,
            CiRunNumber = ciInfo.RunNumber,
            CiWorkflowName = ciInfo.WorkflowName,
            CiActor = ciInfo.Actor,
            CiTag = ciInfo.Tag,
            CiBuildUrl = ciInfo.BuildUrl,
            Args = invocation.Args,
            Options = BuildOptionsWithDefaults(invocation.Options, normalizedCommandConfig),
            FileEnvironment = fileEnvironment,
            ResolvedSecrets = secretPreflight.ResolvedValues,
            SecretMetadata = secretPreflight.Metadata,
            MappedSecretEnvironment = secretPreflight.MappedEnvironment,
            CiVariablePrefix = ciVariablePrefix,
            IsDryRun = TryGetOptionBoolean(invocation.Options, "dry-run") == true,
            CommandCallStack = [.. invocation.CallStack, commandName],
            ResolvedOutputs = BuildOutputsContext(config),
            ResolvedSettings = BuildSettingsContext(config),
            ResolvedVars = BuildVarsContext(config),
            MaxCommandDepth = effectiveMaxDepth,
        };

        var stepExecutor = new StepExecutor(commandExecutor, _templateRenderer, _builtinRegistry);
        var stepResults = new List<StepResult>();
        var currentContext = context;
        var artifactEntries = new List<Core.Models.ArtifactManifestEntry>();
        var pushDecisionEntries = new List<Core.Models.PushDecision>();
        CommandResult? failedResult = null;

        // Group consecutive parallel steps; sequential steps are singleton groups
        var stepGroups = GroupSteps(normalizedCommandConfig.Steps);

        foreach (var group in stepGroups)
        {
            List<(RepoStepConfig Config, StepResult Result)> executed;

            if (group.Count == 1)
            {
                var stepConfig = group[0];
                var stepDef = BuildStepDefinition(stepConfig);
                var stepResult = await stepExecutor.ExecuteAsync(stepDef, currentContext, cancellationToken);
                stepResult = EnrichWithFileOutputs(stepDef, stepResult, currentContext, repositoryRoot, _templateRenderer);
                executed = [(stepConfig, stepResult)];
            }
            else
            {
                executed = await ExecuteParallelGroupAsync(
                    group,
                    stepExecutor,
                    currentContext,
                    normalizedCommandConfig.MaxParallel,
                    cancellationToken);
                // Enrich parallel results with file outputs
                var enriched = new List<(RepoStepConfig Config, StepResult Result)>(executed.Count);
                foreach (var (cfg, res) in executed)
                {
                    var def = BuildStepDefinition(cfg);
                    enriched.Add((cfg, EnrichWithFileOutputs(def, res, currentContext, repositoryRoot, _templateRenderer)));
                }

                executed = enriched;
            }

            foreach (var (_, stepResult) in executed)
            {
                stepResults.Add(stepResult);
                currentContext = currentContext.WithStep(stepResult);

                if (stepResult.Outputs.TryGetValue("__version", out var versionObj) &&
                    versionObj is VersionResult versionResult)
                {
                    currentContext = currentContext.WithVersion(versionResult);
                }

                if (stepResult.Outputs.TryGetValue("__artifacts", out var artifactsObj) &&
                    artifactsObj is List<Core.Models.ArtifactManifestEntry> stepArtifacts)
                {
                    artifactEntries.AddRange(stepArtifacts);
                }

                if (stepResult.Outputs.TryGetValue("__pushDecisions", out var decisionsObj) &&
                    decisionsObj is List<Core.Models.PushDecision> stepDecisions)
                {
                    pushDecisionEntries.AddRange(stepDecisions);
                }
            }

            // Fail fast if any step failed and it doesn't have continueOnError
            var failed = executed
                .FirstOrDefault(t => !t.Result.Success && t.Config.ContinueOnError != true);

            if (failed.Result is not null)
            {
                failedResult = new CommandResult(
                    commandName,
                    false,
                    failed.Result.ExitCode,
                    $"Step '{failed.Result.StepId}' failed with exit code {failed.Result.ExitCode}.",
                    new Dictionary<string, object?>())
                {
                    Steps = stepResults,
                    Version = currentContext.Version,
                    Artifacts = artifactEntries,
                    PushDecisions = pushDecisionEntries,
                };
                break;
            }
        }

        var commandResult = failedResult ?? new CommandResult(
            commandName,
            true,
            0,
            $"Command '{commandName}' completed successfully.",
            new Dictionary<string, object?>())
        {
            Steps = stepResults,
            Version = currentContext.Version,
            Artifacts = artifactEntries,
            PushDecisions = pushDecisionEntries,
        };

        if (emitRuntimeFiles)
        {
            await WriteCommandManifestAsync(repositoryRoot, outputRoot, config, commandName, commandResult, cancellationToken);
        }

        return commandResult;
    }

    internal async Task<StepResult> BuildArtifactsAsync(
        string stepId,
        RepoConfig config,
        ExecutionContext ctx,
        Func<RepoArtifactConfig, bool> includePredicate,
        string successMessage,
        string emptyMessage,
        CancellationToken cancellationToken)
    {
        var artifacts = (config.Artifacts ?? [])
            .Where(includePredicate)
            .ToList();

        if (artifacts.Count == 0)
        {
            return new StepResult(stepId, true, 0, TimeSpan.Zero,
                new Dictionary<string, object?> { ["message"] = emptyMessage });
        }

        foreach (var artifactCfg in artifacts)
        {
            var provider = _artifactProviders.Resolve(artifactCfg.Type);
            if (provider is null)
            {
                Console.Error.WriteLine($"  Warning: No provider found for artifact type '{artifactCfg.Type}'.");
                continue;
            }

            var artifactConfig = ToArtifactConfig(artifactCfg, config, ResolveOutputRoot(config));
            var result = await provider.BuildAsync(artifactConfig, ctx, cancellationToken);
            if (!result.Success)
            {
                var artifactName = ResolveArtifactName(artifactCfg, config);
                return new StepResult(stepId, false, 5, TimeSpan.Zero,
                    new Dictionary<string, object?> { ["error"] = $"Failed to build artifact '{artifactName}'." });
            }
        }

        return new StepResult(stepId, true, 0, TimeSpan.Zero,
            new Dictionary<string, object?> { ["message"] = successMessage });
    }

    internal async Task<StepResult> TagArtifactsAsync(
        string stepId,
        RepoConfig config,
        ExecutionContext ctx,
        Func<RepoArtifactConfig, bool> includePredicate,
        string successMessage,
        string emptyMessage,
        CancellationToken cancellationToken)
    {
        var artifacts = (config.Artifacts ?? [])
            .Where(includePredicate)
            .ToList();

        if (artifacts.Count == 0)
        {
            return new StepResult(stepId, true, 0, TimeSpan.Zero,
                new Dictionary<string, object?> { ["message"] = emptyMessage });
        }

        foreach (var artifactCfg in artifacts)
        {
            var provider = _artifactProviders.Resolve(artifactCfg.Type);
            if (provider is null)
            {
                continue;
            }

            await provider.TagAsync(ToArtifactConfig(artifactCfg, config, ResolveOutputRoot(config)), ctx, cancellationToken);
        }

        return new StepResult(stepId, true, 0, TimeSpan.Zero,
            new Dictionary<string, object?> { ["message"] = successMessage });
    }

    internal async Task<StepResult> PushArtifactsAsync(
        string stepId,
        RepoConfig config,
        string repositoryRoot,
        string outputRoot,
        bool emitRuntimeFiles,
        ExecutionContext ctx,
        Func<RepoArtifactConfig, bool> includePredicate,
        string successMessage,
        string emptyMessage,
        CancellationToken cancellationToken)
    {
        var artifacts = (config.Artifacts ?? [])
            .Where(includePredicate)
            .ToList();

        if (artifacts.Count == 0)
        {
            return new StepResult(stepId, true, 0, TimeSpan.Zero,
                new Dictionary<string, object?> { ["message"] = emptyMessage });
        }

        var manifestEntries = new List<Core.Models.ArtifactManifestEntry>();
        var pushDecisions = new List<Core.Models.PushDecision>();
        var globalPolicy = ParsePushPolicyRules(config);
        var confirmRequested = ResolveConfirmRequested(ctx.Options);
        var dryRun = ctx.IsDryRun;

        if (!confirmRequested && !dryRun)
        {
            foreach (var artifactCfg in artifacts)
            {
                var artifactName = ResolveArtifactName(artifactCfg, config);
                manifestEntries.Add(new Core.Models.ArtifactManifestEntry(
                    artifactCfg.Type,
                    artifactName,
                    Built: true,
                    Pushed: false,
                    Tags: Array.Empty<string>()));
                pushDecisions.Add(new Core.Models.PushDecision(
                    artifactName,
                    false,
                    "Push skipped: use --confirm locally, or use release --push."));
            }

            if (emitRuntimeFiles)
            {
                await WriteArtifactManifestAsync(repositoryRoot, outputRoot, manifestEntries, cancellationToken);
            }
            return new StepResult(stepId, true, 0, TimeSpan.Zero,
                new Dictionary<string, object?>
                {
                    ["message"] = "Push skipped: use --confirm locally, or use release --push.",
                    ["__artifacts"] = manifestEntries,
                    ["__pushDecisions"] = pushDecisions,
                });
        }

        foreach (var artifactCfg in artifacts)
        {
            var provider = _artifactProviders.Resolve(artifactCfg.Type);
            if (provider is null)
            {
                continue;
            }

            var artifactName = ResolveArtifactName(artifactCfg, config);

            var effectivePolicy = BuildEffectivePushPolicy(globalPolicy, artifactCfg.Settings);
            if (!IsPushAllowed(effectivePolicy, ctx, out var gateReason))
            {
                manifestEntries.Add(new Core.Models.ArtifactManifestEntry(
                    artifactCfg.Type,
                    artifactName,
                    Built: true,
                    Pushed: false,
                    Tags: Array.Empty<string>()));
                pushDecisions.Add(new Core.Models.PushDecision(artifactName, false, gateReason));
                continue;
            }

            var artifactConfig = ToArtifactConfig(artifactCfg, config, outputRoot);
            ArtifactPushResult pushResult;
            if (dryRun)
            {
                Console.WriteLine($"  Dry run enabled: simulating push for '{artifactName}'.");
                var simulatedReferences = provider.GetPlannedTags(artifactConfig, ctx).ToList();
                if (simulatedReferences.Count == 0)
                {
                    simulatedReferences.Add($"{artifactName}:{ctx.Version?.SemVer ?? "latest"}");
                }

                pushResult = new ArtifactPushResult(artifactName, true, simulatedReferences);
            }
            else
            {
                pushResult = await provider.PushAsync(artifactConfig, ctx, cancellationToken);
            }

            var pushPerformed = pushResult.PublishedReferences.Count > 0;
            manifestEntries.Add(new Core.Models.ArtifactManifestEntry(
                artifactCfg.Type,
                artifactName,
                Built: true,
                Pushed: pushPerformed,
                Tags: pushResult.PublishedReferences));
            pushDecisions.Add(new Core.Models.PushDecision(
                artifactName,
                pushResult.Success,
                dryRun
                    ? "Dry run: push simulated."
                    : pushResult.Success
                        ? (pushPerformed ? "Push succeeded." : "Push skipped.")
                        : $"Failed to push artifact '{artifactName}'."));

            if (!pushResult.Success)
            {
                if (emitRuntimeFiles)
                {
                    await WriteArtifactManifestAsync(repositoryRoot, outputRoot, manifestEntries, cancellationToken);
                }
                return new StepResult(stepId, false, 6, TimeSpan.Zero,
                    new Dictionary<string, object?>
                    {
                        ["error"] = $"Failed to push artifact '{artifactName}'.",
                        ["__artifacts"] = manifestEntries,
                        ["__pushDecisions"] = pushDecisions,
                    });
            }
        }

        if (emitRuntimeFiles)
        {
            await WriteArtifactManifestAsync(repositoryRoot, outputRoot, manifestEntries, cancellationToken);
        }
        return new StepResult(stepId, true, 0, TimeSpan.Zero,
            new Dictionary<string, object?>
            {
                ["message"] = dryRun ? $"{successMessage} (dry run)" : successMessage,
                ["__artifacts"] = manifestEntries,
                ["__pushDecisions"] = pushDecisions,
            });
    }

    internal static StepResult PlanArtifacts(
        string stepId,
        RepoConfig config,
        ExecutionContext ctx,
        Artifacts.ArtifactProviderRegistry artifactProviders,
        bool pushRequested,
        Func<RepoArtifactConfig, bool> includePredicate,
        string successMessage,
        string emptyMessage)
    {
        var artifacts = (config.Artifacts ?? [])
            .Where(includePredicate)
            .ToList();

        if (artifacts.Count == 0)
        {
            return new StepResult(stepId, true, 0, TimeSpan.Zero,
                new Dictionary<string, object?> { ["message"] = emptyMessage });
        }

        var version = ctx.Version?.SemVer;
        var branch = ctx.Branch ?? ctx.Version?.Branch;
        var commitSha = ctx.CommitSha;
        var isPullRequest = ctx.IsPullRequest;
        var isCleanTree = ctx.IsCleanWorkingTree;

        var globalPolicy = ParsePushPolicyRules(config);
        var planArtifacts = artifacts.Select(a => BuildPlanArtifact(a, config, ctx, artifactProviders, globalPolicy, pushRequested)).ToList();
        var overallCanPush = pushRequested && planArtifacts.All(a => a.Push.Eligible);
        var overallSkipReasons = pushRequested
            ? planArtifacts.SelectMany(a => a.Push.SkipReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var payload = new PlanPayload(
            new PlanRepoSection(config.Name, branch, commitSha, ctx.RemoteUrl),
            ctx.Version is null
                ? null
                : new PlanVersionSection(
                    ctx.Version.SemVer,
                    ctx.Version.DockerVersion,
                    ctx.Version.NuGetVersion,
                    ctx.Version.InformationalVersion),
            planArtifacts,
            new PlanPushSection(
                pushRequested,
                pushRequested ? overallCanPush : null,
                pushRequested ? (overallCanPush ? "Push allowed by current policy." : "Push blocked by one or more policy or credential checks.") : "Push not requested.",
                overallSkipReasons));

        // Generate human-readable plan output
        var planLines = new List<string>();
        planLines.Add("");
        planLines.Add("Rexo plan");
        planLines.Add("");

        planLines.Add("Repo:");
        planLines.Add($"  name:    {config.Name}");
        if (!string.IsNullOrWhiteSpace(branch))
            planLines.Add($"  branch:  {branch}");
        if (!string.IsNullOrWhiteSpace(commitSha))
            planLines.Add($"  commit:  {commitSha}");
        if (!string.IsNullOrWhiteSpace(ctx.RemoteUrl))
            planLines.Add($"  remote:  {ctx.RemoteUrl}");
        planLines.Add("");

        // Version section
        if (ctx.Version is not null)
        {
            planLines.Add("Version:");
            planLines.Add($"  semver:   {ctx.Version.SemVer}");
            if (!string.IsNullOrWhiteSpace(ctx.Version.DockerVersion))
                planLines.Add($"  docker:   {ctx.Version.DockerVersion}");
            if (!string.IsNullOrWhiteSpace(ctx.Version.NuGetVersion))
                planLines.Add($"  nuget:    {ctx.Version.NuGetVersion}");
            planLines.Add($"  info:     {ctx.Version.InformationalVersion}");
            planLines.Add("");
        }

        // Artifacts section
        planLines.Add("Artifacts:");
        foreach (var artifact in planArtifacts)
        {
            planLines.Add($"  [{artifact.Type}] {artifact.Name}");
            planLines.Add($"    build: yes");
            foreach (var setting in artifact.BuildSettings)
            {
                planLines.Add($"    {setting.Key}: {setting.Value}");
            }

            if (artifact.Tags.Count > 0)
            {
                planLines.Add("    tags:");
                foreach (var tag in artifact.Tags)
                    planLines.Add($"      - {tag}");
            }

            planLines.Add($"    push eligible: {(artifact.Push.Eligible ? "yes" : "no")}");
            if (artifact.RequiredCredentials.Count > 0)
            {
                planLines.Add("    required credentials:");
                foreach (var credential in artifact.RequiredCredentials)
                    planLines.Add($"      - {credential}");
            }

            if (artifact.ExpectedOutputs.Count > 0)
            {
                planLines.Add("    expected outputs:");
                foreach (var output in artifact.ExpectedOutputs)
                    planLines.Add($"      - {output}");
            }

            if (artifact.Push.SkipReasons.Count > 0)
            {
                planLines.Add("    push blockers:");
                foreach (var reason in artifact.Push.SkipReasons)
                    planLines.Add($"      - {reason}");
            }
        }
        planLines.Add("");

        // Push eligibility section
        planLines.Add("Push:");
        planLines.Add($"  requested: {(pushRequested ? "yes" : "no")}");
        if (pushRequested)
        {
            planLines.Add($"  eligible: {(overallCanPush ? "yes" : "no")}");
            planLines.Add($"  decision: {payload.Push.Decision}");
        }
        else
        {
            planLines.Add("  eligible: not requested");
            planLines.Add("  decision: will not push unless --push is supplied");
        }

        if (pushRequested && !overallCanPush && overallSkipReasons.Count > 0)
        {
            planLines.Add("  skip reasons:");
            foreach (var reason in overallSkipReasons)
                planLines.Add($"    - {reason}");
        }
        planLines.Add("");

        var planOutput = string.Join(Environment.NewLine, planLines);
        Console.WriteLine(planOutput);

        var json = JsonSerializer.Serialize(payload, IndentedJsonOptions);

        return new StepResult(stepId, true, 0, TimeSpan.Zero,
            new Dictionary<string, object?>
            {
                ["message"] = successMessage,
                ["plan"] = json,
                ["pushRequested"] = pushRequested,
                ["canPush"] = overallCanPush,
                ["skipReasons"] = overallSkipReasons,
            });
    }

    private static PlanArtifact BuildPlanArtifact(
        RepoArtifactConfig artifact,
        RepoConfig config,
        Core.Models.ExecutionContext ctx,
        Artifacts.ArtifactProviderRegistry artifactProviders,
        PushPolicyRules globalPolicy,
        bool pushRequested)
    {
        var artifactName = ResolveArtifactName(artifact, config);
        var artifactConfig = new ArtifactConfig(artifact.Type, artifactName, artifact.Settings ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        var provider = artifactProviders.Resolve(artifact.Type);
        var tags = provider?.GetPlannedTags(artifactConfig, ctx) ?? Array.Empty<string>();
        var effectivePolicy = BuildEffectivePushPolicy(globalPolicy, artifact.Settings);
        var pushEligible = true;
        var skipReasons = new List<string>();

        if (pushRequested)
        {
            if (!IsPushAllowed(effectivePolicy, ctx, out var reason))
            {
                pushEligible = false;
                skipReasons.Add(reason);
            }

            foreach (var credentialCheck in GetCredentialChecks(artifact, ctx.RepositoryRoot, ctx.MappedSecretEnvironment))
            {
                if (!credentialCheck.Available)
                {
                    pushEligible = false;
                    if (!string.IsNullOrWhiteSpace(credentialCheck.Detail))
                    {
                        skipReasons.Add(credentialCheck.Detail);
                    }
                }
            }
        }

        return new PlanArtifact(
            artifact.Type,
            artifactName,
            GetArtifactBuildSettings(artifact, artifactName),
            tags,
            true,
            GetExpectedOutputs(artifact, ctx, artifactName),
            GetRequiredCredentialNames(artifact),
            new PlanArtifactPush(pushRequested, pushEligible, pushRequested ? (pushEligible ? "Push allowed." : "Push blocked.") : "Push not requested.", skipReasons));
    }

    private static IReadOnlyDictionary<string, string> GetArtifactBuildSettings(RepoArtifactConfig artifact, string artifactName)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        switch (artifact.Type.ToLowerInvariant())
        {
            case "docker":
                settings["image"] = TryGetArtifactSettingString(artifact.Settings, "image") ?? artifactName;
                settings["dockerfile"] = TryGetArtifactSettingString(artifact.Settings, "dockerfile") ?? "Dockerfile";
                settings["context"] = TryGetArtifactSettingString(artifact.Settings, "context") ?? ".";
                settings["runner"] = TryGetArtifactSettingString(artifact.Settings, "runner") ?? "build";
                break;
            case "nuget":
                settings["project"] = TryGetArtifactSettingString(artifact.Settings, "project") ?? string.Empty;
                settings["source"] = TryGetArtifactSettingString(artifact.Settings, "target.source") ?? "https://api.nuget.org/v3/index.json";
                settings["output"] = TryGetArtifactSettingString(artifact.Settings, "output") ?? Path.Combine("artifacts", "packages");
                break;
            case "helm-oci":
                settings["chart"] = TryGetArtifactSettingString(artifact.Settings, "chart") ?? artifactName;
                settings["chartPath"] = TryGetArtifactSettingString(artifact.Settings, "chartPath") ?? "chart";
                settings["registry"] = TryGetArtifactSettingString(artifact.Settings, "registry") ?? string.Empty;
                settings["output"] = TryGetArtifactSettingString(artifact.Settings, "output") ?? Path.Combine("artifacts", "charts");
                break;
        }

        return settings;
    }

    private static IReadOnlyList<string> GetExpectedOutputs(RepoArtifactConfig artifact, Core.Models.ExecutionContext ctx, string artifactName)
    {
        return artifact.Type.ToLowerInvariant() switch
        {
            "nuget" => [Path.Combine(TryGetArtifactSettingString(artifact.Settings, "output") ?? Path.Combine("artifacts", "packages"), $"{artifactName}.*.nupkg")],
            "helm-oci" => [Path.Combine(TryGetArtifactSettingString(artifact.Settings, "output") ?? Path.Combine("artifacts", "charts"), $"{(TryGetArtifactSettingString(artifact.Settings, "chart") ?? artifactName)}-{ctx.Version?.SemVer ?? "<version>"}.tgz")],
            "docker" => (TryGetArtifactSettingString(artifact.Settings, "image") is { Length: > 0 } image)
                ? [image]
                : [artifactName],
            _ => Array.Empty<string>(),
        };
    }

    private static IReadOnlyList<string> GetRequiredCredentialNames(RepoArtifactConfig artifact)
    {
        return artifact.Type.ToLowerInvariant() switch
        {
            "docker" => ["DOCKER_LOGIN_USERNAME", "DOCKER_LOGIN_PASSWORD", "DOCKER_LOGIN_REGISTRY", "GITHUB_ACTOR/GITHUB_TOKEN (for ghcr.io in GitHub Actions)", "CI_REGISTRY_USER/CI_JOB_TOKEN (for GitLab Container Registry in GitLab CI)"],
            "nuget" => [$"{TryGetArtifactSettingString(artifact.Settings, "target.apiKeyEnv") ?? "NUGET_API_KEY"}", "NUGET_AUTH_TOKEN", "GITHUB_TOKEN or SYSTEM_ACCESSTOKEN (CI fallback)"],
            "helm-oci" => ["HELM_REGISTRY_USERNAME", "HELM_REGISTRY_PASSWORD", "HELM_REGISTRY", "GITHUB_ACTOR/GITHUB_TOKEN (for ghcr.io in GitHub Actions)", "CI_REGISTRY_USER/CI_JOB_TOKEN (for GitLab Container Registry in GitLab CI)"],
            _ => Array.Empty<string>(),
        };
    }

    private static IReadOnlyList<PlanCredentialCheck> GetCredentialChecks(
        RepoArtifactConfig artifact,
        string repositoryRoot,
        IReadOnlyDictionary<string, string> mappedSecretEnvironment)
    {
        var fileEnv = Artifacts.FeedAuthResolver.OverlayMappedEnvironment(
            RepositoryEnvironmentFiles.Load(repositoryRoot),
            mappedSecretEnvironment);
        var ciInferenceEnabled = IsArtifactCiInferenceEnabled(artifact.Settings);

        return artifact.Type.ToLowerInvariant() switch
        {
            "docker" =>
            [
                ToPlanCredentialCheck(Artifacts.FeedAuthResolver.ResolveDocker(
                    TryGetArtifactSettingString(artifact.Settings, "loginRegistry")
                    ?? TryGetArtifactSettingString(artifact.Settings, "target.loginRegistry")
                    ?? TryGetArtifactSettingString(artifact.Settings, "target.registry"),
                    InferDockerRegistry(TryGetArtifactSettingString(artifact.Settings, "image")),
                    fileEnv,
                    ciInferenceEnabled: ciInferenceEnabled))
            ],
            "nuget" =>
            [
                ToPlanCredentialCheck(ResolvePlanNuGetAuth(
                    TryGetArtifactSettingString(artifact.Settings, "target.source") ?? "https://api.nuget.org/v3/index.json",
                    TryGetArtifactSettingString(artifact.Settings, "target.apiKeyEnv"),
                    fileEnv,
                    ciInferenceEnabled))
            ],
            "helm-oci" =>
            [
                ToPlanCredentialCheck(ResolvePlanHelmOciAuth(
                    TryGetArtifactSettingString(artifact.Settings, "target.loginRegistry")
                    ?? TryGetArtifactSettingString(artifact.Settings, "target.registry")
                    ?? Artifacts.FeedAuthResolver.ExtractRegistryHost(TryGetArtifactSettingString(artifact.Settings, "target.oci")),
                    fileEnv,
                    ciInferenceEnabled))
            ],
            _ => Array.Empty<PlanCredentialCheck>(),
        };
    }

    private static PlanCredentialCheck ToPlanCredentialCheck(Artifacts.FeedAuthResolution resolution) =>
        new(resolution.HasCredentials, resolution.HasCredentials
            ? $"Credentials available via {resolution.Source}."
            : resolution.Error ?? $"Credentials not resolved ({resolution.Source}).");

    // Preflight credential checks used by the plan/dry-run output.  These mirror the auth
    // resolution each provider performs at push time, but return only a presence/source
    // signal rather than the actual secrets.

    private static Artifacts.FeedAuthResolution ResolvePlanNuGetAuth(
        string source,
        string? configuredApiKeyEnv,
        IReadOnlyDictionary<string, string> fileEnv,
        bool ciInferenceEnabled)
    {
        var envName = string.IsNullOrWhiteSpace(configuredApiKeyEnv) ? "NUGET_API_KEY" : configuredApiKeyEnv;
        var secret = Artifacts.FeedAuthResolver.GetEnv(envName, fileEnv)
                     ?? Artifacts.FeedAuthResolver.GetEnv("NUGET_AUTH_TOKEN", fileEnv);

        if (string.IsNullOrWhiteSpace(secret))
        {
            if (!ciInferenceEnabled)
            {
                return new Artifacts.FeedAuthResolution(false, null, null, source, null, "none");
            }

            secret = source.Contains("nuget.pkg.github.com", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                : Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");

            if (!string.IsNullOrWhiteSpace(secret))
            {
                return new Artifacts.FeedAuthResolution(true, null, secret, source, null, "ci-token");
            }

            return new Artifacts.FeedAuthResolution(false, null, null, source, null, "none");
        }

        return new Artifacts.FeedAuthResolution(true, null, secret, source, null, "env");
    }

    private static Artifacts.FeedAuthResolution ResolvePlanHelmOciAuth(
        string? configuredRegistry,
        IReadOnlyDictionary<string, string> fileEnv,
        bool ciInferenceEnabled)
    {
        var username = Artifacts.FeedAuthResolver.GetEnv("HELM_REGISTRY_USERNAME", fileEnv);
        var secret = Artifacts.FeedAuthResolver.GetEnv("HELM_REGISTRY_PASSWORD", fileEnv);
        var endpoint = Artifacts.FeedAuthResolver.GetEnv("HELM_REGISTRY", fileEnv) ?? configuredRegistry;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = Artifacts.FeedAuthResolver.ResolveImplicitContainerRegistry(fileEnv, ciInferenceEnabled);
        }

        return Artifacts.FeedAuthResolver.FinalizeContainerRegistryAuth(
            username,
            secret,
            endpoint,
            fileEnv,
            "HELM_REGISTRY_USERNAME and HELM_REGISTRY_PASSWORD must both be set.",
            "Helm login registry could not be determined. Set settings.target.loginRegistry or HELM_REGISTRY.",
            ciInferenceEnabled);
    }

    private static bool IsArtifactCiInferenceEnabled(Dictionary<string, JsonElement>? settings)
    {
        if (settings is null)
        {
            return true;
        }

        return TryGetBool(settings, "ciInference")
            ?? TryGetBool(settings, "target.ciInference")
            ?? true;
    }


    private static string? InferDockerRegistry(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var firstSegment = image.Split('/')[0];
        return firstSegment.Contains('.', StringComparison.Ordinal) ? firstSegment : null;
    }

    private static bool ResolveConfirmRequested(IReadOnlyDictionary<string, string?> options) =>
        TryGetOptionBoolean(options, "confirm")
        ?? TryGetOptionBoolean(options, "push")
        ?? false;

    internal static bool? TryGetOptionBoolean(IReadOnlyDictionary<string, string?> options, string key)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string ResolveCiVariablePrefix(RepoConfig config)
    {
        var prefix = config.Outputs?.Ci?.Prefix;
        return string.IsNullOrWhiteSpace(prefix) ? "REXO_" : prefix;
    }

    private static (bool canPush, List<string> skipReasons) EvaluatePushEligibility(
        RepoConfig config,
        bool isPullRequest,
        bool isCleanTree)
    {
        var skipReasons = new List<string>();
        var canPush = true;

        var pushPolicy = ParsePushPolicyRules(config);
        if (!pushPolicy.Enabled)
        {
            skipReasons.Add("Push disabled by policy");
            canPush = false;
        }

        if (pushPolicy.NoPushInPullRequest)
        {
            if (isPullRequest)
            {
                skipReasons.Add("Pull request: push blocked");
                canPush = false;
            }
        }

        if (pushPolicy.RequireCleanWorkingTree)
        {
            if (!isCleanTree)
            {
                skipReasons.Add("Uncommitted changes: push blocked");
                canPush = false;
            }
        }

        return (canPush, skipReasons);
    }

    private static ArtifactConfig ToArtifactConfig(RepoArtifactConfig artifactCfg, RepoConfig config, string outputRoot)
    {
        var settings = artifactCfg.Settings is not null
            ? CloneSettings(artifactCfg.Settings)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (string.Equals(artifactCfg.Type, "nuget", StringComparison.OrdinalIgnoreCase) &&
            !settings.ContainsKey("output"))
        {
            var nugetOutput = Path.Combine(outputRoot, "packages");
            settings["output"] = JsonSerializer.SerializeToElement(nugetOutput);
        }

        return new ArtifactConfig(
            artifactCfg.Type,
            ResolveArtifactName(artifactCfg, config),
            settings);
    }

    internal static string ResolveArtifactName(RepoArtifactConfig artifactCfg, RepoConfig config) =>
        string.IsNullOrWhiteSpace(artifactCfg.Name)
            ? config.Name
            : artifactCfg.Name;

    private static string? TryGetArtifactSettingString(Dictionary<string, JsonElement>? settings, string key)
    {
        if (settings is null)
        {
            return null;
        }

        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        if (!settings.TryGetValue(segments[0], out var value))
        {
            return null;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segments[i], out value))
            {
                return null;
            }
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.ToString(),
        };
    }

    private static IEnumerable<JsonProperty>? TryGetArtifactSettingObject(Dictionary<string, JsonElement>? settings, string key)
    {
        if (settings is null || !settings.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value.EnumerateObject();
    }

    internal static Dictionary<string, JsonElement> CloneSettings(Dictionary<string, JsonElement> settings)
    {
        var clone = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            clone[key] = value.Clone();
        }

        return clone;
    }

    private static IReadOnlyDictionary<string, string?> BuildOptionsWithDefaults(
        IReadOnlyDictionary<string, string?> provided,
        RepoCommandConfig commandConfig)
    {
        var result = new Dictionary<string, string?>(provided, StringComparer.OrdinalIgnoreCase);

        foreach (var (optName, optConfig) in commandConfig.Options ?? [])
        {
            if (!result.ContainsKey(optName) && optConfig.Default is not null)
            {
                result[optName] = OptionDefaultToString(optConfig.Default.Value);
            }
        }

        return result;
    }

    private static string? OptionDefaultToString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => value.GetString(),
            _ => value.ToString(),
        };

    private static RepoCommandConfig NormalizeCommandConfig(RepoCommandConfig commandConfig) =>
        new(
            commandConfig.Description,
            commandConfig.Options ?? [],
            BuildCommandSteps(commandConfig))
        {
            Hidden = commandConfig.Hidden,
            Args = commandConfig.Args ?? [],
            Merge = commandConfig.Merge,
            MaxParallel = commandConfig.MaxParallel,
            MaxDepth = commandConfig.MaxDepth,
        };

    private static List<RepoStepConfig> BuildCommandSteps(RepoCommandConfig commandConfig)
    {
        var steps = new List<RepoStepConfig>();
        if (commandConfig.Before is { Count: > 0 })
        {
            steps.AddRange(commandConfig.Before);
        }

        if (commandConfig.Steps is { Count: > 0 })
        {
            steps.AddRange(commandConfig.Steps);
        }

        if (commandConfig.After is { Count: > 0 })
        {
            steps.AddRange(commandConfig.After);
        }

        return steps;
    }

    private static async Task WriteArtifactManifestAsync(
        string repositoryRoot,
        string outputRoot,
        IReadOnlyList<Core.Models.ArtifactManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        var artifactsDir = Path.Combine(repositoryRoot, outputRoot);
        Directory.CreateDirectory(artifactsDir);
        var manifestPath = Path.Combine(artifactsDir, "manifest.json");

        var manifest = new
        {
            SchemaVersion = "1.0",
            GeneratedAt = DateTimeOffset.UtcNow,
            Artifacts = entries,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(manifest, IndentedJsonOptions);
        await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
        Console.WriteLine($"  Artifact manifest written to {manifestPath}");
    }

    private static async Task WriteCommandManifestAsync(
        string repositoryRoot,
        string outputRoot,
        RepoConfig config,
        string commandName,
        CommandResult commandResult,
        CancellationToken cancellationToken)
    {
        var manifestsDir = ResolveManifestsDirectory(repositoryRoot, outputRoot, config);
        Directory.CreateDirectory(manifestsDir);

        var mode = string.Equals(config.Outputs?.Manifests?.CommandMode, "perCommand", StringComparison.OrdinalIgnoreCase)
            ? "perCommand"
            : "aggregate";
        var detail = string.Equals(config.Outputs?.Manifests?.CommandDetail, "verbose", StringComparison.OrdinalIgnoreCase)
            ? "verbose"
            : "summary";

        // Sanitize command name for use as a file name
        var safeName = string.Concat(commandName.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        var manifestFileName = mode switch
        {
            "perCommand" => $"{safeName}.json",
            _ => "commands.json",
        };
        var manifestPath = Path.Combine(manifestsDir, manifestFileName);

        var stepSummaries = commandResult.Steps
            .Select(s =>
            {
                var fileOutputs = s.Outputs.TryGetValue("__fileOutputs", out var fo)
                    && fo is Dictionary<string, IReadOnlyList<string>> dict
                    ? (object)dict
                    : new Dictionary<string, IReadOnlyList<string>>();
                return new
                {
                    id = s.StepId,
                    success = s.Success,
                    exitCode = s.ExitCode,
                    fileOutputs,
                };
            })
            .ToList();

        var aggregatedFileOutputs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in commandResult.Steps)
        {
            if (step.Outputs.TryGetValue("__fileOutputs", out var fo)
                && fo is Dictionary<string, IReadOnlyList<string>> dict)
            {
                foreach (var (key, files) in dict)
                {
                    if (!aggregatedFileOutputs.TryGetValue(key, out var list))
                    {
                        list = [];
                        aggregatedFileOutputs[key] = list;
                    }

                    list.AddRange(files);
                }
            }
        }

        object entry;
        if (detail == "verbose")
        {
            entry = new
            {
                command = commandName,
                status = commandResult.Success ? "success" : "failure",
                version = commandResult.Version?.SemVer,
                fileOutputs = aggregatedFileOutputs,
                steps = stepSummaries,
                result = commandResult,
                generatedAt = DateTimeOffset.UtcNow,
            };
        }
        else
        {
            entry = new
            {
                command = commandName,
                status = commandResult.Success ? "success" : "failure",
                version = commandResult.Version?.SemVer,
                fileOutputs = aggregatedFileOutputs,
                steps = stepSummaries,
                generatedAt = DateTimeOffset.UtcNow,
            };
        }

        if (mode == "aggregate")
        {
            await WriteAggregateCommandManifestAsync(manifestPath, entry, cancellationToken);
            return;
        }

        object manifest;
        if (detail == "verbose")
        {
            manifest = new
            {
                schemaVersion = "1.0",
                generatedAt = DateTimeOffset.UtcNow,
                command = commandName,
                status = commandResult.Success ? "success" : "failure",
                version = commandResult.Version?.SemVer,
                fileOutputs = aggregatedFileOutputs,
                steps = stepSummaries,
                result = commandResult,
            };
        }
        else
        {
            manifest = new
            {
                schemaVersion = "1.0",
                generatedAt = DateTimeOffset.UtcNow,
                command = commandName,
                status = commandResult.Success ? "success" : "failure",
                version = commandResult.Version?.SemVer,
                fileOutputs = aggregatedFileOutputs,
                steps = stepSummaries,
            };
        }

        var json = System.Text.Json.JsonSerializer.Serialize(manifest, IndentedJsonOptions);
        await WriteTextFileAsync(manifestPath, json, cancellationToken);
    }

    private static async Task WriteAggregateCommandManifestAsync(string manifestPath, object entry, CancellationToken cancellationToken)
    {
        var commands = new List<object>();

        if (File.Exists(manifestPath))
        {
            try
            {
                var existingJson = await ReadTextFileAsyncWithRetry(manifestPath, cancellationToken);
                using var document = JsonDocument.Parse(existingJson);
                if (document.RootElement.TryGetProperty("commands", out var commandsElement) &&
                    commandsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var commandElement in commandsElement.EnumerateArray())
                    {
                        commands.Add(commandElement.Clone());
                    }
                }
            }
            catch
            {
                // Ignore malformed existing aggregate manifest and overwrite with a fresh one.
            }
        }

        commands.Add(entry);

        var manifest = new
        {
            schemaVersion = "1.0",
            generatedAt = DateTimeOffset.UtcNow,
            commands,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(manifest, IndentedJsonOptions);
        await WriteTextFileAsync(manifestPath, json, cancellationToken);
    }

    private static async Task<string> ReadTextFileAsyncWithRetry(string path, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path, cancellationToken);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    private static async Task WriteTextFileAsync(string path, string contents, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(path, contents, cancellationToken);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    private static string ResolveManifestsDirectory(string repositoryRoot, string outputRoot, RepoConfig config)
    {
        var manifestsPath = ResolveOutputPath(outputRoot, config.Outputs?.Manifests?.Path, "manifests");
        if (Path.IsPathRooted(manifestsPath))
        {
            return manifestsPath;
        }

        return Path.Combine(repositoryRoot, manifestsPath);
    }

    internal static string ResolveOutputRoot(RepoConfig config) =>
        string.IsNullOrWhiteSpace(config.Outputs?.Root)
            ? DefaultOutputRoot
            : config.Outputs.Root!;

    internal static bool ShouldEmitRuntimeFiles(RepoConfig config) =>
        config.Outputs?.Emit ?? true;

    private sealed record PlanPayload(
        PlanRepoSection Repo,
        PlanVersionSection? Version,
        IReadOnlyList<PlanArtifact> Artifacts,
        PlanPushSection Push);

    private sealed record PlanRepoSection(
        string Name,
        string? Branch,
        string? CommitSha,
        string? RemoteUrl);

    private sealed record PlanVersionSection(
        string SemVer,
        string? DockerVersion,
        string? NuGetVersion,
        string InformationalVersion);

    private sealed record PlanArtifact(
        string Type,
        string Name,
        IReadOnlyDictionary<string, string> BuildSettings,
        IReadOnlyList<string> Tags,
        bool Build,
        IReadOnlyList<string> ExpectedOutputs,
        IReadOnlyList<string> RequiredCredentials,
        PlanArtifactPush Push);

    private sealed record PlanArtifactPush(
        bool Requested,
        bool Eligible,
        string Decision,
        IReadOnlyList<string> SkipReasons);

    private sealed record PlanPushSection(
        bool Requested,
        bool? Eligible,
        string Decision,
        IReadOnlyList<string> SkipReasons);

    private sealed record PlanCredentialCheck(
        bool Available,
        string Detail);

    private static PushPolicyRules ParsePushPolicyRules(RepoConfig config)
    {
        if (config.Runtime?.Push is not { } structuredPush)
        {
            return PushPolicyRules.Default;
        }

        return new PushPolicyRules(
            Enabled: structuredPush.Enabled ?? true,
            NoPushInPullRequest: structuredPush.NoPushInPullRequest ?? false,
            RequireCleanWorkingTree: structuredPush.RequireCleanWorkingTree ?? false,
            Branches: structuredPush.Branches?.ToList() ?? []);
    }

    private static PushPolicyRules BuildEffectivePushPolicy(
        PushPolicyRules globalPolicy,
        Dictionary<string, JsonElement>? artifactSettings)
    {
        if (artifactSettings is null || artifactSettings.Count == 0)
        {
            return globalPolicy;
        }

        var enabled = TryGetBool(artifactSettings, "push.enabled")
            ?? TryGetBool(artifactSettings, "pushEnabled");
        var noPushInPullRequest = TryGetBool(artifactSettings, "push.noPushInPullRequest")
            ?? TryGetBool(artifactSettings, "push.noPushInPr")
            ?? TryGetBool(artifactSettings, "noPushInPullRequest");
        var requireCleanWorkingTree = TryGetBool(artifactSettings, "push.requireCleanWorkingTree")
            ?? TryGetBool(artifactSettings, "requireCleanWorkingTree");
        var branches = TryGetStringList(artifactSettings, "push.branches")
            ?? TryGetStringList(artifactSettings, "pushBranches");

        return globalPolicy with
        {
            Enabled = enabled ?? globalPolicy.Enabled,
            NoPushInPullRequest = noPushInPullRequest ?? globalPolicy.NoPushInPullRequest,
            RequireCleanWorkingTree = requireCleanWorkingTree ?? globalPolicy.RequireCleanWorkingTree,
            Branches = branches ?? globalPolicy.Branches,
        };
    }

    private static bool IsPushAllowed(
        PushPolicyRules policy,
        Core.Models.ExecutionContext ctx,
        out string reason)
    {
        if (!policy.Enabled)
        {
            reason = "Push skipped by policy: disabled.";
            return false;
        }

        if (policy.NoPushInPullRequest && ctx.IsPullRequest)
        {
            reason = "Push skipped by policy: pull request context.";
            return false;
        }

        if (policy.RequireCleanWorkingTree && !ctx.IsCleanWorkingTree)
        {
            reason = "Push skipped by policy: working tree is not clean.";
            return false;
        }

        if (policy.Branches.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(ctx.Branch))
            {
                reason = "Push skipped by policy: branch is unknown.";
                return false;
            }

            if (!policy.Branches.Any(pattern => BranchMatches(pattern, ctx.Branch!)))
            {
                reason = $"Push skipped by policy: branch '{ctx.Branch}' not allowed.";
                return false;
            }
        }

        reason = "Push allowed by policy.";
        return true;
    }

    private static bool BranchMatches(string pattern, string branch)
    {
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(branch, pattern["regex:".Length..], RegexOptions.CultureInvariant);
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(branch, regex, RegexOptions.CultureInvariant);
    }

    private static bool? TryGetBool(Dictionary<string, JsonElement> settings, string path)
    {
        if (!TryGetValue(settings, path, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? ReadBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static List<string>? ReadStringList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => SplitDelimitedList(value.GetString() ?? string.Empty),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToList(),
            _ => null,
        };
    }

    private static List<string>? TryGetStringList(Dictionary<string, JsonElement> settings, string path)
    {
        if (!TryGetValue(settings, path, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => SplitDelimitedList(value.GetString() ?? string.Empty),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToList(),
            _ => null,
        };
    }

    private static bool TryGetValue(Dictionary<string, JsonElement> settings, string path, out JsonElement value)
    {
        value = default;
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !settings.TryGetValue(parts[0], out value))
        {
            return false;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(parts[i], out value))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> SplitDelimitedList(string value) =>
        value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private sealed record PushPolicyRules(
        bool Enabled = true,
        bool NoPushInPullRequest = false,
        bool RequireCleanWorkingTree = false,
        List<string> Branches = null!)
    {
        public static PushPolicyRules Default => new(Branches: []);
    }

    private static StepResult EnrichWithFileOutputs(
        StepDefinition stepDef,
        StepResult stepResult,
        ExecutionContext context,
        string repositoryRoot,
        ITemplateRenderer templateRenderer)
    {
        if (stepDef.StepOutputs is null || stepDef.StepOutputs.Count == 0 || !stepResult.Success)
        {
            return stepResult;
        }

        var fileOutputs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, patterns) in stepDef.StepOutputs)
        {
            var matchedFiles = new List<string>();
            foreach (var pattern in patterns)
            {
                var resolvedPattern = templateRenderer.Render(pattern, context);
                var files = CollectGlobMatches(resolvedPattern, repositoryRoot);
                matchedFiles.AddRange(files);
            }

            if (matchedFiles.Count > 0)
            {
                fileOutputs[key] = matchedFiles;
            }
        }

        if (fileOutputs.Count == 0)
        {
            return stepResult;
        }

        var updatedOutputs = new Dictionary<string, object?>(stepResult.Outputs) { ["__fileOutputs"] = fileOutputs };
        return stepResult with { Outputs = updatedOutputs };
    }

    private static IEnumerable<string> CollectGlobMatches(string pattern, string repositoryRoot)
    {
        // Normalise the pattern: replace backslashes so glob logic is consistent
        pattern = pattern.Replace('\\', '/');

        // Split on ** to detect recursive patterns
        var recursiveSep = "/**/";
        var endsWithDoubleStar = pattern.EndsWith("/**", StringComparison.Ordinal);

        int recursiveIdx = pattern.IndexOf(recursiveSep, StringComparison.Ordinal);
        bool isRecursive = recursiveIdx >= 0 || endsWithDoubleStar;

        string dirPattern;
        string filePattern;

        if (endsWithDoubleStar)
        {
            dirPattern = pattern[..^3]; // strip /**
            filePattern = "*";
        }
        else if (isRecursive)
        {
            dirPattern = pattern[..recursiveIdx];
            filePattern = pattern[(recursiveIdx + recursiveSep.Length)..];
        }
        else
        {
            // No recursive glob — just split on last /
            var lastSlash = pattern.LastIndexOf('/');
            if (lastSlash < 0)
            {
                dirPattern = ".";
                filePattern = pattern;
            }
            else
            {
                dirPattern = pattern[..lastSlash];
                filePattern = pattern[(lastSlash + 1)..];
            }
        }

        // Resolve directory relative to repo root
        var searchDir = Path.IsPathRooted(dirPattern)
            ? dirPattern
            : Path.Combine(repositoryRoot, dirPattern.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(searchDir))
        {
            yield break;
        }

        var searchOption = isRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(searchDir, filePattern, searchOption);
        }
        catch (ArgumentException)
        {
            // filePattern contains invalid glob chars on this OS; yield nothing
            yield break;
        }

        foreach (var file in files)
        {
            // Return relative paths from repository root
            yield return Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
        }
    }

    private static StepDefinition BuildStepDefinition(RepoStepConfig stepConfig) =>
        new(
            Id: stepConfig.Id,
            Run: stepConfig.Run,
            Uses: stepConfig.Uses,
            Command: stepConfig.Command,
            When: stepConfig.When)
        {
            With = stepConfig.With,
            WhenExists = stepConfig.WhenExists ?? false,
            Parallel = stepConfig.Parallel ?? false,
            ContinueOnError = stepConfig.ContinueOnError ?? false,
            OutputPattern = stepConfig.OutputPattern,
            OutputFile = stepConfig.OutputFile,
            StepOutputs = BuildStepOutputs(stepConfig.Outputs),
            Container = stepConfig.Container is null
                ? null
                : new Core.Models.StepContainerDefinition(
                    stepConfig.Container.Image,
                    stepConfig.Container.Env,
                    stepConfig.Container.WorkingDirectory,
                    stepConfig.Container.Entrypoint,
                    stepConfig.Container.Dockerfile,
                    stepConfig.Container.Context,
                    stepConfig.Container.Build is null
                        ? null
                        : new Core.Models.StepContainerBuildDefinition(
                            stepConfig.Container.Build.Target,
                            stepConfig.Container.Build.Args)),
        };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? BuildStepOutputs(
        Dictionary<string, string[]>? outputs)
    {
        if (outputs is null || outputs.Count == 0) return null;
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, patterns) in outputs)
        {
            result[key] = patterns;
        }

        return result;
    }

    /// <summary>
    /// Builds the nested <c>outputs.*</c> dictionary from the resolved output paths.
    /// Missing values fall back to standard defaults under the artifacts root.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> BuildOutputsContext(RepoConfig config)
    {
        var emit = config.Outputs?.Emit ?? true;
        var root = config.Outputs?.Root ?? DefaultOutputRoot;

        if (!emit)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["emit"] = "false",
                ["root"] = root,
                ["tests"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["results"] = string.Empty,
                    ["coverage"] = string.Empty,
                    ["reports"] = string.Empty,
                },
                ["analysis"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reports"] = string.Empty,
                    ["sarif"] = string.Empty,
                },
                ["security"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["audit"] = string.Empty,
                    ["reports"] = string.Empty,
                    ["sarif"] = string.Empty,
                },
                ["packages"] = string.Empty,
                ["manifests"] = string.Empty,
                ["logs"] = string.Empty,
                ["temp"] = string.Empty,
            };
        }

        var testsResults = ResolveOutputPath(root, config.Outputs?.Tests?.Results, "tests");
        var testsCoverage = ResolveOutputPath(root, config.Outputs?.Tests?.Coverage, "coverage");
        var testsReports = ResolveOutputPath(root, config.Outputs?.Tests?.Reports, "tests/reports");
        var analysisReports = ResolveOutputPath(root, config.Outputs?.Analysis?.Reports, "analysis");
        var analysisSarif = ResolveOutputPath(root, config.Outputs?.Analysis?.Sarif, "analysis/sarif");
        var securityAudit = ResolveOutputPath(root, config.Outputs?.Security?.Audit, "security/audit.json");
        var securityReports = ResolveOutputPath(root, config.Outputs?.Security?.Reports, "security");
        var securitySarif = ResolveOutputPath(root, config.Outputs?.Security?.Sarif, "security/sarif");
        var packages = ResolveOutputPath(root, config.Outputs?.Packages, "packages");
        var manifests = ResolveOutputPath(root, config.Outputs?.Manifests?.Path, "manifests");
        var logs = ResolveOutputPath(root, config.Outputs?.Logs, "logs");
        var temp = ResolveOutputPath(root, config.Outputs?.Temp, "tmp");

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["emit"] = "true",
            ["root"] = root,
            ["tests"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["results"] = testsResults,
                ["coverage"] = testsCoverage,
                ["reports"] = testsReports,
            },
            ["analysis"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["reports"] = analysisReports,
                ["sarif"] = analysisSarif,
            },
            ["security"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["audit"] = securityAudit,
                ["reports"] = securityReports,
                ["sarif"] = securitySarif,
            },
            ["packages"] = packages,
            ["manifests"] = manifests,
            ["logs"] = logs,
            ["temp"] = temp,
        };
    }

    private static string ResolveOutputPath(string root, string? configuredPath, string defaultRelativePath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return CombineOutputPath(root, defaultRelativePath);
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        if (configuredPath.StartsWith("~/", StringComparison.Ordinal) ||
            configuredPath.StartsWith("~\\", StringComparison.Ordinal))
        {
            return CombineOutputPath(root, configuredPath[2..]);
        }

        // Plain relative paths are repo-relative, not outputs.root-relative.
        return configuredPath;
    }

    private static string ResolveManifestDirectory(string repositoryRoot, string outputRoot, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(repositoryRoot, outputRoot, "manifests");
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        if (configuredPath.StartsWith("~/", StringComparison.Ordinal) ||
            configuredPath.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(repositoryRoot, CombineOutputPath(outputRoot, configuredPath[2..]));
        }

        return Path.Combine(repositoryRoot, configuredPath);
    }

    private static string CombineOutputPath(string root, string relative)
    {
        var normalizedRoot = root.TrimEnd('/', '\\');
        var normalizedRelative = relative.TrimStart('/', '\\');

        return string.IsNullOrEmpty(normalizedRoot)
            ? normalizedRelative
            : $"{normalizedRoot}/{normalizedRelative}";
    }

    /// <summary>
    /// Converts the freeform <c>settings</c> JSON dictionary from the repo config into a
    /// nested <c>Dictionary&lt;string, object?&gt;</c> structure for template resolution.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> BuildVarsContext(RepoConfig config)
    {
        if (config.Vars is null || config.Vars.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, element) in config.Vars)
        {
            result[key] = ConvertJsonElement(element);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, object?> BuildSettingsContext(RepoConfig config)
    {
        if (config.Settings is null || config.Settings.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, element) in config.Settings)
        {
            result[key] = ConvertJsonElement(element);
        }

        return result;
    }

    private static object? ConvertJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.Object => ConvertJsonObject(element),
            JsonValueKind.Array => ConvertJsonArray(element),
            _ => element.ToString(),
        };

    private static Dictionary<string, object?> ConvertJsonObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = ConvertJsonElement(property.Value);
        }

        return dict;
    }

    private static List<object?> ConvertJsonArray(JsonElement element)
    {
        var list = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(ConvertJsonElement(item));
        }

        return list;
    }

    /// <summary>
    /// Groups consecutive steps marked <c>parallel: true</c> into batches.
    /// Sequential steps (parallel == false) form singleton groups.
    /// </summary>
    private static List<List<RepoStepConfig>> GroupSteps(IEnumerable<RepoStepConfig> steps)
    {
        var groups = new List<List<RepoStepConfig>>();
        List<RepoStepConfig>? currentGroup = null;

        foreach (var step in steps)
        {
            if (step.Parallel == true)
            {
                currentGroup ??= [];
                currentGroup.Add(step);
            }
            else
            {
                if (currentGroup is { Count: > 0 })
                {
                    groups.Add(currentGroup);
                    currentGroup = null;
                }
                groups.Add([step]);
            }
        }

        if (currentGroup is { Count: > 0 })
        {
            groups.Add(currentGroup);
        }

        return groups;
    }

    private static async Task<List<(RepoStepConfig Config, StepResult Result)>> ExecuteParallelGroupAsync(
        IReadOnlyList<RepoStepConfig> group,
        StepExecutor stepExecutor,
        ExecutionContext snapshot,
        int? maxParallel,
        CancellationToken cancellationToken)
    {
        var pending = group
            .Select(sc => (Config: sc, Definition: BuildStepDefinition(sc)))
            .ToList();

        var results = new List<(RepoStepConfig Config, StepResult Result)>();
        var completedInGroup = new Dictionary<string, StepResult>(StringComparer.OrdinalIgnoreCase);
        var knownGroupStepIds = pending
            .Select(p => p.Definition.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validate missing dependency references before execution starts.
        foreach (var step in pending)
        {
            foreach (var dependencyId in GetDependencies(step.Config))
            {
                var foundInGroup = knownGroupStepIds.Contains(dependencyId);
                var foundInCompleted = snapshot.CompletedSteps.ContainsKey(dependencyId);
                if (foundInGroup || foundInCompleted)
                {
                    continue;
                }

                var stepId = step.Definition.Id ?? step.Config.Id ?? "parallel-step";
                var failed = new StepResult(
                    stepId,
                    false,
                    1,
                    TimeSpan.Zero,
                    new Dictionary<string, object?>
                    {
                        ["error"] = $"Step dependency '{dependencyId}' was not found for step '{stepId}'.",
                    });

                results.Add((step.Config, failed));
                return results;
            }
        }

        while (pending.Count > 0)
        {
            var ready = pending
                .Where(step => DependenciesSatisfied(step.Config, snapshot.CompletedSteps, completedInGroup))
                .ToList();

            if (ready.Count == 0)
            {
                var unresolved = pending
                    .Select(p => p.Definition.Id ?? p.Config.Id ?? "<unnamed>")
                    .ToArray();
                var first = pending[0];
                var stepId = first.Definition.Id ?? first.Config.Id ?? "parallel-step";
                var failed = new StepResult(
                    stepId,
                    false,
                    1,
                    TimeSpan.Zero,
                    new Dictionary<string, object?>
                    {
                        ["error"] =
                            $"Parallel dependency deadlock/cycle detected. Unresolved steps: {string.Join(", ", unresolved)}.",
                    });

                results.Add((first.Config, failed));
                return results;
            }

            var stageContext = BuildStageContext(snapshot, completedInGroup.Values);

            List<(RepoStepConfig Config, StepResult Result)> stageResults;
            if (maxParallel is > 0)
            {
                using var semaphore = new SemaphoreSlim(maxParallel.Value, maxParallel.Value);
                var tasks = ready.Select(async step =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var result = await stepExecutor.ExecuteAsync(step.Definition, stageContext, cancellationToken);
                        return (step.Config, result);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                stageResults = [.. await Task.WhenAll(tasks)];
            }
            else
            {
                var tasks = ready.Select(async step =>
                {
                    var result = await stepExecutor.ExecuteAsync(step.Definition, stageContext, cancellationToken);
                    return (step.Config, result);
                });
                stageResults = [.. await Task.WhenAll(tasks)];
            }

            foreach (var stageResult in stageResults)
            {
                results.Add(stageResult);
                completedInGroup[stageResult.Result.StepId] = stageResult.Result;
            }

            foreach (var readyStep in ready)
            {
                pending.Remove(readyStep);
            }
        }

        return results;
    }

    private static IEnumerable<string> GetDependencies(RepoStepConfig step) =>
        step.DependsOn is not { Length: > 0 }
            ? []
            : step.DependsOn
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim());

    private static bool DependenciesSatisfied(
        RepoStepConfig step,
        IReadOnlyDictionary<string, StepResult> previouslyCompleted,
        IReadOnlyDictionary<string, StepResult> completedInGroup)
    {
        foreach (var dependencyId in GetDependencies(step))
        {
            if (!previouslyCompleted.ContainsKey(dependencyId) && !completedInGroup.ContainsKey(dependencyId))
            {
                return false;
            }
        }

        return true;
    }

    private static ExecutionContext BuildStageContext(
        ExecutionContext snapshot,
        IEnumerable<StepResult> completedInGroup)
    {
        var context = snapshot;
        foreach (var stepResult in completedInGroup)
        {
            context = context.WithStep(stepResult);
            if (stepResult.Outputs.TryGetValue("__version", out var versionObj) && versionObj is VersionResult version)
            {
                context = context.WithVersion(version);
            }
        }

        return context;
    }
}
