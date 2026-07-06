namespace Rexo.Execution.Secrets;

using System.ComponentModel;
using System.Diagnostics;

internal static class SecretProcessRunner
{
    internal static async Task<(bool Success, string Stdout, string Stderr, int ExitCode, string? Error)> RunAsync(
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(command)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (process.ExitCode == 0, stdout, stderr, process.ExitCode, null);
        }
        catch (Win32Exception)
        {
            return (false, string.Empty, string.Empty, 127, $"Command '{command}' was not found.");
        }
        catch (FileNotFoundException)
        {
            return (false, string.Empty, string.Empty, 127, $"Command '{command}' was not found.");
        }
    }

    internal static IReadOnlyList<string> ParseArguments(string? rawArgs)
    {
        if (string.IsNullOrWhiteSpace(rawArgs))
        {
            return Array.Empty<string>();
        }

        var trimmed = rawArgs.Trim();
        if (trimmed.Length > 0 && trimmed[0] == '[')
        {
            try
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(trimmed);
                return arr ?? Array.Empty<string>();
            }
            catch
            {
                // Fall through to command-line parsing.
            }
        }

        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    internal static IReadOnlyList<string> MaterializeArguments(
        IReadOnlyList<string> args,
        string? selector,
        string secretName,
        out bool selectorReferenced)
    {
        selectorReferenced = false;
        var materialized = new List<string>(args.Count);

        foreach (var arg in args)
        {
            var replaced = arg
                .Replace("{name}", secretName, StringComparison.Ordinal)
                .Replace("{selector}", selector ?? string.Empty, StringComparison.Ordinal);

            if (!selectorReferenced && !string.IsNullOrWhiteSpace(selector) && !string.Equals(replaced, arg, StringComparison.Ordinal))
            {
                selectorReferenced = true;
            }

            materialized.Add(replaced);
        }

        return materialized;
    }
}
