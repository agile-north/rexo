namespace Rexo.Core.Models;

/// <summary>Evaluated identity of a project selected by an artifact discovery declaration.</summary>
public sealed record ArtifactProject(string Project, string PackageId, bool IsPackable);
