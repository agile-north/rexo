namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class ArtifactGroupWorkflowTests
{
    private static readonly string[] ContractArtifactNames = ["contract-image", "contract-package"];
    private static readonly string[] DockerContractArtifactNames = ["contract-image"];
    private static readonly string[] NuGetContractArtifactNames = ["contract-package"];
    private static readonly string[] SingleContractArtifactName = ["contract"];
    private static readonly string[] AllArtifactNames = ["runtime", "contract-image", "contract-package"];
    private static readonly string[] AllStandardReleaseArtifactNames = ["runtime", "contract"];

    [Fact]
    public async Task LifecycleOperationsSelectGroupsAndComposeTypePredicates()
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-artifact-groups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var docker = new RecordingProvider("docker");
            var nuget = new RecordingProvider("nuget");
            var providers = CreateProviders(docker, nuget);
            var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
                VersionProviderRegistry.CreateDefault(), providers);
            var config = CreateConfig(
                new RepoArtifactConfig("docker", "runtime"),
                new RepoArtifactConfig("docker", "contract-image") { Group = "contracts" },
                new RepoArtifactConfig("nuget", "contract-package") { Group = "contracts" });
            var context = CreateContext(root, artifactGroup: "CONTRACTS");

            var plan = ConfigCommandLoader.PlanArtifacts(
                "plan", config, context, providers, pushRequested: true,
                includePredicate: static _ => true, successMessage: "planned", emptyMessage: "empty");
            Assert.True(plan.Success);
            using (var planJson = JsonDocument.Parse(Assert.IsType<string>(plan.Outputs["plan"])))
            {
                var payload = planJson.RootElement;
                Assert.Equal("CONTRACTS", payload.GetProperty("ArtifactGroup").GetString());
                Assert.Equal(ContractArtifactNames, payload.GetProperty("Artifacts").EnumerateArray()
                        .Select(artifact => artifact.GetProperty("Name").GetString()).ToArray());
            }

            var build = await loader.BuildArtifactsAsync(
                "build", config, context, static _ => true, "built", "empty", CancellationToken.None);
            Assert.True(build.Success);
            Assert.Equal(ContractArtifactNames, docker.BuildCalls.Concat(nuget.BuildCalls));

            docker.BuildCalls.Clear();
            var dockerBuild = await loader.BuildArtifactsAsync(
                "docker-build", config, context,
                static artifact => string.Equals(artifact.Type, "docker", StringComparison.OrdinalIgnoreCase),
                "built", "empty", CancellationToken.None);
            Assert.True(dockerBuild.Success);
            Assert.Equal(DockerContractArtifactNames, docker.BuildCalls);

            var tag = await loader.TagArtifactsAsync(
                "tag", config, context, static _ => true, "tagged", "empty", CancellationToken.None);
            Assert.True(tag.Success);
            Assert.Equal(DockerContractArtifactNames, docker.TagCalls);
            Assert.Equal(NuGetContractArtifactNames, nuget.TagCalls);

            var push = await loader.PushArtifactsAsync(
                "push", config, root, Path.Join(root, "artifacts"), emitRuntimeFiles: false,
                CreateContext(root, artifactGroup: "contracts", confirm: true), static _ => true,
                "pushed", "empty", CancellationToken.None);
            Assert.True(push.Success);
            var manifest = Assert.IsType<List<ArtifactManifestEntry>>(push.Outputs["__artifacts"]);
            Assert.Equal(ContractArtifactNames, manifest.Select(artifact => artifact.Name).ToArray());
            var decisions = Assert.IsType<List<PushDecision>>(push.Outputs["__pushDecisions"]);
            Assert.Equal(2, decisions.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AllArtifactGroupsSelectsDefaultAndNamedGroupsTogether()
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-artifact-groups-all-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var provider = new RecordingProvider("generic");
            var providers = CreateProviders(provider);
            var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
                VersionProviderRegistry.CreateDefault(), providers);
            var config = CreateConfig(
                new RepoArtifactConfig("generic", "runtime"),
                new RepoArtifactConfig("generic", "contract-image") { Group = "contracts" },
                new RepoArtifactConfig("generic", "contract-package") { Group = "contracts" });
            var context = CreateContext(root, allArtifactGroups: true);

            var plan = ConfigCommandLoader.PlanArtifacts(
                "plan", config, context, providers, pushRequested: false,
                includePredicate: static _ => true, successMessage: "planned", emptyMessage: "empty");
            Assert.True(plan.Success);
            using (var planJson = JsonDocument.Parse(Assert.IsType<string>(plan.Outputs["plan"])))
            {
                var payload = planJson.RootElement;
                Assert.Equal("all-artifact-groups", payload.GetProperty("ArtifactGroup").GetString());
                Assert.Equal(AllArtifactNames, payload.GetProperty("Artifacts").EnumerateArray()
                    .Select(artifact => artifact.GetProperty("Name").GetString()).ToArray());
            }

            var build = await loader.BuildArtifactsAsync(
                "build", config, context, static _ => true, "built", "empty", CancellationToken.None);
            Assert.True(build.Success);
            Assert.Equal(AllArtifactNames, provider.BuildCalls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GroupSelectorAndAllGroupsOptionCannotBeCombined()
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-artifact-groups-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
                VersionProviderRegistry.CreateDefault(), CreateProviders(new RecordingProvider("generic")));
            var result = await loader.BuildArtifactsAsync(
                "build",
                CreateConfig(new RepoArtifactConfig("generic", "runtime")),
                CreateContext(root, artifactGroup: "contracts", allArtifactGroups: true),
                static _ => true, "built", "empty", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("Use either --artifact-group", Assert.IsType<string>(result.Outputs["error"]),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EmptySelectionsFailWithAvailableGroupGuidanceButArtifactlessReposRemainValid()
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-artifact-groups-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var provider = new RecordingProvider("generic");
            var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
                VersionProviderRegistry.CreateDefault(), CreateProviders(provider));
            var groupedConfig = CreateConfig(
                new RepoArtifactConfig("generic", "contract-package") { Group = "contracts" });

            var defaultResult = await loader.BuildArtifactsAsync(
                "build", groupedConfig, CreateContext(root), static _ => true,
                "built", "empty", CancellationToken.None);
            Assert.False(defaultResult.Success);
            Assert.Contains("default artifact group", Assert.IsType<string>(defaultResult.Outputs["error"]),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("contracts", Assert.IsType<string>(defaultResult.Outputs["error"]), StringComparison.Ordinal);
            Assert.Contains("--artifact-group", Assert.IsType<string>(defaultResult.Outputs["error"]), StringComparison.Ordinal);

            var unknownResult = ConfigCommandLoader.PlanArtifacts(
                "plan", groupedConfig, CreateContext(root, "missing"), CreateProviders(provider),
                pushRequested: false, includePredicate: static _ => true,
                successMessage: "planned", emptyMessage: "empty");
            Assert.False(unknownResult.Success);
            Assert.Contains("Artifact group 'missing'", Assert.IsType<string>(unknownResult.Outputs["error"]),
                StringComparison.Ordinal);

            var artifactlessResult = await loader.BuildArtifactsAsync(
                "build", CreateConfig(), CreateContext(root, "missing"), static _ => true,
                "built", "No artifacts configured.", CancellationToken.None);
            Assert.True(artifactlessResult.Success);
            Assert.Equal("No artifacts configured.", artifactlessResult.Outputs["message"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StandardReleasePropagatesGroupWithoutSkippingRepositoryVerification()
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-artifact-groups-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configPath = Path.Join(root, "rexo.json");
        var provider = new RecordingProvider("generic");
        try
        {
            await File.WriteAllTextAsync(configPath, """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "artifact-groups-release",
                  "extends": ["embedded:standard"],
                  "versioning": { "provider": "fixed", "fallback": "1.2.3" },
                  "artifacts": [
                    { "type": "generic", "name": "runtime" },
                    { "type": "generic", "name": "contract", "group": "contracts" }
                  ]
                }
                """);
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
            var builtins = new BuiltinRegistry();
            var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(),
                VersionProviderRegistry.CreateDefault(), CreateProviders(provider));
            var registry = new CommandRegistry();
            var executor = new DefaultCommandExecutor(registry);
            loader.LoadInto(registry, config, root, executor);

            var result = await executor.ExecuteAsync(
                "release",
                new CommandInvocation(
                    new Dictionary<string, string>(),
                    new Dictionary<string, string?> { ["artifact-group"] = "contracts" },
                    Json: false,
                    JsonFile: null,
                    WorkingDirectory: root),
                CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Equal(SingleContractArtifactName, provider.BuildCalls);
            Assert.Equal(SingleContractArtifactName, provider.TagCalls);
            Assert.Contains(result.Steps, step => step.StepId == "verify" && step.Success);
            Assert.Equal("contract", Assert.Single(result.Artifacts).Name);

            provider.BuildCalls.Clear();
            provider.TagCalls.Clear();
            var allGroupsResult = await executor.ExecuteAsync(
                "release",
                new CommandInvocation(
                    new Dictionary<string, string>(),
                    new Dictionary<string, string?> { ["all-artifact-groups"] = "true" },
                    Json: false,
                    JsonFile: null,
                    WorkingDirectory: root),
                CancellationToken.None);
            Assert.True(allGroupsResult.Success, allGroupsResult.Message);
            Assert.Equal(AllStandardReleaseArtifactNames, provider.BuildCalls);
            Assert.Equal(AllStandardReleaseArtifactNames, provider.TagCalls);
            Assert.Equal(AllStandardReleaseArtifactNames, allGroupsResult.Artifacts.Select(artifact => artifact.Name));
            Assert.Contains(allGroupsResult.Steps, step => step.StepId == "verify" && step.Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static RepoConfig CreateConfig(params RepoArtifactConfig[] artifacts) =>
        new("artifact-groups", null, null) { Artifacts = [.. artifacts] };

    private static Rexo.Core.Models.ExecutionContext CreateContext(
        string root,
        string? artifactGroup = null,
        bool confirm = false,
        bool allArtifactGroups = false)
    {
        var options = new Dictionary<string, string?>();
        if (artifactGroup is not null)
        {
            options["artifact-group"] = artifactGroup;
        }
        if (confirm)
        {
            options["confirm"] = "true";
        }
        if (allArtifactGroups)
        {
            options["all-artifact-groups"] = "true";
        }

        return Rexo.Core.Models.ExecutionContext.Empty(root) with { Options = options };
    }

    private static ArtifactProviderRegistry CreateProviders(params RecordingProvider[] providers)
    {
        var registry = new ArtifactProviderRegistry();
        foreach (var provider in providers)
        {
            registry.Register(provider.Type, provider);
        }

        return registry;
    }

    private sealed class RecordingProvider(string type) : IArtifactProvider
    {
        public string Type { get; } = type;
        public List<string> BuildCalls { get; } = [];
        public List<string> TagCalls { get; } = [];
        public List<string> PushCalls { get; } = [];

        public Task<ArtifactBuildResult> BuildAsync(
            ArtifactConfig artifact,
            Rexo.Core.Models.ExecutionContext context,
            CancellationToken cancellationToken)
        {
            BuildCalls.Add(artifact.Name);
            return Task.FromResult(new ArtifactBuildResult(artifact.Name, true, null));
        }

        public Task<ArtifactTagResult> TagAsync(
            ArtifactConfig artifact,
            Rexo.Core.Models.ExecutionContext context,
            CancellationToken cancellationToken)
        {
            TagCalls.Add(artifact.Name);
            return Task.FromResult(new ArtifactTagResult(artifact.Name, true, []));
        }

        public Task<ArtifactPushResult> PushAsync(
            ArtifactConfig artifact,
            Rexo.Core.Models.ExecutionContext context,
            CancellationToken cancellationToken)
        {
            PushCalls.Add(artifact.Name);
            return Task.FromResult(new ArtifactPushResult(artifact.Name, true, [$"{artifact.Name}:1.0.0"]));
        }
    }
}
