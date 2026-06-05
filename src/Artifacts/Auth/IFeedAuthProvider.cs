namespace Rexo.Artifacts.Auth;

internal interface IFeedAuthProvider
{
    bool TryResolve(FeedAuthProviderContext context, out FeedAuthResolution resolution);
}
