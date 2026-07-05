namespace Rexo.Integration.Tests;

using System.Globalization;
using System.Text.Json;
using Rexo.Cli;

[Collection("IntegrationSequential")]
public sealed class CliSmokeTests
{
    [Fact]
    public async Task VersionCommandReturnsSuccess()
    {
        var exitCode = await Program.ExecuteAsync(["version"], CancellationToken.None);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task NewAliasRunsInit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = tempDir;

            var exitCode = await Program.ExecuteAsync(["new", "--yes"], CancellationToken.None);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".rexo", "rexo.json")));
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-init-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "package.json"), "{\"name\":\"sample\"}");
            Environment.CurrentDirectory = tempDir;

            var exitCode = await Program.ExecuteAsync(["init", "detect"], CancellationToken.None);
            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(Path.Combine(tempDir, ".rexo", "rexo.json")));
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-embedded-standard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            // Truly artifacts-only config: no commands or aliases section
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "rexo.json"),
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

            var listJsonPath = Path.Combine(tempDir, "list-embedded.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-embedded-none-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Combine(tempDir, "rexo.json"),
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

            var listJsonPath = Path.Combine(tempDir, "list-none.json");
            var listExitCode = await Program.ExecuteAsync(["--json-file", listJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, listExitCode);

            var listOutput = await File.ReadAllTextAsync(listJsonPath);
            Assert.Contains("hello", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("release", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("build", listOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verify", listOutput, StringComparison.OrdinalIgnoreCase);
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-embedded-standard-commands-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Combine(tempDir, "rexo.json"),
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

            var listJsonPath = Path.Combine(tempDir, "list-standard.json");
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
    public async Task PolicyCommandAppearsInListAndExecutesDirectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

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
                    Path.Combine(tempDir, "policy.json"),
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

            var listJsonPath = Path.Combine(tempDir, "list.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-ship-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                    Path.Combine(tempDir, "rexo.json"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-run-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-run-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-remote-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;
        var originalSources = Environment.GetEnvironmentVariable("REXO_POLICY_SOURCES");

                try
                {
                        await File.WriteAllTextAsync(
                                Path.Combine(tempDir, "rexo.json"),
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

                        var externalPolicy = Path.Combine(tempDir, "external.policy.json");
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

                        var listJsonPath = Path.Combine(tempDir, "list-remote-policy.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-templates-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var outFile = Path.Combine(tempDir, "templates-list.json");
            var exitCode = await Program.ExecuteAsync(
                ["--json", "--json-file", outFile, "policies", "list"],
                CancellationToken.None);
            Assert.Equal(0, exitCode);

            var output = await File.ReadAllTextAsync(outFile);
            Assert.Contains("standard", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dotnet", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("git-tag", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("github-status", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("github-sarif", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gitlab-status", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("github-release", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("none", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("node", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet-library", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet-api", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TemplatesShowReturnsJsonForKnownTemplate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-templates-show-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var outFile = Path.Combine(tempDir, "templates-show.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-init-detect-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0");
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "Api.csproj"),
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-init-detect-explicit-template-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "package.json"), "{\"name\":\"sample\"}");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-layered-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "rexo.json"),
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
            var listJsonPath = Path.Combine(tempDir, "list-layered.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-explain-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "rexo.json"),
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
            var explainJsonPath = Path.Combine(tempDir, "explain-test.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-cli-list-hidden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var originalDirectory = Environment.CurrentDirectory;

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

            var defaultListJsonPath = Path.Combine(tempDir, "list-default.json");
            var defaultListExitCode = await Program.ExecuteAsync(["--json-file", defaultListJsonPath, "--json", "list"], CancellationToken.None);
            Assert.Equal(0, defaultListExitCode);

            var defaultListOutput = await File.ReadAllTextAsync(defaultListJsonPath);
            Assert.Contains("visible", defaultListOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hidden-helper", defaultListOutput, StringComparison.OrdinalIgnoreCase);

            var includeHiddenListJsonPath = Path.Combine(tempDir, "list-include-hidden.json");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-standalone-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "rexo.json"),
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
    public async Task StandardDotnetLayerModeReleaseBuildCallDoesNotIncludeDotnetBuildSteps()
    {
        // Verify that when standard + dotnet are merged with layer mode,
        // the 'build' command that 'release' calls does NOT include dotnet.build steps.
        // This ensures release -> build is a fresh command lookup, not a layer continuation.
        var tempDir = Path.Combine(Path.GetTempPath(), $"rexo-release-build-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "rexo.json"),
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
            var explainJsonPath = Path.Combine(tempDir, "explain-build.json");
            var exitCode = await Program.ExecuteAsync(
                ["--json-file", explainJsonPath, "--json", "explain", "build"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var output = await File.ReadAllTextAsync(explainJsonPath);

            // The build command should include standard lifecycle steps
            Assert.Contains("build-artifacts", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tag-artifacts", output, StringComparison.OrdinalIgnoreCase);

            // But should NOT include dotnet-specific steps
            Assert.DoesNotContain("dotnet build", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet-build", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}

