namespace Rexo.Execution;

using System.Text.Json;
using Rexo.Configuration.Models;
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

    public ConfigSecretResolver(
        RepoConfig config,
        IReadOnlyDictionary<string, string> fileEnvironment,
        SecretProviderRegistry providers)
    {
        _config = config;
        _fileEnvironment = fileEnvironment;
        _providers = providers;
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

        var providerType = ResolveProviderType(item);
        var result = providerType switch
        {
            "env" => ResolveFromEnvironment(name, item, providerType),
            _ => await ResolveFromProviderAsync(name, item, providerType, cachePolicy, cancellationToken),
        };

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

    private async Task<SecretResolution> ResolveFromProviderAsync(
        string name,
        RepoSecretConfig item,
        string providerType,
        SecretCachePolicy cachePolicy,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryResolve(providerType, out var provider) || provider is null)
        {
            return ResolveUnsupportedProvider(name, item, providerType);
        }

        var providerConfig = ResolveProvider(item);
        var request = new SecretRequest(
            Name: name,
            Provider: providerType,
            Selector: item.Selector ?? item.Env ?? name,
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

    private RepoSecretProviderConfig? ResolveProvider(RepoSecretConfig item)
    {
        if (string.IsNullOrWhiteSpace(item.ProviderRef) || _config.Secrets?.Providers is not { Count: > 0 } providers)
        {
            return null;
        }

        return providers.TryGetValue(item.ProviderRef, out var provider) ? provider : null;
    }

    private SecretResolution ResolveFromEnvironment(string name, RepoSecretConfig item, string providerType)
    {
        var envName = item.Env ?? item.Selector ?? name;
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
