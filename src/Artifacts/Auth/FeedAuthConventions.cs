namespace Rexo.Artifacts.Auth;

internal static class FeedAuthConventions
{
    internal const string GitHubContainerRegistryHost = "ghcr.io";
    internal const string GitLabCloudRegistryHost = "registry.gitlab.com";

    internal static bool ShouldWarnOnMissingContainerRegistryCredentials(
        string? endpoint,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var host = ExtractRegistryHost(endpoint);
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, GitHubContainerRegistryHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, GitLabCloudRegistryHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var gitLabRegistry = ExtractRegistryHost(FeedAuthResolver.GetEnv("CI_REGISTRY", fileEnv));
        return !string.IsNullOrWhiteSpace(gitLabRegistry)
            && string.Equals(host, gitLabRegistry, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ExtractRegistryHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var normalized = endpoint.Trim();
        if (normalized.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["oci://".Length..];
        }
        else if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://".Length..];
        }
        else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["http://".Length..];
        }

        var slashIndex = normalized.IndexOf('/', StringComparison.Ordinal);
        return slashIndex >= 0
            ? normalized[..slashIndex]
            : normalized;
    }

    internal static bool IsAzureArtifactsEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        return endpoint.Contains("pkgs.dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || endpoint.Contains(".pkgs.visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsGitLabPackageEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        return endpoint.Contains("/api/v4/projects/", StringComparison.OrdinalIgnoreCase)
            && endpoint.Contains("/packages/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ResolveImplicitContainerRegistry(
        IReadOnlyDictionary<string, string> fileEnv,
        bool ciInferenceEnabled)
    {
        if (!ciInferenceEnabled)
        {
            return null;
        }

        var githubActions = FeedAuthResolver.GetEnv("GITHUB_ACTIONS", fileEnv);
        if (string.Equals(githubActions, "true", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubContainerRegistryHost;
        }

        return FeedAuthResolver.GetEnv("CI_REGISTRY", fileEnv);
    }
}
