namespace Rexo.Core.Models;

using System.Text.Json.Serialization;

public sealed record RunManifest
{
    public string SchemaVersion { get; init; } = "1.0";
    public string? ToolVersion { get; init; }
    public string? RepoName { get; init; }
    public string? RepoRoot { get; init; }
    public string? Branch { get; init; }
    public string? CommitSha { get; init; }
    public string? RemoteUrl { get; init; }
    public string? CiProvider { get; init; }
    public bool IsCi { get; init; }
    public string? CiBuildId { get; init; }
    public string? CiRunNumber { get; init; }
    public string? CiWorkflowName { get; init; }
    public string? CiActor { get; init; }
    public string? CiTag { get; init; }
    public string? CiBuildUrl { get; init; }
    public string? CommandExecuted { get; init; }
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration => CompletedAt - StartedAt;
    public VersionResult? Version { get; init; }
    public IReadOnlyList<StepManifestEntry> Steps { get; init; } = Array.Empty<StepManifestEntry>();
    public IReadOnlyList<ArtifactManifestEntry> Artifacts { get; init; } = Array.Empty<ArtifactManifestEntry>();
    public IReadOnlyList<PushDecision> PushDecisions { get; init; } = Array.Empty<PushDecision>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>SHA-256 hash of the resolved rexo config content at run time.</summary>
    public string? ConfigHash { get; init; }

    /// <summary>Assembly version derived from the resolved version.</summary>
    public string? AssemblyVersion { get; init; }

    /// <summary>Informational version (SemVer + build metadata) derived from the resolved version.</summary>
    public string? InformationalVersion { get; init; }

    /// <summary>NuGet-compatible version string derived from the resolved version.</summary>
    [JsonPropertyName("nugetVersion")]
    public string? NuGetVersion { get; init; }
}

public sealed record StepManifestEntry(
    string StepId,
    bool Success,
    int ExitCode,
    double DurationMs)
{
    /// <summary>Files produced by this step, keyed by logical output name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FileOutputs { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ArtifactManifestEntry(
    string Type,
    string Name,
    bool Built,
    bool Pushed,
    IReadOnlyList<string> Tags);

/// <summary>Records why an artifact push was allowed or denied.</summary>
public sealed record PushDecision(
    string ArtifactName,
    bool Allowed,
    string Reason);

