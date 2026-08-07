namespace Rexo.Execution.Secrets;

using Rexo.Core.Abstractions;
using Rexo.Core.Models;

internal sealed class AzureDevOpsSecretProvider : ISecretProvider
{
    public string Type => "azure-devops";

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
                "azure-devops",
                "Azure DevOps provider requires a selector or environment variable name."));
        }

        var fromProcess = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(fromProcess))
        {
            return Task.FromResult(new SecretResolution(request.Name, true, fromProcess, Type, "azure-devops-env", null, false));
        }

        var fromProcessUpper = Environment.GetEnvironmentVariable(envName.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(fromProcessUpper))
        {
            return Task.FromResult(new SecretResolution(request.Name, true, fromProcessUpper, Type, "azure-devops-env", null, false));
        }

        return Task.FromResult(new SecretResolution(
            request.Name,
            false,
            null,
            Type,
            "azure-devops",
            $"Azure DevOps environment secret '{envName}' for '{request.Name}' is missing.",
            false));
    }
}