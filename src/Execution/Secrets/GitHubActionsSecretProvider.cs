namespace Rexo.Execution.Secrets;

using Rexo.Core.Abstractions;
using Rexo.Core.Models;

internal sealed class GitHubActionsSecretProvider : ISecretProvider
{
    public string Type => "github-actions";

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
                "github-actions",
                "GitHub Actions provider requires a selector or environment variable name."));
        }

        var fromProcess = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(fromProcess))
        {
            return Task.FromResult(new SecretResolution(request.Name, true, fromProcess, Type, "github-actions-env", null, false));
        }

        var fromProcessLower = Environment.GetEnvironmentVariable(envName.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(fromProcessLower))
        {
            return Task.FromResult(new SecretResolution(request.Name, true, fromProcessLower, Type, "github-actions-env", null, false));
        }

        return Task.FromResult(new SecretResolution(
            request.Name,
            false,
            null,
            Type,
            "github-actions",
            $"GitHub Actions environment secret '{envName}' for '{request.Name}' is missing.",
            false));
    }
}
