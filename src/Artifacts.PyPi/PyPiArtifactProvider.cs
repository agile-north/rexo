namespace Rexo.Artifacts.PyPi;

using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

/// <summary>
/// Python/PyPI artifact provider — <c>python -m build</c> for build, <c>python -m twine upload</c>
/// for push.  Type key: <c>pypi</c>.
/// <para>
/// Docker fallback image: <c>python:3-slim</c>.  The default image does not include
/// <c>build</c> or <c>twine</c>; supply a custom <c>dockerImage</c> with those packages
/// pre-installed, or ensure them in the project's virtual environment.
/// </para>
/// </summary>
public sealed class PyPiArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("pypi", new PyPiArtifactProvider());

    private const string DefaultContainerImage = "python:3-slim";

    public string Type => "pypi";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = GetSetting(artifact, "directory") ?? context.RepositoryRoot;
        var dockerImage = ResolveDockerImage(artifact);

        var args = new List<string> { "-m", "build" };
        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-build-args")));

        Console.WriteLine($"  > python {ToolRunner.FormatArgs(args)}");
        var result = await RunPythonAsync(args, workDir, artifact, dockerImage, cancellationToken);

        return new ArtifactBuildResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? Path.Combine(workDir, "dist") : null);
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
        var workDir = GetSetting(artifact, "directory") ?? context.RepositoryRoot;
        var distDir = GetSetting(artifact, "dist-dir") ?? "dist/*";
        var dockerImage = ResolveDockerImage(artifact);

        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var repositoryUrl = ResolveRepositoryUrl(artifact, fileEnv);
        var auth = ResolveAuth(artifact, repositoryUrl, fileEnv);
        IReadOnlyDictionary<string, string?>? envOverrides = auth.HasCredentials
            ? new Dictionary<string, string?>
            {
                ["TWINE_USERNAME"] = auth.Username,
                ["TWINE_PASSWORD"] = auth.Secret,
            }
            : null;

        var args = new List<string> { "-m", "twine", "upload", distDir };
        if (!string.IsNullOrWhiteSpace(repositoryUrl))
        {
            args.Add("--repository-url");
            args.Add(repositoryUrl);
        }

        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-push-args")));

        Console.WriteLine($"  > python {ToolRunner.FormatArgs(args)}");
        var result = await RunPythonAsync(args, workDir, artifact, dockerImage, cancellationToken, envOverrides);

        var tag = context.Version?.SemVer ?? "latest";
        return new ArtifactPushResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? [$"{artifact.Name}=={tag}"] : []);
    }

    /// <summary>
    /// Tries <c>python</c> then <c>python3</c> natively before falling back to Docker.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunPythonAsync(
        IReadOnlyList<string> args,
        string workDir,
        ArtifactConfig artifact,
        string dockerImage,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? envOverrides = null)
    {
        var native =
            await ToolRunner.TryRunNativeAsync("python", args, workDir, cancellationToken, envOverrides: envOverrides)
            ?? await ToolRunner.TryRunNativeAsync("python3", args, workDir, cancellationToken, envOverrides: envOverrides);

        if (native is not null)
        {
            return native.Value;
        }

        if (ToolRunner.IsDockerFallbackDisabled(artifact))
        {
            throw new InvalidOperationException(
                "python/python3 is not installed and Docker fallback is disabled (settings.useDocker = false).");
        }

        Console.WriteLine($"  > python not found on host, falling back to Dockerized runtime ({dockerImage})");
        return await ToolRunner.RunViaDockerAsync("python", args, workDir, dockerImage, cancellationToken, envOverrides: envOverrides);
    }

    private static string ResolveDockerImage(ArtifactConfig artifact) =>
        Environment.GetEnvironmentVariable("PYTHON_CONTAINER_IMAGE")
            ?? ToolRunner.GetSetting(artifact, "dockerImage")
            ?? DefaultContainerImage;

    private static string? ResolveRepositoryUrl(ArtifactConfig artifact, IReadOnlyDictionary<string, string> fileEnv) =>
        FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "PYPI_TARGET_REPOSITORY_URL",
            configuredEnvName: GetSetting(artifact, "target.repositoryUrlEnv"),
            configuredValue: GetSetting(artifact, "target.repositoryUrl"),
            fileEnv: fileEnv);

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        ToolRunner.GetSetting(artifact, key);

    /// <summary>
    /// Resolves PyPI / twine credentials. Order: target.apiTokenEnv (or TWINE_API_TOKEN)
    /// (username=__token__) → target.usernameEnv + target.passwordEnv
    /// (defaults TWINE_USERNAME/TWINE_PASSWORD) → SYSTEM_ACCESSTOKEN for Azure Artifacts feeds.
    /// Credentials are injected as TWINE_USERNAME / TWINE_PASSWORD environment variables.
    /// </summary>
    private static FeedAuthResolution ResolveAuth(
        ArtifactConfig artifact,
        string? repositoryUrl,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var apiToken = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "TWINE_API_TOKEN",
            configuredEnvName: GetSetting(artifact, "target.apiTokenEnv"),
            fileEnv: fileEnv);
        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            return new FeedAuthResolution(true, "__token__", apiToken, repositoryUrl, null, "env");
        }

        var username = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "TWINE_USERNAME",
            configuredEnvName: GetSetting(artifact, "target.usernameEnv"),
            fileEnv: fileEnv);
        var password = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "TWINE_PASSWORD",
            configuredEnvName: GetSetting(artifact, "target.passwordEnv"),
            fileEnv: fileEnv);

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            return new FeedAuthResolution(true, username, password, repositoryUrl, null, "env");
        }

        var azureFallback = FeedAuthResolver.ResolveAzureArtifactsTokenAuth(
            endpoint: repositoryUrl,
            fileEnv: fileEnv,
            username: "VssSessionToken",
            ciInferenceEnabled: ciInferenceEnabled);
        if (azureFallback.HasCredentials)
        {
            return azureFallback;
        }

        return new FeedAuthResolution(false, null, null, repositoryUrl, null, "none");
    }
}
