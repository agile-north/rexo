namespace Rexo.Artifacts;

using System.Security.Cryptography;
using Rexo.Core.Models;

/// <summary>Shared integrity checks for provider-described repository-local files.</summary>
public static class PreparedArtifactFiles
{
    public static async Task<PreparedArtifact> PrepareAsync(
        ArtifactConfig artifact, IEnumerable<string> paths, string repositoryRoot, CancellationToken cancellationToken)
    {
        var outputs = new List<PreparedArtifactOutput>();
        foreach (var path in paths)
        {
            var reference = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(
                Path.IsPathRooted(path) ? path : Path.Join(repositoryRoot, path))).Replace('\\', '/');
            var fullPath = ResolvePath(repositoryRoot, reference);
            outputs.Add(new PreparedArtifactOutput("file", reference, await HashAsync(fullPath, cancellationToken)));
        }
        if (outputs.Count == 0 || outputs.Select(output => output.Reference).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
        {
            throw new InvalidOperationException("Prepared artifact must have a nonempty, unique file inventory.");
        }
        return new PreparedArtifact(artifact.Type, artifact.Name, outputs);
    }

    public static async Task ValidateAsync(
        PreparedArtifact prepared, string repositoryRoot, CancellationToken cancellationToken)
    {
        if (prepared.Outputs is null || prepared.Outputs.Count == 0)
        {
            throw new InvalidOperationException("Prepared artifact has no outputs.");
        }
        foreach (var output in prepared.Outputs)
        {
            if (output.Kind != "file" || output.Identity != await HashAsync(ResolvePath(repositoryRoot, output.Reference), cancellationToken))
            {
                throw new InvalidOperationException($"Prepared artifact integrity mismatch: '{output.Reference}'.");
            }
        }
    }

    public static string ResolvePath(string repositoryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Prepared artifact paths must be portable repository-relative paths.");
        }
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Join(root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Prepared artifact path escapes the repository.");
        }
        for (var current = fullPath; !string.Equals(current, root, comparison); current = Path.GetDirectoryName(current)!)
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Prepared artifact paths must not traverse symbolic links.");
            }
        }
        return fullPath;
    }

    public static bool MatchesPaths(
        PreparedArtifact prepared, ArtifactConfig artifact, IEnumerable<string> paths, string repositoryRoot)
    {
        var expected = paths.Select(path =>
        {
            var reference = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(
                Path.IsPathRooted(path) ? path : Path.Join(repositoryRoot, path))).Replace('\\', '/');
            _ = ResolvePath(repositoryRoot, reference);
            return reference;
        }).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        return prepared.Type == artifact.Type && prepared.Name == artifact.Name && prepared.Outputs is not null &&
            prepared.Outputs.All(output => output.Kind == "file") &&
            expected.Length > 0 && expected.Distinct(StringComparer.Ordinal).Count() == expected.Length &&
            expected.SequenceEqual(prepared.Outputs.Select(output => output.Reference).OrderBy(path => path, StringComparer.Ordinal));
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
