namespace Rexo.Execution.Secrets;

using Rexo.Core.Abstractions;
using Rexo.Core.Models;

internal sealed class GitLabCiSecretProvider : ISecretProvider
{
    public string Type => "gitlab-ci";

    public Task<SecretResolution> ResolveAsync(SecretRequest request, CancellationToken cancellationToken)
    {
        var envName = request.Selector;
        if (string.IsNullOrWhiteSpace(envName))
        {
            return Task.FromResult(new SecretResolution(
                request.Name,
                false,
                null,
                Type,
                "gitlab-ci",
                "GitLab CI provider requires a selector or environment variable name."));
        }

        var fromProcess = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(fromProcess))
        {
            return Task.FromResult(new SecretResolution(request.Name, true, fromProcess, Type, "gitlab-ci-env", null, false));
        }

        var fromProcessUpper = Environment.GetEnvironmentVariable(envName.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(fromProcessUpper))
        {
            return Task.FromResult(new SecretResolution(request.Name, true, fromProcessUpper, Type, "gitlab-ci-env", null, false));
        }

        return Task.FromResult(new SecretResolution(
            request.Name,
            false,
            null,
            Type,
            "gitlab-ci",
            $"GitLab CI environment secret '{envName}' for '{request.Name}' is missing.",
            false));
    }
}
