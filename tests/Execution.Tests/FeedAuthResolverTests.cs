namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts;

[Collection("EnvVar Mutation Sequential")]
public sealed class FeedAuthResolverTests
{
    [Fact]
    public void ResolveGitHubPackagesTokenAuthReturnsGithubTokenForNuGetHost()
    {
        const string githubTokenVar = "GITHUB_TOKEN";
        var original = Environment.GetEnvironmentVariable(githubTokenVar);
        Environment.SetEnvironmentVariable(githubTokenVar, "gh-token");

        try
        {
            var resolution = FeedAuthResolver.ResolveGitHubPackagesTokenAuth(
                endpoint: "https://nuget.pkg.github.com/acme/index.json",
                fileEnv: new Dictionary<string, string>(StringComparer.Ordinal),
                packageHostFragment: "nuget.pkg.github.com");

            Assert.True(resolution.HasCredentials);
            Assert.Equal("gh-token", resolution.Secret);
            Assert.Equal("github-token", resolution.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(githubTokenVar, original);
        }
    }

    [Fact]
    public void ResolveAzureArtifactsTokenAuthReturnsSystemAccessTokenForAzureArtifactsEndpoint()
    {
        const string systemAccessTokenVar = "SYSTEM_ACCESSTOKEN";
        var original = Environment.GetEnvironmentVariable(systemAccessTokenVar);
        Environment.SetEnvironmentVariable(systemAccessTokenVar, "az-token");

        try
        {
            var resolution = FeedAuthResolver.ResolveAzureArtifactsTokenAuth(
                endpoint: "https://pkgs.dev.azure.com/org/project/_packaging/feed/pypi/upload",
                fileEnv: new Dictionary<string, string>(StringComparer.Ordinal),
                username: "VssSessionToken");

            Assert.True(resolution.HasCredentials);
            Assert.Equal("VssSessionToken", resolution.Username);
            Assert.Equal("az-token", resolution.Secret);
            Assert.Equal("ci-token", resolution.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(systemAccessTokenVar, original);
        }
    }

    [Fact]
    public void ResolveAzureArtifactsTokenAuthSupportsUnknownEndpointWhenAllowed()
    {
        const string systemAccessTokenVar = "SYSTEM_ACCESSTOKEN";
        var original = Environment.GetEnvironmentVariable(systemAccessTokenVar);
        Environment.SetEnvironmentVariable(systemAccessTokenVar, "az-token");

        try
        {
            var resolution = FeedAuthResolver.ResolveAzureArtifactsTokenAuth(
                endpoint: null,
                fileEnv: new Dictionary<string, string>(StringComparer.Ordinal),
                username: "VssSessionToken",
                allowWhenEndpointUnknown: true);

            Assert.True(resolution.HasCredentials);
            Assert.Equal("VssSessionToken", resolution.Username);
            Assert.Equal("az-token", resolution.Secret);
            Assert.Equal("ci-token", resolution.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(systemAccessTokenVar, original);
        }
    }

    [Fact]
    public void ResolveGitLabPackageTokenAuthReturnsCiJobTokenForGitLabPackageEndpoint()
    {
        const string ciJobTokenVar = "CI_JOB_TOKEN";
        var original = Environment.GetEnvironmentVariable(ciJobTokenVar);
        Environment.SetEnvironmentVariable(ciJobTokenVar, "gl-token");

        try
        {
            var resolution = FeedAuthResolver.ResolveGitLabPackageTokenAuth(
                endpoint: "https://gitlab.com/api/v4/projects/123/packages/nuget/index.json",
                fileEnv: new Dictionary<string, string>(StringComparer.Ordinal),
                username: null);

            Assert.True(resolution.HasCredentials);
            Assert.Equal("gitlab-ci-token", resolution.Username);
            Assert.Equal("gl-token", resolution.Secret);
            Assert.Equal("gitlab-ci-token", resolution.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ciJobTokenVar, original);
        }
    }

    [Fact]
    public void IsArtifactCiInferenceEnabledDefaultsToTrue()
    {
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}")!;

        Assert.True(FeedAuthResolver.IsArtifactCiInferenceEnabled(settings));
    }

    [Fact]
    public void IsArtifactCiInferenceEnabledHonorsFalseOverride()
    {
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {
              "target": {
                "ciInference": false
              }
            }
            """)!;

        Assert.False(FeedAuthResolver.IsArtifactCiInferenceEnabled(settings));
    }

    [Fact]
    public void ResolveDockerDoesNotUseGitHubCiTokenWhenCiInferenceDisabled()
    {
        const string githubActorVar = "GITHUB_ACTOR";
        const string githubTokenVar = "GITHUB_TOKEN";
        var originalActor = Environment.GetEnvironmentVariable(githubActorVar);
        var originalToken = Environment.GetEnvironmentVariable(githubTokenVar);
        Environment.SetEnvironmentVariable(githubActorVar, "copilot");
        Environment.SetEnvironmentVariable(githubTokenVar, "gh-token");

        try
        {
            var resolution = FeedAuthResolver.ResolveDocker(
                configuredRegistry: "ghcr.io",
                inferredRegistry: null,
                fileEnv: new Dictionary<string, string>(StringComparer.Ordinal),
                ciInferenceEnabled: false);

            Assert.False(resolution.HasCredentials);
            Assert.Equal("none", resolution.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(githubActorVar, originalActor);
            Environment.SetEnvironmentVariable(githubTokenVar, originalToken);
        }
    }
}
