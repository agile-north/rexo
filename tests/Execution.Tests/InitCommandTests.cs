namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Core.Models;
using YamlDotNet.RepresentationModel;

public sealed class InitCommandTests
{
    [Fact]
    public async Task InitCreatesYamlConfigInDotRexoByDefault()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-init-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["with-policy"] = "true",
                    ["policy"] = "dotnet",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success, result.Message ?? string.Empty);
            var configPath = Path.Join(dir, ".rexo", "rexo.yaml");
            var policyPath = Path.Join(dir, ".rexo", "policy.yaml");
            Assert.True(File.Exists(configPath));
            Assert.True(File.Exists(policyPath));
            Assert.False(File.Exists(Path.Join(dir, ".rexo", "rexo.json")));
            Assert.False(File.Exists(Path.Join(dir, ".rexo", "policy.json")));

            var content = await File.ReadAllTextAsync(configPath);
            Assert.StartsWith(
                "# yaml-language-server: $schema=https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json\n",
                content,
                StringComparison.Ordinal);
            Assert.Contains("$schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json", content, StringComparison.Ordinal);
            Assert.Contains("schemaVersion: \"1.0\"", content, StringComparison.Ordinal);

            var policyContent = await File.ReadAllTextAsync(policyPath);
            Assert.StartsWith("# yaml-language-server: $schema=", policyContent, StringComparison.Ordinal);

            Assert.Equal(configPath, Rexo.Configuration.ConfigFileLocator.FindConfigPath(dir));
            var config = await Rexo.Configuration.RepoConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
            Assert.NotNull(config.Commands);
            var policy = await Rexo.Configuration.RepoConfigurationLoader.LoadPolicyAsync(policyPath, CancellationToken.None);
            Assert.NotNull(policy);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("dotnet", "yaml")]
    [InlineData("dotnet", "json")]
    [InlineData("node", "yaml")]
    [InlineData("node", "json")]
    [InlineData("python", "yaml")]
    [InlineData("go", "yaml")]
    [InlineData("java", "yaml")]
    [InlineData("ruby", "yaml")]
    [InlineData("generic", "yaml")]
    [InlineData("generic", "json")]
    public async Task InitTemplatesProduceConfigAndPolicyThatPassSchemaValidation(string stack, string format)
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-init-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault());
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["stack"] = stack,
                    ["with-policy"] = "true",
                    ["format"] = format,
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);
            Assert.True(result.Success, result.Message ?? string.Empty);

            var configPath = Rexo.Configuration.ConfigFileLocator.FindConfigPath(dir);
            var policyPath = Rexo.Configuration.ConfigFileLocator.FindPolicyPath(dir);
            Assert.Equal(Path.Join(dir, ".rexo", $"rexo.{format}"), configPath);
            Assert.Equal(Path.Join(dir, ".rexo", $"policy.{format}"), policyPath);
            Assert.Empty(Rexo.Configuration.ConfigFileLocator.GetShadowedFileWarnings(dir));

            var config = await Rexo.Configuration.RepoConfigurationLoader.LoadAsync(configPath!, CancellationToken.None);
            Assert.NotNull(config);
            var policy = await Rexo.Configuration.RepoConfigurationLoader.LoadPolicyAsync(policyPath!, CancellationToken.None);
            Assert.NotNull(policy);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("toml")]
    [InlineData("yml")]
    public async Task InitRejectsInvalidFormat(string format)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-format-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault());
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?> { ["yes"] = "true", ["format"] = format },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("--format", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitCreatesConfigInDotRexoByDefaultWhenNonInteractive()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?> { ["yes"] = "true", ["format"] = "json" },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            Assert.True(File.Exists(configPath));
            // Default schema source is remote — no local schema file written.
            var schemaPath = Path.Combine(dir, ".rexo", "rexo.schema.json");
            Assert.False(File.Exists(schemaPath));
            var content = await File.ReadAllTextAsync(configPath);
            Assert.Contains("\"$schema\": \"https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json\"", content, StringComparison.Ordinal);
            Assert.Contains("\"schemaVersion\": \"1.0\"", content, StringComparison.Ordinal);
            Assert.Contains("\"commands\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitReturnsFailureWhenConfigExistsAndNoForce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-exists-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var existingPath = Path.Combine(dir, "rexo.json");
            await File.WriteAllTextAsync(existingPath, "{}");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?> { ["yes"] = "true", ["format"] = "json" },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("already exists", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitForceRemovesOnlySupersededDotRexoVariants()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-force-variants-{Guid.NewGuid():N}");
        var rexoDir = Path.Combine(dir, ".rexo");
        Directory.CreateDirectory(rexoDir);

        try
        {
            var rootConfig = Path.Combine(dir, "rexo.json");
            await File.WriteAllTextAsync(Path.Combine(rexoDir, "rexo.json"), "old-json-config");
            await File.WriteAllTextAsync(Path.Combine(rexoDir, "rexo.yml"), "old-yaml-config");
            await File.WriteAllTextAsync(Path.Combine(rexoDir, "policy.json"), "old-json-policy");
            await File.WriteAllTextAsync(Path.Combine(rexoDir, "policy.yml"), "old-yaml-policy");
            await File.WriteAllTextAsync(rootConfig, "preserve-root-config");

            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault());
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["force"] = "true",
                    ["format"] = "yaml",
                    ["with-policy"] = "true",
                    ["policy"] = "standard",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(rexoDir, "rexo.yaml")));
            Assert.True(File.Exists(Path.Combine(rexoDir, "policy.yaml")));
            Assert.False(File.Exists(Path.Combine(rexoDir, "rexo.json")));
            Assert.False(File.Exists(Path.Combine(rexoDir, "rexo.yml")));
            Assert.False(File.Exists(Path.Combine(rexoDir, "policy.json")));
            Assert.False(File.Exists(Path.Combine(rexoDir, "policy.yml")));
            Assert.Equal("preserve-root-config", await File.ReadAllTextAsync(rootConfig));
            Assert.Contains($"Removed superseded config: {Path.Combine(".rexo", "rexo.json")}", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains($"Removed superseded config: {Path.Combine(".rexo", "policy.json")}", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("Preserved out-of-slot config candidate: rexo.json", result.Message ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitCreatesPolicyWhenRequested()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["with-policy"] = "true",
                    ["policy"] = "dotnet",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var policyPath = Path.Combine(dir, ".rexo", "policy.json");
            Assert.True(File.Exists(policyPath));
            var content = await File.ReadAllTextAsync(policyPath);
            Assert.Contains("dotnet-policy", content, StringComparison.Ordinal);
            Assert.Contains("\"restore\":", content, StringComparison.Ordinal);
            Assert.Contains("\"build\":", content, StringComparison.Ordinal);
            Assert.Contains("\"test\":", content, StringComparison.Ordinal);
            Assert.Contains("\"analyze\":", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ci\":", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"release\":", content, StringComparison.Ordinal);

            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var configContent = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("\"build\":", configContent, StringComparison.Ordinal);
            Assert.Contains("\"local build\":", configContent, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithStandardPolicyRenamesCollidingStarterBuildCommand()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-standard-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["with-policy"] = "true",
                    ["policy"] = "standard",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("\"build\":", content, StringComparison.Ordinal);
            Assert.Contains("\"local build\":", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitFailsWhenPolicyTemplateIsInvalid()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-policy-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["with-policy"] = "true",
                    ["policy"] = "does-not-exist",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("Invalid --policy", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitAutoDetectsPythonTemplate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-python-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "requirements.txt"), "pytest\n");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["template"] = "auto",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);
            Assert.Contains("python -m compileall .", content, StringComparison.Ordinal);
            Assert.Contains("python -m pytest", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitAutoWithPolicyPrefersDotnetTemplateForLibraryProjects()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-dotnet-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "Library.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["template"] = "auto",
                    ["with-policy"] = "true",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var policyPath = Path.Combine(dir, ".rexo", "policy.json");
            var content = await File.ReadAllTextAsync(policyPath);
            Assert.Contains("dotnet-policy", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitAutoWithPolicyPrefersDotnetTemplateWhenDockerfileDetected()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-dotnet-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "Api.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["template"] = "auto",
                    ["with-policy"] = "true",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var policyPath = Path.Combine(dir, ".rexo", "policy.json");
            var content = await File.ReadAllTextAsync(policyPath);
            Assert.Contains("dotnet-policy", content, StringComparison.Ordinal);
            Assert.Contains("Dockerfile detected", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitDetectPreviewReturnsDetectionWithoutWritingFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "Api.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?> { ["mode"] = "detect" },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("init detect", result.Command);
            Assert.Contains("detectedTemplate: dotnet", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("recommendedPolicyTemplate: dotnet", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("1.1", Assert.IsType<string>(result.Outputs["contractVersion"]));

            var detectionJson = JsonSerializer.Serialize(result.Outputs["detection"]);
            using var detectionDoc = JsonDocument.Parse(detectionJson);
            Assert.Equal("dotnet", detectionDoc.RootElement.GetProperty("DetectedTemplate").GetString());
            Assert.True(detectionDoc.RootElement.GetProperty("HasDockerfile").GetBoolean());

            var recommendationsJson = JsonSerializer.Serialize(result.Outputs["recommendations"]);
            using var recommendationsDoc = JsonDocument.Parse(recommendationsJson);
            var recommendations = recommendationsDoc.RootElement.EnumerateArray().ToList();
            Assert.True(recommendations.Count >= 3);

            var policyRecommendation = recommendations.First(r =>
                string.Equals(r.GetProperty("Kind").GetString(), "policy-template", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("dotnet", policyRecommendation.GetProperty("Value").GetString());
            Assert.True(policyRecommendation.GetProperty("Confidence").GetDouble() > 0.5);
            Assert.True(policyRecommendation.GetProperty("Reasons").GetArrayLength() > 0);

            Assert.False(File.Exists(Path.Combine(dir, ".rexo", "rexo.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithDockerArtifactOptionAddsDockerArtifactToConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-with-docker-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["with-docker-artifact"] = "true",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);
            Assert.Contains("\"artifacts\"", content, StringComparison.Ordinal);
            Assert.Contains("\"type\": \"docker\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"image\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"dockerfile\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"context\"", content, StringComparison.Ordinal);

            using var configDoc = JsonDocument.Parse(content);
            var artifact = configDoc.RootElement.GetProperty("artifacts")[0];
            Assert.False(artifact.TryGetProperty("name", out _));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithDockerfileDefaultsToAddingDockerArtifact()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-dockerfile-default-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"), "FROM alpine:3.20");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);
            Assert.Contains("\"artifacts\"", content, StringComparison.Ordinal);
            Assert.Contains("\"type\": \"docker\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"image\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"dockerfile\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"context\"", content, StringComparison.Ordinal);

            using var configDoc = JsonDocument.Parse(content);
            var artifact = configDoc.RootElement.GetProperty("artifacts")[0];
            Assert.False(artifact.TryGetProperty("name", out _));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithNestedDockerfileAddsExplicitDockerfileAndContext()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-nested-dockerfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var appDir = Path.Combine(dir, "services", "api");
            Directory.CreateDirectory(appDir);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Dockerfile"), "FROM alpine:3.20");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);
            Assert.Contains("\"artifacts\"", content, StringComparison.Ordinal);
            Assert.Contains("\"type\": \"docker\"", content, StringComparison.Ordinal);
            Assert.Contains("\"dockerfile\": \"services/api/Dockerfile\"", content, StringComparison.Ordinal);
            Assert.Contains("\"context\": \"services/api\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"image\"", content, StringComparison.Ordinal);

            using var configDoc = JsonDocument.Parse(content);
            var artifact = configDoc.RootElement.GetProperty("artifacts")[0];
            Assert.False(artifact.TryGetProperty("name", out _));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithDockerfileAndWithoutDockerArtifactDoesNotAddArtifact()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-dockerfile-without-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"), "FROM alpine:3.20");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["without-docker-artifact"] = "true",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("\"artifacts\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitFailsWhenBothWithAndWithoutDockerArtifactFlagsAreSupplied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-dockerfile-conflicting-flags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"), "FROM alpine:3.20");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["with-docker-artifact"] = "true",
                    ["without-docker-artifact"] = "true",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("either --with-docker-artifact or --without-docker-artifact", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitFailsWhenInstructionsPathEscapesRepository()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-instructions-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["with-instructions"] = "true",
                    ["instructions-path"] = "..\\outside.instructions.md",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("Path must remain within the repository", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitFailsWhenInstructionsFileExistsAndNoForce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-instructions-exists-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var instructionsPath = Path.Combine(dir, ".github", "instructions", "rexo.instructions.md");
            Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
            await File.WriteAllTextAsync(instructionsPath, "existing");

            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["with-instructions"] = "true",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("Instructions file already exists", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitFailsWhenLocationIsRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-root-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["location"] = "root",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("always creates the config in .rexo/", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithRemoteSchemaDoesNotCreateLocalSchemaFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-remote-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["schema-source"] = "remote",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var schemaPath = Path.Combine(dir, ".rexo", "rexo.schema.json");
            Assert.True(File.Exists(configPath));
            Assert.False(File.Exists(schemaPath));
            var content = await File.ReadAllTextAsync(configPath);
            Assert.Contains("\"$schema\": \"https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitCiCreatesGitHubAndAzdoTemplatesByDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-ci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["mode"] = "ci",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var githubPath = Path.Combine(dir, ".github", "workflows", "rexo-release.yml");
            var azdoPath = Path.Combine(dir, ".azuredevops", "rexo-release.yml");
            Assert.True(File.Exists(githubPath));
            Assert.True(File.Exists(azdoPath));

            var githubYaml = await File.ReadAllTextAsync(githubPath);
            var githubRoot = ParseYamlMapping(githubYaml);
            Assert.Contains("name: rexo-release", githubYaml, StringComparison.Ordinal);
            Assert.Contains("- name: Checkout", githubYaml, StringComparison.Ordinal);
            Assert.Contains("  uses: actions/checkout@v4", githubYaml, StringComparison.Ordinal);
            Assert.Contains("- name: Setup .NET", githubYaml, StringComparison.Ordinal);
            Assert.Contains("  uses: actions/setup-dotnet@v4", githubYaml, StringComparison.Ordinal);
            Assert.Contains("  with:", githubYaml, StringComparison.Ordinal);
            Assert.Contains("    dotnet-version: '10.0.x'", githubYaml, StringComparison.Ordinal);
            Assert.True(githubRoot.Children.ContainsKey(new YamlScalarNode("on")));
            Assert.True(githubRoot.Children.ContainsKey(new YamlScalarNode("jobs")));

            var azdoYaml = await File.ReadAllTextAsync(azdoPath);
            var azdoRoot = ParseYamlMapping(azdoYaml);
            Assert.Contains("trigger:", azdoYaml, StringComparison.Ordinal);
            Assert.Contains("- task: UseDotNet@2", azdoYaml, StringComparison.Ordinal);
            Assert.Contains("  inputs:", azdoYaml, StringComparison.Ordinal);
            Assert.Contains("    packageType: sdk", azdoYaml, StringComparison.Ordinal);
            Assert.Contains("    version: 10.0.x", azdoYaml, StringComparison.Ordinal);
            Assert.Contains("- script: dotnet tool restore", azdoYaml, StringComparison.Ordinal);
            Assert.Contains("  displayName: Restore tools", azdoYaml, StringComparison.Ordinal);
            Assert.True(azdoRoot.Children.ContainsKey(new YamlScalarNode("trigger")));
            Assert.True(azdoRoot.Children.ContainsKey(new YamlScalarNode("steps")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitCiDryRunDoesNotWriteFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-ci-dry-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault());
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["mode"] = "ci",
                    ["dry-run"] = "true",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("Dry run: would initialize", result.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(dir, ".github")));
            Assert.False(Directory.Exists(Path.Combine(dir, ".azuredevops")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitCiChecksAllTargetsBeforeWritingAnyFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-ci-conflict-{Guid.NewGuid():N}");
        var azdoDirectory = Path.Combine(dir, ".azuredevops");
        Directory.CreateDirectory(azdoDirectory);
        var azdoPath = Path.Combine(azdoDirectory, "rexo-release.yml");
        await File.WriteAllTextAsync(azdoPath, "existing");

        try
        {
            var executor = new DefaultCommandExecutor(BuiltinCommandRegistration.CreateDefault());
            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?> { ["mode"] = "ci", ["provider"] = "both" },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.False(Directory.Exists(Path.Combine(dir, ".github")));
            Assert.Equal("existing", await File.ReadAllTextAsync(azdoPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitCiFailsForUnknownProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-ci-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["mode"] = "ci",
                    ["provider"] = "unknown",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("Invalid --provider value", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static YamlMappingNode ParseYamlMapping(string content)
    {
        using var reader = new StringReader(content);
        var stream = new YamlStream();
        stream.Load(reader);

        var root = stream.Documents.Single().RootNode;
        var mapping = root as YamlMappingNode;
        Assert.NotNull(mapping);
        return mapping!;
    }

    [Fact]
    public async Task InitWithBlankTemplateCreatesMinimalConfigWithoutExtends()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-blank-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?> { ["yes"] = "true", ["stack"] = "blank", ["format"] = "json" },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("\"extends\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("embedded:standard", content, StringComparison.Ordinal);
            Assert.Contains("\"hello\":", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"tests\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithBlankTemplateAndWithPolicyButNoPolicyTemplateReturnsError()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-blank-policy-error-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?> { ["yes"] = "true", ["stack"] = "blank", ["with-policy"] = "true" },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("blank template", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task InitWithSpecificPolicyTemplateStacksExtendsOverStandard()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-init-extends-stack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var registry = BuiltinCommandRegistration.CreateDefault();
            var executor = new DefaultCommandExecutor(registry);

            var invocation = new CommandInvocation(
                new Dictionary<string, string>(),
                new Dictionary<string, string?>
                {
                    ["yes"] = "true",
                    ["format"] = "json",
                    ["stack"] = "dotnet",
                    ["with-policy"] = "true",
                    ["policy"] = "dotnet",
                },
                Json: false,
                JsonFile: null,
                WorkingDirectory: dir);

            var result = await executor.ExecuteAsync("init", invocation, CancellationToken.None);

            Assert.True(result.Success);
            var configPath = Path.Combine(dir, ".rexo", "rexo.json");
            var content = await File.ReadAllTextAsync(configPath);

            // extends must contain both standard (lifecycle) and the selected policy template
            Assert.Contains("embedded:standard", content, StringComparison.Ordinal);
            Assert.Contains("embedded:dotnet", content, StringComparison.Ordinal);
            // project-level command uses local prefix, not colliding with policy lifecycle names
            Assert.Contains("\"local build\":", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"compile\":", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
