namespace Rexo.Cli;

using System.Text.Json;
using Rexo.Configuration;

internal sealed record ConfigLayerSnapshot(string Reference, JsonElement Document);

internal static class ConfigProvenanceReader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static async Task<IReadOnlyList<ConfigLayerSnapshot>> ReadRepositoryLayersAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        var layers = new List<ConfigLayerSnapshot>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        await ReadLayerAsync(configPath, visited, layers, cancellationToken);

        var overlayPath = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        if (!string.IsNullOrWhiteSpace(overlayPath))
        {
            var configDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
            var resolvedOverlayPath = Path.IsPathRooted(overlayPath)
                ? Path.GetFullPath(overlayPath)
                : Path.GetFullPath(Path.Join(configDirectory, overlayPath));
            if (File.Exists(resolvedOverlayPath))
            {
                await ReadLayerAsync(
                    resolvedOverlayPath,
                    visited,
                    layers,
                    cancellationToken,
                    resolveExtends: false);
            }
        }

        return layers;
    }

    private static async Task ReadLayerAsync(
        string path,
        HashSet<string> visited,
        List<ConfigLayerSnapshot> layers,
        CancellationToken cancellationToken,
        bool resolveExtends = true)
    {
        var fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var json = YamlJsonConverter.IsYamlPath(fullPath)
            ? YamlJsonConverter.ToJson(content, fullPath)
            : content;
        using var document = JsonDocument.Parse(json, JsonOptions);
        var extendsProperty = resolveExtends && document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals("extends", StringComparison.OrdinalIgnoreCase))
                .Value
            : default;
        if (extendsProperty.ValueKind == JsonValueKind.Array)
        {
            var configDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            foreach (var reference in extendsProperty.EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var baseReference = reference.GetString();
                if (string.IsNullOrWhiteSpace(baseReference) ||
                    baseReference.StartsWith("embedded:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var basePath = Path.IsPathRooted(baseReference)
                    ? baseReference
                    : Path.GetFullPath(Path.Join(configDirectory, baseReference));
                if (File.Exists(basePath))
                {
                    await ReadLayerAsync(basePath, visited, layers, cancellationToken);
                }
            }
        }

        layers.Add(new ConfigLayerSnapshot(fullPath, document.RootElement.Clone()));
    }
}
