namespace Rexo.Artifacts;

using System.Text.Json;
using Rexo.Artifacts.Auth;

/// <summary>
/// Resolved credentials from env vars, file env, or CI-native token sources.
/// </summary>
public sealed record FeedAuthResolution(
    bool HasCredentials,
    string? Username,
    string? Secret,
    string? Endpoint,
    string? Error,
    string Source);

/// <summary>
/// Shared auth infrastructure for cross-provider credential resolution.
/// Includes container registry auth, package feed CI-native fallback helpers,
/// and the <see cref="GetEnv"/> utility used by provider-specific resolvers.
/// </summary>
public static class FeedAuthResolver
{
    private static readonly IFeedAuthProvider[] ContainerRegistryAuthProviders =
    [
        new GitHubContainerRegistryAuthProvider(),
        new GitLabContainerRegistryAuthProvider(),
    ];

    private static readonly IFeedAuthProvider[] PackageTokenProviders =
    [
        new GitHubPackagesTokenAuthProvider(),
        new GitLabPackageTokenAuthProvider(),
        new AzureArtifactsTokenAuthProvider(),
    ];

    /// <summary>
    /// Resolves a target value using environment and settings indirection.
    /// Order: configured env-name (or default env-name) -> configured value.
    /// </summary>
    public static string? ResolveTargetValue(
        string defaultEnvName,
        string? configuredEnvName,
        string? configuredValue,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var envName = string.IsNullOrWhiteSpace(configuredEnvName)
            ? defaultEnvName
            : configuredEnvName;

        return GetEnv(envName, fileEnv) ?? configuredValue;
    }

    /// <summary>
    /// Resolves a secret/token value using environment and optional fallback aliases.
    /// Order: configured env-name (or default env-name) -> fallback env names.
    /// </summary>
    public static string? ResolveSecret(
        string defaultEnvName,
        string? configuredEnvName,
        IReadOnlyDictionary<string, string> fileEnv,
        params string[] fallbackEnvNames)
    {
        var envName = string.IsNullOrWhiteSpace(configuredEnvName)
            ? defaultEnvName
            : configuredEnvName;

        var resolved = GetEnv(envName, fileEnv);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        foreach (var fallbackEnvName in fallbackEnvNames)
        {
            resolved = GetEnv(fallbackEnvName, fileEnv);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    public static FeedAuthResolution ResolveDocker(
        string? configuredRegistry,
        string? inferredRegistry,
        IReadOnlyDictionary<string, string> fileEnv,
        string? configuredUsernameEnv = null,
        string? configuredPasswordEnv = null,
        string? configuredRegistryEnv = null,
        bool ciInferenceEnabled = true)
    {
        var username = ResolveSecret(
            defaultEnvName: "DOCKER_LOGIN_USERNAME",
            configuredEnvName: configuredUsernameEnv,
            fileEnv: fileEnv,
            "DOCKER_AUTH_USERNAME");
        var secret = ResolveSecret(
            defaultEnvName: "DOCKER_LOGIN_PASSWORD",
            configuredEnvName: configuredPasswordEnv,
            fileEnv: fileEnv,
            "DOCKER_AUTH_PASSWORD");
        var endpoint = ResolveTargetValue(
                           defaultEnvName: "DOCKER_LOGIN_REGISTRY",
                           configuredEnvName: configuredRegistryEnv,
                           configuredValue: configuredRegistry,
                           fileEnv: fileEnv)
                       ?? GetEnv("DOCKER_AUTH_REGISTRY", fileEnv)
                       ?? inferredRegistry;

        return FinalizeContainerRegistryAuth(
            username,
            secret,
            endpoint,
            fileEnv,
            "DOCKER_LOGIN_USERNAME and DOCKER_LOGIN_PASSWORD must both be set.",
            "Docker login registry could not be determined. Set settings.loginRegistry or DOCKER_LOGIN_REGISTRY.",
            ciInferenceEnabled);
    }

    public static FeedAuthResolution FinalizeContainerRegistryAuth(
        string? username,
        string? secret,
        string? endpoint,
        IReadOnlyDictionary<string, string> fileEnv,
        string missingCredentialsError,
        string missingEndpointError,
        bool ciInferenceEnabled = true)
    {
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(secret))
        {
            var ciResolution = TryResolveCiContainerRegistryAuth(endpoint, fileEnv, ciInferenceEnabled);
            if (ciResolution is not null)
            {
                return ciResolution;
            }

            return new FeedAuthResolution(false, null, null, endpoint, null, "none");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secret))
        {
            return new FeedAuthResolution(false, null, null, endpoint, missingCredentialsError, "env");
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new FeedAuthResolution(false, null, null, null, missingEndpointError, "env");
        }

        return new FeedAuthResolution(true, username, secret, endpoint, null, "env");
    }

    public static bool ShouldWarnOnMissingContainerRegistryCredentials(
        string? endpoint,
        IReadOnlyDictionary<string, string> fileEnv)
        => FeedAuthConventions.ShouldWarnOnMissingContainerRegistryCredentials(endpoint, fileEnv);

    public static string? ExtractRegistryHost(string? endpoint)
        => FeedAuthConventions.ExtractRegistryHost(endpoint);

    public static bool IsAzureArtifactsEndpoint(string? endpoint)
        => FeedAuthConventions.IsAzureArtifactsEndpoint(endpoint);

    public static FeedAuthResolution ResolveGitHubPackagesTokenAuth(
        string? endpoint,
        IReadOnlyDictionary<string, string> fileEnv,
        string packageHostFragment,
        bool ciInferenceEnabled = true)
    {
        var context = new FeedAuthProviderContext(
            Endpoint: endpoint,
            FileEnv: fileEnv,
            CiInferenceEnabled: ciInferenceEnabled,
            PackageHostFragment: packageHostFragment);

        return ResolveFromProvider<GitHubPackagesTokenAuthProvider>(context);
    }

    public static FeedAuthResolution ResolveAzureArtifactsTokenAuth(
        string? endpoint,
        IReadOnlyDictionary<string, string> fileEnv,
        string? username,
        bool allowWhenEndpointUnknown = false,
        bool ciInferenceEnabled = true)
    {
        var context = new FeedAuthProviderContext(
            Endpoint: endpoint,
            FileEnv: fileEnv,
            CiInferenceEnabled: ciInferenceEnabled,
            UsernameHint: username,
            AllowWhenEndpointUnknown: allowWhenEndpointUnknown);

        return ResolveFromProvider<AzureArtifactsTokenAuthProvider>(context);
    }

    public static bool IsGitLabPackageEndpoint(string? endpoint)
        => FeedAuthConventions.IsGitLabPackageEndpoint(endpoint);

    public static FeedAuthResolution ResolveGitLabPackageTokenAuth(
        string? endpoint,
        IReadOnlyDictionary<string, string> fileEnv,
        string? username = null,
        bool ciInferenceEnabled = true)
    {
        var context = new FeedAuthProviderContext(
            Endpoint: endpoint,
            FileEnv: fileEnv,
            CiInferenceEnabled: ciInferenceEnabled,
            UsernameHint: username);

        return ResolveFromProvider<GitLabPackageTokenAuthProvider>(context);
    }

    private static FeedAuthResolution? TryResolveCiContainerRegistryAuth(
        string? endpoint,
        IReadOnlyDictionary<string, string> fileEnv,
        bool ciInferenceEnabled)
    {
        var context = new FeedAuthProviderContext(endpoint, fileEnv, ciInferenceEnabled);
        foreach (var provider in ContainerRegistryAuthProviders)
        {
            if (provider.TryResolve(context, out var resolution))
            {
                return resolution;
            }
        }

        return null;
    }

    public static string? ResolveImplicitContainerRegistry(
        IReadOnlyDictionary<string, string> fileEnv,
        bool ciInferenceEnabled = true)
        => FeedAuthConventions.ResolveImplicitContainerRegistry(fileEnv, ciInferenceEnabled);

    private static FeedAuthResolution ResolveFromProvider<TProvider>(FeedAuthProviderContext context)
        where TProvider : IFeedAuthProvider
    {
        foreach (var provider in PackageTokenProviders)
        {
            if (provider is TProvider && provider.TryResolve(context, out var resolution))
            {
                return resolution;
            }
        }

        return new FeedAuthResolution(false, null, null, context.Endpoint, null, "none");
    }

    public static bool IsArtifactCiInferenceEnabled(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (TryGetBooleanSetting(settings, "ciInference", out var ciInference))
        {
            return ciInference;
        }

        if (TryGetBooleanSetting(settings, "target.ciInference", out ciInference))
        {
            return ciInference;
        }

        return true;
    }

    private static bool TryGetBooleanSetting(
        IReadOnlyDictionary<string, JsonElement> settings,
        string path,
        out bool value)
    {
        value = default;
        if (!TryGetSettingValue(settings, out var setting, path))
        {
            return false;
        }

        switch (setting.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(setting.GetString(), out value);
            default:
                return false;
        }
    }

    private static bool TryGetSettingValue(
        IReadOnlyDictionary<string, JsonElement> settings,
        out JsonElement value,
        string path)
    {
        value = default;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !settings.TryGetValue(segments[0], out value))
        {
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segments[i], out value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads <paramref name="key"/> from the process environment first, then falls back to
    /// <paramref name="fileEnv"/> (loaded from .env / repo env files).  Returns <c>null</c>
    /// when the key is absent or blank in both sources.
    /// </summary>
    public static string? GetEnv(string key, IReadOnlyDictionary<string, string> fileEnv)
    {
        var process = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(process))
        {
            return process;
        }

        return fileEnv.TryGetValue(key, out var fileValue) && !string.IsNullOrWhiteSpace(fileValue)
            ? fileValue
            : null;
    }
}
