namespace Rexo.Configuration.Tests;

using Rexo.Configuration;

[Collection("EnvironmentVariableSensitive")]
public sealed class RepoConfigurationLoaderYamlTests
{
    [Fact]
    public async Task LoadAsyncParsesYamlConfig()
    {
      var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
      Environment.SetEnvironmentVariable("REXO_OVERLAY", null);
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
    var schemaPath = Path.Combine(dir, "rexo.schema.json");
    var configPath = Path.Combine(dir, "rexo.yml");

        await File.WriteAllTextAsync(
            schemaPath,
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

        await File.WriteAllTextAsync(
            configPath,
            """
            $schema: rexo.schema.json
            schemaVersion: "1.0"
            name: yaml-sample
            commands:
              build:
                description: Build
                options: {}
                steps: []
            aliases: {}
            """);

        try
        {
            var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

            Assert.Equal("yaml-sample", config.Name);
      Assert.True(config.Commands!.ContainsKey("build"));
    }
        finally
        {
          Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

  [Fact]
  public async Task LoadAsyncParsesYamlHiddenCommand()
  {
    var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
    Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

    var dir = Path.Combine(Path.GetTempPath(), $"rexo-yaml-hidden-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var configPath = Path.Combine(dir, "rexo.yml");

    await File.WriteAllTextAsync(
        configPath,
        """
        $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
        schemaVersion: "1.0"
        name: yaml-hidden-sample
        commands:
          hidden-helper:
            hidden: true
            description: Hidden helper
            options: {}
            steps: []
        aliases: {}
        """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.Equal("yaml-hidden-sample", config.Name);
      Assert.True(config.Commands!.ContainsKey("hidden-helper"));
      Assert.True(config.Commands!["hidden-helper"].Hidden);
    }
    finally
    {
      Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Fact]
    public async Task LoadPolicyAsyncParsesYaml()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-policy-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var policyPath = Path.Combine(dir, "policy.yml");

        await File.WriteAllTextAsync(
            policyPath,
            """
            $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json
            schemaVersion: "1.0"
            name: yaml-policy
            commands:
              secure-check:
                description: Run secure check
                options: {}
                steps:
                  - run: echo secure
            aliases:
              sc: secure-check
            """);

        try
        {
            var policy = await RepoConfigurationLoader.LoadPolicyAsync(policyPath, CancellationToken.None);

            Assert.NotNull(policy);
            Assert.NotNull(policy!.Commands);
            Assert.True(policy.Commands!.ContainsKey("secure-check"));
            Assert.NotNull(policy.Aliases);
            Assert.Equal("secure-check", policy.Aliases!["sc"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

      [Fact]
      public async Task LoadPolicyAsyncThrowsWhenRequiredCapabilityIsUnsupported()
      {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-policy-capability-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
          var schemaPath = Path.Combine(dir, "policy.schema.json");
        var policyPath = Path.Combine(dir, "policy.yml");

          await File.WriteAllTextAsync(
              schemaPath,
              """
              {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "type": "object",
                "required": ["$schema", "schemaVersion", "name", "commands", "aliases"],
                "additionalProperties": false,
                "properties": {
                  "$schema": { "type": "string" },
                  "schemaVersion": { "type": "string" },
                  "name": { "type": "string" },
                  "commands": { "type": "object" },
                  "aliases": { "type": "object" },
                  "capabilities": {
                    "type": "object",
                    "properties": {
                      "required": {
                        "type": "array",
                        "items": { "type": "string" }
                      }
                    }
                  }
                }
              }
              """);

        await File.WriteAllTextAsync(
          policyPath,
          """
          $schema: policy.schema.json
          schemaVersion: "1.0"
          name: yaml-policy
          capabilities:
            required:
              - capability.not.supported
          commands:
            secure-check:
              description: Run secure check
              options: {}
              steps:
                - run: echo secure
          aliases:
            sc: secure-check
          """);

        try
        {
          var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepoConfigurationLoader.LoadPolicyAsync(policyPath, CancellationToken.None));

          Assert.Contains("CAP-001", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
          if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
      }
}

