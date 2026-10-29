namespace Rexo.Cli;

using Rexo.Ci;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Git;

internal static class SourceBranchResolver
{
    public static async Task<SourceBranchResolution> ResolveAsync(
        RepoConfig config,
        string? explicitSourceBranch,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (explicitSourceBranch is not null)
        {
            if (!BranchRefNormalizer.TryNormalize(explicitSourceBranch, out var explicitBranch))
            {
                return new SourceBranchResolution(null, "cli", "The explicit --source-branch value is empty or is not a branch ref.");
            }

            return new SourceBranchResolution(explicitBranch, "cli", null);
        }

        var sourceBranchOptions = config.EnvironmentSelection?.SourceBranch;
        var variableName = sourceBranchOptions?.Variable ?? "REXO_SOURCE_BRANCH";
        var variableValue = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(variableValue))
        {
            if (!BranchRefNormalizer.TryNormalize(variableValue, out var variableBranch))
            {
                return new SourceBranchResolution(
                    null,
                    "environment-variable",
                    $"Configured source-branch variable '{variableName}' does not contain a valid branch ref.");
            }

            return new SourceBranchResolution(variableBranch, "environment-variable", null);
        }

        var fallback = sourceBranchOptions?.Fallback ?? "auto";
        if (fallback.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceBranchResolution(null, "unresolved", null);
        }

        var ciInfo = CiDetector.Detect();
        if (ciInfo.IsCi)
        {
            if (ciInfo.IsPullRequest || ciInfo.Tag is not null)
            {
                return new SourceBranchResolution(null, "unresolved", null);
            }

            if (string.Equals(ciInfo.Provider, "github-actions", StringComparison.OrdinalIgnoreCase))
            {
                var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
                if (string.Equals(eventName, "workflow_run", StringComparison.OrdinalIgnoreCase))
                {
                    return new SourceBranchResolution(null, "unresolved", null);
                }

                var refType = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE");
                var candidate = string.Equals(refType, "branch", StringComparison.OrdinalIgnoreCase)
                    ? Environment.GetEnvironmentVariable("GITHUB_REF_NAME")
                    : Environment.GetEnvironmentVariable("GITHUB_REF");
                if (BranchRefNormalizer.TryNormalize(candidate, out var githubBranch))
                {
                    return new SourceBranchResolution(githubBranch, "github-actions", null);
                }

                return new SourceBranchResolution(null, "unresolved", null);
            }

            if (ciInfo.Provider is "azure-devops" or "gitlab-ci" or "bitbucket-pipelines" &&
                BranchRefNormalizer.TryNormalize(ciInfo.Branch, out var ciBranch))
            {
                return new SourceBranchResolution(ciBranch, ciInfo.Provider, null);
            }

            return new SourceBranchResolution(null, "unresolved", null);
        }

        var gitInfo = await GitDetector.DetectAsync(workingDirectory, cancellationToken);
        return BranchRefNormalizer.TryNormalize(gitInfo.Branch, out var localBranch)
            ? new SourceBranchResolution(localBranch, "local-git", null)
            : new SourceBranchResolution(null, "unresolved", null);
    }
}

internal sealed record SourceBranchResolution(string? Branch, string? Source, string? Error);
