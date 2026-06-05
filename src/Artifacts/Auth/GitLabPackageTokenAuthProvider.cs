namespace Rexo.Artifacts.Auth;

internal sealed class GitLabPackageTokenAuthProvider : IFeedAuthProvider
{
    public bool TryResolve(FeedAuthProviderContext context, out FeedAuthResolution resolution)
    {
        resolution = new FeedAuthResolution(false, null, null, context.Endpoint, null, "none");

        if (!context.CiInferenceEnabled)
        {
            return false;
        }

        if (!FeedAuthConventions.IsGitLabPackageEndpoint(context.Endpoint))
        {
            return false;
        }

        var token = FeedAuthResolver.GetEnv("CI_JOB_TOKEN", context.FileEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var username = string.IsNullOrWhiteSpace(context.UsernameHint) ? "gitlab-ci-token" : context.UsernameHint;
        resolution = new FeedAuthResolution(true, username, token, context.Endpoint, null, "gitlab-ci-token");
        return true;
    }
}
