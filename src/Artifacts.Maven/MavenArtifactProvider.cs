namespace Rexo.Artifacts.Maven;

using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

/// <summary>
/// Maven artifact provider — <c>mvn package</c> for build, <c>mvn deploy</c> for push.
/// Type key: <c>maven</c>.
/// <para>
/// Feed credentials are injected as <c>MAVEN_REPO_USERNAME</c> / <c>MAVEN_REPO_PASSWORD</c>
/// environment variables.  Reference these in your <c>settings.xml</c> via
/// <c>${env.MAVEN_REPO_USERNAME}</c>.
/// </para>
/// </summary>
public sealed class MavenArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("maven", new MavenArtifactProvider());

    private const string DefaultContainerImage = "maven:3-eclipse-temurin-21";

    public string Type => "maven";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = context.RepositoryRoot;
        var dockerImage = ResolveDockerImage(artifact);

        var args = BuildMvnArgs(artifact, "package", context);

        Console.WriteLine($"  > mvn {ToolRunner.FormatArgs(args)}");
        var result = await ToolRunner.RunAsync("mvn", args, workDir, artifact, dockerImage, cancellationToken);

        return new ArtifactBuildResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? Path.Combine(workDir, "target") : null);
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

        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var auth = ResolveAuth(artifact, fileEnv);
        IReadOnlyDictionary<string, string?>? envOverrides = auth.HasCredentials
            ? new Dictionary<string, string?>
            {
                ["MAVEN_REPO_USERNAME"] = auth.Username,
                ["MAVEN_REPO_PASSWORD"] = auth.Secret,
            }
            : null;

        var args = BuildMvnArgs(artifact, "deploy", context, extraArgsSetting: "extra-push-args");

        Console.WriteLine($"  > mvn {ToolRunner.FormatArgs(args)}");
        var result = await ToolRunner.RunAsync("mvn", args, workDir, artifact, dockerImage, cancellationToken, envOverrides: envOverrides);

        return new ArtifactPushResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? [$"{artifact.Name}:{context.Version?.SemVer ?? "latest"}"] : []);
    }

    private static List<string> BuildMvnArgs(
        ArtifactConfig artifact,
        string goal,
        ExecutionContext context,
        string extraArgsSetting = "extra-build-args")
    {
        var project = GetSetting(artifact, "project");
        var profiles = GetSetting(artifact, "profiles");
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(project))
        {
            args.Add("-f");
            args.Add(project);
        }

        args.Add(goal);
        args.Add("-DskipTests");

        if (!string.IsNullOrWhiteSpace(profiles))
        {
            args.Add($"-P{profiles}");
        }

        if (context.Version is not null)
        {
            args.Add($"-Drevision={context.Version.SemVer}");
        }

        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, extraArgsSetting)));
        return args;
    }

    private static string ResolveDockerImage(ArtifactConfig artifact) =>
        Environment.GetEnvironmentVariable("MVN_CONTAINER_IMAGE")
            ?? ToolRunner.GetSetting(artifact, "dockerImage")
            ?? DefaultContainerImage;

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        ToolRunner.GetSetting(artifact, key);

    /// <summary>
    /// Resolves Maven repository credentials.  Order: target.usernameEnv + target.passwordEnv
    /// (defaults MAVEN_REPO_USERNAME/MAVEN_REPO_PASSWORD) → SYSTEM_ACCESSTOKEN
    /// (Azure Artifacts CI-native).
    /// Credentials are injected as env vars; reference them in settings.xml via
    /// <c>${env.MAVEN_REPO_USERNAME}</c> and <c>${env.MAVEN_REPO_PASSWORD}</c>.
    /// </summary>
    private static FeedAuthResolution ResolveAuth(
        ArtifactConfig artifact,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var username = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "MAVEN_REPO_USERNAME",
            configuredEnvName: GetSetting(artifact, "target.usernameEnv"),
            fileEnv: fileEnv);
        var password = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "MAVEN_REPO_PASSWORD",
            configuredEnvName: GetSetting(artifact, "target.passwordEnv"),
            fileEnv: fileEnv);

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            return new FeedAuthResolution(true, username, password, null, null, "env");
        }

        var azureFallback = FeedAuthResolver.ResolveAzureArtifactsTokenAuth(
            endpoint: null,
            fileEnv: fileEnv,
            username: "VssSessionToken",
            allowWhenEndpointUnknown: true,
            ciInferenceEnabled: ciInferenceEnabled);
        if (azureFallback.HasCredentials)
        {
            return azureFallback;
        }

        return new FeedAuthResolution(false, null, null, null, null, "none");
    }
}
