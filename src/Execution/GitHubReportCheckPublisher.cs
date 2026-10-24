namespace Rexo.Execution;

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rexo.Core.Models;

internal sealed record GitHubReportAnnotation(string Path, int Line, string Title, string Message);
internal sealed record GitHubReportCheckResult(string Status, string? Url = null, string? SkipReason = null);

internal static class GitHubReportCheckPublisher
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public static async Task<GitHubReportCheckResult> PublishAsync(
        ExecutionContext context, GitHubReportOptions options, string title, string markdown,
        string conclusion, IReadOnlyList<GitHubReportAnnotation> annotations,
        CancellationToken cancellationToken, HttpClient? client = null)
    {
        if (!options.Publish || !options.Boolean("checks", true))
            return new("skipped", SkipReason: !options.Publish ? "Publishing is not enabled for this invocation." : "Checks are disabled.");
        var (originHost, originRepository) = GitHubReleaseOptions.ParseRemote(context.RemoteUrl);
        var host = options.Read("hostname", originHost);
        var repository = options.Read("repository", originRepository);
        var tokenName = options.Read("tokenEnvironment", host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "GH_TOKEN" : "GH_ENTERPRISE_TOKEN");
        if (!Regex.IsMatch(tokenName, @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("The report tokenEnvironment must be an environment variable name.");
        var token = context.MappedSecretEnvironment.GetValueOrDefault(tokenName) ?? context.GetEnvironmentValue(tokenName);
        if (string.IsNullOrWhiteSpace(token) && tokenName is "GH_TOKEN" or "GH_ENTERPRISE_TOKEN")
        {
            var fallback = tokenName == "GH_TOKEN" ? "GITHUB_TOKEN" : "GITHUB_ENTERPRISE_TOKEN";
            token = context.MappedSecretEnvironment.GetValueOrDefault(fallback) ?? context.GetEnvironmentValue(fallback);
        }
        if (string.IsNullOrWhiteSpace(token)) return Skip("No explicit GitHub token is available.");
        if (await IsKnownReadOnlyAsync(context, options, cancellationToken))
            return Skip("The Actions context is known to be read-only.");
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':', StringComparison.Ordinal) ||
            !Regex.IsMatch(repository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            repository.Split('/').Any(part => part is "." or ".."))
            throw new InvalidOperationException("Set the report hostname and repository, or configure a valid GitHub origin.");
        var sha = options.Read("commitSha", context.CommitSha ?? "");
        if (!Regex.IsMatch(sha, @"\A[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("A full commit SHA is required to publish report Checks.");
        var name = options.Read("checkName", title) + $" ({options.Scope})";
        if (name.Length > 100 || name.Any(char.IsControl))
            throw new InvalidOperationException("The report Check name including scope must be at most 100 characters without control characters.");
        var run = options.IsActions
            ? $"{context.GetEnvironmentValue("GITHUB_RUN_ID")}:{context.GetEnvironmentValue("GITHUB_RUN_ATTEMPT")}:{context.GetEnvironmentValue("GITHUB_JOB")}"
            : "local";
        var identity = $"{options.Section}:{repository}:{sha}:{options.Scope}:{run}";
        var identityPrefix = $"rexo-report:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}:";
        var externalId = identityPrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(annotations))));
        var api = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com/" : $"https://{host}/api/v3/";
        var path = $"{api}repos/{repository}/check-runs";
        var secrets = SecretMasker.CollectSecretValues(context.ResolvedSecrets.Values
            .Concat(context.MappedSecretEnvironment.Values).Append(token));
        var safeMarkdown = Limit(SecretMasker.Mask(markdown, secrets), 60000);
        var safeAnnotations = annotations.Select(annotation => new
        {
            path = annotation.Path,
            start_line = annotation.Line,
            end_line = annotation.Line,
            annotation_level = "failure",
            title = Limit(SecretMasker.Mask(annotation.Title, secrets), 255),
            message = Limit(SecretMasker.Mask(annotation.Message, secrets), 60000),
        }).ToArray();
        client ??= Client;
        long? existingId = null;
        var supersededIds = new HashSet<long>();
        for (var page = 1; page <= 100; page++)
        {
            var list = await SendAsync(client, HttpMethod.Get,
                $"{api}repos/{repository}/commits/{sha}/check-runs?filter=all&per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                token, null, cancellationToken);
            var checks = list.GetProperty("check_runs");
            foreach (var check in checks.EnumerateArray().Where(IsScopedCheck))
            {
                if (check.GetProperty("external_id").GetString() == externalId)
                {
                    existingId = check.GetProperty("id").GetInt64();
                }
                else supersededIds.Add(check.GetProperty("id").GetInt64());
            }
            if (checks.GetArrayLength() < 100) break;
            if (page == 100) throw new InvalidOperationException("GitHub Check discovery exceeded 10,000 Checks.");
        }
        // Identical reruns update the summary, without appending duplicate annotations.
        // A changed annotation set gets its own run because GitHub cannot replace annotations.
        var annotationsToSend = existingId is null ? safeAnnotations : [];
        var payload = new
        {
            name, head_sha = sha, external_id = externalId, status = "completed", conclusion,
            completed_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            output = new { title, summary = safeMarkdown, annotations = annotationsToSend.Take(50).ToArray() },
        };
        var response = await SendAsync(client, existingId is null ? HttpMethod.Post : HttpMethod.Patch,
            existingId is null ? path : $"{path}/{existingId.Value.ToString(CultureInfo.InvariantCulture)}",
            token, payload, cancellationToken);
        var checkId = response.GetProperty("id").GetInt64();
        for (var offset = 50; offset < annotationsToSend.Length; offset += 50)
        {
            await SendAsync(client, HttpMethod.Patch, $"{path}/{checkId.ToString(CultureInfo.InvariantCulture)}", token,
                new { output = new { title, summary = safeMarkdown, annotations = annotationsToSend.Skip(offset).Take(50).ToArray() } },
                cancellationToken);
        }
        var url = response.GetProperty("html_url").GetString();
        // Neutralize stale annotated runs only after the replacement is fully published.
        foreach (var superseded in supersededIds)
        {
            await SendAsync(client, HttpMethod.Patch, $"{path}/{superseded.ToString(CultureInfo.InvariantCulture)}", token,
                new
                {
                    status = "completed", conclusion = "neutral",
                    output = new { title = "Superseded report", summary = $"This report was superseded by a newer result: {url}" },
                }, cancellationToken);
        }
        return new("published", url);

        bool IsScopedCheck(JsonElement check) =>
            check.TryGetProperty("external_id", out var id) &&
            id.GetString() is { } value && value.StartsWith(identityPrefix, StringComparison.Ordinal) &&
            check.GetProperty("name").GetString() == name;
    }

    private static async Task<bool> IsKnownReadOnlyAsync(
        ExecutionContext context, GitHubReportOptions options, CancellationToken cancellationToken)
    {
        if (!options.IsActions) return false;
        if (context.GetEnvironmentValue("GITHUB_ACTOR") is "dependabot[bot]") return true;
        var eventPath = context.GetEnvironmentValue("GITHUB_EVENT_PATH");
        if (string.IsNullOrEmpty(eventPath) || !File.Exists(eventPath)) return false;
        if (new FileInfo(eventPath).Length > 8 * 1024 * 1024)
            throw new InvalidOperationException("The Actions event payload exceeds 8 MiB.");
        await using var stream = File.OpenRead(eventPath);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("pull_request", out var pullRequest)) return false;
        var head = pullRequest.GetProperty("head").GetProperty("repo");
        return head.ValueKind == JsonValueKind.Null || !string.Equals(
            head.GetProperty("full_name").GetString(), context.GetEnvironmentValue("GITHUB_REPOSITORY"),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string uri,
        string token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Rexo/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub Check API request failed (HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}). Verify GitHub App/GITHUB_TOKEN checks: write permissions.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return json.RootElement.Clone();
    }

    internal static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    private static GitHubReportCheckResult Skip(string reason)
    {
        Console.Error.WriteLine($"GitHub report Check skipped: {reason} A GitHub App token with checks: write is required.");
        return new("skipped", SkipReason: reason);
    }
}
