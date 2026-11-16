namespace Rexo.Core.Abstractions;

using Rexo.Core.Models;

/// <summary>Optional capability to publish previously verified artifacts without rebuilding.</summary>
public interface IPreparedArtifactPublisher : IPreparedArtifactProvider
{
    /// <summary>Publish only the supplied verified outputs, without rebuilding or rediscovering.</summary>
    Task<ArtifactPushResult> PushPreparedAsync(
        ArtifactConfig artifact, PreparedArtifact prepared, ExecutionContext context, CancellationToken cancellationToken);
}
