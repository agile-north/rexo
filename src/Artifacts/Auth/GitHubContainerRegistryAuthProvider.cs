namespace Rexo.Artifacts.Auth;

internal sealed class GitHubContainerRegistryAuthProvider : IFeedAuthProvider
{
    public bool TryResolve(FeedAuthProviderContext context, out FeedAuthResolution resolution)
    {
        resolution = new FeedAuthResolution(false, null, null, context.Endpoint, null, "none");

        if (!context.CiInferenceEnabled)
        {
            return false;
        }

        var host = FeedAuthConventions.ExtractRegistryHost(context.Endpoint);
        if (!string.Equals(host, FeedAuthConventions.GitHubContainerRegistryHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actor = FeedAuthResolver.GetEnv("GITHUB_ACTOR", context.FileEnv);
        var token = FeedAuthResolver.GetEnv("GITHUB_TOKEN", context.FileEnv);
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        resolution = new FeedAuthResolution(true, actor, token, context.Endpoint, null, "github-token");
        return true;
    }
}
