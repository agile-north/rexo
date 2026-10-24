namespace Rexo.Execution;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rexo.Configuration.Models;
using Rexo.Core.Models;

internal sealed record GitHubReleaseCommand(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

internal static class GitHubReleasePublisher
{
    public static async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        RepoConfig config, ExecutionContext context, CancellationToken cancellationToken,
        Func<GitHubReleaseCommand, CancellationToken, Task<ShellRunResult>>? run = null)
    {
        if (!GitHubReleaseOptions.ReadBoolean(context, "enabled", true))
        {
            return Skip("GitHub releases are disabled.");
        }
        if (context.CommandCallStack.Count == 0 || context.CommandCallStack[^1] != "release" ||
            ConfigCommandLoader.TryGetOptionBoolean(context.Options, "push") != true)
        {
            return Skip("GitHub releases require the release lifecycle with --push.");
        }
        if (context.IsDryRun || config.Runtime?.DryRun == true || config.Runtime?.Push?.DryRun == true ||
            ConfigCommandLoader.TryGetOptionBoolean(context.Options, "dry-run") == true)
        {
            return Skip("Dry run: GitHub release and tag creation skipped.");
        }
        if (context.CompletedSteps.Values.Any(step => !step.Success))
        {
            return Skip("Earlier lifecycle work failed.");
        }
        if (context.Version is null || string.IsNullOrWhiteSpace(context.Version.SemVer))
        {
            return Skip("No resolved semantic version is available.");
        }
        if (!ConfigCommandLoader.IsGlobalPushAllowed(config, context, out var reason))
        {
            return Skip(reason);
        }

        var selected = ConfigCommandLoader.SelectArtifacts(config, context, static _ => true).ToArray();
        var gate = GitHubReleaseOptions.Read(context, "artifactGate", "all");
        if (gate is not ("all" or "any" or "none"))
        {
            throw new InvalidOperationException("vars.githubRelease.artifactGate must be all, any or none.");
        }
        if (selected.Length == 0 && !GitHubReleaseOptions.ReadBoolean(context, "allowSourceOnly", true))
        {
            return Skip("Source-only GitHub releases are disabled.");
        }
        if (selected.Length > 0 && gate != "none")
        {
            var identities = selected.Select(artifact => (
                Type: artifact.Type.ToUpperInvariant(),
                Name: ConfigCommandLoader.ResolveArtifactName(artifact, config).ToUpperInvariant())).ToHashSet();
            var selectedNames = identities.Select(identity => identity.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = context.CompletedSteps.Values
                .SelectMany(step => step.Outputs.TryGetValue("__artifacts", out var value) &&
                    value is IEnumerable<ArtifactManifestEntry> artifacts ? artifacts : [])
                .Where(entry => identities.Contains((entry.Type.ToUpperInvariant(), entry.Name.ToUpperInvariant())))
                .ToArray();
            var decisions = context.CompletedSteps.Values
                .SelectMany(step => step.Outputs.TryGetValue("__pushDecisions", out var value) &&
                    value is IEnumerable<PushDecision> items ? items : [])
                .Where(decision => selectedNames.Contains(decision.ArtifactName))
                .ToArray();
            var publication = ArtifactPublicationSummary.From(entries, decisions);
            var completeSelection = selected.All(artifact => entries.Any(entry =>
                string.Equals(entry.Type, artifact.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Name, ConfigCommandLoader.ResolveArtifactName(artifact, config), StringComparison.OrdinalIgnoreCase) &&
                entry.Pushed));
            if (gate == "all" ? !publication.AllPushed || !completeSelection : !publication.AnyPushed)
            {
                return Skip("The selected artifact publication gate was not satisfied.");
            }
        }

        if (context.CommitSha is null || !Regex.IsMatch(context.CommitSha, @"\A[0-9a-fA-F]{40}\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new InvalidOperationException("A full released commit SHA is required for a GitHub release.");
        }
        var options = GitHubReleaseOptions.From(context);
        var cli = new GitHubReleaseCli(options, context, run ?? RunAsync);
        var repoPath = $"repos/{options.Repository}";
        // Authenticate against the repository before interpreting a missing tag/release as absence.
        await cli.ApiAsync(repoPath, "GET", false, cancellationToken);
        var commit = await cli.ApiAsync($"{repoPath}/commits/{context.CommitSha}", "GET", false, cancellationToken);
        if (!string.Equals(commit!.Value.GetProperty("sha").GetString(), context.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The target repository did not resolve the exact released commit.");
        }
        var tagPath = $"{repoPath}/git/ref/tags/{Uri.EscapeDataString(options.Tag)}";
        var tag = await cli.ApiAsync(tagPath, "GET", true, cancellationToken);
        if (tag is not null)
        {
            await VerifyTagAsync(cli, repoPath, tag.Value, context.CommitSha, cancellationToken);
        }
        var release = await FindReleaseAsync(cli, repoPath, options.Tag, cancellationToken);
        if (release is not null)
        {
            if (tag is null)
            {
                throw new InvalidOperationException("An existing release has no verifiable tag.");
            }
            return Outcome("existing", options, context, release.Value.GetProperty("html_url").GetString());
        }
        if (tag is null)
        {
            await cli.ApiAsync($"{repoPath}/git/refs", "POST", false, cancellationToken,
                ["-f", $"ref=refs/tags/{options.Tag}", "-f", $"sha={context.CommitSha}"]);
        }
        // Re-read immediately before creation; never let gh implicitly tag a default branch.
        tag = await cli.ApiAsync(tagPath, "GET", false, cancellationToken);
        await VerifyTagAsync(cli, repoPath, tag!.Value, context.CommitSha, cancellationToken);
        var args = new List<string>
        {
            "release", "create", options.Tag, "--repo", $"{options.Hostname}/{options.Repository}",
            "--verify-tag", "--target", context.CommitSha, "--title", options.Title,
        };
        if (options.GenerateNotes) args.Add("--generate-notes");
        if (options.Prerelease) args.Add("--prerelease");
        if (options.Draft) args.Add("--draft");
        if (options.NotesStartTag.Length > 0)
        {
            args.AddRange(["--notes-start-tag", options.NotesStartTag]);
        }
        if (options.NotesFile.Length > 0)
        {
            args.AddRange(["--notes-file", options.Execution == "container" ? "/rexo-notes.md" : options.NotesFile]);
        }
        else if (!options.GenerateNotes)
        {
            args.AddRange(["--notes", ""]);
        }
        await cli.ExecuteAsync(args, cancellationToken);
        release = await FindReleaseAsync(cli, repoPath, options.Tag, cancellationToken);
        if (release is null)
        {
            throw new InvalidOperationException("The created GitHub release could not be read back.");
        }
        return Outcome("created", options, context, release.Value.GetProperty("html_url").GetString());
    }

    private static async Task<JsonElement?> FindReleaseAsync(
        GitHubReleaseCli cli, string repoPath, string tag, CancellationToken cancellationToken)
    {
        var published = await cli.ApiAsync($"{repoPath}/releases/tags/{Uri.EscapeDataString(tag)}",
            "GET", true, cancellationToken);
        if (published is not null) return published;

        // REST's by-tag lookup excludes drafts; the authenticated release list includes them.
        for (var page = 1; page <= 1000; page++)
        {
            var response = await cli.ApiAsync(
                $"{repoPath}/releases?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                "GET", false, cancellationToken);
            if (response is not { ValueKind: JsonValueKind.Array } releases)
            {
                throw new InvalidOperationException("The GitHub release list was not an array.");
            }
            foreach (var release in releases.EnumerateArray())
            {
                if (string.Equals(release.GetProperty("tag_name").GetString(), tag, StringComparison.Ordinal))
                {
                    return release.Clone();
                }
            }
            if (releases.GetArrayLength() < 100) return null;
        }
        throw new InvalidOperationException("The GitHub release lookup exceeded 100,000 releases.");
    }

    private static async Task VerifyTagAsync(GitHubReleaseCli cli, string repoPath, JsonElement reference,
        string expectedCommit, CancellationToken cancellationToken)
    {
        var target = reference.GetProperty("object");
        for (var depth = 0; depth < 16; depth++)
        {
            var type = target.GetProperty("type").GetString();
            var sha = target.GetProperty("sha").GetString();
            if (type == "commit")
            {
                if (!string.Equals(sha, expectedCommit, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The existing GitHub release tag points to a different commit; it will not be moved.");
                }
                return;
            }
            if (type != "tag" || sha is null || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{40}\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                throw new InvalidOperationException("The release tag does not resolve to a commit.");
            }
            var annotated = await cli.ApiAsync($"{repoPath}/git/tags/{sha}", "GET", false, cancellationToken);
            target = annotated!.Value.GetProperty("object");
        }
        throw new InvalidOperationException("The release tag exceeds the annotated-tag resolution limit.");
    }

    private static IReadOnlyDictionary<string, object?> Skip(string reason)
    {
        Console.WriteLine($"  GitHub release skipped: {reason}");
        return new Dictionary<string, object?> { ["skipped"] = true, ["skipReason"] = reason, ["message"] = reason };
    }

    private static IReadOnlyDictionary<string, object?> Outcome(
        string status, GitHubReleaseOptions options, ExecutionContext context, string? url)
    {
        Console.WriteLine($"  GitHub release {status}: {options.Tag}");
        return new Dictionary<string, object?>
        {
            ["status"] = status, ["tag"] = options.Tag, ["url"] = url,
            ["repository"] = $"{options.Hostname}/{options.Repository}", ["commitSha"] = context.CommitSha,
        };
    }

    private static Task<ShellRunResult> RunAsync(GitHubReleaseCommand command, CancellationToken cancellationToken) =>
        ShellRunner.RunProcessAsync(command.FileName, command.Arguments, command.WorkingDirectory,
            command.Environment, cancellationToken: cancellationToken);
}

internal sealed class GitHubReleaseCli
{
    private readonly GitHubReleaseOptions _options;
    private readonly ExecutionContext _context;
    private readonly Func<GitHubReleaseCommand, CancellationToken, Task<ShellRunResult>> _run;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly IReadOnlySet<string> _secrets;

    public GitHubReleaseCli(GitHubReleaseOptions options, ExecutionContext context,
        Func<GitHubReleaseCommand, CancellationToken, Task<ShellRunResult>> run)
    {
        _options = options;
        _context = context;
        _run = run;
        string? ReadToken(string name) => context.MappedSecretEnvironment.GetValueOrDefault(name) ??
            context.GetEnvironmentValue(name);
        var token = options.Hostname.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? ReadToken("GH_TOKEN") ?? ReadToken("GITHUB_TOKEN")
            : ReadToken("GH_ENTERPRISE_TOKEN") ?? ReadToken("GITHUB_ENTERPRISE_TOKEN") ??
                ReadToken("GH_TOKEN") ?? ReadToken("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("An explicit GH_TOKEN/GITHUB_TOKEN (or enterprise token) is required.");
        }
        _environment = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = token, ["GH_ENTERPRISE_TOKEN"] = token,
            ["GITHUB_TOKEN"] = null, ["GITHUB_ENTERPRISE_TOKEN"] = null,
            ["GH_HOST"] = options.Hostname, ["GH_PROMPT_DISABLED"] = "1",
            ["GH_DEBUG"] = null, ["GH_REPO"] = null,
            ["GH_FORCE_TTY"] = null,
        };
        _secrets = SecretMasker.CollectSecretValues(context.ResolvedSecrets.Values.Append(token));
    }

    public async Task<JsonElement?> ApiAsync(string path, string method, bool allowNotFound,
        CancellationToken cancellationToken, IReadOnlyList<string>? fields = null)
    {
        var args = new List<string> { "api", "--hostname", _options.Hostname, "--include", "--method", method, path };
        if (fields is not null) args.AddRange(fields);
        var result = await InvokeAsync(args, cancellationToken);
        var response = result.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = response.IndexOf("\n\n", StringComparison.Ordinal);
        var statusLineEnd = response.IndexOf('\n');
        var statusParts = (statusLineEnd < 0 ? response : response[..statusLineEnd])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 || !statusParts[0].StartsWith("HTTP/", StringComparison.Ordinal) ||
            !int.TryParse(statusParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            throw Failure("GitHub API returned no valid HTTP status.", result);
        }
        if (status == 404 && allowNotFound) return null;
        if (result.ExitCode != 0 || status is < 200 or >= 300 || separator < 0)
        {
            throw Failure($"GitHub API request failed (HTTP {status.ToString(CultureInfo.InvariantCulture)}).", result);
        }
        using var json = JsonDocument.Parse(response[(separator + 2)..]);
        return json.RootElement.Clone();
    }

    public async Task ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(args, cancellationToken);
        if (result.ExitCode != 0) throw Failure("gh release create failed.", result);
    }

    private Task<ShellRunResult> InvokeAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (_options.Execution == "native")
        {
            return _run(new GitHubReleaseCommand("gh", args, _context.RepositoryRoot, _environment), cancellationToken);
        }
        var dockerArgs = new List<string>
        {
            "run", "--rm", "--env", "GH_TOKEN", "--env", "GH_ENTERPRISE_TOKEN",
            "--env", "GH_HOST", "--env", "GH_PROMPT_DISABLED", "--entrypoint", "gh",
        };
        if (_options.NotesFile.Length > 0 && args.Count > 0 && args[0] == "release")
        {
            dockerArgs.AddRange(["--volume", $"{_options.NotesFile}:/rexo-notes.md:ro"]);
        }
        dockerArgs.Add(_options.Image);
        dockerArgs.AddRange(args);
        return _run(new GitHubReleaseCommand("docker", dockerArgs, _context.RepositoryRoot, _environment), cancellationToken);
    }

    private InvalidOperationException Failure(string message, ShellRunResult result) =>
        new(SecretMasker.Mask($"{message} {result.Stderr}", _secrets));
}
