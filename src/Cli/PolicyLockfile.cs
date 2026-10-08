namespace Rexo.Cli;

internal sealed record PolicyLockfile
{
    public string SchemaVersion { get; init; } = "1.0";
    public IReadOnlyList<PolicyLockEntry> Policies { get; init; } = Array.Empty<PolicyLockEntry>();
}

internal sealed record PolicyLockEntry(string Source, string Sha256);
