namespace Rexo.Execution;

using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Uploads repository-relative report files using the Actions artifact v4 protocol.</summary>
/// <remarks>
/// Protocol reference: actions/toolkit@6444290c57f7d41b694d586da743d4281c81a1f1,
/// packages/artifact/src/internal/upload/upload-artifact.ts and
/// src/generated/results/api/v1/artifact.twirp-client.ts (@actions/artifact 2.3.3, version 4).
/// Normal run steps do not receive runtime credentials: compare NodeScriptActionHandler.cs
/// and ScriptHandler.cs in actions/runner@f4a774978d9562d6539be5055c17c0eb970d08df.
/// Run rx as a child of a trusted repository-local JavaScript action (no downloaded action
/// or package needed), or explicitly export its masked runtime credentials from such an
/// action. GITHUB_TOKEN is not an artifact runtime credential.
/// An injected client must have no default headers, credentials, cookies or automatic redirects.
/// </remarks>
internal static class ActionsReportArtifactUploader
{
    private const int BlockSize = 4 * 1024 * 1024;
    private const int MaximumBlocks = 50000;
    private const string ServicePath = "/twirp/github.actions.results.api.v1.ArtifactService/";
    private const string ArchivePrefix = ".rexo-actions-artifact-";

    public static async Task<string> UploadAsync(
        IReadOnlyList<string> files, string repositoryRoot, string name, int retentionDays,
        Func<string, string?> environment, CancellationToken cancellationToken, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateName(name);
        if (retentionDays < 0)
        {
            throw new InvalidOperationException("Artifact retention days must be nonnegative (zero uses the repository default).");
        }

        var token = environment("ACTIONS_RUNTIME_TOKEN");
        var endpoint = environment("ACTIONS_RESULTS_URL");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                "Actions artifact upload requires ACTIONS_RUNTIME_TOKEN and ACTIONS_RESULTS_URL. " +
                "Ordinary Actions run steps do not expose these by default; GITHUB_TOKEN cannot replace them. " +
                "Launch rx from a trusted JavaScript action, or explicitly export its masked runtime credentials.");
        }

        var resultsUrl = ValidateUrl(endpoint, blob: false);
        var (runId, jobId) = ReadBackendIds(token);
        var retentionLimit = environment("GITHUB_RETENTION_DAYS");
        if (!string.IsNullOrWhiteSpace(retentionLimit))
        {
            if (!int.TryParse(retentionLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) || maximum <= 0)
            {
                throw new InvalidOperationException("GITHUB_RETENTION_DAYS must be a positive integer.");
            }
            retentionDays = Math.Min(retentionDays, maximum);
        }
        string? expiresAt = null;
        if (retentionDays > 0)
        {
            try
            {
                expiresAt = DateTimeOffset.UtcNow.AddDays(retentionDays).ToString("O", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new InvalidOperationException("Artifact retention days exceed the supported date range.");
            }
        }

        if (client is not null && client.DefaultRequestHeaders.Any())
        {
            throw new InvalidOperationException("The artifact HTTP client must not have default headers; credentials must not reach blob storage.");
        }

        using var ownedClient = client is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
            : null;
        client ??= ownedClient!;
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                RejectLink(directory.FullName);
            }
            var inputs = ValidateFiles(files, root);
            // CreateNew + DeleteOnClose remove precisely this archive on success, failure and cancellation.
            var archivePath = Path.Join(root, $"{ArchivePrefix}{Guid.NewGuid():N}.zip");
            await using var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, entryName) in inputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateFilePath(root, entryName);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = zip.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                    await input.CopyToAsync(output, cancellationToken);
                }
            }
            if (archive.Length > (long)BlockSize * MaximumBlocks)
            {
                throw new InvalidOperationException("The report archive exceeds the supported Azure block blob size.");
            }
            archive.Position = 0;

            var create = new Dictionary<string, object>
            {
                ["workflow_run_backend_id"] = runId,
                ["workflow_job_run_backend_id"] = jobId,
                ["name"] = name,
                ["version"] = 4,
            };
            if (expiresAt is not null)
            {
                create["expires_at"] = expiresAt;
            }
            using var created = await RpcAsync(client, resultsUrl, token, "CreateArtifact", create, cancellationToken);
            var signedUrl = ReadString(created.RootElement, "signed_upload_url", "signedUploadUrl");
            var blobUrl = ValidateUrl(signedUrl, blob: true);
            var digest = await UploadBlocksAsync(client, blobUrl, archive, cancellationToken);
            var finalize = new Dictionary<string, object>
            {
                ["workflow_run_backend_id"] = runId,
                ["workflow_job_run_backend_id"] = jobId,
                ["name"] = name,
                ["size"] = archive.Length.ToString(CultureInfo.InvariantCulture),
                // google.protobuf.StringValue uses a JSON string, not {"value": "..."}.
                ["hash"] = $"sha256:{digest}",
            };
            using var finalized = await RpcAsync(client, resultsUrl, token, "FinalizeArtifact", finalize, cancellationToken);
            var id = ReadString(finalized.RootElement, "artifact_id", "artifactId");
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
            {
                throw new InvalidOperationException("FinalizeArtifact returned an invalid artifact ID.");
            }
            return id;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Actions artifact HTTP request timed out; the upload was not confirmed.");
        }
        catch (HttpRequestException)
        {
            // Never include HTTP exception text, response bodies or signed URLs in diagnostics.
            throw new InvalidOperationException("Actions artifact HTTP transport failed; the upload was not confirmed.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Actions artifact service returned invalid JSON.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("Actions artifact input or archive could not be accessed safely.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." ||
            name.Any(c => char.IsControl(c) || "\"<>|:*?\\/".Contains(c, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Artifact name must be 1–255 characters without control characters or \"<>|:*?\\/.");
        }
    }

    private static List<(string Path, string EntryName)> ValidateFiles(IReadOnlyList<string> files, string root)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("At least one report file is required for artifact upload.");
        }
        var inputs = new List<(string, string)>(files.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file) ||
                file.StartsWith('/') || file.StartsWith('\\'))
            {
                throw new InvalidOperationException("Artifact files must be repository-relative regular files.");
            }
            var entryName = file.Replace('\\', '/');
            if (!names.Add(entryName))
            {
                throw new InvalidOperationException("Artifact files must not contain duplicate archive paths.");
            }
            inputs.Add((ValidateFilePath(root, entryName), entryName));
        }
        return inputs;
    }

    private static string ValidateFilePath(string root, string entryName)
    {
        RejectLink(root);
        var path = root;
        var segments = entryName.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || IsDeviceName(segment) ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => char.IsControl(c) || "\"<>|:*?".Contains(c, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Artifact paths contain an unsafe or traversing component.");
            }
            path = Path.Join(path, segment);
            var attributes = RejectLink(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory != (index < segments.Length - 1))
            {
                throw new InvalidOperationException("Artifact inputs must be existing regular files, not directories.");
            }
        }
        return path;
    }

    private static bool IsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private static FileAttributes RejectLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidOperationException("Artifact paths must not contain symbolic links, reparse points or devices.");
        }
        return attributes;
    }

    private static (string RunId, string JobId) ReadBackendIds(string token)
    {
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3 || token.Length > 65536 || token.Any(char.IsWhiteSpace))
            {
                throw new FormatException();
            }
            var encoded = segments[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(encoded));
            if (!payload.RootElement.TryGetProperty("scp", out var claim) || claim.ValueKind != JsonValueKind.String)
            {
                throw new FormatException();
            }
            foreach (var parts in claim.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(scope => scope.Split(':')))
            {
                if (parts.Length == 3 && parts[0] == "Actions.Results" &&
                    Guid.TryParse(parts[1], out _) && Guid.TryParse(parts[2], out _))
                {
                    return (parts[1], parts[2]);
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            // JWT decoding extracts backend IDs only; the service authenticates the token.
            throw new InvalidOperationException("ACTIONS_RUNTIME_TOKEN lacks valid Actions.Results run/job backend IDs.");
        }
        throw new InvalidOperationException("ACTIONS_RUNTIME_TOKEN lacks valid Actions.Results run/job backend IDs.");
    }

    private static Uri ValidateUrl(string? value, bool blob)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Fragment.Length != 0 ||
            (blob ? !url.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
                  : !url.Host.EndsWith(".actions.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(blob
                ? "Artifact service returned an unsafe HTTPS Azure signed blob URL."
                : "ACTIONS_RESULTS_URL must be an HTTPS GitHub Actions results endpoint.");
        }
        var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (blob)
        {
            if (!query.Any(part => part.StartsWith("sig=", StringComparison.OrdinalIgnoreCase) && part.Length > 4) ||
                query.Any(part => part.StartsWith("comp=", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("blockid=", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Artifact service returned an invalid Azure signed blob URL.");
            }
        }
        else if (url.Query.Length != 0)
        {
            throw new InvalidOperationException("ACTIONS_RESULTS_URL must not contain a query string.");
        }
        return url;
    }

    private static async Task<JsonDocument> RpcAsync(
        HttpClient client, Uri endpoint, string token, string method,
        Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, ServicePath + method));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response, method);
        await response.Content.LoadIntoBufferAsync(65536, cancellationToken);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            json.Dispose();
            throw new InvalidOperationException($"{method} was not accepted by the Actions artifact service.");
        }
        return json;
    }

    private static string? ReadString(JsonElement element, string snakeName, string camelName)
    {
        if (!element.TryGetProperty(snakeName, out var value) && !element.TryGetProperty(camelName, out value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static async Task<string> UploadBlocksAsync(
        HttpClient client, Uri signedUrl, FileStream archive, CancellationToken cancellationToken)
    {
        var buffer = new byte[BlockSize];
        var blocks = new List<string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count;
        while ((count = await archive.ReadAtLeastAsync(buffer, BlockSize, throwOnEndOfStream: false, cancellationToken)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes(blocks.Count.ToString("D8", CultureInfo.InvariantCulture)));
            blocks.Add(blockId);
            hash.AppendData(buffer, 0, count);
            using var content = new ByteArrayContent(buffer, 0, count);
            // Azure transactional MD5 detects transfer corruption; SHA-256 authenticates the whole archive.
#pragma warning disable CA5351 // Azure Put Block requires MD5 for its optional transactional checksum.
            content.Headers.ContentMD5 = MD5.HashData(buffer.AsSpan(0, count));
#pragma warning restore CA5351
            await PutBlobAsync(client, signedUrl, $"comp=block&blockid={Uri.EscapeDataString(blockId)}",
                content, cancellationToken);
        }
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        foreach (var block in blocks)
        {
            xml.Append("<Latest>").Append(block).Append("</Latest>");
        }
        xml.Append("</BlockList>");
        using var blockList = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");
        await PutBlobAsync(client, signedUrl, "comp=blocklist", blockList, cancellationToken);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task PutBlobAsync(
        HttpClient client, Uri signedUrl, string query, HttpContent content, CancellationToken cancellationToken)
    {
        var url = new UriBuilder(signedUrl) { Query = signedUrl.Query.TrimStart('?') + "&" + query }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Add("x-ms-version", "2021-12-02");
        if (query == "comp=blocklist")
        {
            request.Headers.Add("x-ms-blob-content-type", "application/zip");
        }
        request.Content = content;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response, "Azure block blob upload");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{operation} failed (HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}); the artifact upload was not confirmed.");
        }
    }
}
