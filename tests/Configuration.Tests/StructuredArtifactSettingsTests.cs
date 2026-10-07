namespace Rexo.Configuration.Tests;

using System.Text.Json;
using Rexo.Configuration;

[Collection("EnvironmentVariableSensitive")]
public sealed class StructuredArtifactSettingsTests
{
    [Fact]
    public async Task LoadAsyncPreservesStructuredArtifactSettings()
    {
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

        var dir = Path.Join(Path.GetTempPath(), $"rexo-artifact-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var configPath = Path.Join(dir, "rexo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "sample",
              "commands": {
                "build": {
                  "steps": []
                }
              },
              "aliases": {},
              "artifacts": [
                {
                  "type": "docker",
                  "name": "app",
                  "settings": {
                    "image": "ghcr.io/acme/app",
                    "buildArgs": {
                      "ENV": "prod"
                    },
                    "secrets": {
                      "npm_token": {
                        "env": "NPM_TOKEN"
                      }
                    },
                    "buildOutput": ["type=local,dest=./out"]
                  }
                }
              ]
            }
            """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

            var settings = config.Artifacts![0].Settings!;
            Assert.Equal(JsonValueKind.Object, settings["buildArgs"].ValueKind);
            Assert.Equal("prod", settings["buildArgs"].GetProperty("ENV").GetString());
            Assert.Equal(JsonValueKind.Object, settings["secrets"].ValueKind);
            Assert.Equal("NPM_TOKEN", settings["secrets"].GetProperty("npm_token").GetProperty("env").GetString());
            Assert.Equal(JsonValueKind.Array, settings["buildOutput"].ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
