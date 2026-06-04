namespace Rexo.Integration.Tests;

using System.Globalization;
using System.Text.Json;
using Rexo.Cli;

[Collection("IntegrationSequential")]
public sealed class CliJsonOutputTests
{
    [Fact]
    public async Task JsonModeWritesOnlyJsonToStdout()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-json-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "sample",
                  "commands": {
                    "hello": {
                      "description": "Test command",
                      "steps": [
                        { "id": "resolve", "uses": "builtin:resolve-version" }
                      ]
                    }
                  },
                  "versioning": {
                    "provider": "fixed",
                    "fallback": "1.2.3"
                  }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Program.ExecuteAsync(["--json", "hello"], CancellationToken.None);

            Assert.Equal(0, exitCode);

            var output = stdout.ToString().Trim();
            Assert.NotEmpty(output);
            using var parsed = JsonDocument.Parse(output);
            Assert.Equal("hello", parsed.RootElement.GetProperty("Command").GetString());
            Assert.DoesNotContain("Resolved version", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("  > ", output, StringComparison.Ordinal);
            Assert.True(string.IsNullOrWhiteSpace(stderr.ToString()));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.CurrentDirectory = originalDirectory;

      if (Directory.Exists(tempDir))
      {
        Directory.Delete(tempDir, true);
      }
    }
  }

  [Fact]
  public async Task JsonFileModeWritesRunManifestForDirectCommandPath()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-json-file-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    var originalDirectory = Environment.CurrentDirectory;

    try
    {
      Environment.CurrentDirectory = tempDir;

      var jsonFile = Path.Combine(tempDir, "out", "version.json");
      var manifestFile = Path.Combine(tempDir, "out", "version-manifest.json");

      var exitCode = await Program.ExecuteAsync(["version", "--json-file", jsonFile, "--quiet"], CancellationToken.None);

      Assert.Equal(0, exitCode);
      Assert.True(File.Exists(jsonFile));
      Assert.True(File.Exists(manifestFile));

      var json = await File.ReadAllTextAsync(jsonFile);
      using var resultDoc = JsonDocument.Parse(json);
      Assert.Equal("version", resultDoc.RootElement.GetProperty("Command").GetString());

      var manifestJson = await File.ReadAllTextAsync(manifestFile);
      using var manifestDoc = JsonDocument.Parse(manifestJson);
      Assert.Equal("version", manifestDoc.RootElement.GetProperty("CommandExecuted").GetString());
      Assert.True(manifestDoc.RootElement.TryGetProperty("Success", out var success));
      Assert.True(success.GetBoolean());
    }
    finally
    {
      Environment.CurrentDirectory = originalDirectory;

      if (Directory.Exists(tempDir))
      {
        Directory.Delete(tempDir, true);
      }
    }
  }

  [Fact]
  public async Task CommandOutputDefaultsCanDisableStdoutAndSetJsonFile()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-output-defaults-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    var originalDirectory = Environment.CurrentDirectory;
    var originalOut = Console.Out;
    var originalError = Console.Error;

    try
    {
      var jsonFile = Path.Combine(tempDir, "artifacts", "version.json");

      await File.WriteAllTextAsync(
          Path.Combine(tempDir, "rexo.json"),
          $$"""
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "outputs": {
              "command": {
                "stdout": false,
                "jsonFile": "{{jsonFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
              }
            },
            "commands": {
              "hello": {
                "description": "Test command",
                "steps": [
                  { "id": "resolve", "uses": "builtin:resolve-version" }
                ]
              }
            },
            "versioning": {
              "provider": "fixed",
              "fallback": "1.2.3"
            }
          }
          """);

      Environment.CurrentDirectory = tempDir;

      using var stdout = new StringWriter(CultureInfo.InvariantCulture);
      using var stderr = new StringWriter(CultureInfo.InvariantCulture);
      Console.SetOut(stdout);
      Console.SetError(stderr);

      var exitCode = await Program.ExecuteAsync(["version"], CancellationToken.None);

      Assert.Equal(0, exitCode);
      Assert.True(File.Exists(jsonFile));
      Assert.True(File.Exists(Path.Combine(tempDir, "artifacts", "version-manifest.json")));
      Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()));
      Assert.True(string.IsNullOrWhiteSpace(stderr.ToString()));
    }
    finally
    {
      Console.SetOut(originalOut);
      Console.SetError(originalError);
      Environment.CurrentDirectory = originalDirectory;

      if (Directory.Exists(tempDir))
      {
        Directory.Delete(tempDir, true);
      }
    }
  }

  [Fact]
  public async Task ExplicitCiProviderCanEmitLocallyWithoutDetectedCi()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-ci-emit-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    var originalDirectory = Environment.CurrentDirectory;
    var originalOut = Console.Out;
    var originalError = Console.Error;

    try
    {
      await File.WriteAllTextAsync(
          Path.Combine(tempDir, "rexo.json"),
          """
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "outputs": {
              "command": {
                "stdout": false
              },
              "ci": {
                "emit": true,
                "provider": "generic",
                "prefix": "CI_"
              }
            },
            "versioning": {
              "provider": "fixed",
              "fallback": "1.2.3"
            }
          }
          """);

      Environment.CurrentDirectory = tempDir;

      using var stdout = new StringWriter(CultureInfo.InvariantCulture);
      using var stderr = new StringWriter(CultureInfo.InvariantCulture);
      Console.SetOut(stdout);
      Console.SetError(stderr);

      var exitCode = await Program.ExecuteAsync(["version"], CancellationToken.None);

      Assert.Equal(0, exitCode);
      Assert.Contains("CI_COMMAND_EXECUTED=version", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
      Assert.Contains("CI_SUCCESS=true", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
      Assert.True(string.IsNullOrWhiteSpace(stderr.ToString()));
    }
    finally
    {
      Console.SetOut(originalOut);
      Console.SetError(originalError);
      Environment.CurrentDirectory = originalDirectory;

      if (Directory.Exists(tempDir))
      {
        Directory.Delete(tempDir, true);
      }
    }
  }

  [Fact]
  public async Task GitHubActionsProviderWritesVariablesToGitHubEnvFile()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-gh-env-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    var originalDirectory = Environment.CurrentDirectory;
    var originalOut = Console.Out;
    var originalError = Console.Error;
    var previousGitHubEnv = Environment.GetEnvironmentVariable("GITHUB_ENV");

    try
    {
      var envFile = Path.Combine(tempDir, "github.env");
      await File.WriteAllTextAsync(envFile, string.Empty);

      await File.WriteAllTextAsync(
          Path.Combine(tempDir, "rexo.json"),
          """
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "outputs": {
              "command": {
                "stdout": false
              },
              "ci": {
                "emit": true,
                "provider": "github-actions",
                "prefix": "CI_"
              }
            },
            "versioning": {
              "provider": "fixed",
              "fallback": "1.2.3"
            }
          }
          """);

      Environment.SetEnvironmentVariable("GITHUB_ENV", envFile);
      Environment.CurrentDirectory = tempDir;

      using var stdout = new StringWriter(CultureInfo.InvariantCulture);
      using var stderr = new StringWriter(CultureInfo.InvariantCulture);
      Console.SetOut(stdout);
      Console.SetError(stderr);

      var exitCode = await Program.ExecuteAsync(["version"], CancellationToken.None);

      Assert.Equal(0, exitCode);
      Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()));
      Assert.True(string.IsNullOrWhiteSpace(stderr.ToString()));

      var envContent = await File.ReadAllTextAsync(envFile);
      Assert.Contains("CI_COMMAND_EXECUTED=version", envContent, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("CI_SUCCESS=true", envContent, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      Console.SetOut(originalOut);
      Console.SetError(originalError);
      Environment.SetEnvironmentVariable("GITHUB_ENV", previousGitHubEnv);
      Environment.CurrentDirectory = originalDirectory;

      if (Directory.Exists(tempDir))
      {
        Directory.Delete(tempDir, true);
      }
    }
  }

  [Fact]
  public async Task GitHubActionsProviderCanWriteVariablesToGitHubOutputFile()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-gh-output-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    var originalDirectory = Environment.CurrentDirectory;
    var originalOut = Console.Out;
    var originalError = Console.Error;
    var previousGitHubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");

    try
    {
      var outputFile = Path.Combine(tempDir, "github.output");
      await File.WriteAllTextAsync(outputFile, string.Empty);

      await File.WriteAllTextAsync(
          Path.Combine(tempDir, "rexo.json"),
          """
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "outputs": {
              "command": {
                "stdout": false
              },
              "ci": {
                "emit": true,
                "provider": "github-actions",
                "github-actions": {
                  "scope": "output"
                },
                "prefix": "CI_"
              }
            },
            "versioning": {
              "provider": "fixed",
              "fallback": "1.2.3"
            }
          }
          """);

      Environment.SetEnvironmentVariable("GITHUB_OUTPUT", outputFile);
      Environment.CurrentDirectory = tempDir;

      using var stdout = new StringWriter(CultureInfo.InvariantCulture);
      using var stderr = new StringWriter(CultureInfo.InvariantCulture);
      Console.SetOut(stdout);
      Console.SetError(stderr);

      var exitCode = await Program.ExecuteAsync(["version"], CancellationToken.None);

      Assert.Equal(0, exitCode);
      Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()));
      Assert.True(string.IsNullOrWhiteSpace(stderr.ToString()));

      var outputContent = await File.ReadAllTextAsync(outputFile);
      Assert.Contains("CI_COMMAND_EXECUTED=version", outputContent, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("CI_SUCCESS=true", outputContent, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      Console.SetOut(originalOut);
      Console.SetError(originalError);
      Environment.SetEnvironmentVariable("GITHUB_OUTPUT", previousGitHubOutput);
      Environment.CurrentDirectory = originalDirectory;

      if (Directory.Exists(tempDir))
      {
        Directory.Delete(tempDir, true);
      }
    }
  }
}

