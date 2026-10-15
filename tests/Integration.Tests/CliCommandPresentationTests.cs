namespace Rexo.Integration.Tests;

using System.Globalization;
using Rexo.Cli;

[Collection("IntegrationSequential")]
public sealed class CliCommandPresentationTests
{
    private static readonly string[][] InformationalCommands =
    [
        ["config", "explain", "name"],
        ["explain", "version"],
        ["init", "detect"],
        ["run", "explain", "version"],
        ["check"],
        ["secrets", "doctor"],
        ["secrets", "preflight"],
    ];

    [Fact]
    public async Task DefaultSummaryHidesCommandBodyAndDoesNotCreateCommandManifest()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "hello": {
              "steps": [
                { "id": "greeting", "run": "echo synthetic-output" }
              ]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, _) = await ExecuteAsync(directory, ["--no-color", "hello"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Step: greeting", stdout, StringComparison.Ordinal);
            Assert.Contains("Execution: native", stdout, StringComparison.Ordinal);
            Assert.Contains("synthetic-output", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("echo synthetic-output", stdout, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Join(directory, "artifacts", "manifests")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CliFullEchoOverridesStepPolicyAndMasksKnownSecrets()
    {
        const string secret = "synthetic-sensitive-token-123";
        var priorSecret = Environment.GetEnvironmentVariable("EXAMPLE_TOKEN");
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "runtime": { "commands": { "echo": "none" } },
          "commands": {
            "hello": {
              "echo": "none",
              "steps": [
                { "id": "greeting", "echo": "none", "run": "echo {{env.EXAMPLE_TOKEN}}" }
              ]
            }
          }
        }
        """);

        try
        {
            Environment.SetEnvironmentVariable("EXAMPLE_TOKEN", secret);
            var (exitCode, stdout, _) = await ExecuteAsync(directory, ["--no-color", "--echo", "full", "hello"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("[native]", stdout, StringComparison.Ordinal);
            Assert.Contains("***", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stdout, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Join(directory, "artifacts", "manifests")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXAMPLE_TOKEN", priorSecret);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedStepLabelMasksSecretsBeforeSanitizingCommandText()
    {
        const string secret = "synthetic-sensitive-label-token";
        var priorSecret = Environment.GetEnvironmentVariable("EXAMPLE_TOKEN");
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "hello": {
              "steps": [{ "run": "echo synthetic-sensitive-label-token" }]
            }
          }
        }
        """);

        try
        {
            Environment.SetEnvironmentVariable("EXAMPLE_TOKEN", secret);
            var (exitCode, stdout, stderr) = await ExecuteAsync(directory, ["--no-color", "hello"]);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("Step: run-echo", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXAMPLE_TOKEN", priorSecret);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StepEchoOverridesCommandAndRuntimeSettings()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "runtime": { "commands": { "echo": "full" } },
          "commands": {
            "hello": {
              "echo": "none",
              "steps": [
                { "id": "verbose-step", "echo": "full", "run": "echo first-command-body" },
                { "id": "quiet-step", "run": "echo second-command-body" }
              ]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, _) = await ExecuteAsync(directory, ["--no-color", "hello"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("echo first-command-body", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("echo second-command-body", stdout, StringComparison.Ordinal);
            Assert.Contains("first-command-body", stdout, StringComparison.Ordinal);
            Assert.Contains("second-command-body", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommandStepEchoPolicyIsAppliedToDelegatedCommand()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "parent": {
              "steps": [{ "command": "child", "echo": "full" }]
            },
            "child": {
              "steps": [{ "run": "echo delegated-command-body" }]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "parent"]);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("echo delegated-command-body", stdout, StringComparison.Ordinal);

            (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "--echo", "none", "parent"]);

            Assert.True(exitCode == 0, stderr);
            Assert.DoesNotContain("echo delegated-command-body", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuiltinProgressMessagesRespectEffectiveEchoMode()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "versioning": { "provider": "fixed", "fallback": "1.2.3" },
          "commands": {
            "quiet-validation": { "steps": [{ "uses": "builtin:validate", "echo": "none" }] },
            "loud-validation": { "steps": [{ "uses": "builtin:validate", "echo": "full" }] },
            "quiet-version": { "steps": [{ "uses": "builtin:resolve-version", "echo": "none" }] },
            "loud-version": { "steps": [{ "uses": "builtin:resolve-version", "echo": "full" }] },
            "quiet-clean": { "steps": [{ "uses": "builtin:clean", "echo": "none" }] }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "quiet-validation"]);
            Assert.True(exitCode == 0, stderr);
            Assert.DoesNotContain("Validating configuration", stdout, StringComparison.Ordinal);

            (exitCode, stdout, stderr) = await ExecuteAsync(directory, ["--no-color", "loud-validation"]);
            Assert.True(exitCode == 0, stderr);
            Assert.Contains("Validating configuration", stdout, StringComparison.Ordinal);

            (exitCode, stdout, stderr) = await ExecuteAsync(directory, ["--no-color", "quiet-version"]);
            Assert.True(exitCode == 0, stderr);
            Assert.DoesNotContain("Resolved version: 1.2.3", stdout, StringComparison.Ordinal);

            (exitCode, stdout, stderr) = await ExecuteAsync(directory, ["--no-color", "loud-version"]);
            Assert.True(exitCode == 0, stderr);
            Assert.Contains("Resolved version: 1.2.3", stdout, StringComparison.Ordinal);

            (exitCode, stdout, stderr) = await ExecuteAsync(directory, ["--no-color", "quiet-clean"]);
            Assert.True(exitCode == 0, stderr);
            Assert.DoesNotContain("Cleaning generated output", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutoVersionProviderProgressRespectsEffectiveEchoMode()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "versioning": { "provider": "auto", "fallback": "1.2.3" },
          "commands": {
            "quiet-version": { "steps": [{ "uses": "builtin:resolve-version", "echo": "none" }] },
            "loud-version": { "steps": [{ "uses": "builtin:resolve-version", "echo": "full" }] }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(directory, ["--no-color", "quiet-version"]);
            Assert.True(exitCode == 0, stderr);
            Assert.DoesNotContain("[auto] Detected version provider:", stdout, StringComparison.Ordinal);

            (exitCode, stdout, stderr) = await ExecuteAsync(directory, ["--no-color", "loud-version"]);
            Assert.True(exitCode == 0, stderr);
            Assert.Contains("[auto] Detected version provider:", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EchoGlobalFlagDoesNotConsumeDeclaredCommandOption()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "hello": {
              "options": { "echo": { "type": "string" } },
              "steps": [
                { "id": "greeting", "run": "echo custom-option-output" }
              ]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, _) = await ExecuteAsync(directory, ["--no-color", "hello", "--echo", "custom-value"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("custom-option-output", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("echo custom-option-output", stdout, StringComparison.Ordinal);

            (exitCode, stdout, _) = await ExecuteAsync(
                directory,
                ["--no-color", "--echo", "full", "hello"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("echo custom-option-output", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GlobalEchoAndCommandEchoAreClassifiedPerOccurrence()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "hello": {
              "options": { "echo": { "type": "string" } },
              "steps": [{ "run": "echo {{options.echo}}" }]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "--echo", "full", "hello", "--echo", "custom-value"]);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("echo custom-value", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommandMatchingDoesNotJoinWordsAcrossCommandOwnedOptions()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "build": {
              "options": { "echo": { "type": "string" } },
              "steps": [{ "run": "echo shorter-{{options.echo}}" }]
            },
            "build release": {
              "steps": [{ "run": "echo longer-command" }]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "build", "--echo", "custom", "release"]);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("shorter-custom", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("longer-command", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommandOwnedEchoTokensKeepTheirOriginalOptionOrder()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "hello": {
              "options": {
                "flag": { "type": "bool" },
                "echo": { "type": "string" }
              },
              "steps": [
                { "run": "echo {{options.flag}}-{{options.echo}}-{{options.name}}" }
              ]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "hello", "--flag", "--echo", "custom", "--name", "tail"]);

            Assert.True(exitCode == 0, stderr);
            Assert.True(stdout.Contains("true-custom-tail", StringComparison.Ordinal), stdout);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BooleanCommandEchoDoesNotConsumeFollowingOptionsOrGlobalFlags()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "hello": {
              "options": {
                "echo": { "type": "bool" },
                "name": { "type": "string" }
              },
              "steps": [
                { "run": "echo {{options.echo}}-{{options.name}}" }
              ]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "hello", "--echo", "--name", "value"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("true-value", stdout, StringComparison.Ordinal);

            (exitCode, stdout, _) = await ExecuteAsync(
                directory,
                ["--no-color", "hello", "--echo", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"Success\": true", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Command 'hello' completed successfully.", stdout, StringComparison.Ordinal);

            (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "hello", "--echo", "-v", "--name", "value"]);

            Assert.True(exitCode == 0, stderr);
            Assert.Contains("true-value", stdout, StringComparison.Ordinal);

            (exitCode, _, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "hello", "--echo", "-q", "--name", "value"]);

            Assert.True(exitCode == 0, stderr);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunRouteHonorsCommandEchoAndInformationalResultsRemainVisible()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "hello": {
              "echo": "none",
              "steps": [{ "id": "greeting", "run": "echo run-route-output" }]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, _) = await ExecuteAsync(
                directory,
                ["--no-color", "run", "hello", "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("run-route-output", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Command 'hello' completed successfully.", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("StepId", stdout, StringComparison.Ordinal);

            (exitCode, stdout, _) = await ExecuteAsync(directory, ["--no-color", "list", "--echo", "none"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("hello", stdout, StringComparison.Ordinal);

            foreach (var commandArgs in InformationalCommands)
            {
                var commandInvocation = new List<string> { "--no-color" };
                commandInvocation.AddRange(commandArgs);
                commandInvocation.Add("--echo");
                commandInvocation.Add("none");
                (exitCode, stdout, _) = await ExecuteAsync(directory, commandInvocation.ToArray());

                Assert.NotEmpty(stdout);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AliasUsesTargetEchoPolicyAndEchoOptionOwnership()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "aliases": { "hi": "hello" },
          "commands": {
            "hello": {
              "echo": "none",
              "options": { "echo": { "type": "string" } },
              "steps": [{ "id": "greeting", "run": "echo alias-output" }]
            }
          }
        }
        """);

        try
        {
            foreach (var args in new[]
            {
                new[] { "--no-color", "hi", "--echo", "custom-value" },
                new[] { "--no-color", "run", "hi", "--verbose", "--echo", "custom-value" },
            })
            {
                var (exitCode, stdout, _) = await ExecuteAsync(directory, args);

                Assert.Equal(0, exitCode);
                Assert.Contains("alias-output", stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("Command 'hello' completed successfully.", stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("StepId", stdout, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EchoOwnershipUsesTheLongestMatchingCommand()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "build": {
              "options": { "echo": { "type": "string" } },
              "steps": [{ "run": "echo shorter-command" }]
            },
            "build release": {
              "steps": [{ "run": "echo longer-command" }]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, _) = await ExecuteAsync(
                directory,
                ["--no-color", "build", "release", "--echo", "full"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("echo longer-command", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("shorter-command", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GlobalEchoBetweenCommandWordsDoesNotHideLongerCommandOption()
    {
        var directory = CreateTempDirectory();
        await WriteConfigAsync(directory, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "build": {
              "steps": [{ "run": "echo shorter-command" }]
            },
            "build release": {
              "options": { "echo": { "type": "string" } },
              "steps": [{ "run": "echo {{options.echo}}" }]
            }
          }
        }
        """);

        try
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(
                directory,
                ["--no-color", "build", "--echo", "full", "release", "--echo", "custom-value"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("custom-value", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("shorter-command", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutomaticManifestIsOptInAndExplicitJsonFileStillWritesSidecar()
    {
        var directory = CreateTempDirectory();
        var config = """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "outputs": {
            "manifests": {
              "emit": true,
              "path": "custom-manifests"
            }
          },
          "commands": {
            "hello": { "steps": [{ "id": "greeting", "run": "echo hello" }] }
          }
        }
        """;
        await WriteConfigAsync(directory, config);

        try
        {
            var (exitCode, _, _) = await ExecuteAsync(directory, ["--no-color", "hello"]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Join(directory, "custom-manifests", "commands.json")));

            File.Delete(Path.Join(directory, "custom-manifests", "commands.json"));
            await WriteConfigAsync(directory, config.Replace("\"emit\": true", "\"emit\": false", StringComparison.Ordinal));
            var jsonFile = Path.Join(directory, "runs", "hello.json");
            (exitCode, _, _) = await ExecuteAsync(directory, ["--no-color", "hello", "--json-file", jsonFile]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(jsonFile));
            Assert.True(File.Exists(Path.Join(directory, "runs", "hello-manifest.json")));
            Assert.False(File.Exists(Path.Join(directory, "custom-manifests", "commands.json")));

            var priorManifest = Path.Join(directory, "custom-manifests", "commands.json");
            await File.WriteAllTextAsync(priorManifest, "existing-manifest");
            (exitCode, _, _) = await ExecuteAsync(directory, ["--no-color", "hello"]);
            Assert.Equal(0, exitCode);
            Assert.Equal("existing-manifest", await File.ReadAllTextAsync(priorManifest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), $"rexo-cli-output-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Task WriteConfigAsync(string directory, string json) =>
        File.WriteAllTextAsync(Path.Join(directory, "rexo.json"), json);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string workingDirectory,
        string[] args)
    {
        var originalDirectory = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Environment.CurrentDirectory = workingDirectory;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = await Program.ExecuteAsync(args, CancellationToken.None);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.CurrentDirectory = originalDirectory;
        }
    }
}
