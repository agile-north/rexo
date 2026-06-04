namespace Rexo.Ci;

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rexo.Core.Models;

public static class CiOutputEmitter
{
    private const string DefaultPrefix = "REXO_";
    private const int DefaultMaxValueLength = 8192;
    private const int DefaultMaxVariables = 1000;
    private static readonly JsonSerializerOptions FullManifestJsonOptions = new() { WriteIndented = false };

    public static CiEmissionPayload BuildPayload(RunManifest manifest, CiEmissionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var effectiveOptions = options ?? CiEmissionOptions.Default;
        var scope = ResolveScope(effectiveOptions.Scope);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        var source = scope.Mode.Equals("full", StringComparison.OrdinalIgnoreCase)
            ? FlattenFullManifest(manifest)
            : BuildSafeManifest(manifest, effectiveOptions.IncludeStepOutputs);

        if (scope.Include.Count > 0 || scope.Exclude.Count > 0)
        {
            source = source.Where(item => MatchesScope(item.Key, scope)).ToArray();
        }

        foreach (var (path, value) in source)
        {
            if (variables.Count >= effectiveOptions.MaxVariables)
            {
                warnings.Add($"CI emission truncated after {effectiveOptions.MaxVariables} variables.");
                break;
            }

            var key = NormalizeKey(path, effectiveOptions.KeyCasing, effectiveOptions.Prefix);
            var emittedValue = NormalizeValue(value, effectiveOptions.Redact, effectiveOptions.MaxValueLength);

            if (!effectiveOptions.EmitEmptyValues && string.IsNullOrEmpty(emittedValue))
            {
                continue;
            }

            if (!variables.TryAdd(key, emittedValue))
            {
                var suffix = 2;
                var collisionKey = $"{key}_{suffix}";
                while (variables.ContainsKey(collisionKey))
                {
                    suffix++;
                    collisionKey = $"{key}_{suffix}";
                }

                warnings.Add($"CI variable key collision for '{key}'. Emitting '{collisionKey}' instead.");
                variables[collisionKey] = emittedValue;
            }
        }

        return new CiEmissionPayload(manifest, effectiveOptions, variables, warnings);
    }

    public static IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return CiOutputDialectRegistry.Resolve(payload.Options.Provider).FormatStdoutLines(payload);
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> BuildSafeManifest(RunManifest manifest, bool includeStepOutputs)
    {
        var items = new List<KeyValuePair<string, object?>>(32)
        {
            new("schemaVersion", manifest.SchemaVersion),
            new("toolVersion", manifest.ToolVersion),
            new("repoName", manifest.RepoName),
            new("repoRoot", manifest.RepoRoot),
            new("branch", manifest.Branch),
            new("commitSha", manifest.CommitSha),
            new("remoteUrl", manifest.RemoteUrl),
            new("ciProvider", manifest.CiProvider),
            new("isCi", manifest.IsCi),
            new("ciBuildId", manifest.CiBuildId),
            new("ciRunNumber", manifest.CiRunNumber),
            new("ciWorkflowName", manifest.CiWorkflowName),
            new("ciActor", manifest.CiActor),
            new("ciTag", manifest.CiTag),
            new("ciBuildUrl", manifest.CiBuildUrl),
            new("commandExecuted", manifest.CommandExecuted),
            new("success", manifest.Success),
            new("exitCode", manifest.ExitCode),
            new("startedAt", manifest.StartedAt),
            new("completedAt", manifest.CompletedAt),
            new("durationMs", manifest.Duration.TotalMilliseconds),
            new("configHash", manifest.ConfigHash),
            new("assemblyVersion", manifest.AssemblyVersion),
            new("informationalVersion", manifest.InformationalVersion),
            new("nugetVersion", manifest.NuGetVersion),
            new("stepsCount", manifest.Steps.Count),
            new("stepsSucceeded", manifest.Steps.Count(step => step.Success)),
            new("stepsFailed", manifest.Steps.Count(step => !step.Success)),
            new("artifactsCount", manifest.Artifacts.Count),
            new("pushDecisionsCount", manifest.PushDecisions.Count),
            new("warningsCount", manifest.Warnings.Count),
            new("errorsCount", manifest.Errors.Count),
        };

        AppendVersionFields(items, manifest.Version);

        if (includeStepOutputs)
        {
            foreach (var step in manifest.Steps)
            {
                AppendStepFileOutputsFields(items, step);
            }
        }

        return items;
    }

    private static void AppendVersionFields(ICollection<KeyValuePair<string, object?>> items, VersionResult? version)
    {
        if (version is null)
        {
            return;
        }

        items.Add(new KeyValuePair<string, object?>("version.semVer", version.SemVer));
        items.Add(new KeyValuePair<string, object?>("version.major", version.Major));
        items.Add(new KeyValuePair<string, object?>("version.minor", version.Minor));
        items.Add(new KeyValuePair<string, object?>("version.patch", version.Patch));
        items.Add(new KeyValuePair<string, object?>("version.preRelease", version.PreRelease));
        items.Add(new KeyValuePair<string, object?>("version.commitSha", version.CommitSha));
        items.Add(new KeyValuePair<string, object?>("version.shortSha", version.ShortSha));
        items.Add(new KeyValuePair<string, object?>("version.isPreRelease", version.IsPreRelease));
        items.Add(new KeyValuePair<string, object?>("version.isStable", version.IsStable));
        items.Add(new KeyValuePair<string, object?>("version.buildMetadata", version.BuildMetadata));
        items.Add(new KeyValuePair<string, object?>("version.branch", version.Branch));
        items.Add(new KeyValuePair<string, object?>("version.nugetVersion", version.NuGetVersion));
        items.Add(new KeyValuePair<string, object?>("version.dockerVersion", version.DockerVersion));
        items.Add(new KeyValuePair<string, object?>("version.preReleaseTag", version.PreReleaseTag));
        items.Add(new KeyValuePair<string, object?>("version.preReleaseLabel", version.PreReleaseLabel));
        items.Add(new KeyValuePair<string, object?>("version.preReleaseNumber", version.PreReleaseNumber));
        items.Add(new KeyValuePair<string, object?>("version.preReleaseLabelWithDash", version.PreReleaseLabelWithDash));
        items.Add(new KeyValuePair<string, object?>("version.preReleaseTagWithDash", version.PreReleaseTagWithDash));
        items.Add(new KeyValuePair<string, object?>("version.assemblyVersion", version.AssemblyVersion));
        items.Add(new KeyValuePair<string, object?>("version.fileVersion", version.FileVersion));
        items.Add(new KeyValuePair<string, object?>("version.informationalVersion", version.InformationalVersion));
        items.Add(new KeyValuePair<string, object?>("version.commitsSinceVersionSource", version.CommitsSinceVersionSource));
        items.Add(new KeyValuePair<string, object?>("version.weightedPreReleaseNumber", version.WeightedPreReleaseNumber));
    }

    private static void AppendStepFileOutputsFields(ICollection<KeyValuePair<string, object?>> items, StepManifestEntry step)
    {
        var basePath = $"steps.{step.StepId}.fileOutputs";
        foreach (var fileOutput in step.FileOutputs)
        {
            var outputName = fileOutput.Key;
            var values = fileOutput.Value;

            items.Add(new KeyValuePair<string, object?>($"{basePath}.{outputName}.count", values.Count));

            for (var index = 0; index < values.Count; index++)
            {
                items.Add(new KeyValuePair<string, object?>($"{basePath}.{outputName}[{index}]", values[index]));
            }
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> FlattenFullManifest(RunManifest manifest)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(manifest, FullManifestJsonOptions));

        var items = new List<KeyValuePair<string, object?>>();
        FlattenJsonElement(document.RootElement, string.Empty, items);
        return items;
    }

    private static void FlattenJsonElement(JsonElement element, string path, ICollection<KeyValuePair<string, object?>> items)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var nextPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                    FlattenJsonElement(property.Value, nextPath, items);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenJsonElement(item, $"{path}[{index}]", items);
                    index++;
                }
                break;
            case JsonValueKind.String:
                items.Add(new KeyValuePair<string, object?>(path, element.GetString()));
                break;
            case JsonValueKind.Number:
                items.Add(new KeyValuePair<string, object?>(path, element.GetRawText()));
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                items.Add(new KeyValuePair<string, object?>(path, element.GetBoolean()));
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                items.Add(new KeyValuePair<string, object?>(path, null));
                break;
            default:
                items.Add(new KeyValuePair<string, object?>(path, element.GetRawText()));
                break;
        }
    }

    private static CiEmissionScopeOptions ResolveScope(JsonElement? scopeElement)
    {
        if (scopeElement is null || scopeElement.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return CiEmissionScopeOptions.Safe;
        }

        return scopeElement.Value.ValueKind switch
        {
            JsonValueKind.String => CiEmissionScopeOptions.FromPreset(scopeElement.Value.GetString()),
            JsonValueKind.Object => CiEmissionScopeOptions.FromSelector(
                ReadOptionalString(scopeElement.Value, "mode"),
                ReadStringList(scopeElement.Value, "include"),
                ReadStringList(scopeElement.Value, "exclude")),
            _ => throw new JsonException("outputs.ci.scope must be 'safe', 'full', or an object containing mode/include/exclude masks."),
        };
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"outputs.ci.scope.{propertyName} must be an array of strings.");
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"outputs.ci.scope.{propertyName} must contain strings only.");
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.GetString();
    }

    private static bool MatchesScope(string path, CiEmissionScopeOptions scope)
    {
        var canonicalPath = CanonicalizePath(path);

        if (scope.Include.Count > 0 && !scope.Include.Any(mask => MatchesMask(canonicalPath, mask)))
        {
            return false;
        }

        return scope.Exclude.Count == 0 || !scope.Exclude.Any(mask => MatchesMask(canonicalPath, mask));
    }

    private static bool MatchesMask(string canonicalPath, string mask)
    {
        if (string.IsNullOrWhiteSpace(mask))
        {
            return false;
        }

        if (mask.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = mask[6..];
            return Regex.IsMatch(canonicalPath, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (mask.Contains('*', StringComparison.Ordinal) || mask.Contains('?', StringComparison.Ordinal))
        {
            var pattern = WildcardToRegex(CanonicalizePath(mask));
            return Regex.IsMatch(canonicalPath, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return string.Equals(canonicalPath, CanonicalizePath(mask), StringComparison.OrdinalIgnoreCase);
    }

    private static string WildcardToRegex(string mask)
    {
        var pattern = Regex.Escape(mask)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);

        return $"^{pattern}$";
    }

    private static string CanonicalizePath(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        var previousWasWord = false;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (builder.Length > 0 && char.IsUpper(c) && previousWasWord)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
                previousWasWord = true;
                continue;
            }

            builder.Append(c);
            previousWasWord = false;
        }

        return builder.ToString();
    }

    private static string NormalizeKey(string path, string casing, string? prefix)
    {
        prefix = string.IsNullOrWhiteSpace(prefix) ? DefaultPrefix : prefix;
        var normalized = casing.ToLowerInvariant() switch
        {
            "lowersnake" => ToSnakeCase(path, lower: true),
            "kebab" => ToDelimitedCase(path, '-').ToLowerInvariant(),
            "camel" => ToCamelCase(path),
            "pascal" => ToPascalCase(path),
            _ => ToSnakeCase(path, lower: false),
        };

        return prefix + normalized;
    }

    private static string ToSnakeCase(string value, bool lower)
    {
        var builder = new StringBuilder(value.Length + 8);
        var previousWasSeparator = false;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (builder.Length > 0 && char.IsUpper(c) && !previousWasSeparator)
                {
                    builder.Append('_');
                }

                builder.Append(lower ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static string ToDelimitedCase(string value, char delimiter)
    {
        var builder = new StringBuilder(value.Length + 8);
        var previousWasSeparator = false;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (builder.Length > 0 && char.IsUpper(c) && !previousWasSeparator)
                {
                    builder.Append(delimiter);
                }

                builder.Append(char.ToLowerInvariant(c));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append(delimiter);
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim(delimiter);
    }

    private static string ToCamelCase(string value)
    {
        var pascal = ToPascalCase(value);
        return string.IsNullOrEmpty(pascal) ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    private static string ToPascalCase(string value)
    {
        var words = SplitWords(value);
        var builder = new StringBuilder(value.Length + 8);

        foreach (var word in words)
        {
            if (word.Length == 0)
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1)
            {
                builder.Append(word[1..].ToLowerInvariant());
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> SplitWords(string value)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (current.Length > 0 && char.IsUpper(c) && char.IsLower(current[^1]))
            {
                words.Add(current.ToString());
                current.Clear();
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static string NormalizeValue(object? value, bool redact, int maxValueLength)
    {
        var raw = value switch
        {
            null => string.Empty,
            bool booleanValue => booleanValue ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant(),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.TotalMilliseconds.ToString(CultureInfo.InvariantCulture),
            Enum enumValue => enumValue.ToString(),
            JsonElement element => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => element.GetRawText(),
            },
            _ => value.ToString() ?? string.Empty,
        };

        if (redact && LooksSensitive(raw))
        {
            raw = "***";
        }

        if (raw.Length > maxValueLength)
        {
            return raw[..maxValueLength];
        }

        return raw;
    }

    private static bool LooksSensitive(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var lowered = value.ToLowerInvariant();
        return lowered.Contains("token", StringComparison.Ordinal) ||
               lowered.Contains("secret", StringComparison.Ordinal) ||
               lowered.Contains("password", StringComparison.Ordinal) ||
               lowered.Contains("apikey", StringComparison.Ordinal) ||
               lowered.Contains("connectionstring", StringComparison.Ordinal) ||
               lowered.Contains("key", StringComparison.Ordinal);
    }

}

public sealed record CiEmissionOptions
{
    public static CiEmissionOptions Default { get; } = new();

    public string Provider { get; init; } = "generic";
    public string Prefix { get; init; } = "REXO_";
    public string KeyCasing { get; init; } = "upperSnake";
    public JsonElement? Scope { get; init; }
    public bool IncludeStepOutputs { get; init; }
    public bool EmitEmptyValues { get; init; }
    public bool Redact { get; init; } = true;
    public bool FailOnError { get; init; }
    public int MaxValueLength { get; init; } = 8192;
    public int MaxVariables { get; init; } = 1000;
}

public sealed record CiEmissionScopeOptions(
    string Mode,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude)
{
    public static CiEmissionScopeOptions Safe { get; } = new("safe", [], []);

    public static CiEmissionScopeOptions Full { get; } = new("full", [], []);

    public static CiEmissionScopeOptions FromPreset(string? preset) =>
        string.Equals(preset, "full", StringComparison.OrdinalIgnoreCase) ? Full : Safe;

    public static CiEmissionScopeOptions FromSelector(string? mode, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(mode)
            ? (include.Count > 0 ? "full" : "safe")
            : mode;

        return new CiEmissionScopeOptions(effectiveMode, include, exclude);
    }
}

public sealed record CiEmissionPayload(
    RunManifest Manifest,
    CiEmissionOptions Options,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string> Warnings);
