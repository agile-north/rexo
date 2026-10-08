namespace Rexo.Core.Models;

/// <summary>A provider-described immutable output ready for publication without rebuilding.</summary>
public sealed record PreparedArtifact(
    string Type,
    string Name,
    IReadOnlyList<PreparedArtifactOutput> Outputs);

/// <summary>A file or provider-specific immutable reference and its integrity identity.</summary>
public sealed record PreparedArtifactOutput(string Kind, string Reference, string Identity);
