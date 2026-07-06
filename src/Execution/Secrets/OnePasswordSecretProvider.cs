namespace Rexo.Execution.Secrets;

using Rexo.Core.Abstractions;
using Rexo.Core.Models;

internal sealed class OnePasswordSecretProvider : ISecretProvider
{
    private const string DefaultCommand = "op";
    private const string DefaultSubcommand = "read";

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
            : DefaultCommand;

        var subcommand = settings.TryGetValue("subcommand", out var subcommandValue)
            ? subcommandValue
            : DefaultSubcommand;

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

        var environmentOverrides = BuildEnvironmentOverrides(request.Auth);
        var processResult = await SecretProcessRunner.RunAsync(command, materializedArgs, environmentOverrides, cancellationToken);
        if (!processResult.Success)
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                Type,
                ResolveSource(environmentOverrides),
                BuildFailureMessage(command, processResult, environmentOverrides));
        }

        var value = processResult.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                Type,
                ResolveSource(environmentOverrides),
                $"1Password did not return a value for selector '{request.Selector}'.");
        }

        return new SecretResolution(
            request.Name,
            true,
            value,
            Type,
            ResolveSource(environmentOverrides));
    }

    private static IReadOnlyDictionary<string, string>? BuildEnvironmentOverrides(IReadOnlyDictionary<string, string>? auth)
    {
        if (auth is not { Count: > 0 })
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        AddEnvironmentOverride(auth, environment, "serviceAccountToken", "OP_SERVICE_ACCOUNT_TOKEN");
        AddEnvironmentOverride(auth, environment, "connectHost", "OP_CONNECT_HOST");
        AddEnvironmentOverride(auth, environment, "connectToken", "OP_CONNECT_TOKEN");
        AddEnvironmentOverride(auth, environment, "account", "OP_ACCOUNT");

        return environment.Count == 0 ? null : environment;
    }

    private static void AddEnvironmentOverride(
        IReadOnlyDictionary<string, string> auth,
        IDictionary<string, string> environment,
        string authKey,
        string environmentKey)
    {
        if (TryResolveAuthValue(auth, authKey, out var value))
        {
            environment[environmentKey] = value;
        }
    }

    private static bool TryResolveAuthValue(
        IReadOnlyDictionary<string, string> auth,
        string authKey,
        out string value)
    {
        value = string.Empty;

        if (auth.TryGetValue(authKey, out var directValue) && !string.IsNullOrWhiteSpace(directValue))
        {
            value = directValue;
            return true;
        }

        var envKey = authKey + "Env";
        if (!auth.TryGetValue(envKey, out var envName) || string.IsNullOrWhiteSpace(envName))
        {
            return false;
        }

        var envValue = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return false;
        }

        value = envValue;
        return true;
    }

    private static string ResolveSource(IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        if (environmentOverrides is null || environmentOverrides.Count == 0)
        {
            return "1password";
        }

        if (environmentOverrides.ContainsKey("OP_SERVICE_ACCOUNT_TOKEN"))
        {
            return "1password-service-account";
        }

        if (environmentOverrides.ContainsKey("OP_CONNECT_TOKEN") || environmentOverrides.ContainsKey("OP_CONNECT_HOST"))
        {
            return "1password-connect";
        }

        return "1password";
    }

    private static string BuildFailureMessage(
        string command,
        (bool Success, string Stdout, string Stderr, int ExitCode, string? Error) processResult,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        if (!string.IsNullOrWhiteSpace(processResult.Error))
        {
            if (string.Equals(command, DefaultCommand, StringComparison.OrdinalIgnoreCase))
            {
                return "1Password CLI 'op' was not found. Install the 1Password CLI or override settings.command.";
            }

            return processResult.Error;
        }

        var stderr = processResult.Stderr.Trim();
        if (stderr.Contains("not currently signed in", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("run op signin", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("signin", StringComparison.OrdinalIgnoreCase))
        {
            return "1Password CLI is not signed in. Run 'op signin', or configure service account/auth settings for non-interactive use.";
        }

        if ((environmentOverrides is null || environmentOverrides.Count == 0)
            && (stderr.Contains("service account", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("connect server", StringComparison.OrdinalIgnoreCase)))
        {
            return "1Password authentication is not configured. Provide ambient OP_* environment variables or configure secrets.providers.<name>.auth for service account or Connect.";
        }

        var summary = ExtractErrorSummary(stderr);
        return string.IsNullOrWhiteSpace(summary)
            ? $"1Password command failed with exit code {processResult.ExitCode}."
            : $"1Password command failed: {summary}";
    }

    private static string ExtractErrorSummary(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var firstLine = stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return string.Empty;
        }

        return firstLine.Length <= 200 ? firstLine : firstLine[..200];
    }
}
