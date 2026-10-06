namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Configuration.Models;
using Rexo.Core.Models;

public sealed class BuiltinCommandRegistrationTests
{
    private static CommandInvocation EmptyInvocation() =>
        new(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

    [Fact]
    public async Task VersionCommandReturnsSuccess()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("version", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("version", result.Command);
    }

    [Fact]
    public async Task ListCommandReturnsSuccess()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("list", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("capabilities", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapabilitiesCommandReturnsSupportedContract()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("capabilities", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("contractVersion", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outputs.contract.v1", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainCommandWithNoArgReturnsMeaningfulResult()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "version" },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("explain", invocation, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task UnknownCommandReturnsExitCode8()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("definitely-not-a-command", EmptyInvocation(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(8, result.ExitCode);
    }

    [Fact]
    public async Task CheckReportsMissingConfiguredArtifactSourcePath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"rexo-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var config = new RepoConfig("test", [], [])
            {
                Artifacts =
                [
                    new RepoArtifactConfig("generic", "sample", new Dictionary<string, JsonElement>
                    {
                        ["directory"] = JsonSerializer.SerializeToElement("missing-package"),
                    }),
                ],
            };
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: workingDirectory);
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.Contains("artifact.source.missing", result.Message, StringComparison.Ordinal);
            Assert.Contains("missing-package", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckReportsCredentialPresenceWithoutPrintingTheSecret()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"rexo-check-credentials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            const string apiKey = "test-nuget-api-key-that-must-not-appear";
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, ".env"), $"NUGET_API_KEY={apiKey}");
            var config = new RepoConfig("test", [], [])
            {
                Artifacts = [new RepoArtifactConfig("nuget", "sample")],
                Runtime = new RepoRuntimeConfig(Push: new RepoPushConfig(RequireCleanWorkingTree: true)),
            };
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: workingDirectory);
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.Contains("artifact.credentials.available", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, result.Message, StringComparison.Ordinal);
            Assert.Contains("artifact.push.policy", result.Message, StringComparison.Ordinal);
            Assert.Contains("working tree is not clean", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckDetectsEnvironmentVersionFromDotEnvWithoutPrintingItsValue()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"rexo-check-version-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        var variableName = $"REXO_CHECK_VERSION_{Guid.NewGuid():N}";
        const string versionValue = "private-version-value";
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, ".env"), $"{variableName}={versionValue}");
            var config = new RepoConfig("test", [], [])
            {
                Versioning = new RepoVersioningConfig(
                    "env",
                    Settings: new Dictionary<string, string> { ["variable"] = variableName }),
            };
            var invocation = EmptyInvocation() with { WorkingDirectory = workingDirectory };
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.Contains("version.environment.available", result.Message, StringComparison.Ordinal);
            Assert.Contains(variableName, result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(versionValue, result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckRecognizesAutoVersionProviderAndReportsItsDetection()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"rexo-check-version-auto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".git"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".rexo"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, ".rexo", "rexo.yaml"), "config");
            var config = new RepoConfig("test", [], [])
            {
                Versioning = new RepoVersioningConfig("auto"),
            };
            var invocation = EmptyInvocation() with { WorkingDirectory = workingDirectory };
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Contains("Configured provider: auto (detected git)", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckTreatsHelmOciChartAsAnIdentityRatherThanAPath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"rexo-check-helm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var config = new RepoConfig("test", [], [])
            {
                Artifacts =
                [
                    new RepoArtifactConfig("helm-oci", "sample", new Dictionary<string, JsonElement>
                    {
                        ["chart"] = JsonSerializer.SerializeToElement("sample-chart"),
                    }),
                ],
            };
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: workingDirectory);
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.DoesNotContain("artifact.source.missing", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void RegistryExposedViaExecutor()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        Assert.NotNull(executor.Registry);
        Assert.True(executor.Registry.TryResolve("version", out _));
    }

    [Fact]
    public async Task ConfigResolvedReturnsSuccessWhenConfigProvided()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: [],
            Aliases: [])
        { SchemaVersion = "1.0" };
        var registry = BuiltinCommandRegistration.CreateDefault(config, configPath: null);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("config resolved", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("schemaVersion", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigResolvedReturnsFailureWhenNoConfigProvided()
    {
        var registry = BuiltinCommandRegistration.CreateDefault(config: null, configPath: null);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("config resolved", EmptyInvocation(), CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ConfigMaterializeDryRunDoesNotWriteFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-materialize-dry-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new RepoConfig("sample", null, null)
            {
                Versioning = new RepoVersioningConfig(Provider: "gitversion"),
            };
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));
            var invocation = EmptyInvocation() with
            {
                WorkingDirectory = dir,
                Options = new Dictionary<string, string?> { ["dry-run"] = "true" },
            };

            var result = await executor.ExecuteAsync("config materialize", invocation, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("Dry run: would materialize", result.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir, "GitVersion.yml")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExplainVersionReturnsProviderInfo()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: [],
            Aliases: [])
        {
            SchemaVersion = "1.0",
            Versioning = new RepoVersioningConfig(Provider: "fixed", Fallback: "1.0.0"),
        };
        var registry = BuiltinCommandRegistration.CreateDefault(config, configPath: null);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("explain version", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("fixed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainVersionReturnsMessageWhenNoVersioningConfig()
    {
        var registry = BuiltinCommandRegistration.CreateDefault(config: null, configPath: null);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("explain version", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No versioning", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoctorReportsEmbeddedSchemaFallbackWhenNoLocalSchema()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-doctor-noschema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.json"), "{}");

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("doctor", invocation, CancellationToken.None);

            Assert.Contains("schema", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("embedded fallback", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DoctorReportsLocalSchemaWhenPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-doctor-localschema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), "{}");

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("doctor", invocation, CancellationToken.None);

            Assert.Contains("schema", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("local", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CheckPassesForLoadedConfigAndReturnsStructuredFindings()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-check-valid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.yaml"), "config");

        try
        {
            var config = new RepoConfig("test", Commands: [], Aliases: [])
            {
                Versioning = new RepoVersioningConfig("fixed", "1.2.3"),
            };
            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: true,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("findings", result.Outputs.Keys);
            Assert.Contains("config.loaded", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("version.provider", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CheckTreatsDuplicateConfigAsWarningUnlessStrict()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-check-duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.yaml"), "config");
        await File.WriteAllTextAsync(Path.Combine(dir, "rexo.json"), "shadow");

        try
        {
            var config = new RepoConfig("test", Commands: [], Aliases: []);
            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var warningResult = await executor.ExecuteAsync("check", invocation, CancellationToken.None);
            var strictResult = await executor.ExecuteAsync("check", invocation with
            {
                Options = new Dictionary<string, string?> { ["strict"] = "true" },
            }, CancellationToken.None);

            Assert.True(warningResult.Success, warningResult.Message);
            Assert.Contains("[WARNING] config.duplicate", warningResult.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.False(strictResult.Success);
            Assert.Equal(9, strictResult.ExitCode);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GraphRendersEffectiveStepMetadataWithoutCommandBodies()
    {
        var config = new RepoConfig("test", Commands: new Dictionary<string, RepoCommandConfig>
        {
            ["release"] = new(
                Description: "Release safely",
                Options: [],
                Steps:
                [
                    new(Id: "verify", Uses: "command:verify", When: "{{options.push}}", Parallel: true),
                    new(Id: "publish", Command: "push artifacts", AlwaysRun: true,
                        Container: new RepoStepContainerConfig(Image: "alpine") { Fallback = "error" }),
                    new(Id: "emit", Run: "echo {{secrets.token}}"),
                ])
        }, Aliases: new Dictionary<string, string> { ["ship"] = "release" });
        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);
        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "ship" },
            new Dictionary<string, string?> { ["format"] = "json" },
            Json: true,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("graph", invocation, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains("\"command\": \"release\"", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"parallel\": true", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"alwaysRun\": true", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"container\": \"alpine\"", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("echo {{secrets.token}}", result.Message ?? string.Empty, StringComparison.Ordinal);

        var mermaidInvocation = invocation with
        {
            Options = new Dictionary<string, string?> { ["format"] = "mermaid" },
        };
        var mermaid = await executor.ExecuteAsync("graph", mermaidInvocation, CancellationToken.None);
        Assert.Contains("flowchart TD", mermaid.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("always-run", mermaid.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsUnknownArtifactProvidersAndDoesNotResolveRequiredSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-check-unknown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.yaml"), "config");

        try
        {
            var config = new RepoConfig("test", Commands: [], Aliases: [])
            {
                Artifacts = [new RepoArtifactConfig("not-a-provider", "unknown")],
                Secrets = new RepoSecretsConfig
                {
                    Items = new Dictionary<string, RepoSecretConfig>
                    {
                        ["private-token"] = new() { Env = "MISSING_SECRET_FOR_RX_CHECK" },
                    },
                },
            };
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));
            var result = await executor.ExecuteAsync("check", new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("artifact.provider.unknown", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("secrets.preflight", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("MISSING_SECRET_FOR_RX_CHECK", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CheckReportsMissingPolicyLockWhenStrictLockingIsEnabled()
    {
        var originalRequireLocked = Environment.GetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED");
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-check-policy-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));

        try
        {
            Environment.SetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED", "true");
            var config = new RepoConfig("sample", null, null)
            {
                PolicySources = ["https://raw.githubusercontent.com/agile-north/rexo/main/policy.json"],
            };
            var invocation = EmptyInvocation() with { WorkingDirectory = dir };
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var missingLock = await executor.ExecuteAsync("check", invocation, CancellationToken.None);
            Assert.False(missingLock.Success);
            Assert.Contains("strict policy locking", missingLock.Message, StringComparison.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.lock.yaml"), "schemaVersion: \"1.0\"\npolicies: []\n");
            var existingLock = await executor.ExecuteAsync("check", invocation, CancellationToken.None);
            Assert.Contains("policy.lockfile", existingLock.Message, StringComparison.Ordinal);
            Assert.Contains("lack lock entries", existingLock.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED", originalRequireLocked);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CheckWarnsWhenPolicyLockfileDoesNotCoverConfiguredSources()
    {
        var originalRequireLocked = Environment.GetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED");
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-check-policy-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));
        try
        {
            Environment.SetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED", null);
            await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.yaml"), "config");
            await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", "rexo.lock.yaml"), "schemaVersion: \"1.0\"\npolicies: []\n");
            var config = new RepoConfig("sample", null, null)
            {
                PolicySources = ["https://example.invalid/policy.json"],
            };
            var invocation = EmptyInvocation() with { WorkingDirectory = dir };
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Contains("policy.lockfile.coverage", result.Message, StringComparison.Ordinal);
            Assert.Contains("lack lock entries", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED", originalRequireLocked);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CheckReportsMalformedPolicyLockfileAsStructuredError()
    {
        var originalRequireLocked = Environment.GetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED");
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-check-policy-lock-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));
        try
        {
            Environment.SetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED", null);
            await File.WriteAllTextAsync(
                Path.Combine(dir, ".rexo", "rexo.lock.yaml"),
                "schemaVersion: \"1.0\"\npolicies: [\n");
            var config = new RepoConfig("sample", null, null)
            {
                PolicySources = ["https://example.invalid/policy.json"],
            };
            var invocation = EmptyInvocation() with { WorkingDirectory = dir };
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));

            var result = await executor.ExecuteAsync("check", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("policy.lockfile.invalid", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_POLICY_REQUIRE_LOCKED", originalRequireLocked);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("bash", "complete -W")]
    [InlineData("zsh", "compctl -k")]
    [InlineData("fish", "complete -c rx")]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    public async Task CompletionGeneratesSupportedShellScript(string shell, string expectedText)
    {
        var config = new RepoConfig("test", Commands: new Dictionary<string, RepoCommandConfig>
        {
            ["ship"] = new("Ship", [], []),
            ["release candidate"] = new("Not a shell-safe word", [], []),
        }, Aliases: []);
        var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault(config));
        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["shell"] = shell },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("completion", invocation, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains(expectedText, result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("ship", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("release candidate", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorIncludesHelmCheckWhenHelmOciArtifactConfigured()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-doctor-helm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new RepoConfig(
                "test",
                Commands: null,
                Aliases: null)
            {
                Artifacts = [new RepoArtifactConfig("helm-oci", "my-chart")]
            };

            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("doctor", invocation, CancellationToken.None);

            // helm check appears in output (pass or fail depending on environment)
            Assert.Contains("helm", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("gitversion", "gitversion")]
    [InlineData("minver", "minver")]
    [InlineData("nbgv", "nbgv")]
    public async Task DoctorIncludesVersionProviderCheckWhenExternalProviderConfigured(
        string providerKey, string expectedCheckName)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-doctor-vp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new RepoConfig(
                "test",
                Commands: null,
                Aliases: null)
            {
                Versioning = new RepoVersioningConfig(providerKey)
            };

            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("doctor", invocation, CancellationToken.None);

            Assert.Contains(expectedCheckName, result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DoctorDoesNotIncludeVersionProviderCheckForFixedProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-doctor-fixed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new RepoConfig(
                "test",
                Commands: null,
                Aliases: null)
            {
                Versioning = new RepoVersioningConfig("fixed", Fallback: "1.0.0")
            };

            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>(),
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("doctor", invocation, CancellationToken.None);

            // fixed provider needs no external tool — should not see gitversion/minver/nbgv in output
            Assert.DoesNotContain("gitversion", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("minver", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nbgv", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExplainReturnsAliasTargetInfo()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["release"] = new RepoCommandConfig(
                    Description: "Full release workflow",
                    Options: new Dictionary<string, RepoOptionConfig>
                    {
                        ["push"] = new RepoOptionConfig("bool")
                    },
                    Steps: [new RepoStepConfig { Uses = "builtin:resolve-version" }])
            },
            Aliases: new Dictionary<string, string> { ["rel"] = "release" })
        { SchemaVersion = "1.0" };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "rel" },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("explain", invocation, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("rel", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alias", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecretsDoctorReturnsNoSecretsWhenConfigHasNoSecrets()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>(),
            Aliases: new Dictionary<string, string>())
        {
            SchemaVersion = "1.0",
        };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("secrets doctor", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("No configured secrets", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecretsDoctorFailsWhenRequiredSecretMissing()
    {
        var envName = $"REXO_MISSING_SECRET_{Guid.NewGuid():N}";
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>(),
            Aliases: new Dictionary<string, string>())
        {
            SchemaVersion = "1.0",
            Secrets = new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apiKey"] = new RepoSecretConfig
                    {
                        Env = envName,
                        Required = true,
                        ExposeInTemplates = true,
                    },
                },
            },
        };

        var original = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, null);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);

            var result = await executor.ExecuteAsync("secrets preflight", EmptyInvocation(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(9, result.ExitCode);
            Assert.Contains("missing-required", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(result.StructuredErrors);
            Assert.Equal(ErrorCodes.SecretResolutionFailed, result.StructuredErrors[0].Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, original);
        }
    }

    [Fact]
    public async Task SecretsDoctorShowsMappedSecretMetadataWhenResolved()
    {
        var envName = $"REXO_PRESENT_SECRET_{Guid.NewGuid():N}";
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>(),
            Aliases: new Dictionary<string, string>())
        {
            SchemaVersion = "1.0",
            Secrets = new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nugetApiKey"] = new RepoSecretConfig
                    {
                        Env = envName,
                        Required = true,
                        ExposeInTemplates = false,
                        MapToEnv = "MY_FEED_API_KEY",
                    },
                },
            },
        };

        var original = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "present-secret");

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);

            var result = await executor.ExecuteAsync("secrets doctor", EmptyInvocation(), CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("resolved", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mapToEnv=MY_FEED_API_KEY", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("present-secret", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, original);
        }
    }

    [Fact]
    public async Task SecretsDoctorShowsMultipleMappedSecretMetadataWhenResolved()
    {
        var envName = $"REXO_PRESENT_SECRET_{Guid.NewGuid():N}";
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>(),
            Aliases: new Dictionary<string, string>())
        {
            SchemaVersion = "1.0",
            Secrets = new RepoSecretsConfig
            {
                Defaults = new RepoSecretDefaultsConfig { Provider = "env", Required = true },
                Items = new Dictionary<string, RepoSecretConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nugetApiKey"] = new RepoSecretConfig
                    {
                        Env = envName,
                        Required = true,
                        ExposeInTemplates = false,
                        MapToEnv = "MY_FEED_API_KEY_PRIMARY",
                        MapToEnvs = ["MY_FEED_API_KEY_SECONDARY", "MY_FEED_API_KEY_TERTIARY"],
                    },
                },
            },
        };

        var original = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "present-secret");

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault(config);
            var executor = new DefaultCommandExecutor(registry);

            var result = await executor.ExecuteAsync("secrets doctor", EmptyInvocation(), CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("resolved", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mapToEnvs=MY_FEED_API_KEY_PRIMARY, MY_FEED_API_KEY_SECONDARY, MY_FEED_API_KEY_TERTIARY", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("present-secret", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, original);
        }
    }

    [Fact]
    public async Task ExplainAliasWithNoMatchingCommandShowsAliasOnly()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>(),
            Aliases: new Dictionary<string, string> { ["r"] = "restore" })
        { SchemaVersion = "1.0" };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "r" },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("explain", invocation, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("r", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restore", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainUnknownNameReturnsFailure()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>(),
            Aliases: new Dictionary<string, string>())
        { SchemaVersion = "1.0" };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "no-such-command" },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("explain", invocation, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(8, result.ExitCode);
    }

    [Fact]
    public async Task ListIncludesConfigSubCommands()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("list", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("config resolved", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("config sources", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("config materialize", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explain version", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainRecognizesConfigResolvedAsBuiltin()
    {
        var registry = BuiltinCommandRegistration.CreateDefault();
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "config resolved" },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("explain", invocation, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("built-in", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListOmitsHiddenConfigCommands()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["visible"] = new RepoCommandConfig(
                    Description: "Shown in list",
                    Options: [],
                    Steps: []),
                ["hidden-helper"] = new RepoCommandConfig(
                    Description: "Hidden from list",
                    Options: [],
                    Steps: [])
                {
                    Hidden = true,
                },
            },
            Aliases: [])
        { SchemaVersion = "1.0" };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var result = await executor.ExecuteAsync("list", EmptyInvocation(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("visible", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden-helper", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainHiddenCommandReturnsCommandDetailsWhenExplicitlyRequested()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["hidden-helper"] = new RepoCommandConfig(
                    Description: "Reusable hidden helper",
                    Options: [],
                    Steps: [new RepoStepConfig { Id = "helper", Uses = "builtin:resolve-version" }])
                {
                    Hidden = true,
                },
            },
            Aliases: [])
        {
            SchemaVersion = "1.0",
            Versioning = new RepoVersioningConfig(Provider: "fixed", Fallback: "1.2.3"),
        };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "hidden-helper" },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("explain", invocation, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hidden-helper", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reusable hidden helper", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainAliasToHiddenTargetReturnsAliasAndTargetInfo()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["hidden-helper"] = new RepoCommandConfig(
                    Description: "Reusable hidden helper",
                    Options: [],
                    Steps: [new RepoStepConfig { Id = "helper", Uses = "builtin:resolve-version" }])
                {
                    Hidden = true,
                },
            },
            Aliases: new Dictionary<string, string> { ["helper"] = "hidden-helper" })
        {
            SchemaVersion = "1.0",
            Versioning = new RepoVersioningConfig(Provider: "fixed", Fallback: "1.2.3"),
        };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);

        var invocation = new CommandInvocation(
            new Dictionary<string, string> { ["command"] = "helper" },
            new Dictionary<string, string?>(),
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("explain", invocation, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("helper", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden-helper", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alias", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListIncludesHiddenCommandsWhenIncludeHiddenOptionIsTrue()
    {
        var config = new RepoConfig(
            Name: "test",
            Commands: new Dictionary<string, RepoCommandConfig>
            {
                ["visible"] = new RepoCommandConfig(
                    Description: "Shown in list",
                    Options: [],
                    Steps: []),
                ["hidden-helper"] = new RepoCommandConfig(
                    Description: "Shown only when requested",
                    Options: [],
                    Steps: [])
                {
                    Hidden = true,
                },
            },
            Aliases: [])
        { SchemaVersion = "1.0" };

        var registry = BuiltinCommandRegistration.CreateDefault(config);
        var executor = new DefaultCommandExecutor(registry);
        var invocation = new CommandInvocation(
            new Dictionary<string, string>(),
            new Dictionary<string, string?>
            {
                ["include-hidden"] = "true",
            },
            Json: false,
            JsonFile: null,
            WorkingDirectory: "C:\\repo");

        var result = await executor.ExecuteAsync("list", invocation, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("visible", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden-helper", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
