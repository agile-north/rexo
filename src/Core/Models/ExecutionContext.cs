namespace Rexo.Core.Models;

using Rexo.Core.Environment;

public sealed record ExecutionContext(
    string RepositoryRoot,
    string? Branch,
    string? CommitSha,
    IReadOnlyDictionary<string, object?> Values)
{
    public string? ShortSha { get; init; }
    public string? RemoteUrl { get; init; }
    public bool IsCi { get; init; }
    public string? CiProvider { get; init; }
    public bool IsPullRequest { get; init; }
    public bool IsCleanWorkingTree { get; init; }
    public bool IsDryRun { get; init; }
    public string? CiBuildId { get; init; }
    public string? CiRunNumber { get; init; }
    public string? CiWorkflowName { get; init; }
    public string? CiActor { get; init; }
    public string? CiTag { get; init; }
    public string? CiBuildUrl { get; init; }
    public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string?> Options { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string> FileEnvironment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public string CiVariablePrefix { get; init; } = "REXO_";
    public VersionResult? Version { get; init; }
    public IReadOnlyDictionary<string, StepResult> CompletedSteps { get; init; } = new Dictionary<string, StepResult>();

    /// <summary>Resolved output paths available as <c>{{outputs.*}}</c> in templates.</summary>
    public IReadOnlyDictionary<string, object?> ResolvedOutputs { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved toolchain settings available as <c>{{settings.*}}</c> in templates.</summary>
    public IReadOnlyDictionary<string, object?> ResolvedSettings { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved template vars available as <c>{{vars.*}}</c> in templates.</summary>
    public IReadOnlyDictionary<string, object?> ResolvedVars { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks the command call stack for cross-command cycle detection.</summary>
    public IReadOnlyList<string> CommandCallStack { get; init; } = [];

    public static ExecutionContext Empty(string repositoryRoot) =>
        new(repositoryRoot, null, null, new Dictionary<string, object?>())
        {
            FileEnvironment = RepositoryEnvironmentFiles.Load(repositoryRoot),
        };

    public ExecutionContext WithStep(StepResult result) =>
        string.IsNullOrEmpty(result.StepId)
            ? this
            : this with
            {
                CompletedSteps = new Dictionary<string, StepResult>(CompletedSteps) { [result.StepId] = result }
            };

    public ExecutionContext WithVersion(VersionResult version) =>
        this with { Version = version };

    public string? GetEnvironmentValue(string name) =>
        System.Environment.GetEnvironmentVariable(name)
        ?? (FileEnvironment.TryGetValue(name, out var value) ? value : null);
}

