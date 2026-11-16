namespace Rexo.Core.Abstractions;

using Rexo.Core.Models;

/// <summary>Optional capability for preparing and verifying already-built artifacts.</summary>
public interface IPreparedArtifactProvider
{
    /// <summary>Describe the exact built outputs using immutable identities.</summary>
    Task<PreparedArtifact> PrepareAsync(
        ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken);

    /// <summary>Validate the prepared outputs against the original build and current artifact contents.</summary>
    Task ValidatePreparedAsync(
        ArtifactConfig artifact, PreparedArtifact prepared, ArtifactManifestEntry build,
        ExecutionContext context, CancellationToken cancellationToken);
}
