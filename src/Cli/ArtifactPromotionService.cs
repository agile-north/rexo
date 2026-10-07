namespace Rexo.Cli;

using System.Security.Cryptography;
using System.Text.Json;
using Rexo.Configuration.Models;
using Rexo.Core.Models;

internal static class ArtifactPromotionService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<string> PromoteAsync(
        string manifestPath,
        string environmentName,
        RepoConfig config,
        string repositoryRoot,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var environment = config.Environments?.FirstOrDefault(entry =>
            entry.Key.Equals(environmentName, StringComparison.OrdinalIgnoreCase));
        if (environment is null)
        {
            throw new InvalidOperationException($"Promotion environment '{environmentName}' is not configured.");
        }

        var environmentPath = environment.Value.Value.Path;
        if (string.IsNullOrWhiteSpace(environmentPath) ||
            Path.IsPathRooted(environmentPath) ||
            environmentPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(segment => segment == ".."))
        {
            throw new InvalidOperationException(
                $"Environment '{environmentName}' path must remain inside the repository.");
        }

        var resolvedRepositoryRoot = ResolveDirectoryPath(repositoryRoot);
        var resolvedEnvironmentPath = ResolveRepositoryDirectoryPath(
            resolvedRepositoryRoot,
            environmentPath,
            environmentName);

        var resolvedManifestPath = Path.IsPathRooted(manifestPath)
            ? manifestPath
            : Path.GetFullPath(Path.Join(repositoryRoot, manifestPath));
        await using var manifestStream = File.OpenRead(resolvedManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<RunManifest>(
            manifestStream,
            ManifestJsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException($"Run manifest '{manifestPath}' is empty or invalid.");
        if (manifest.SchemaVersion != "1.0")
        {
            throw new InvalidOperationException($"Unsupported run manifest schemaVersion '{manifest.SchemaVersion}'.");
        }

        if (manifest.Artifacts.Count != 1)
        {
            throw new InvalidOperationException("Promotion currently requires a run manifest containing exactly one artifact.");
        }

        var artifact = manifest.Artifacts[0];
        if (string.IsNullOrWhiteSpace(artifact.Location) ||
            string.IsNullOrWhiteSpace(artifact.ContentSha256) ||
            artifact.ContentSha256.Length != 64 ||
            !artifact.ContentSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"Artifact '{artifact.Name}' has no verified local file identity and cannot be promoted.");
        }

        var sourcePath = Path.IsPathRooted(artifact.Location)
            ? artifact.Location
            : Path.GetFullPath(Path.Join(repositoryRoot, artifact.Location));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Artifact '{artifact.Name}' file was not found.", sourcePath);
        }

        var actualHash = await HashFileAsync(sourcePath, cancellationToken);
        if (!actualHash.Equals(artifact.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Artifact '{artifact.Name}' no longer matches its recorded SHA-256.");
        }

        var fileName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException($"Artifact '{artifact.Name}' has an invalid file name.");
        }

        var objectDirectory = ResolveRepositoryDirectoryPath(
            resolvedRepositoryRoot,
            Path.GetRelativePath(
                resolvedRepositoryRoot,
                Path.Join(resolvedEnvironmentPath, "objects", actualHash.ToLowerInvariant())),
            environmentName);
        var destinationPath = Path.Join(objectDirectory, fileName);
        var recordDirectory = ResolveRepositoryDirectoryPath(
            resolvedRepositoryRoot,
            Path.GetRelativePath(resolvedRepositoryRoot, Path.Join(resolvedEnvironmentPath, "promotions")),
            environmentName);
        var recordPath = Path.Join(
            recordDirectory,
            $"{SanitizeFileName(artifact.Name)}-{actualHash.ToLowerInvariant()}.json");
        var relativeManifest = Path.GetRelativePath(repositoryRoot, resolvedManifestPath);
        var promotion = new ArtifactPromotionRecord(
            "1.0",
            environmentName,
            relativeManifest,
            artifact.Type,
            artifact.Name,
            actualHash.ToLowerInvariant(),
            manifest.CommitSha,
            manifest.Version?.SemVer,
            manifest.ConfigHash,
            manifest.PolicyLockHash,
            Path.GetRelativePath(repositoryRoot, destinationPath),
            DateTimeOffset.UtcNow);

        ArtifactPromotionRecord? existingPromotion = null;
        if (IsSymbolicLink(recordPath))
        {
            throw new InvalidOperationException($"Promotion record path '{recordPath}' must not be a symbolic link.");
        }

        if (File.Exists(recordPath))
        {
            var existingRecord = await File.ReadAllTextAsync(recordPath, cancellationToken);
            existingPromotion = JsonSerializer.Deserialize<ArtifactPromotionRecord>(existingRecord, ManifestJsonOptions);
            if (existingPromotion is null || !HasSameIdentity(existingPromotion, promotion))
            {
                throw new InvalidOperationException($"Promotion record '{recordPath}' conflicts with the requested promotion.");
            }
        }

        if (dryRun)
        {
            return $"Dry run: would promote {artifact.Type} artifact '{artifact.Name}' to environment '{environmentName}' at '{promotion.DestinationPath}'.";
        }

        if (IsSymbolicLink(destinationPath))
        {
            throw new InvalidOperationException($"Immutable promotion target '{destinationPath}' must not be a symbolic link.");
        }

        if (File.Exists(destinationPath))
        {
            var existingHash = await HashFileAsync(destinationPath, cancellationToken);
            if (!existingHash.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Immutable promotion target '{destinationPath}' already contains different bytes.");
            }
        }
        else
        {
            Directory.CreateDirectory(objectDirectory);
            var temporaryObjectPath = Path.Join(objectDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var source = File.OpenRead(sourcePath))
                await using (var target = new FileStream(
                    temporaryObjectPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                var copiedHash = await HashFileAsync(temporaryObjectPath, cancellationToken);
                if (!copiedHash.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Artifact '{artifact.Name}' changed while it was being copied; promotion was aborted.");
                }

                File.Move(temporaryObjectPath, destinationPath);
            }
            finally
            {
                if (File.Exists(temporaryObjectPath))
                {
                    File.Delete(temporaryObjectPath);
                }
            }
        }

        Directory.CreateDirectory(recordDirectory);
        if (existingPromotion is null)
        {
            var temporaryRecordPath = recordPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporaryRecordPath,
                    JsonSerializer.Serialize(promotion, ManifestJsonOptions),
                    cancellationToken);
                File.Move(temporaryRecordPath, recordPath);
            }
            finally
            {
                if (File.Exists(temporaryRecordPath))
                {
                    File.Delete(temporaryRecordPath);
                }
            }
        }

        return $"Promoted {artifact.Type} artifact '{artifact.Name}' to environment '{environmentName}' with SHA-256 {actualHash}.";
    }

    private static bool HasSameIdentity(ArtifactPromotionRecord existing, ArtifactPromotionRecord requested) =>
        string.Equals(existing.TargetEnvironment, requested.TargetEnvironment, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(existing.SourceManifest, requested.SourceManifest, StringComparison.Ordinal) &&
        string.Equals(existing.ArtifactType, requested.ArtifactType, StringComparison.Ordinal) &&
        string.Equals(existing.ArtifactName, requested.ArtifactName, StringComparison.Ordinal) &&
        string.Equals(existing.ContentSha256, requested.ContentSha256, StringComparison.OrdinalIgnoreCase) &&
        existing.CommitSha == requested.CommitSha &&
        existing.Version == requested.Version &&
        existing.ConfigHash == requested.ConfigHash &&
        existing.PolicyLockHash == requested.PolicyLockHash &&
        string.Equals(existing.DestinationPath, requested.DestinationPath, StringComparison.Ordinal);

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        return directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
    }

    private static bool IsSymbolicLink(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.LinkTarget is not null)
        {
            return true;
        }

        var directoryInfo = new DirectoryInfo(path);
        return directoryInfo.LinkTarget is not null;
    }

    private static string ResolveRepositoryDirectoryPath(
        string repositoryRoot,
        string relativePath,
        string environmentName)
    {
        var root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var currentPath = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.GetFullPath(Path.Join(currentPath, segment));
            if (Directory.Exists(currentPath))
            {
                var directory = new DirectoryInfo(currentPath);
                currentPath = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
            }

            currentPath = Path.GetFullPath(currentPath);
            if (!currentPath.Equals(root, pathComparison) &&
                !currentPath.StartsWith(rootPrefix, pathComparison))
            {
                throw new InvalidOperationException(
                    $"Environment '{environmentName}' path must remain inside the repository, including through symbolic links.");
            }
        }

        return currentPath;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(value.Select(character =>
            invalidCharacters.Contains(character) || character is '/' or '\\' ? '-' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "artifact" : sanitized;
    }

    private sealed record ArtifactPromotionRecord(
        string SchemaVersion,
        string TargetEnvironment,
        string SourceManifest,
        string ArtifactType,
        string ArtifactName,
        string ContentSha256,
        string? CommitSha,
        string? Version,
        string? ConfigHash,
        string? PolicyLockHash,
        string DestinationPath,
        DateTimeOffset PromotedAt);
}
