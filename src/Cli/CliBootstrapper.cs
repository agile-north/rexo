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
    public static async Task<(CommandRegistry registry, DefaultCommandExecutor executor, RepoConfig? config)>
        BuildServicesAsync(string workingDir, bool debug, IReadOnlyList<string>? setOverrides, CancellationToken cancellationToken)
    {
        if (debug) Console.WriteLine($"[debug] Loading configuration from {workingDir}");

        // Load config
        RepoConfig? config = await ConfigBuilder.LoadConfigAsync(workingDir, debug, cancellationToken);
        var configBeforePolicyDefaults = config;

        // Load and merge policies
        PolicyConfig? policyConfig = null;
        if (config is not null)
        {
            policyConfig = await ConfigBuilder.LoadAndMergePoliciesAsync(config, workingDir, debug, cancellationToken);

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
                    Console.WriteLine($"[debug] --set override: {s}");
            }

            var (mergedConfig, warnings) = ConfigBuilder.ApplySetOverridesWithWarnings(effectiveConfig, setOverrides);
            effectiveConfig = mergedConfig;

            // Emit warnings for malformed overrides
            foreach (var warning in warnings)
            {
                Console.Error.WriteLine($"[warn] {warning}");
            }
        }

        // Create command registry
        var configPath = ConfigFileLocator.FindConfigPath(workingDir)
            ?? ConfigFileLocator.GetDefaultConfigPath(workingDir);
        var registry = BuiltinCommandRegistration.CreateDefault(effectiveConfig, File.Exists(configPath) ? configPath : null);
        var executor = new DefaultCommandExecutor(registry);

        // Register config commands if config is present
        if (config is not null)
        {
            RegisterConfigCommands(registry, config, workingDir, executor, policyConfig ?? new PolicyConfig());
        }

        return (registry, executor, effectiveConfig);
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
