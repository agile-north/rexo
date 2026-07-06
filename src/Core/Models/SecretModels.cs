namespace Rexo.Core.Models;

/// <summary>
/// Canonical secret request used by providers and resolver pipelines.
/// </summary>
public sealed record SecretRequest(
    string Name,
    string Provider,
    string? Selector,
    bool Required,
    SecretCachePolicy CachePolicy,
    IReadOnlyDictionary<string, string>? Auth = null,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>
/// Resolved secret payload returned by providers.
/// </summary>
public sealed record SecretResolution(
    string Name,
    bool Success,
    string? Value,
    string Provider,
    string Source,
    string? Error = null,
    bool Cached = false);

/// <summary>
/// Non-sensitive metadata surfaced for diagnostics and explainability.
/// </summary>
public sealed record SecretResolutionMetadata(
    string Provider,
    string Source,
    bool Required,
    bool Cached,
    string? Mapping = null);

/// <summary>
/// Cache policy for a secret provider request.
/// </summary>
public sealed record SecretCachePolicy(
    bool Enabled,
    TimeSpan? Ttl = null)
{
    public static SecretCachePolicy Disabled { get; } = new(false, null);
}

public sealed record SecretPreflightResult(
    bool Success,
    IReadOnlyList<RexoError> Errors,
    IReadOnlyDictionary<string, string> ResolvedValues,
    IReadOnlyDictionary<string, SecretResolutionMetadata> Metadata,
    IReadOnlyDictionary<string, string> MappedEnvironment);
