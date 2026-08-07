namespace Rexo.Configuration.Tests;

using Rexo.Configuration;

[Collection("EnvironmentVariableSensitive")]
public sealed class RepoConfigurationLoaderSecretsYamlTests
{
    [Fact]
    public async Task LoadAsyncParsesYamlSecretsContract()
    {
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

        var dir = Path.Combine(Path.GetTempPath(), $"rexo-yaml-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "rexo.yml");

        await File.WriteAllTextAsync(
            configPath,
            """
            $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
            schemaVersion: "1.0"
            name: yaml-secrets-sample
            commands:
              run:
                description: Run
                options: {}
                steps:
                  - run: echo hi
            aliases: {}
            secrets:
              defaults:
                provider: env
                required: true
                cache:
                  enabled: false
            """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

            Assert.NotNull(config.Secrets);
            Assert.Equal("env", config.Secrets!.Defaults!.Provider);
            Assert.True(config.Secrets.Defaults.Required);
            Assert.Equal(false, config.Secrets.Defaults.Cache!.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task LoadAsyncParsesJsonSecretsProvidersAndItems()
    {
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

        var dir = Path.Combine(Path.GetTempPath(), $"rexo-json-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "rexo.json");

        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "json-secrets-sample",
              "commands": {
                "run": {
                  "description": "Run",
                  "options": {},
                  "steps": [
                    { "run": "echo hi" }
                  ]
                }
              },
              "aliases": {},
              "secrets": {
                "defaults": {
                  "provider": "exec",
                  "required": true
                },
                "providers": {
                  "shared": {
                    "type": "exec",
                    "settings": {
                      "command": "pwsh"
                    }
                  }
                },
                "items": {
                  "demo": {
                    "providerRef": "shared",
                    "selector": "abc",
                    "settings": {
                      "mode": "raw"
                    }
                  }
                }
              }
            }
            """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

            Assert.NotNull(config.Secrets);
            Assert.NotNull(config.Secrets!.Providers);
            Assert.True(config.Secrets.Providers!.ContainsKey("shared"));
            Assert.NotNull(config.Secrets.Items);
            Assert.True(config.Secrets.Items!.ContainsKey("demo"));
            Assert.Equal("shared", config.Secrets.Items["demo"].ProviderRef);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task LoadAsyncParsesYamlSecretsProviderChain()
    {
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

        var dir = Path.Combine(Path.GetTempPath(), $"rexo-yaml-secrets-chain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "rexo.yml");

        await File.WriteAllTextAsync(
            configPath,
            """
            $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
            schemaVersion: "1.0"
            name: yaml-secrets-chain-sample
            commands:
              run:
                description: Run
                options: {}
                steps:
                  - run: echo hi
            aliases: {}
            secrets:
              defaults:
                providerChain:
                  - runtime: local
                    providerRef: localExec
                  - runtime: ci
                    provider: env
              providers:
                localExec:
                  type: exec
                  settings:
                    command: pwsh
              items:
                chainSecret:
                  required: true
            """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

            Assert.NotNull(config.Secrets);
            Assert.NotNull(config.Secrets!.Defaults!.ProviderChain);
            Assert.Equal(2, config.Secrets.Defaults.ProviderChain!.Count);
            Assert.Equal("local", config.Secrets.Defaults.ProviderChain[0].Runtime);
            Assert.Equal("localExec", config.Secrets.Defaults.ProviderChain[0].ProviderRef);
            Assert.Equal("ci", config.Secrets.Defaults.ProviderChain[1].Runtime);
            Assert.Equal("env", config.Secrets.Defaults.ProviderChain[1].Provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
