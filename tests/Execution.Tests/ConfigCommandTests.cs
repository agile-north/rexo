namespace Rexo.Execution.Tests;

using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Execution;

/// <summary>
/// Tests for the config resolved / config sources built-in commands.
/// </summary>
public sealed class ConfigCommandTests
{
    private static CommandInvocation EmptyInvocation() =>
        new(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: Environment.CurrentDirectory);

    [Fact]
    public async Task ConfigResolvedReturnsSuccessWithJsonOutput()
    {
        var config = new RepoConfig(
            "my-repo",
            new Dictionary<string, RepoCommandConfig>
            {
                ["build"] = new RepoCommandConfig("Build the project", new Dictionary<string, RepoOptionConfig>(), [])
            },
            new Dictionary<string, string>());

        var registry = BuiltinCommandRegistration.CreateDefault(config: config);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("config resolved", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("my-repo", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigResolvedReturnsFailureWhenNoConfigLoaded()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("config resolved", EmptyInvocation(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ConfigSourcesReturnsSuccessWithConfigPath()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "rexo.json");

        var registry = BuiltinCommandRegistration.CreateDefault(configPath: configPath);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("config sources", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("rexo.json", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigSourcesSucceedsWithNullConfigPath()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("config sources", EmptyInvocation(), CancellationToken.None);

        // Without a configPath the loader attempts to resolve from working directory — result is still success
        Assert.True(result.Success);
        Assert.Contains("Configuration sources", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveArtifactNamePreservesExplicitEmptyName()
    {
        var config = new RepoConfig("root-name", null, null);

        Assert.Equal("root-name", ConfigCommandLoader.ResolveArtifactName(new RepoArtifactConfig("docker"), config));
        Assert.Equal(string.Empty, ConfigCommandLoader.ResolveArtifactName(new RepoArtifactConfig("docker", string.Empty), config));
    }
}
