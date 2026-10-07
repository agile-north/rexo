namespace Rexo.Execution;

using System.Security.Cryptography;
using System.Text.Json;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;

/// <summary>Seals and verifies repository-local NuGet packages from a successful release run.</summary>
public static class VerifiedNuGetHandoff
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task SealAsync(
        string runManifestPath,
        string handoffPath,
        RepoConfig config,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var run = await ReadAsync<RunManifest>(ResolvePath(context.RepositoryRoot, runManifestPath), cancellationToken);
        ValidateRun(run, config, context);
        await ValidateLockAsync(run, context.RepositoryRoot, cancellationToken);
        var packages = new List<VerifiedPackage>();
        foreach (var artifact in config.Artifacts ?? [])
        {
            var path = PackagePath(artifact, run.Version!);
            var built = run.Artifacts.Where(entry => entry.Name == artifact.Name && entry.Type == artifact.Type).ToArray();
            if (built.Length != 1 || !built[0].Built || built[0].Pushed)
            {
                throw new InvalidOperationException($"Artifact '{artifact.Name}' was not built exactly once without publication.");
            }

            packages.Add(new VerifiedPackage(path, await HashAsync(ResolvePath(context.RepositoryRoot, path), cancellationToken)));
        }

        if (packages.Count == 0)
        {
            throw new InvalidOperationException("No NuGet packages are configured for handoff.");
        }

        var destination = ResolvePath(context.RepositoryRoot, handoffPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new NuGetHandoff(run, packages), JsonOptions), cancellationToken);
    }

    public static async Task<VersionResult> VerifyAsync(
        string handoffPath,
        RepoConfig config,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var handoff = await ReadAsync<NuGetHandoff>(ResolvePath(context.RepositoryRoot, handoffPath), cancellationToken);
        if (handoff.Run is null || handoff.Packages is null)
        {
            throw new InvalidOperationException("Handoff is missing its release run or package inventory.");
        }

        ValidateRun(handoff.Run, config, context);
        await ValidateLockAsync(handoff.Run, context.RepositoryRoot, cancellationToken);
        var expected = (config.Artifacts ?? []).Select(artifact => PackagePath(artifact, handoff.Run.Version!)).ToArray();
        if (expected.Length == 0 || handoff.Packages.Count != expected.Length ||
            !expected.Order(StringComparer.Ordinal).SequenceEqual(handoff.Packages.Select(package => package.Path).Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Handoff package inventory does not match the configured release.");
        }

        foreach (var package in handoff.Packages)
        {
            var hash = await HashAsync(ResolvePath(context.RepositoryRoot, package.Path), cancellationToken);
            if (!string.Equals(hash, package.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Package hash mismatch: '{package.Path}'. Publication refused.");
            }
        }

        return handoff.Run.Version!;
    }

    private static void ValidateRun(RunManifest run, RepoConfig config, ExecutionContext context)
    {
        if (run.SchemaVersion != "1.0" || !run.Success || run.ExitCode != 0 ||
            run.CommandExecuted != "release" || run.Version is null ||
            run.Steps.Count == 0 || run.Steps.Any(step => !step.Success) ||
            run.Artifacts.Count != (config.Artifacts?.Count ?? 0) ||
            run.Artifacts.Any(artifact => !artifact.Built || artifact.Pushed) ||
            run.PushDecisions.Count != 0)
        {
            throw new InvalidOperationException("Handoff requires a successful, unpublished release run.");
        }

        if (string.IsNullOrWhiteSpace(context.CommitSha) || run.CommitSha != context.CommitSha ||
            run.ConfigHash != CanonicalConfigHasher.Compute(config) ||
            context.Version is null || run.Version.SemVer != context.Version.SemVer ||
            run.Version.NuGetVersion != context.Version.NuGetVersion ||
            (context.IsCi && (!run.IsCi || string.IsNullOrWhiteSpace(context.CiBuildId) || run.CiBuildId != context.CiBuildId)))
        {
            throw new InvalidOperationException("Handoff commit, config, version or CI run identity does not match this invocation.");
        }
    }

    private static string PackagePath(RepoArtifactConfig artifact, VersionResult version)
    {
        if (artifact.Type != "nuget" || string.IsNullOrWhiteSpace(artifact.Name))
        {
            throw new InvalidOperationException("Verified handoff currently supports explicitly named NuGet artifacts only.");
        }

        if ((artifact.Settings?.TryGetValue("symbols", out var symbols) == true &&
            symbols.ValueKind == JsonValueKind.Object && symbols.TryGetProperty("enabled", out var enabled) &&
            enabled.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)) ||
            (artifact.Settings?.TryGetValue("symbols.enabled", out var flatEnabled) == true &&
             flatEnabled.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Symbol publication is not supported by verified NuGet handoff.");
        }

        var output = artifact.Settings?.TryGetValue("output", out var setting) == true
            ? setting.GetString() ?? "artifacts/packages"
            : "artifacts/packages";
        return $"{output.TrimEnd('/', '\\')}/{artifact.Name}.{version.NuGetVersion ?? version.SemVer}.nupkg";
    }

    private static async Task ValidateLockAsync(RunManifest run, string root, CancellationToken cancellationToken)
    {
        var path = Path.Join(root, ".rexo", "rexo.lock.yaml");
        var currentHash = File.Exists(path) ? await HashAsync(path, cancellationToken) : null;
        if (run.PolicyLockHash != currentHash)
        {
            throw new InvalidOperationException("Handoff policy lock identity does not match this invocation.");
        }
    }

    private static string ResolvePath(string repositoryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Handoff paths must be portable repository-relative paths.");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Join(root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Handoff path escapes the repository.");
        }

        for (var current = fullPath; !string.Equals(current, root, comparison); current = Path.GetDirectoryName(current)!)
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Handoff paths must not traverse symbolic links.");
            }
        }

        return fullPath;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Handoff JSON must not be null.");
    }
}

public sealed record VerifiedPackage(string Path, string Sha256);
public sealed record NuGetHandoff(RunManifest Run, IReadOnlyList<VerifiedPackage> Packages);
