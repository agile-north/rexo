namespace Rexo.Integration.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Rexo.Cli;
using Rexo.Configuration;
using Spectre.Console;

[Collection("IntegrationSequential")]
public sealed class CliSmokeTests
{
    [Fact]
    public async Task GlobalColorFlagsRespectNoColorAndCanOverrideIt()
    {
        var originalNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
        var originalConsole = AnsiConsole.Console;

        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Assert.Equal(0, await Program.ExecuteAsync(["help"], CancellationToken.None));
            Assert.Equal(ColorSystem.NoColors, AnsiConsole.Profile.Capabilities.ColorSystem);

            Assert.Equal(0, await Program.ExecuteAsync(["--color", "help"], CancellationToken.None));
            Assert.NotEqual(ColorSystem.NoColors, AnsiConsole.Profile.Capabilities.ColorSystem);

            Assert.Equal(0, await Program.ExecuteAsync(["--no-color", "help"], CancellationToken.None));
            Assert.Equal(ColorSystem.NoColors, AnsiConsole.Profile.Capabilities.ColorSystem);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task VersionCommandReturnsSuccess()
    {
        var exitCode = await Program.ExecuteAsync(["version"], CancellationToken.None);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task LifecycleWorkflowUsesRexoAndIsolatesPublishing()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "solution.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var workflowPath = Path.Join(directory!.FullName, ".github", "workflows", "release.yml");
        var workflowText = await File.ReadAllTextAsync(workflowPath);
        using var workflow = JsonDocument.Parse(YamlJsonConverter.ToJson(workflowText, workflowPath));

        var steps = workflow.RootElement
            .GetProperty("jobs")
            .GetProperty("verify")
            .GetProperty("steps")
            .EnumerateArray()
            .ToArray();
        var names = steps
            .Select(step => step.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains("Bootstrap source-built Rexo", names);
        Assert.False(File.Exists(Path.Join(directory.FullName, ".github", "workflows", "build.yml")));
        Assert.Equal("read", workflow.RootElement.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Single(workflow.RootElement.GetProperty("permissions").EnumerateObject());
        var verify = workflow.RootElement.GetProperty("jobs").GetProperty("verify");
        Assert.DoesNotContain("secrets.", verify.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("--push", verify.GetRawText(), StringComparison.Ordinal);
        Assert.False(workflow.RootElement.GetProperty("on").GetProperty("workflow_dispatch").GetProperty("inputs").GetProperty("publish").GetProperty("default").GetBoolean());
        Assert.True(workflow.RootElement.GetProperty("on").TryGetProperty("pull_request", out _));
        var publish = workflow.RootElement.GetProperty("jobs").GetProperty("publish");
        Assert.Equal("verify", publish.GetProperty("needs").GetString());
        var gate = publish.GetProperty("if").GetString();
        Assert.Contains("github.event_name != 'pull_request'", gate, StringComparison.Ordinal);
        Assert.Contains("inputs.publish", gate, StringComparison.Ordinal);
        Assert.Contains("github.ref_type == 'branch'", gate, StringComparison.Ordinal);
        Assert.Contains("github.ref_name == 'main'", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", publish.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("release --push", publish.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("ci publish --confirm", publish.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("actions/download-artifact@v4", publish.GetRawText(), StringComparison.Ordinal);
        var acceptance = Assert.Single(steps, step => step.GetProperty("name").GetString() == "Verify release without publication");
        Assert.Contains("--json-file artifacts/selfhost/release.json release", acceptance.GetProperty("run").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(directory.FullName, "scripts", "Test-SelfHost.ps1")));
        Assert.DoesNotContain(".ps1", workflowText, StringComparison.Ordinal);
        Assert.True(Array.IndexOf(names, "Check repository readiness") < Array.IndexOf(names, "Verify release without publication"));
        Assert.True(Array.IndexOf(names, "Verify release without publication") < Array.IndexOf(names, "Validate and seal verified outputs"));
        var evidence = Assert.Single(steps, step => step.GetProperty("name").GetString() == "Upload release rehearsal evidence");
        Assert.Contains("artifacts/selfhost/**", evidence.GetProperty("with").GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Contains("artifacts/packages/*.nupkg", evidence.GetProperty("with").GetProperty("path").GetString(), StringComparison.Ordinal);
        var coverage = Assert.Single(steps, step => step.GetProperty("name").GetString() == "Generate coverage summary");
        Assert.Contains("--non-interactive ci coverage", coverage.GetProperty("run").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("reportgenerator", coverage.GetProperty("run").GetString(), StringComparison.Ordinal);
        Assert.All(steps, step =>
        {
            if (step.TryGetProperty("run", out var run))
            {
                Assert.DoesNotContain("dotnet run --project src/Cli/Cli.csproj", run.GetString() ?? string.Empty, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task RunManifestConfigHashIsStableAcrossFormatsAndRedactsSensitiveValues()
    {
        var yamlDir = Path.Join(Path.GetTempPath(), $"rexo-hash-yaml-{Guid.NewGuid():N}");
        var jsonDir = Path.Join(Path.GetTempPath(), $"rexo-hash-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(yamlDir, ".rexo"));
        Directory.CreateDirectory(Path.Join(jsonDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(yamlDir, ".rexo", "rexo.yaml"),
                """
                $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
                schemaVersion: "1.0"
                name: hash-sample
                versioning:
                  provider: fixed
                  fallback: 1.2.3
                vars:
                  API_KEY: yaml-secret-value
                """);
            await File.WriteAllTextAsync(
                Path.Join(jsonDir, ".rexo", "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "hash-sample",
                  "versioning": {
                    "provider": "fixed",
                    "fallback": "1.2.3"
                  },
                  "vars": {
                    "API_KEY": "different-json-secret"
                  }
                }
                """);

            Environment.CurrentDirectory = yamlDir;
            var yamlHash = await ReadVersionManifestConfigHashAsync(yamlDir);
            Environment.CurrentDirectory = jsonDir;
            var jsonHash = await ReadVersionManifestConfigHashAsync(jsonDir);

            Assert.Equal(yamlHash, jsonHash);
            Assert.Matches("^[a-f0-9]{64}$", yamlHash);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(yamlDir)) Directory.Delete(yamlDir, true);
            if (Directory.Exists(jsonDir)) Directory.Delete(jsonDir, true);
        }
    }

    [Fact]
    public async Task ConfigExplainReturnsEffectiveValueAndRedactsNestedCredentials()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-config-explain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(tempDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;
        var originalOverlay = Environment.GetEnvironmentVariable("REXO_OVERLAY");

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, ".rexo", "base.yaml"),
                """
                $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
                schemaVersion: "1.0"
                name: explain-base
                vars:
                  API_KEY: never-show-this-key
                  displayName: base-value
                """);
            await File.WriteAllTextAsync(
                Path.Join(tempDir, ".rexo", "rexo.yaml"),
                """
                $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
                schemaVersion: "1.0"
                name: explain-sample
                extends:
                  - base.yaml
                versioning:
                  provider: fixed
                  fallback: 2.3.4
                vars:
                  displayName: safe-value
                """);
            var overlayPath = Path.Join(tempDir, ".rexo", "overlay.yaml");
            await File.WriteAllTextAsync(
                overlayPath,
                """
                vars:
                  displayName: overlay-value
                  overlayOnly: overlay-value
                """);
            Environment.SetEnvironmentVariable("REXO_OVERLAY", overlayPath);
            Environment.CurrentDirectory = tempDir;

            var jsonPath = Path.Join(tempDir, "explain.json");
            Assert.Equal(0, await Program.ExecuteAsync(
                ["--set", "vars.displayName=cli-value", "--json-file", jsonPath, "config", "explain", "vars"],
                CancellationToken.None));

            var output = await File.ReadAllTextAsync(jsonPath);
            Assert.DoesNotContain("never-show-this-key", output, StringComparison.Ordinal);
            using var result = JsonDocument.Parse(output);
            var outputs = result.RootElement.GetProperty("Outputs");
            var value = outputs.GetProperty("value");
            Assert.Equal("***", value.GetProperty("API_KEY").GetString());
            Assert.Equal("cli-value", value.GetProperty("displayName").GetString());
            Assert.True(outputs.GetProperty("provenanceAvailable").GetBoolean());
            Assert.True(outputs.GetProperty("repositoryFileAttributionAvailable").GetBoolean());
            Assert.False(outputs.GetProperty("policySourceAttributionAvailable").GetBoolean());
            var sourceLayers = outputs.GetProperty("sourceLayers").EnumerateArray()
                .Select(layer => layer.GetString())
                .ToArray();
            Assert.Contains(sourceLayers, layer => layer?.Contains("base.yaml", StringComparison.Ordinal) == true);
            Assert.Contains(sourceLayers, layer => layer?.Contains(Path.Join(".rexo", "rexo.yaml"), StringComparison.Ordinal) == true);
            Assert.Contains(sourceLayers, layer => layer?.Contains("overlay.yaml", StringComparison.Ordinal) == true);
            Assert.Contains("CLI --set override", sourceLayers);

            var resolvedJsonPath = Path.Join(tempDir, "resolved-with-provenance.json");
            Assert.Equal(0, await Program.ExecuteAsync(
                ["--set", "vars.displayName=cli-value", "--json-file", resolvedJsonPath, "config", "resolved", "--provenance"],
                CancellationToken.None));

            var resolvedOutput = await File.ReadAllTextAsync(resolvedJsonPath);
            Assert.DoesNotContain("never-show-this-key", resolvedOutput, StringComparison.Ordinal);
            using var resolvedResult = JsonDocument.Parse(resolvedOutput);
            var resolvedOutputs = resolvedResult.RootElement.GetProperty("Outputs");
            Assert.Equal("***", resolvedOutputs.GetProperty("effectiveConfig").GetProperty("Vars").GetProperty("API_KEY").GetString());
            Assert.True(resolvedOutputs.GetProperty("repositoryFileAttributionAvailable").GetBoolean());
            var resolvedSourceLayers = resolvedOutputs.GetProperty("sourceLayers").EnumerateArray()
                .Select(layer => layer.GetString())
                .ToArray();
            Assert.Contains("CLI --set override: vars.displayName", resolvedSourceLayers);
            Assert.DoesNotContain(resolvedSourceLayers, layer => layer?.Contains("cli-value", StringComparison.Ordinal) == true);
            var repositoryFiles = resolvedOutputs.GetProperty("repositoryFiles").EnumerateArray()
                .Select(file => file.GetString())
                .ToArray();
            Assert.Contains(repositoryFiles, file => file?.Contains("base.yaml", StringComparison.Ordinal) == true);
            Assert.Contains(repositoryFiles, file => file?.Contains(Path.Join(".rexo", "rexo.yaml"), StringComparison.Ordinal) == true);
            Assert.Contains(repositoryFiles, file => file?.Contains("overlay.yaml", StringComparison.Ordinal) == true);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("REXO_OVERLAY", originalOverlay);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CheckWritesStructuredArtifactSourceFinding()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-check-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(tempDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, ".rexo", "rexo.yaml"),
                """
                $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
                schemaVersion: "1.0"
                name: check-sample
                versioning:
                  provider: fixed
                  fallback: 1.0.0
                artifacts:
                  - type: nuget
                    name: sample
                    settings:
                      project: missing.csproj
                """);
            Environment.CurrentDirectory = tempDir;

            var jsonPath = Path.Join(tempDir, "check.json");
            Assert.Equal(9, await Program.ExecuteAsync(
                ["--json-file", jsonPath, "check"],
                CancellationToken.None));

            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            var findings = result.RootElement.GetProperty("Outputs").GetProperty("findings").EnumerateArray();
            Assert.Contains(findings, finding =>
                finding.GetProperty("code").GetString() == "artifact.source.missing" &&
                finding.GetProperty("message").GetString()!.Contains("missing.csproj", StringComparison.Ordinal));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CheckReportsUnknownArtifactProviderAsStructuredFinding()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-check-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(tempDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, ".rexo", "rexo.yaml"),
                """
                $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
                schemaVersion: "1.0"
                name: check-provider-sample
                versioning:
                  provider: fixed
                  fallback: 1.0.0
                artifacts:
                  - type: custom-provider
                    name: sample
                """);
            Environment.CurrentDirectory = tempDir;

            var jsonPath = Path.Join(tempDir, "check.json");
            Assert.Equal(9, await Program.ExecuteAsync(
                ["--json-file", jsonPath, "check"],
                CancellationToken.None));

            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            var findings = result.RootElement.GetProperty("Outputs").GetProperty("findings").EnumerateArray();
            Assert.Contains(findings, finding =>
                finding.GetProperty("code").GetString() == "artifact.provider.unknown" &&
                finding.GetProperty("severity").GetString() == "error");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UnknownArtifactProviderReturnsActionableCliErrorOutsideCheck()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-unknown-version-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(tempDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, ".rexo", "rexo.yaml"),
                """
                $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
                schemaVersion: "1.0"
                name: unknown-artifact-provider
                artifacts:
                  - type: custom-provider
                    name: sample
                """);
            Environment.CurrentDirectory = tempDir;

            Assert.Equal(9, await Program.ExecuteAsync(["version"], CancellationToken.None));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromoteCopiesVerifiedArtifactWithoutRebuildingAndIsIdempotent(bool useAbsolutePaths)
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-promote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(tempDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            var configPath = Path.Join(tempDir, ".rexo", "rexo.yaml");
            const string configYaml =
                """
                $schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
                schemaVersion: "1.0"
                name: promotion-sample
                versioning:
                  provider: fixed
                  fallback: 1.0.0
                environments:
                  staging:
                    path: deployments/staging
                """;
            await File.WriteAllTextAsync(configPath, configYaml);

            const string artifactContent = "built package bytes";
            var sourceArtifact = Path.Join(tempDir, "sample.nupkg");
            await File.WriteAllTextAsync(sourceArtifact, artifactContent);
            var contentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(artifactContent))).ToLowerInvariant();
            var sourceManifestPath = Path.Join(tempDir, "build-manifest.json");
            var originalManifestJson = JsonSerializer.Serialize(new Rexo.Core.Models.RunManifest
                {
                    CommitSha = "0123456789abcdef",
                    ConfigHash = new string('a', 64),
                    PolicyLockHash = new string('b', 64),
                    Artifacts =
                    [
                        new Rexo.Core.Models.ArtifactManifestEntry("nuget", "sample", true, false, ["1.0.0"])
                        {
                            Location = useAbsolutePaths ? sourceArtifact : Path.GetFileName(sourceArtifact),
                            ContentSha256 = contentHash,
                        },
                    ],
                });
            await File.WriteAllTextAsync(sourceManifestPath, originalManifestJson);
            Environment.CurrentDirectory = tempDir;
            var manifestArgument = useAbsolutePaths ? sourceManifestPath : Path.GetFileName(sourceManifestPath);

            var outputPath = Path.Join(tempDir, "promotion.json");
            Assert.Equal(0, await Program.ExecuteAsync(
                ["--dry-run", "--json-file", outputPath, "promote", manifestArgument, "staging"],
                CancellationToken.None));
            Assert.False(Directory.Exists(Path.Join(tempDir, "deployments")));

            Assert.Equal(0, await Program.ExecuteAsync(
                ["--json-file", outputPath, "promote", manifestArgument, "staging"],
                CancellationToken.None));

            var destination = Path.Join(tempDir, "deployments", "staging", "objects", contentHash, "sample.nupkg");
            Assert.Equal(artifactContent, await File.ReadAllTextAsync(destination));
            var recordPath = Directory.GetFiles(Path.Join(tempDir, "deployments", "staging", "promotions"), "*.json").Single();
            var record = await File.ReadAllTextAsync(recordPath);
            Assert.Contains("0123456789abcdef", record, StringComparison.Ordinal);
            Assert.Contains(new string('b', 64), record, StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                configPath,
                configYaml.Replace("path: deployments/staging", "path: ../outside", StringComparison.Ordinal));
            Assert.Equal(9, await Program.ExecuteAsync(
                ["promote", manifestArgument, "staging"],
                CancellationToken.None));
            Assert.False(Directory.Exists(Path.Join(tempDir, "outside")));
            await File.WriteAllTextAsync(configPath, configYaml);

            Assert.Equal(0, await Program.ExecuteAsync(
                ["promote", manifestArgument, "staging"],
                CancellationToken.None));
            Assert.Single(Directory.GetFiles(Path.Join(tempDir, "deployments", "staging", "promotions"), "*.json"));

            var alteredManifest = JsonSerializer.Deserialize<Rexo.Core.Models.RunManifest>(originalManifestJson)! with
            {
                CommitSha = "fedcba9876543210",
            };
            await File.WriteAllTextAsync(sourceManifestPath, JsonSerializer.Serialize(alteredManifest));
            Assert.Equal(9, await Program.ExecuteAsync(
                ["promote", manifestArgument, "staging"],
                CancellationToken.None));
            Assert.Single(Directory.GetFiles(Path.Join(tempDir, "deployments", "staging", "promotions"), "*.json"));

            await File.WriteAllTextAsync(sourceManifestPath, originalManifestJson);
            await File.WriteAllTextAsync(sourceArtifact, "modified package bytes");
            Assert.Equal(9, await Program.ExecuteAsync(
                ["--dry-run", "promote", manifestArgument, "staging"],
                CancellationToken.None));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GraphSubcommandUsesEffectiveConfiguredCommand()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-graph-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(tempDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, ".rexo", "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "graph-sample",
                  "versioning": { "provider": "fixed", "fallback": "1.0.0" },
                  "commands": {
                    "release": {
                      "description": "Sample release",
                      "options": {},
                      "steps": [
                        { "id": "verify", "uses": "command:verify", "when": "{{options.push}}" }
                      ]
                    }
                  }
                }
                """);
            Environment.CurrentDirectory = tempDir;
            var resultPath = Path.Join(tempDir, "graph.json");

            var exitCode = await Program.ExecuteAsync(
                ["--json-file", resultPath, "--json", "graph", "release", "--format", "mermaid"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            var message = result.RootElement.GetProperty("Message").GetString() ?? string.Empty;
            Assert.Contains("flowchart TD", message, StringComparison.Ordinal);
            Assert.Contains("steps/verify: builtin command:verify", message, StringComparison.Ordinal);
            Assert.Contains("when {{options.push}}", message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PolicyUpdateAndRestoreCommandsEnforceTheLockfile()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-policy-lock-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(tempDir, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, ".rexo", "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "policy-lock-sample",
                  "policySources": ["team.policy.json"]
                }
                """);
            var policyPath = Path.Join(tempDir, "team.policy.json");
            const string policyContent =
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json",
                  "schemaVersion": "1.0",
                  "name": "team-policy",
                  "commands": {},
                  "aliases": {}
                }
                """;
            await File.WriteAllTextAsync(policyPath, policyContent);
            Environment.CurrentDirectory = tempDir;

            Assert.Equal(9, await Program.ExecuteAsync(["restore"], CancellationToken.None));
            Assert.Equal(0, await Program.ExecuteAsync(["--dry-run", "update"], CancellationToken.None));
            Assert.False(File.Exists(Path.Join(tempDir, ".rexo", "rexo.lock.yaml")));
            Assert.Equal(0, await Program.ExecuteAsync(["update"], CancellationToken.None));
            Assert.True(File.Exists(Path.Join(tempDir, ".rexo", "rexo.lock.yaml")));
            Assert.Equal(0, await Program.ExecuteAsync(["restore"], CancellationToken.None));

            await File.WriteAllTextAsync(policyPath, policyContent.Replace("team-policy", "changed-policy", StringComparison.Ordinal));
            Assert.Equal(9, await Program.ExecuteAsync(["restore"], CancellationToken.None));
            Assert.Equal(0, await Program.ExecuteAsync(["update"], CancellationToken.None));
            Assert.Equal(0, await Program.ExecuteAsync(["restore"], CancellationToken.None));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static async Task<string> ReadVersionManifestConfigHashAsync(string workingDirectory)
    {
        var resultPath = Path.Join(workingDirectory, "version.json");
        var exitCode = await Program.ExecuteAsync(
            ["--json-file", resultPath, "--json", "version"],
            CancellationToken.None);
        Assert.Equal(0, exitCode);

        var manifestPath = Path.Join(workingDirectory, "version-manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        return manifest.RootElement.GetProperty("ConfigHash").GetString()!;
    }

    [Fact]
    public async Task NewAliasRunsInit()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = tempDir;

            var exitCode = await Program.ExecuteAsync(["--non-interactive", "new"], CancellationToken.None);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Join(tempDir, ".rexo", "rexo.yaml")));
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
    public async Task InitDetectPreviewDoesNotWriteFiles()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-init-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(Path.Join(tempDir, "package.json"), "{\"name\":\"sample\"}");
            Environment.CurrentDirectory = tempDir;

            var exitCode = await Program.ExecuteAsync(["init", "detect"], CancellationToken.None);
            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(Path.Join(tempDir, ".rexo", "rexo.yaml")));
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
    public async Task ArtifactOnlyConfigDoesNotIncludeStandardCommandsImplicitly()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-embedded-standard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            // Truly artifacts-only config: no commands or aliases section
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                  "schemaVersion": "1.0",
                  "name": "sample",
                  "artifacts": [
                    {
                      "type": "docker",
                      "name": "api",
                      "settings": {
                        "image": "ghcr.io/acme/api"
                      }
                    }
                  ]
                }
                """);

            Environment.CurrentDirectory = tempDir;

            var listJsonPath = Path.Join(tempDir, "list-embedded.json");
            var listExitCode = await Program.ExecuteAsync(["--json-file", listJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, listExitCode);

            var listOutput = await File.ReadAllTextAsync(listJsonPath);
            Assert.DoesNotContain("\n  plan", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\n  release", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("publish", listOutput, StringComparison.OrdinalIgnoreCase);

            var explainExitCode = await Program.ExecuteAsync(["explain", "release"], CancellationToken.None);
            Assert.NotEqual(0, explainExitCode);

            var planExitCode = await Program.ExecuteAsync(["plan"], CancellationToken.None);
            Assert.NotEqual(0, planExitCode);

            var buildExitCode = await Program.ExecuteAsync(["build"], CancellationToken.None);
            Assert.NotEqual(0, buildExitCode);

            var verifyExitCode = await Program.ExecuteAsync(["verify"], CancellationToken.None);
            Assert.NotEqual(0, verifyExitCode);
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
    public async Task EmbeddedNoneDoesNotInheritStandardCommands()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-embedded-none-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "extends": ["embedded:none"],
                                    "commands": {
                                        "hello": {
                                            "steps": [
                                                { "run": "echo hello" }
                                            ]
                                        }
                                    }
                                }
                                """);

            Environment.CurrentDirectory = tempDir;

            var listJsonPath = Path.Join(tempDir, "list-none.json");
            var listExitCode = await Program.ExecuteAsync(["--json-file", listJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, listExitCode);

            var listOutput = await File.ReadAllTextAsync(listJsonPath);
            Assert.Contains("hello", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("release", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("build", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\n  verify", listOutput, StringComparison.OrdinalIgnoreCase);
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
    public async Task EmbeddedStandardExposesLifecycleCommands()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-embedded-standard-commands-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "extends": ["embedded:standard"],
                                    "versioning": {
                                        "provider": "fixed",
                                        "fallback": "1.2.3"
                                    }
                                }
                                """);

            Environment.CurrentDirectory = tempDir;

            var listJsonPath = Path.Join(tempDir, "list-standard.json");
            var listExitCode = await Program.ExecuteAsync(["--json-file", listJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, listExitCode);

            var listOutput = await File.ReadAllTextAsync(listJsonPath);
            Assert.Contains("plan", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("release", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("build", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("verify", listOutput, StringComparison.OrdinalIgnoreCase);
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
    public async Task ConfigCommandCleansUpEmptyAnalysisOutputDirectories()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-analysis-dirs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "outputs": {
                                        "analysis": {
                                            "reports": "artifacts/analysis",
                                            "sarif": "artifacts/analysis/sarif"
                                        }
                                    },
                                    "commands": {
                                        "noop": {
                                            "steps": [
                                                { "run": "dotnet --version" }
                                            ]
                                        }
                                    }
                                }
                                """);

            Environment.CurrentDirectory = tempDir;

            var exitCode = await Program.ExecuteAsync(["noop"], CancellationToken.None);
            Assert.Equal(0, exitCode);

            Assert.False(Directory.Exists(Path.Join(tempDir, "artifacts", "analysis")));
            Assert.False(Directory.Exists(Path.Join(tempDir, "artifacts", "analysis", "sarif")));
            Assert.True(File.Exists(Path.Join(tempDir, "artifacts", "manifests", "commands.json")));
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
    public async Task PolicyCommandAppearsInListAndExecutesDirectly()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "commands": {
                                        "local": {
                                            "description": "Local command",
                                            "steps": [
                                                { "id": "local-step", "uses": "builtin:resolve-version" }
                                            ]
                                        }
                                    },
                                    "aliases": {},
                                    "versioning": {
                                        "provider": "fixed",
                                        "fallback": "1.2.3"
                                    }
                                }
                                """);

            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "policy.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample-policy",
                                    "commands": {
                                        "release": {
                                            "description": "Resolve version from policy",
                                            "steps": [
                                                { "uses": "builtin:resolve-version" }
                                            ]
                                        }
                                    },
                                    "aliases": {}
                                }
                                """);

            Environment.CurrentDirectory = tempDir;

            var listJsonPath = Path.Join(tempDir, "list.json");
            var listExitCode = await Program.ExecuteAsync(["--json-file", listJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, listExitCode);

            var listOutput = await File.ReadAllTextAsync(listJsonPath);
            Assert.Contains("release", listOutput, StringComparison.OrdinalIgnoreCase);

            var releaseExitCode = await Program.ExecuteAsync(["release"], CancellationToken.None);
            Assert.Equal(0, releaseExitCode);
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
    public async Task ShipWithoutConfirmSkipsPushAndSucceeds()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-ship-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "extends": ["embedded:standard"],
                                    "artifacts": [
                                        {
                                            "type": "docker",
                                            "name": "api",
                                            "settings": {
                                                "image": "ghcr.io/acme/api"
                                            }
                                        }
                                    ]
                                }
                                """);

            Environment.CurrentDirectory = tempDir;
            var shipExitCode = await Program.ExecuteAsync(["ship"], CancellationToken.None);
            Assert.Equal(0, shipExitCode);
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
    public async Task DirectAndRunPathsResolveMultiWordCommandsIdentically()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-run-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "commands": {
                                        "branch feature": {
                                            "description": "Create a feature branch",
                                            "args": {
                                                "name": { "required": true, "description": "Branch name" }
                                            },
                                            "steps": [
                                                {
                                                    "id": "emit",
                                                    "run": "echo {{args.name}}",
                                                    "outputPattern": "(?<name>.+)"
                                                }
                                            ]
                                        }
                                    },
                                    "aliases": {}
                                }
                                """);

            Environment.CurrentDirectory = tempDir;

            using var direct = await ExecuteJsonAsync(["branch", "feature", "customer-search"]);
            using var viaRun = await ExecuteJsonAsync(["run", "branch", "feature", "customer-search"]);

            Assert.Equal("branch feature", direct.RootElement.GetProperty("Command").GetString());
            Assert.Equal("branch feature", viaRun.RootElement.GetProperty("Command").GetString());

            var directName = direct.RootElement.GetProperty("Steps")[0].GetProperty("Outputs").GetProperty("name").GetString();
            var viaRunName = viaRun.RootElement.GetProperty("Steps")[0].GetProperty("Outputs").GetProperty("name").GetString();

            Assert.Equal("customer-search", directName);
            Assert.Equal(directName, viaRunName);
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
    public async Task DirectAndRunPathsForwardOptionsIdentically()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-run-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "commands": {
                                        "release": {
                                            "description": "Synthetic release command for option parity",
                                            "options": {
                                                "push": { "type": "bool", "default": false }
                                            },
                                            "steps": [
                                                {
                                                    "id": "emit",
                                                    "run": "echo {{options.push}}",
                                                    "outputPattern": "(?<push>true|false)"
                                                }
                                            ]
                                        }
                                    },
                                    "aliases": {}
                                }
                                """);

            Environment.CurrentDirectory = tempDir;

            using var direct = await ExecuteJsonAsync(["release", "--push"]);
            using var viaRun = await ExecuteJsonAsync(["run", "release", "--push"]);

            Assert.Equal("release", direct.RootElement.GetProperty("Command").GetString());
            Assert.Equal("release", viaRun.RootElement.GetProperty("Command").GetString());

            var directPush = direct.RootElement.GetProperty("Steps")[0].GetProperty("Outputs").GetProperty("push").GetString();
            var viaRunPush = viaRun.RootElement.GetProperty("Steps")[0].GetProperty("Outputs").GetProperty("push").GetString();

            Assert.Equal("true", directPush);
            Assert.Equal(directPush, viaRunPush);
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
    public async Task RemotePolicySourceFromEnvironmentIsMerged()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-remote-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;
        var originalSources = Environment.GetEnvironmentVariable("REXO_POLICY_SOURCES");

                try
                {
                        await File.WriteAllTextAsync(
                                Path.Join(tempDir, "rexo.json"),
                                """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "versioning": {
                                        "provider": "fixed",
                                        "fallback": "1.2.3"
                                    }
                                }
                                """);

                        var externalPolicy = Path.Join(tempDir, "external.policy.json");
                        await File.WriteAllTextAsync(
                                externalPolicy,
                                """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "external-policy",
                                    "commands": {
                                        "from remote": {
                                            "description": "Resolve version from environment policy source",
                                            "steps": [
                                                { "uses": "builtin:resolve-version" }
                                            ]
                                        }
                                    },
                                    "aliases": {}
                                }
                                """);

                        Environment.SetEnvironmentVariable("REXO_POLICY_SOURCES", externalPolicy);
                        Environment.CurrentDirectory = tempDir;

                        var listJsonPath = Path.Join(tempDir, "list-remote-policy.json");
                        var listExitCode = await Program.ExecuteAsync(["--json-file", listJsonPath, "--json", "list"], CancellationToken.None);
                        Assert.Equal(0, listExitCode);

                        var listOutput = await File.ReadAllTextAsync(listJsonPath);
                        Assert.Contains("from remote", listOutput, StringComparison.OrdinalIgnoreCase);

                        var commandExitCode = await Program.ExecuteAsync(["from", "remote"], CancellationToken.None);
                        Assert.Equal(0, commandExitCode);
                }
                finally
                {
                        Environment.SetEnvironmentVariable("REXO_POLICY_SOURCES", originalSources);
                        Environment.CurrentDirectory = originalDirectory;

                        if (Directory.Exists(tempDir))
                        {
                                Directory.Delete(tempDir, true);
                        }
                }
        }

    [Fact]
    public async Task TemplatesListReturnsAllEmbeddedTemplateNames()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-templates-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var outFile = Path.Join(tempDir, "templates-list.json");
            var exitCode = await Program.ExecuteAsync(
                ["--json", "--json-file", outFile, "policies", "list"],
                CancellationToken.None);
            Assert.Equal(0, exitCode);

            var output = await File.ReadAllTextAsync(outFile);
            Assert.Contains("standard", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dotnet", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("git-tag", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("none", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("node", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet-library", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet-api", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("github-status", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("github-sarif", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gitlab-status", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("github-release", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TemplatesShowReturnsJsonForKnownTemplate()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-templates-show-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var outFile = Path.Join(tempDir, "templates-show.json");
            var exitCode = await Program.ExecuteAsync(
                ["--json", "--json-file", outFile, "policies", "show", "dotnet"],
                CancellationToken.None);
            Assert.Equal(0, exitCode);

            var output = await File.ReadAllTextAsync(outFile);
            Assert.Contains("dotnet-policy", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("commands", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TemplatesShowReturnsNonZeroForUnknownTemplate()
    {
        var exitCode = await Program.ExecuteAsync(["policies", "show", "no-such-template"], CancellationToken.None);
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task InitDetectJsonContractIncludesStructuredDetectionAndRecommendations()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-init-detect-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(Path.Join(tempDir, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0");
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "Api.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            Environment.CurrentDirectory = tempDir;

            using var document = await ExecuteJsonAsync(["init", "detect"]);
            var root = document.RootElement;

            Assert.Equal("init detect", root.GetProperty("Command").GetString());
            var outputs = root.GetProperty("Outputs");

            Assert.Equal("1.1", outputs.GetProperty("contractVersion").GetString());
            Assert.True(outputs.GetProperty("hasDockerfile").GetBoolean());
            Assert.Equal("dotnet", outputs.GetProperty("detectedTemplate").GetString());
            Assert.Equal("dotnet", outputs.GetProperty("resolvedTemplate").GetString());
            Assert.Equal("dotnet", outputs.GetProperty("recommendedPolicyTemplate").GetString());

            var detection = outputs.GetProperty("detection");
            Assert.Equal("dotnet", detection.GetProperty("DetectedTemplate").GetString());
            Assert.Equal("dotnet", detection.GetProperty("ResolvedTemplate").GetString());
            Assert.True(detection.GetProperty("HasDockerfile").GetBoolean());
            Assert.True(detection.GetProperty("Signals").GetArrayLength() > 0);

            var recommendations = outputs.GetProperty("recommendations").EnumerateArray().ToList();
            Assert.True(recommendations.Count >= 3);

            var policyRecommendation = recommendations.First(r =>
                string.Equals(r.GetProperty("Kind").GetString(), "policy-template", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("dotnet", policyRecommendation.GetProperty("Value").GetString());
            Assert.True(policyRecommendation.GetProperty("Confidence").GetDouble() >= 0.8);
            Assert.True(policyRecommendation.GetProperty("Reasons").GetArrayLength() > 0);
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
    public async Task InitDetectJsonContractUsesFullConfidenceForExplicitTemplateSelection()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-init-detect-explicit-template-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(Path.Join(tempDir, "package.json"), "{\"name\":\"sample\"}");
            Environment.CurrentDirectory = tempDir;

            using var document = await ExecuteJsonAsync(["init", "detect", "--stack", "node"]);
            var outputs = document.RootElement.GetProperty("Outputs");
            Assert.Equal("node", outputs.GetProperty("resolvedTemplate").GetString());

            var starterTemplateRecommendation = outputs
                .GetProperty("recommendations")
                .EnumerateArray()
                .First(r => string.Equals(r.GetProperty("Kind").GetString(), "starter-template", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("node", starterTemplateRecommendation.GetProperty("Value").GetString());
            Assert.Equal(1.0, starterTemplateRecommendation.GetProperty("Confidence").GetDouble());
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

    private static async Task<JsonDocument> ExecuteJsonAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Program.ExecuteAsync([.. args.Prepend("--json")], CancellationToken.None);
            Assert.Equal(0, exitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr.ToString()));

            return JsonDocument.Parse(stdout.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Layered command composition integration tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StandardDotnetLayeredTestCommandExpandsAndExecutesSuccessfully()
    {
        // Verify that extends: ["embedded:dotnet", "embedded:standard"] correctly
        // expands standard.test (wrap) around dotnet.test content at compile time.
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-layered-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                    "schemaVersion": "1.0",
                    "name": "sample",
                    "extends": ["embedded:dotnet", "embedded:standard"],
                    "versioning": {
                        "provider": "fixed",
                        "fallback": "1.2.3"
                    }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            // The 'test' command should exist (from both layers) and be executable
            var listJsonPath = Path.Join(tempDir, "list-layered.json");
            var listExitCode = await Program.ExecuteAsync(["--json-file", listJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, listExitCode);

            var listOutput = await File.ReadAllTextAsync(listJsonPath);
            Assert.Contains("test", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("analyze", listOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExplainCommandShowsStepsForLayeredCommands()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-explain-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                    "schemaVersion": "1.0",
                    "name": "sample",
                    "extends": ["embedded:standard"],
                    "versioning": {
                        "provider": "fixed",
                        "fallback": "1.2.3"
                    }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            // explain test via --json-file to get structured output containing the explain text
            var explainJsonPath = Path.Join(tempDir, "explain-test.json");
            var exitCode = await Program.ExecuteAsync(
                ["--json-file", explainJsonPath, "--json", "explain", "test"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var output = await File.ReadAllTextAsync(explainJsonPath);
            // The test command from standard shows its description and steps
            Assert.Contains("test", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Run configured tests", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ListIncludeHiddenShowsHiddenCommandsWhenRequested()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-cli-list-hidden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Join(tempDir, "rexo.json"),
                    """
                                {
                                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                                    "schemaVersion": "1.0",
                                    "name": "sample",
                                    "commands": {
                                        "visible": {
                                            "steps": [
                                                { "run": "echo visible" }
                                            ]
                                        },
                                        "hidden-helper": {
                                            "hidden": true,
                                            "steps": [
                                                { "run": "echo hidden" }
                                            ]
                                        }
                                    }
                                }
                                """);

            Environment.CurrentDirectory = tempDir;

            var defaultListJsonPath = Path.Join(tempDir, "list-default.json");
            var defaultListExitCode = await Program.ExecuteAsync(["--json-file", defaultListJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, defaultListExitCode);

            var defaultListOutput = await File.ReadAllTextAsync(defaultListJsonPath);
            Assert.Contains("visible", defaultListOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hidden-helper", defaultListOutput, StringComparison.OrdinalIgnoreCase);

            var includeHiddenListJsonPath = Path.Join(tempDir, "list-include-hidden.json");
            var includeHiddenListExitCode = await Program.ExecuteAsync(["--json-file", includeHiddenListJsonPath, "--json", "list", "--include-hidden"], CancellationToken.None);
            Assert.Equal(0, includeHiddenListExitCode);

            var includeHiddenListOutput = await File.ReadAllTextAsync(includeHiddenListJsonPath);
            Assert.Contains("visible", includeHiddenListOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hidden-helper", includeHiddenListOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task StandardTestStandaloneRunsGracefullyWithNoInnerLayer()
    {
        // When extends: ["embedded:standard"] is used alone (no dotnet/node layer),
        // the 'test' command should succeed with a skipped continuation step.
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-standalone-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                    "schemaVersion": "1.0",
                    "name": "sample",
                    "extends": ["embedded:standard"],
                    "versioning": {
                        "provider": "fixed",
                        "fallback": "1.2.3"
                    }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            // 'test' runs without error even when no inner layer provides test steps
            var testExitCode = await Program.ExecuteAsync(["test"], CancellationToken.None);
            Assert.Equal(0, testExitCode);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task StandardDotnetLayerModeReleaseBuildCallIncludesDotnetBuildSteps()
    {
        // Verify that when standard + dotnet are merged with layer mode,
        // the 'build' command that 'release' calls does NOT include dotnet.build steps.
        // This ensures release -> build is a fresh command lookup, not a layer continuation.
        var tempDir = Path.Join(Path.GetTempPath(), $"rexo-release-build-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "rexo.json"),
                """
                {
                    "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
                    "schemaVersion": "1.0",
                    "name": "sample",
                    "extends": ["embedded:standard", "embedded:dotnet"],
                    "versioning": {
                        "provider": "fixed",
                        "fallback": "1.2.3"
                    }
                }
                """);

            Environment.CurrentDirectory = tempDir;

            // explain build to see what steps will execute
            var explainJsonPath = Path.Join(tempDir, "explain-build.json");
            var exitCode = await Program.ExecuteAsync(
                ["--json-file", explainJsonPath, "--json", "explain", "build"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var output = await File.ReadAllTextAsync(explainJsonPath);

            // The build command should include standard lifecycle steps
            Assert.Contains("build-artifacts", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tag-artifacts", output, StringComparison.OrdinalIgnoreCase);

            // And should include the dotnet overlay build step in the continuation slot
            Assert.Contains("dotnet build", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dotnet-build", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
