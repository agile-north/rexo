namespace Rexo.Core.Models;

/// <summary>Publication outcomes for distinct artifacts, independent of command success.</summary>
public sealed record ArtifactPublicationSummary(bool AnyPushed, int PushedCount, bool AllPushed)
{
    public static ArtifactPublicationSummary From(
        IReadOnlyList<ArtifactManifestEntry> artifacts,
        IReadOnlyList<PushDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(decisions);

        var selectedNames = decisions.Select(decision => decision.ArtifactName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outcomes = artifacts
            .GroupBy(artifact => (artifact.Type.ToUpperInvariant(), artifact.Name.ToUpperInvariant()))
            .Select(group => new { group.First().Name, Pushed = group.Any(artifact => artifact.Pushed) })
            .ToArray();
        var selected = outcomes.Where(artifact => selectedNames.Count == 0 || selectedNames.Contains(artifact.Name)).ToArray();
        var pushedCount = outcomes.Count(artifact => artifact.Pushed);
        var allPushed = selected.Length > 0 && selected.All(artifact => artifact.Pushed) &&
            decisions.All(decision => decision.Allowed) &&
            selectedNames.All(name => selected.Any(artifact => string.Equals(artifact.Name, name, StringComparison.OrdinalIgnoreCase)));

        return new ArtifactPublicationSummary(pushedCount > 0, pushedCount, allPushed);
    }
}
