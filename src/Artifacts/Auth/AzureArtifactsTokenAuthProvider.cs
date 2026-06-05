namespace Rexo.Artifacts.Auth;

internal sealed class AzureArtifactsTokenAuthProvider : IFeedAuthProvider
{
    public bool TryResolve(FeedAuthProviderContext context, out FeedAuthResolution resolution)
    {
        resolution = new FeedAuthResolution(false, null, null, context.Endpoint, null, "none");

        if (!context.CiInferenceEnabled)
        {
            return false;
        }

        var canUseToken = FeedAuthConventions.IsAzureArtifactsEndpoint(context.Endpoint)
            || (context.AllowWhenEndpointUnknown && string.IsNullOrWhiteSpace(context.Endpoint));

        if (!canUseToken)
        {
            return false;
        }

        var token = FeedAuthResolver.GetEnv("SYSTEM_ACCESSTOKEN", context.FileEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        resolution = new FeedAuthResolution(true, context.UsernameHint, token, context.Endpoint, null, "ci-token");
        return true;
    }
}
