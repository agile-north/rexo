namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Templating;

/// <summary>
/// Verifies embedded policy defaults (opt-in vars), deep var merging, the container registry,
/// and policy/config parity semantics.
/// </summary>
public sealed class PolicyDefaultsTests
{
    private static readonly string SchemaUri = RepoConfigurationLoader.SupportedRexoSchemaUri;

    private static async Task<RepoConfig> LoadAsync(string extra)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-policy-defaults-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "rexo.json");
            await File.WriteAllTextAsync(path, $$"""
                {
                  "$schema": "{{SchemaUri}}",
                  "schemaVersion": "1.0",
                  "name": "test-repo",
                  {{extra}}
                }
                """);
            return await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static ExecutionContext Context(RepoConfig config, string sarifDir = "artifacts/sarif", IReadOnlyDictionary<string, string?>? options = null) =>
        new(Path.GetTempPath(), "main", "abc", new Dictionary<string, object?>())
        {
            Options = options ?? new Dictionary<string, string?>(),
            ResolvedVars = ConfigCommandLoader.BuildVarsContext(config),
            ResolvedOutputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["analysis"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["sarif"] = sarifDir },
            },
        };

    private static HashSet<string> ActiveSteps(RepoConfig config, string command, ExecutionContext context)
    {
        var renderer = new TemplateRenderer();
        return config.Commands![command].Steps
            .Where(step => step.When is null || IsTruthy(renderer.Render(step.When, context)))
            .Select(step => step.Id!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsTruthy(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
        value != "0" &&
        !value.Equals("no", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task DotnetPolicyEnablesOnlyEssentialAnalyzeStepsByDefault()
    {
        var config = await LoadAsync("""
            "extends": ["embedded:dotnet", "embedded:standard"]
            """);

        var analyze = ActiveSteps(config, "analyze", Context(config));
        Assert.Equal(["dotnet-build-warnings-no-sarif"], analyze.Order(StringComparer.Ordinal));

        var test = ActiveSteps(config, "test", Context(config));
        Assert.Equal(["dotnet-test"], test);

        var security = ActiveSteps(config, "security", Context(config));
        Assert.Empty(security);
    }

    [Fact]
    public async Task DotnetPolicyOptInVarsEnableFormatSarifAndSecurity()
    {
        var config = await LoadAsync("""
            "extends": ["embedded:dotnet"],
            "vars": {
              "dotnet": {
                "analyze": { "format": { "enabled": true }, "sarif": { "enabled": true } },
                "security": { "enabled": true },
                "test": { "coverage": { "enabled": false } }
              }
            }
            """);

        var analyze = ActiveSteps(config, "analyze", Context(config));
        Assert.Contains("dotnet-format-check", analyze);
        Assert.Contains("dotnet-sarif-targets", analyze);
        Assert.Contains("dotnet-build-warnings", analyze);
        Assert.Contains("dotnet-sarif-merge", analyze);
        Assert.DoesNotContain("dotnet-build-warnings-no-sarif", analyze);

        Assert.Equal(["dotnet-test-no-coverage"], ActiveSteps(config, "test", Context(config)));
        Assert.Equal(["dotnet-vulnerable-packages"], ActiveSteps(config, "security", Context(config)));

        // Deep merge keeps untouched policy defaults.
        var renderer = new TemplateRenderer();
        Assert.Equal("Release", renderer.Render("{{vars.dotnet.configuration}}", Context(config)));
        Assert.Equal("2.1", renderer.Render("{{vars.dotnet.analyze.sarif.version}}", Context(config)));
    }

    [Fact]
    public async Task DotnetSarifStaysOffWhenSarifOutputIsDisabled()
    {
        var config = await LoadAsync("""
            "extends": ["embedded:dotnet"],
            "vars": { "dotnet": { "analyze": { "sarif": { "enabled": true } } } }
            """);

        var analyze = ActiveSteps(config, "analyze", Context(config, sarifDir: string.Empty));
        Assert.Equal(["dotnet-build-warnings-no-sarif"], analyze);
    }

    [Fact]
    public async Task DotnetLegacyCoverageModeNoneStillDisablesCoverage()
    {
        var config = await LoadAsync("""
            "extends": ["embedded:dotnet"],
            "vars": { "dotnet": { "test": { "coverage": { "mode": "none" } } } }
            """);

        Assert.Equal(["dotnet-test-no-coverage"], ActiveSteps(config, "test", Context(config)));
    }

    [Fact]
    public async Task NodePolicyDefaultsAndPackageManagerRestore()
    {
        var config = await LoadAsync("""
            "extends": ["embedded:node"],
            "vars": { "node": { "packageManager": "pnpm" } }
            """);

        Assert.Equal(["node-install-pnpm"], ActiveSteps(config, "restore", Context(config)));
        Assert.Equal(["node-lint"], ActiveSteps(config, "analyze", Context(config)));
        Assert.Empty(ActiveSteps(config, "security", Context(config)));

        var custom = await LoadAsync("""
            "extends": ["embedded:node"],
            "vars": { "node": { "restore": { "command": "npm install" }, "audit": { "enabled": true } } }
            """);
        Assert.Equal(["node-install-custom"], ActiveSteps(custom, "restore", Context(custom)));
        Assert.Equal(["node-audit"], ActiveSteps(custom, "security", Context(custom)));
    }

    [Fact]
    public async Task GitTagPolicyHonorsDryRunPrefixAndRemote()
    {
        var config = await LoadAsync("""
            "extends": ["embedded:git-tag"],
            "vars": { "gitTag": { "prefix": "v", "remote": "upstream" } }
            """);

        var dry = new Dictionary<string, string?> { ["dry-run"] = "true", ["force"] = "false", ["remote"] = string.Empty };
        Assert.DoesNotContain("create-tag", ActiveSteps(config, "post-push", Context(config, options: dry)));
        Assert.DoesNotContain("tag-exists", ActiveSteps(config, "post-push", Context(config, options: dry)));

        var live = new Dictionary<string, string?> { ["dry-run"] = "false", ["force"] = "false", ["remote"] = string.Empty };
        var active = ActiveSteps(config, "post-push", Context(config, options: live));
        Assert.Contains("create-tag", active);
        Assert.DoesNotContain("delete-existing-tag", active);

        var createTag = config.Commands!["post-push"].Steps.Single(step => step.Id == "create-tag");
        var rendered = new TemplateRenderer().Render(createTag.Run!, Context(config, options: live) with
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abc", "abc", false, true),
        });
        Assert.Contains("git tag \"v1.2.3\"", rendered, StringComparison.Ordinal);
        Assert.Contains("git push upstream", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerRegistryReferencesResolveWithOverridesAndCommandDefaults()
    {
        var config = await LoadAsync("""
            "containers": {
              "base": { "image": "alpine:3", "env": { "A": "1" } },
              "tools": { "extends": "base", "env": { "B": "2" }, "workingDirectory": "/src" }
            },
            "vars": { "ctr": "tools" },
            "commands": {
              "ci": {
                "container": "{{vars.ctr}}",
                "steps": [
                  { "id": "inherit", "run": "echo 1" },
                  { "id": "override", "run": "echo 2", "container": { "use": "base", "env": { "C": "3" } } },
                  { "id": "host", "run": "echo 3", "container": false },
                  { "id": "builtin", "uses": "builtin:validate" }
                ]
              }
            }
            """);

        var command = config.Commands!["ci"];
        var ctx = Context(config);
        var renderer = new TemplateRenderer();
        string Render(string value) => renderer.Render(value, ctx);

        var inherit = ContainerResolver.ResolveForStep(command.Steps[0], command.Container, config.Containers, Render)!;
        Assert.Equal("alpine:3", inherit.Image);
        Assert.Equal("/src", inherit.WorkingDirectory);
        Assert.Equal("1", inherit.Env!["A"]);
        Assert.Equal("2", inherit.Env["B"]);

        var overridden = ContainerResolver.ResolveForStep(command.Steps[1], command.Container, config.Containers, Render)!;
        Assert.Equal("alpine:3", overridden.Image);
        Assert.Equal("3", overridden.Env!["C"]);
        Assert.False(overridden.Env.ContainsKey("B"));

        Assert.Null(ContainerResolver.ResolveForStep(command.Steps[2], command.Container, config.Containers, Render));
        Assert.Null(ContainerResolver.ResolveForStep(command.Steps[3], command.Container, config.Containers, Render));
    }

    [Fact]
    public void ContainerResolverRejectsUnknownAndCircularReferences()
    {
        var registry = new Dictionary<string, RepoStepContainerConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new() { Extends = "b" },
            ["b"] = new() { Extends = "a" },
        };

        Assert.Throws<InvalidOperationException>(() =>
            ContainerResolver.Resolve(new RepoStepContainerConfig { Use = "missing" }, registry, static s => s));
        Assert.Throws<InvalidOperationException>(() =>
            ContainerResolver.Resolve(new RepoStepContainerConfig { Use = "a" }, registry, static s => s));
        Assert.Null(ContainerResolver.Resolve(new RepoStepContainerConfig { Use = "none" }, registry, static s => s));
    }

    [Fact]
    public async Task PolicyDefaultsNeverOverrideRepoValues()
    {
        var policy = await RepoConfigurationLoader.LoadEmbeddedPolicyAsync("dotnet", CancellationToken.None);
        var config = new RepoConfig("repo", null, null)
        {
            Vars = new Dictionary<string, JsonElement>
            {
                ["dotnet"] = JsonSerializer.SerializeToElement(new { configuration = "Debug" }),
            },
            Containers = new Dictionary<string, RepoStepContainerConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["dotnet-sdk"] = new() { Image = "mcr.microsoft.com/dotnet/sdk:9.0" },
            },
        };

        var effective = RepoConfigurationLoader.ApplyPolicyDefaults(config, policy);

        Assert.Equal("Debug", effective.Vars!["dotnet"].GetProperty("configuration").GetString());
        Assert.False(effective.Vars["dotnet"].GetProperty("analyze").GetProperty("sarif").GetProperty("enabled").GetBoolean());
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:9.0", effective.Containers!["dotnet-sdk"].Image);
        Assert.Equal("/work", effective.Containers["dotnet-sdk"].WorkingDirectory);
        Assert.Null(effective.Commands);
    }

    [Fact]
    public void ContainerJsonAcceptsStringFalseAndObjectForms()
    {
        static RepoStepContainerConfig? Parse(string json) =>
            JsonSerializer.Deserialize<RepoStepContainerConfig>(json);

        Assert.Equal("dotnet-sdk", Parse("\"dotnet-sdk\"")!.Use);
        Assert.Equal(RepoStepContainerConfig.NoneReference, Parse("false")!.Use);
        var obj = Parse("""{ "use": "x", "image": "img", "env": { "K": "V" } }""")!;
        Assert.Equal("x", obj.Use);
        Assert.Equal("img", obj.Image);
        Assert.Equal("V", obj.Env!["K"]);
    }
}
