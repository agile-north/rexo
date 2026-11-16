namespace Rexo.Core.Abstractions;

using Rexo.Core.Models;

/// <summary>Provides locally verifiable build outputs without authorizing publication.</summary>
public interface ILocalArtifactHandoffProvider
{
    Task<PreparedArtifact> PrepareAsync(
        ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken);

    Task ValidatePreparedAsync(
        ArtifactConfig artifact, PreparedArtifact prepared, ArtifactManifestEntry build,
        ExecutionContext context, CancellationToken cancellationToken);
}
