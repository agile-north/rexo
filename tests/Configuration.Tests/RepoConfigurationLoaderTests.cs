namespace Rexo.Configuration.Tests;

using System.Text.Json;
using Rexo.Configuration;
using Rexo.Policies;

[Collection("EnvironmentVariableSensitive")]
public sealed class RepoConfigurationLoaderTests
{
    [Fact]
    public async Task LoadAsyncParsesMinimalConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}.json");
        var root = Path.GetDirectoryName(path)!;
    Directory.CreateDirectory(root);
    await File.WriteAllTextAsync(
          Path.Combine(root, "rexo.schema.json"),
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
              "properties": {
                "$schema": { "type": "string" },
                "schemaVersion": { "type": "string" },
                "name": { "type": "string" },
                "commands": { "type": "object" },
                "aliases": { "type": "object" }
              }
            }
            """);

        await File.WriteAllTextAsync(path, """
        {
          "$schema": "rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "release": {
              "description": "Release",
              "options": {},
              "steps": []
            }
          },
          "aliases": {}
        }
        """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal("sample", config.Name);
      Assert.True(config.Commands!.ContainsKey("release"));
    }
    finally
    {
            File.Delete(path);
      var schemaPath = Path.Combine(root, "rexo.schema.json");
      if (File.Exists(schemaPath)) File.Delete(schemaPath);
    }
  }

  [Fact]
  public async Task LoadAsyncParsesConfigUsingEmbeddedSchemaWhenNoLocalSchemaExists()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-embedded-schema-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "build": {
              "description": "Build",
              "options": {},
              "steps": [
                { "run": "echo hello" }
              ]
            }
          },
          "aliases": {}
        }
        """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.Equal("sample", config.Name);
      Assert.True(config.Commands!.ContainsKey("build"));
    }
    finally
    {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncParsesRuntimeOutputSettings()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-output-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "commands": {
              "build": {
                "description": "Build",
                "options": {},
                "steps": [
                  { "run": "echo hello" }
                ]
              }
            },
            "aliases": {},
            "outputs": {
              "emit": false,
              "root": "build-output"
            }
          }
          """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.False(config.Outputs?.Emit);
      Assert.Equal("build-output", config.Outputs?.Root);
    }
    finally
    {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

    [Fact]
    public async Task LoadAsyncParsesStructuredCiScopeSelectors()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-ci-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var configPath = Path.Combine(dir, "rexo.json");
        await File.WriteAllTextAsync(configPath, """
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "commands": {
              "build": {
                "description": "Build",
                "options": {},
                "steps": [
                  { "run": "echo hello" }
                ]
              }
            },
            "aliases": {},
            "outputs": {
              "ci": {
                "emit": true,
                "github-actions": {
                  "scope": "output"
                },
                "emitEmptyValues": true,
                "scope": {
                  "mode": "full",
                  "include": ["repoName", "version.*"],
                  "exclude": ["regex:^version\\.secret.*"]
                }
              }
            }
          }
          """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

            Assert.True(config.Outputs?.Ci?.Emit);
            Assert.Equal("output", config.Outputs?.Ci?.GitHubActions?.Scope);
            Assert.True(config.Outputs?.Ci?.EmitEmptyValues);
            Assert.Equal(JsonValueKind.Object, config.Outputs?.Ci?.Scope?.ValueKind);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
  public async Task LoadAsyncParsesStructuredPushRules()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-push-rules-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "commands": {
              "build": {
                "description": "Build",
                "options": {},
                "steps": [
                  { "run": "echo hello" }
                ]
              }
            },
            "aliases": {},
            "runtime": {
              "push": {
                "enabled": true,
                "noPushInPullRequest": true,
                "requireCleanWorkingTree": true,
                "branches": ["main", "release/*"]
              }
            }
          }
          """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.NotNull(config.Runtime?.Push);
      Assert.True(config.Runtime!.Push!.Enabled);
      Assert.True(config.Runtime.Push.NoPushInPullRequest);
      Assert.True(config.Runtime.Push.RequireCleanWorkingTree);
      Assert.Equal(2, config.Runtime.Push.Branches!.Length);
    }
    finally
    {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncParsesRuntimeOutput()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-artifacts-output-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
          {
            "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
            "schemaVersion": "1.0",
            "name": "sample",
            "commands": {
              "build": {
                "description": "Build",
                "options": {},
                "steps": [
                  { "run": "echo hello" }
                ]
              }
            },
            "aliases": {},
            "outputs": {
              "emit": false,
              "root": "runtime-output"
            }
          }
          """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.False(config.Outputs?.Emit);
      Assert.Equal("runtime-output", config.Outputs?.Root);
    }
    finally
    {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

    [Fact]
    public async Task LoadAsyncThrowsWhenSchemaVersionMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "$schema": "rexo.schema.json",
          "name": "sample",
          "commands": {
            "release": {
              "options": {},
              "steps": [
                { "run": "echo hi" }
              ]
            }
          },
          "aliases": {}
        }
        """);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RepoConfigurationLoader.LoadAsync(path, CancellationToken.None));

            Assert.Contains("schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsyncThrowsWhenSchemaVersionUnsupported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "$schema": "rexo.schema.json",
          "schemaVersion": "2.0",
          "name": "sample",
          "commands": {
            "release": {
              "options": {},
              "steps": [
                { "run": "echo hi" }
              ]
            }
          },
          "aliases": {}
        }
        """);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RepoConfigurationLoader.LoadAsync(path, CancellationToken.None));

            Assert.Contains("Unsupported schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsyncThrowsWhenSchemaUriUnsupported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "$schema": "https://example.com/repo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "release": {
              "options": {},
              "steps": [
                { "run": "echo hi" }
              ]
            }
          },
          "aliases": {}
        }
        """);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RepoConfigurationLoader.LoadAsync(path, CancellationToken.None));

            Assert.Contains("$schema", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsyncSkipsSchemaValidationWhenEnvVarEnabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}.json");
        var previous = Environment.GetEnvironmentVariable(RepoConfigurationLoader.DisableSchemaValidationEnvVar);
        await File.WriteAllTextAsync(path, """
        {
          "$schema": "https://example.com/repo.schema.json",
          "schemaVersion": "2.0",
          "name": "sample",
          "commands": {
            "release": {
              "options": {},
              "steps": [
                { "run": "echo hi" }
              ]
            }
          },
          "aliases": {}
        }
        """);

        try
        {
            Environment.SetEnvironmentVariable(RepoConfigurationLoader.DisableSchemaValidationEnvVar, "true");
            var config = await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal("sample", config.Name);
            Assert.True(config.Commands!.ContainsKey("release"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RepoConfigurationLoader.DisableSchemaValidationEnvVar, previous);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task LoadAsyncThrowsWhenRequiredCapabilityIsUnsupported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "release": {
              "options": {},
              "steps": [
                { "run": "echo hi" }
              ]
            }
          },
          "aliases": {},
          "capabilities": {
            "required": ["capability.not.supported"]
          }
        }
        """);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RepoConfigurationLoader.LoadAsync(path, CancellationToken.None));

            Assert.Contains("CAP-001", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("capability.not.supported", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsyncAcceptsKnownRequiredCapability()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          "schemaVersion": "1.0",
          "name": "sample",
          "commands": {
            "release": {
              "options": {},
              "steps": [
                { "run": "echo hi" }
              ]
            }
          },
          "aliases": {},
          "capabilities": {
            "contractVersion": "1.0",
            "required": ["outputs.contract.v1"]
          }
        }
        """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.NotNull(config.Capabilities);
            Assert.Contains("outputs.contract.v1", config.Capabilities!.Required!);
        }
        finally
        {
            File.Delete(path);
        }
    }

  [Fact]
  public async Task LoadAsyncMergesExtendsConfig()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-extends-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var minimalSchema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
              "properties": {
                "$schema": { "type": "string" },
                "schemaVersion": { "type": "string" },
                "name": { "type": "string" },
                "commands": { "type": "object" },
                "aliases": { "type": "object" },
                "extends": { "type": "array" }
              }
            }
            """;

    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), minimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "base",
              "commands": {
                "shared": { "options": {}, "steps": [] }
              },
              "aliases": {}
            }
            """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, $$"""
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "child",
              "extends": ["./base.json"],
              "commands": {
                "child-cmd": { "options": {}, "steps": [] }
              },
              "aliases": {}
            }
            """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);

      Assert.Equal("child", config.Name);
      Assert.True(config.Commands!.ContainsKey("shared"), "Should inherit base command 'shared'");
      Assert.True(config.Commands!.ContainsKey("child-cmd"), "Should have own command 'child-cmd'");
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncMergesOutputsSettingsAndVarsAcrossExtends()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-extends-outputs-settings-vars-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {},
        "aliases": {},
        "outputs": {
          "root": "artifacts/base",
          "tests": {
            "results": "artifacts/base-tests"
          },
          "packages": "artifacts/base-packages"
        },
        "settings": {
          "quality": "strict",
          "source": "base-source"
        },
        "vars": {
          "target": "base",
          "region": "eus"
        }
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {},
        "aliases": {},
        "outputs": {
          "analysis": {
            "sarif": "artifacts/analysis/sarif"
          }
        },
        "settings": {
          "quality": "relaxed",
          "signing": true
        },
        "vars": {
          "target": "child",
          "channel": "preview"
        }
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);

      Assert.Equal("artifacts/base", config.Outputs?.Root);
      Assert.Equal("artifacts/base-tests", config.Outputs?.Tests?.Results);
      Assert.Equal("artifacts/analysis/sarif", config.Outputs?.Analysis?.Sarif);
      Assert.Equal("artifacts/base-packages", config.Outputs?.Packages);

      Assert.NotNull(config.Settings);
      Assert.Equal("relaxed", config.Settings!["quality"].GetString());
      Assert.Equal("base-source", config.Settings["source"].GetString());
      Assert.True(config.Settings["signing"].GetBoolean());

      Assert.NotNull(config.Vars);
      Assert.Equal("child", config.Vars!["target"].GetString());
      Assert.Equal("eus", config.Vars["region"].GetString());
      Assert.Equal("preview", config.Vars["channel"].GetString());
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncMergesPolicySourcesAcrossExtends()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-extends-policy-sources-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {},
        "aliases": {},
        "policySources": ["embedded:standard"]
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {},
        "aliases": {},
        "policySources": ["embedded:dotnet"]
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);

      Assert.Equal(2, config.PolicySources?.Count);
      Assert.Equal("embedded:standard", config.PolicySources?[0]);
      Assert.Equal("embedded:dotnet", config.PolicySources?[1]);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncThrowsOnCircularExtends()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-circular-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var minimalSchema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
              "properties": {
                "$schema": { "type": "string" },
                "schemaVersion": { "type": "string" },
                "name": { "type": "string" },
                "commands": { "type": "object" },
                "aliases": { "type": "object" },
                "extends": { "type": "array" }
              }
            }
            """;

    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), minimalSchema);

    var aPath = Path.Combine(dir, "a.json");
    var bPath = Path.Combine(dir, "b.json");

    await File.WriteAllTextAsync(aPath, $$"""
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "a",
              "extends": ["./b.json"],
              "commands": {},
              "aliases": {}
            }
            """);

    await File.WriteAllTextAsync(bPath, $$"""
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "b",
              "extends": ["./a.json"],
              "commands": {},
              "aliases": {}
            }
            """);

    try
    {
      var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
          RepoConfigurationLoader.LoadAsync(aPath, CancellationToken.None));

      Assert.Contains("Circular", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  // ─────────────────────────────────────────────────────────────────────────
  // REXO_OVERLAY environment variable overlay tests
  // ─────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task LoadAsyncAppliesEnvironmentOverlay()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-overlay-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var minimalSchema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
              "properties": {
                "$schema": { "type": "string" },
                "schemaVersion": { "type": "string" },
                "name": { "type": "string" },
                "description": { "type": "string" },
                "commands": { "type": "object" },
                "aliases": { "type": "object" }
              }
            }
            """;
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), minimalSchema);

    var repoPath = Path.Combine(dir, "repo.json");
    await File.WriteAllTextAsync(repoPath, """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "base-name",
              "description": "base description",
              "commands": {},
              "aliases": {}
            }
            """);

    var overlayPath = Path.Combine(dir, "overlay.json");
    await File.WriteAllTextAsync(overlayPath, """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "base-name",
              "description": "overlay description",
              "commands": {},
              "aliases": {}
            }
            """);

    var previous = Environment.GetEnvironmentVariable("REXO_OVERLAY");
    try
    {
      Environment.SetEnvironmentVariable("REXO_OVERLAY", overlayPath);
      var config = await RepoConfigurationLoader.LoadAsync(repoPath, CancellationToken.None);

      Assert.Equal("overlay description", config.Description);
    }
    finally
    {
      Environment.SetEnvironmentVariable("REXO_OVERLAY", previous);
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncIgnoresOverlayWhenFileDoesNotExist()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-overlay-missing-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var minimalSchema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
              "properties": {
                "$schema": { "type": "string" },
                "schemaVersion": { "type": "string" },
                "name": { "type": "string" },
                "commands": { "type": "object" },
                "aliases": { "type": "object" }
              }
            }
            """;
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), minimalSchema);

    var repoPath = Path.Combine(dir, "repo.json");
    await File.WriteAllTextAsync(repoPath, """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "base-repo",
              "commands": {},
              "aliases": {}
            }
            """);

    var previous = Environment.GetEnvironmentVariable("REXO_OVERLAY");
    try
    {
      Environment.SetEnvironmentVariable("REXO_OVERLAY", "/nonexistent/path/overlay.json");
      var config = await RepoConfigurationLoader.LoadAsync(repoPath, CancellationToken.None);

      Assert.Equal("base-repo", config.Name);
    }
    finally
    {
      Environment.SetEnvironmentVariable("REXO_OVERLAY", previous);
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncMergesCommandsDictionaryChildWins()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-cmdmerge-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var minimalSchema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
              "properties": {
                "$schema": { "type": "string" },
                "schemaVersion": { "type": "string" },
                "name": { "type": "string" },
                "commands": { "type": "object" },
                "aliases": { "type": "object" },
                "extends": { "type": "array" }
              }
            }
            """;
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), minimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "base",
              "commands": {
                "shared": { "description": "from base", "steps": [] },
                "base-only": { "description": "base exclusive", "steps": [] }
              },
              "aliases": {}
            }
            """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, $$"""
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "child",
              "extends": ["./base.json"],
              "commands": {
                "shared": { "description": "from child", "steps": [] },
                "child-only": { "description": "child exclusive", "steps": [] }
              },
              "aliases": {}
            }
            """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);

      Assert.Equal("from child", config.Commands!["shared"].Description);
      Assert.True(config.Commands!.ContainsKey("base-only"), "base-only should be inherited");
      Assert.True(config.Commands!.ContainsKey("child-only"), "child-only should exist");
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncEmbeddedExtendsMergesStandardCommands()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-embedded-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "extended-sample",
        "extends": ["embedded:standard"],
        "artifacts": [
          { "type": "docker", "name": "api", "settings": { "image": "ghcr.io/acme/api" } }
        ]
      }
      """);

    // Provide a local schema that does not require commands (relaxed)
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), """
      {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["$schema", "schemaVersion", "name"]
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.Equal("extended-sample", config.Name);
      // Commands from embedded:standard should be inherited
      Assert.NotNull(config.Commands);
      Assert.True(config.Commands!.ContainsKey("plan"), "Should inherit 'plan' from embedded:standard");
      Assert.True(config.Commands!.ContainsKey("release"), "Should inherit 'release' from embedded:standard");
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncEmbeddedStandardThenDotnetSuppressesDotnetBuildLayer()
  {
    // standard.build has no same-name continuation step, so the layer default means
    // dotnet.build steps are NOT merged into the resolved build command.
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-embedded-standard-dotnet-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "extended-sample",
        "extends": ["embedded:standard", "embedded:dotnet"]
      }
      """);

    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), """
      {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["$schema", "schemaVersion", "name"]
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
      var build = config.Commands!["build"];

      // dotnet-build must NOT appear — standard.build has no continuation for build
      Assert.False(
          build.Steps.Exists(s => string.Equals(s.Id, "dotnet-build", StringComparison.OrdinalIgnoreCase)),
          "dotnet-build should NOT be included: standard.build has no same-name continuation step (layer default)");

      // standard lifecycle steps must still be present
      Assert.True(
          build.Steps.Exists(s => string.Equals(s.Id, "build-artifacts", StringComparison.OrdinalIgnoreCase)),
          "build-artifacts step from standard should be present");
      Assert.True(
          build.Steps.Exists(s => string.Equals(s.Id, "tag-artifacts", StringComparison.OrdinalIgnoreCase)),
          "tag-artifacts step from standard should be present");
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncEmbeddedStandardThenDotnetIncludesDotnetTestViaStandardContinuation()
  {
    // standard.test has a same-name continuation step, so dotnet.test steps ARE inlined.
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-embedded-standard-dotnet-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "extended-sample",
        "extends": ["embedded:standard", "embedded:dotnet"]
      }
      """);

    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), """
      {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["$schema", "schemaVersion", "name"]
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
      var test = config.Commands!["test"];

      // dotnet-test must be present — standard.test has a same-name continuation step
      Assert.True(
          test.Steps.Exists(s => string.Equals(s.Id, "dotnet-test", StringComparison.OrdinalIgnoreCase)),
          "dotnet-test should be included: standard.test has a same-name continuation step");
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncEmbeddedStandardThenDotnetReleaseCallsBuildNotDotnetBuild()
  {
    // When release -> build (cross-command call), it should execute standard.build, not dotnet.build
    // This tests that different-name command calls start from the first layer, not continue current layer
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-embedded-standard-dotnet-release-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "release-test",
        "extends": ["embedded:standard", "embedded:dotnet"]
      }
      """);

    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), """
      {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["$schema", "schemaVersion", "name"]
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
      var build = config.Commands!["build"];
      var release = config.Commands!["release"];

      // verify build is standard-only (no dotnet-build)
      Assert.False(
          build.Steps.Exists(s => string.Equals(s.Id, "dotnet-build", StringComparison.OrdinalIgnoreCase)),
          "build should not include dotnet-build with layer mode");

      // verify release exists with build step
      Assert.True(
          release.Steps.Exists(s => string.Equals(s.Id, "build", StringComparison.OrdinalIgnoreCase) && s.Command == "build"),
          "release should have a build command step");

      // verify build step is a command call (not expanded in-place)
      var buildStep = release.Steps.FirstOrDefault(s => string.Equals(s.Id, "build", StringComparison.OrdinalIgnoreCase));
      Assert.NotNull(buildStep);
      Assert.Equal("build", buildStep.Command);
      Assert.Null(buildStep.Run); // should be command call, not run
      Assert.Null(buildStep.Uses); // should be command call, not builtin
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncArtifactOnlyConfigDoesNotApplyStandardTemplateImplicitly()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-implicit-standard-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "artifact-only",
        "artifacts": [
          { "type": "docker", "name": "api", "settings": { "image": "ghcr.io/acme/api" } }
        ]
      }
      """);

    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), """
      {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["$schema", "schemaVersion", "name"],
        "properties": {
          "$schema": { "type": "string" },
          "schemaVersion": { "type": "string" },
          "name": { "type": "string" },
          "artifacts": { "type": "array" }
        }
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.True(config.Commands is null || config.Commands.Count == 0);
      Assert.True(config.Aliases is null || config.Aliases.Count == 0);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncParsesBooleanOptionDefault()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-bool-default-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "sample",
        "commands": {
          "push": {
            "description": "Push",
            "options": {
              "confirm": {
                "type": "bool",
                "default": false
              }
            },
            "steps": [
              { "uses": "builtin:push-artifacts" }
            ]
          }
        },
        "aliases": {}
      }
      """);

    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), """
      {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
        "properties": {
          "$schema": { "type": "string" },
          "schemaVersion": { "type": "string" },
          "name": { "type": "string" },
          "commands": { "type": "object" },
          "aliases": { "type": "object" }
        }
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
      var confirm = config.Commands!["push"].Options["confirm"].Default;
      Assert.True(confirm.HasValue);
      Assert.Equal(System.Text.Json.JsonValueKind.False, confirm.Value.ValueKind);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncParsesCommandMergeAndWhenExists()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-merge-whenexists-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "sample",
        "commands": {
          "build": {
            "description": "Build",
            "merge": "append",
            "steps": [
              { "id": "continue", "command": "build", "whenExists": true }
            ]
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
      var build = config.Commands!["build"];

      Assert.Equal("append", build.Merge);
      Assert.Single(build.Steps);
      Assert.True(build.Steps[0].WhenExists);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncParsesRunStepContainer()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-step-container-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "sample",
        "commands": {
          "build": {
            "steps": [
              {
                "id": "container-step",
                "run": "dotnet --info",
                "container": {
                  "image": "mcr.microsoft.com/dotnet/sdk:10.0",
                  "workingDirectory": "/work",
                  "env": {
                    "DOTNET_CLI_TELEMETRY_OPTOUT": "1"
                  }
                }
              }
            ]
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
      var step = Assert.Single(config.Commands!["build"].Steps);

      Assert.NotNull(step.Container);
      Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", step.Container?.Image);
      Assert.Equal("/work", step.Container?.WorkingDirectory);
      Assert.Equal("1", step.Container?.Env?["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncRejectsContainerOnUsesStep()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-step-container-invalid-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "sample",
        "commands": {
          "release": {
            "steps": [
              {
                "id": "invalid-container-uses",
                "uses": "builtin:resolve-version",
                "container": {
                  "image": "mcr.microsoft.com/dotnet/sdk:10.0"
                }
              }
            ]
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
          RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None));

      Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncRejectsInvalidCommandMergeValue()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-invalid-merge-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var configPath = Path.Combine(dir, "rexo.json");
    await File.WriteAllTextAsync(configPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "sample",
        "commands": {
          "build": {
            "merge": "invalid-mode",
            "steps": [
              { "run": "echo hi" }
            ]
          }
        },
        "aliases": {}
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
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public void EmbeddedDotnetTemplateCommandsHaveNoMergeAnnotation()
  {
    using var document = JsonDocument.Parse(EmbeddedPolicyTemplates.ReadTemplate("dotnet"));
    var commands = document.RootElement.GetProperty("commands");

    // Dotnet policy uses automatic layer-composition (no explicit merge annotations required).
    foreach (var name in new[] { "restore", "build", "test", "analyze", "format" })
    {
      Assert.True(commands.TryGetProperty(name, out var cmd), $"Should have '{name}'");
      Assert.False(cmd.TryGetProperty("merge", out _), $"'{name}' should have no merge annotation — composition is automatic");
    }
  }

  [Fact]
  public void EmbeddedTemplateNamesIncludesDotnetAndNode()
  {
    var names = EmbeddedPolicyTemplates.TemplateNames;
    Assert.Contains("dotnet", names);
    Assert.Contains("node", names);
    Assert.DoesNotContain("dotnet-library", names);
    Assert.DoesNotContain("dotnet-api", names);
  }

  [Fact]
  public void EmbeddedNodeTemplateHasExpectedCommands()
  {
    using var document = JsonDocument.Parse(EmbeddedPolicyTemplates.ReadTemplate("node"));
    var root = document.RootElement;
    var commands = root.GetProperty("commands");

    Assert.True(commands.TryGetProperty("restore", out _), "Should have 'restore'");
    Assert.True(commands.TryGetProperty("build", out _), "Should have 'build'");
    Assert.True(commands.TryGetProperty("test", out _), "Should have 'test'");
    Assert.True(commands.TryGetProperty("analyze", out _), "Should have 'analyze'");
    Assert.True(commands.TryGetProperty("security", out _), "Should have 'security'");
    Assert.False(commands.TryGetProperty("release", out _), "Should not have 'release' — lifecycle is provided by standard");

    Assert.False(root.TryGetProperty("aliases", out _), "Should have no aliases — node contributes commands, not shortcuts");
  }

  [Fact]
  public void EmbeddedNodeTemplateCommandsHaveNoMergeAnnotation()
  {
    using var document = JsonDocument.Parse(EmbeddedPolicyTemplates.ReadTemplate("node"));
    var commands = document.RootElement.GetProperty("commands");

    foreach (var name in new[] { "restore", "build", "test", "analyze", "security" })
    {
      Assert.True(commands.TryGetProperty(name, out var cmd), $"Should have '{name}'");
      Assert.False(cmd.TryGetProperty("merge", out _), $"'{name}' should have no merge annotation — composition is automatic");
    }
  }

  [Fact]
  public void EmbeddedDotnetTemplateHasExpectedCommands()
  {
    using var document = JsonDocument.Parse(EmbeddedPolicyTemplates.ReadTemplate("dotnet"));
    var root = document.RootElement;
    var commands = root.GetProperty("commands");

    Assert.True(commands.TryGetProperty("restore", out _), "Should have 'restore'");
    Assert.True(commands.TryGetProperty("build", out _), "Should have 'build'");
    Assert.True(commands.TryGetProperty("test", out _), "Should have 'test'");
    Assert.True(commands.TryGetProperty("analyze", out _), "Should have 'analyze'");
    Assert.True(commands.TryGetProperty("format", out _), "Should have 'format' — auto-formats source files");
    Assert.False(commands.TryGetProperty("pack", out _), "Should not have 'pack' — artifact lifecycle belongs to standard");
    Assert.False(commands.TryGetProperty("stage", out _), "Should not have 'stage' — artifact lifecycle belongs to standard");
    Assert.False(commands.TryGetProperty("ci", out _), "Should not have 'ci' — lifecycle is provided by standard");
    Assert.False(commands.TryGetProperty("release", out _), "Should not have 'release' — lifecycle is provided by standard");

    Assert.False(root.TryGetProperty("aliases", out _), "Should have no aliases — dotnet contributes commands, not shortcuts");
  }

    [Fact]
    public void EmbeddedDotnetTemplateTestCommandSupportsOptionalVarsContract()
    {
        using var document = JsonDocument.Parse(EmbeddedPolicyTemplates.ReadTemplate("dotnet"));
        var commands = document.RootElement.GetProperty("commands");
        var testCommand = commands.GetProperty("test");
        var steps = testCommand.GetProperty("steps");

        Assert.Equal(2, steps.GetArrayLength());

        var noCoverageStep = steps[0];
        Assert.Equal("dotnet-test-no-coverage", noCoverageStep.GetProperty("id").GetString());
        Assert.Equal("{{vars.dotnet.test.coverage.mode == 'none'}}", noCoverageStep.GetProperty("when").GetString());
        Assert.Contains("vars.dotnet.test.runsettings", noCoverageStep.GetProperty("run").GetString(), StringComparison.Ordinal);
        Assert.Contains("vars.dotnet.test.extraArgs", noCoverageStep.GetProperty("run").GetString(), StringComparison.Ordinal);

        var coverageStep = steps[1];
        Assert.Equal("dotnet-test", coverageStep.GetProperty("id").GetString());
        Assert.Equal("{{vars.dotnet.test.coverage.mode != 'none'}}", coverageStep.GetProperty("when").GetString());
        Assert.Contains("--collect \"XPlat Code Coverage\"", coverageStep.GetProperty("run").GetString(), StringComparison.Ordinal);
        Assert.Contains("vars.dotnet.test.runsettings", coverageStep.GetProperty("run").GetString(), StringComparison.Ordinal);
        Assert.Contains("vars.dotnet.test.extraArgs", coverageStep.GetProperty("run").GetString(), StringComparison.Ordinal);

        var buildRun = commands.GetProperty("build").GetProperty("steps")[0].GetProperty("run").GetString();
        Assert.Contains("vars.dotnet.build.extraArgs", buildRun, StringComparison.Ordinal);

        var restoreRun = commands.GetProperty("restore").GetProperty("steps")[0].GetProperty("run").GetString();
        Assert.Contains("vars.dotnet.restore.extraArgs", restoreRun, StringComparison.Ordinal);

        var formatRun = commands.GetProperty("format").GetProperty("steps")[0].GetProperty("run").GetString();
        Assert.Contains("vars.dotnet.format.extraArgs", formatRun, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Layered command composition via extends: append / prepend / wrap
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
  public async Task ExtendsWithAppendMergeAppendsStepsAfterBase()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-append-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), MinimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": { "steps": [ { "id": "base-step", "run": "echo base" } ] }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": { "merge": "append", "steps": [ { "id": "child-step", "run": "echo child" } ] }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      Assert.Equal(2, steps.Count);
      Assert.Equal("base-step", steps[0].Id);
      Assert.Equal("child-step", steps[1].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWithPrependMergeInsertsStepsBeforeBase()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-prepend-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), MinimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": { "steps": [ { "id": "base-step", "run": "echo base" } ] }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": { "merge": "prepend", "steps": [ { "id": "child-step", "run": "echo child" } ] }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      Assert.Equal(2, steps.Count);
      Assert.Equal("child-step", steps[0].Id);
      Assert.Equal("base-step", steps[1].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWithWrapMergeExpandsContinuationStepWithBaseSteps()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-wrap-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), MinimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": { "steps": [ { "id": "content-step", "run": "echo content" } ] }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": {
            "merge": "wrap",
            "steps": [
              { "id": "before", "run": "echo before" },
              { "id": "continuation", "command": "build" },
              { "id": "after", "run": "echo after" }
            ]
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      Assert.Equal(3, steps.Count);
      Assert.Equal("before", steps[0].Id);
      Assert.Equal("content-step", steps[1].Id);   // base step injected at continuation point
      Assert.Equal("after", steps[2].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWrapWithNoContinuationStepFallsBackToAppend()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-wrap-no-marker-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), MinimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": { "steps": [ { "id": "base-step", "run": "echo base" } ] }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": {
            "merge": "wrap",
            "steps": [
              { "id": "wrap-step", "run": "echo wrap" }
            ]
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      // Fallback: no continuation marker → treat as append (wrap-step first, then base-step appended)
      Assert.Equal(2, steps.Count);
      Assert.Equal("wrap-step", steps[0].Id);
      Assert.Equal("base-step", steps[1].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWithLayerModeBaseWins()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-layer-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), MinimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": { "steps": [ { "id": "base-step", "run": "echo base" } ] }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": { "merge": "layer", "steps": [ { "id": "child-step", "run": "echo child" } ] }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      // layer: base wins, child steps are not included (lower layer does not auto-continue)
      Assert.Single(steps);
      Assert.Equal("base-step", steps[0].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWithReplaceModeDefaultChildWinsAsUsual()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-replace-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "rexo.schema.json"), MinimalSchema);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": { "steps": [ { "id": "base-step", "run": "echo base" } ] }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": { "steps": [ { "id": "child-step", "run": "echo child" } ] }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      // No merge field → replace (default): child wins entirely
      Assert.Single(steps);
      Assert.Equal("child-step", steps[0].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWithStepOpsCanCombineRemoveReplacePrependAppendInDeterministicOrder()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-stepops-combined-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": {
            "steps": [
              { "id": "restore", "run": "echo restore" },
              { "id": "build", "run": "echo build" },
              { "id": "test", "run": "echo test" }
            ]
          }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": {
            "steps": [],
            "stepOps": {
              "remove": ["test"],
              "replace": [
                {
                  "id": "build",
                  "step": { "run": "echo build --no-restore" }
                }
              ],
              "prepend": [
                { "id": "setup", "run": "echo setup" }
              ],
              "append": [
                { "id": "notify", "run": "echo notify" }
              ]
            }
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      Assert.Equal(4, steps.Count);
      Assert.Equal("setup", steps[0].Id);
      Assert.Equal("restore", steps[1].Id);
      Assert.Equal("build", steps[2].Id);
      Assert.Equal("echo build --no-restore", steps[2].Run);
      Assert.Equal("notify", steps[3].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWithStepOpsAndNoMatchDoesNotFail()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-stepops-no-match-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": {
            "steps": [
              { "id": "restore", "run": "echo restore" }
            ]
          }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": {
            "steps": [],
            "stepOps": {
              "remove": ["missing"],
              "replace": [
                { "id": "missing", "step": { "id": "x", "run": "echo x" } }
              ]
            }
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      Assert.Single(steps);
      Assert.Equal("restore", steps[0].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task ExtendsWithMergeEnvelopeStepsCanCombineOperationsInDeterministicOrder()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-merge-envelope-ops-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": {
            "steps": [
              { "id": "restore", "run": "echo restore" },
              { "id": "build", "run": "echo build" },
              { "id": "test", "run": "echo test" }
            ]
          }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": {
            "steps": [],
            "merge": {
              "mode": "append",
              "steps": {
                "remove": ["test"],
                "replace": [
                  {
                    "id": "build",
                    "step": { "run": "echo build --no-restore" }
                  }
                ],
                "prepend": [
                  { "id": "setup", "run": "echo setup" }
                ],
                "append": [
                  { "id": "notify", "run": "echo notify" }
                ]
              }
            }
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      Assert.Equal(4, steps.Count);
      Assert.Equal("setup", steps[0].Id);
      Assert.Equal("restore", steps[1].Id);
      Assert.Equal("build", steps[2].Id);
      Assert.Equal("echo build --no-restore", steps[2].Run);
      Assert.Equal("notify", steps[3].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task MergeEnvelopeStepsTakePrecedenceOverLegacyStepOps()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"rexo-merge-envelope-precedence-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);

    var basePath = Path.Combine(dir, "base.json");
    await File.WriteAllTextAsync(basePath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "base",
        "commands": {
          "build": {
            "steps": [
              { "id": "restore", "run": "echo restore" }
            ]
          }
        },
        "aliases": {}
      }
      """);

    var childPath = Path.Combine(dir, "child.json");
    await File.WriteAllTextAsync(childPath, """
      {
        "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
        "schemaVersion": "1.0",
        "name": "child",
        "extends": ["./base.json"],
        "commands": {
          "build": {
            "steps": [],
            "merge": {
              "steps": {
                "append": [
                  { "id": "notify", "run": "echo notify" }
                ]
              }
            },
            "stepOps": {
              "remove": ["restore"]
            }
          }
        },
        "aliases": {}
      }
      """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(childPath, CancellationToken.None);
      var steps = config.Commands!["build"].Steps;

      Assert.Equal(2, steps.Count);
      Assert.Equal("restore", steps[0].Id);
      Assert.Equal("notify", steps[1].Id);
    }
    finally
    {
      Directory.Delete(dir, true);
    }
  }

  private const string MinimalSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "required": ["$schema", "schemaVersion", "name"],
      "properties": {
        "$schema": { "type": "string" },
        "schemaVersion": { "type": "string" },
        "name": { "type": "string" },
        "commands": { "type": "object" },
        "aliases": { "type": "object" },
        "extends": { "type": "array" }
      }
    }
    """;
}


