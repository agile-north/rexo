namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Configuration.Models;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Templating;
using Rexo.Versioning;

[Collection("EnvVar Mutation Sequential")]
public sealed class PushGateTests
{
    [Fact]
    public async Task PushDryRunSimulatesSuccessWithoutCallingProvider()
    {
        var provider = new RecordingArtifactProvider("docker");
        var (executor, _) = CreateExecutor(
            provider,
            new RepoConfig(
                Name: "test",
                Commands: new Dictionary<string, RepoCommandConfig>
                {
                    ["push"] = new RepoCommandConfig(
                        Description: "push",
                        Options: new Dictionary<string, RepoOptionConfig>(),
                        Steps: [new RepoStepConfig(Id: "push", Uses: "builtin:push-artifacts")]),
                },
                Aliases: new Dictionary<string, string>())
            {
                Artifacts = [new RepoArtifactConfig("docker", "a")],
            });

        var result = await ExecuteAsync(
            executor,
            "push",
            new Dictionary<string, string?>
            {
                ["dry-run"] = "true",
            },
            Path.GetTempPath());

        Assert.True(result.Success);
        Assert.Empty(provider.PushedArtifacts);
        Assert.Single(result.PushDecisions);
        Assert.Contains("dry run", result.PushDecisions[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.Artifacts);
        Assert.True(result.Artifacts[0].Pushed);
        Assert.NotEmpty(result.Artifacts[0].Tags);
    }

    [Fact]
    public async Task GlobalPushGateCanBeOverriddenPerArtifact()
    {
        var provider = new RecordingArtifactProvider("docker");
        var (executor, _) = CreateExecutor(
            provider,
            new RepoConfig(
                Name: "test",
                Commands: new Dictionary<string, RepoCommandConfig>
                {
                    ["push"] = new RepoCommandConfig(
                        Description: "push",
                        Options: new Dictionary<string, RepoOptionConfig>(),
                        Steps: [new RepoStepConfig(Id: "push", Uses: "builtin:push-artifacts")]),
                },
                Aliases: new Dictionary<string, string>())
            {
                Artifacts =
                [
                    new RepoArtifactConfig("docker", "a"),
                    new RepoArtifactConfig(
                        "docker",
                        "b",
                        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                            """
                            {
                              "push": {
                                "enabled": true
                              }
                            }
                            """)!),
                ],
                Runtime = new RepoRuntimeConfig(
                    Push: new RepoPushConfig(Enabled: false)),
            });

        var result = await ExecuteAsync(executor, "push", new Dictionary<string, string?> { ["confirm"] = "true" }, Path.GetTempPath());

        Assert.True(result.Success);
        Assert.Single(provider.PushedArtifacts);
        Assert.Equal("b", provider.PushedArtifacts[0]);

        Assert.Equal(2, result.PushDecisions.Count);
        var denied = result.PushDecisions.Single(d => d.ArtifactName == "a");
        Assert.False(denied.Allowed);
        Assert.Contains("disabled", denied.Reason, StringComparison.OrdinalIgnoreCase);

        var allowed = result.PushDecisions.Single(d => d.ArtifactName == "b");
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public async Task BranchGateCanBeOverriddenPerArtifact()
    {
        var provider = new RecordingArtifactProvider("docker");
        var (executor, _) = CreateExecutor(
            provider,
            new RepoConfig(
                Name: "test",
                Commands: new Dictionary<string, RepoCommandConfig>
                {
                    ["push"] = new RepoCommandConfig(
                        Description: "push",
                        Options: new Dictionary<string, RepoOptionConfig>(),
                        Steps: [new RepoStepConfig(Id: "push", Uses: "builtin:push-artifacts")]),
                },
                Aliases: new Dictionary<string, string>())
            {
                Artifacts =
                [
                    new RepoArtifactConfig("docker", "a"),
                    new RepoArtifactConfig(
                        "docker",
                        "b",
                        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                            """
                            {
                              "push": {
                                "branches": []
                              }
                            }
                            """)!),
                ],
                Runtime = new RepoRuntimeConfig(
                    Push: new RepoPushConfig(Branches: ["main"])),
            });

        var result = await ExecuteAsync(executor, "push", new Dictionary<string, string?> { ["confirm"] = "true" }, Path.GetTempPath());

        Assert.True(result.Success);
        Assert.Single(provider.PushedArtifacts);
        Assert.Equal("b", provider.PushedArtifacts[0]);

        var denied = result.PushDecisions.Single(d => d.ArtifactName == "a");
        Assert.False(denied.Allowed);
        Assert.Contains("branch", denied.Reason, StringComparison.OrdinalIgnoreCase);

        var allowed = result.PushDecisions.Single(d => d.ArtifactName == "b");
        Assert.True(allowed.Allowed);
    }

    private static (DefaultCommandExecutor Executor, BuiltinRegistry Builtins) CreateExecutor(
        IArtifactProvider provider,
        RepoConfig config,
        string? repositoryRoot = null)
    {
        var builtins = new BuiltinRegistry();
        var providerRegistry = new ArtifactProviderRegistry();
        providerRegistry.Register(provider.Type, provider);

        var loader = new ConfigCommandLoader(
            builtins,
            new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(),
            providerRegistry);

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        loader.LoadInto(registry, config, repositoryRoot ?? Path.GetTempPath(), executor);
        return (executor, builtins);
    }

    private static Task<CommandResult> ExecuteAsync(DefaultCommandExecutor executor, string commandName) =>
        ExecuteAsync(executor, commandName, new Dictionary<string, string?>(), Path.GetTempPath());

    private static Task<CommandResult> ExecuteAsync(
        DefaultCommandExecutor executor,
        string commandName,
        IReadOnlyDictionary<string, string?> options,
        string workingDirectory) =>
        executor.ExecuteAsync(
            commandName,
            new CommandInvocation(
                new Dictionary<string, string>(),
                options,
                Json: false,
                JsonFile: null,
                WorkingDirectory: workingDirectory),
            CancellationToken.None);

    [Fact]
    public async Task PushRequiresExplicitConfirmationLocally()
    {
        using var _ = new CiEnvironmentScope();

        var provider = new RecordingArtifactProvider("docker");
        var (executor, _) = CreateExecutor(
            provider,
            new RepoConfig(
                Name: "test",
                Commands: new Dictionary<string, RepoCommandConfig>
                {
                    ["push"] = new RepoCommandConfig(
                        Description: "push",
                        Options: new Dictionary<string, RepoOptionConfig>(),
                        Steps: [new RepoStepConfig(Id: "push", Uses: "builtin:push-artifacts")]),
                },
                Aliases: new Dictionary<string, string>())
            {
                Artifacts = [new RepoArtifactConfig("docker", "a")],
            });

        var result = await ExecuteAsync(executor, "push");

        Assert.True(result.Success);
        Assert.Empty(provider.PushedArtifacts);
        Assert.Single(result.PushDecisions);
        Assert.Contains("use --confirm locally", result.PushDecisions[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PushWritesManifestToConfiguredOutputRoot()
    {
        var provider = new RecordingArtifactProvider("docker");
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-output-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            var (executor, _) = CreateExecutor(
                provider,
                new RepoConfig(
                    Name: "test",
                    Commands: new Dictionary<string, RepoCommandConfig>
                    {
                        ["push"] = new RepoCommandConfig(
                            Description: "push",
                            Options: new Dictionary<string, RepoOptionConfig>(),
                            Steps: [new RepoStepConfig(Id: "push", Uses: "builtin:push-artifacts")]),
                    },
                    Aliases: new Dictionary<string, string>())
                {
                    Artifacts = [new RepoArtifactConfig("docker", "a")],
                    Outputs = new RepoOutputsConfig { Emit = true, Root = "output" },
                },
                repoRoot);

            var result = await ExecuteAsync(
                executor,
                "push",
                new Dictionary<string, string?> { ["confirm"] = "true" },
                repoRoot);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(repoRoot, "output", "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Fact]
    public async Task PushDoesNotWriteManifestWhenEmitRuntimeFilesDisabled()
    {
        var provider = new RecordingArtifactProvider("docker");
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-emit-off-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            var (executor, _) = CreateExecutor(
                provider,
                new RepoConfig(
                    Name: "test",
                    Commands: new Dictionary<string, RepoCommandConfig>
                    {
                        ["push"] = new RepoCommandConfig(
                            Description: "push",
                            Options: new Dictionary<string, RepoOptionConfig>(),
                            Steps: [new RepoStepConfig(Id: "push", Uses: "builtin:push-artifacts")]),
                    },
                    Aliases: new Dictionary<string, string>())
                {
                    Artifacts = [new RepoArtifactConfig("docker", "a")],
                    Outputs = new RepoOutputsConfig { Emit = false, Root = "output" },
                },
                repoRoot);

            var result = await ExecuteAsync(
                executor,
                "push",
                new Dictionary<string, string?> { ["confirm"] = "true" },
                repoRoot);

            Assert.True(result.Success);
            Assert.False(File.Exists(Path.Combine(repoRoot, "output", "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    private sealed class RecordingArtifactProvider(string type) : IArtifactProvider
    {
        public string Type { get; } = type;
        public List<string> PushedArtifacts { get; } = [];

        public Task<ArtifactBuildResult> BuildAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ArtifactBuildResult(artifact.Name, true, null));

        public Task<ArtifactTagResult> TagAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ArtifactTagResult(artifact.Name, true, Array.Empty<string>()));

        public Task<ArtifactPushResult> PushAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken)
        {
            PushedArtifacts.Add(artifact.Name);
            return Task.FromResult(new ArtifactPushResult(artifact.Name, true, [$"{artifact.Name}:latest"]));
        }
    }

    /// <summary>Clears CI environment variables for the duration of a test, then restores them.</summary>
    private sealed class CiEnvironmentScope : IDisposable
    {
        private static readonly string[] CiVars = ["GITHUB_ACTIONS", "TF_BUILD", "GITLAB_CI", "BITBUCKET_BUILD_NUMBER", "CI"];
        private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);

        public CiEnvironmentScope()
        {
            foreach (var v in CiVars)
            {
                _saved[v] = Environment.GetEnvironmentVariable(v);
                Environment.SetEnvironmentVariable(v, null);
            }
        }

        public void Dispose()
        {
            foreach (var (v, val) in _saved)
            {
                Environment.SetEnvironmentVariable(v, val);
            }
        }
    }
}
