namespace Rexo.Cli;

using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Artifacts.DockerCompose;
using Rexo.Artifacts.Docker;
using Rexo.Artifacts.Generic;
using Rexo.Artifacts.Gradle;
using Rexo.Artifacts.Helm;
using Rexo.Artifacts.Maven;
using Rexo.Artifacts.Npm;
using Rexo.Artifacts.NuGet;
using Rexo.Artifacts.PyPi;
using Rexo.Artifacts.RubyGems;
using Rexo.Artifacts.Terraform;
using Rexo.Ci;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Policies;
using Rexo.Templating;
using Rexo.Versioning;

/// <summary>
/// Bootstraps the CLI service graph: registry, executor, and effective configuration.
/// Handles config loading, policy loading, service composition, and command registration.
/// </summary>
internal static class CliBootstrapper
{
    public static async Task<(
        CommandRegistry registry,
        DefaultCommandExecutor executor,
        RepoConfig? config,
        ConfigProvenanceSnapshot provenance,
        EnvironmentSelectionResult environmentSelection)>
        BuildServicesAsync(
            string workingDir,
            bool debug,
            IReadOnlyList<string>? setOverrides,
            CancellationToken cancellationToken,
            bool updatePolicies = false,
            bool requirePolicyLock = false,
            bool captureConfigProvenance = false,
            bool registerConfigCommands = true,
            string? explicitEnvironmentName = null,
            string? explicitSourceBranch = null)
    {
        if (debug) Console.WriteLine($"[debug] Loading configuration from {workingDir}");

        // Load config
        RepoConfig? config = await ConfigBuilder.LoadConfigAsync(workingDir, debug, cancellationToken);
        var configBeforePolicyDefaults = config;

        // Load and merge policies
        PolicyConfig? policyConfig = null;
        if (config is not null)
        {
            policyConfig = await ConfigBuilder.LoadAndMergePoliciesAsync(
                config,
                workingDir,
                debug,
                cancellationToken,
                ignorePolicyLock: updatePolicies,
                requirePolicyLock: requirePolicyLock);

            // Policy defaults (vars, settings, containers, ...) sit underneath the repo config; the repo always wins.
            config = RepoConfigurationLoader.ApplyPolicyDefaults(config, policyConfig);
        }

        var effectiveConfig = ConfigBuilder.MergePolicyIntoEffectiveConfig(configBeforePolicyDefaults, policyConfig);

        // Apply CLI --set overrides (highest-priority layer in the merge pipeline)
        if (setOverrides is { Count: > 0 })
        {
            if (debug)
            {
                foreach (var s in setOverrides)
                {
                    var separator = s.IndexOf('=', StringComparison.Ordinal);
                    var path = separator > 0 ? s[..separator] : "<invalid>";
                    Console.WriteLine($"[debug] --set override: {path}=<redacted>");
                }
            }

            var (mergedConfig, warnings) = ConfigBuilder.ApplySetOverridesWithWarnings(effectiveConfig, setOverrides);
            effectiveConfig = mergedConfig;

            // Emit warnings for malformed overrides
            foreach (var warning in warnings)
            {
                Console.Error.WriteLine($"[warn] {warning}");
            }
        }

        if (effectiveConfig is not null)
        {
            EnvironmentConfigurationValidator.Validate(effectiveConfig);
        }

        var selectionConfig = effectiveConfig ?? new RepoConfig(string.Empty, null, null);
        var sourceBranch = await SourceBranchResolver.ResolveAsync(
            selectionConfig,
            explicitSourceBranch,
            workingDir,
            cancellationToken);
        var ciInfo = CiDetector.Detect();
        var environmentSelection = EnvironmentSelectionResolver.Resolve(
            selectionConfig,
            explicitEnvironmentName,
            Environment.GetEnvironmentVariable("REXO_ENVIRONMENT"),
            sourceBranch.Branch,
            sourceBranch.Source,
            ciInfo.IsPullRequest);
        if (sourceBranch.Error is not null && environmentSelection.Error is null && !environmentSelection.IsExplicit)
        {
            environmentSelection = environmentSelection with { Error = sourceBranch.Error };
        }

        if (environmentSelection.IsResolved && environmentSelection.EnvironmentName is { } selectedEnvironment)
        {
            config = RepoConfigurationLoader.ApplyEnvironment(config!, selectedEnvironment);
            effectiveConfig = RepoConfigurationLoader.ApplyEnvironment(effectiveConfig!, selectedEnvironment);

            // Environment overlays are a config layer; CLI overrides remain the final layer.
            if (setOverrides is { Count: > 0 })
            {
                effectiveConfig = ConfigBuilder.ApplySetOverridesWithWarnings(effectiveConfig, setOverrides).config;
            }
        }

        // Create command registry
        var configPath = ConfigFileLocator.FindConfigPath(workingDir)
            ?? ConfigFileLocator.GetDefaultConfigPath(workingDir);
        var repositoryLayers = captureConfigProvenance && File.Exists(configPath)
            ? await ConfigProvenanceReader.ReadRepositoryLayersAsync(configPath, cancellationToken)
            : Array.Empty<ConfigLayerSnapshot>();
        var registry = BuiltinCommandRegistration.CreateDefault(effectiveConfig, File.Exists(configPath) ? configPath : null);
        var executor = new DefaultCommandExecutor(registry);

        // Register config commands if config is present
        if (config is not null && registerConfigCommands)
        {
            RegisterConfigCommands(registry, config, workingDir, executor, policyConfig ?? new PolicyConfig());
        }

        return (
            registry,
            executor,
            effectiveConfig,
            new ConfigProvenanceSnapshot(
                configBeforePolicyDefaults,
                policyConfig,
                setOverrides ?? [],
                File.Exists(configPath) ? configPath : null,
                repositoryLayers),
            environmentSelection);
    }

    private static void RegisterConfigCommands(
        CommandRegistry registry,
        RepoConfig config,
        string workingDir,
        DefaultCommandExecutor executor,
        PolicyConfig policyConfig)
    {
        // Set up provider registries
        var templateRenderer = new TemplateRenderer();
        var versionProviders = VersionProviderRegistry.CreateDefault();
        var artifactProviders = new ArtifactProviderRegistry();
        DockerArtifactProvider.Register(artifactProviders);
        DockerComposeArtifactProvider.Register(artifactProviders);
        GenericArtifactProvider.Register(artifactProviders);
        GradleArtifactProvider.Register(artifactProviders);
        HelmArtifactProvider.Register(artifactProviders);
        HelmOciArtifactProvider.Register(artifactProviders);
        MavenArtifactProvider.Register(artifactProviders);
        NpmArtifactProvider.Register(artifactProviders);
        NuGetArtifactProvider.Register(artifactProviders);
        PyPiArtifactProvider.Register(artifactProviders);
        RubyGemsArtifactProvider.Register(artifactProviders);
        TerraformArtifactProvider.Register(artifactProviders);

        RuntimeParityValidator.ValidateOrThrow(config, versionProviders, artifactProviders);

        var builtinRegistry = new BuiltinRegistry();
        var configLoader = new ConfigCommandLoader(
            builtinRegistry,
            templateRenderer,
            versionProviders,
            artifactProviders);

        configLoader.LoadInto(registry, config, workingDir, executor);
        configLoader.LoadPolicyCommandsInto(registry, policyConfig, config, workingDir, executor);
    }
}

internal sealed record ConfigProvenanceSnapshot(
    RepoConfig? RepositoryConfig,
    PolicyConfig? PolicyConfig,
    IReadOnlyList<string> SetOverrides,
    string? ConfigPath,
    IReadOnlyList<ConfigLayerSnapshot> RepositoryLayers);
