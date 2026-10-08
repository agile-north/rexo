namespace Rexo.Configuration.Tests;

using Rexo.Cli;

[Collection("EnvironmentVariableSensitive")]
public sealed class CliBootstrapperParityTests
{
    [Fact]
    public async Task BuildServicesAsyncAppliesPolicySecretRoutesOnlyOnce()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-policy-secret-routes-{Guid.NewGuid():N}");
        var rexoDir = Path.Join(dir, ".rexo");
        Directory.CreateDirectory(rexoDir);
        var configPath = Path.Join(rexoDir, "rexo.json");
        var policyPath = Path.Join(rexoDir, "policy.json");
        var originalPolicySources = Environment.GetEnvironmentVariable("REXO_POLICY_SOURCES");
        Environment.SetEnvironmentVariable("REXO_POLICY_SOURCES", null);

        try
        {
            await File.WriteAllTextAsync(configPath, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {},
          "aliases": {},
          "secrets": {
            "defaults": {
              "providerChain": [{ "runtime": "local", "provider": "env" }]
            }
          }
        }
        """);
            await File.WriteAllTextAsync(policyPath, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json",
          "schemaVersion": "1.0",
          "name": "sample-policy",
          "secrets": {
            "defaults": {
              "providerChain": [{ "runtime": "ci", "provider": "env" }]
            }
          }
        }
        """);

            var (_, _, effectiveConfig, _) = await CliBootstrapper.BuildServicesAsync(
                dir, debug: false, setOverrides: null, CancellationToken.None);

            Assert.NotNull(effectiveConfig?.Secrets?.Defaults?.ProviderChain);
            Assert.Equal(2, effectiveConfig.Secrets.Defaults.ProviderChain!.Count);
            Assert.Equal("ci", effectiveConfig.Secrets.Defaults.ProviderChain[0].Runtime);
            Assert.Equal("local", effectiveConfig.Secrets.Defaults.ProviderChain[1].Runtime);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_POLICY_SOURCES", originalPolicySources);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task BuildServicesAsyncThrowsWhenArtifactTypeIsUnsupported()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-parity-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var configPath = Path.Join(dir, "rexo.json");
        await File.WriteAllTextAsync(configPath, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {},
          "aliases": {},
          "artifacts": [
            {
              "type": "not-a-real-provider",
              "name": "bad"
            }
          ]
        }
        """);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CliBootstrapper.BuildServicesAsync(dir, debug: false, setOverrides: null, CancellationToken.None));

            Assert.Contains("ART-005", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not-a-real-provider", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

}
