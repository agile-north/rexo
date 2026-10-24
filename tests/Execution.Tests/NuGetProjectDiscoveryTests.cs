namespace Rexo.Execution.Tests;

using System.Diagnostics;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Artifacts.NuGet;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class NuGetProjectDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"rexo discovery {Guid.NewGuid():N}");
    private static readonly string[] ExpectedMatches = ["RootContracts.csproj", "src/nested/BContracts.csproj"];
    private static readonly string[] DiscoveryIncludes = ["**/*Contracts.csproj"];
    private static readonly string[] AllProjects = ["**/*.csproj"];
    private static readonly string[] TestExcludes = ["samples/**"];
    private static readonly VersionResult Version = new("1.2.3", 1, 2, 3, null, "commit", "commit", false, true)
    {
        NuGetVersion = "1.2.3",
    };

    public NuGetProjectDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void MatchingIsRecursiveDeterministicDeduplicatedAndPrunesGeneratedDirectories()
    {
        Touch("RootContracts.csproj");
        Touch("src/nested/BContracts.csproj");
        Touch("samples/AContracts.csproj");
        Touch("src/obj/GeneratedContracts.csproj");
        Touch("bin/BinaryContracts.csproj");
        Touch(".git/HiddenContracts.csproj");
        Touch("src/Other.csproj");
        Touch("src/lowercontracts.csproj");
        var discovery = new RepoArtifactDiscoveryConfig(
            ["**/*Contracts.csproj", "src/**/*.csproj"], ["samples/**", "src/Other.csproj", "src/lowercontracts.csproj"]);

        Assert.Equal(ExpectedMatches, NuGetProjectDiscovery.MatchProjects(_root, discovery, CancellationToken.None));
    }

    [Fact]
    public void QuestionMarkMatchesExactlyOneCharacterAndGlobstarMatchesZeroDirectories()
    {
        Touch("AContracts.csproj");
        Touch("ABContracts.csproj");
        var matches = NuGetProjectDiscovery.MatchProjects(_root,
            new RepoArtifactDiscoveryConfig(["**/?Contracts.csproj"]), CancellationToken.None);
        Assert.Equal("AContracts.csproj", Assert.Single(matches));
    }

    [Theory]
    [InlineData("../*.csproj")]
    [InlineData("/tmp/*.csproj")]
    [InlineData("C:/src/*.csproj")]
    [InlineData("src\\*.csproj")]
    [InlineData("src/**Contracts.csproj")]
    [InlineData("src//*.csproj")]
    [InlineData("./*.csproj")]
    [InlineData("src/[AB].csproj")]
    [InlineData("!tests/**")]
    public void InvalidPatternsFailClearly(string pattern)
    {
        Assert.Throws<InvalidOperationException>(() => NuGetProjectDiscovery.MatchProjects(_root,
            new RepoArtifactDiscoveryConfig([pattern]), CancellationToken.None));
    }

    [Fact]
    public void MatchingNonProjectFilesFailsInsteadOfSilentlyIgnoringThem()
    {
        Touch("readme.md");
        var error = Assert.Throws<InvalidOperationException>(() => NuGetProjectDiscovery.MatchProjects(_root,
            new RepoArtifactDiscoveryConfig(["**/*"]), CancellationToken.None));
        Assert.Contains("non-project", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluationUsesImportedConditionalPackageIdAndReportsNonPackableProjects()
    {
        await WriteAsync("Directory.Build.props", """
            <Project><PropertyGroup>
              <PackageId Condition="'$(Configuration)' == 'Release' and '$(Version)' == '1.2.3'">Acme.$(MSBuildProjectName)</PackageId>
            </PropertyGroup></Project>
            """);
        await ProjectAsync("src/ActualContracts.csproj");
        await ProjectAsync("tests/TestContracts.csproj", "<IsPackable>false</IsPackable>");
        var config = Config();
        var context = Context();
        var result = await NuGetProjectDiscovery.ResolveAsync(config, context, static _ => true, CancellationToken.None);
        var artifact = Assert.Single(result);
        Assert.Equal("Acme.ActualContracts", artifact.Name);
        Assert.Equal("src/ActualContracts.csproj", artifact.SourceProject);
        Assert.Equal("src/ActualContracts.csproj", artifact.Settings!["project"].GetString());

        var plan = await ConfigCommandLoader.PlanArtifactsAsync("plan", config, context, Providers(), false,
            static _ => true, "planned", "empty", CancellationToken.None);
        Assert.True(plan.Success);
        Assert.Equal("tests/TestContracts.csproj", Assert.Single(Assert.IsType<string[]>(plan.Outputs["skippedProjects"])));
        using var json = JsonDocument.Parse(Assert.IsType<string>(plan.Outputs["plan"]));
        var planned = Assert.Single(json.RootElement.GetProperty("Artifacts").EnumerateArray());
        Assert.Equal("Acme.ActualContracts", planned.GetProperty("Name").GetString());
        Assert.Contains("src/ActualContracts.csproj", plan.Outputs["plan"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryIsReusedWithinInvocationButFreshInvocationsDiscoverNewProjects()
    {
        await ProjectAsync("AContracts.csproj");
        var config = Config();
        var context = Context();
        Assert.Single(await NuGetProjectDiscovery.ResolveAsync(config, context, static _ => true, CancellationToken.None));
        await ProjectAsync("BContracts.csproj");
        Assert.Single(await NuGetProjectDiscovery.ResolveAsync(config, context.WithVersion(Version), static _ => true, CancellationToken.None));
        Assert.Equal(2, (await NuGetProjectDiscovery.ResolveAsync(config, Context(), static _ => true, CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrAllNonPackableDiscoveryFailsBeforeBuild(bool nonPackable)
    {
        if (nonPackable) await ProjectAsync("TestContracts.csproj", "<IsPackable>false</IsPackable>");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuGetProjectDiscovery.ResolveAsync(Config(), Context(), static _ => true, CancellationToken.None));
        Assert.Contains("no packable", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("literal")]
    [InlineData("declaration")]
    public async Task DuplicatePackageIdentitiesFailAcrossSelectedArtifacts(string duplicate)
    {
        await ProjectAsync("AContracts.csproj", "<PackageId>Same.Package</PackageId>");
        var config = Config();
        if (duplicate == "project")
            await ProjectAsync("BContracts.csproj", "<PackageId>same.package</PackageId>");
        else
            config = config with
            {
                Artifacts = [config.Artifacts![0], duplicate == "literal"
                    ? new RepoArtifactConfig("nuget", "same.package") : config.Artifacts[0]],
            };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuGetProjectDiscovery.ResolveAsync(config, Context(), static _ => true, CancellationToken.None));
        Assert.Contains("Duplicate NuGet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupsAreSelectedBeforeAnyProjectEvaluation()
    {
        var config = Config() with
        {
            Artifacts =
            [
                new RepoArtifactConfig("nuget", "Literal"),
                new RepoArtifactConfig("nuget") { Group = "contracts", Discover = new RepoArtifactDiscoveryConfig(AllProjects) },
            ],
        };
        Assert.Equal("Literal", Assert.Single(await NuGetProjectDiscovery.ResolveAsync(
            config, Context(), static _ => true, CancellationToken.None)).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NuGetProjectDiscovery.ResolveAsync(
            config, Context() with { Options = new Dictionary<string, string?> { ["all-artifact-groups"] = "true" } },
            static _ => true, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidProjectEvaluationFailsWithProjectDiagnostic()
    {
        await WriteAsync("BadContracts.csproj", "<Project><Import Project=\"missing.props\" /></Project>");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuGetProjectDiscovery.ResolveAsync(Config(), Context(), static _ => true, CancellationToken.None));
        Assert.Contains("BadContracts.csproj", error.Message, StringComparison.Ordinal);
        Assert.Contains("Restore/setup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableSdkFailsWithoutFallingBackToTheProjectFilename()
    {
        await WriteAsync("global.json", """{"sdk":{"version":"999.0.100","rollForward":"disable"}}""");
        await ProjectAsync("ActualContracts.csproj");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuGetProjectDiscovery.ResolveAsync(Config(), Context(), static _ => true, CancellationToken.None));
        Assert.Contains("failed to evaluate 'ActualContracts.csproj'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafeOutputOrWildcardSymbolsFailBeforeInvokingPack(bool wildcardSymbols)
    {
        var calls = 0;
        var provider = new NuGetArtifactProvider((_, _, _) =>
        {
            calls++;
            return Task.FromResult((0, string.Empty));
        });
        var artifact = new ArtifactConfig("nuget", "Example", new Dictionary<string, JsonElement>
        {
            ["output"] = JsonSerializer.SerializeToElement(wildcardSymbols ? "packages" : "../outside"),
            ["symbols"] = JsonSerializer.SerializeToElement(new { enabled = true, pattern = "packages/*.snupkg" }),
        }) { SourceProject = "Example.csproj" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.BuildAsync(artifact, Context(), CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Path.Join(_root, "packages")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiTargetingRequiresAnUnambiguousIdentity(bool ambiguous)
    {
        await ProjectAsync("MultiContracts.csproj", $$"""
            <TargetFramework></TargetFramework><TargetFrameworks>net10.0;net9.0</TargetFrameworks>
            <PackageId>Multi.Contracts</PackageId>
            {{(ambiguous ? "<PackageId Condition=\"'$(TargetFramework)' == 'net9.0'\">Different.Contracts</PackageId>" : "")}}
            """);
        if (ambiguous)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NuGetProjectDiscovery.ResolveAsync(Config(), Context(), static _ => true, CancellationToken.None));
            Assert.Contains("ambiguous", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("Multi.Contracts", Assert.Single(await NuGetProjectDiscovery.ResolveAsync(
                Config(), Context(), static _ => true, CancellationToken.None)).Name);
        }
    }

    [Fact]
    public async Task CancellationDoesNotReturnAnEmptySuccessfulInventory()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NuGetProjectDiscovery.ResolveAsync(Config(), Context(), static _ => true, cancellation.Token));
    }

    [Fact]
    public async Task DirectoryLinksAreNotFollowed()
    {
        await ProjectAsync("projects/AContracts.csproj");
        var link = Path.Join(_root, "linked");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{Path.Join(_root, "projects")}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            await process.WaitForExitAsync(CancellationToken.None);
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, Path.Join(_root, "projects"));
        }
        try
        {
            Assert.Equal("projects/AContracts.csproj", Assert.Single(NuGetProjectDiscovery.MatchProjects(
                _root, new RepoArtifactDiscoveryConfig(DiscoveryIncludes), CancellationToken.None)));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task RealPackUsesEvaluatedIdentityResolvedVersionAndConfiguredOutputRoot()
    {
        await WriteAsync("Directory.Build.props", """
            <Project><PropertyGroup>
              <PackageId Condition="'$(Configuration)' == 'Release'">Evaluated.Contracts</PackageId>
            </PropertyGroup></Project>
            """);
        await ProjectAsync("src/Some Contracts.csproj", "<PackageVersion>99.0.0</PackageVersion><IncludeSymbols>true</IncludeSymbols><SymbolPackageFormat>snupkg</SymbolPackageFormat>");
        await WriteAsync("src/Contract.cs", "namespace Example; public sealed class Contract { }");
        var config = Config(new Dictionary<string, JsonElement>
        {
            ["symbols"] = JsonSerializer.SerializeToElement(new { enabled = true }),
        }) with { Outputs = new RepoOutputsConfig { Root = "custom output" } };
        var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), Providers());
        var context = Context();

        var plan = await ConfigCommandLoader.PlanArtifactsAsync("plan", config, context, Providers(), false,
            static _ => true, "planned", "empty", CancellationToken.None);
        Assert.True(plan.Success);
        var planText = Assert.IsType<string>(plan.Outputs["plan"]);
        Assert.Contains("Evaluated.Contracts.1.2.3.nupkg", planText, StringComparison.Ordinal);
        Assert.DoesNotContain("Evaluated.Contracts.*", planText, StringComparison.Ordinal);

        var result = await loader.BuildArtifactsAsync("build", config, context,
            static _ => true, "built", "empty", CancellationToken.None);
        Assert.True(result.Success);
        var artifact = Assert.Single(Assert.IsType<List<ArtifactManifestEntry>>(result.Outputs["__artifacts"]));
        Assert.Equal("Evaluated.Contracts", artifact.Name);
        Assert.Equal(Path.Join(_root, "custom output", "packages", "Evaluated.Contracts.1.2.3.nupkg"), artifact.Location);
        Assert.NotNull(artifact.ContentSha256);
        Assert.True(File.Exists(Path.Join(_root, "custom output", "packages", "Evaluated.Contracts.1.2.3.snupkg")));
        Assert.False(File.Exists(Path.Join(_root, "custom output", "packages", "Evaluated.Contracts.99.0.0.nupkg")));
    }

    [Fact]
    public async Task LifecycleBuildsEachProjectAndPublishesOnlyExactPackagesAndSymbols()
    {
        await ProjectAsync("src/One Contracts.csproj", "<PackageId>One.Contracts</PackageId>");
        await ProjectAsync("src/TwoContracts.csproj", "<PackageId>Two.Contracts</PackageId>");
        var config = Config(new Dictionary<string, JsonElement>
        {
            ["output"] = JsonSerializer.SerializeToElement("custom packages"),
            ["symbols"] = JsonSerializer.SerializeToElement(new { enabled = true }),
        });
        var context = Context() with
        {
            Options = new Dictionary<string, string?> { ["confirm"] = "true" },
            MappedSecretEnvironment = new Dictionary<string, string> { ["NUGET_API_KEY"] = "test-token" },
        };
        var commands = new List<string>();
        var provider = new NuGetArtifactProvider(async (arguments, _, _) =>
        {
            commands.Add(arguments);
            if (arguments.StartsWith("pack ", StringComparison.Ordinal))
            {
                var name = arguments.Contains("One Contracts", StringComparison.Ordinal) ? "One.Contracts" : "Two.Contracts";
                await WriteAsync($"custom packages/{name}.1.2.3.nupkg", "package");
                await WriteAsync($"custom packages/{name}.1.2.3.snupkg", "symbols");
            }
            return (0, string.Empty);
        });
        var providers = Providers(provider);
        var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), providers);
        var build = await loader.BuildArtifactsAsync("build", config, context, static _ => true, "built", "empty", CancellationToken.None);
        Assert.True(build.Success);
        Assert.Equal(2, commands.Count);
        Assert.Contains("pack \"src/One Contracts.csproj\"", commands[0], StringComparison.Ordinal);
        Assert.All(Assert.IsType<List<ArtifactManifestEntry>>(build.Outputs["__artifacts"]), artifact =>
        {
            Assert.NotNull(artifact.SourceProject);
            Assert.NotNull(artifact.ContentSha256);
            Assert.EndsWith(".nupkg", artifact.Location, StringComparison.Ordinal);
        });
        await WriteAsync("custom packages/Unrelated.1.2.3.nupkg", "stale");
        await WriteAsync("custom packages/One.Contracts.0.1.0.nupkg", "stale");
        Assert.True((await loader.TagArtifactsAsync("tag", config, context, static _ => true, "tagged", "empty", CancellationToken.None)).Success);
        var push = await loader.PushArtifactsAsync("push", config, _root, "custom packages", false, context,
            static _ => true, "pushed", "empty", CancellationToken.None);
        Assert.True(push.Success);
        Assert.Equal(6, commands.Count);
        Assert.All(commands.Skip(2), command =>
        {
            Assert.DoesNotContain('*', command);
            Assert.DoesNotContain("Unrelated", command, StringComparison.Ordinal);
            Assert.DoesNotContain("0.1.0", command, StringComparison.Ordinal);
        });
        Assert.All(Assert.IsType<List<ArtifactManifestEntry>>(push.Outputs["__artifacts"]), artifact => Assert.True(artifact.Pushed));
    }

    [Fact]
    public async Task DryRunEvaluatesButDoesNotPackOrCreateOutputDirectories()
    {
        await ProjectAsync("DryContracts.csproj");
        var commands = new List<string>();
        var provider = new NuGetArtifactProvider((arguments, _, _) =>
        {
            commands.Add(arguments);
            return Task.FromResult((0, string.Empty));
        });
        var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), Providers(provider));
        var result = await loader.BuildArtifactsAsync("build", Config(), Context() with { IsDryRun = true },
            static _ => true, "built", "empty", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Empty(commands);
        Assert.False(Directory.Exists(Path.Join(_root, "artifacts")));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("identity")]
    [InlineData("association")]
    [InlineData("bytes")]
    public async Task HandoffRejectsInventoryProjectAndContentDrift(string change)
    {
        await ProjectAsync("OneContracts.csproj", "<PackageId>One.Contracts</PackageId>");
        await WriteAsync("artifacts/packages/One.Contracts.1.2.3.nupkg", "package");
        var config = Config();
        var context = Context();
        var run = new RunManifest
        {
            Success = true, CommandExecuted = "release", CommitSha = "commit", Version = Version,
            ConfigHash = CanonicalConfigHasher.Compute(config),
            Steps = [new StepManifestEntry("build", true, 0, 1)],
            Artifacts = [new ArtifactManifestEntry("nuget", "One.Contracts", true, false, []) { SourceProject = "OneContracts.csproj" }],
        };
        await WriteAsync("run.json", JsonSerializer.Serialize(run));
        await VerifiedArtifactHandoff.SealAsync("run.json", "handoff.json", config, context, Providers(), CancellationToken.None);
        Assert.Equal("OneContracts.csproj", Assert.Single((await VerifiedArtifactHandoff.VerifyAsync(
            "handoff.json", config, Context(), Providers(), CancellationToken.None)).Artifacts).SourceProject);

        switch (change)
        {
            case "add": await ProjectAsync("TwoContracts.csproj"); break;
            case "remove": File.Delete(Path.Join(_root, "OneContracts.csproj")); break;
            case "identity": await ProjectAsync("OneContracts.csproj", "<PackageId>Changed.Contracts</PackageId>"); break;
            case "association": File.Move(Path.Join(_root, "OneContracts.csproj"), Path.Join(_root, "RenamedContracts.csproj")); break;
            case "bytes": await WriteAsync("artifacts/packages/One.Contracts.1.2.3.nupkg", "tampered"); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifiedArtifactHandoff.VerifyAsync(
            "handoff.json", config, Context(), Providers(), CancellationToken.None));
    }

    private ExecutionContext Context() => ExecutionContext.Empty(_root) with { Version = Version, CommitSha = "commit" };

    private static RepoConfig Config(Dictionary<string, JsonElement>? settings = null) => new("discovery", null, null)
    {
        Artifacts = [new RepoArtifactConfig("nuget", Settings: settings)
        {
            Discover = new RepoArtifactDiscoveryConfig(DiscoveryIncludes, TestExcludes),
        }],
    };

    private static ArtifactProviderRegistry Providers(NuGetArtifactProvider? provider = null)
    {
        var registry = new ArtifactProviderRegistry();
        registry.Register("nuget", provider ?? new NuGetArtifactProvider());
        return registry;
    }

    private Task ProjectAsync(string path, string properties = "") => WriteAsync(path, $$"""
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>{{properties}}</PropertyGroup></Project>
        """);

    private async Task WriteAsync(string path, string content)
    {
        var full = Path.Join(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private void Touch(string path) => WriteAsync(path, string.Empty).GetAwaiter().GetResult();
}
