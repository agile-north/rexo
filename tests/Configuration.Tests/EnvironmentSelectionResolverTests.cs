namespace Rexo.Configuration.Tests;

using Rexo.Configuration;
using Rexo.Configuration.Models;

public sealed class EnvironmentSelectionResolverTests
{
    [Fact]
    public void ResolveSelectsOnlyEnvironmentMatchingSourceBranch()
    {
        var result = EnvironmentSelectionResolver.Resolve(
            CreateConfig(),
            explicitEnvironment: null,
            environmentVariableEnvironment: null,
            sourceBranch: "feature/login",
            branchSource: "local-git",
            isPullRequest: false);

        Assert.True(result.IsResolved);
        Assert.Equal("development", result.EnvironmentName);
        Assert.Equal("branch", result.SelectionSource);
        Assert.Equal("feature/**", result.MatchedPattern);
    }

    [Fact]
    public void ResolveReportsAmbiguousAutomaticMatches()
    {
        var config = CreateConfig();
        config.Environments!["qa"] = new RepoEnvironmentConfig
        {
            Selection = new RepoEnvironmentBranchSelectionConfig { Branches = ["feature/**"] },
        };

        var result = EnvironmentSelectionResolver.Resolve(
            config,
            explicitEnvironment: null,
            environmentVariableEnvironment: null,
            sourceBranch: "feature/login",
            branchSource: "local-git",
            isPullRequest: false);

        Assert.False(result.IsResolved);
        Assert.NotNull(result.Error);
        Assert.Equal(["development", "qa"], result.MatchingEnvironments.Select(match => match.EnvironmentName));
    }

    [Fact]
    public void ResolveAppliesExplicitPrecedenceAndReportsOverride()
    {
        var result = EnvironmentSelectionResolver.Resolve(
            CreateConfig(),
            explicitEnvironment: "production",
            environmentVariableEnvironment: "development",
            sourceBranch: "feature/login",
            branchSource: "local-git",
            isPullRequest: false);

        Assert.True(result.IsResolved);
        Assert.True(result.IsExplicit);
        Assert.True(result.OverrodeAutomaticSelection);
        Assert.Equal("production", result.EnvironmentName);
        Assert.Equal("cli", result.SelectionSource);
    }

    [Fact]
    public void ResolveDoesNotAutomaticallySelectForPullRequest()
    {
        var automatic = EnvironmentSelectionResolver.Resolve(
            CreateConfig(),
            explicitEnvironment: null,
            environmentVariableEnvironment: null,
            sourceBranch: "feature/login",
            branchSource: "github-actions",
            isPullRequest: true);
        var explicitSelection = EnvironmentSelectionResolver.Resolve(
            CreateConfig(),
            explicitEnvironment: null,
            environmentVariableEnvironment: "development",
            sourceBranch: null,
            branchSource: "unresolved",
            isPullRequest: true);

        Assert.False(automatic.IsResolved);
        Assert.Equal("development", explicitSelection.EnvironmentName);
        Assert.True(explicitSelection.IsPullRequest);
    }

    [Theory]
    [InlineData("refs/heads/feature/login", "feature/login")]
    [InlineData("feature/login", "feature/login")]
    [InlineData("refs/tags/v1.0", null)]
    [InlineData("refs/pull/42/merge", null)]
    [InlineData("HEAD", null)]
    public void BranchRefNormalizerAcceptsOnlyBranchRefs(string value, string? expected)
    {
        var isValid = BranchRefNormalizer.TryNormalize(value, out var actual);

        Assert.Equal(expected is not null, isValid);
        Assert.Equal(expected, actual);
    }

    private static RepoConfig CreateConfig() =>
        new("test", new Dictionary<string, RepoCommandConfig>(), new Dictionary<string, string>())
        {
            Environments = new Dictionary<string, RepoEnvironmentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["development"] = new()
                {
                    Selection = new RepoEnvironmentBranchSelectionConfig { Branches = ["feature/**"] },
                },
                ["production"] = new()
                {
                    Selection = new RepoEnvironmentBranchSelectionConfig { Branches = ["main"] },
                },
            },
        };
}
