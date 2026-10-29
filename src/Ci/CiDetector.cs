namespace Rexo.Ci;

using Rexo.Core.Models;

public static class CiDetector
{
    public static CiInfo Detect()
    {
        // GitHub Actions
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            var serverUrl = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL") ?? "https://github.com";
            var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
            var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
            var buildUrl = repo is not null && runId is not null
                ? $"{serverUrl}/{repo}/actions/runs/{runId}"
                : null;

            var refType = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE");
            var refName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
            var tag = string.Equals(refType, "tag", StringComparison.OrdinalIgnoreCase) ? refName : null;
            var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
            var isPullRequest = eventName?.StartsWith("pull_request", StringComparison.OrdinalIgnoreCase) == true ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_BASE_REF"));

            return new CiInfo(
                IsCi: true,
                Provider: "github-actions",
                BuildId: runId,
                Branch: Environment.GetEnvironmentVariable("GITHUB_REF_NAME"),
                IsPullRequest: isPullRequest)
            {
                RunNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER"),
                WorkflowName = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW"),
                JobName = Environment.GetEnvironmentVariable("GITHUB_JOB"),
                Actor = Environment.GetEnvironmentVariable("GITHUB_ACTOR"),
                Tag = tag,
                BuildUrl = buildUrl,
                RunAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            };
        }

        // Azure DevOps
        if (Environment.GetEnvironmentVariable("TF_BUILD") == "True")
        {
            var teamFoundationServerUri = Environment.GetEnvironmentVariable("SYSTEM_TEAMFOUNDATIONSERVERURI");
            var teamProject = Environment.GetEnvironmentVariable("SYSTEM_TEAMPROJECT");
            var buildId = Environment.GetEnvironmentVariable("BUILD_BUILDID");
            var buildUrl = teamFoundationServerUri is not null && teamProject is not null && buildId is not null
                ? $"{teamFoundationServerUri}{teamProject}/_build/results?buildId={buildId}"
                : null;

            var sourceBranch = Environment.GetEnvironmentVariable("BUILD_SOURCEBRANCH") ?? string.Empty;
            var tag = sourceBranch.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase)
                ? sourceBranch["refs/tags/".Length..]
                : null;

            return new CiInfo(
                IsCi: true,
                Provider: "azure-devops",
                BuildId: buildId,
                Branch: Environment.GetEnvironmentVariable("BUILD_SOURCEBRANCH"),
                IsPullRequest: Environment.GetEnvironmentVariable("BUILD_REASON") == "PullRequest")
            {
                RunNumber = Environment.GetEnvironmentVariable("BUILD_BUILDNUMBER"),
                WorkflowName = Environment.GetEnvironmentVariable("BUILD_DEFINITIONNAME"),
                JobName = Environment.GetEnvironmentVariable("AGENT_JOBNAME"),
                Actor = Environment.GetEnvironmentVariable("BUILD_REQUESTEDFOR"),
                Tag = tag,
                BuildUrl = buildUrl,
            };
        }

        // GitLab CI
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITLAB_CI")))
        {
            var gitlabHost = Environment.GetEnvironmentVariable("CI_SERVER_URL") ?? "https://gitlab.com";
            var projectPath = Environment.GetEnvironmentVariable("CI_PROJECT_PATH");
            var pipelineId = Environment.GetEnvironmentVariable("CI_PIPELINE_ID");
            var buildUrl = projectPath is not null && pipelineId is not null
                ? $"{gitlabHost}/{projectPath}/-/pipelines/{pipelineId}"
                : null;

            return new CiInfo(
                IsCi: true,
                Provider: "gitlab-ci",
                BuildId: pipelineId,
                Branch: Environment.GetEnvironmentVariable("CI_COMMIT_REF_NAME"),
                IsPullRequest: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI_MERGE_REQUEST_ID")))
            {
                RunNumber = Environment.GetEnvironmentVariable("CI_PIPELINE_IID"),
                WorkflowName = Environment.GetEnvironmentVariable("CI_PIPELINE_NAME"),
                JobName = Environment.GetEnvironmentVariable("CI_JOB_NAME"),
                Actor = Environment.GetEnvironmentVariable("GITLAB_USER_LOGIN"),
                Tag = Environment.GetEnvironmentVariable("CI_COMMIT_TAG"),
                BuildUrl = buildUrl,
            };
        }

        // Bitbucket Pipelines
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BITBUCKET_BUILD_NUMBER")))
        {
            var workspace = Environment.GetEnvironmentVariable("BITBUCKET_WORKSPACE");
            var repoSlug = Environment.GetEnvironmentVariable("BITBUCKET_REPO_SLUG");
            var buildNumber = Environment.GetEnvironmentVariable("BITBUCKET_BUILD_NUMBER");
            var buildUrl = workspace is not null && repoSlug is not null && buildNumber is not null
                ? $"https://bitbucket.org/{workspace}/{repoSlug}/pipelines/results/{buildNumber}"
                : null;

            return new CiInfo(
                IsCi: true,
                Provider: "bitbucket-pipelines",
                BuildId: buildNumber,
                Branch: Environment.GetEnvironmentVariable("BITBUCKET_BRANCH"),
                IsPullRequest: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BITBUCKET_PR_ID")))
            {
                RunNumber = buildNumber,
                WorkflowName = Environment.GetEnvironmentVariable("BITBUCKET_PIPELINE_UUID"),
                JobName = Environment.GetEnvironmentVariable("BITBUCKET_STEP_TRIGGERER_UUID"),
                Actor = Environment.GetEnvironmentVariable("BITBUCKET_STEP_TRIGGERER_UUID"),
                Tag = Environment.GetEnvironmentVariable("BITBUCKET_TAG"),
                BuildUrl = buildUrl,
            };
        }

        // Generic CI
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            return new CiInfo(
                IsCi: true,
                Provider: "unknown",
                BuildId: null,
                Branch: null,
                IsPullRequest: false);
        }

        return new CiInfo(
            IsCi: false,
            Provider: null,
            BuildId: null,
            Branch: null,
            IsPullRequest: false);
    }
}
