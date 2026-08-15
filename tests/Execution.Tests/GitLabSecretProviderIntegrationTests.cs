namespace Rexo.Execution.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Templating;
using Rexo.Versioning;

[Collection("EnvVar Mutation Sequential")]
public sealed class GitLabSecretProviderIntegrationTests
{
    [Fact]
    public async Task SecretsDoctorUsesGitLabApiVariablesModeWithCiJobTokenPrecedence()
    {
        using var listener = StartLoopbackServer(out var baseUrl);
        var serverTask = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                var request = context.Request;

                if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(request.Url?.AbsolutePath, "/api/v4/projects/42/variables/MY_SECRET", StringComparison.Ordinal)
                    || !string.Equals(request.Headers["JOB-TOKEN"], "ci-job-token", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 401;
                    await WriteResponseAsync(context.Response, "invalid request");
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await WriteResponseAsync(context.Response, "{\"value\":\"api-secret\"}");
            }
            finally
            {
                listener.Stop();
            }
        });

        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Providers = new Dictionary<string, RepoSecretProviderConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gitlabApi"] = new RepoSecretProviderConfig
                    {
                        Type = "gitlab",
                        Settings = ParseSettings(
                                                        $"{{\"mode\":\"variables\",\"baseUrl\":\"{baseUrl}\",\"projectId\":\"42\",\"tokenPrecedence\":\"ciJobToken,gitlabToken\"}}")
                    }
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtimeSecret"] = new RepoSecretConfig
                    {
                        ProviderRef = "gitlabApi",
                        Selector = "MY_SECRET",
                        Required = true,
                        ExposeInTemplates = false,
                    }
                }
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-gitlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalCiJobToken = Environment.GetEnvironmentVariable("CI_JOB_TOKEN");
        var originalGitLabToken = Environment.GetEnvironmentVariable("GITLAB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CI_JOB_TOKEN", "ci-job-token");
            Environment.SetEnvironmentVariable("GITLAB_TOKEN", "gitlab-token");

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=gitlab", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source=gitlab-api", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI_JOB_TOKEN", originalCiJobToken);
            Environment.SetEnvironmentVariable("GITLAB_TOKEN", originalGitLabToken);
            Directory.Delete(tempRoot, true);
            await serverTask;
        }
    }

    [Fact]
    public async Task SecretsDoctorUsesGitLabVaultModeWithOidcTokenEnv()
    {
        using var listener = StartLoopbackServer(out var baseUrl);
        var serverTask = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                var request = context.Request;

                if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(request.Url?.AbsolutePath, "/vault/resolve", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 404;
                    await WriteResponseAsync(context.Response, "not found");
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync();
                }

                if (!body.Contains("\"selector\":\"my/secret/path\"", StringComparison.Ordinal)
                    || !body.Contains("\"token\":\"oidc-token-value\"", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 401;
                    await WriteResponseAsync(context.Response, "invalid vault payload");
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await WriteResponseAsync(context.Response, "{\"data\":{\"value\":\"vault-secret\"}}");
            }
            finally
            {
                listener.Stop();
            }
        });

        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Providers = new Dictionary<string, RepoSecretProviderConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gitlabVault"] = new RepoSecretProviderConfig
                    {
                        Type = "gitlab",
                        Settings = ParseSettings(
                                                        $"{{\"mode\":\"vault\",\"vaultEndpoint\":\"{baseUrl}/vault/resolve\",\"oidcTokenEnv\":\"REXO_TEST_OIDC_TOKEN\",\"oidcTokenPrecedence\":\"oidcTokenEnv,ciJobJwtV2\"}}")
                    }
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtimeSecret"] = new RepoSecretConfig
                    {
                        ProviderRef = "gitlabVault",
                        Selector = "my/secret/path",
                        Required = true,
                        ExposeInTemplates = false,
                    }
                }
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-gitlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalOidc = Environment.GetEnvironmentVariable("REXO_TEST_OIDC_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("REXO_TEST_OIDC_TOKEN", "oidc-token-value");

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=gitlab", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source=gitlab-vault", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_TEST_OIDC_TOKEN", originalOidc);
            Directory.Delete(tempRoot, true);
            await serverTask;
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes.AsMemory(), CancellationToken.None);
        response.OutputStream.Close();
    }

    private static HttpListener StartLoopbackServer(out string baseUrl)
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        baseUrl = prefix.TrimEnd('/');
        return listener;
    }

    private static async Task<CommandResult> ExecuteBuiltinCommandAsync(RepoConfig config, string repositoryRoot, string commandName)
    {
        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>(),
            false,
            null,
            repositoryRoot);

        return await executor.ExecuteAsync(commandName, invocation, CancellationToken.None);
    }

    private static RepoConfig CreateConfig(string runCommand, RepoSecretsConfig secrets)
    {
        return new RepoConfig(
            Name: "secrets-test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["run"] = new RepoCommandConfig(
                    Description: "run",
                    Options: new Dictionary<string, RepoOptionConfig>(),
                    Steps:
                    [
                        new RepoStepConfig(
                            Id: "run",
                            Run: runCommand),
                    ]),
            },
            Aliases: new Dictionary<string, string>())
        {
            Secrets = secrets,
        };
    }

    private static Dictionary<string, JsonElement> ParseSettings(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize settings JSON.");
    }
}
