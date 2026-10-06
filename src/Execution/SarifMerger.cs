namespace Rexo.Execution;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Merges SARIF 2.1.0 logs into a single log containing one run per tool, which is the shape
/// GitHub code scanning requires (it rejects multiple runs with the same tool and category).
/// </summary>
internal static class SarifMerger
{
    internal const string SarifVersion = "2.1.0";
    internal const string SarifSchema = "https://json.schemastore.org/sarif-2.1.0.json";
    internal const string SourceRootBaseId = "%SRCROOT%";

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    internal sealed record MergeResult(
        int InputFiles,
        int Runs,
        int Results,
        bool Written,
        IReadOnlyList<string> Warnings);

    public static async Task<MergeResult> MergeAsync(
        IReadOnlyList<string> inputFiles,
        string outputFile,
        string? category,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var runsByTool = new Dictionary<string, MergedRun>(StringComparer.Ordinal);
        var toolOrder = new List<string>();
        var rootUri = BuildDirectoryUri(repositoryRoot);
        var outputFullPath = Path.GetFullPath(outputFile);

        foreach (var file in inputFiles)
        {
            if (string.Equals(Path.GetFullPath(file), outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonNode? document;
            try
            {
                var text = await File.ReadAllTextAsync(file, cancellationToken);
                document = JsonNode.Parse(text);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                warnings.Add($"Skipping '{file}': {ex.Message}");
                continue;
            }

            var version = document?["version"]?.GetValue<string>();
            if (!string.Equals(version, SarifVersion, StringComparison.Ordinal))
            {
                warnings.Add($"Skipping '{file}': SARIF version '{version ?? "<missing>"}' is not {SarifVersion}.");
                continue;
            }

            if (document?["runs"] is not JsonArray runs)
            {
                continue;
            }

            foreach (var run in runs.OfType<JsonObject>())
            {
                var toolName = run["tool"]?["driver"]?["name"]?.GetValue<string>() ?? "unknown";
                if (!runsByTool.TryGetValue(toolName, out var merged))
                {
                    merged = new MergedRun(run);
                    runsByTool[toolName] = merged;
                    toolOrder.Add(toolName);
                }

                merged.Add(run, rootUri);
            }
        }

        if (toolOrder.Count == 0)
        {
            if (File.Exists(outputFullPath))
            {
                File.Delete(outputFullPath);
            }

            return new MergeResult(inputFiles.Count, 0, 0, false, warnings);
        }

        var outputRuns = new JsonArray();
        var totalResults = 0;
        foreach (var toolName in toolOrder)
        {
            var merged = runsByTool[toolName];
            totalResults += merged.ResultCount;
            outputRuns.Add(merged.Build(category, rootUri));
        }

        var output = new JsonObject
        {
            ["$schema"] = SarifSchema,
            ["version"] = SarifVersion,
            ["runs"] = outputRuns,
        };

        var directory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputFullPath, output.ToJsonString(IndentedOptions), cancellationToken);
        return new MergeResult(inputFiles.Count, outputRuns.Count, totalResults, true, warnings);
    }

    private static string BuildDirectoryUri(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.EndsWith(Path.DirectorySeparatorChar) && !full.EndsWith(Path.AltDirectorySeparatorChar))
        {
            full += Path.DirectorySeparatorChar;
        }

        return new Uri(full).AbsoluteUri;
    }

    private sealed class MergedRun
    {
        private readonly JsonObject _template;
        private readonly JsonArray _rules = [];
        private readonly Dictionary<string, int> _ruleIndexById = new(StringComparer.Ordinal);
        private readonly JsonArray _results = [];
        private readonly HashSet<string> _resultKeys = new(StringComparer.Ordinal);
        private readonly JsonArray _artifacts = [];
        private readonly JsonArray _invocations = [];
        private bool _usedSourceRoot;

        public MergedRun(JsonObject firstRun)
        {
            _template = firstRun;
        }

        public int ResultCount => _results.Count;

        public void Add(JsonObject run, string rootUri)
        {
            var sourceRules = run["tool"]?["driver"]?["rules"] as JsonArray;
            var ruleIndexMap = new Dictionary<int, int>();
            if (sourceRules is not null)
            {
                for (var i = 0; i < sourceRules.Count; i++)
                {
                    if (sourceRules[i] is not JsonObject rule)
                    {
                        continue;
                    }

                    var id = rule["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    if (!_ruleIndexById.TryGetValue(id, out var mergedIndex))
                    {
                        mergedIndex = _rules.Count;
                        _rules.Add(rule.DeepClone());
                        _ruleIndexById[id] = mergedIndex;
                    }

                    ruleIndexMap[i] = mergedIndex;
                }
            }

            var artifactOffset = _artifacts.Count;
            if (run["artifacts"] is JsonArray artifacts)
            {
                foreach (var artifact in artifacts)
                {
                    var clone = artifact?.DeepClone();
                    if (clone is not null)
                    {
                        _usedSourceRoot |= RewriteLocations(clone, 0, rootUri);
                    }

                    _artifacts.Add(clone);
                }
            }

            if (run["invocations"] is JsonArray invocations)
            {
                foreach (var invocation in invocations)
                {
                    _invocations.Add(invocation?.DeepClone());
                }
            }

            if (run["results"] is not JsonArray results)
            {
                return;
            }

            foreach (var result in results.OfType<JsonObject>())
            {
                var clone = (JsonObject)result.DeepClone();
                RemapRuleIndex(clone, ruleIndexMap);
                _usedSourceRoot |= RewriteLocations(clone, artifactOffset, rootUri);

                if (_resultKeys.Add(clone.ToJsonString()))
                {
                    _results.Add(clone);
                }
            }
        }

        public JsonObject Build(string? category, string rootUri)
        {
            var run = new JsonObject();

            var tool = _template["tool"]?.DeepClone() as JsonObject ?? new JsonObject();
            var driver = tool["driver"] as JsonObject ?? new JsonObject();
            driver.Remove("rules");
            driver["rules"] = _rules.DeepClone();
            tool["driver"] = driver;
            run["tool"] = tool;

            if (_invocations.Count > 0)
            {
                run["invocations"] = _invocations.DeepClone();
            }

            if (_usedSourceRoot)
            {
                run["originalUriBaseIds"] = new JsonObject
                {
                    [SourceRootBaseId] = new JsonObject { ["uri"] = rootUri },
                };
            }

            if (_artifacts.Count > 0)
            {
                run["artifacts"] = _artifacts.DeepClone();
            }

            run["results"] = _results.DeepClone();

            if (_template["columnKind"] is JsonNode columnKind)
            {
                run["columnKind"] = columnKind.DeepClone();
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                var trimmed = category.Trim();
                run["automationDetails"] = new JsonObject
                {
                    ["id"] = trimmed.EndsWith('/') ? trimmed : trimmed + "/",
                };
            }
            else if (_template["automationDetails"] is JsonNode automationDetails)
            {
                run["automationDetails"] = automationDetails.DeepClone();
            }

            return run;
        }

        private void RemapRuleIndex(JsonObject result, Dictionary<int, int> ruleIndexMap)
        {
            var ruleId = result["ruleId"]?.GetValue<string>();

            if (result["ruleIndex"] is JsonValue ruleIndexValue && ruleIndexValue.TryGetValue<int>(out var originalIndex))
            {
                if (ruleIndexMap.TryGetValue(originalIndex, out var mapped))
                {
                    result["ruleIndex"] = mapped;
                }
                else if (ruleId is not null && _ruleIndexById.TryGetValue(ruleId, out var byId))
                {
                    result["ruleIndex"] = byId;
                }
                else
                {
                    result.Remove("ruleIndex");
                }
            }

            if (result["rule"] is JsonObject ruleReference &&
                ruleReference["index"] is JsonValue referenceIndexValue &&
                referenceIndexValue.TryGetValue<int>(out var referenceIndex))
            {
                if (ruleIndexMap.TryGetValue(referenceIndex, out var mapped))
                {
                    ruleReference["index"] = mapped;
                }
                else
                {
                    ruleReference.Remove("index");
                }
            }
        }

        private static bool RewriteLocations(JsonNode node, int artifactOffset, string rootUri)
        {
            var usedSourceRoot = false;

            switch (node)
            {
                case JsonObject obj:
                    if (obj["artifactLocation"] is JsonObject artifactLocation)
                    {
                        usedSourceRoot |= RewriteArtifactLocation(artifactLocation, artifactOffset, rootUri);
                    }

                    if (obj["location"] is JsonObject location && location["uri"] is not null && obj["artifactLocation"] is null)
                    {
                        // artifact objects use "location" instead of "artifactLocation"
                        usedSourceRoot |= RewriteArtifactLocation(location, 0, rootUri);
                    }

                    foreach (var (key, child) in obj.ToList())
                    {
                        if (child is not null && key is not "artifactLocation")
                        {
                            usedSourceRoot |= RewriteLocations(child, artifactOffset, rootUri);
                        }
                    }

                    break;

                case JsonArray array:
                    foreach (var child in array)
                    {
                        if (child is not null)
                        {
                            usedSourceRoot |= RewriteLocations(child, artifactOffset, rootUri);
                        }
                    }

                    break;
            }

            return usedSourceRoot;
        }

        private static bool RewriteArtifactLocation(JsonObject artifactLocation, int artifactOffset, string rootUri)
        {
            if (artifactOffset > 0 &&
                artifactLocation["index"] is JsonValue indexValue &&
                indexValue.TryGetValue<int>(out var index))
            {
                artifactLocation["index"] = index + artifactOffset;
            }

            if (artifactLocation["uriBaseId"] is not null ||
                artifactLocation["uri"] is not JsonValue uriValue ||
                !uriValue.TryGetValue<string>(out var uri) ||
                !uri.StartsWith(rootUri, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            artifactLocation["uri"] = uri[rootUri.Length..];
            artifactLocation["uriBaseId"] = SourceRootBaseId;
            return true;
        }
    }
}
