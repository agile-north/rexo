namespace Rexo.Execution;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

public sealed record ShellRunResult(int ExitCode, string Stdout, string Stderr);

public static class ShellRunner
{
    public static Task<ShellRunResult> RunAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onStdout = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(command, workingDirectory, null, null, null, environment, onStdout, cancellationToken);

    public static async Task<ShellRunResult> RunAsync(
        string command,
        string workingDirectory,
        string? user,
        string? domain,
        string? password,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onStdout = null,
        CancellationToken cancellationToken = default)
    {
        var psi = CreateShellStartInfo(command, workingDirectory, user, domain, password, environment);
        using var process = new Process { StartInfo = psi };
        var stdoutLines = new System.Text.StringBuilder();
        var stderrLines = new System.Text.StringBuilder();

        process.Start();

        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutLines, onStdout, cancellationToken);
        var stderrTask = ReadStreamAsync(process.StandardError, stderrLines, null, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        await stderrTask;

        return new ShellRunResult(
            process.ExitCode,
            stdoutLines.ToString().Trim(),
            stderrLines.ToString().Trim());
    }

    internal static ProcessStartInfo CreateShellStartInfo(
        string command,
        string workingDirectory,
        string? user,
        string? domain,
        string? password,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = isWindows
            ? new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"{command}\"")
            : new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", command },
            };

        psi.WorkingDirectory = workingDirectory;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        if (user is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(user);
            psi.UserName = user;
            if (OperatingSystem.IsWindows())
            {
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Windows host user execution requires a resolved passwordSecret.");
                }

                psi.Domain = domain;
                psi.PasswordInClearText = password;
            }
            else if (domain is not null || password is not null)
            {
                throw new PlatformNotSupportedException("Host domain and passwordSecret are supported only on Windows.");
            }
        }
        else if (domain is not null || password is not null)
        {
            throw new InvalidOperationException("Host credentials require a user.");
        }

        if (environment is not null)
        {
            foreach (var kv in environment)
            {
                if (kv.Value is not null)
                    psi.Environment[kv.Key] = kv.Value;
                else
                    psi.Environment.Remove(kv.Key);
            }
        }

        return psi;
    }

    public static async Task<ShellRunResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onStdout = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
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

        if (environment is not null)
        {
            foreach (var kv in environment)
            {
                if (kv.Value is not null)
                    process.StartInfo.Environment[kv.Key] = kv.Value;
                else
                    process.StartInfo.Environment.Remove(kv.Key);
            }
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (IsCommandNotFound(ex))
        {
            throw new FileNotFoundException($"Command '{fileName}' was not found.", fileName, ex);
        }

        var stdoutLines = new System.Text.StringBuilder();
        var stderrLines = new System.Text.StringBuilder();

        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutLines, onStdout, cancellationToken);
        var stderrTask = ReadStreamAsync(process.StandardError, stderrLines, null, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        await stderrTask;

        return new ShellRunResult(
            process.ExitCode,
            stdoutLines.ToString().Trim(),
            stderrLines.ToString().Trim());
    }

    private static async Task ReadStreamAsync(
        System.IO.TextReader reader,
        System.Text.StringBuilder buffer,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            buffer.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static bool IsCommandNotFound(Win32Exception exception) =>
        exception.NativeErrorCode is 2 or 3;
}
