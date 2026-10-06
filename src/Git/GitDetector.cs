namespace Rexo.Git;

using System.Diagnostics;
using Rexo.Core.Models;

public static class GitDetector
{
    public static async Task<GitInfo> DetectAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var branch = await RunGitAsync("rev-parse --abbrev-ref HEAD", workingDirectory, cancellationToken);
        var commitSha = await RunGitAsync("rev-parse HEAD", workingDirectory, cancellationToken);
        var shortSha = await RunGitAsync("rev-parse --short HEAD", workingDirectory, cancellationToken);
        var remoteUrl = await RunGitAsync("remote get-url origin", workingDirectory, cancellationToken);
        var statusOutput = await RunGitAsync("status --porcelain", workingDirectory, cancellationToken);
        var isClean = statusOutput is not null && string.IsNullOrWhiteSpace(statusOutput);

        return new GitInfo(
            Branch: branch?.Trim(),
            CommitSha: commitSha?.Trim(),
            ShortSha: shortSha?.Trim(),
            RemoteUrl: remoteUrl?.Trim(),
            IsClean: isClean);
    }

    private static async Task<string?> RunGitAsync(
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
