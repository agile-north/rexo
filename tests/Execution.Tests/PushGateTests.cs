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
        Assert.False(result.Artifacts[0].Pushed);
        Assert.NotEmpty(result.Artifacts[0].Tags);
        Assert.False(result.ArtifactsAnyPushed);
        Assert.Equal(0, result.ArtifactsPushedCount);
        Assert.False(result.ArtifactsAllPushed);
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
        Assert.True(result.ArtifactsAnyPushed);
        Assert.Equal(1, result.ArtifactsPushedCount);
        Assert.False(result.ArtifactsAllPushed);
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
        Assert.False(result.ArtifactsAnyPushed);
        Assert.Equal(0, result.ArtifactsPushedCount);
        Assert.False(result.ArtifactsAllPushed);
    }

    [Theory]
    [InlineData(null, false, 3, true)]
    [InlineData(null, true, 0, false)]
    [InlineData("a", false, 0, false)]
    [InlineData("b", false, 1, false)]
    public async Task PublicationSummaryAccountsForSkippedFailedAndUnprocessedArtifacts(
        string? failArtifact, bool skipPublication, int pushedCount, bool allPushed)
    {
        var provider = new RecordingArtifactProvider("docker", failArtifact: failArtifact, skipPublication: skipPublication);
        var config = new RepoConfig("test",
            new Dictionary<string, RepoCommandConfig>
            {
                ["publish"] = new("publish", [], [new(Id: "push", Uses: "builtin:push-artifacts")]),
                ["release"] = new("release", [],
                    [new(Id: "build", Uses: "builtin:build-artifacts"), new(Id: "publish", Command: "publish")]),
            }, new Dictionary<string, string>())
        {
            Artifacts = [new("docker", "a"), new("docker", "b"), new("docker", "c")],
            Outputs = new RepoOutputsConfig { Emit = false },
        };
        var (executor, _) = CreateExecutor(provider, config);

        var result = await ExecuteAsync(executor, "release",
            new Dictionary<string, string?> { ["confirm"] = "true" }, Path.GetTempPath());

        Assert.Equal(failArtifact is null, result.Success);
        Assert.Equal(3, result.PushDecisions.Count);
        Assert.Equal(pushedCount > 0, result.ArtifactsAnyPushed);
        Assert.Equal(pushedCount, result.ArtifactsPushedCount);
        Assert.Equal(allPushed, result.ArtifactsAllPushed);
        if (failArtifact is not null)
        {
            Assert.Contains(result.PushDecisions, decision => decision.ArtifactName == "c" &&
                !decision.Allowed && decision.Reason == "Push not attempted.");
        }
    }

    [Fact]
    public async Task MissingPushProviderFailsWithoutReportingCompletePublication()
    {
        var provider = new RecordingArtifactProvider("docker");
        var config = new RepoConfig("test",
            new Dictionary<string, RepoCommandConfig>
            {
                ["push"] = new("push", [], [new(Id: "push", Uses: "builtin:push-artifacts")]),
            }, new Dictionary<string, string>())
        {
            Artifacts = [new("docker", "a"), new("missing", "b"), new("docker", "c")],
            Outputs = new RepoOutputsConfig { Emit = false },
        };
        var (executor, _) = CreateExecutor(provider, config);

        var result = await ExecuteAsync(executor, "push",
            new Dictionary<string, string?> { ["confirm"] = "true" }, Path.GetTempPath());

        Assert.False(result.Success);
        Assert.Equal(6, result.ExitCode);
        Assert.Equal(3, result.Artifacts.Count);
        Assert.Equal(3, result.PushDecisions.Count);
        Assert.True(result.ArtifactsAnyPushed);
        Assert.Equal(1, result.ArtifactsPushedCount);
        Assert.False(result.ArtifactsAllPushed);
        Assert.Contains("No provider found", result.PushDecisions[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushWritesManifestToConfiguredOutputRoot()
    {
        var provider = new RecordingArtifactProvider("docker");
        var repoRoot = Path.Join(Path.GetTempPath(), $"rexo-output-root-{Guid.NewGuid():N}");
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
                    Outputs = new RepoOutputsConfig
                    {
                        Emit = true,
                        Root = "output",
                        Manifests = new RepoManifestOutputsConfig { Emit = true },
                    },
                },
                repoRoot);

            var result = await ExecuteAsync(
                executor,
                "push",
                new Dictionary<string, string?> { ["confirm"] = "true" },
                repoRoot);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Join(repoRoot, "output", "manifest.json")));
            Assert.True(File.Exists(Path.Join(repoRoot, "output", "manifests", "commands.json")));
            Assert.False(Directory.Exists(Path.Join(repoRoot, "output", "tests")));
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Theory]
    [InlineData(null, null, "skip")]
    [InlineData(null, null, "success")]
    [InlineData(null, null, "failure")]
    [InlineData(true, false, "skip")]
    [InlineData(true, false, "success")]
    [InlineData(true, false, "failure")]
    [InlineData(false, true, "skip")]
    [InlineData(false, true, "success")]
    [InlineData(false, true, "failure")]
    [InlineData(null, true, "skip")]
    [InlineData(null, true, "success")]
    [InlineData(null, true, "failure")]
    [InlineData(true, true, "skip")]
    [InlineData(true, true, "success")]
    [InlineData(true, true, "failure")]
    public async Task AutomaticArtifactAndCommandManifestsRequireBothEmissionPermissions(
        bool? emit, bool? manifestsEmit, string outcome)
    {
        using var _ = new CiEnvironmentScope();
        var provider = new RecordingArtifactProvider("docker", outcome != "failure");
        var repoRoot = Path.Join(Path.GetTempPath(), $"rexo-emit-off-{Guid.NewGuid():N}");
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
                    Outputs = new RepoOutputsConfig
                    {
                        Emit = emit,
                        Root = "output",
                        Manifests = new RepoManifestOutputsConfig { Emit = manifestsEmit },
                    },
                },
                repoRoot);

            var result = await ExecuteAsync(
                executor,
                "push",
                new Dictionary<string, string?> { ["confirm"] = outcome == "skip" ? "false" : "true" },
                repoRoot);

            Assert.Equal(outcome != "failure", result.Success);
            Assert.Single(result.Artifacts);
            Assert.Single(result.PushDecisions);
            Assert.Equal(outcome == "skip" ? 0 : 1, provider.PushedArtifacts.Count);
            var expected = emit != false && manifestsEmit == true;
            var manifestPath = Path.Join(repoRoot, "output", "manifest.json");
            Assert.Equal(expected, File.Exists(manifestPath));
            Assert.Equal(expected, File.Exists(Path.Join(repoRoot, "output", "manifests", "commands.json")));
            Assert.Equal(expected, Directory.Exists(Path.Join(repoRoot, "output")));
            Assert.False(Directory.Exists(Path.Join(repoRoot, "output", "tests")));
            if (expected)
            {
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
                Assert.Equal("a", Assert.Single(manifest.RootElement.GetProperty("Artifacts").EnumerateArray())
                    .GetProperty("Name").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    private sealed class RecordingArtifactProvider(
        string type, bool pushSuccess = true, string? failArtifact = null, bool skipPublication = false) : IArtifactProvider
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
            var success = pushSuccess && artifact.Name != failArtifact;
            return Task.FromResult(new ArtifactPushResult(artifact.Name, success,
                success && !skipPublication ? [$"{artifact.Name}:latest"] : []));
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
