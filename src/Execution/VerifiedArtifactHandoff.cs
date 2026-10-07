namespace Rexo.Execution;

using System.Security.Cryptography;
using System.Text.Json;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Core.Abstractions;
using Rexo.Artifacts;

/// <summary>Seals and verifies provider-described outputs from a successful release run.</summary>
public static class VerifiedArtifactHandoff
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task SealAsync(
        string runManifestPath,
        string handoffPath,
        RepoConfig config,
        ExecutionContext context,
        ArtifactProviderRegistry providers,
        CancellationToken cancellationToken)
    {
        var run = await ReadAsync<RunManifest>(ResolvePath(context.RepositoryRoot, runManifestPath), cancellationToken);
        ValidateRun(run, config, context);
        await ValidateLockAsync(run, context.RepositoryRoot, cancellationToken);
        var artifacts = new List<PreparedArtifact>();
        foreach (var artifact in config.Artifacts ?? [])
        {
            var artifactConfig = ConfigCommandLoader.ToArtifactConfig(artifact, config, ConfigCommandLoader.ResolveOutputRoot(config, context));
            var built = run.Artifacts.Where(entry => entry.Name == artifactConfig.Name && entry.Type == artifact.Type).ToArray();
            if (built.Length != 1 || !built[0].Built || built[0].Pushed)
            {
                throw new InvalidOperationException($"Artifact '{artifact.Name}' was not built exactly once without publication.");
            }

            var provider = ResolveProvider(providers, artifact.Type);
            var prepared = await provider.PrepareAsync(artifactConfig, context, cancellationToken);
            if (prepared.Type != artifactConfig.Type || prepared.Name != artifactConfig.Name)
            {
                throw new InvalidOperationException("Provider returned prepared outputs for a different artifact.");
            }
            await provider.ValidatePreparedAsync(artifactConfig, prepared, context, cancellationToken);
            artifacts.Add(prepared);
        }

        if (artifacts.Count == 0 ||
            artifacts.Select(artifact => (artifact.Type, artifact.Name)).Distinct().Count() != artifacts.Count)
        {
            throw new InvalidOperationException("Handoff requires a nonempty, unique artifact inventory.");
        }

        var destination = ResolvePath(context.RepositoryRoot, handoffPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new ArtifactHandoff(run, artifacts), JsonOptions), cancellationToken);
    }

    public static async Task<ArtifactHandoff> VerifyAsync(
        string handoffPath,
        RepoConfig config,
        ExecutionContext context,
        ArtifactProviderRegistry providers,
        CancellationToken cancellationToken)
    {
        var handoff = await ReadAsync<ArtifactHandoff>(ResolvePath(context.RepositoryRoot, handoffPath), cancellationToken);
        if (handoff.Run is null || handoff.Artifacts is null)
        {
            throw new InvalidOperationException("Handoff is missing its release run or artifact inventory.");
        }

        ValidateRun(handoff.Run, config, context);
        await ValidateLockAsync(handoff.Run, context.RepositoryRoot, cancellationToken);
        var expected = config.Artifacts ?? [];
        if (expected.Count == 0 || handoff.Artifacts.Count != expected.Count ||
            handoff.Artifacts.Select(artifact => (artifact.Type, artifact.Name)).Distinct().Count() != expected.Count)
        {
            throw new InvalidOperationException("Handoff artifact inventory does not match the configured release.");
        }

        foreach (var artifact in expected)
        {
            var artifactConfig = ConfigCommandLoader.ToArtifactConfig(artifact, config, ConfigCommandLoader.ResolveOutputRoot(config, context));
            var prepared = handoff.Artifacts.SingleOrDefault(entry => entry.Type == artifact.Type && entry.Name == artifactConfig.Name);
            if (prepared is null)
            {
                throw new InvalidOperationException($"Handoff has no prepared output for '{artifactConfig.Name}'.");
            }
            if (handoff.Run.Artifacts.Count(entry => entry.Type == artifact.Type && entry.Name == artifactConfig.Name) != 1)
            {
                throw new InvalidOperationException($"Release evidence has no unique build result for '{artifactConfig.Name}'.");
            }
            await ResolveProvider(providers, artifact.Type).ValidatePreparedAsync(artifactConfig, prepared, context, cancellationToken);
        }

        return handoff;
    }

    private static void ValidateRun(RunManifest run, RepoConfig config, ExecutionContext context)
    {
        if (run.SchemaVersion != "1.0" || !run.Success || run.ExitCode != 0 ||
            run.CommandExecuted != "release" || run.Version is null ||
            run.Steps is null || run.Steps.Count == 0 || run.Steps.Any(step => !step.Success) ||
            run.Artifacts is null || run.Artifacts.Count != (config.Artifacts?.Count ?? 0) ||
            run.Artifacts.Any(artifact => !artifact.Built || artifact.Pushed) ||
            run.PushDecisions is null || run.PushDecisions.Count != 0)
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

    private static IPreparedArtifactProvider ResolveProvider(ArtifactProviderRegistry providers, string type) =>
        providers.Resolve(type) as IPreparedArtifactProvider
        ?? throw new InvalidOperationException($"Artifact provider '{type}' does not support prepared publication.");

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
        return PreparedArtifactFiles.ResolvePath(repositoryRoot, path);
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

public sealed record ArtifactHandoff(RunManifest Run, IReadOnlyList<PreparedArtifact> Artifacts);
