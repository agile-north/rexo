namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Policies;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class GitHubReleaseTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherCommit = "ffffffffffffffffffffffffffffffffffffffff";
    private const string Token = "test-github-release-token";
    private static readonly RepoConfig SourceConfig = new("example", [], []);

    private static ExecutionContext Context(RepoConfig? config = null, string vars = "{}")
    {
        using var json = JsonDocument.Parse(vars);
        var effective = (config ?? SourceConfig) with
        {
            Vars = new Dictionary<string, JsonElement> { ["githubRelease"] = json.RootElement.Clone() },
        };
        return ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Branch = "main",
            CommitSha = Commit,
            RemoteUrl = "git@github.com:owner/example.git",
            IsCleanWorkingTree = true,
            CommandCallStack = ["release"],
            Version = new VersionResult("1.2.3", 1, 2, 3, null, Commit, Commit[..7], false, true),
            Options = new Dictionary<string, string?> { ["push"] = "true" },
            MappedSecretEnvironment = new Dictionary<string, string> { ["GH_TOKEN"] = Token },
            ResolvedVars = ConfigCommandLoader.BuildVarsContext(effective),
        };
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-push")]
    [InlineData("wrong-command")]
    [InlineData("dry-run")]
    [InlineData("runtime-dry-run")]
    [InlineData("push-dry-run")]
    [InlineData("failed")]
    [InlineData("no-version")]
    [InlineData("source-disabled")]
    [InlineData("push-disabled")]
    [InlineData("pull-request")]
    [InlineData("dirty")]
    [InlineData("branch")]
    [InlineData("artifact-not-pushed")]
    public async Task IneligibleRunsSkipBeforeInvokingTools(string scenario)
    {
        var config = SourceConfig;
        var ctx = Context();
        switch (scenario)
        {
            case "disabled": ctx = Context(vars: """{"enabled":false}"""); break;
            case "no-push": ctx = ctx with { Options = new Dictionary<string, string?>() }; break;
            case "wrong-command": ctx = ctx with { CommandCallStack = ["push"] }; break;
            case "dry-run": ctx = ctx with { IsDryRun = true }; break;
            case "runtime-dry-run": config = config with { Runtime = new(DryRun: true) }; break;
            case "push-dry-run": config = config with { Runtime = new(Push: new(DryRun: true)) }; break;
            case "failed": ctx = ctx.WithStep(new("post-release", false, 1, TimeSpan.Zero, new Dictionary<string, object?>())); break;
            case "no-version": ctx = ctx with { Version = null }; break;
            case "source-disabled": ctx = Context(vars: """{"allowSourceOnly":false}"""); break;
            case "push-disabled": config = config with { Runtime = new(Push: new(Enabled: false)) }; break;
            case "pull-request":
                config = config with { Runtime = new(Push: new(NoPushInPullRequest: true)) };
                ctx = ctx with { IsPullRequest = true };
                break;
            case "dirty":
                config = config with { Runtime = new(Push: new(RequireCleanWorkingTree: true)) };
                ctx = ctx with { IsCleanWorkingTree = false };
                break;
            case "branch": config = config with { Runtime = new(Push: new(Branches: ["production"])) }; break;
            case "artifact-not-pushed": config = config with { Artifacts = [new("generic", "package")] }; break;
        }

        var fake = new FakeCli();
        var result = await GitHubReleasePublisher.ExecuteAsync(config, ctx, CancellationToken.None, fake.RunAsync);

        Assert.Equal(true, result["skipped"]);
        Assert.NotEmpty(Assert.IsType<string>(result["skipReason"]));
        Assert.Empty(fake.Commands);
    }

    [Theory]
    [InlineData("all", false, false)]
    [InlineData("all", true, true)]
    [InlineData("any", false, true)]
    [InlineData("none", false, true)]
    public async Task ArtifactGateUsesSelectedPublicationAndCanBeConfigured(string gate, bool bothPushed, bool eligible)
    {
        var config = SourceConfig with
        {
            Artifacts = [new("docker", "api"), new("generic", "package"), new("generic", "other") { Group = "other" }],
        };
        var ctx = Context(config, $$"""{"artifactGate":"{{gate}}"}""").WithStep(new("push", true, 0, TimeSpan.Zero,
            new Dictionary<string, object?>
            {
                ["__artifacts"] = new List<ArtifactManifestEntry>
                {
                    new("docker", "api", true, true, ["api:v1"]),
                    new("generic", "package", true, bothPushed, []),
                },
                ["__pushDecisions"] = new List<PushDecision>
                {
                    new("api", true, "published"), new("package", bothPushed, "decision"),
                },
            }));
        var fake = new FakeCli();

        var result = await GitHubReleasePublisher.ExecuteAsync(config, ctx, CancellationToken.None, fake.RunAsync);

        Assert.Equal(eligible, fake.Commands.Count > 0);
        Assert.Equal(eligible ? "created" : null, result.GetValueOrDefault("status"));
    }

    [Fact]
    public async Task DefaultContainerCreatesExactCommitTagAndPublishedRelease()
    {
        var fake = new FakeCli();
        var result = await GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), CancellationToken.None, fake.RunAsync);

        Assert.Equal("created", result["status"]);
        Assert.Equal("v1.2.3", result["tag"]);
        Assert.Equal(Commit, result["commitSha"]);
        Assert.All(fake.Commands, command =>
        {
            Assert.Equal("docker", command.FileName);
            Assert.Contains(GitHubReleaseOptions.DefaultImage, command.Arguments);
            Assert.DoesNotContain(Token, command.Arguments);
            Assert.Equal(Token, command.Environment["GH_TOKEN"]);
            Assert.Contains("--entrypoint", command.Arguments);
            Assert.DoesNotContain("--volume", command.Arguments);
        });
        var tagCreate = Assert.Single(fake.GhArguments, args => args.Contains("POST"));
        Assert.Contains($"sha={Commit}", tagCreate);
        Assert.Contains("ref=refs/tags/v1.2.3", tagCreate);
        var release = Assert.Single(fake.GhArguments, args => args[0] == "release");
        Assert.Contains("--verify-tag", release);
        Assert.Contains("--target", release);
        Assert.Contains(Commit, release);
        Assert.Contains("--generate-notes", release);
        Assert.DoesNotContain("--draft", release);
        Assert.DoesNotContain("--prerelease", release);
        Assert.True(fake.GhArguments.FindIndex(args => args.Contains("POST")) <
            fake.GhArguments.FindIndex(args => args[0] == "release"));
    }

    [Theory]
    [InlineData("default", "v1.2.3", "1.2.3")]
    [InlineData("contracts", "contracts/v1.2.3", "contracts 1.2.3")]
    [InlineData("all", "v1.2.3", "1.2.3")]
    public async Task NamingAndNativeExecutionRespectArtifactGroup(string group, string tag, string title)
    {
        var ctx = Context(vars: """{"execution":"native"}""");
        if (group != "default")
        {
            ctx = ctx with { Options = new Dictionary<string, string?>
            {
                ["push"] = "true",
                [group == "all" ? "all-artifact-groups" : "artifact-group"] = group == "all" ? "true" : group,
            } };
        }
        var fake = new FakeCli();
        var result = await GitHubReleasePublisher.ExecuteAsync(SourceConfig, ctx, CancellationToken.None, fake.RunAsync);

        Assert.Equal(tag, result["tag"]);
        Assert.All(fake.Commands, command => Assert.Equal("gh", command.FileName));
        var release = Assert.Single(fake.GhArguments, args => args[0] == "release");
        Assert.Contains(title, release);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingMatchingLightweightOrAnnotatedTagAndReleaseIsNotModified(bool annotated)
    {
        var fake = new FakeCli { TagExists = true, ReleaseExists = true, Annotated = annotated };
        var result = await GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), CancellationToken.None, fake.RunAsync);

        Assert.Equal("existing", result["status"]);
        Assert.DoesNotContain(fake.GhArguments, args => args.Contains("POST") || args[0] == "release");
        Assert.Equal(annotated, fake.GhArguments.Any(args => args.Any(arg => arg.Contains("/git/tags/", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftReleaseCreationAndRerunLeaveExistingDraftUntouched(bool alreadyExists)
    {
        var fake = new FakeCli { TagExists = alreadyExists, DraftExists = alreadyExists };
        var ctx = Context(vars: """{"draft":true}""");
        var first = await GitHubReleasePublisher.ExecuteAsync(SourceConfig, ctx, CancellationToken.None, fake.RunAsync);
        Assert.Equal(alreadyExists ? "existing" : "created", first["status"]);
        var creations = fake.GhArguments.Count(args => args[0] == "release");

        var second = await GitHubReleasePublisher.ExecuteAsync(SourceConfig, ctx, CancellationToken.None, fake.RunAsync);
        Assert.Equal("existing", second["status"]);
        Assert.Equal(creations, fake.GhArguments.Count(args => args[0] == "release"));
    }

    [Fact]
    public async Task DraftLookupContinuesBeyondTheFirstReleasePage()
    {
        var fake = new FakeCli { TagExists = true, DraftExists = true, DraftOnSecondPage = true };
        var result = await GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), CancellationToken.None, fake.RunAsync);

        Assert.Equal("existing", result["status"]);
        Assert.Contains(fake.GhArguments, args => args.Any(arg => arg.EndsWith("page=2", StringComparison.Ordinal)));
        Assert.DoesNotContain(fake.GhArguments, args => args[0] == "release" || args.Contains("POST"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedTagFailsEvenWhenReleaseAlreadyExists(bool annotated)
    {
        var fake = new FakeCli { TagExists = true, ReleaseExists = true, TagCommit = OtherCommit, Annotated = annotated };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), CancellationToken.None, fake.RunAsync));
        Assert.Contains("different commit", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.GhArguments, args => args.Contains("POST") || args[0] == "release");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task RepositoryApiErrorsNeverBecomeMissingRelease(int status)
    {
        var fake = new FakeCli { RepositoryStatus = status };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), CancellationToken.None, fake.RunAsync));
        Assert.Single(fake.Commands);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("release")]
    [InlineData("create")]
    [InlineData("malformed")]
    [InlineData("network")]
    [InlineData("missing-tag")]
    [InlineData("wrong-commit")]
    [InlineData("list")]
    public async Task EligibleRunErrorsFailWithoutSuccessShapedFallback(string error)
    {
        var fake = new FakeCli { Error = error, ReleaseExists = error == "missing-tag" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), CancellationToken.None, fake.RunAsync));
        if (error != "create")
        {
            Assert.DoesNotContain(fake.GhArguments, args => args[0] == "release");
        }
    }

    [Theory]
    [InlineData("execution", "auto")]
    [InlineData("artifactGate", "maybe")]
    [InlineData("prerelease", "yes")]
    [InlineData("draft", "yes")]
    [InlineData("tagTemplate", "-invalid")]
    [InlineData("tagTemplate", "a..b")]
    [InlineData("tagTemplate", "a.lock")]
    [InlineData("repository", "owner/repo/extra")]
    [InlineData("hostname", "https://github.com")]
    public async Task InvalidVarsFailBeforeToolInvocation(string key, string value)
    {
        var vars = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value });
        var fake = new FakeCli();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(vars: vars), CancellationToken.None, fake.RunAsync));
        Assert.Empty(fake.Commands);
    }

    [Theory]
    [InlineData("git@github.example.com:owner/example.git")]
    [InlineData("ssh://git@github.example.com/owner/example.git")]
    [InlineData("https://github.example.com/owner/example.git")]
    public async Task EnterpriseRemoteAndTokenArePassedExplicitly(string remote)
    {
        var ctx = Context() with
        {
            RemoteUrl = remote,
            MappedSecretEnvironment = new Dictionary<string, string> { ["GH_ENTERPRISE_TOKEN"] = Token },
        };
        var fake = new FakeCli();
        var result = await GitHubReleasePublisher.ExecuteAsync(SourceConfig, ctx, CancellationToken.None, fake.RunAsync);

        Assert.Equal("github.example.com/owner/example", result["repository"]);
        Assert.All(fake.Commands, command => Assert.Equal("github.example.com", command.Environment["GH_HOST"]));
        Assert.All(fake.GhArguments.Where(args => args[0] == "api"), args => Assert.Contains("github.example.com", args));
    }

    [Fact]
    public async Task MetadataVarsAreRenderedAndPassedAsSeparateArguments()
    {
        var path = Path.Join(Path.GetTempPath(), $"rexo-gh-notes-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "Release notes");
        try
        {
            var vars = JsonSerializer.Serialize(new
            {
                repository = "other/target", hostname = "github.com",
                tagTemplate = "sdk/{{version.semver}}", titleTemplate = "SDK {{version.semver}}; not a shell",
                draft = true, prerelease = "true", generateNotes = true,
                notesFile = path, notesStartTag = "sdk/1.2.2",
            });
            var fake = new FakeCli();
            await GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(vars: vars), CancellationToken.None, fake.RunAsync);
            var release = Assert.Single(fake.GhArguments, args => args[0] == "release");
            Assert.Contains("sdk/1.2.3", release);
            Assert.Contains("SDK 1.2.3; not a shell", release);
            Assert.Contains("github.com/other/target", release);
            Assert.Contains("--draft", release);
            Assert.Contains("--prerelease", release);
            Assert.Contains("--generate-notes", release);
            Assert.Contains("sdk/1.2.2", release);
            Assert.Contains("/rexo-notes.md", release);
            Assert.Contains(fake.Commands, command => command.Arguments.Contains($"{path}:/rexo-notes.md:ro"));
            Assert.Single(fake.Commands, command => command.Arguments.Contains("--volume"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PolicyMergesAfterStandardPostReleaseAndRemainsOptIn()
    {
        Assert.Contains("github-release", EmbeddedPolicyTemplates.TemplateNames);
        var root = Path.Join(Path.GetTempPath(), $"rexo-gh-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Join(root, "rexo.json");
            await File.WriteAllTextAsync(path, """
                {
                  "$schema": "rexo.schema.json", "schemaVersion": "1.0", "name": "example",
                  "extends": ["embedded:standard", "embedded:github-release"],
                  "versioning": { "provider": "fixed", "fallback": "1.2.3" },
                  "vars": { "githubRelease": { "execution": "native" } },
                  "outputs": { "emit": false }
                }
                """);
            var config = await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
            var steps = config.Commands!["release"].Steps;
            Assert.Equal("github-release", steps[^1].Id);
            Assert.True(steps.FindIndex(step => step.Id == "post-release") < steps.Count - 1);
            Assert.Null(steps[^1].AlwaysRun);
            Assert.Equal("native", GitHubReleaseOptions.Read(Context(config) with
            {
                ResolvedVars = ConfigCommandLoader.BuildVarsContext(config),
            }, "execution"));

            var builtins = new BuiltinRegistry();
            var registry = new CommandRegistry();
            var executor = new DefaultCommandExecutor(registry);
            var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(),
                VersionProviderRegistry.CreateDefault(), new ArtifactProviderRegistry());
            loader.LoadInto(registry, config, root, executor);
            var result = await executor.ExecuteAsync("release",
                new CommandInvocation(new Dictionary<string, string>(), new Dictionary<string, string?>(),
                    false, null, root), CancellationToken.None);
            Assert.True(result.Success, result.Message);
            var skipped = Assert.Single(result.Steps, step => step.StepId == "github-release");
            Assert.Equal(true, skipped.Outputs["skipped"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleRunsPublicationOnlyAfterSuccessfulPostRelease(bool hookFails)
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-gh-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Join(root, "rexo.json");
            await File.WriteAllTextAsync(path, """
                {
                  "$schema": "rexo.schema.json", "schemaVersion": "1.0", "name": "example",
                  "extends": ["embedded:standard", "embedded:github-release"],
                  "versioning": { "provider": "fixed", "fallback": "1.2.3" },
                  "outputs": { "emit": false, "ci": { "emit": false, "prefix": "CUSTOM_" } },
                  "commands": {
                    "post-release": {
                      "steps": [{ "id": "check", "uses": "builtin:test-check" }]
                    }
                  }
                }
                """);
            var config = await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
            var builtins = new BuiltinRegistry();
            var registry = new CommandRegistry();
            var executor = new DefaultCommandExecutor(registry);
            var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(),
                VersionProviderRegistry.CreateDefault(), new ArtifactProviderRegistry());
            loader.LoadInto(registry, config, root, executor);
            var hookRan = false;
            var publishRan = false;
            builtins.Register("builtin:test-check", (_, _, _) =>
            {
                hookRan = true;
                return Task.FromResult(new StepResult("check", !hookFails, hookFails ? 1 : 0, TimeSpan.Zero,
                    new Dictionary<string, object?> { ["error"] = hookFails ? "hook failed" : null }));
            });
            builtins.Register("builtin:github-release", (_, ctx, _) =>
            {
                publishRan = true;
                Assert.True(hookRan);
                Assert.True(ctx.CompletedSteps["post-release"].Success);
                Assert.Equal("1.2.3", ctx.Version?.SemVer);
                Assert.Equal("true", ctx.Options["push"]);
                Assert.Equal("CUSTOM_", ctx.CiVariablePrefix);
                return Task.FromResult(new StepResult("github-release", true, 0, TimeSpan.Zero,
                    new Dictionary<string, object?> { ["status"] = "created" }));
            });

            var result = await executor.ExecuteAsync("release",
                new CommandInvocation(new Dictionary<string, string>(), new Dictionary<string, string?> { ["push"] = "true" },
                    false, null, root), CancellationToken.None);
            Assert.True(hookRan);
            Assert.Equal(!hookFails, result.Success);
            Assert.Equal(!hookFails, publishRan);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AutomaticPrereleaseAndEmptyNotesAreNoninteractive()
    {
        var ctx = Context(vars: """{"generateNotes":false}""") with
        {
            Version = new VersionResult("1.2.3-rc.1", 1, 2, 3, "rc.1", Commit, Commit[..7], true, false),
        };
        var fake = new FakeCli();
        await GitHubReleasePublisher.ExecuteAsync(SourceConfig, ctx, CancellationToken.None, fake.RunAsync);

        var release = Assert.Single(fake.GhArguments, args => args[0] == "release");
        Assert.Contains("--prerelease", release);
        Assert.DoesNotContain("--generate-notes", release);
        Assert.Equal("", release[release.ToList().IndexOf("--notes") + 1]);
    }

    [Fact]
    public async Task ToolErrorsMaskExplicitTokensAndCancellationPropagates()
    {
        var fake = new FakeCli { Error = "secret" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), CancellationToken.None, fake.RunAsync));
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
        Assert.Contains("***", error.Message, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GitHubReleasePublisher.ExecuteAsync(SourceConfig, Context(), cancellation.Token, new FakeCli().RunAsync));
    }

    [Fact]
    public async Task MissingExplicitTokenFailsBeforeToolExecution()
    {
        var fake = new FakeCli();
        var ctx = Context() with
        {
            MappedSecretEnvironment = new Dictionary<string, string>
            {
                ["GH_TOKEN"] = "", ["GITHUB_TOKEN"] = "",
            },
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubReleasePublisher.ExecuteAsync(SourceConfig, ctx, CancellationToken.None, fake.RunAsync));
        Assert.Contains("explicit", error.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task BuiltinReportsInvalidConfigurationAsExitSix()
    {
        var builtins = new BuiltinRegistry();
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), new ArtifactProviderRegistry());
        var registry = new CommandRegistry();
        loader.LoadInto(registry, SourceConfig, Path.GetTempPath(), new DefaultCommandExecutor(registry));
        Assert.True(builtins.TryResolve("builtin:github-release", out var handler));
        Assert.NotNull(handler);
        var result = await handler(new StepDefinition("github-release", null, "builtin:github-release", null, null),
            Context(vars: """{"execution":"invalid"}"""), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(6, result.ExitCode);
        Assert.Contains("execution", Assert.IsType<string>(result.Outputs["error"]), StringComparison.Ordinal);
    }

    private sealed class FakeCli
    {
        private string _tag = "v1.2.3";
        public List<GitHubReleaseCommand> Commands { get; } = [];
        public List<IReadOnlyList<string>> GhArguments { get; } = [];
        public bool TagExists { get; set; }
        public bool ReleaseExists { get; set; }
        public bool DraftExists { get; set; }
        public bool DraftOnSecondPage { get; init; }
        public bool Annotated { get; init; }
        public string TagCommit { get; init; } = Commit;
        public int RepositoryStatus { get; init; } = 200;
        public string? Error { get; init; }

        public Task<ShellRunResult> RunAsync(GitHubReleaseCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            IReadOnlyList<string> args = command.FileName == "docker"
                ? command.Arguments.SkipWhile(arg => !arg.StartsWith("docker.io/maniator/gh:", StringComparison.Ordinal)).Skip(1).ToArray()
                : command.Arguments;
            GhArguments.Add(args);
            if (Error == "network") return Task.FromResult(new ShellRunResult(1, "", "network error"));
            if (Error == "secret") return Task.FromResult(new ShellRunResult(1, "", $"rejected {Token}"));
            if (args[0] == "release")
            {
                if (Error == "create") return Task.FromResult(new ShellRunResult(1, "", "release creation rejected"));
                DraftExists = args.Contains("--draft");
                ReleaseExists = !DraftExists;
                return Task.FromResult(new ShellRunResult(0, "https://github.com/owner/example/releases/tag/v1.2.3", ""));
            }
            var path = args[6];
            if (Error == "malformed") return Task.FromResult(new ShellRunResult(0, "not HTTP", ""));
            if (path.EndsWith("/git/refs", StringComparison.Ordinal))
            {
                TagExists = true;
                return Response(201, "{}");
            }
            if (path.Contains("/git/ref/tags/", StringComparison.Ordinal))
            {
                _tag = Uri.UnescapeDataString(path.Split("/git/ref/tags/", StringSplitOptions.None)[1]);
                if (Error == "tag") return Response(403, "{}");
                return TagExists
                    ? Response(200, JsonSerializer.Serialize(new { @object = new { type = Annotated ? "tag" : "commit", sha = Annotated ? OtherCommit : TagCommit } }))
                    : Response(404, "{}");
            }
            if (path.Contains("/git/tags/", StringComparison.Ordinal))
            {
                return Response(200, JsonSerializer.Serialize(new { @object = new { type = "commit", sha = TagCommit } }));
            }
            if (path.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                if (Error == "release") return Response(500, "{}");
                return ReleaseExists ? Response(200, """{"html_url":"https://github.com/owner/example/releases/tag/v1.2.3"}""") : Response(404, "{}");
            }
            if (path.Contains("/releases?", StringComparison.Ordinal))
            {
                if (Error == "list") return Response(403, "{}");
                if (DraftOnSecondPage && path.EndsWith("page=1", StringComparison.Ordinal))
                {
                    return Response(200, JsonSerializer.Serialize(Enumerable.Range(0, 100)
                        .Select(index => new { tag_name = $"other-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}" })));
                }
                return DraftExists
                    ? Response(200, JsonSerializer.Serialize(new[]
                    {
                        new { tag_name = _tag, draft = true, html_url = "https://github.com/owner/example/releases/tag/v1.2.3" },
                    }))
                    : Response(200, "[]");
            }
            if (path.Contains("/commits/", StringComparison.Ordinal))
            {
                return Response(200, JsonSerializer.Serialize(new { sha = Error == "wrong-commit" ? OtherCommit : Commit }));
            }
            return Response(RepositoryStatus, "{}");
        }

        private static Task<ShellRunResult> Response(int status, string json) =>
            Task.FromResult(new ShellRunResult(status < 400 ? 0 : 1,
                $"HTTP/2.0 {status.ToString(System.Globalization.CultureInfo.InvariantCulture)} status\r\nContent-Type: application/json\r\n\r\n{json}",
                status < 400 ? "" : "GitHub API error"));
    }
}
