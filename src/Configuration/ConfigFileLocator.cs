namespace Rexo.Configuration;

public static class ConfigFileLocator
{
    public const string DefaultConfigDirectory = ".rexo";
    public const string DefaultConfigFileName = "rexo.yaml";
    public const string DefaultPolicyFileName = "policy.yaml";

    // Precedence: .rexo/ folder first, then repository root, then legacy repo.* names.
    // Within each location YAML is preferred over JSON.
    private static readonly string[] ConfigRelativeCandidates =
    [
        ".rexo/rexo.yaml",
        ".rexo/rexo.yml",
        ".rexo/rexo.json",
        "rexo.yaml",
        "rexo.yml",
        "rexo.json",
        ".repo/repo.yaml",
        ".repo/repo.yml",
        ".repo/repo.json",
        "repo.yaml",
        "repo.yml",
        "repo.json",
    ];

    private static readonly string[] PolicyRelativeCandidates =
    [
        ".rexo/policy.yaml",
        ".rexo/policy.yml",
        ".rexo/policy.json",
        "policy.yaml",
        "policy.yml",
        "policy.json",
        ".repo/policy.yaml",
        ".repo/policy.yml",
        ".repo/policy.json",
    ];

    public static IReadOnlyList<string> GetConfigCandidates(string workingDirectory) =>
        ConfigRelativeCandidates
            .Select(relative => Path.Combine(workingDirectory, NormalizeRelativePath(relative)))
            .ToArray();

    public static IReadOnlyList<string> GetPolicyCandidates(string workingDirectory) =>
        PolicyRelativeCandidates
            .Select(relative => Path.Combine(workingDirectory, NormalizeRelativePath(relative)))
            .ToArray();

    public static string? FindConfigPath(string workingDirectory) =>
        GetConfigCandidates(workingDirectory).FirstOrDefault(File.Exists);

    public static string? FindPolicyPath(string workingDirectory) =>
        GetPolicyCandidates(workingDirectory).FirstOrDefault(File.Exists);

    /// <summary>All existing config files in precedence order. The first entry is the one that is used.</summary>
    public static IReadOnlyList<string> FindAllConfigPaths(string workingDirectory) =>
        GetConfigCandidates(workingDirectory).Where(File.Exists).ToArray();

    /// <summary>All existing policy files in precedence order. The first entry is the one that is used.</summary>
    public static IReadOnlyList<string> FindAllPolicyPaths(string workingDirectory) =>
        GetPolicyCandidates(workingDirectory).Where(File.Exists).ToArray();

    /// <summary>
    /// Returns a warning when more than one config (or policy) file exists, naming the files that are ignored.
    /// </summary>
    public static IReadOnlyList<string> GetShadowedFileWarnings(string workingDirectory)
    {
        var warnings = new List<string>();
        AddShadowWarning(warnings, workingDirectory, FindAllConfigPaths(workingDirectory), "config");
        AddShadowWarning(warnings, workingDirectory, FindAllPolicyPaths(workingDirectory), "policy");
        return warnings;
    }

    public static string GetDefaultConfigPath(string workingDirectory) =>
        Path.Combine(workingDirectory, DefaultConfigDirectory, DefaultConfigFileName);

    private static void AddShadowWarning(List<string> warnings, string workingDirectory, IReadOnlyList<string> found, string kind)
    {
        if (found.Count < 2)
        {
            return;
        }

        var used = Path.GetRelativePath(workingDirectory, found[0]);
        var ignored = string.Join(", ", found.Skip(1).Select(path => Path.GetRelativePath(workingDirectory, path)));
        warnings.Add($"Multiple rexo {kind} files found; using '{used}' and ignoring: {ignored}");
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);
}
