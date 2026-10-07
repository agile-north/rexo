namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class VerifiedNuGetHandoffTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"rexo-handoff-{Guid.NewGuid():N}");
    private readonly RepoConfig _config = new("example", null, null)
    {
        Artifacts = [new RepoArtifactConfig("nuget", "Example")],
    };
    private readonly VersionResult _version = new("1.2.3", 1, 2, 3, null, "commit", "commit", false, true)
    {
        NuGetVersion = "1.2.3",
    };

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task HandoffTransfersExactPackagesBetweenRepositoryRoots()
    {
        await PrepareAsync();
        await SealAsync();
        var otherRoot = Path.Join(_root, "download");
        Directory.CreateDirectory(Path.Join(otherRoot, "artifacts", "packages"));
        File.Copy(Path.Join(_root, "artifacts", "packages", "Example.1.2.3.nupkg"),
            Path.Join(otherRoot, "artifacts", "packages", "Example.1.2.3.nupkg"));
        File.Copy(Path.Join(_root, "handoff.json"), Path.Join(otherRoot, "handoff.json"));

        var version = await VerifiedNuGetHandoff.VerifyAsync("handoff.json", _config,
            Context() with { RepositoryRoot = otherRoot }, CancellationToken.None);

        Assert.Equal(_version.SemVer, version.SemVer);
        Assert.False(File.Exists(Path.Join(otherRoot, "run.json")));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("commit")]
    [InlineData("config")]
    [InlineData("version")]
    [InlineData("ci-run")]
    [InlineData("lock")]
    public async Task VerificationRejectsChangedEvidence(string change)
    {
        await PrepareAsync();
        await SealAsync();
        var context = Context();
        var config = _config;
        var package = Path.Join(_root, "artifacts", "packages", "Example.1.2.3.nupkg");
        switch (change)
        {
            case "bytes":
                await File.WriteAllTextAsync(package, "changed");
                break;
            case "missing":
                File.Delete(package);
                break;
            case "commit":
                context = context with { CommitSha = "different" };
                break;
            case "config":
                config = config with { Name = "different" };
                break;
            case "version":
                context = context with { Version = _version with { SemVer = "2.0.0" } };
                break;
            case "ci-run":
                context = context with { CiBuildId = "different" };
                break;
            case "lock":
                Directory.CreateDirectory(Path.Join(_root, ".rexo"));
                await File.WriteAllTextAsync(Path.Join(_root, ".rexo", "rexo.lock.yaml"), "changed");
                break;
        }

        if (change == "missing")
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                VerifiedNuGetHandoff.VerifyAsync("handoff.json", config, context, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                VerifiedNuGetHandoff.VerifyAsync("handoff.json", config, context, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("pushed")]
    [InlineData("failed-step")]
    [InlineData("duplicate")]
    [InlineData("not-release")]
    public async Task SealRejectsInvalidReleaseEvidence(string change)
    {
        var run = await PrepareAsync();
        run = change switch
        {
            "failure" => run with { Success = false },
            "pushed" => run with { Artifacts = [run.Artifacts[0] with { Pushed = true }] },
            "failed-step" => run with { Steps = [new StepManifestEntry("verify", false, 1, 1)] },
            "duplicate" => run with { Artifacts = [run.Artifacts[0], run.Artifacts[0]] },
            _ => run with { CommandExecuted = "build" },
        };
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run));
        await Assert.ThrowsAsync<InvalidOperationException>(SealAsync);
        Assert.False(File.Exists(Path.Join(_root, "handoff.json")));
    }

    [Theory]
    [InlineData("../outside.nupkg")]
    [InlineData("artifacts/packages/Other.1.2.3.nupkg")]
    [InlineData("/absolute.nupkg")]
    public async Task VerificationRejectsUnexpectedPackageInventory(string path)
    {
        await PrepareAsync();
        await SealAsync();
        var handoffPath = Path.Join(_root, "handoff.json");
        var handoff = JsonSerializer.Deserialize<NuGetHandoff>(await File.ReadAllTextAsync(handoffPath))!;
        await File.WriteAllTextAsync(handoffPath, JsonSerializer.Serialize(handoff with
        {
            Packages = [handoff.Packages[0] with { Path = path }],
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedNuGetHandoff.VerifyAsync("handoff.json", _config, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task SealRejectsOutputTraversal()
    {
        await PrepareAsync();
        var config = _config with
        {
            Artifacts = [new RepoArtifactConfig("nuget", "Example",
                new Dictionary<string, JsonElement> { ["output"] = JsonSerializer.SerializeToElement("../outside") })],
        };
        var run = JsonSerializer.Deserialize<RunManifest>(await File.ReadAllTextAsync(Path.Join(_root, "run.json")))!;
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"),
            JsonSerializer.Serialize(run with { ConfigHash = CanonicalConfigHasher.Compute(config) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedNuGetHandoff.SealAsync("run.json", "handoff.json", config, Context(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task PublishBuiltinNeverBuildsAndVerifiesBeforeCallingProvider(bool dryRun, bool tamper, bool confirm)
    {
        await PrepareAsync();
        await SealAsync();
        if (tamper)
        {
            await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "packages", "Example.1.2.3.nupkg"), "changed");
        }
        var builtins = new BuiltinRegistry();
        var providers = new ArtifactProviderRegistry();
        var provider = new PushOnlyProvider();
        providers.Register("nuget", provider);
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), providers);
        var commands = new CommandRegistry();
        loader.LoadInto(commands, _config, _root, new DefaultCommandExecutor(commands));
        Assert.True(builtins.TryResolve("builtin:push-nuget-handoff", out var push));
        Assert.NotNull(push);
        var step = new StepDefinition("publish", null, "builtin:push-nuget-handoff", null, null)
        {
            With = new Dictionary<string, string> { ["path"] = "handoff.json" },
        };
        var context = Context() with
        {
            IsDryRun = dryRun,
            Options = new Dictionary<string, string?> { ["confirm"] = confirm ? "true" : "false" },
        };

        if (tamper || !confirm)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => push!(step, context, CancellationToken.None));
        }
        else
        {
            var result = await push!(step, context, CancellationToken.None);
            Assert.True(result.Success);
        }
        Assert.Equal(!dryRun && !tamper && confirm ? 1 : 0, provider.PushCalls);
    }

    private ExecutionContext Context() => ExecutionContext.Empty(_root) with
    {
        CommitSha = "commit",
        Version = _version,
        IsCi = true,
        CiBuildId = "run",
    };

    private Task SealAsync() => VerifiedNuGetHandoff.SealAsync(
        "run.json", "handoff.json", _config, Context(), CancellationToken.None);

    private async Task<RunManifest> PrepareAsync()
    {
        Directory.CreateDirectory(Path.Join(_root, "artifacts", "packages"));
        await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "packages", "Example.1.2.3.nupkg"), "verified bytes");
        var run = new RunManifest
        {
            Success = true,
            CommandExecuted = "release",
            CommitSha = "commit",
            IsCi = true,
            CiBuildId = "run",
            Version = _version,
            ConfigHash = CanonicalConfigHasher.Compute(_config),
            Steps = [new StepManifestEntry("verify", true, 0, 1)],
            Artifacts = [new ArtifactManifestEntry("nuget", "Example", true, false, [])],
        };
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run));
        return run;
    }

    private sealed class PushOnlyProvider : IArtifactProvider
    {
        public string Type => "nuget";
        public int PushCalls { get; private set; }

        public Task<ArtifactBuildResult> BuildAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Publication must not build.");

        public Task<ArtifactTagResult> TagAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Publication must not retag package bytes.");

        public Task<ArtifactPushResult> PushAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken)
        {
            Assert.Equal("1.2.3", context.Version?.NuGetVersion);
            PushCalls++;
            return Task.FromResult(new ArtifactPushResult(artifact.Name, true, ["feed/Example"]));
        }
    }
}
