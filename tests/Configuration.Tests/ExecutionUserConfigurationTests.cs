namespace Rexo.Configuration.Tests;

using System.Text.Json;
using Rexo.Configuration.Models;

public sealed class ExecutionUserConfigurationTests
{
    [Fact]
    public void CommandAndContainerUsersRoundTrip()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var command = JsonSerializer.Deserialize<RepoCommandConfig>("""
            {"user":"builder","domain":".","passwordSecret":"buildLogin","container":{"image":"alpine","user":"1000:1000"},"steps":[{"run":"echo hello","user":"other","passwordSecret":"otherLogin"}]}
            """, options);
        Assert.NotNull(command);
        var copy = JsonSerializer.Deserialize<RepoCommandConfig>(JsonSerializer.Serialize(command), options);
        Assert.NotNull(copy);
        Assert.Equal("builder", copy.User);
        Assert.Equal(".", copy.Domain);
        Assert.Equal("buildLogin", copy.PasswordSecret);
        Assert.Equal("1000:1000", copy.Container?.User);
        Assert.Equal("other", Assert.Single(copy.Steps).User);
        Assert.Equal("otherLogin", copy.Steps[0].PasswordSecret);
        Assert.DoesNotContain("hostUserDefaultsApplied", JsonSerializer.Serialize(copy), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostDefaultsPreserveCredentialOwnership()
    {
        var command = new RepoCommandConfig(null, [], [])
        {
            User = "builder",
            Domain = "BUILD",
            PasswordSecret = "buildLogin",
        };
        var inherited = CommandUserResolver.ApplyDefaults(new RepoStepConfig(Run: "echo hello"), command);
        Assert.Equal("builder", inherited.User);
        Assert.Equal("BUILD", inherited.Domain);
        Assert.Equal("buildLogin", inherited.PasswordSecret);

        var changed = CommandUserResolver.ApplyDefaults(new RepoStepConfig(Run: "echo hello") { User = "other" }, command);
        Assert.Equal("other", changed.User);
        Assert.Null(changed.Domain);
        Assert.Null(changed.PasswordSecret);

        var rotated = CommandUserResolver.ApplyDefaults(new RepoStepConfig(Run: "echo hello")
        {
            User = "builder",
            PasswordSecret = "rotatedLogin",
        }, command);
        Assert.Equal("BUILD", rotated.Domain);
        Assert.Equal("rotatedLogin", rotated.PasswordSecret);
        Assert.Null(CommandUserResolver.ApplyDefaults(new RepoStepConfig(Command: "child"), command).User);
        Assert.Null(CommandUserResolver.ApplyDefaults(new RepoStepConfig(Uses: "builtin:validate"), command).User);
    }

    [Fact]
    public void ContainerUsersFollowRegistryInheritanceAndOverrides()
    {
        var registry = new Dictionary<string, RepoStepContainerConfig>
        {
            ["base"] = new(Image: "alpine") { User = "1000:1000" },
            ["child"] = new() { Extends = "base" },
        };
        Assert.Equal("1000:1000", ContainerResolver.Resolve(new() { Use = "child" }, registry, static s => s)?.User);
        Assert.Equal("root", ContainerResolver.Resolve(new() { Use = "child", User = "root" }, registry, static s => s)?.User);
        var merged = ContainerResolver.MergeRegistries(registry, new()
        {
            ["base"] = new() { User = "2000:2000" },
        });
        Assert.Equal("2000:2000", merged?["base"].User);
        Assert.Equal("alpine", merged?["base"].Image);
    }

    [Theory]
    [InlineData("\"user\":\"builder\",\"domain\":\".\",\"passwordSecret\":\"login\",", "\"run\":\"echo hello\"")]
    [InlineData("", "\"run\":\"echo hello\",\"user\":\"builder\",\"passwordSecret\":\"login\"")]
    [InlineData("", "\"run\":\"echo hello\",\"container\":{\"image\":\"alpine\",\"user\":\"1000:1000\"}")]
    public async Task SchemaAcceptsExecutionUsers(string commandFields, string stepFields)
    {
        using var repo = new TempRepo();
        var config = await repo.LoadAsync(commandFields, stepFields);
        Assert.NotNull(config.Commands?["build"]);
    }

    [Theory]
    [InlineData("\"user\":\" \",", "\"run\":\"echo hello\"")]
    [InlineData("", "\"run\":\"echo hello\",\"user\":\"\"")]
    [InlineData("", "\"run\":\"echo hello\",\"domain\":\" \"")]
    [InlineData("", "\"run\":\"echo hello\",\"passwordSecret\":\"\"")]
    [InlineData("", "\"run\":\"echo hello\",\"container\":{\"image\":\"alpine\",\"user\":\" \"}")]
    [InlineData("", "\"uses\":\"builtin:validate\",\"user\":\"builder\"")]
    [InlineData("", "\"command\":\"child\",\"passwordSecret\":\"login\"")]
    [InlineData("", "\"uses\":\"builtin:validate\",\"domain\":\".\"")]
    [InlineData("", "\"run\":\"echo hello\",\"password\":\"literal-not-allowed\"")]
    public async Task SchemaRejectsInvalidExecutionUsers(string commandFields, string stepFields)
    {
        using var repo = new TempRepo();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.LoadAsync(commandFields, stepFields));
    }

    [Theory]
    [InlineData("append")]
    [InlineData("prepend")]
    [InlineData("wrap")]
    public async Task CommandCompositionKeepsUsersOnOriginatingSteps(string mode)
    {
        using var repo = new TempRepo();
        await File.WriteAllTextAsync(Path.Join(repo.Root, "base.json"), """
            {"$schema":"rexo.schema.json","schemaVersion":"1.0","name":"base",
             "commands":{"build":{"user":"base-user","domain":"BASE","passwordSecret":"baseLogin",
               "before":[{"id":"before","run":"echo before"}],
               "steps":[{"id":"base","run":"echo base"}],"after":[{"id":"after","run":"echo after"}]}}}
            """);
        var config = await repo.LoadAsync(
            $"\"merge\":\"{mode}\",\"user\":\"child-user\",\"passwordSecret\":\"childLogin\",",
            "\"id\":\"child\",\"run\":\"echo child\"",
            "\"extends\":[\"base.json\"],");
        var steps = config.Commands!["build"].Steps;
        Assert.Equal(4, steps.Count);
        foreach (var step in steps.Where(s => s.Id != "child"))
        {
            Assert.Equal("base-user", step.User);
            Assert.Equal("BASE", step.Domain);
            Assert.Equal("baseLogin", step.PasswordSecret);
        }

        var child = Assert.Single(steps, s => s.Id == "child");
        Assert.Equal("child-user", child.User);
        Assert.Null(child.Domain);
        Assert.Equal("childLogin", child.PasswordSecret);
    }

    [Fact]
    public async Task StepOperationsDoNotApplyChildUserToPreviouslyNativeSteps()
    {
        using var repo = new TempRepo();
        await File.WriteAllTextAsync(Path.Join(repo.Root, "base.json"), """
            {"$schema":"rexo.schema.json","schemaVersion":"1.0","name":"base",
             "commands":{"build":{"steps":[{"id":"base","run":"echo base"}]}}}
            """);
        var config = await repo.LoadAsync(
            """
            "user":"child","passwordSecret":"childLogin","merge":{"steps":{"append":[{"id":"added","run":"echo added"}]}},
            """,
            "\"id\":\"unused\",\"run\":\"echo unused\"",
            "\"extends\":[\"base.json\"],");
        var command = config.Commands!["build"];
        Assert.Null(command.User);
        Assert.Null(command.PasswordSecret);
        Assert.Null(Assert.Single(command.Steps, s => s.Id == "base").User);
        Assert.Equal("child", Assert.Single(command.Steps, s => s.Id == "added").User);
    }

    private sealed class TempRepo : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), $"rexo-user-config-{Guid.NewGuid():N}");

        public TempRepo() => Directory.CreateDirectory(Root);

        public async Task<RepoConfig> LoadAsync(string commandFields, string stepFields, string extra = "")
        {
            var path = Path.Join(Root, "rexo.json");
            await File.WriteAllTextAsync(path, $$$$"""
                {"$schema":"rexo.schema.json","schemaVersion":"1.0","name":"users",{{{{extra}}}}
                 "commands":{"build":{ {{{{commandFields}}}} "steps":[{ {{{{stepFields}}}} }]}}}
                """);
            return await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
