namespace Rexo.Core.Abstractions;

using Rexo.Core.Models;

public interface ISecretResolver
{
    Task<SecretPreflightResult> PreflightRequiredAsync(CancellationToken cancellationToken);

    Task<string?> GetSecretValueAsync(string name, CancellationToken cancellationToken);

    IReadOnlyDictionary<string, string> ResolvedValues { get; }

    IReadOnlyDictionary<string, SecretResolutionMetadata> Metadata { get; }

    IReadOnlyDictionary<string, string> MappedEnvironment { get; }
}
