namespace Rexo.Configuration;

using Rexo.Configuration.Models;
using Rexo.Core.Models;
using System.Text.RegularExpressions;

public static class BranchRefNormalizer
{
    public static bool TryNormalize(string? value, out string? branch)
    {
        branch = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("refs/tags/", StringComparison.Ordinal) ||
            candidate.StartsWith("refs/pull/", StringComparison.Ordinal) ||
            candidate.StartsWith("refs/merge-requests/", StringComparison.Ordinal) ||
            candidate.StartsWith("refs/pipelines/", StringComparison.Ordinal) ||
            candidate.StartsWith("refs/changes/", StringComparison.Ordinal))
        {
            return false;
        }

        if (candidate.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            candidate = candidate["refs/heads/".Length..];
        }
        else if (candidate.StartsWith("refs/", StringComparison.Ordinal))
        {
            return false;
        }

        if (candidate.Length == 0 ||
            candidate.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
            candidate.Any(char.IsControl))
        {
            return false;
        }

        branch = candidate;
        return true;
    }
}

public static class EnvironmentSelectionResolver
{
    public static EnvironmentSelectionResult Resolve(
        RepoConfig config,
        string? explicitEnvironment,
        string? environmentVariableEnvironment,
        string? sourceBranch,
        string? branchSource,
        bool isPullRequest)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (explicitEnvironment is not null || !string.IsNullOrWhiteSpace(environmentVariableEnvironment))
        {
            var selectionSource = explicitEnvironment is not null ? "cli" : "environment-variable";
            var requestedEnvironment = explicitEnvironment ?? environmentVariableEnvironment!;
            var branchMatches = ResolveBranchMatchesForDiagnostics(config.Environments, sourceBranch, isPullRequest);
            return ResolveExplicit(
                config.Environments,
                requestedEnvironment,
                selectionSource,
                sourceBranch,
                branchSource,
                branchMatches,
                isPullRequest);
        }

        var automaticMatches = ResolveBranchMatches(config.Environments, sourceBranch, isPullRequest);
        if (isPullRequest || sourceBranch is null)
        {
            return new EnvironmentSelectionResult
            {
                SourceBranch = sourceBranch,
                BranchSource = branchSource,
                SelectionSource = "unresolved",
                IsPullRequest = isPullRequest,
            };
        }

        if (automaticMatches.Count > 1)
        {
            var details = string.Join(
                Environment.NewLine,
                automaticMatches.Select(match => $"  {match.EnvironmentName,-12} {match.MatchedPattern}"));
            return new EnvironmentSelectionResult
            {
                SourceBranch = sourceBranch,
                BranchSource = branchSource,
                SelectionSource = "branch",
                IsPullRequest = false,
                MatchingEnvironments = automaticMatches,
                Error = "Automatic environment selection failed." + Environment.NewLine + Environment.NewLine +
                    $"Source branch: {sourceBranch}" + Environment.NewLine + Environment.NewLine +
                    "Matching environments:" + Environment.NewLine + details + Environment.NewLine + Environment.NewLine +
                    "Select an environment explicitly or update the branch matching configuration.",
            };
        }

        if (automaticMatches.Count == 0)
        {
            return new EnvironmentSelectionResult
            {
                SourceBranch = sourceBranch,
                BranchSource = branchSource,
                SelectionSource = "unresolved",
            };
        }

        var selectedMatch = automaticMatches[0];
        return new EnvironmentSelectionResult
        {
            EnvironmentName = selectedMatch.EnvironmentName,
            SourceBranch = sourceBranch,
            SelectionSource = "branch",
            BranchSource = branchSource,
            MatchedPattern = selectedMatch.MatchedPattern,
            IsResolved = true,
            MatchingEnvironments = automaticMatches,
        };
    }

    private static IReadOnlyList<EnvironmentBranchMatch> ResolveBranchMatchesForDiagnostics(
        IReadOnlyDictionary<string, RepoEnvironmentConfig>? environments,
        string? sourceBranch,
        bool isPullRequest)
    {
        if (sourceBranch is { Length: > BranchGlobMatcher.MaximumBranchLength })
        {
            return [];
        }

        try
        {
            return ResolveBranchMatches(environments, sourceBranch, isPullRequest);
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    private static EnvironmentSelectionResult ResolveExplicit(
        IReadOnlyDictionary<string, RepoEnvironmentConfig>? environments,
        string requestedName,
        string selectionSource,
        string? sourceBranch,
        string? branchSource,
        IReadOnlyList<EnvironmentBranchMatch> branchMatches,
        bool isPullRequest)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return Failure($"Explicit environment from {selectionSource} must not be empty.", selectionSource, sourceBranch, branchSource, isPullRequest);
        }

        if (environments is null ||
            !environments.TryGetValue(requestedName, out var environment) &&
            !TryFindEnvironment(environments, requestedName, out environment))
        {
            return Failure(
                $"Environment '{requestedName}' from {selectionSource} is not configured.",
                selectionSource,
                sourceBranch,
                branchSource,
                isPullRequest);
        }

        var canonicalName = environments.First(entry => ReferenceEquals(entry.Value, environment)).Key;
        var selectedBranchMatch = branchMatches.FirstOrDefault(match =>
            match.EnvironmentName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));
        return new EnvironmentSelectionResult
        {
            EnvironmentName = canonicalName,
            SourceBranch = sourceBranch,
            SelectionSource = selectionSource,
            BranchSource = branchSource,
            MatchedPattern = selectedBranchMatch?.MatchedPattern,
            IsExplicit = true,
            IsResolved = true,
            IsPullRequest = isPullRequest,
            MatchingEnvironments = branchMatches,
            OverrodeAutomaticSelection = branchMatches.Any(match =>
                !match.EnvironmentName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static EnvironmentSelectionResult Failure(
        string error,
        string selectionSource,
        string? sourceBranch,
        string? branchSource,
        bool isPullRequest) =>
        new()
        {
            SourceBranch = sourceBranch,
            SelectionSource = selectionSource,
            BranchSource = branchSource,
            IsPullRequest = isPullRequest,
            Error = error,
        };

    private static IReadOnlyList<EnvironmentBranchMatch> ResolveBranchMatches(
        IReadOnlyDictionary<string, RepoEnvironmentConfig>? environments,
        string? sourceBranch,
        bool isPullRequest)
    {
        if (isPullRequest || sourceBranch is null || environments is null)
        {
            return [];
        }

        return environments
            .Select(entry => (
                entry.Key,
                Evaluation: entry.Value.Selection?.Branches is { Count: > 0 } patterns
                    ? BranchGlobMatcher.Evaluate(patterns, sourceBranch)
                    : null))
            .Where(entry => entry.Evaluation?.IsMatch == true)
            .Select(entry => new EnvironmentBranchMatch(entry.Key, entry.Evaluation!.MatchedPattern))
            .OrderBy(match => match.EnvironmentName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryFindEnvironment(
        IReadOnlyDictionary<string, RepoEnvironmentConfig> environments,
        string name,
        out RepoEnvironmentConfig? value)
    {
        foreach (var (key, environment) in environments)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = environment;
                return true;
            }
        }

        value = null;
        return false;
    }
}
