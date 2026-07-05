namespace Rexo.Execution.Tests;

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
