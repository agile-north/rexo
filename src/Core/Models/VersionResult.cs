namespace Rexo.Core.Models;

using System.Globalization;
using System.Text.Json.Serialization;

public sealed record VersionResult(
    string SemVer,
    int Major,
    int Minor,
    int Patch,
    string? PreRelease,
    string CommitSha,
    string ShortSha,
    bool IsPreRelease,
    bool IsStable)
{
    private string? _preReleaseLabel;
    private int? _preReleaseNumber;

    /// <summary>Build metadata segment (e.g. the part after <c>+</c> in SemVer 2.0).</summary>
    public string? BuildMetadata { get; init; }

    /// <summary>Repository branch at the time of version resolution.</summary>
    public string? Branch { get; init; }

    /// <summary>Version string for NuGet packages (pre-release separators converted to dots).</summary>
    [JsonPropertyName("nugetVersion")]
    public string? NuGetVersion { get; init; }

    /// <summary>Version string for Docker image tags (only alphanumeric, dot, hyphen).</summary>
    public string? DockerVersion { get; init; }

    /// <summary>Provider-reported pre-release tag (e.g. <c>qa.5</c>).</summary>
    public string? PreReleaseTag => PreRelease;

    /// <summary>Pre-release label without numeric suffix (e.g. <c>qa</c> from <c>qa.5</c>).</summary>
    public string? PreReleaseLabel
    {
        get => _preReleaseLabel ?? ParsePreRelease(PreRelease).Label;
        init => _preReleaseLabel = value;
    }

    /// <summary>Numeric pre-release sequence when available (e.g. <c>5</c> from <c>qa.5</c>).</summary>
    public int? PreReleaseNumber
    {
        get => _preReleaseNumber ?? ParsePreRelease(PreRelease).Number;
        init => _preReleaseNumber = value;
    }

    /// <summary>Pre-release label prefixed with dash (e.g. <c>-qa</c>).</summary>
    public string? PreReleaseLabelWithDash =>
        string.IsNullOrWhiteSpace(PreReleaseLabel) ? null : $"-{PreReleaseLabel}";

    /// <summary>Pre-release tag prefixed with dash (e.g. <c>-qa.5</c>).</summary>
    public string? PreReleaseTagWithDash =>
        string.IsNullOrWhiteSpace(PreReleaseTag) ? null : $"-{PreReleaseTag}";

    /// <summary>Assembly-compatible version (Major.Minor.Patch.0).</summary>
    public string AssemblyVersion =>
        $"{Major}.{Minor}.{Patch}.0";

    /// <summary>File version (Major.Minor.Patch.0).</summary>
    public string FileVersion =>
        $"{Major}.{Minor}.{Patch}.0";

    /// <summary>Full informational version string including pre-release and build metadata.</summary>
    public string InformationalVersion =>
        BuildMetadata is not null ? $"{SemVer}+{BuildMetadata}" : SemVer;

    /// <summary>Number of commits since the version source tag (if available).</summary>
    public int? CommitsSinceVersionSource { get; init; }

    /// <summary>
    /// Numeric weight of the pre-release label for sorting purposes.
    /// Conventional mapping: alpha=1, beta=2, rc=3, preview=4; null when stable (no pre-release label).
    /// </summary>
    public int? WeightedPreReleaseNumber =>
        string.IsNullOrEmpty(PreReleaseLabel) ? null :
        PreReleaseLabel.StartsWith("alpha", StringComparison.OrdinalIgnoreCase) ? 1 :
        PreReleaseLabel.StartsWith("beta", StringComparison.OrdinalIgnoreCase) ? 2 :
        PreReleaseLabel.StartsWith("rc", StringComparison.OrdinalIgnoreCase) ? 3 :
        PreReleaseLabel.StartsWith("preview", StringComparison.OrdinalIgnoreCase) ? 4 :
        0;

    private static (string? Label, int? Number) ParsePreRelease(string? preRelease)
    {
        if (string.IsNullOrWhiteSpace(preRelease))
        {
            return (null, null);
        }

        var lastDot = preRelease.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= preRelease.Length - 1)
        {
            return (preRelease, null);
        }

        var numberPart = preRelease[(lastDot + 1)..];
        if (!int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return (preRelease, null);
        }

        var label = preRelease[..lastDot];
        return string.IsNullOrWhiteSpace(label)
            ? (preRelease, null)
            : (label, number);
    }
}
