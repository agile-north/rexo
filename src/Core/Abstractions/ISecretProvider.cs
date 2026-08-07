namespace Rexo.Core.Abstractions;

using Rexo.Core.Models;

public interface ISecretProvider
{
    string Type { get; }

    Task<SecretResolution> ResolveAsync(
        SecretRequest request,
        CancellationToken cancellationToken);
}
