namespace Rexo.Execution.Secrets;

using System.Text.Json;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;

internal sealed class ExecSecretProvider : ISecretProvider
{
    public string Type => "exec";

    public async Task<SecretResolution> ResolveAsync(SecretRequest request, CancellationToken cancellationToken)
    {
        var settings = request.Settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!settings.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                Type,
                "exec",
                "Exec provider requires settings.command.");
        }

        settings.TryGetValue("args", out var rawArgs);
        if (string.IsNullOrWhiteSpace(rawArgs) && settings.TryGetValue("arguments", out var arguments))
        {
            rawArgs = arguments;
        }

        var parsedArgs = SecretProcessRunner.ParseArguments(rawArgs);
        var materializedArgs = SecretProcessRunner.MaterializeArguments(parsedArgs, request.Selector, request.Name, out var selectorReferenced).ToList();

        var appendSelector = !settings.TryGetValue("appendSelector", out var appendSelectorRaw)
            || !string.Equals(appendSelectorRaw, "false", StringComparison.OrdinalIgnoreCase);

        if (appendSelector && !selectorReferenced && !string.IsNullOrWhiteSpace(request.Selector))
        {
            materializedArgs.Add(request.Selector);
        }

        var processResult = await SecretProcessRunner.RunAsync(command, materializedArgs, cancellationToken);
        if (!processResult.Success)
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                Type,
                "exec",
                processResult.Error ?? $"Exec provider command failed with exit code {processResult.ExitCode}.");
        }

        var mode = settings.TryGetValue("mode", out var modeValue)
            ? modeValue
            : "raw";

        string? secretValue;
        if (string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase))
        {
            settings.TryGetValue("valuePath", out var valuePath);
            secretValue = TryExtractJsonValue(processResult.Stdout, valuePath ?? "value");
            if (string.IsNullOrWhiteSpace(secretValue))
            {
                return new SecretResolution(
                    request.Name,
                    false,
                    null,
                    Type,
                    "exec",
                    "Exec provider JSON mode could not resolve a value from command output.");
            }
        }
        else
        {
            secretValue = processResult.Stdout.Trim();
        }

        return new SecretResolution(
            request.Name,
            !string.IsNullOrWhiteSpace(secretValue),
            secretValue,
            Type,
            "exec");
    }

    private static string? TryExtractJsonValue(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                {
                    return null;
                }
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                _ => element.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }
}
