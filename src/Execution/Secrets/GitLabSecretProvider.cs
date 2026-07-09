namespace Rexo.Execution.Secrets;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;

internal sealed class GitLabSecretProvider : ISecretProvider
{
    private static readonly HttpClient HttpClient = new();
    private static readonly string[] DefaultTokenPrecedence = ["token", "tokenEnv", "ciJobToken", "gitlabToken"];
    private static readonly string[] DefaultOidcPrecedence = ["oidcToken", "oidcTokenEnv", "ciJobJwtV2", "ciJobJwt"];

    public string Type => "gitlab";

    public async Task<SecretResolution> ResolveAsync(SecretRequest request, CancellationToken cancellationToken)
    {
        var settings = request.Settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var auth = request.Auth ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var mode = ResolveMode(settings);
        return mode switch
        {
            "env" => ResolveFromEnvironment(request),
            "variables" => await ResolveFromVariablesApiAsync(request, settings, auth, cancellationToken),
            "api" => await ResolveFromVariablesApiAsync(request, settings, auth, cancellationToken),
            "vault" => await ResolveFromVaultAsync(request, settings, auth, cancellationToken),
            _ => new SecretResolution(
                request.Name,
                false,
                null,
                Type,
                "gitlab",
                $"GitLab provider mode '{mode}' is not supported. Use env, variables, api, or vault."),
        };
    }

    private static SecretResolution ResolveFromEnvironment(SecretRequest request)
    {
        var envName = request.Selector;
        if (string.IsNullOrWhiteSpace(envName))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-env",
                "GitLab env mode requires a selector or environment variable name.");
        }

        var fromProcess = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(fromProcess))
        {
            return new SecretResolution(request.Name, true, fromProcess, "gitlab", "gitlab-env", null, false);
        }

        var fromProcessUpper = Environment.GetEnvironmentVariable(envName.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(fromProcessUpper))
        {
            return new SecretResolution(request.Name, true, fromProcessUpper, "gitlab", "gitlab-env", null, false);
        }

        return new SecretResolution(
            request.Name,
            false,
            null,
            "gitlab",
            "gitlab-env",
            $"GitLab environment secret '{envName}' for '{request.Name}' is missing.",
            false);
    }

    private static async Task<SecretResolution> ResolveFromVariablesApiAsync(
        SecretRequest request,
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> auth,
        CancellationToken cancellationToken)
    {
        var variableKey = request.Selector;
        if (string.IsNullOrWhiteSpace(variableKey))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-api",
                "GitLab variables mode requires selector set to the variable key.");
        }

        var (token, tokenSource) = ResolveApiToken(settings, auth);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-api",
                "GitLab API token is missing. Configure auth/settings token or tokenEnv, or provide CI_JOB_TOKEN/GITLAB_TOKEN.");
        }

        var endpoint = BuildVariablesEndpoint(variableKey, settings);
        if (endpoint is null)
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-api",
                "GitLab variables mode requires projectId/groupId (or CI_PROJECT_ID) unless settings.variablesEndpoint is provided.");
        }

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (string.Equals(tokenSource, "ciJobToken", StringComparison.OrdinalIgnoreCase))
        {
            requestMessage.Headers.TryAddWithoutValidation("JOB-TOKEN", token);
        }
        else
        {
            requestMessage.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        }

        using var response = await HttpClient.SendAsync(requestMessage, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var summary = Summarize(payload);
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-api",
                string.IsNullOrWhiteSpace(summary)
                    ? $"GitLab variables request failed with status {(int)response.StatusCode}."
                    : $"GitLab variables request failed with status {(int)response.StatusCode}: {summary}");
        }

        var value = TryExtractValue(payload);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-api",
                $"GitLab variables response for '{variableKey}' did not contain a value.");
        }

        return new SecretResolution(request.Name, true, value, "gitlab", "gitlab-api", null, false);
    }

    private static async Task<SecretResolution> ResolveFromVaultAsync(
        SecretRequest request,
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> auth,
        CancellationToken cancellationToken)
    {
        if (!settings.TryGetValue("vaultEndpoint", out var vaultEndpoint) || string.IsNullOrWhiteSpace(vaultEndpoint))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-vault",
                "GitLab vault mode requires settings.vaultEndpoint.");
        }

        var selector = request.Selector;
        if (string.IsNullOrWhiteSpace(selector))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-vault",
                "GitLab vault mode requires selector set to the secret path/key.");
        }

        var oidcToken = ResolveOidcToken(settings, auth);
        if (string.IsNullOrWhiteSpace(oidcToken))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-vault",
                "GitLab vault mode requires an OIDC token. Configure oidcToken/oidcTokenEnv or provide CI_JOB_JWT_V2/CI_JOB_JWT.");
        }

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, vaultEndpoint);
        var payload = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["selector"] = selector,
            ["token"] = oidcToken,
            ["projectId"] = settings.TryGetValue("projectId", out var projectId) ? projectId : Environment.GetEnvironmentVariable("CI_PROJECT_ID"),
            ["ref"] = Environment.GetEnvironmentVariable("CI_COMMIT_REF_NAME"),
            ["role"] = settings.TryGetValue("role", out var role) ? role : null,
        });
        requestMessage.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(requestMessage, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var summary = Summarize(body);
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-vault",
                string.IsNullOrWhiteSpace(summary)
                    ? $"GitLab vault request failed with status {(int)response.StatusCode}."
                    : $"GitLab vault request failed with status {(int)response.StatusCode}: {summary}");
        }

        var value = TryExtractValue(body);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new SecretResolution(
                request.Name,
                false,
                null,
                "gitlab",
                "gitlab-vault",
                "GitLab vault response did not contain a value.");
        }

        return new SecretResolution(request.Name, true, value, "gitlab", "gitlab-vault", null, false);
    }

    private static string ResolveMode(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.TryGetValue("mode", out var mode) && !string.IsNullOrWhiteSpace(mode))
        {
            return mode.Trim();
        }

        return "env";
    }

    private static (string? Token, string Source) ResolveApiToken(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> auth)
    {
        foreach (var entry in ResolvePrecedence(settings, "tokenPrecedence", DefaultTokenPrecedence))
        {
            switch (entry)
            {
                case "token":
                    if (TryGetDirectToken(auth, settings, out var direct))
                    {
                        return (direct, "token");
                    }

                    break;
                case "tokenenv":
                    if (TryGetTokenFromEnv(auth, settings, out var envToken))
                    {
                        return (envToken, "tokenEnv");
                    }

                    break;
                case "cijobtoken":
                    if (TryGetEnvironmentValue("CI_JOB_TOKEN", out var jobToken))
                    {
                        return (jobToken, "ciJobToken");
                    }

                    break;
                case "gitlabtoken":
                    if (TryGetEnvironmentValue("GITLAB_TOKEN", out var gitlabToken))
                    {
                        return (gitlabToken, "gitlabToken");
                    }

                    break;
            }
        }

        return (null, "none");
    }

    private static IEnumerable<string> ResolvePrecedence(
        IReadOnlyDictionary<string, string> settings,
        string settingKey,
        IReadOnlyList<string> defaultOrder)
    {
        if (!settings.TryGetValue(settingKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return defaultOrder;
        }

        return raw
            .Split([','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant());
    }

    private static bool TryGetDirectToken(
        IReadOnlyDictionary<string, string> auth,
        IReadOnlyDictionary<string, string> settings,
        out string token)
    {
        token = string.Empty;
        if (auth.TryGetValue("token", out var fromAuth) && !string.IsNullOrWhiteSpace(fromAuth))
        {
            token = fromAuth;
            return true;
        }

        if (settings.TryGetValue("token", out var fromSettings) && !string.IsNullOrWhiteSpace(fromSettings))
        {
            token = fromSettings;
            return true;
        }

        return false;
    }

    private static bool TryGetTokenFromEnv(
        IReadOnlyDictionary<string, string> auth,
        IReadOnlyDictionary<string, string> settings,
        out string token)
    {
        token = string.Empty;
        if (auth.TryGetValue("tokenEnv", out var authTokenEnv)
            && TryGetEnvironmentValue(authTokenEnv, out var fromAuthEnv))
        {
            token = fromAuthEnv;
            return true;
        }

        if (settings.TryGetValue("tokenEnv", out var settingsTokenEnv)
            && TryGetEnvironmentValue(settingsTokenEnv, out var fromSettingsEnv))
        {
            token = fromSettingsEnv;
            return true;
        }

        return false;
    }

    private static string? BuildVariablesEndpoint(string variableKey, IReadOnlyDictionary<string, string> settings)
    {
        if (settings.TryGetValue("variablesEndpoint", out var endpoint) && !string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint
                .Replace("{key}", Uri.EscapeDataString(variableKey), StringComparison.Ordinal)
                .Replace("{selector}", Uri.EscapeDataString(variableKey), StringComparison.Ordinal);
        }

        var baseUrl = ResolveBaseUrl(settings);
        var encodedKey = Uri.EscapeDataString(variableKey);

        if (settings.TryGetValue("groupId", out var groupId) && !string.IsNullOrWhiteSpace(groupId))
        {
            return BuildVariablesUrl(baseUrl, $"/api/v4/groups/{Uri.EscapeDataString(groupId)}/variables/{encodedKey}", settings);
        }

        var projectId = settings.TryGetValue("projectId", out var configuredProjectId)
            ? configuredProjectId
            : Environment.GetEnvironmentVariable("CI_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            return BuildVariablesUrl(baseUrl, $"/api/v4/projects/{Uri.EscapeDataString(projectId)}/variables/{encodedKey}", settings);
        }

        return null;
    }

    private static string BuildVariablesUrl(string baseUrl, string path, IReadOnlyDictionary<string, string> settings)
    {
        var url = baseUrl.TrimEnd('/') + path;
        if (!settings.TryGetValue("environmentScope", out var environmentScope) || string.IsNullOrWhiteSpace(environmentScope))
        {
            return url;
        }

        return url + "?filter[environment_scope]=" + Uri.EscapeDataString(environmentScope);
    }

    private static string ResolveBaseUrl(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.TryGetValue("baseUrl", out var baseUrl) && !string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        return Environment.GetEnvironmentVariable("CI_SERVER_URL") ?? "https://gitlab.com";
    }

    private static string? ResolveOidcToken(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> auth)
    {
        foreach (var entry in ResolvePrecedence(settings, "oidcTokenPrecedence", DefaultOidcPrecedence))
        {
            switch (entry)
            {
                case "oidctoken":
                    if (auth.TryGetValue("oidcToken", out var authToken) && !string.IsNullOrWhiteSpace(authToken))
                    {
                        return authToken;
                    }

                    if (settings.TryGetValue("oidcToken", out var settingToken) && !string.IsNullOrWhiteSpace(settingToken))
                    {
                        return settingToken;
                    }

                    break;
                case "oidctokenenv":
                    if (auth.TryGetValue("oidcTokenEnv", out var authEnvName) && TryGetEnvironmentValue(authEnvName, out var authEnvToken))
                    {
                        return authEnvToken;
                    }

                    if (settings.TryGetValue("oidcTokenEnv", out var settingsEnvName) && TryGetEnvironmentValue(settingsEnvName, out var settingsEnvToken))
                    {
                        return settingsEnvToken;
                    }

                    break;
                case "cijobjwtv2":
                    if (TryGetEnvironmentValue("CI_JOB_JWT_V2", out var jobJwtV2))
                    {
                        return jobJwtV2;
                    }

                    break;
                case "cijobjwt":
                    if (TryGetEnvironmentValue("CI_JOB_JWT", out var jobJwt))
                    {
                        return jobJwt;
                    }

                    break;
            }
        }

        return null;
    }

    private static bool TryGetEnvironmentValue(string? variableName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return false;
        }

        var candidate = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static string? TryExtractValue(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String)
                {
                    return valueElement.GetString();
                }

                if (root.TryGetProperty("data", out var dataElement)
                    && dataElement.ValueKind == JsonValueKind.Object
                    && dataElement.TryGetProperty("value", out var nestedValue)
                    && nestedValue.ValueKind == JsonValueKind.String)
                {
                    return nestedValue.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Summarize(string payload)
    {
        var trimmed = payload.Trim();
        if (trimmed.Length <= 200)
        {
            return trimmed;
        }

        return trimmed[..200];
    }
}
