namespace Rexo.Artifacts.Auth;

internal sealed record FeedAuthProviderContext(
    string? Endpoint,
    IReadOnlyDictionary<string, string> FileEnv,
    bool CiInferenceEnabled,
    string? UsernameHint = null,
    string? PackageHostFragment = null,
    bool AllowWhenEndpointUnknown = false);
