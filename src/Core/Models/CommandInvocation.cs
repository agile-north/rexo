namespace Rexo.Core.Models;

using System.Collections.Concurrent;
using System.Text.Json.Serialization;

public sealed record CommandInvocation(
    IReadOnlyDictionary<string, string> Args,
    IReadOnlyDictionary<string, string?> Options,
    bool Json,
    string? JsonFile,
    string WorkingDirectory)
{
    public bool Stdout { get; init; } = true;
    public string? EchoMode { get; init; }
    public string? PresentationEchoMode { get; init; }
    public EnvironmentSelectionResult? EnvironmentSelection { get; init; }

    /// <summary>Command names currently on the call stack, used for cross-command cycle detection.</summary>
    public IReadOnlyList<string> CallStack { get; init; } = [];

    /// <summary>Maximum delegated command call depth allowed for the active invocation chain.</summary>
    public int? MaxCommandDepth { get; init; }

    /// <summary>Discovery snapshots shared by the active command delegation chain.</summary>
    [JsonIgnore]
    public ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<ArtifactProject>>>> ArtifactDiscoveryCache { get; init; } = new(StringComparer.Ordinal);
}
