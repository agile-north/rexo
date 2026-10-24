namespace Rexo.Execution.Tests;

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

public sealed class ActionsReportArtifactUploaderTests : IDisposable
{
    private const string RunId = "11111111-1111-1111-1111-111111111111";
    private const string JobId = "22222222-2222-2222-2222-222222222222";
    private const string ResultsUrl = "https://results-receiver.actions.githubusercontent.com/";
    private const string BlobUrl = "https://reports.blob.core.windows.net/artifacts/archive?sv=2021-12-02&sig=secret-signature";
    private const string ArtifactId = "9007199254740993";
    private readonly string root = Path.Join(Directory.GetCurrentDirectory(), $".uploader-test-{Guid.NewGuid():N}");

    public ActionsReportArtifactUploaderTests()
    {
        Directory.CreateDirectory(Path.Join(root, "reports"));
        File.WriteAllText(Path.Join(root, "reports", "results.xml"), "<testsuites />");
    }

    private static string RuntimeToken(string? scope = null) =>
        "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            scp = scope ?? $"Actions.ExampleScope Actions.Results:{RunId}:{JobId}",
        })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    private static Dictionary<string, string?> Environment() => new()
    {
        ["ACTIONS_RUNTIME_TOKEN"] = RuntimeToken(),
        ["ACTIONS_RESULTS_URL"] = ResultsUrl,
        ["GITHUB_TOKEN"] = "repository-secret-must-never-be-used",
        ["GITHUB_RETENTION_DAYS"] = "7",
    };

    [Fact]
    public async Task UploadUsesVersionFourTwirpZipBlocksAndFinalizesWithSha256()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var environment = Environment();
        var before = DateTimeOffset.UtcNow;
        var id = await ActionsReportArtifactUploader.UploadAsync(
            [Path.Join("reports", "results.xml")], root, "test-results", 30,
            environment.GetValueOrDefault, CancellationToken.None, client);

        Assert.Equal(ArtifactId, id);
        Assert.Equal(4, handler.Requests.Count);
        var create = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal(ResultsUrl + "twirp/github.actions.results.api.v1.ArtifactService/CreateArtifact", create.Url.AbsoluteUri);
        Assert.Equal("Bearer " + RuntimeToken(), create.Authorization);
        Assert.Equal("application/json; charset=utf-8", create.ContentType);
        using var createJson = JsonDocument.Parse(create.Content);
        Assert.Equal(4, createJson.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(RunId, createJson.RootElement.GetProperty("workflow_run_backend_id").GetString());
        Assert.Equal(JobId, createJson.RootElement.GetProperty("workflow_job_run_backend_id").GetString());
        Assert.Equal("test-results", createJson.RootElement.GetProperty("name").GetString());
        var expiration = createJson.RootElement.GetProperty("expires_at").GetDateTimeOffset();
        Assert.InRange(expiration, before.AddDays(7), DateTimeOffset.UtcNow.AddDays(7));

        var block = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, block.Method);
        Assert.Contains("comp=block&", block.Url.Query, StringComparison.Ordinal);
        Assert.Null(block.Authorization);
        Assert.Equal("2021-12-02", block.StorageVersion);
#pragma warning disable CA5351 // Checking Azure's transactional checksum, not a security hash.
        Assert.Equal(MD5.HashData(block.Content), block.Md5);
#pragma warning restore CA5351
        using var zip = new ZipArchive(new MemoryStream(block.Content));
        Assert.Single(zip.Entries);
        Assert.Equal("reports/results.xml", zip.Entries[0].FullName);
        using var reader = new StreamReader(zip.Entries[0].Open());
        Assert.Equal("<testsuites />", await reader.ReadToEndAsync(CancellationToken.None));

        var commit = handler.Requests[2];
        Assert.Contains("comp=blocklist", commit.Url.Query, StringComparison.Ordinal);
        Assert.Null(commit.Authorization);
        Assert.Equal("application/zip", commit.BlobContentType);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes("00000000")),
            XDocument.Parse(Encoding.UTF8.GetString(commit.Content)).Root!.Element("Latest")!.Value);
        var finalize = handler.Requests[3];
        Assert.EndsWith("/FinalizeArtifact", finalize.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer " + RuntimeToken(), finalize.Authorization);
        using var finalizedJson = JsonDocument.Parse(finalize.Content);
        Assert.Equal(block.Content.Length.ToString(CultureInfo.InvariantCulture), finalizedJson.RootElement.GetProperty("size").GetString());
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(block.Content)),
            finalizedJson.RootElement.GetProperty("hash").GetString());
        Assert.All(handler.Requests.Where(request => request.Method == HttpMethod.Put),
            request => Assert.DoesNotContain("repository-secret", Encoding.UTF8.GetString(request.Content), StringComparison.Ordinal));
        AssertArchiveCleaned();
    }

    [Fact]
    public async Task LargeArchiveUsesBoundedOrderedBlocks()
    {
        var bytes = new byte[9 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Join(root, "large.bin"), bytes, CancellationToken.None);
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await ActionsReportArtifactUploader.UploadAsync(["large.bin"], root, "large", 0,
            Environment().GetValueOrDefault, CancellationToken.None, client);
        var blocks = handler.Requests.Where(request => request.Url.Query.Contains("comp=block&", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, blocks.Length);
        Assert.All(blocks, block => Assert.InRange(block.Content.Length, 1, 4 * 1024 * 1024));
        var uploaded = blocks.SelectMany(block => block.Content).ToArray();
        using var zip = new ZipArchive(new MemoryStream(uploaded));
        using var extracted = new MemoryStream();
        await zip.Entries[0].Open().CopyToAsync(extracted, CancellationToken.None);
        Assert.Equal(bytes, extracted.ToArray());
        var commit = handler.Requests.Single(request => request.Url.Query.Contains("comp=blocklist", StringComparison.Ordinal));
        var ids = XDocument.Parse(Encoding.UTF8.GetString(commit.Content)).Root!.Elements("Latest").Select(element => element.Value).ToArray();
        Assert.Equal(3, ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            Assert.Equal(index.ToString("D8", CultureInfo.InvariantCulture), Encoding.ASCII.GetString(Convert.FromBase64String(ids[index])));
            Assert.Contains("blockid=" + Uri.EscapeDataString(ids[index]), blocks[index].Url.Query, StringComparison.Ordinal);
        }
        using var create = JsonDocument.Parse(handler.Requests[0].Content);
        Assert.False(create.RootElement.TryGetProperty("expires_at", out _));
        using var finalize = JsonDocument.Parse(handler.Requests[^1].Content);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(uploaded)), finalize.RootElement.GetProperty("hash").GetString());
        Assert.Equal(uploaded.Length.ToString(CultureInfo.InvariantCulture), finalize.RootElement.GetProperty("size").GetString());
        AssertArchiveCleaned();
    }

    [Theory]
    [InlineData("ACTIONS_RUNTIME_TOKEN")]
    [InlineData("ACTIONS_RESULTS_URL")]
    public async Task MissingRuntimeCredentialsFailRatherThanUsingGithubToken(string missing)
    {
        var environment = Environment();
        environment.Remove(missing);
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                environment.GetValueOrDefault, CancellationToken.None, client));
        Assert.Contains("Ordinary Actions run steps", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN cannot replace", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trusted JavaScript action", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("folder\\escape")]
    [InlineData("bad:name")]
    [InlineData("bad\nname")]
    [InlineData("bad*name")]
    [InlineData(".")]
    public async Task UnsafeNamesAreRejectedBeforeNetwork(string name)
    {
        await AssertRejectedAsync(["reports/results.xml"], name: name);
    }

    [Theory]
    [InlineData("../outside.xml")]
    [InlineData("reports/../reports/results.xml")]
    [InlineData("reports\\..\\results.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("C:\\absolute.xml")]
    [InlineData("reports/results.xml:alternate")]
    [InlineData("reports/NUL")]
    [InlineData("reports/COM1.xml")]
    [InlineData("reports//results.xml")]
    [InlineData("reports/./results.xml")]
    [InlineData("reports")]
    [InlineData("missing.xml")]
    public async Task UnsafeMissingOrDirectoryInputsAreRejectedBeforeNetwork(string file)
    {
        await AssertRejectedAsync([file]);
    }

    [Fact]
    public async Task EmptyAndDuplicateInputsAreRejectedBeforeNetwork()
    {
        await AssertRejectedAsync([]);
        await AssertRejectedAsync(["reports/results.xml", "reports\\results.xml"]);
    }

    [Fact]
    public async Task LinkedFileAndAncestorAreRejectedBeforeNetwork()
    {
        var link = Path.Join(root, "linked");
        // Windows junctions need no developer mode or symbolic-link privilege.
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{Path.Join(root, "reports")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await process.WaitForExitAsync(CancellationToken.None);
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, Path.Join(root, "reports"));
        }
        try
        {
            await AssertRejectedAsync(["linked/results.xml"]);
        }
        finally
        {
            Directory.Delete(link);
        }

        if (!OperatingSystem.IsWindows())
        {
            var fileLink = Path.Join(root, "linked.xml");
            File.CreateSymbolicLink(fileLink, Path.Join(root, "reports", "results.xml"));
            await AssertRejectedAsync(["linked.xml"]);
        }
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("header.@@@@.signature")]
    [InlineData("header.e30.signature")]
    [InlineData("wrong-scope")]
    public async Task InvalidRuntimeTokenDoesNotReachNetwork(string value)
    {
        var environment = Environment();
        environment["ACTIONS_RUNTIME_TOKEN"] = value == "wrong-scope" ? RuntimeToken("repo:read") : value;
        await AssertRejectedAsync(["reports/results.xml"], environment);
    }

    [Theory]
    [InlineData("http://results-receiver.actions.githubusercontent.com/")]
    [InlineData("https://results-receiver.actions.githubusercontent.com.evil.invalid/")]
    [InlineData("https://user:password@results-receiver.actions.githubusercontent.com/")]
    [InlineData("https://results-receiver.actions.githubusercontent.com/?sig=secret")]
    [InlineData("https://results-receiver.actions.githubusercontent.com/#secret")]
    [InlineData("https://localhost/")]
    public async Task UnsafeResultsEndpointDoesNotReceiveCredentials(string url)
    {
        var environment = Environment();
        environment["ACTIONS_RESULTS_URL"] = url;
        await AssertRejectedAsync(["reports/results.xml"], environment);
    }

    [Theory]
    [InlineData("http://reports.blob.core.windows.net/file?sig=secret")]
    [InlineData("https://reports.blob.core.windows.net.evil.invalid/file?sig=secret")]
    [InlineData("https://user:password@reports.blob.core.windows.net/file?sig=secret")]
    [InlineData("https://reports.blob.core.windows.net/file")]
    [InlineData("https://reports.blob.core.windows.net/file?sig=secret&comp=block")]
    [InlineData("https://reports.blob.core.windows.net/file?sig=secret#fragment")]
    public async Task UnsafeSignedUrlPreventsBlobUpload(string url)
    {
        using var handler = new RecordingHandler { SignedUrl = url };
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                Environment().GetValueOrDefault, CancellationToken.None, client));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        AssertArchiveCleaned();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailedStagesNeverReturnSuccessAndDoNotExposeResponseSecrets(int failedStage)
    {
        using var handler = new RecordingHandler
        {
            Override = (request, index, _) => index == failedStage
                ? Task.FromResult(new HttpResponseMessage(index == 1 ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("secret-signature " + RuntimeToken() + " repository-secret"),
                    Headers = { Location = new Uri("https://evil.invalid/") },
                })
                : null,
        };
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                Environment().GetValueOrDefault, CancellationToken.None, client));
        Assert.Equal(failedStage + 1, handler.Requests.Count);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeToken(), exception.ToString(), StringComparison.Ordinal);
        AssertArchiveCleaned();
    }

    [Theory]
    [InlineData("""{"ok":false,"msg":"secret"}""", 0)]
    [InlineData("""{"ok":true}""", 0)]
    [InlineData("""{"ok":false,"msg":"secret"}""", 3)]
    [InlineData("""{"ok":true,"artifact_id":"0"}""", 3)]
    [InlineData("""{"ok":true,"artifact_id":"not-id-secret"}""", 3)]
    [InlineData("""{"ok":true,"artifact_id":"18446744073709551615"}""", 3)]
    [InlineData("""{"ok":true,"artifact_id":123}""", 3)]
    [InlineData("""{"ok":true,"artifact_id":"123","extra":""", 3)]
    public async Task InvalidResponsesNeverReturnAnArtifactId(string body, int stage)
    {
        using var handler = new RecordingHandler
        {
            Override = (_, index, _) => index == stage ? Task.FromResult(Json(body)) : null,
        };
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                Environment().GetValueOrDefault, CancellationToken.None, client));
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        AssertArchiveCleaned();
    }

    [Fact]
    public async Task TransportErrorsAreSanitizedAndArchiveIsRemoved()
    {
        using var handler = new RecordingHandler
        {
            Override = (_, _, _) => throw new HttpRequestException("secret-signature " + RuntimeToken()),
        };
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                Environment().GetValueOrDefault, CancellationToken.None, client));
        Assert.Contains("transport failed", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        AssertArchiveCleaned();
    }

    [Fact]
    public async Task CancellationIsPropagatedAndArchiveIsRemoved()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RecordingHandler
        {
            Override = (_, index, token) =>
            {
                if (index == 1)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return null;
            },
        };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                Environment().GetValueOrDefault, cancellation.Token, client));
        Assert.Equal(2, handler.Requests.Count);
        AssertArchiveCleaned();
    }

    [Fact]
    public async Task DefaultClientHeadersAreRejectedToPreventBlobCredentialLeakage()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "repository-secret");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                Environment().GetValueOrDefault, CancellationToken.None, client));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PreCancellationDoesNotCreateArchiveOrMakeRequests()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
                Environment().GetValueOrDefault, cancellation.Token, client));
        Assert.Empty(handler.Requests);
        AssertArchiveCleaned();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidRetentionDaysAreRejectedBeforeNetwork(int retention)
    {
        var environment = Environment();
        environment.Remove("GITHUB_RETENTION_DAYS");
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", retention,
                environment.GetValueOrDefault, CancellationToken.None, client));
        Assert.Empty(handler.Requests);
        AssertArchiveCleaned();
    }

    [Fact]
    public async Task CamelCaseProtobufResponseNamesAreAccepted()
    {
        using var handler = new RecordingHandler
        {
            Override = (_, index, _) => index switch
            {
                0 => Task.FromResult(Json(JsonSerializer.Serialize(new { ok = true, signedUploadUrl = BlobUrl }))),
                3 => Task.FromResult(Json(JsonSerializer.Serialize(new { ok = true, artifactId = ArtifactId }))),
                _ => null,
            },
        };
        using var client = new HttpClient(handler);
        Assert.Equal(ArtifactId, await ActionsReportArtifactUploader.UploadAsync(["reports/results.xml"], root, "reports", 1,
            Environment().GetValueOrDefault, CancellationToken.None, client));
        AssertArchiveCleaned();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("-1")]
    [InlineData("0")]
    public async Task InvalidRetentionLimitFailsBeforeNetwork(string maximum)
    {
        var environment = Environment();
        environment["GITHUB_RETENTION_DAYS"] = maximum;
        await AssertRejectedAsync(["reports/results.xml"], environment);
    }

    private async Task AssertRejectedAsync(
        IReadOnlyList<string> files, Dictionary<string, string?>? environment = null, string name = "reports")
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionsReportArtifactUploader.UploadAsync(files, root, name, 1,
                (environment ?? Environment()).GetValueOrDefault, CancellationToken.None, client));
        Assert.Empty(handler.Requests);
        AssertArchiveCleaned();
    }

    private void AssertArchiveCleaned() =>
        Assert.Empty(Directory.EnumerateFiles(root, ".rexo-actions-artifact-*.zip"));

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record Request(
        HttpMethod Method, Uri Url, string? Authorization, byte[] Content,
        byte[]? Md5, string? ContentType, string? StorageVersion, string? BlobContentType);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public string SignedUrl { get; init; } = BlobUrl;
        public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>?>? Override { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsByteArrayAsync(cancellationToken), request.Content.Headers.ContentMD5,
                request.Content.Headers.ContentType?.ToString(),
                request.Headers.TryGetValues("x-ms-version", out var version) ? version.Single() : null,
                request.Headers.TryGetValues("x-ms-blob-content-type", out var contentType) ? contentType.Single() : null));
            var overridden = Override?.Invoke(request, Requests.Count - 1, cancellationToken);
            if (overridden is not null)
            {
                return await overridden;
            }
            if (request.Method == HttpMethod.Put)
            {
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/CreateArtifact", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new { ok = true, signed_upload_url = SignedUrl }));
            }
            return Json(JsonSerializer.Serialize(new { ok = true, artifact_id = ArtifactId }));
        }
    }
}
