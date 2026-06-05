namespace Rexo.Execution.Tests;

internal sealed class CiEnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _savedValues;

    public CiEnvironmentVariableScope(params IReadOnlyList<string> variableNames)
    {
        _savedValues = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var variableName in variableNames)
        {
            _savedValues[variableName] = Environment.GetEnvironmentVariable(variableName);
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    public void Dispose()
    {
        foreach (var (variableName, value) in _savedValues)
        {
            Environment.SetEnvironmentVariable(variableName, value);
        }
    }

    public static CiEnvironmentVariableScope CreateIsolatedCiScope() =>
        new(
            "CI",
            "GITHUB_ACTIONS",
            "GITHUB_REPOSITORY",
            "GITHUB_ACTOR",
            "GITHUB_TOKEN",
            "GITLAB_CI",
            "CI_REGISTRY",
            "CI_PROJECT_PATH",
            "CI_REGISTRY_USER",
            "CI_REGISTRY_PASSWORD",
            "CI_JOB_TOKEN",
            "TF_BUILD",
            "BITBUCKET_BUILD_NUMBER");
}
