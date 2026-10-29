namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Configuration;
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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
    public async Task CommandRequiredSecretsFailWhenUndefined()
    {
        var config = CreateConfig(
            runCommand: "echo should-not-run",
            secrets: new RepoSecretsConfig())
            with
            {
                Commands = new Dictionary<string, RepoCommandConfig>
                {
                    ["run"] = new RepoCommandConfig(
                        "run",
                        new Dictionary<string, RepoOptionConfig>(),
                        [new RepoStepConfig(Id: "run", Run: "echo should-not-run")])
                    {
                        RequiredSecrets = ["missingSecret"],
                    },
                },
            };
        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.False(result.Success);
            Assert.Equal(9, result.ExitCode);
            Assert.Contains("missingSecret", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task CommandRequiredSecretsResolveDefinitionsMarkedOptional()
    {
        var secretEnvironmentVariable = $"REXO_TEST_OPTIONAL_REQUIRED_{Guid.NewGuid():N}";
        var original = Environment.GetEnvironmentVariable(secretEnvironmentVariable);
        Environment.SetEnvironmentVariable(secretEnvironmentVariable, "required-secret-value");
        var config = CreateConfig(
            runCommand: "echo {{secrets.apiKey}}",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = false },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiKey"] = new()
                    {
                        Env = secretEnvironmentVariable,
                        Required = false,
                    },
                },
            })
            with
            {
                Commands = new Dictionary<string, RepoCommandConfig>
                {
                    ["run"] = new RepoCommandConfig(
                        "run",
                        new Dictionary<string, RepoOptionConfig>(),
                        [new RepoStepConfig(Id: "run", Run: "echo {{secrets.apiKey}}")])
                    {
                        RequiredSecrets = ["apiKey"],
                    },
                },
            };
        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.True(result.Success, result.Message);
            Assert.Contains("***", Assert.IsType<string>(result.Steps[0].Outputs["stdout"]), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretEnvironmentVariable, original);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task EnvironmentOverrideDoesNotSuppressGloballyRequiredSecret()
    {
        var secretEnvironmentVariable = $"REXO_TEST_GLOBAL_REQUIRED_{Guid.NewGuid():N}";
        var original = Environment.GetEnvironmentVariable(secretEnvironmentVariable);
        Environment.SetEnvironmentVariable(secretEnvironmentVariable, null);
        var baseConfig = CreateConfig(
            runCommand: "echo should-not-run",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiKey"] = new()
                    {
                        Env = secretEnvironmentVariable,
                        Required = true,
                    },
                },
            })
            with
            {
                Environments = new Dictionary<string, RepoEnvironmentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["qa"] = new()
                    {
                        Secrets = new RepoSecretsConfig
                        {
                            Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["apiKey"] = new()
                                {
                                    Env = secretEnvironmentVariable,
                                    Required = false,
                                },
                            },
                        },
                    },
                },
            };
        var config = RepoConfigurationLoader.ApplyEnvironment(baseConfig, "qa");
        var selection = new EnvironmentSelectionResult
        {
            EnvironmentName = "qa",
            SelectionSource = "cli",
            IsExplicit = true,
            IsResolved = true,
        };
        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot, environmentSelection: selection);

            Assert.False(result.Success);
            Assert.Equal(9, result.ExitCode);
            Assert.Contains("apiKey", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretEnvironmentVariable, original);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task CommandCanRequireAnEnvironment()
    {
        var config = CreateConfig(
            runCommand: "echo should-not-run",
            secrets: new RepoSecretsConfig())
            with
            {
                Commands = new Dictionary<string, RepoCommandConfig>
                {
                    ["run"] = new RepoCommandConfig(
                        "run",
                        new Dictionary<string, RepoOptionConfig>(),
                        [new RepoStepConfig(Id: "run", Run: "echo should-not-run")])
                    {
                        RequiresEnvironment = true,
                    },
                },
            };
        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-environment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.False(result.Success);
            Assert.Equal(9, result.ExitCode);
            Assert.Contains("requires a selected execution environment", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Empty(result.Steps);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretMountsArePreflightedBeforeAnyCommandStepRuns()
    {
        var config = CreateConfig(
            runCommand: "echo should-not-run",
            secrets: new RepoSecretsConfig())
            with
            {
                Commands = new Dictionary<string, RepoCommandConfig>
                {
                    ["run"] = new RepoCommandConfig(
                        "run",
                        new Dictionary<string, RepoOptionConfig>(),
                        [
                            new RepoStepConfig(Id: "side-effect", Run: "echo side-effect"),
                            new RepoStepConfig(
                                Id: "container",
                                Run: "echo container-step",
                                Container: new RepoStepContainerConfig(Image: "example/deployer:latest")
                                {
                                    Mounts =
                                    [
                                        new RepoStepContainerMountConfig
                                        {
                                            Secret = "missingMountSecret",
                                            Target = "/run/secrets/deploy",
                                        },
                                    ],
                                }),
                        ]),
                },
            };
        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secret-mount-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.False(result.Success);
            Assert.Equal(9, result.ExitCode);
            Assert.Contains("missingMountSecret", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Empty(result.Steps);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task EnvironmentOverlayIsAppliedOnlyToCommandsThatUseIt()
    {
        var baseConfig = CreateConfig(
            runCommand: "echo {{vars.deployTarget}}",
            secrets: new RepoSecretsConfig())
            with
            {
                Vars = ParseSettings("""{"deployTarget":"repository"}"""),
                Commands = new Dictionary<string, RepoCommandConfig>
                {
                    ["build"] = new RepoCommandConfig(
                        "build",
                        new Dictionary<string, RepoOptionConfig>(),
                        [new RepoStepConfig(Id: "run", Run: "echo {{vars.deployTarget}}")]),
                    ["deploy"] = new RepoCommandConfig(
                        "deploy",
                        new Dictionary<string, RepoOptionConfig>(),
                        [new RepoStepConfig(Id: "run", Run: "echo {{vars.deployTarget}}")])
                    {
                        UsesEnvironment = true,
                    },
                },
                Environments = new Dictionary<string, RepoEnvironmentConfig>
                {
                    ["qa"] = new()
                    {
                        Vars = ParseSettings("""{"deployTarget":"qa"}"""),
                    },
                },
            };
        var environmentConfig = RepoConfigurationLoader.ApplyEnvironment(baseConfig, "qa");
        var selection = new EnvironmentSelectionResult
        {
            EnvironmentName = "qa",
            SelectionSource = "cli",
            IsExplicit = true,
            IsResolved = true,
        };
        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-environment-overlay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var buildResult = await ExecuteConfigCommandAsync(
                baseConfig,
                tempRoot,
                commandName: "build",
                environmentSelection: selection,
                environmentConfig: environmentConfig);
            var deployResult = await ExecuteConfigCommandAsync(
                baseConfig,
                tempRoot,
                commandName: "deploy",
                environmentSelection: selection,
                environmentConfig: environmentConfig);

            Assert.True(buildResult.Success, buildResult.Message);
            Assert.True(deployResult.Success, deployResult.Message);
            Assert.Contains("repository", buildResult.Steps[0].Outputs["stdout"]?.ToString(), StringComparison.Ordinal);
            Assert.Contains("qa", deployResult.Steps[0].Outputs["stdout"]?.ToString(), StringComparison.Ordinal);
        }
        finally
        {
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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
    public async Task EnvProviderCanDereferenceOnePasswordSelectors()
    {
        var envName = $"REXO_TEST_OP_ENV_{Guid.NewGuid():N}";
        var selectorValue = "op://vault/item/field";
        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Providers = new Dictionary<string, RepoSecretProviderConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["op"] = new RepoSecretProviderConfig
                    {
                        Type = "1password",
                        Settings = ParseSettings(
                            """
                            {
                              "command": "pwsh",
                              "subcommand": "",
                              "args": ["-NoProfile", "-Command", "Write-Output indirect-secret-value"]
                            }
                            """)
                    },
                },
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiKey"] = new RepoSecretConfig
                    {
                        Env = envName,
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalSecret = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, selectorValue);

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=1password", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source=1password", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, originalSecret);
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task SecretMapToEnvAliasesAreInjectedForRunStepEnvironment()
    {
        var config = CreateConfig(
            runCommand: "pwsh -NoProfile -Command \"if ([Environment]::GetEnvironmentVariable('REXO_MAPPED_SECRET_PRIMARY') -eq 'mapped-value' -and [Environment]::GetEnvironmentVariable('REXO_MAPPED_SECRET_SECONDARY') -eq 'mapped-value' -and [Environment]::GetEnvironmentVariable('REXO_MAPPED_SECRET_TERTIARY') -eq 'mapped-value') { Write-Output ok } else { Write-Output bad; exit 7 }\"",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mappedSecret"] = new RepoSecretConfig
                    {
                        Env = "REXO_TEST_MAPPED_SECRET_ALIASES",
                        MapToEnv = "REXO_MAPPED_SECRET_PRIMARY",
                        MapToEnvs = ["REXO_MAPPED_SECRET_SECONDARY", "REXO_MAPPED_SECRET_TERTIARY"],
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var original = Environment.GetEnvironmentVariable("REXO_TEST_MAPPED_SECRET_ALIASES");
        try
        {
            Environment.SetEnvironmentVariable("REXO_TEST_MAPPED_SECRET_ALIASES", "mapped-value");

            var result = await ExecuteConfigCommandAsync(config, tempRoot);

            Assert.True(result.Success);
            var step = Assert.Single(result.Steps);
            Assert.Equal(0, step.ExitCode);
            Assert.True(step.Outputs.TryGetValue("stdout", out var stdoutObj));
            Assert.Contains("ok", Assert.IsType<string>(stdoutObj), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_TEST_MAPPED_SECRET_ALIASES", original);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretsDoctorUsesLocalRuntimeProviderChainWhenNotInCi()
    {
        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig
                {
                    ProviderChain =
                    [
                        new RepoSecretProviderRouteConfig { ProviderRef = "localExec", Runtime = "local", Selector = "local-chain-value" },
                        new RepoSecretProviderRouteConfig { Provider = "env", Runtime = "ci", Env = "REXO_TEST_CHAIN_SECRET" },
                    ]
                },
                Providers = new Dictionary<string, RepoSecretProviderConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["localExec"] = new RepoSecretProviderConfig
                    {
                        Type = "exec",
                        Settings = ParseSettings(
                            """
                            {
                              "command": "pwsh",
                              "args": "-NoProfile -Command \"Write-Output local-chain-value\"",
                              "mode": "raw"
                            }
                            """)
                    }
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtimeSecret"] = new RepoSecretConfig
                    {
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        var originalGenericCi = Environment.GetEnvironmentVariable("CI");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            Environment.SetEnvironmentVariable("CI", null);

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=exec", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalCi);
            Environment.SetEnvironmentVariable("CI", originalGenericCi);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretsDoctorUsesCiRuntimeProviderChainWhenInCi()
    {
        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig
                {
                    ProviderChain =
                    [
                        new RepoSecretProviderRouteConfig { ProviderRef = "localExec", Runtime = "local", Selector = "local-chain-value" },
                        new RepoSecretProviderRouteConfig { Provider = "env", Runtime = "ci", Env = "REXO_TEST_CHAIN_SECRET" },
                    ]
                },
                Providers = new Dictionary<string, RepoSecretProviderConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["localExec"] = new RepoSecretProviderConfig
                    {
                        Type = "exec",
                        Settings = ParseSettings(
                            """
                            {
                              "command": "pwsh",
                              "args": "-NoProfile -Command \"Write-Output local-chain-value\"",
                              "mode": "raw"
                            }
                            """)
                    }
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtimeSecret"] = new RepoSecretConfig
                    {
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        var originalGenericCi = Environment.GetEnvironmentVariable("CI");
        var originalSecret = Environment.GetEnvironmentVariable("REXO_TEST_CHAIN_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            Environment.SetEnvironmentVariable("CI", "true");
            Environment.SetEnvironmentVariable("REXO_TEST_CHAIN_SECRET", "ci-chain-value");

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=env", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalCi);
            Environment.SetEnvironmentVariable("CI", originalGenericCi);
            Environment.SetEnvironmentVariable("REXO_TEST_CHAIN_SECRET", originalSecret);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretsDoctorUsesGitHubActionsProviderWhenSelectedByCiChain()
    {
        var envName = $"REXO_TEST_GHA_SECRET_{Guid.NewGuid():N}";
        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig
                {
                    ProviderChain =
                    [
                        new RepoSecretProviderRouteConfig { Provider = "github-actions", Runtime = "github-actions" },
                    ]
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtimeSecret"] = new RepoSecretConfig
                    {
                        Env = envName,
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        var originalGenericCi = Environment.GetEnvironmentVariable("CI");
        var originalSecret = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            Environment.SetEnvironmentVariable("CI", "true");
            Environment.SetEnvironmentVariable(envName, "gha-chain-value");

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=github-actions", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalCi);
            Environment.SetEnvironmentVariable("CI", originalGenericCi);
            Environment.SetEnvironmentVariable(envName, originalSecret);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretsDoctorUsesAzureDevOpsProviderWhenSelectedByCiChain()
    {
        var envName = $"REXO_TEST_ADO_SECRET_{Guid.NewGuid():N}";
        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig
                {
                    ProviderChain =
                    [
                        new RepoSecretProviderRouteConfig { Provider = "azure-devops", Runtime = "azure-devops", Env = envName },
                    ]
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtimeSecret"] = new RepoSecretConfig
                    {
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalCi = Environment.GetEnvironmentVariable("TF_BUILD");
        var originalGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        var originalGenericCi = Environment.GetEnvironmentVariable("CI");
        var originalSecret = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("TF_BUILD", "True");
            Environment.SetEnvironmentVariable(envName, "ado-chain-value");

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=azure-devops", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TF_BUILD", originalCi);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGithubActions);
            Environment.SetEnvironmentVariable("CI", originalGenericCi);
            Environment.SetEnvironmentVariable(envName, originalSecret);
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SecretsDoctorUsesGitLabProviderWhenSelectedByCiChain()
    {
        var envName = $"REXO_TEST_GITLAB_SECRET_{Guid.NewGuid():N}";
        var config = CreateConfig(
            runCommand: "echo hi",
            secrets: new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig
                {
                    ProviderChain =
                    [
                        new RepoSecretProviderRouteConfig { Provider = "gitlab-ci", Runtime = "gitlab-ci", Env = envName },
                    ]
                },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtimeSecret"] = new RepoSecretConfig
                    {
                        Required = true,
                        ExposeInTemplates = false,
                    },
                },
            });

        var tempRoot = Path.Join(Path.GetTempPath(), $"rexo-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var originalGitlabCi = Environment.GetEnvironmentVariable("GITLAB_CI");
        var originalGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        var originalAzureDevOps = Environment.GetEnvironmentVariable("TF_BUILD");
        var originalGenericCi = Environment.GetEnvironmentVariable("CI");
        var originalSecret = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            Environment.SetEnvironmentVariable("TF_BUILD", null);
            Environment.SetEnvironmentVariable("GITLAB_CI", "true");
            Environment.SetEnvironmentVariable("CI", "true");
            Environment.SetEnvironmentVariable(envName, "gitlab-chain-value");

            var result = await ExecuteBuiltinCommandAsync(config, tempRoot, "secrets doctor");

            Assert.True(result.Success);
            Assert.Contains("provider=gitlab-ci", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITLAB_CI", originalGitlabCi);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGithubActions);
            Environment.SetEnvironmentVariable("TF_BUILD", originalAzureDevOps);
            Environment.SetEnvironmentVariable("CI", originalGenericCi);
            Environment.SetEnvironmentVariable(envName, originalSecret);
            Directory.Delete(tempRoot, true);
        }
    }

    private static async Task<CommandResult> ExecuteConfigCommandAsync(
        RepoConfig config,
        string repositoryRoot,
        string commandName = "run",
        EnvironmentSelectionResult? environmentSelection = null,
        RepoConfig? environmentConfig = null)
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
        loader.LoadInto(registry, config, repositoryRoot, executor, environmentConfig);

        var invocation = new CommandInvocation(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>(),
            false,
            null,
            repositoryRoot)
        {
            EnvironmentSelection = environmentSelection,
        };

        return await executor.ExecuteAsync(commandName, invocation, CancellationToken.None);
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
