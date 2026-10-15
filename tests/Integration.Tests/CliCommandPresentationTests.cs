namespace Rexo.Integration.Tests;

using System.Globalization;
using Rexo.Cli;

[Collection("IntegrationSequential")]
public sealed class CliCommandPresentationTests
{
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
