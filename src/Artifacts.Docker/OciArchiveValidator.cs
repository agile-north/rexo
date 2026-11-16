namespace Rexo.Artifacts.Docker;

using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

internal static class OciArchiveValidator
{
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxManifests = 256;
    private const string ImageManifest = "application/vnd.oci.image.manifest.v1+json";
    private const string ImageIndex = "application/vnd.oci.image.index.v1+json";
    private const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private const string DockerIndex = "application/vnd.docker.distribution.manifest.list.v2+json";

    internal static async Task ValidateAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var tar = new TarReader(stream);
        byte[]? index = null;
        byte[]? layout = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var blobs = new Dictionary<string, long>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: cancellationToken)) is not null)
        {
            if (!names.Add(entry.Name))
            {
                throw new InvalidOperationException("OCI archive contains duplicate entries.");
            }
            if (entry.EntryType == TarEntryType.Directory)
            {
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                throw new InvalidOperationException("OCI archive contains a non-file entry.");
            }
            if (entry.Name is "index.json" or "oci-layout")
            {
                if (entry.Length > MaxMetadataBytes)
                {
                    throw new InvalidOperationException("OCI archive metadata is too large.");
                }
                using var buffer = new MemoryStream();
                await entry.DataStream.CopyToAsync(buffer, cancellationToken);
                if (entry.Name == "index.json") index = buffer.ToArray();
                else layout = buffer.ToArray();
            }
            else if (entry.Name.StartsWith("blobs/sha256/", StringComparison.Ordinal))
            {
                var name = entry.Name["blobs/sha256/".Length..];
                if (name.Length != 64 || !name.All(char.IsAsciiHexDigit) ||
                    !string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("OCI archive contains an invalid blob name.");
                }
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(entry.DataStream, cancellationToken));
                if (hash != name)
                {
                    throw new InvalidOperationException("OCI archive blob digest mismatch.");
                }
                blobs.Add("sha256:" + name, entry.Length);
            }
        }

        if (index is null || layout is null)
        {
            throw new InvalidOperationException("OCI archive is missing layout metadata.");
        }
        try
        {
            using var layoutDocument = JsonDocument.Parse(layout);
            using var indexDocument = JsonDocument.Parse(index);
            var layoutRoot = layoutDocument.RootElement;
            var indexRoot = indexDocument.RootElement;
            if (layoutRoot.ValueKind != JsonValueKind.Object ||
                !layoutRoot.TryGetProperty("imageLayoutVersion", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() != "1.0.0" ||
                indexRoot.ValueKind != JsonValueKind.Object ||
                !indexRoot.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 2 ||
                !indexRoot.TryGetProperty("manifests", out var manifests) ||
                manifests.ValueKind != JsonValueKind.Array || manifests.GetArrayLength() == 0 ||
                manifests.GetArrayLength() > MaxManifests)
            {
                throw new InvalidOperationException("OCI archive has invalid layout or index metadata.");
            }
            var pending = new Queue<(string Digest, string MediaType)>();
            foreach (var manifest in manifests.EnumerateArray())
            {
                pending.Enqueue(ReadManifestDescriptor(manifest, blobs));
            }
            var visited = new Dictionary<string, string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var descriptor))
            {
                if (visited.TryGetValue(descriptor.Digest, out var previousType))
                {
                    if (previousType != descriptor.MediaType)
                    {
                        throw new InvalidOperationException("OCI archive assigns conflicting types to a manifest.");
                    }
                    continue;
                }
                visited.Add(descriptor.Digest, descriptor.MediaType);
                if (visited.Count > MaxManifests)
                {
                    throw new InvalidOperationException("OCI archive contains too many manifests.");
                }
                var content = await ReadManifestAsync(path, descriptor.Digest, cancellationToken);
                using var manifestDocument = JsonDocument.Parse(content);
                var root = manifestDocument.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("schemaVersion", out var manifestSchema) ||
                    manifestSchema.ValueKind != JsonValueKind.Number ||
                    !manifestSchema.TryGetInt32(out var manifestSchemaVersion) || manifestSchemaVersion != 2)
                {
                    throw new InvalidOperationException("OCI archive contains an invalid manifest.");
                }
                if (descriptor.MediaType is ImageIndex or DockerIndex)
                {
                    if (!root.TryGetProperty("manifests", out var nested) ||
                        nested.ValueKind != JsonValueKind.Array || nested.GetArrayLength() == 0 ||
                        nested.GetArrayLength() > MaxManifests)
                    {
                        throw new InvalidOperationException("OCI archive contains an invalid nested index.");
                    }
                    foreach (var child in nested.EnumerateArray()) pending.Enqueue(ReadManifestDescriptor(child, blobs));
                }
                else
                {
                    if (!root.TryGetProperty("config", out var config) ||
                        !root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException("OCI archive contains an invalid image manifest.");
                    }
                    ValidateDescriptor(config, blobs);
                    foreach (var layer in layers.EnumerateArray()) ValidateDescriptor(layer, blobs);
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("OCI archive contains invalid JSON metadata.", ex);
        }
    }

    private static (string Digest, string MediaType) ReadManifestDescriptor(
        JsonElement element, IReadOnlyDictionary<string, long> blobs)
    {
        var digest = ValidateDescriptor(element, blobs);
        if (!element.TryGetProperty("mediaType", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() is not (ImageManifest or ImageIndex or DockerManifest or DockerIndex))
        {
            throw new InvalidOperationException("OCI archive contains an unsupported manifest media type.");
        }
        return (digest, type.GetString()!);
    }

    private static string ValidateDescriptor(JsonElement element, IReadOnlyDictionary<string, long> blobs)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            digest.GetString() is not { } value ||
            !element.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out var length) || !blobs.TryGetValue(value, out var actual) || length != actual)
        {
            throw new InvalidOperationException("OCI archive references a missing or invalid blob.");
        }
        return value;
    }

    private static async Task<byte[]> ReadManifestAsync(string path, string digest, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var tar = new TarReader(stream);
        TarEntry? entry;
        var name = "blobs/sha256/" + digest["sha256:".Length..];
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: cancellationToken)) is not null)
        {
            if (entry.Name != name) continue;
            if (entry.Length > MaxMetadataBytes || entry.DataStream is null)
            {
                throw new InvalidOperationException("OCI archive manifest is too large.");
            }
            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        throw new InvalidOperationException("OCI archive manifest is missing.");
    }
}
