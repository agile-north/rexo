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

    /// <summary>Deterministic SHA-256 hash of the effective, secret-redacted rexo configuration.</summary>
    public string? ConfigHash { get; init; }

    /// <summary>SHA-256 of the verified local policy lockfile, when one is present.</summary>
    public string? PolicyLockHash { get; init; }

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

    /// <summary>Actual execution mode used for this step (for example: native, container).</summary>
    public string? ExecutionMode { get; init; }

    /// <summary>Requested execution mode before any fallback was applied.</summary>
    public string? RequestedExecutionMode { get; init; }

    /// <summary>Container image configured for the step when applicable.</summary>
    public string? ContainerImage { get; init; }

    /// <summary>Working directory used inside the container when applicable.</summary>
    public string? ContainerWorkingDirectory { get; init; }

    /// <summary>Configured policy for falling back from the requested container runtime.</summary>
    public string? ContainerFallbackPolicy { get; init; }

    /// <summary>True when the configured policy explicitly permits host fallback.</summary>
    public bool? ContainerFallbackAllowed { get; init; }

    /// <summary>True when container execution was requested but native execution was used instead.</summary>
    public bool? ContainerFallbackUsed { get; init; }

    /// <summary>Machine-readable fallback reason.</summary>
    public string? ContainerFallbackReason { get; init; }
}

public sealed record ArtifactManifestEntry(
    string Type,
    string Name,
    bool Built,
    bool Pushed,
    IReadOnlyList<string> Tags)
{
    /// <summary>SHA-256 of a locally produced file when its bytes are available.</summary>
    public string? ContentSha256 { get; init; }

    /// <summary>Provider-reported build output location, when available.</summary>
    public string? Location { get; init; }
}

/// <summary>Records why an artifact push was allowed or denied.</summary>
public sealed record PushDecision(
    string ArtifactName,
    bool Allowed,
    string Reason);
