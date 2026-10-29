namespace Rexo.Configuration.Tests;

using Rexo.Cli;
using Rexo.Configuration.Models;

[Collection("EnvironmentVariableSensitive")]
public sealed class SourceBranchResolverTests
{
    private static readonly string[] CiEnvironmentVariables =
    [
        "REXO_SOURCE_BRANCH",
        "GITHUB_ACTIONS",
        "GITHUB_EVENT_NAME",
        "GITHUB_BASE_REF",
        "GITHUB_REF",
        "GITHUB_REF_NAME",
        "GITHUB_REF_TYPE",
        "GITHUB_HEAD_REF",
        "GITLAB_CI",
        "CI_COMMIT_REF_NAME",
        "CI_COMMIT_TAG",
        "BITBUCKET_BUILD_NUMBER",
        "BITBUCKET_BRANCH",
        "BITBUCKET_TAG",
        "TF_BUILD",
        "BUILD_SOURCEBRANCH",
        "BUILD_REASON",
        "CI",
    ];

    [Fact]
    public async Task ResolveUsesGithubPushBranch()
    {
        var originals = CaptureEnvironment();
        try
        {
            ClearEnvironment();
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            Environment.SetEnvironmentVariable("GITHUB_EVENT_NAME", "push");
            Environment.SetEnvironmentVariable("GITHUB_REF_TYPE", "branch");
            Environment.SetEnvironmentVariable("GITHUB_REF_NAME", "main");

            var result = await SourceBranchResolver.ResolveAsync(
                CreateConfig(),
                explicitSourceBranch: null,
                Path.GetTempPath(),
                CancellationToken.None);

            Assert.Equal("main", result.Branch);
            Assert.Equal("github-actions", result.Source);
        }
        finally
        {
            RestoreEnvironment(originals);
        }
    }

    [Theory]
    [InlineData("pull_request")]
    [InlineData("pull_request_target")]
    [InlineData("workflow_run")]
    public async Task ResolveDoesNotUseGithubPullRequestOrWorkflowRunBranch(string eventName)
    {
        var originals = CaptureEnvironment();
        try
        {
            ClearEnvironment();
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            Environment.SetEnvironmentVariable("GITHUB_EVENT_NAME", eventName);
            Environment.SetEnvironmentVariable("GITHUB_REF_TYPE", "branch");
            Environment.SetEnvironmentVariable("GITHUB_REF_NAME", "feature-branch");
            Environment.SetEnvironmentVariable("GITHUB_HEAD_REF", "feature-branch");
            if (!eventName.Equals("workflow_run", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("GITHUB_BASE_REF", "main");
            }

            var result = await SourceBranchResolver.ResolveAsync(
                CreateConfig(),
                explicitSourceBranch: null,
                Path.GetTempPath(),
                CancellationToken.None);

            Assert.Null(result.Branch);
            Assert.Equal("unresolved", result.Source);
        }
        finally
        {
            RestoreEnvironment(originals);
        }
    }

    [Fact]
    public async Task ResolveDoesNotTreatGitlabTagAsSourceBranch()
    {
        var originals = CaptureEnvironment();
        try
        {
            ClearEnvironment();
            Environment.SetEnvironmentVariable("GITLAB_CI", "true");
            Environment.SetEnvironmentVariable("CI_COMMIT_REF_NAME", "v1.2.3");
            Environment.SetEnvironmentVariable("CI_COMMIT_TAG", "v1.2.3");

            var result = await SourceBranchResolver.ResolveAsync(
                CreateConfig(),
                explicitSourceBranch: null,
                Path.GetTempPath(),
                CancellationToken.None);

            Assert.Null(result.Branch);
            Assert.Equal("unresolved", result.Source);
        }
        finally
        {
            RestoreEnvironment(originals);
        }
    }

    private static RepoConfig CreateConfig() =>
        new("test", new Dictionary<string, RepoCommandConfig>(), new Dictionary<string, string>());

    private static Dictionary<string, string?> CaptureEnvironment() =>
        CiEnvironmentVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

    private static void ClearEnvironment()
    {
        foreach (var name in CiEnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> originals)
    {
        foreach (var (name, value) in originals)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
