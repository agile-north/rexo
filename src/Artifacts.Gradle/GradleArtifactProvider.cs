namespace Rexo.Artifacts.Gradle;

using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

/// <summary>
/// Gradle artifact provider — <c>gradle build</c> / wrapper for build, <c>gradle publish</c> for push.
/// Type key: <c>gradle</c>.
/// <para>
/// Feed credentials are resolved from <c>ORG_GRADLE_PROJECT_mavenUsername</c> and
/// <c>ORG_GRADLE_PROJECT_mavenPassword</c>.  Gradle automatically exposes these as project
/// properties.  Docker fallback uses the container's <c>gradle</c> binary rather than the
/// host wrapper.
/// </para>
/// </summary>
public sealed class GradleArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("gradle", new GradleArtifactProvider());

    private const string DefaultContainerImage = "gradle:8-jdk21";

    public string Type => "gradle";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = GetWorkDir(artifact, context);
        var useWrapper = IsTrueOrDefault(artifact, "wrapper", defaultValue: true);
        var nativeExe = ResolveNativeExe(useWrapper, workDir);
        var dockerImage = ResolveDockerImage(artifact);

        var tasks = GetSetting(artifact, "tasks") ?? "build";
        var args = new List<string>(ToolRunner.ParseExtraArgs(tasks));
        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-build-args")));

        Console.WriteLine($"  > {nativeExe} {ToolRunner.FormatArgs(args)}");
        var result = await ToolRunner.RunAsync(
            nativeExe,
            args,
            workDir,
            artifact,
            dockerImage,
            cancellationToken,
            dockerToolName: "gradle");

        return new ArtifactBuildResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? Path.Combine(workDir, "build", "libs") : null);
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
        var workDir = GetWorkDir(artifact, context);
        var useWrapper = IsTrueOrDefault(artifact, "wrapper", defaultValue: true);
        var nativeExe = ResolveNativeExe(useWrapper, workDir);
        var dockerImage = ResolveDockerImage(artifact);

        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var auth = ResolveAuth(artifact, fileEnv);
        IReadOnlyDictionary<string, string?>? envOverrides = auth.HasCredentials
            ? new Dictionary<string, string?>
            {
                ["ORG_GRADLE_PROJECT_mavenUsername"] = auth.Username,
                ["ORG_GRADLE_PROJECT_mavenPassword"] = auth.Secret,
            }
            : null;

        var args = new List<string> { "publish" };
        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-push-args")));

        Console.WriteLine($"  > {nativeExe} {ToolRunner.FormatArgs(args)}");
        var result = await ToolRunner.RunAsync(
            nativeExe,
            args,
            workDir,
            artifact,
            dockerImage,
            cancellationToken,
            envOverrides: envOverrides,
            dockerToolName: "gradle");

        return new ArtifactPushResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? [$"{artifact.Name}:{context.Version?.SemVer ?? "latest"}"] : []);
    }

    private static string GetWorkDir(ArtifactConfig artifact, ExecutionContext context)
    {
        var dir = GetSetting(artifact, "directory");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return context.RepositoryRoot;
        }

        return Path.IsPathRooted(dir) ? dir : Path.Combine(context.RepositoryRoot, dir);
    }

    private static string ResolveNativeExe(bool useWrapper, string workDir)
    {
        if (useWrapper)
        {
            if (File.Exists(Path.Combine(workDir, "gradlew.bat")))
            {
                return "gradlew.bat";
            }

            if (File.Exists(Path.Combine(workDir, "gradlew")))
            {
                return "./gradlew";
            }
        }

        return "gradle";
    }

    private static string ResolveDockerImage(ArtifactConfig artifact) =>
        Environment.GetEnvironmentVariable("GRADLE_CONTAINER_IMAGE")
            ?? ToolRunner.GetSetting(artifact, "dockerImage")
            ?? DefaultContainerImage;

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        ToolRunner.GetSetting(artifact, key);

    private static bool IsTrueOrDefault(ArtifactConfig artifact, string key, bool defaultValue)
    {
        var val = ToolRunner.GetSetting(artifact, key);
        if (val is null)
        {
            return defaultValue;
        }

        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves Gradle publishing credentials.  Order:
    /// target.usernameEnv + target.passwordEnv (defaults
    /// ORG_GRADLE_PROJECT_mavenUsername/ORG_GRADLE_PROJECT_mavenPassword; standard Maven
    /// publishing via Gradle, automatically surfaced as project properties) →
    /// target.publishKeyEnv + target.publishSecretEnv (defaults
    /// GRADLE_PUBLISH_KEY/GRADLE_PUBLISH_SECRET for Gradle Plugin Portal).
    /// Credentials are forwarded as env vars so Gradle picks them up as project properties.
    /// </summary>
    private static FeedAuthResolution ResolveAuth(
        ArtifactConfig artifact,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var username = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "ORG_GRADLE_PROJECT_mavenUsername",
            configuredEnvName: GetSetting(artifact, "target.usernameEnv"),
            fileEnv: fileEnv);
        var password = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "ORG_GRADLE_PROJECT_mavenPassword",
            configuredEnvName: GetSetting(artifact, "target.passwordEnv"),
            fileEnv: fileEnv);

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            return new FeedAuthResolution(true, username, password, null, null, "env");
        }

        var pluginKey = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "GRADLE_PUBLISH_KEY",
            configuredEnvName: GetSetting(artifact, "target.publishKeyEnv"),
            fileEnv: fileEnv);
        var pluginSecret = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "GRADLE_PUBLISH_SECRET",
            configuredEnvName: GetSetting(artifact, "target.publishSecretEnv"),
            fileEnv: fileEnv);
        if (!string.IsNullOrWhiteSpace(pluginKey) && !string.IsNullOrWhiteSpace(pluginSecret))
        {
            return new FeedAuthResolution(true, pluginKey, pluginSecret, null, null, "env");
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
