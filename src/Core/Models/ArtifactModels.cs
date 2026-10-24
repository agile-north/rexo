namespace Rexo.Core.Models;

using System.Text.Json;

public sealed record ArtifactConfig(
    string Type,
    string Name,
    IReadOnlyDictionary<string, JsonElement> Settings)
{
    public string? SourceProject { get; init; }
}

public sealed record ArtifactBuildResult(string Name, bool Success, string? Location);

public sealed record ArtifactTagResult(string Name, bool Success, IReadOnlyList<string> Tags);

public sealed record ArtifactPushResult(string Name, bool Success, IReadOnlyList<string> PublishedReferences);
