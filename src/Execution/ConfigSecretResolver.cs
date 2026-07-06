namespace Rexo.Execution;

using System.Text.Json;
using Rexo.Configuration.Models;
using Rexo.Ci;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;
using Rexo.Execution.Secrets;

internal sealed class ConfigSecretResolver : ISecretResolver
{
    private readonly RepoConfig _config;
    private readonly IReadOnlyDictionary<string, string> _fileEnvironment;
    private readonly SecretProviderRegistry _providers;
    private readonly Dictionary<string, SecretResolution> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _resolvedValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SecretResolutionMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _mappedEnvironment = new(StringComparer.Ordinal);
    private readonly CiInfo _ciInfo;

    private readonly record struct SecretRouteSelection(RepoSecretProviderRouteConfig Route, bool IsImplicitEnvironmentFallback);

    public ConfigSecretResolver(
        RepoConfig config,
        IReadOnlyDictionary<string, string> fileEnvironment,
        SecretProviderRegistry providers,
        CiInfo? ciInfo = null)
    {
        _config = config;
        _fileEnvironment = fileEnvironment;
        _providers = providers;
        _ciInfo = ciInfo ?? CiDetector.Detect();
    }

    public IReadOnlyDictionary<string, string> ResolvedValues => _resolvedValues;

    public IReadOnlyDictionary<string, SecretResolutionMetadata> Metadata => _metadata;

    public IReadOnlyDictionary<string, string> MappedEnvironment => _mappedEnvironment;

    public async Task<SecretPreflightResult> PreflightRequiredAsync(CancellationToken cancellationToken)
    {
        var errors = new List<RexoError>();
        var items = _config.Secrets?.Items;
        if (items is null || items.Count == 0)
        {
            return new SecretPreflightResult(
                true,
                errors,
                _resolvedValues,
                _metadata,
                _mappedEnvironment);
        }

        foreach (var (name, definition) in items)
        {
            if (!IsRequired(definition))
            {
                continue;
            }

            var resolution = await ResolveInternalAsync(name, definition, cancellationToken);
            if (!resolution.Success)
            {
                errors.Add(new RexoError(ErrorCodes.SecretResolutionFailed, resolution.Error ?? $"Secret '{name}' could not be resolved.")
                {
                    Source = $"secrets.items.{name}",
                    SuggestedFix = "Check provider/auth configuration and ensure required secret references are available.",
                });
            }
        }

        // Warm optional template-visible secrets so {{secrets.*}} is available without forcing strict failures.
        foreach (var (name, definition) in items)
        {
            if (IsRequired(definition) || definition.ExposeInTemplates == false)
            {
                continue;
            }

            _ = await ResolveInternalAsync(name, definition, cancellationToken);
        }

        return new SecretPreflightResult(
            errors.Count == 0,
            errors,
            _resolvedValues,
            _metadata,
            _mappedEnvironment);
    }

    public async Task<string?> GetSecretValueAsync(string name, CancellationToken cancellationToken)
    {
        if (_resolvedValues.TryGetValue(name, out var value))
        {
            return value;
        }

        var item = GetSecretItem(name);
        if (item is null)
        {
            return null;
        }

        var resolution = await ResolveInternalAsync(name, item, cancellationToken);
        return resolution.Success ? resolution.Value : null;
    }

    private RepoSecretConfig? GetSecretItem(string name)
    {
        if (_config.Secrets?.Items is not { Count: > 0 } items)
        {
            return null;
        }

        return items.TryGetValue(name, out var value) ? value : null;
    }

    private async Task<SecretResolution> ResolveInternalAsync(
        string name,
        RepoSecretConfig item,
        CancellationToken cancellationToken)
    {
        var cachePolicy = ResolveCachePolicy(item);
        if (cachePolicy.Enabled && _cache.TryGetValue(name, out var cached))
        {
            if (!IsCacheExpired(cached, cachePolicy))
            {
                return cached;
            }
        }

        var result = await ResolveFromCandidateChainAsync(name, item, cachePolicy, cancellationToken);

        if (result.Success)
        {
            var exposeInTemplates = item.ExposeInTemplates ?? true;
            if (exposeInTemplates && result.Value is not null)
            {
                _resolvedValues[name] = result.Value;
            }

            if (!string.IsNullOrWhiteSpace(item.MapToEnv) && result.Value is not null)
            {
                _mappedEnvironment[item.MapToEnv] = result.Value;
            }

            _metadata[name] = new SecretResolutionMetadata(
                result.Provider,
                result.Source,
                IsRequired(item),
                result.Cached,
                item.MapToEnv);
        }

        if (cachePolicy.Enabled)
        {
            _cache[name] = result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<SecretResolution> ResolveFromCandidateChainAsync(
        string name,
        RepoSecretConfig item,
        SecretCachePolicy cachePolicy,
        CancellationToken cancellationToken)
    {
        var candidates = BuildCandidates(item).ToList();
        if (candidates.Count == 0)
        {
            return new SecretResolution(
                name,
                false,
                null,
                ResolveProviderType(item),
                "none",
                GetFallbackErrorMessage(name, item),
                false);
        }

        SecretResolution? firstFailure = null;
        SecretResolution? lastFailure = null;
        foreach (var candidate in candidates)
        {
            if (!IsCandidateApplicable(candidate.Route.Runtime))
            {
                continue;
            }

            var providerType = ResolveCandidateProviderType(candidate.Route);
            if (string.IsNullOrWhiteSpace(providerType))
            {
                var missingProvider = new SecretResolution(
                    name,
                    false,
                    null,
                    candidate.Route.ProviderRef ?? candidate.Route.Provider ?? "unknown",
                    "none",
                    $"Secret provider reference '{candidate.Route.ProviderRef ?? candidate.Route.Provider ?? "unknown"}' could not be resolved.",
                    false);

                if (ShouldStopOnFirstError(item))
                {
                    return missingProvider;
                }

                firstFailure ??= missingProvider;
                lastFailure = missingProvider;

                continue;
            }

            var selector = ResolveCandidateSelector(candidate.Route, item);
            var envName = ResolveCandidateEnv(candidate.Route, item);

            var result = providerType.Equals("env", StringComparison.OrdinalIgnoreCase)
                ? ResolveFromEnvironment(name, item, providerType, envName, selector)
                : await ResolveFromProviderAsync(name, item, providerType, cachePolicy, cancellationToken, candidate.Route.ProviderRef, selector);

            if (result.Success)
            {
                return result;
            }

            firstFailure ??= result;
            lastFailure = result;

            if (ShouldStopOnFirstError(item))
            {
                return result;
            }

            if (candidate.IsImplicitEnvironmentFallback && firstFailure is not null)
            {
                return firstFailure;
            }
        }

        if (lastFailure is not null)
        {
            return lastFailure;
        }

        return new SecretResolution(
            name,
            false,
            null,
            ResolveProviderType(item),
            "none",
            GetFallbackErrorMessage(name, item),
            false);
    }

    private async Task<SecretResolution> ResolveFromProviderAsync(
        string name,
        RepoSecretConfig item,
        string providerType,
        SecretCachePolicy cachePolicy,
        CancellationToken cancellationToken,
        string? providerRefOverride = null,
        string? selectorOverride = null)
    {
        var provider = ResolveProvider(providerType, providerRefOverride);
        if (provider is null)
        {
            return ResolveUnsupportedProvider(name, item, providerType);
        }

        var providerConfig = ResolveProvider(item, providerRefOverride);
        var request = new SecretRequest(
            Name: name,
            Provider: providerType,
            Selector: selectorOverride ?? item.Selector ?? item.Env ?? name,
            Required: IsRequired(item),
            CachePolicy: cachePolicy,
            Auth: providerConfig?.Auth,
            Settings: MergeProviderSettings(providerConfig?.Settings, item.Settings));

        var resolution = await provider.ResolveAsync(request, cancellationToken);
        if (!resolution.Success)
        {
            return resolution;
        }

        return resolution with { Cached = cachePolicy.Enabled };
    }

    private ISecretProvider? ResolveProvider(string providerType, string? providerRefOverride)
    {
        if (!string.IsNullOrWhiteSpace(providerRefOverride) &&
            _config.Secrets?.Providers is { Count: > 0 } providers &&
            providers.TryGetValue(providerRefOverride, out var providerConfig) &&
            !string.IsNullOrWhiteSpace(providerConfig.Type) &&
            _providers.TryResolve(providerConfig.Type, out var providerByRef) &&
            providerByRef is not null)
        {
            return providerByRef;
        }

        return _providers.TryResolve(providerType, out var provider) ? provider : null;
    }

    private SecretCachePolicy ResolveCachePolicy(RepoSecretConfig item)
    {
        var defaults = _config.Secrets?.Defaults?.Cache;
        var providerDefaults = ResolveProvider(item)?.Cache;
        var itemCache = item.Cache;

        var enabled = itemCache?.Enabled
            ?? providerDefaults?.Enabled
            ?? defaults?.Enabled
            ?? false;

        var ttlSeconds = itemCache?.TtlSeconds
            ?? providerDefaults?.TtlSeconds
            ?? defaults?.TtlSeconds;

        TimeSpan? ttl = ttlSeconds is > 0
            ? TimeSpan.FromSeconds(ttlSeconds.Value)
            : null;

        return new SecretCachePolicy(enabled, ttl);
    }

    private static bool IsCacheExpired(SecretResolution resolution, SecretCachePolicy cachePolicy)
    {
        if (!cachePolicy.Enabled || cachePolicy.Ttl is null)
        {
            return false;
        }

        // Cache entries are command-scope only. Expiry checks are currently no-op until timestamps are tracked.
        return false;
    }

    private string ResolveProviderType(RepoSecretConfig item)
    {
        if (!string.IsNullOrWhiteSpace(item.Provider))
        {
            return item.Provider;
        }

        var provider = ResolveProvider(item);
        if (!string.IsNullOrWhiteSpace(provider?.Type))
        {
            return provider.Type!;
        }

        if (!string.IsNullOrWhiteSpace(_config.Secrets?.Defaults?.Provider))
        {
            return _config.Secrets.Defaults.Provider!;
        }

        return "env";
    }

    private RepoSecretProviderConfig? ResolveProvider(RepoSecretConfig item, string? providerRefOverride = null)
    {
        var providerRef = providerRefOverride ?? item.ProviderRef;
        if (string.IsNullOrWhiteSpace(providerRef) || _config.Secrets?.Providers is not { Count: > 0 } providers)
        {
            return null;
        }

        return providers.TryGetValue(providerRef, out var provider) ? provider : null;
    }

    private IEnumerable<SecretRouteSelection> BuildCandidates(RepoSecretConfig item)
    {
        if (!string.IsNullOrWhiteSpace(item.Provider) || !string.IsNullOrWhiteSpace(item.ProviderRef))
        {
            yield return new SecretRouteSelection(
                new RepoSecretProviderRouteConfig
                {
                    Provider = item.Provider,
                    ProviderRef = item.ProviderRef,
                }, false);

            if (ShouldFallbackToEnvironment(item))
            {
                yield return new SecretRouteSelection(new RepoSecretProviderRouteConfig { Provider = "env" }, true);
            }

            yield break;
        }

        if (item.ProviderChain is { Count: > 0 })
        {
            foreach (var route in item.ProviderChain)
            {
                yield return new SecretRouteSelection(route, false);
            }

            if (ShouldFallbackToEnvironment(item))
            {
                yield return new SecretRouteSelection(new RepoSecretProviderRouteConfig { Provider = "env" }, true);
            }

            yield break;
        }

        var defaults = _config.Secrets?.Defaults;
        if (defaults?.ProviderChain is { Count: > 0 })
        {
            foreach (var route in defaults.ProviderChain)
            {
                yield return new SecretRouteSelection(route, false);
            }

            if (ShouldFallbackToEnvironment(item))
            {
                yield return new SecretRouteSelection(new RepoSecretProviderRouteConfig { Provider = "env" }, true);
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(defaults?.Provider))
        {
            yield return new SecretRouteSelection(new RepoSecretProviderRouteConfig { Provider = defaults.Provider }, false);
            if (ShouldFallbackToEnvironment(item))
            {
                yield return new SecretRouteSelection(new RepoSecretProviderRouteConfig { Provider = "env" }, true);
            }

            yield break;
        }

        if (ShouldFallbackToEnvironment(item))
        {
            yield return new SecretRouteSelection(new RepoSecretProviderRouteConfig { Provider = "env" }, true);
        }
    }

    private string? ResolveCandidateProviderType(RepoSecretProviderRouteConfig candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Provider))
        {
            return candidate.Provider;
        }

        if (string.IsNullOrWhiteSpace(candidate.ProviderRef) || _config.Secrets?.Providers is not { Count: > 0 } providers)
        {
            return null;
        }

        return providers.TryGetValue(candidate.ProviderRef, out var provider) ? provider.Type : null;
    }

    private static string? ResolveCandidateSelector(RepoSecretProviderRouteConfig candidate, RepoSecretConfig item)
    {
        return candidate.Selector ?? item.Selector ?? item.Env ?? item.Provider ?? item.ProviderRef;
    }

    private static string? ResolveCandidateEnv(RepoSecretProviderRouteConfig candidate, RepoSecretConfig item)
    {
        return candidate.Env ?? item.Env ?? candidate.Selector ?? item.Selector;
    }

    private bool IsCandidateApplicable(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime) || string.Equals(runtime, "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(runtime, "ci", StringComparison.OrdinalIgnoreCase))
        {
            return _ciInfo.IsCi;
        }

        if (string.Equals(runtime, "local", StringComparison.OrdinalIgnoreCase))
        {
            return !_ciInfo.IsCi;
        }

        return string.Equals(runtime, _ciInfo.Provider, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldStopOnFirstError(RepoSecretConfig item) => item.StopOnFirstError
        ?? _config.Secrets?.Defaults?.StopOnFirstError
        ?? false;

    private bool ShouldFallbackToEnvironment(RepoSecretConfig item) => item.FallbackToEnvironment
        ?? _config.Secrets?.Defaults?.FallbackToEnvironment
        ?? true;

    private string GetFallbackErrorMessage(string name, RepoSecretConfig item)
    {
        return ShouldStopOnFirstError(item)
            ? $"Secret '{name}' could not be resolved because the selected provider failed."
            : $"Secret '{name}' could not be resolved from any configured provider.";
    }

    private SecretResolution ResolveFromEnvironment(string name, RepoSecretConfig item, string providerType, string? envOverride = null, string? selectorOverride = null)
    {
        var envName = envOverride ?? selectorOverride ?? item.Env ?? item.Selector ?? name;
        var fromProcess = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(fromProcess))
        {
            return new SecretResolution(name, true, fromProcess, providerType, "env", null, false);
        }

        if (_fileEnvironment.TryGetValue(envName, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
        {
            return new SecretResolution(name, true, fromFile, providerType, "file-env", null, false);
        }

        return new SecretResolution(
            name,
            false,
            null,
            providerType,
            "none",
            $"Required environment secret '{envName}' for '{name}' is missing.",
            false);
    }

    private static SecretResolution ResolveUnsupportedProvider(string name, RepoSecretConfig item, string providerType)
    {
        var selector = item.Selector ?? item.Env ?? name;
        return new SecretResolution(
            name,
            false,
            null,
            providerType,
            "none",
            $"Secret provider '{providerType}' is not implemented yet for selector '{selector}'.",
            false);
    }

    private static IReadOnlyDictionary<string, string>? MergeProviderSettings(
        Dictionary<string, JsonElement>? providerSettings,
        Dictionary<string, JsonElement>? itemSettings)
    {
        if ((providerSettings is null || providerSettings.Count == 0) &&
            (itemSettings is null || itemSettings.Count == 0))
        {
            return null;
        }

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (providerSettings is { Count: > 0 })
        {
            foreach (var (key, value) in providerSettings)
            {
                merged[key] = JsonElementToString(value);
            }
        }

        if (itemSettings is { Count: > 0 })
        {
            foreach (var (key, value) in itemSettings)
            {
                merged[key] = JsonElementToString(value);
            }
        }

        return merged;
    }

    private static string JsonElementToString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            _ => element.ToString(),
        };

    private bool IsRequired(RepoSecretConfig item) => item.Required
        ?? _config.Secrets?.Defaults?.Required
        ?? true;
}
