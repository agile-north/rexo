namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Templating;
using Rexo.Versioning;

[Collection("EnvVar Mutation Sequential")]
public sealed class SecretsExecutionTests
{
    [Fact]
    public async Task RequiredSecretPreflightFailsWhenMissing()
    {
        var config = CreateConfig(
            runCommand: "echo should-not-run",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiKey"] = new RepoSecretConfig { Env = "REXO_TEST_REQUIRED_SECRET", Required = true },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var original = Environment.GetEnvironmentVariable("REXO_TEST_REQUIRED_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("REXO_TEST_REQUIRED_SECRET", null);

            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.False(result.Success);
            Assert.Equal(9, result.ExitCode);
            Assert.Contains("secret", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("errorCode", result.Outputs.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.NotEmpty(result.StructuredErrors);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_TEST_REQUIRED_SECRET", original);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task ExecProviderResolvesSecretViaCommand()
    {
        var config = CreateConfig(
            runCommand: "echo {{secrets.execSecret}}",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "exec", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["execSecret"] = new RepoSecretConfig
                    {
                        Selector = "exec-provider-value",
                        Required = true,
                        Settings = ParseSettings(
                            """
                            {
                              "command": "pwsh",
                              "args": "-NoProfile -Command \"Write-Output {selector}\"",
                              "mode": "raw"
                            }
                            """)
                    },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.True(result.Success);
            var step = Assert.Single(result.Steps);
            Assert.True(step.Outputs.TryGetValue("stdout", out var stdoutObj));
            var stdout = Assert.IsType<string>(stdoutObj);
            Assert.Contains("***", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("exec-provider-value", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task OnePasswordProviderResolvesSecretViaCommandOverride()
    {
        var config = CreateConfig(
            runCommand: "echo {{secrets.onePassSecret}}",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "1password", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["onePassSecret"] = new RepoSecretConfig
                    {
                        Selector = "onepass-selector",
                        Required = true,
                        Settings = ParseSettings(
                            """
                            {
                              "command": "pwsh",
                              "subcommand": "",
                              "args": "-NoProfile -Command \"Write-Output onepass-{selector}\""
                            }
                            """)
                    },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.True(result.Success);
            var step = Assert.Single(result.Steps);
            Assert.True(step.Outputs.TryGetValue("stdout", out var stdoutObj));
            var stdout = Assert.IsType<string>(stdoutObj);
            Assert.Contains("***", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("onepass-onepass-selector", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task OnePasswordProviderUsesServiceAccountTokenFromProviderAuth()
    {
        var tokenEnvName = $"REXO_TEST_OP_TOKEN_{Guid.NewGuid():N}";
        var original = Environment.GetEnvironmentVariable(tokenEnvName);
        Environment.SetEnvironmentVariable(tokenEnvName, "svc-token-value");

        var config = CreateConfig(
            runCommand: "echo {{secrets.onePassSecret}}",
            secrets: new RepoSecretsConfig
            {
                Providers = new Dictionary<string, RepoSecretProviderConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["op"] = new RepoSecretProviderConfig
                    {
                        Type = "1password",
                        Auth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["serviceAccountTokenEnv"] = tokenEnvName,
                        },
                        Settings = ParseSettings(
                            """
                            {
                              "command": "pwsh",
                              "subcommand": "",
                              "args": ["-NoProfile", "-Command", "Write-Output ($env:OP_SERVICE_ACCOUNT_TOKEN + '-' + '{selector}')"]
                            }
                            """)
                    },
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["onePassSecret"] = new RepoSecretConfig
                    {
                        ProviderRef = "op",
                        Selector = "onepass-selector",
                        Required = true,
                    },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.True(result.Success);
            var step = Assert.Single(result.Steps);
            var stdout = Assert.IsType<string>(step.Outputs["stdout"]);
            Assert.Contains("***", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("svc-token-value", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenEnvName, original);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task OnePasswordProviderFailsWithHelpfulMessageWhenOpMissing()
    {
        var config = CreateConfig(
            runCommand: "echo {{secrets.onePassSecret}}",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "1password", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["onePassSecret"] = new RepoSecretConfig
                    {
                        Selector = "onepass-selector",
                        Required = true,
                        Settings = ParseSettings(
                            """
                            {
                              "command": "rexo-definitely-missing-op-binary"
                            }
                            """)
                    },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.False(result.Success);
            Assert.Contains("not found", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ErrorCodes.SecretResolutionFailed, result.StructuredErrors[0].Code);
            Assert.Contains("not found", result.StructuredErrors[0].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task OnePasswordProviderFailsWithHelpfulMessageWhenNotSignedIn()
    {
        var config = CreateConfig(
            runCommand: "echo {{secrets.onePassSecret}}",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "1password", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["onePassSecret"] = new RepoSecretConfig
                    {
                        Selector = "onepass-selector",
                        Required = true,
                        Settings = ParseSettings(
                            """
                            {
                              "command": "pwsh",
                              "subcommand": "",
                              "args": ["-NoProfile", "-Command", "Write-Error 'You are not currently signed in. Run op signin.'; exit 1"]
                            }
                            """)
                    },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.False(result.Success);
            Assert.Contains("signed in", result.StructuredErrors[0].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretIsAvailableInTemplateContext()
    {
        var config = CreateConfig(
            runCommand: "echo {{secrets.apiKey}}",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiKey"] = new RepoSecretConfig { Env = "REXO_TEST_TEMPLATE_SECRET", Required = true },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var original = Environment.GetEnvironmentVariable("REXO_TEST_TEMPLATE_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("REXO_TEST_TEMPLATE_SECRET", "hello-template-secret");

            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.True(result.Success);
            var step = Assert.Single(result.Steps);
            Assert.True(step.Outputs.TryGetValue("stdout", out var stdoutObj));
            var stdout = Assert.IsType<string>(stdoutObj);
            Assert.Contains("***", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_TEST_TEMPLATE_SECRET", original);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretMapToEnvIsInjectedForRunStepEnvironment()
    {
        var config = CreateConfig(
            runCommand: "pwsh -NoProfile -Command \"if ([Environment]::GetEnvironmentVariable('REXO_MAPPED_SECRET') -eq 'mapped-value') { Write-Output ok } else { Write-Output bad; exit 7 }\"",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mappedSecret"] = new RepoSecretConfig
                    {
                        Env = "REXO_TEST_MAPPED_SECRET",
                        MapToEnv = "REXO_MAPPED_SECRET",
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Combine(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var original = Environment.GetEnvironmentVariable("REXO_TEST_MAPPED_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("REXO_TEST_MAPPED_SECRET", "mapped-value");

            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.True(result.Success);
            var step = Assert.Single(result.Steps);
            Assert.Equal(0, step.ExitCode);
            Assert.True(step.Outputs.TryGetValue("stdout", out var stdoutObj));
            Assert.Contains("ok", Assert.IsType<string>(stdoutObj), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_TEST_MAPPED_SECRET", original);
            Directory.Delete(tempRoot, true);
        }
    }

    private static async Task<CommandResult> ExecuteConfigCommandAsync(RepoConfig config, string repositoryRoot)
    {
        var builtins = new BuiltinRegistry();
        var artifactProviders = new Artifacts.ArtifactProviderRegistry();
        var loader = new ConfigCommandLoader(
            builtins,
            new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(),
            artifactProviders);

        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        loader.LoadInto(registry, config, repositoryRoot, executor);

        var invocation = new CommandInvocation(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>(),
            false,
            null,
            repositoryRoot);

        return await executor.ExecuteAsync("run", invocation, CancellationToken.None);
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
