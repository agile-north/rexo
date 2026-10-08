namespace Rexo.Versioning;

using Rexo.Core.Abstractions;
using Rexo.Core.Models;

/// <summary>
/// Automatically selects the best version provider by detecting configuration file
/// evidence in the repository root, then falling back through a sensible chain.
///
/// Detection order:
/// <list type="number">
///   <item><c>nbgv</c> — <c>version.json</c> or <c>nbgv.json</c> exists at repository root.</item>
///   <item><c>gitversion</c> — <c>GitVersion.yml</c> or <c>GitVersion.yaml</c> exists at repository root or <c>.gitversion/</c>.</item>
///   <item><c>minver</c> — <c>.minverrc</c> exists at repository root.</item>
///   <item><c>git</c> — <c>.git</c> directory exists (use most recent SemVer tag).</item>
///   <item><c>fixed</c> — universal fallback using the configured fallback version.</item>
/// </list>
/// </summary>
public sealed class AutoVersionProvider : IVersionProvider
{
    public async Task<VersionResult> ResolveAsync(
        VersioningConfig config,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var root = context.RepositoryRoot;

        var detected = DetectProvider(root);
        Console.WriteLine($"  [auto] Detected version provider: {detected}");

        IVersionProvider provider = detected switch
        {
            "nbgv"       => new NbgvVersionProvider(),
            "gitversion" => new GitVersionVersionProvider(),
            "minver"     => new MinVerVersionProvider(),
            "git"        => new GitTagVersionProvider(),
            _            => new FixedVersionProvider(),
        };

        return await provider.ResolveAsync(config, context, cancellationToken);
    }

    public static string DetectProvider(string repositoryRoot)
    {
        // nbgv: version.json or nbgv.json at repo root (Nerdbank.GitVersioning convention)
        if (File.Exists(Path.Join(repositoryRoot, "version.json")) ||
            File.Exists(Path.Join(repositoryRoot, "nbgv.json")))
        {
            return "nbgv";
        }

        // gitversion: GitVersion.yml / .yaml at repo root or inside .gitversion/
        if (File.Exists(Path.Join(repositoryRoot, "GitVersion.yml")) ||
            File.Exists(Path.Join(repositoryRoot, "GitVersion.yaml")) ||
            File.Exists(Path.Join(repositoryRoot, ".gitversion", "GitVersion.yml")) ||
            File.Exists(Path.Join(repositoryRoot, ".gitversion", "GitVersion.yaml")))
        {
            return "gitversion";
        }

        // minver: .minverrc at repo root
        if (File.Exists(Path.Join(repositoryRoot, ".minverrc")))
        {
            return "minver";
        }

        // git: .git directory present — use tag-based versioning
        if (Directory.Exists(Path.Join(repositoryRoot, ".git")))
        {
            return "git";
        }

        return "fixed";
    }
}
