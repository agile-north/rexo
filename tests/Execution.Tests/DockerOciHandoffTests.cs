namespace Rexo.Execution.Tests;

using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Artifacts.Docker;
using Rexo.Artifacts.NuGet;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Templating;
using Rexo.Versioning;

[Collection("EnvVar Mutation Sequential")]
public sealed class DockerOciHandoffTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"rexo-oci-{Guid.NewGuid():N}");
    private static readonly VersionResult Version = new("1.2.3", 1, 2, 3, null, "commit", "commit", false, true);

    public DockerOciHandoffTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task ArchiveBuildCanBeSealedVerifiedAndTransferredWithoutPublishing()
    {
        var invocations = new List<string[]>();
        var docker = Docker(invocations);
        var config = Config();
        var registry = Providers(docker);
        var build = await BuildAsync(config, registry);
        var entry = Assert.Single(Assert.IsType<List<ArtifactManifestEntry>>(build.Outputs["__artifacts"]));
        Assert.Equal("artifacts/image.oci.tar", entry.Location);
        Assert.NotNull(entry.ContentSha256);
        Assert.Single(invocations);
        Assert.Contains("buildx", invocations[0]);
        Assert.Contains(invocations[0], argument => argument.StartsWith("type=oci,dest=", StringComparison.Ordinal));
        Assert.DoesNotContain("login", invocations[0]);

        await WriteRunAsync(config, [entry]);
        await VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), registry, CancellationToken.None);
        var handoff = await VerifiedArtifactHandoff.VerifyAsync("handoff.json", config, Context(), registry, CancellationToken.None);
        Assert.Equal(entry.ContentSha256, Assert.Single(Assert.Single(handoff.Artifacts).Outputs).Identity);
        var builtins = new BuiltinRegistry();
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(), VersionProviderRegistry.CreateDefault(), registry);
        var commands = new CommandRegistry();
        loader.LoadInto(commands, config, _root, new DefaultCommandExecutor(commands));
        Assert.True(builtins.TryResolve("builtin:verify-artifact-handoff", out var verify));
        var verifyResult = await verify!(new StepDefinition("verify", null, "builtin:verify-artifact-handoff", null, null)
        {
            With = new Dictionary<string, string> { ["path"] = "handoff.json" },
        }, Context(), CancellationToken.None);
        Assert.True(verifyResult.Success);

        var other = Path.Join(_root, "transfer");
        Directory.CreateDirectory(Path.Join(other, "artifacts"));
        File.Copy(Path.Join(_root, "artifacts", "image.oci.tar"), Path.Join(other, "artifacts", "image.oci.tar"));
        File.Copy(Path.Join(_root, "handoff.json"), Path.Join(other, "handoff.json"));
        await VerifiedArtifactHandoff.VerifyAsync("handoff.json", config,
            Context() with { RepositoryRoot = other }, registry, CancellationToken.None);

        await File.AppendAllTextAsync(Path.Join(other, "artifacts", "image.oci.tar"), "tampered");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedArtifactHandoff.VerifyAsync("handoff.json", config,
                Context() with { RepositoryRoot = other }, registry, CancellationToken.None));
    }

    [Fact]
    public async Task SealRejectsArchiveChangedAfterBuild()
    {
        var config = Config();
        var registry = Providers(Docker([]));
        var build = await BuildAsync(config, registry);
        var entry = Assert.Single(Assert.IsType<List<ArtifactManifestEntry>>(build.Outputs["__artifacts"]));
        await WriteRunAsync(config, [entry]);
        WriteArchive(Path.Join(_root, "artifacts", "image.oci.tar"), "different manifest");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), registry, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedInventoryRejectsVerifiedPushBeforeAnyProviderPublishes(bool dryRun)
    {
        var calls = 0;
        var docker = Docker([]);
        var registry = Providers(docker, new NuGetArtifactProvider((_, _, _) =>
        {
            calls++;
            return Task.FromResult((0, string.Empty));
        }));
        var config = Config() with
        {
            Artifacts =
            [
                new RepoArtifactConfig("nuget", "Example"),
                Config().Artifacts![0],
            ],
        };
        Directory.CreateDirectory(Path.Join(_root, "artifacts", "packages"));
        await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "packages", "Example.1.2.3.nupkg"), "package");
        var dockerBuild = await BuildAsync(Config(), registry);
        var dockerEntry = Assert.Single(Assert.IsType<List<ArtifactManifestEntry>>(dockerBuild.Outputs["__artifacts"]));
        await WriteRunAsync(config, [new ArtifactManifestEntry("nuget", "Example", true, false, []), dockerEntry]);
        await VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), registry, CancellationToken.None);

        var builtins = new BuiltinRegistry();
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(), VersionProviderRegistry.CreateDefault(), registry);
        var commands = new CommandRegistry();
        loader.LoadInto(commands, config, _root, new DefaultCommandExecutor(commands));
        Assert.True(builtins.TryResolve("builtin:push-artifact-handoff", out var push));
        var step = new StepDefinition("push", null, "builtin:push-artifact-handoff", null, null)
        {
            With = new Dictionary<string, string> { ["path"] = "handoff.json" },
        };
        var result = await push!(step, Context() with
        {
            IsDryRun = dryRun,
            Options = new Dictionary<string, string?> { ["confirm"] = "true" },
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("does not support prepared publication", result.Outputs["error"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ArchiveModeRejectsExistingOutputAndDirectPush()
    {
        var invocations = new List<string[]>();
        var docker = Docker(invocations);
        var artifact = Artifact();
        Directory.CreateDirectory(Path.Join(_root, "artifacts"));
        await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "image.oci.tar"), "previous output");

        Assert.False((await docker.BuildAsync(artifact, Context(), CancellationToken.None)).Success);
        Assert.False((await docker.PushAsync(artifact, Context(), CancellationToken.None)).Success);
        Assert.Empty(invocations);
        Assert.Equal("previous output", await File.ReadAllTextAsync(Path.Join(_root, "artifacts", "image.oci.tar")));
    }

    [Fact]
    public async Task ArchiveDryRunDoesNotInvokeDockerOrCreateOutput()
    {
        var invocations = new List<string[]>();
        var result = await Docker(invocations).BuildAsync(
            Artifact(), Context() with { IsDryRun = true }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Null(result.Location);
        Assert.Empty(invocations);
        Assert.False(Directory.Exists(Path.Join(_root, "artifacts")));
    }

    [Fact]
    public async Task MixedOrdinaryPushRejectsArchiveBeforeOtherProvidersPublish()
    {
        var calls = 0;
        var providers = Providers(Docker([]), new NuGetArtifactProvider((_, _, _) =>
        {
            calls++;
            return Task.FromResult((0, string.Empty));
        }));
        var config = Config() with
        {
            Artifacts = [new RepoArtifactConfig("nuget", "Example"), Config().Artifacts![0]],
        };
        var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(), VersionProviderRegistry.CreateDefault(), providers);
        var result = await loader.PushArtifactsAsync("push", config, _root, "artifacts", false,
            Context() with { Options = new Dictionary<string, string?> { ["confirm"] = "true" } },
            static _ => true, "pushed", "empty", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("OCI archive publication is not supported", result.Outputs["error"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BuildRejectsArchiveWithCorruptedBlob()
    {
        var docker = new DockerArtifactProvider((args, _, _, _, _) =>
        {
            var path = args.Single(value => value.StartsWith("type=oci,dest=", StringComparison.Ordinal))["type=oci,dest=".Length..];
            WriteArchive(path, "layer-content");
            var bytes = File.ReadAllBytes(path);
            var content = Encoding.UTF8.GetBytes("layer-content");
            var offset = bytes.AsSpan().IndexOf(content);
            Assert.True(offset >= 0);
            bytes[offset] ^= 1;
            File.WriteAllBytes(path, bytes);
            return Task.FromResult((0, string.Empty));
        }, (_, _, _) => Task.FromResult(true));

        Assert.False((await docker.BuildAsync(Artifact(), Context(), CancellationToken.None)).Success);
    }

    private static DockerArtifactProvider Docker(List<string[]> calls) => new(
        (args, _, _, _, _) =>
        {
            calls.Add(args.ToArray());
            var output = args.Single(value => value.StartsWith("type=oci,dest=", StringComparison.Ordinal));
            WriteArchive(output["type=oci,dest=".Length..], "image manifest");
            return Task.FromResult((0, string.Empty));
        }, (_, _, _) => Task.FromResult(true));

    private static void WriteArchive(string path, string manifest)
    {
        var config = Encoding.UTF8.GetBytes("{}");
        var configHash = Convert.ToHexStringLower(SHA256.HashData(config));
        var layer = Encoding.UTF8.GetBytes(manifest);
        var layerHash = Convert.ToHexStringLower(SHA256.HashData(layer));
        var bytes = Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":2,"config":{"digest":"sha256:{{configHash}}","size":{{config.Length}}},"layers":[{"digest":"sha256:{{layerHash}}","size":{{layer.Length}}}]}""");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var file = File.Create(path);
        using var tar = new TarWriter(file);
        Add($"blobs/sha256/{configHash}", config);
        Add($"blobs/sha256/{layerHash}", layer);
        Add($"blobs/sha256/{hash}", bytes);
        Add("oci-layout", Encoding.UTF8.GetBytes("""{"imageLayoutVersion":"1.0.0"}"""));
        Add("index.json", Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":2,"manifests":[{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"sha256:{{hash}}","size":{{bytes.Length}}}]}"""));

        void Add(string name, byte[] content)
        {
            using var data = new MemoryStream(content);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
        }
    }

    private static ArtifactConfig Artifact() => new("docker", "image", Settings());

    private static Dictionary<string, JsonElement> Settings() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"image":"example/image","runner":"buildx","ociArchive":"artifacts/image.oci.tar"}""")!;

    private static RepoConfig Config() => new("example", null, null)
    {
        Artifacts = [new RepoArtifactConfig("docker", "image", Settings())],
    };

    private ExecutionContext Context() => ExecutionContext.Empty(_root) with { CommitSha = "commit", Version = Version };

    private static ArtifactProviderRegistry Providers(DockerArtifactProvider docker, NuGetArtifactProvider? nuget = null)
    {
        var providers = new ArtifactProviderRegistry();
        providers.Register("docker", docker);
        providers.Register("nuget", nuget ?? new NuGetArtifactProvider());
        return providers;
    }

    private async Task<StepResult> BuildAsync(RepoConfig config, ArtifactProviderRegistry providers)
    {
        var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(), VersionProviderRegistry.CreateDefault(), providers);
        var result = await loader.BuildArtifactsAsync("build", config, Context(), static _ => true,
            "built", "empty", CancellationToken.None);
        Assert.True(result.Success);
        return result;
    }

    private Task WriteRunAsync(RepoConfig config, IReadOnlyList<ArtifactManifestEntry> artifacts) =>
        File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(new RunManifest
        {
            Success = true,
            CommandExecuted = "release",
            CommitSha = "commit",
            Version = Version,
            ConfigHash = CanonicalConfigHasher.Compute(config),
            Steps = [new StepManifestEntry("build", true, 0, 1)],
            Artifacts = artifacts,
        }));
}
