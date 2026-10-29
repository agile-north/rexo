namespace Rexo.Core.Models;

public sealed record EnvironmentSelectionResult
{
    public string? EnvironmentName { get; init; }
    public string? SourceBranch { get; init; }
    public string? SelectionSource { get; init; }
    public string? BranchSource { get; init; }
    public string? MatchedPattern { get; init; }
    public bool IsExplicit { get; init; }
    public bool IsResolved { get; init; }
    public bool OverrodeAutomaticSelection { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<EnvironmentBranchMatch> MatchingEnvironments { get; init; } = [];
    public bool IsPullRequest { get; init; }
}

public sealed record EnvironmentBranchMatch(string EnvironmentName, string? MatchedPattern);
