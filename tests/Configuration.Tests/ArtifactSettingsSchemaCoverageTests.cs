namespace Rexo.Configuration.Tests;

using Rexo.Configuration;

[Collection("EnvironmentVariableSensitive")]
public sealed class ArtifactSettingsSchemaCoverageTests
{
    [Fact]
    public async Task LoadAsyncAcceptsExpandedDockerAndNuGetSettings()
    {
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

        var dir = CreateTempDirectory();
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
                  "maxParallel": 3,
                  "steps": [
                    {
                      "id": "a",
                      "run": "echo hi",
                      "parallel": true,
                      "outputPattern": "(?<value>.+)",
                      "outputFile": "artifacts/out.txt"
                    }
                  ]
                }
              },
              "aliases": {},
              "artifacts": [
                {
                  "type": "docker",
                  "name": "app",
                  "settings": {
                    "image": "ghcr.io/acme/app",
                    "runner": "buildx",
                    "buildArgs": {
                      "ENV": "prod"
                    },
                    "secrets": {
                      "npm_token": {
                        "env": "NPM_TOKEN"
                      }
                    },
                    "buildOutput": ["type=local,dest=./out"],
                    "push": {
                      "enabled": true,
                      "branches": ["main", "release/*"]
                    },
                    "aliases": {
                      "sanitizedBranch": true,
                      "rules": [
                        {
                          "match": "regex:^feature/(.+)$",
                          "template": "feature-$1",
                          "sanitize": "sanitized"
                        }
                      ]
                    },
                    "stages": {
                      "publish": {
                        "target": "publish",
                        "output": ["type=local,dest=./publish"],
                        "runner": "buildx",
                        "platform": "linux/amd64"
                      }
                    },
                    "stageFallback": false
                  }
                },
                {
                  "type": "nuget",
                  "name": "Rexo.Core",
                  "settings": {
                    "project": "src/Core/Core.csproj",
                    "output": "artifacts/packages",
                    "target": {
                      "source": "https://api.nuget.org/v3/index.json",
                      "sourceEnv": "NUGET_TARGET_SOURCE",
                      "apiKeyEnv": "NUGET_API_KEY"
                    }
                  }
                }
              ]
            }
            """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
            Assert.Equal(2, config.Artifacts?.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task LoadAsyncRejectsDockerSecretWithoutEnvOrFile()
    {
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

        var dir = CreateTempDirectory();
        var configPath = Path.Join(dir, "rexo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "sample",
              "commands": {
                "build": { "steps": [] }
              },
              "aliases": {},
              "artifacts": [
                {
                  "type": "docker",
                  "name": "app",
                  "settings": {
                    "secrets": {
                      "npm_token": {}
                    }
                  }
                }
              ]
            }
            """);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None));

            Assert.Contains("Validation errors", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task LoadAsyncRejectsUnsupportedDockerRunner()
    {
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
        Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

        var dir = CreateTempDirectory();
        var configPath = Path.Join(dir, "rexo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "sample",
              "commands": {
                "build": { "steps": [] }
              },
              "aliases": {},
              "artifacts": [
                {
                  "type": "docker",
                  "name": "app",
                  "settings": {
                    "runner": "podman"
                  }
                }
              ]
            }
            """);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None));

            Assert.Contains("Validation errors", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            DeleteDirectory(dir);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-artifact-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
