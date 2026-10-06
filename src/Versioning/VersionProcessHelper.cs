namespace Rexo.Versioning;

using System.Diagnostics;
using Rexo.Core.Models;
using Rexo.Templating;

/// <summary>
/// Shared process-execution utilities used by version providers.
/// </summary>
internal static class VersionProcessHelper
{
    private const string DockerContainerWorkDir = "/repo";

    // -------------------------------------------------------------------------
    // Host process execution
    // -------------------------------------------------------------------------

    internal static async Task<(int exitCode, string output)> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await outputTask);
        }
        catch (Exception)
        {
            return (-1, string.Empty);
        }
    }

    // -------------------------------------------------------------------------
    // Docker execution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs a command inside a Docker container with the repository root mounted at
    /// <c>/repo</c>. Each element of <paramref name="containerArgs"/> is passed as a
    /// separate argument to <c>docker run</c>, which allows paths with spaces and
    /// shell-script strings to be forwarded correctly without additional escaping.
    /// </summary>
    internal static async Task<(int exitCode, string output)> RunDockerAsync(
        string image,
        string repoRoot,
        IReadOnlyList<string> containerArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--rm");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add($"{repoRoot}:/repo");
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(DockerContainerWorkDir);
            psi.ArgumentList.Add(image);
            foreach (var arg in containerArgs)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await outputTask);
        }
        catch (Exception)
        {
            return (-1, string.Empty);
        }
    }

    // -------------------------------------------------------------------------
    // Settings helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> unless the user has explicitly set
    /// <c>settings["useDocker"] = "false"</c>.  Docker fallback is opt-out: when
    /// Docker is available and the native tool is not installed, providers will
    /// automatically retry using a Docker image.
    /// </summary>
    internal static bool UseDockerFallback(VersioningConfig config) =>
        !string.Equals(
            config.Settings?.GetValueOrDefault("useDocker"),
            "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>settings["dockerImage"]</c> when set, otherwise
    /// <paramref name="defaultImage"/>. Template expressions are rendered against
    /// the current execution context before use.
    /// </summary>
    internal static string GetDockerImage(VersioningConfig config, ExecutionContext context, string defaultImage)
    {
        var image = config.Settings?.GetValueOrDefault("dockerImage");
        if (string.IsNullOrWhiteSpace(image))
        {
            return defaultImage;
        }

        var renderedImage = new TemplateRenderer().Render(image!, context);
        return string.IsNullOrWhiteSpace(renderedImage) ? defaultImage : renderedImage;
    }

    // -------------------------------------------------------------------------
    // Shell-script runner (SDK-based Docker images)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs a shell script inside an SDK Docker image.  Automatically prepends
    /// <c>git config --global --add safe.directory /repo</c> so Git operations
    /// work correctly even when the mounted directory is owned by a different UID.
    /// </summary>
    internal static Task<(int exitCode, string output)> RunSdkDockerScriptAsync(
        string image,
        string repoRoot,
        string shellScript,
        CancellationToken cancellationToken)
    {
        var fullScript = $"git config --global --add safe.directory /repo && {shellScript}";
        return RunDockerAsync(image, repoRoot, ["sh", "-c", fullScript], cancellationToken);
    }
}
