namespace Rexo.Execution.Tests;

using System.Text.Json;
using System.Diagnostics;
using Rexo.Artifacts;
using Rexo.Artifacts.NuGet;
using Rexo.Artifacts.Generic;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class VerifiedArtifactHandoffTests : IDisposable
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

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task HandoffInventoryIsLimitedToSelectedGroup()
    {
        var runtime = new RepoArtifactConfig("nuget", "Contracts");
        var contracts = new RepoArtifactConfig("nuget", "Contracts") { Group = "contracts" };
        var config = _config with { Artifacts = [runtime, contracts] };
        Directory.CreateDirectory(Path.Join(_root, "artifacts", "packages"));
        await File.WriteAllTextAsync(
            Path.Join(_root, "artifacts", "packages", "Contracts.1.2.3.nupkg"),
            "verified contracts bytes");
        var run = new RunManifest
        {
            Success = true,
            CommandExecuted = "release",
            CommitSha = "commit",
            IsCi = true,
            CiBuildId = "run",
            Version = _version,
            ConfigHash = CanonicalConfigHasher.Compute(config),
            Steps = [new StepManifestEntry("verify", true, 0, 1)],
            Artifacts = [new ArtifactManifestEntry("nuget", "Contracts", true, false, [])
            {
                HandoffOutputs = (await new NuGetArtifactProvider().PrepareAsync(
                    new ArtifactConfig("nuget", "Contracts", new Dictionary<string, JsonElement>()),
                    Context(), CancellationToken.None)).Outputs,
            }],
        };
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run));

        var selectedContext = Context() with
        {
            Options = new Dictionary<string, string?> { ["artifact-group"] = "contracts" },
        };
        await VerifiedArtifactHandoff.SealAsync(
            "run.json", "handoff.json", config, selectedContext, Providers(), CancellationToken.None);
        var handoff = await VerifiedArtifactHandoff.VerifyAsync(
            "handoff.json", config, selectedContext, Providers(), CancellationToken.None);
        Assert.Equal("Contracts", Assert.Single(handoff.Artifacts).Name);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedArtifactHandoff.VerifyAsync("handoff.json", config, Context(), Providers(), CancellationToken.None));
        Assert.Contains("group selection does not match", exception.Message, StringComparison.Ordinal);
    }

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

        var handoff = await VerifiedArtifactHandoff.VerifyAsync("handoff.json", _config,
            Context() with { RepositoryRoot = otherRoot }, Providers(), CancellationToken.None);

        Assert.Equal(_version.SemVer, handoff.Run.Version!.SemVer);
        Assert.False(File.Exists(Path.Join(otherRoot, "run.json")));
    }

    [Fact]
    public async Task MixedProvidersPublishOnlyPreparedFiles()
    {
        var run = await PrepareAsync();
        var generic = new RepoArtifactConfig("generic", "Bundle",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"target":{"destination":"published"}}""")!);
        var config = _config with { Artifacts = [_config.Artifacts![0], generic] };
        Directory.CreateDirectory(Path.Join(_root, "artifacts", "generic"));
        await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "generic", "Bundle-1.2.3.zip"), "prepared bundle");
        await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "generic", "Bundle-0.9.0.zip"), "unrelated old bundle");
        run = run with
        {
            ConfigHash = CanonicalConfigHasher.Compute(config),
            Artifacts = [run.Artifacts[0], new ArtifactManifestEntry("generic", "Bundle", true, false, [])
            {
                HandoffOutputs = (await new GenericArtifactProvider().PrepareAsync(
                    new ArtifactConfig("generic", "Bundle", generic.Settings!), Context(), CancellationToken.None)).Outputs,
            }],
        };
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run));
        await VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), Providers(), CancellationToken.None);
        var handoff = await VerifiedArtifactHandoff.VerifyAsync("handoff.json", config, Context(), Providers(), CancellationToken.None);
        var prepared = Assert.Single(handoff.Artifacts, artifact => artifact.Type == "generic");
        var provider = new GenericArtifactProvider();
        var result = await provider.PushPreparedAsync(
            new ArtifactConfig("generic", "Bundle", generic.Settings!), prepared, Context(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("prepared bundle", await File.ReadAllTextAsync(Path.Join(_root, "published", "Bundle-1.2.3.zip")));
        Assert.False(File.Exists(Path.Join(_root, "published", "Bundle-0.9.0.zip")));
    }

    [Fact]
    public async Task UnsupportedProviderFailsBeforePublication()
    {
        var run = await PrepareAsync();
        var config = _config with { Artifacts = [new RepoArtifactConfig("docker", "Example")] };
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run with
        {
            ConfigHash = CanonicalConfigHasher.Compute(config),
            Artifacts = [run.Artifacts[0] with { Type = "docker" }],
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), Providers(), CancellationToken.None));
        Assert.Contains("does not support verified handoff", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NuGetProviderIncludesExactSymbolPackage()
    {
        await PrepareAsync();
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"output":"artifacts/packages","symbols":{"enabled":true}}""")!;
        await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "packages", "Example.1.2.3.snupkg"), "verified symbols");
        var provider = new NuGetArtifactProvider();
        var artifact = new ArtifactConfig("nuget", "Example", settings);
        var prepared = await provider.PrepareAsync(artifact, Context(), CancellationToken.None);
        Assert.Equal(2, prepared.Outputs.Count);
        var build = new ArtifactManifestEntry("nuget", "Example", true, false, []);
        await provider.ValidatePreparedAsync(artifact, prepared, build, Context(), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "packages", "Example.1.2.3.snupkg"), "changed symbols");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ValidatePreparedAsync(artifact, prepared, build, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task ProviderCanValidateImmutableRemoteReferenceWithoutLocalFiles()
    {
        var run = await PrepareAsync();
        var config = _config with { Artifacts = [new RepoArtifactConfig("reference-test", "Example")] };
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run with
        {
            ConfigHash = CanonicalConfigHasher.Compute(config),
            Artifacts = [run.Artifacts[0] with
            {
                Type = "reference-test",
                HandoffOutputs = [new PreparedArtifactOutput("registry-reference", "registry/Example@sha256:immutable", "immutable")],
            }],
        }));
        var registry = new ArtifactProviderRegistry();
        registry.Register("reference-test", new ReferenceProvider());
        await VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), registry, CancellationToken.None);
        var handoff = await VerifiedArtifactHandoff.VerifyAsync("handoff.json", config, Context(), registry, CancellationToken.None);
        Assert.Equal("registry-reference", Assert.Single(Assert.Single(handoff.Artifacts).Outputs).Kind);
    }

    [Fact]
    public async Task VerificationOnlyProviderSealsButCannotPublish()
    {
        var run = await PrepareAsync();
        var config = _config with { Artifacts = [new RepoArtifactConfig("reference-test", "Example")] };
        var output = new PreparedArtifactOutput("registry-reference", "registry/Example@sha256:immutable", "immutable");
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run with
        {
            ConfigHash = CanonicalConfigHasher.Compute(config),
            Artifacts = [run.Artifacts[0] with { Type = "reference-test", HandoffOutputs = [output] }],
        }));
        var providers = new ArtifactProviderRegistry();
        providers.Register("reference-test", new VerificationOnlyProvider());
        await VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), providers, CancellationToken.None);
        await VerifiedArtifactHandoff.VerifyAsync("handoff.json", config, Context(), providers, CancellationToken.None);

        var builtins = new BuiltinRegistry();
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(), VersionProviderRegistry.CreateDefault(), providers);
        var commands = new CommandRegistry();
        loader.LoadInto(commands, config, _root, new DefaultCommandExecutor(commands));
        Assert.True(builtins.TryResolve("builtin:push-artifact-handoff", out var push));
        var result = await push!(new StepDefinition("push", null, "builtin:push-artifact-handoff", null, null)
        {
            With = new Dictionary<string, string> { ["path"] = "handoff.json" },
        }, Context() with { Options = new Dictionary<string, string?> { ["confirm"] = "true" } }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("does not support prepared publication", result.Outputs["error"]!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing-evidence")]
    [InlineData("extra-output")]
    public async Task SealRejectsOutputNotBoundToBuild(string change)
    {
        var run = await PrepareAsync();
        if (change == "changed")
        {
            await File.WriteAllTextAsync(Path.Join(_root, "artifacts", "packages", "Example.1.2.3.nupkg"), "substituted");
        }
        else
        {
            var outputs = change == "missing-evidence" ? []
                : run.Artifacts[0].HandoffOutputs!.Append(new PreparedArtifactOutput("file", "other", "hash")).ToArray();
            await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run with
            {
                Artifacts = [run.Artifacts[0] with { HandoffOutputs = outputs }],
            }));
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(SealAsync);
        Assert.Contains("build evidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacySingleFileBuildHashRemainsVerifiable()
    {
        var run = await PrepareAsync();
        var output = Assert.Single(run.Artifacts[0].HandoffOutputs!);
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run with
        {
            RepoRoot = _root,
            Artifacts = [run.Artifacts[0] with
            {
                HandoffOutputs = null,
                Location = Path.Join(_root, "artifacts", "packages", "Example.1.2.3.nupkg"),
                ContentSha256 = output.Identity,
            }],
        }));
        await SealAsync();
        await VerifiedArtifactHandoff.VerifyAsync("handoff.json", _config, Context(), Providers(), CancellationToken.None);
    }

    [Fact]
    public async Task GenericBuildEvidenceRejectsSubstitutionBeforeSeal()
    {
        Directory.CreateDirectory(Path.Join(_root, "input"));
        await File.WriteAllTextAsync(Path.Join(_root, "input", "source.txt"), "source");
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"target":{"source":"input"}}""")!;
        var config = _config with { Artifacts = [new RepoArtifactConfig("generic", "Bundle", settings)] };
        var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), Providers());
        var built = await loader.BuildArtifactsAsync("build", config, Context(), static _ => true,
            "built", "empty", CancellationToken.None);
        Assert.True(built.Success);
        var entry = Assert.Single(Assert.IsType<List<ArtifactManifestEntry>>(built.Outputs["__artifacts"]));
        Assert.Single(entry.HandoffOutputs!);
        await File.WriteAllTextAsync(entry.Location!, "substituted archive");
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(new RunManifest
        {
            Success = true, CommandExecuted = "release", CommitSha = "commit", Version = _version,
            IsCi = true, CiBuildId = "run", ConfigHash = CanonicalConfigHasher.Compute(config),
            Steps = [new StepManifestEntry("build", true, 0, 1)],
            Artifacts = [entry],
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), Providers(), CancellationToken.None));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("commit")]
    [InlineData("config")]
    [InlineData("version")]
    [InlineData("version-commit")]
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
            case "version-commit":
                context = context with { Version = _version with { CommitSha = "different" } };
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
                VerifiedArtifactHandoff.VerifyAsync("handoff.json", config, context, Providers(), CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                VerifiedArtifactHandoff.VerifyAsync("handoff.json", config, context, Providers(), CancellationToken.None));
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
        var handoff = JsonSerializer.Deserialize<ArtifactHandoff>(await File.ReadAllTextAsync(handoffPath))!;
        await File.WriteAllTextAsync(handoffPath, JsonSerializer.Serialize(handoff with
        {
            Artifacts = [handoff.Artifacts[0] with
            {
                Outputs = [handoff.Artifacts[0].Outputs[0] with { Reference = path }],
            }],
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedArtifactHandoff.VerifyAsync("handoff.json", _config, Context(), Providers(), CancellationToken.None));
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
            VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, Context(), Providers(), CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public async Task NestedReleaseCanBeSealedOnlyAfterSuccessfulCapturedCompletion(
        bool capture, bool failRelease, bool samePaths, bool expectedSuccess)
    {
        Directory.CreateDirectory(_root);
        await GitAsync("init", "--quiet");
        await GitAsync("-c", "user.name=Rexo test", "-c", "user.email=test@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--allow-empty", "--quiet", "-m", "test");
        Directory.CreateDirectory(Path.Join(_root, "input"));
        await File.WriteAllTextAsync(Path.Join(_root, "input", "payload.txt"), "payload");
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"target":{"source":"input"}}""")!;
        var releaseSteps = new List<RepoStepConfig>
        {
            new("version", Uses: "builtin:resolve-version"),
            new("build", Uses: "builtin:build-artifacts"),
        };
        if (failRelease)
        {
            releaseSteps.Add(new RepoStepConfig("fail", Uses: "builtin:missing"));
        }

        var config = new RepoConfig("test", null, null)
        {
            Versioning = new RepoVersioningConfig("counted", "1.2.3"),
            Artifacts = [new RepoArtifactConfig("generic", "Bundle", settings)],
            Commands = new Dictionary<string, RepoCommandConfig>
            {
                ["release"] = new(null, [], releaseSteps),
                ["prepare"] = new(null, [],
                [
                    new("version-first", Uses: "builtin:resolve-version"),
                    new("release", Command: "release", ContinueOnError: true,
                        With: new Dictionary<string, string> { ["captureRunManifest"] = capture.ToString() }),
                    new("seal", Uses: "builtin:seal-artifact-handoff", With: new Dictionary<string, string>
                    {
                        ["fromStep"] = "release",
                        ["runManifest"] = "artifacts/release.json",
                        ["path"] = samePaths ? "artifacts/release.json" : "artifacts/handoff.json",
                    }),
                    new("verify", Uses: "builtin:verify-artifact-handoff",
                        With: new Dictionary<string, string> { ["path"] = "artifacts/handoff.json" }),
                ]),
            },
        };
        var counter = new CountingVersionProvider();
        var versions = VersionProviderRegistry.CreateDefault();
        versions.Register("counted", counter);
        var builtins = new BuiltinRegistry();
        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(), versions, Providers());
        loader.LoadInto(registry, config, _root, executor);
        var invocation = new CommandInvocation(new Dictionary<string, string>(),
            new Dictionary<string, string?>(), false, null, _root);

        var result = await executor.ExecuteAsync("prepare", invocation, CancellationToken.None);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(1, counter.Calls);
        Assert.Equal(expectedSuccess, File.Exists(Path.Join(_root, "artifacts", "release.json")));
        Assert.Equal(expectedSuccess, File.Exists(Path.Join(_root, "artifacts", "handoff.json")));
        if (expectedSuccess)
        {
            var manifest = JsonSerializer.Deserialize<RunManifest>(
                await File.ReadAllTextAsync(Path.Join(_root, "artifacts", "release.json")))!;
            Assert.True(manifest.Success);
            Assert.Equal("release", manifest.CommandExecuted);
            Assert.Equal(manifest.CommitSha, manifest.Version!.CommitSha);
            Assert.Single(manifest.Artifacts);
            Assert.DoesNotContain(manifest.Steps, step => step.StepId == "seal");
            Assert.Equal("verify", result.Steps[^1].StepId);
        }
    }

    private async Task GitAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }

    private sealed class CountingVersionProvider : IVersionProvider
    {
        private readonly FixedVersionProvider _inner = new();
        public int Calls { get; private set; }

        public Task<VersionResult> ResolveAsync(
            VersioningConfig config, ExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return _inner.ResolveAsync(config, context, cancellationToken);
        }
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
        Assert.True(builtins.TryResolve("builtin:push-artifact-handoff", out var push));
        Assert.NotNull(push);
        var step = new StepDefinition("publish", null, "builtin:push-artifact-handoff", null, null)
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
            var result = await push!(step, context, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(6, result.ExitCode);
            Assert.True(result.Outputs.ContainsKey("error"));
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

    private static ArtifactProviderRegistry Providers()
    {
        var providers = new ArtifactProviderRegistry();
        providers.Register("nuget", new NuGetArtifactProvider());
        providers.Register("generic", new GenericArtifactProvider());
        return providers;
    }

    private Task SealAsync() => VerifiedArtifactHandoff.SealAsync(
        "run.json", "handoff.json", _config, Context(), Providers(), CancellationToken.None);

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
            Artifacts = [new ArtifactManifestEntry("nuget", "Example", true, false, [])
            {
                HandoffOutputs = (await new NuGetArtifactProvider().PrepareAsync(
                    new ArtifactConfig("nuget", "Example", new Dictionary<string, JsonElement>()),
                    Context(), CancellationToken.None)).Outputs,
            }],
        };
        await File.WriteAllTextAsync(Path.Join(_root, "run.json"), JsonSerializer.Serialize(run));
        return run;
    }

    private sealed class PushOnlyProvider : IArtifactProvider, IPreparedArtifactPublisher
    {
        public string Type => "nuget";
        public int PushCalls { get; private set; }

        public Task<ArtifactBuildResult> BuildAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Publication must not build.");

        public Task<ArtifactTagResult> TagAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Publication must not retag package bytes.");

        public Task<ArtifactPushResult> PushAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Prepared publication must not discover outputs again.");

        public Task<PreparedArtifact> PrepareAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            new NuGetArtifactProvider().PrepareAsync(artifact, context, cancellationToken);

        public Task ValidatePreparedAsync(ArtifactConfig artifact, PreparedArtifact prepared, ArtifactManifestEntry build, ExecutionContext context, CancellationToken cancellationToken) =>
            new NuGetArtifactProvider().ValidatePreparedAsync(artifact, prepared, build, context, cancellationToken);

        public Task<ArtifactPushResult> PushPreparedAsync(ArtifactConfig artifact, PreparedArtifact prepared, ExecutionContext context, CancellationToken cancellationToken)
        {
            Assert.Equal("1.2.3", context.Version?.NuGetVersion);
            PushCalls++;
            return Task.FromResult(new ArtifactPushResult(artifact.Name, true, ["feed/Example"]));
        }

    }

    private sealed class ReferenceProvider : IArtifactProvider, IPreparedArtifactPublisher
    {
        public string Type => "reference-test";

        public Task<PreparedArtifact> PrepareAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new PreparedArtifact(Type, artifact.Name,
                [new PreparedArtifactOutput("registry-reference", "registry/Example@sha256:immutable", "immutable")]));

        public Task ValidatePreparedAsync(ArtifactConfig artifact, PreparedArtifact prepared, ArtifactManifestEntry build, ExecutionContext context, CancellationToken cancellationToken)
        {
            Assert.Equal(Type, prepared.Type);
            Assert.Equal(artifact.Name, prepared.Name);
            Assert.Equal("registry/Example@sha256:immutable", Assert.Single(prepared.Outputs).Reference);
            Assert.Equal("immutable", Assert.Single(prepared.Outputs).Identity);
            return Task.CompletedTask;
        }

        public Task<ArtifactPushResult> PushPreparedAsync(ArtifactConfig artifact, PreparedArtifact prepared, ExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ArtifactPushResult(artifact.Name, true, [prepared.Outputs[0].Reference]));

        public Task<ArtifactBuildResult> BuildAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No build in handoff.");

        public Task<ArtifactTagResult> TagAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No retag in handoff.");

        public Task<ArtifactPushResult> PushAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Use prepared publication.");
    }

    private sealed class VerificationOnlyProvider : IArtifactProvider, IPreparedArtifactProvider
    {
        private readonly ReferenceProvider _inner = new();
        public string Type => _inner.Type;
        public Task<PreparedArtifact> PrepareAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            _inner.PrepareAsync(artifact, context, cancellationToken);
        public Task ValidatePreparedAsync(ArtifactConfig artifact, PreparedArtifact prepared, ArtifactManifestEntry build, ExecutionContext context, CancellationToken cancellationToken) =>
            _inner.ValidatePreparedAsync(artifact, prepared, build, context, cancellationToken);
        public Task<ArtifactBuildResult> BuildAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No build in handoff.");
        public Task<ArtifactTagResult> TagAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No retag in handoff.");
        public Task<ArtifactPushResult> PushAsync(ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No ordinary push in verified publication.");
    }
}
