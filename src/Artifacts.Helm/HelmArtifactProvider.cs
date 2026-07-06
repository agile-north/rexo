namespace Rexo.Artifacts.Helm;

using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

/// <summary>
/// Helm (non-OCI / ChartMuseum) artifact provider.
/// Build: <c>helm package</c>.  Push: <c>helm cm-push</c> for http/https repos,
/// or file copy for filesystem destinations.  Type key: <c>helm</c>.
/// </summary>
public sealed class HelmArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("helm", new HelmArtifactProvider());

    private const string DefaultContainerImage = "alpine/helm:3.17.3";
    private readonly Func<ArtifactConfig, IReadOnlyList<string>, string, string, CancellationToken, Task<(int ExitCode, string Output)>> _runHelmAsync;

    public HelmArtifactProvider(
        Func<ArtifactConfig, IReadOnlyList<string>, string, string, CancellationToken, Task<(int ExitCode, string Output)>>? runHelmAsync = null)
    {
        _runHelmAsync = runHelmAsync ?? RunHelmAsync;
    }

    public string Type => "helm";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = context.RepositoryRoot;
        var dockerImage = ResolveDockerImage(artifact);
        var chartDir = GetSetting(artifact, "chart-directory") ?? ".";
        var outputDir = GetSetting(artifact, "output-directory") ?? workDir;

        var args = new List<string> { "package", chartDir, "--destination", outputDir };
        if (context.Version is not null)
        {
            args.Add("--version");
            args.Add(context.Version.SemVer);
            args.Add("--app-version");
            args.Add(context.Version.SemVer);
        }

        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-build-args")));

        Console.WriteLine($"  > helm {ToolRunner.FormatArgs(args)}");
        var result = await _runHelmAsync(artifact, args, workDir, dockerImage, cancellationToken);
        if (result.ExitCode != 0 && ShouldRetryWithDependencyUpdate(result.Output))
        {
            var dependencyArgs = new List<string> { "dependency", "update", chartDir };
            Console.WriteLine($"  > helm {ToolRunner.FormatArgs(dependencyArgs)}");
            var dependencyResult = await _runHelmAsync(artifact, dependencyArgs, workDir, dockerImage, cancellationToken);
            if (dependencyResult.ExitCode != 0)
            {
                return new ArtifactBuildResult(artifact.Name, false, null);
            }

            Console.WriteLine($"  > helm {ToolRunner.FormatArgs(args)}");
            result = await _runHelmAsync(artifact, args, workDir, dockerImage, cancellationToken);
        }

        return new ArtifactBuildResult(artifact.Name, result.ExitCode == 0, result.ExitCode == 0 ? outputDir : null);
    }

    public Task<ArtifactTagResult> TagAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var tags = context.Version is not null ? new[] { context.Version.SemVer } : Array.Empty<string>();
        return Task.FromResult(new ArtifactTagResult(artifact.Name, true, tags));
    }

    public async Task<ArtifactPushResult> PushAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = context.RepositoryRoot;
        var dockerImage = ResolveDockerImage(artifact);
        var outputDir = GetSetting(artifact, "output-directory") ?? workDir;
        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var repo = ResolveRepository(artifact, fileEnv);

        if (string.IsNullOrWhiteSpace(repo))
        {
            Console.WriteLine("  ! helm push skipped: no repository configured");
            return new ArtifactPushResult(artifact.Name, true, []);
        }

        // Filesystem destination — just copy tgz files
        if (!repo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var tgz in Directory.EnumerateFiles(outputDir, "*.tgz"))
            {
                var dest = Path.Combine(repo, Path.GetFileName(tgz));
                File.Copy(tgz, dest, overwrite: true);
                Console.WriteLine($"  > copied {Path.GetFileName(tgz)} -> {repo}");
            }

            return new ArtifactPushResult(artifact.Name, true, [repo]);
        }

        var auth = ResolveRepoAuth(artifact, repo, fileEnv);

        var args = new List<string> { "cm-push", outputDir, repo };
        if (auth.HasCredentials)
        {
            args.Add("--username");
            args.Add(auth.Username ?? string.Empty);
            args.Add("--password");
            args.Add(auth.Secret ?? string.Empty);
        }

        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-push-args")));

        Console.WriteLine($"  > helm {ToolRunner.FormatArgs(args)}");
        var result = await ToolRunner.RunAsync("helm", args, workDir, artifact, dockerImage, cancellationToken);

        return new ArtifactPushResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? [$"{repo}/{artifact.Name}"] : []);
    }

    private static string ResolveDockerImage(ArtifactConfig artifact) =>
        Environment.GetEnvironmentVariable("HELM_CONTAINER_IMAGE")
            ?? ToolRunner.GetSetting(artifact, "dockerImage")
            ?? DefaultContainerImage;

    private static string? ResolveRepository(ArtifactConfig artifact, IReadOnlyDictionary<string, string> fileEnv) =>
        FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_TARGET_REPOSITORY",
            configuredEnvName: GetSetting(artifact, "target.repositoryEnv"),
            configuredValue: GetSetting(artifact, "target.repository"),
            fileEnv: fileEnv);

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        ToolRunner.GetSetting(artifact, key);

    private static bool ShouldRetryWithDependencyUpdate(string output) =>
        output.Contains("found in Chart.yaml, but missing in charts/ directory", StringComparison.OrdinalIgnoreCase);

    private static Task<(int ExitCode, string Output)> RunHelmAsync(
        ArtifactConfig artifact,
        IReadOnlyList<string> args,
        string workingDirectory,
        string dockerImage,
        CancellationToken cancellationToken) =>
        ToolRunner.RunAsync("helm", args, workingDirectory, artifact, dockerImage, cancellationToken);

    /// <summary>
    /// Resolves Chart Museum repository credentials from target.usernameEnv /
    /// target.passwordEnv (defaults HELM_REPO_USERNAME/HELM_REPO_PASSWORD).
    /// Credentials are passed as --username / --password args to
    /// helm cm-push; they are not injected as env vars.
    /// </summary>
    private static FeedAuthResolution ResolveRepoAuth(
        ArtifactConfig artifact,
        string? configuredRepo,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var username = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "HELM_REPO_USERNAME",
            configuredEnvName: GetSetting(artifact, "target.usernameEnv"),
            fileEnv: fileEnv);
        var password = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "HELM_REPO_PASSWORD",
            configuredEnvName: GetSetting(artifact, "target.passwordEnv"),
            fileEnv: fileEnv);
        var endpoint = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_REPO_URL",
            configuredEnvName: GetSetting(artifact, "target.repositoryEnv"),
            configuredValue: configuredRepo,
            fileEnv: fileEnv);

        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
        {
            return new FeedAuthResolution(false, null, null, endpoint, null, "none");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new FeedAuthResolution(false, null, null, endpoint,
                "HELM_REPO_USERNAME and HELM_REPO_PASSWORD must both be set.", "env");
        }

        return new FeedAuthResolution(true, username, password, endpoint, null, "env");
    }
}
