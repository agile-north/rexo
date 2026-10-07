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
        var dir = Path.Join(Path.GetTempPath(), $"rexo-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
    var schemaPath = Path.Join(dir, "rexo.schema.json");
    var configPath = Path.Join(dir, "rexo.yml");

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

    var dir = Path.Join(Path.GetTempPath(), $"rexo-yaml-hidden-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var configPath = Path.Join(dir, "rexo.yml");

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
  public async Task LoadAsyncAcceptsModelineInsteadOfSchemaKeyAndTypesScalars()
  {
    var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");
    Environment.SetEnvironmentVariable("REXO_OVERLAY", null);

    var dir = Path.Join(Path.GetTempPath(), $"rexo-yaml-modeline-{Guid.NewGuid():N}");
    var dotRexo = Path.Join(dir, ".rexo");
    Directory.CreateDirectory(dotRexo);
    var configPath = Path.Join(dotRexo, "rexo.yaml");

    await File.WriteAllTextAsync(
        configPath,
        """
        # yaml-language-server: $schema=https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
        schemaVersion: 1.0
        name: yaml-modeline-sample
        commands:
          build:
            description: Build
            options: {}
            steps:
              - run: dotnet build
                continueOnError: false
        aliases: {}
        """);

    try
    {
      var config = await RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);

      Assert.Equal("yaml-modeline-sample", config.Name);
      Assert.Equal("1.0", config.SchemaVersion);
      Assert.Equal(
          "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
          config.Schema);
      Assert.True(config.Commands!.ContainsKey("build"));
    }
    finally
    {
      Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Fact]
  public async Task LoadAsyncRequiresSchemaWhenYamlHasNoKeyOrModeline()
  {
    var dir = Path.Join(Path.GetTempPath(), $"rexo-yaml-noschema-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var configPath = Path.Join(dir, "rexo.yaml");
    await File.WriteAllTextAsync(configPath, "schemaVersion: \"1.0\"\nname: x\n");

    try
    {
      var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
          RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None));
      Assert.Contains("$schema", ex.Message, StringComparison.Ordinal);
    }
    finally
    {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Fact]
    public async Task LoadPolicyAsyncParsesYaml()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-policy-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var policyPath = Path.Join(dir, "policy.yml");

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
    public async Task LoadPolicyAsyncAllowsEmptyPolicy()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-policy-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var policyPath = Path.Join(dir, "policy.yml");

        await File.WriteAllTextAsync(
            policyPath,
            """
        $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json
        schemaVersion: "1.0"
        name: empty-policy
        description: Empty policy with no commands or aliases
        """);

        try
        {
            var policy = await RepoConfigurationLoader.LoadPolicyAsync(policyPath, CancellationToken.None);

            Assert.NotNull(policy);
            Assert.Null(policy.Commands);
            Assert.Null(policy.Aliases);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
      public async Task LoadPolicyAsyncThrowsWhenRequiredCapabilityIsUnsupported()
      {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-policy-capability-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
          var schemaPath = Path.Join(dir, "policy.schema.json");
        var policyPath = Path.Join(dir, "policy.yml");

          await File.WriteAllTextAsync(
              schemaPath,
              """
              {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "type": "object",
                "required": ["$schema", "schemaVersion", "name"],
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

