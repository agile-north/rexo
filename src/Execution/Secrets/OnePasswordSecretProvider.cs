namespace Rexo.Execution.Secrets;

using Rexo.Core.Abstractions;
using Rexo.Core.Models;

internal sealed class OnePasswordSecretProvider : ISecretProvider
{
    public string Type => "1password";

    public async Task<SecretResolution> ResolveAsync(SecretRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Selector))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                Type,
                "1password",
                "1Password provider requires a selector (for example op://vault/item/field)." );
        }

        var settings = request.Settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var command = settings.TryGetValue("command", out var commandValue) && !string.IsNullOrWhiteSpace(commandValue)
            ? commandValue
            : "op";

        var subcommand = settings.TryGetValue("subcommand", out var subcommandValue)
            ? subcommandValue
            : "read";

        settings.TryGetValue("args", out var rawArgs);
        if (string.IsNullOrWhiteSpace(rawArgs) && settings.TryGetValue("arguments", out var arguments))
        {
            rawArgs = arguments;
        }

        var parsedArgs = SecretProcessRunner.ParseArguments(rawArgs);
        var materializedArgs = SecretProcessRunner.MaterializeArguments(parsedArgs, request.Selector, request.Name, out var selectorReferenced).ToList();

        if (!string.IsNullOrWhiteSpace(subcommand))
        {
            materializedArgs.Insert(0, subcommand);
        }

        if (!selectorReferenced)
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
                "1password",
                processResult.Error ?? $"1Password command failed with exit code {processResult.ExitCode}.");
        }

        var value = processResult.Stdout.Trim();
        return new SecretResolution(
            request.Name,
            !string.IsNullOrWhiteSpace(value),
            value,
            Type,
            "1password");
    }
}
