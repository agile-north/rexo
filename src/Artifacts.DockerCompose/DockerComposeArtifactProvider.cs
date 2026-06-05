namespace Rexo.Artifacts.DockerCompose;

using System.Diagnostics;
using System.Text;
using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

/// <summary>
/// Docker Compose artifact provider.  Type key: <c>docker-compose</c>.
/// <para>
/// Docker Compose IS Docker — no Docker fallback image is used.
/// Instead, <c>docker login</c> is performed before push when a registry is configured.
/// </para>
/// </summary>
public sealed class DockerComposeArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("docker-compose", new DockerComposeArtifactProvider());

    public string Type => "docker-compose";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = context.RepositoryRoot;
        var args = BuildComposeArgs(artifact, "build");
        args.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-build-args")));

        Console.WriteLine($"  > docker {ToolRunner.FormatArgs(args)}");
        var result = await RunDockerAsync(args, workDir, cancellationToken);

        return new ArtifactBuildResult(artifact.Name, result.ExitCode == 0, result.ExitCode == 0 ? workDir : null);
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
        var fileEnv = RepositoryEnvironmentFiles.Load(context.RepositoryRoot);
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var registry = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "DOCKER_COMPOSE_TARGET_REGISTRY",
            configuredEnvName: GetSetting(artifact, "target.registryEnv"),
            configuredValue: GetSetting(artifact, "target.registry"),
            fileEnv: fileEnv);

        if (string.IsNullOrWhiteSpace(registry))
        {
            registry = FeedAuthResolver.ResolveImplicitContainerRegistry(fileEnv, ciInferenceEnabled);
        }

        // docker login before push
        if (!string.IsNullOrWhiteSpace(registry))
        {
            var auth = FeedAuthResolver.ResolveDocker(
                configuredRegistry: registry,
                inferredRegistry: null,
                fileEnv: fileEnv,
                configuredUsernameEnv: GetSetting(artifact, "target.usernameEnv"),
                configuredPasswordEnv: GetSetting(artifact, "target.passwordEnv"),
                configuredRegistryEnv: GetSetting(artifact, "target.loginRegistryEnv"),
                ciInferenceEnabled: ciInferenceEnabled);
            if (auth.HasCredentials)
            {
                var loginArgs = new List<string> { "login", registry, "-u", auth.Username ?? string.Empty, "--password-stdin" };
                Console.WriteLine($"  > docker login {registry}");
                await RunDockerWithStdinAsync(loginArgs, workDir, auth.Secret ?? string.Empty, cancellationToken);
            }
            else if (FeedAuthResolver.ShouldWarnOnMissingContainerRegistryCredentials(auth.Endpoint, fileEnv))
            {
                Console.WriteLine($"  Warning: no Docker Compose login credentials resolved for '{auth.Endpoint}'. Push may fail unless the registry allows anonymous access.");
            }
        }

        var pushArgs = BuildComposeArgs(artifact, "push");
        pushArgs.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-push-args")));

        Console.WriteLine($"  > docker {ToolRunner.FormatArgs(pushArgs)}");
        var result = await RunDockerAsync(pushArgs, workDir, cancellationToken);

        return new ArtifactPushResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? [$"{artifact.Name}:{context.Version?.SemVer ?? "latest"}"] : []);
    }

    private static List<string> BuildComposeArgs(ArtifactConfig artifact, string subcommand)
    {
        var file = GetSetting(artifact, "file");
        var projectName = GetSetting(artifact, "project-name");
        var services = GetSetting(artifact, "services");

        var args = new List<string> { "compose" };

        if (!string.IsNullOrWhiteSpace(file))
        {
            args.Add("-f");
            args.Add(file);
        }

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            args.Add("-p");
            args.Add(projectName);
        }

        args.Add(subcommand);

        if (!string.IsNullOrWhiteSpace(services))
        {
            args.AddRange(ToolRunner.ParseExtraArgs(services));
        }

        return args;
    }

    private static async Task<(int ExitCode, string Output)> RunDockerAsync(
        IReadOnlyList<string> args,
        string workDir,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutBuffer, line => Console.WriteLine(line), cancellationToken);
        var stderrTask = ReadStreamAsync(process.StandardError, stderrBuffer, line => Console.Error.WriteLine(line), cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        await stderrTask;

        var output = stdoutBuffer.ToString();
        var error = stderrBuffer.ToString();

        if (!string.IsNullOrWhiteSpace(output) && !output.EndsWith('\n')) Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(error) && !error.EndsWith('\n')) Console.Error.WriteLine();

        return (process.ExitCode, output);
    }

    private static async Task RunDockerWithStdinAsync(
        IReadOnlyList<string> args,
        string workDir,
        string stdinContent,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        await process.StandardInput.WriteAsync(stdinContent);
        process.StandardInput.Close();
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutBuffer, line => Console.WriteLine(line), cancellationToken);
        var stderrTask = ReadStreamAsync(process.StandardError, stderrBuffer, line => Console.Error.WriteLine(line), cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        await stderrTask;

        var output = stdoutBuffer.ToString();
        var error = stderrBuffer.ToString();

        if (!string.IsNullOrWhiteSpace(output) && !output.EndsWith('\n')) Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(error) && !error.EndsWith('\n')) Console.Error.WriteLine();
    }

    private static async Task ReadStreamAsync(
        TextReader reader,
        StringBuilder buffer,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            buffer.AppendLine(line);
            onLine(line);
        }
    }

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        ToolRunner.GetSetting(artifact, key);
}
