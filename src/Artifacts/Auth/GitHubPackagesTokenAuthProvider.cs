namespace Rexo.Artifacts.Auth;

internal sealed class GitHubPackagesTokenAuthProvider : IFeedAuthProvider
{
    public bool TryResolve(FeedAuthProviderContext context, out FeedAuthResolution resolution)
    {
        resolution = new FeedAuthResolution(false, null, null, context.Endpoint, null, "none");

        if (!context.CiInferenceEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.Endpoint)
            || string.IsNullOrWhiteSpace(context.PackageHostFragment)
            || !context.Endpoint.Contains(context.PackageHostFragment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = FeedAuthResolver.GetEnv("GITHUB_TOKEN", context.FileEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        resolution = new FeedAuthResolution(true, null, token, context.Endpoint, null, "github-token");
        return true;
    }
}
