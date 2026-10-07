namespace Rexo.Integration.Tests;

using System.Text.Json;
using Rexo.Cli;

/// <summary>
/// Tests for the --set CLI override flag (layer 6 of the config merge pipeline).
/// </summary>
[Collection("IntegrationSequential")]
public sealed class CliSetOverrideTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task SetOverrideChangesScalarPropertyReflectedInConfigResolved()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "set-test",
                  "versioning": { "provider": "fixed", "fallback": "1.0.0" }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            var outFile = Path.Join(tempDir, "resolved.json");
            var exitCode = await Program.ExecuteAsync(
                ["--set", "versioning.fallback=9.9.9", "--json", "--json-file", outFile, "config", "resolved"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var json = await File.ReadAllTextAsync(outFile);
            // The output is a CommandResult; the Output field contains the resolved config JSON
            using var doc = JsonDocument.Parse(json);
            var output = doc.RootElement.GetProperty("Message").GetString() ?? "";
            using var configDoc = JsonDocument.Parse(output);
            var fallback = configDoc.RootElement
                .GetProperty("Versioning")
                .GetProperty("Fallback")
                .GetString();
            Assert.Equal("9.9.9", fallback);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetOverrideBoolPropertyParsedAsBoolNotString()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-set-bool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "set-bool-test",
                  "runtime": { "push": { "enabled": true } }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            var outFile = Path.Join(tempDir, "resolved.json");
            var exitCode = await Program.ExecuteAsync(
                ["--set", "runtime.push.enabled=false", "--json", "--json-file", outFile, "config", "resolved"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var json = await File.ReadAllTextAsync(outFile);
            using var doc = JsonDocument.Parse(json);
            var output = doc.RootElement.GetProperty("Message").GetString() ?? "";
            using var configDoc = JsonDocument.Parse(output);
            var enabled = configDoc.RootElement
                .GetProperty("Runtime")
                .GetProperty("Push")
                .GetProperty("Enabled")
                .GetBoolean();
            Assert.False(enabled);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetOverrideWinsOverRepoConfigHighestPriority()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-set-priority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "priority-test",
                  "versioning": { "provider": "fixed", "fallback": "1.0.0" }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            var outFile = Path.Join(tempDir, "resolved.json");
            var exitCode = await Program.ExecuteAsync(
                ["--set", "versioning.provider=env", "--json", "--json-file", outFile, "config", "resolved"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var json = await File.ReadAllTextAsync(outFile);
            using var doc = JsonDocument.Parse(json);
            var output = doc.RootElement.GetProperty("Message").GetString() ?? "";
            using var configDoc = JsonDocument.Parse(output);
            var provider = configDoc.RootElement
                .GetProperty("Versioning")
                .GetProperty("Provider")
                .GetString();
            Assert.Equal("env", provider);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MultipleSetOverridesAllApplied()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-set-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "multi-set-test",
                  "versioning": { "provider": "fixed", "fallback": "1.0.0" }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            var outFile = Path.Join(tempDir, "resolved.json");
            var exitCode = await Program.ExecuteAsync(
                ["--set", "versioning.provider=env", "--set", "versioning.fallback=2.0.0",
                 "--json", "--json-file", outFile, "config", "resolved"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var json = await File.ReadAllTextAsync(outFile);
            using var doc = JsonDocument.Parse(json);
            var output = doc.RootElement.GetProperty("Message").GetString() ?? "";
            using var configDoc = JsonDocument.Parse(output);
            var versioning = configDoc.RootElement.GetProperty("Versioning");
            Assert.Equal("env", versioning.GetProperty("Provider").GetString());
            Assert.Equal("2.0.0", versioning.GetProperty("Fallback").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

