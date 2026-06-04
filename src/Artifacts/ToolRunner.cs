namespace Rexo.Artifacts;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Rexo.Core.Models;

/// <summary>
/// Shared helper that runs a CLI tool, falling back to a Dockerized version when the tool is not
/// installed on the host.  The Docker run mounts the working directory at <c>/work</c> and overrides
/// the container entrypoint so any image can be used regardless of its default CMD/ENTRYPOINT.
/// </summary>
public static class ToolRunner
{
    /// <summary>
    /// Try to run <paramref name="toolName"/> natively; if not found, fall back to Docker using
    /// <paramref name="resolvedDockerImage"/>.  Pass <paramref name="dockerToolName"/> when the
    /// executable name inside the container differs from the native name (e.g. Gradle wrapper
    /// vs the container's <c>gradle</c> binary).
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string toolName,
        IReadOnlyList<string> args,
        string workingDirectory,
        ArtifactConfig artifact,
        string resolvedDockerImage,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? envOverrides = null,
        string? dockerToolName = null)
    {
        var native = await TryRunNativeAsync(toolName, args, workingDirectory, cancellationToken, standardInput, envOverrides);
        if (native is not null)
        {
            return native.Value;
        }

        if (IsDockerFallbackDisabled(artifact))
        {
            throw new InvalidOperationException(
                $"{toolName} CLI is not installed and Docker fallback is disabled (settings.useDocker = false).");
        }

        Console.WriteLine($"  > {toolName} not found on host, falling back to Dockerized runtime ({resolvedDockerImage})");
        return await RunViaDockerAsync(
            dockerToolName ?? toolName,
            args,
            workingDirectory,
            resolvedDockerImage,
            cancellationToken,
            standardInput,
            envOverrides);
    }

    /// <summary>
    /// Attempts to run <paramref name="toolName"/> natively.  Returns <c>null</c> when the
    /// executable is not found (rather than throwing), allowing callers to try alternatives.
    /// </summary>
    public static async Task<(int ExitCode, string Output)?> TryRunNativeAsync(
        string toolName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? envOverrides = null)
    {
        try
        {
            return await RunProcessAsync(toolName, args, workingDirectory, cancellationToken, standardInput, envOverrides);
        }
        catch (Win32Exception ex) when (IsCommandNotFound(ex))
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="toolName"/> inside a Docker container by overriding the entrypoint.
    /// The working directory is mounted at <c>/work</c>.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunViaDockerAsync(
        string toolName,
        IReadOnlyList<string> args,
        string workingDirectory,
        string containerImage,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? envOverrides = null)
    {
        var dockerArgs = new List<string>
        {
            "run", "--rm",
            "--entrypoint", toolName,
            "-v", $"{workingDirectory}:/work",
            "-w", "/work",
        };

        if (standardInput is not null)
        {
            dockerArgs.Add("-i");
        }

        if (envOverrides is not null)
        {
            foreach (var (key, value) in envOverrides)
            {
                if (value is null)
                {
                    continue;
                }

                dockerArgs.Add("-e");
                dockerArgs.Add($"{key}={value}");
            }
        }

        dockerArgs.Add(containerImage);
        dockerArgs.AddRange(args);

        return await RunProcessAsync("docker", dockerArgs, workingDirectory, cancellationToken, standardInput, null);
    }

    /// <summary>Runs a process with the given argument list (no shell expansion).</summary>
    public static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? envOverrides = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (envOverrides is not null)
        {
            foreach (var (key, value) in envOverrides)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutBuffer = new System.Text.StringBuilder();
        var stderrBuffer = new System.Text.StringBuilder();

        var stdoutTask = ReadStreamAsync(
            process.StandardOutput,
            stdoutBuffer,
            line => Console.WriteLine(line),
            cancellationToken);
        var stderrTask = ReadStreamAsync(
            process.StandardError,
            stderrBuffer,
            line => Console.Error.WriteLine(line),
            cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        await stderrTask;

        var stdout = stdoutBuffer.ToString();
        var stderr = stderrBuffer.ToString();

        return (process.ExitCode, stdout + stderr);
    }

    private static async Task ReadStreamAsync(
        System.IO.TextReader reader,
        System.Text.StringBuilder buffer,
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

    /// <summary>Returns <c>true</c> when the artifact's <c>useDocker</c> setting is explicitly <c>false</c>.</summary>
    public static bool IsDockerFallbackDisabled(ArtifactConfig artifact) =>
        string.Equals(GetSetting(artifact, "useDocker"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a setting value from the artifact config as a string, handling all JSON value kinds.</summary>
    public static string? GetSetting(ArtifactConfig artifact, string key)
    {
        if (!artifact.Settings.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Splits a space-separated extra-args setting into individual tokens suitable for
    /// <see cref="RunAsync"/> argument lists.  Quoted values are not supported; use the
    /// provider's dedicated settings for values that may contain spaces.
    /// </summary>
    public static IReadOnlyList<string> ParseExtraArgs(string? extraArgs) =>
        string.IsNullOrWhiteSpace(extraArgs)
            ? Array.Empty<string>()
            : extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Formats an argument list for display, quoting tokens that contain spaces.</summary>
    public static string FormatArgs(IReadOnlyList<string> args) =>
        string.Join(" ", args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

    private static bool IsCommandNotFound(Win32Exception exception) =>
        exception.NativeErrorCode is 2 or 3;
}
