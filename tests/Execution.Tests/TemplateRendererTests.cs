namespace Rexo.Execution.Tests;

using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Templating;

[Collection("EnvVar Mutation Sequential")]
public sealed class TemplateRendererTests
{
    private static ExecutionContext MakeContext(
        string? version = null,
        Dictionary<string, string>? args = null,
        Dictionary<string, string?>? options = null)
    {
        var ctx = ExecutionContext.Empty("C:\\repo");
        if (args is not null || options is not null)
        {
            ctx = ctx with
            {
                Args = args ?? new Dictionary<string, string>(),
                Options = options ?? new Dictionary<string, string?>(),
            };
        }

        if (version is not null)
        {
            var parts = version.Split('.');
            var major = parts.Length > 0 && int.TryParse(parts[0], out var mj) ? mj : 0;
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var mn) ? mn : 0;
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
            var vr = new VersionResult(version, major, minor, patch, null, "abc1234", "abc1234", false, true);
            ctx = ctx.WithVersion(vr);
        }

        return ctx;
    }

    [Fact]
    public void RenderReturnsPlainTextUnchanged()
    {
        var renderer = new TemplateRenderer();
        var result = renderer.Render("hello world", MakeContext());
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void RenderResolvesArgVariable()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["name"] = "acme" });
        var result = renderer.Render("hello {{args.name}}", ctx);
        Assert.Equal("hello acme", result);
    }

    [Fact]
    public void RenderResolvesOptionVariable()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(options: new Dictionary<string, string?> { ["env"] = "prod" });
        var result = renderer.Render("env={{options.env}}", ctx);
        Assert.Equal("env=prod", result);
    }

    [Fact]
    public void RenderResolvesGitVariables()
    {
        var renderer = new TemplateRenderer();
        var ctx = ExecutionContext.Empty("C:\\repo") with
        {
            Branch = "feature/demo",
            CommitSha = "abcdef1234567890",
            ShortSha = "abcdef1",
            RemoteUrl = "https://github.com/agile-north/rexo",
            IsCleanWorkingTree = true,
        };

        Assert.Equal("feature/demo", renderer.Render("{{git.branch}}", ctx));
        Assert.Equal("abcdef1234567890", renderer.Render("{{git.commitSha}}", ctx));
        Assert.Equal("abcdef1", renderer.Render("{{git.shortSha}}", ctx));
        Assert.Equal("https://github.com/agile-north/rexo", renderer.Render("{{git.remoteUrl}}", ctx));
        Assert.Equal("true", renderer.Render("{{git.isCleanWorkingTree}}", ctx));
    }

    [Fact]
    public void RenderAppliesSlugFilter()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["branch"] = "Feature/My Cool Branch" });
        var result = renderer.Render("{{args.branch | slug}}", ctx);
        Assert.Equal("feature-my-cool-branch", result);
    }

    [Fact]
    public void RenderAppliesUpperFilter()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["env"] = "prod" });
        var result = renderer.Render("{{args.env | upper}}", ctx);
        Assert.Equal("PROD", result);
    }

    [Fact]
    public void RenderAppliesLowerFilter()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["tag"] = "LATEST" });
        var result = renderer.Render("{{args.tag | lower}}", ctx);
        Assert.Equal("latest", result);
    }

    [Fact]
    public void RenderAppliesDefaultFilter()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        var result = renderer.Render("{{args.missing | default('fallback')}}", ctx);
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void RenderDefaultFilterCanResolveFallbackVariable()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string>
        {
            ["fallback"] = "release",
        });

        var result = renderer.Render("{{args.missing | default(args.fallback)}}", ctx);
        Assert.Equal("release", result);
    }

    [Fact]
    public void RenderCoalesceFilterReturnsCurrentValueWhenPresent()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string>
        {
            ["tag"] = "v1",
            ["fallback"] = "release",
        });

        var result = renderer.Render("{{args.tag | coalesce(args.fallback, 'dev')}}", ctx);
        Assert.Equal("v1", result);
    }

    [Fact]
    public void RenderCoalesceFilterFallsBackAcrossVariablesAndLiteral()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string>
        {
            ["fallback"] = "release",
        });

        var result = renderer.Render("{{args.tag | coalesce(args.missing, args.fallback, 'dev')}}", ctx);
        Assert.Equal("release", result);
    }

    [Fact]
    public void RenderCoalesceFilterCanResolveEnvironmentFallback()
    {
        const string key = "REXO_TEMPLATE_COALESCE_ENV_TEST";
        var original = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "from-env");

        try
        {
            var renderer = new TemplateRenderer();
            var ctx = MakeContext();

            var result = renderer.Render($"{{{{args.tag | coalesce(args.missing, env.{key}, 'dev')}}}}", ctx);
            Assert.Equal("from-env", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    [Fact]
    public void RenderCoalesceFilterTreatsWhitespaceAsEmpty()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string>
        {
            ["tag"] = "   ",
            ["fallback"] = "release",
        });

        var result = renderer.Render("{{args.tag | coalesce(args.fallback, 'dev')}}", ctx);
        Assert.Equal("release", result);
    }

    [Fact]
    public void RenderSupportsNullCoalescingOperator()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string>
        {
            ["fallback"] = "release",
        });

        var result = renderer.Render("{{args.tag ?? args.fallback ?? 'dev'}}", ctx);
        Assert.Equal("release", result);
    }

    [Fact]
    public void RenderSupportsNullCoalescingOperatorWithFilters()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string>
        {
            ["tag"] = "   ",
            ["fallback"] = "release",
        });

        var result = renderer.Render("{{args.tag | trim ?? args.fallback | upper ?? 'dev'}}", ctx);
        Assert.Equal("RELEASE", result);
    }

    [Fact]
    public void RenderResolvesVersionMajorMinorPatch()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(version: "2.3.4");
        Assert.Equal("2", renderer.Render("{{version.major}}", ctx));
        Assert.Equal("3", renderer.Render("{{version.minor}}", ctx));
        Assert.Equal("4", renderer.Render("{{version.patch}}", ctx));
    }

    [Fact]
    public void RenderResolvesVersionPreReleaseComponents()
    {
        var renderer = new TemplateRenderer();
        var ctx = ExecutionContext.Empty("C:\\repo") with
        {
            Version = new VersionResult(
                SemVer: "0.1.322-qa.5",
                Major: 0,
                Minor: 1,
                Patch: 322,
                PreRelease: "qa.5",
                CommitSha: "abc1234",
                ShortSha: "abc1234",
                IsPreRelease: true,
                IsStable: false),
        };

        Assert.Equal("qa.5", renderer.Render("{{version.preReleaseTag}}", ctx));
        Assert.Equal("qa", renderer.Render("{{version.preReleaseLabel}}", ctx));
        Assert.Equal("5", renderer.Render("{{version.preReleaseNumber}}", ctx));
        Assert.Equal("-qa", renderer.Render("{{version.preReleaseLabelWithDash}}", ctx));
        Assert.Equal("-qa.5", renderer.Render("{{version.preReleaseTagWithDash}}", ctx));
    }

    [Fact]
    public void RenderLeavesMissingVariableAsEmpty()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        var result = renderer.Render("x={{args.notexist}}", ctx);
        Assert.Equal("x=", result);
    }

    [Fact]
    public void RenderHandlesMultipleSubstitutionsInOneString()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(
            args: new Dictionary<string, string> { ["a"] = "hello", ["b"] = "world" });
        var result = renderer.Render("{{args.a}} {{args.b}}", ctx);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void EqualityExpressionReturnsTrueWhenEqual()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(version: "1.0.0");
        var result = renderer.Render("{{version.major == '1'}}", ctx);
        Assert.Equal("true", result);
    }

    [Fact]
    public void EqualityExpressionReturnsFalseWhenNotEqual()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(version: "2.0.0");
        var result = renderer.Render("{{version.major == '1'}}", ctx);
        Assert.Equal("false", result);
    }

    [Fact]
    public void InequalityExpressionReturnsTrueWhenNotEqual()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(options: new Dictionary<string, string?> { ["ci"] = "true" });
        var result = renderer.Render("{{options.ci != ''}}", ctx);
        Assert.Equal("true", result);
    }

    [Fact]
    public void InequalityExpressionReturnsFalseWhenEqual()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(options: new Dictionary<string, string?> { ["ci"] = "" });
        var result = renderer.Render("{{options.ci != ''}}", ctx);
        Assert.Equal("false", result);
    }

    [Fact]
    public void EqualityExpressionReturnsFalseWhenVariableIsMissing()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        var result = renderer.Render("{{vars.dotnet.test.coverage.mode == 'none'}}", ctx);
        Assert.Equal("false", result);
    }

    [Fact]
    public void InequalityExpressionReturnsTrueWhenVariableIsMissing()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        var result = renderer.Render("{{vars.dotnet.test.coverage.mode != 'none'}}", ctx);
        Assert.Equal("true", result);
    }

    [Fact]
    public void EqualityExpressionSupportsBooleanLiteral()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(options: new Dictionary<string, string?> { ["confirm"] = "true" });
        var result = renderer.Render("{{options.confirm == true}}", ctx);
        Assert.Equal("true", result);
    }

    [Fact]
    public void EqualityExpressionSupportsFilterOperands()
    {
        var renderer = new TemplateRenderer();
        var missing = MakeContext();
        var confirmed = MakeContext(options: new Dictionary<string, string?> { ["confirm"] = "true" });

        Assert.Equal("true", renderer.Render("{{options.confirm | default(false) == false}}", missing));
        Assert.Equal("false", renderer.Render("{{options.confirm | default(false) == false}}", confirmed));
    }

    [Fact]
    public void EqualityExpressionSupportsCoalesceOperands()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["fallback"] = "release" });

        var result = renderer.Render("{{args.tag | coalesce(args.fallback, 'dev') == 'release'}}", ctx);
        Assert.Equal("true", result);
    }

    [Fact]
    public void EqualityExpressionSupportsNullCoalescingOperatorOperands()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["fallback"] = "release" });

        var result = renderer.Render("{{args.tag ?? args.fallback ?? 'dev' == 'release'}}", ctx);
        Assert.Equal("true", result);
    }

    [Fact]
    public void EqualityExpressionComparesLiteralToLiteral()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        var result = renderer.Render("{{'foo' == 'foo'}}", ctx);
        Assert.Equal("true", result);
    }

    [Fact]
    public void TrimFilterRemovesWhitespace()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["val"] = "  hello  " });
        Assert.Equal("hello", renderer.Render("{{args.val | trim}}", ctx));
    }

    [Fact]
    public void BasenameFilterReturnsFileName()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["path"] = "/foo/bar/baz.txt" });
        Assert.Equal("baz.txt", renderer.Render("{{args.path | basename}}", ctx));
    }

    [Fact]
    public void DirnameFilterReturnsDirectory()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["path"] = "/foo/bar/baz.txt" });
        var result = renderer.Render("{{args.path | dirname}}", ctx);
        // Result is OS-dependent but must not contain the filename
        Assert.DoesNotContain("baz.txt", result);
    }

    [Fact]
    public void FilestemFilterReturnsNameWithoutExtension()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["path"] = "report.xml" });
        Assert.Equal("report", renderer.Render("{{args.path | filestem}}", ctx));
    }

    [Fact]
    public void FileextFilterReturnsExtension()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["path"] = "archive.tar.gz" });
        Assert.Equal(".gz", renderer.Render("{{args.path | fileext}}", ctx));
    }

    [Fact]
    public void UrlencodeFilterEncodesSpecialCharacters()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["q"] = "hello world" });
        Assert.Equal("hello%20world", renderer.Render("{{args.q | urlencode}}", ctx));
    }

    [Fact]
    public void ReplaceFilterSubstitutesSubstring()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["branch"] = "feature/my-feature" });
        Assert.Equal("feature-my-feature", renderer.Render("{{args.branch | replace(/,-)}}", ctx));
    }

    [Fact]
    public void ReplaceFilterDoesNotInterpretRegexSyntax()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["value"] = "fooXXXbar" });
        Assert.Equal("fooXXXbar", renderer.Render("{{args.value | replace(/foo.*bar/, 'baz')}}", ctx));
    }

    [Fact]
    public void ReplacePatternFilterSupportsRegexCaptureGroups()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["pair"] = "foo=123" });
        Assert.Equal("123-foo", renderer.Render("{{args.pair | replacePattern(/(\\w+)=(\\d+)/, '$2-$1')}}", ctx));
    }

    [Fact]
    public void TruncateFilterCutsLongValues()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["sha"] = "abcdef1234567890" });
        Assert.Equal("abcdef", renderer.Render("{{args.sha | truncate(6)}}", ctx));
    }

    [Fact]
    public void Sha256FilterProduces64CharHexString()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["val"] = "hello" });
        var result = renderer.Render("{{args.val | sha256}}", ctx);
        Assert.Equal(64, result.Length);
        Assert.Matches("^[0-9a-f]{64}$", result);
    }

    [Fact]
    public void RenderResolvesEnvFromRexoDotEnvWhenProcessValueMissing()
    {
        const string key = "REXO_TEMPLATE_ENV_TEST";
        var original = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, null);

        var dir = Path.Combine(Path.GetTempPath(), $"rexo-template-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));

        try
        {
            File.WriteAllText(Path.Combine(dir, ".rexo", ".env"), $"{key}=from-rexo\n");

            var renderer = new TemplateRenderer();
            var ctx = ExecutionContext.Empty(dir);
            var result = renderer.Render($"{{{{env.{key}}}}}", ctx);

            Assert.Equal("from-rexo", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    // ----------------------------------------------------------------
    // {{outputs.*}} template resolution
    // ----------------------------------------------------------------

    private static ExecutionContext MakeOutputsContext(RepoConfig? config = null)
    {
        var cfg = config ?? new RepoConfig(Name: "test", Commands: null, Aliases: null);
        return ExecutionContext.Empty("C:\\repo") with
        {
            ResolvedOutputs = ConfigCommandLoader.BuildOutputsContext(cfg),
        };
    }

    [Fact]
    public void RenderResolvesOutputsTestsResultsDefaultPath()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeOutputsContext();
        Assert.Equal("artifacts/tests", renderer.Render("{{outputs.tests.results}}", ctx));
    }

    [Fact]
    public void RenderResolvesOutputsTestsCoverageDefaultPath()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeOutputsContext();
        Assert.Equal("artifacts/coverage", renderer.Render("{{outputs.tests.coverage}}", ctx));
    }

    [Fact]
    public void RenderResolvesOutputsAnalysisReportsDefaultPath()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeOutputsContext();
        Assert.Equal("artifacts/analysis", renderer.Render("{{outputs.analysis.reports}}", ctx));
    }

    [Fact]
    public void RenderResolvesOutputsRootDefaultPath()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeOutputsContext();
        Assert.Equal("artifacts", renderer.Render("{{outputs.root}}", ctx));
    }

    [Fact]
    public void RenderResolvesOutputsTestsResultsCustomPath()
    {
        var renderer = new TemplateRenderer();
        var cfg = new RepoConfig(Name: "test", Commands: null, Aliases: null) with
        {
            Outputs = new RepoOutputsConfig
            {
                Tests = new RepoTestOutputPathsConfig { Results = "custom/test-out" },
            },
        };
        var ctx = MakeOutputsContext(cfg);
        Assert.Equal("custom/test-out", renderer.Render("{{outputs.tests.results}}", ctx));
    }

    // ----------------------------------------------------------------
    // {{settings.*}} template resolution
    // ----------------------------------------------------------------

    private static ExecutionContext MakeSettingsContext(RepoConfig config)
    {
        return ExecutionContext.Empty("C:\\repo") with
        {
            ResolvedSettings = ConfigCommandLoader.BuildSettingsContext(config),
        };
    }

    [Fact]
    public void RenderResolvesSettingsNestedValue()
    {
        var json = System.Text.Json.JsonDocument.Parse("""{"configuration":"Debug"}""");
        var cfg = new RepoConfig(Name: "test", Commands: null, Aliases: null) with
        {
            Settings = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["dotnet"] = json.RootElement,
            },
        };
        var ctx = MakeSettingsContext(cfg);
        var renderer = new TemplateRenderer();
        Assert.Equal("Debug", renderer.Render("{{settings.dotnet.configuration}}", ctx));
    }

    [Fact]
    public void RenderUsesDefaultFilterWhenSettingsMissing()
    {
        var ctx = ExecutionContext.Empty("C:\\repo") with
        {
            ResolvedSettings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
        };
        var renderer = new TemplateRenderer();
        Assert.Equal("npm", renderer.Render("{{settings.node.packageManager | default('npm')}}", ctx));
    }

    // ----------------------------------------------------------------
    // prefix / suffix filters
    // ----------------------------------------------------------------

    [Fact]
    public void PrefixFilterReturnsEmptyWhenInputIsEmpty()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["val"] = "" });
        Assert.Equal("", renderer.Render("{{args.val | prefix('--flag ')}}", ctx));
    }

    [Fact]
    public void PrefixFilterReturnsEmptyWhenInputIsWhitespace()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["val"] = "   " });
        Assert.Equal("", renderer.Render("{{args.val | prefix('--flag ')}}", ctx));
    }

    [Fact]
    public void PrefixFilterReturnsEmptyWhenVariableIsMissing()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        Assert.Equal("", renderer.Render("{{args.missing | prefix('--flag ')}}", ctx));
    }

    [Fact]
    public void PrefixFilterPrependsPrefixToNonEmptyInput()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["dir"] = "artifacts/tests" });
        Assert.Equal("--results-directory artifacts/tests", renderer.Render("{{args.dir | prefix('--results-directory ')}}", ctx));
    }

    [Fact]
    public void SuffixFilterReturnsEmptyWhenInputIsEmpty()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["val"] = "" });
        Assert.Equal("", renderer.Render("{{args.val | suffix('.sarif')}}", ctx));
    }

    [Fact]
    public void SuffixFilterReturnsEmptyWhenInputIsWhitespace()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["val"] = "   " });
        Assert.Equal("", renderer.Render("{{args.val | suffix('.sarif')}}", ctx));
    }

    [Fact]
    public void SuffixFilterReturnsEmptyWhenVariableIsMissing()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        Assert.Equal("", renderer.Render("{{args.missing | suffix('.sarif')}}", ctx));
    }

    [Fact]
    public void SuffixFilterAppendsSuffixToNonEmptyInput()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["dir"] = "artifacts/analysis/sarif" });
        Assert.Equal("artifacts/analysis/sarif/dotnet-build.sarif", renderer.Render("{{args.dir | suffix('/dotnet-build.sarif')}}", ctx));
    }

    [Fact]
    public void AbspathFilterReturnsEmptyWhenInputIsEmpty()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["dir"] = "" });
        Assert.Equal("", renderer.Render("{{args.dir | abspath}}", ctx));
    }

    [Fact]
    public void AbspathFilterResolvesRepoRelativePath()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["dir"] = "artifacts/analysis/sarif/dotnet-build.sarif" });
        Assert.Equal(
            Path.Combine("C:\\repo", "artifacts", "analysis", "sarif", "dotnet-build.sarif"),
            renderer.Render("{{args.dir | abspath}}", ctx));
    }

    [Fact]
    public void MultiPipeChainProducesEmptyWhenInputIsMissing()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();
        Assert.Equal("", renderer.Render("{{args.missing | suffix('/dotnet-build.sarif') | prefix('/p:ErrorLog=')}}", ctx));
    }

    [Fact]
    public void MultiPipeChainProducesFullValueWhenInputIsPresent()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["dir"] = "artifacts/analysis/sarif" });
        Assert.Equal(
            Path.Combine("/p:ErrorLog=C:\\repo", "artifacts", "analysis", "sarif", "dotnet-build.sarif"),
            renderer.Render("{{args.dir | suffix('/dotnet-build.sarif') | abspath | prefix('/p:ErrorLog=')}}", ctx));
    }

    [Fact]
    public void CoalesceFilterComposesWithLaterFilters()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext(args: new Dictionary<string, string> { ["dir"] = "artifacts/analysis/sarif" });

        Assert.Equal(
            Path.Combine("/p:ErrorLog=C:\\repo", "artifacts", "analysis", "sarif", "dotnet-build.sarif"),
            renderer.Render("{{args.missing | coalesce(args.dir, 'artifacts/analysis') | suffix('/dotnet-build.sarif') | abspath | prefix('/p:ErrorLog=')}}", ctx));
    }

    [Fact]
    public void RenderResolvesPushSummaryDefaultsWhenNoPushStepsHaveRun()
    {
        var renderer = new TemplateRenderer();
        var ctx = MakeContext();

        Assert.Equal("false", renderer.Render("{{push.hasData}}", ctx));
        Assert.Equal("false", renderer.Render("{{push.anyPushed}}", ctx));
        Assert.Equal("0", renderer.Render("{{push.pushedCount}}", ctx));
        Assert.Equal("", renderer.Render("{{push.blockReasons}}", ctx));
    }

    [Fact]
    public void RenderResolvesPushSummaryFromCompletedStepOutputs()
    {
        var renderer = new TemplateRenderer();
        var pushStep = new StepResult(
            "push",
            true,
            0,
            TimeSpan.Zero,
            new Dictionary<string, object?>
            {
                ["__artifacts"] = new List<ArtifactManifestEntry>
                {
                    new("docker", "api", true, true, ["ghcr.io/org/api:1.2.3"]),
                    new("npm", "sdk", true, false, []),
                },
                ["__pushDecisions"] = new List<PushDecision>
                {
                    new("api", true, "Push succeeded."),
                    new("sdk", false, "Push disabled by policy."),
                    new("cli", false, "Push disabled by policy."),
                },
            });

        var ctx = MakeContext() with
        {
            CompletedSteps = new Dictionary<string, StepResult>(StringComparer.OrdinalIgnoreCase)
            {
                ["push"] = pushStep,
            },
        };

        Assert.Equal("true", renderer.Render("{{push.hasData}}", ctx));
        Assert.Equal("true", renderer.Render("{{push.anyPushed}}", ctx));
        Assert.Equal("1", renderer.Render("{{push.pushedCount}}", ctx));
        Assert.Equal("2", renderer.Render("{{push.artifactCount}}", ctx));
        Assert.Equal("3", renderer.Render("{{push.decisionCount}}", ctx));
        Assert.Equal("1", renderer.Render("{{push.allowedCount}}", ctx));
        Assert.Equal("2", renderer.Render("{{push.deniedCount}}", ctx));
        Assert.Equal("true", renderer.Render("{{push.anyBlocked}}", ctx));
        Assert.Equal("Push disabled by policy.", renderer.Render("{{push.blockReasons}}", ctx));
    }
}
