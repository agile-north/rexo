namespace Rexo.Core.Abstractions;

using Rexo.Core.Models;

/// <summary>Optional capability for transferring and publishing already-built artifacts.</summary>
public interface IPreparedArtifactProvider
{
    Task<PreparedArtifact> PrepareAsync(
        ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken);

    Task ValidatePreparedAsync(
        ArtifactConfig artifact, PreparedArtifact prepared, ExecutionContext context, CancellationToken cancellationToken);

    Task<ArtifactPushResult> PushPreparedAsync(
        ArtifactConfig artifact, PreparedArtifact prepared, ExecutionContext context, CancellationToken cancellationToken);
}
