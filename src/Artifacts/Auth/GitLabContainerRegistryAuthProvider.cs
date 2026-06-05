namespace Rexo.Artifacts.Auth;

internal sealed class GitLabContainerRegistryAuthProvider : IFeedAuthProvider
{
    public bool TryResolve(FeedAuthProviderContext context, out FeedAuthResolution resolution)
    {
        resolution = new FeedAuthResolution(false, null, null, context.Endpoint, null, "none");

        if (!context.CiInferenceEnabled)
        {
            return false;
        }

        var host = FeedAuthConventions.ExtractRegistryHost(context.Endpoint);
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var gitLabRegistry = FeedAuthConventions.ExtractRegistryHost(FeedAuthResolver.GetEnv("CI_REGISTRY", context.FileEnv));
        var looksLikeGitLabRegistry = string.Equals(host, FeedAuthConventions.GitLabCloudRegistryHost, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(gitLabRegistry)
                && string.Equals(host, gitLabRegistry, StringComparison.OrdinalIgnoreCase));

        if (!looksLikeGitLabRegistry)
        {
            return false;
        }

        var username = FeedAuthResolver.GetEnv("CI_REGISTRY_USER", context.FileEnv) ?? "gitlab-ci-token";
        var token = FeedAuthResolver.GetEnv("CI_REGISTRY_PASSWORD", context.FileEnv) ?? FeedAuthResolver.GetEnv("CI_JOB_TOKEN", context.FileEnv);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        resolution = new FeedAuthResolution(true, username, token, context.Endpoint, null, "gitlab-ci-token");
        return true;
    }
}
