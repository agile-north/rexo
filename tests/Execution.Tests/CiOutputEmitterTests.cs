namespace Rexo.Execution.Tests;

using System.Globalization;
using System.Text.Json;
using Rexo.Ci;
using Rexo.Core.Models;

public sealed class CiOutputEmitterTests
{
    [Fact]
    public void BuildPayloadDefaultsToUpperSnakeWithPrefix()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest);

        Assert.Contains("REXO_REPO_NAME", payload.Variables.Keys);
        Assert.Equal("repo", payload.Variables["REXO_REPO_NAME"]);
        Assert.Equal("0", payload.Variables["REXO_EXIT_CODE"]);
        Assert.Equal("true", payload.Variables["REXO_SUCCESS"]);
        Assert.Equal("10000", payload.Variables["REXO_DURATION_MS"]);
    }

    [Fact]
    public void BuildPayloadSupportsLowerSnakeAndCollisionSuffixing()
    {
        var manifest = new RunManifest
        {
            RepoName = "Repo One",
            RepoRoot = "Repo-One",
            ExitCode = 1,
            Success = false,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:01Z", CultureInfo.InvariantCulture),
            Artifacts = [new ArtifactManifestEntry("docker", "app", true, false, ["v1"])],
        };

        using var scopeDocument = JsonDocument.Parse("\"full\"");

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            KeyCasing = "lowerSnake",
            Prefix = "ci_",
            Scope = scopeDocument.RootElement.Clone(),
        });

        Assert.Contains("ci_repo_name", payload.Variables.Keys);
        Assert.Contains("ci_repo_root", payload.Variables.Keys);
        Assert.True(payload.Warnings.Count >= 0);
    }

    [Fact]
    public void FormatStdoutLinesUsesProviderSpecificSyntax()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            Provider = "azure-devops",
        });

        var lines = CiOutputEmitter.FormatStdoutLines(payload);

        Assert.Contains(lines, line => line.StartsWith("##vso[task.setvariable variable=REXO_REPO_NAME]", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatStdoutLinesSupportsGitHubActionsFallbackFormatting()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            Provider = "github-actions",
        });

        var lines = CiOutputEmitter.FormatStdoutLines(payload);

        Assert.Contains("REXO_REPO_NAME=repo", lines);
    }

    [Theory]
    [InlineData("gitlab-ci")]
    [InlineData("bitbucket-pipelines")]
    public void FormatStdoutLinesFallsBackToGenericDialectForUnsupportedContexts(string provider)
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            Provider = provider,
        });

        var lines = CiOutputEmitter.FormatStdoutLines(payload);

        Assert.Contains("REXO_REPO_NAME=repo", lines);
    }

    [Fact]
    public void BuildPayloadSupportsPreciseScopeSelectors()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            CommandExecuted = "release",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        using var scopeDocument = JsonDocument.Parse(
            """
            {
              "mode": "full",
              "include": ["repo_name", "command_*", "regex:^exit_code$"],
              "exclude": ["command_executed"]
            }
            """);

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            Scope = scopeDocument.RootElement.Clone(),
        });

        Assert.Contains("REXO_REPO_NAME", payload.Variables.Keys);
        Assert.Contains("REXO_EXIT_CODE", payload.Variables.Keys);
        Assert.DoesNotContain("REXO_COMMAND_EXECUTED", payload.Variables.Keys);
        Assert.DoesNotContain("REXO_VERSION", payload.Variables.Keys);
    }

    [Fact]
    public void BuildPayloadKeepsNuGetTokenTogetherInUpperSnakeKeys()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            NuGetVersion = "1.2.3",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest);

        Assert.Contains("REXO_NUGET_VERSION", payload.Variables.Keys);
        Assert.DoesNotContain("REXO_NU_GET_VERSION", payload.Variables.Keys);
    }

    [Fact]
    public void BuildPayloadMatchesNuGetScopeMaskWithoutSplitToken()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            NuGetVersion = "1.2.3",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        using var scopeDocument = JsonDocument.Parse(
            """
            {
              "mode": "full",
              "include": ["nuget_version"]
            }
            """);

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            Scope = scopeDocument.RootElement.Clone(),
        });

        Assert.Single(payload.Variables);
        Assert.Contains("REXO_NUGET_VERSION", payload.Variables.Keys);
    }

    [Fact]
    public void BuildPayloadSkipsEmptyValuesByDefault()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            RepoRoot = null,
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest);

        Assert.DoesNotContain("REXO_REPO_ROOT", payload.Variables.Keys);
        Assert.Contains("REXO_REPO_NAME", payload.Variables.Keys);
    }

    [Fact]
    public void BuildPayloadCanEmitEmptyValuesWhenConfigured()
    {
        var manifest = new RunManifest
        {
            RepoName = "repo",
            RepoRoot = null,
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            EmitEmptyValues = true,
        });

        Assert.Contains("REXO_REPO_ROOT", payload.Variables.Keys);
        Assert.Equal(string.Empty, payload.Variables["REXO_REPO_ROOT"]);
    }

    [Fact]
    public void BuildPayloadSafeModeFlattensVersionFieldsInsteadOfRawObject()
    {
        var version = new VersionResult(
            SemVer: "1.2.3",
            Major: 1,
            Minor: 2,
            Patch: 3,
            PreRelease: null,
            CommitSha: "abcdef123456",
            ShortSha: "abcdef1",
            IsPreRelease: false,
            IsStable: true)
        {
            NuGetVersion = "1.2.3",
            DockerVersion = "1.2.3",
        };

        var manifest = new RunManifest
        {
            RepoName = "repo",
            Version = version,
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
        };

        var payload = CiOutputEmitter.BuildPayload(manifest);

        Assert.Contains("REXO_VERSION_SEM_VER", payload.Variables.Keys);
        Assert.Equal("1.2.3", payload.Variables["REXO_VERSION_SEM_VER"]);
        Assert.DoesNotContain("REXO_VERSION", payload.Variables.Keys);
    }

    [Fact]
    public void BuildPayloadSafeModeFlattensStepFileOutputsWhenIncluded()
    {
        var step = new StepManifestEntry("pack", true, 0, 10)
        {
            FileOutputs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["packages"] = ["artifacts/pkg-a.nupkg", "artifacts/pkg-b.nupkg"],
            },
        };

        var manifest = new RunManifest
        {
            RepoName = "repo",
            ExitCode = 0,
            Success = true,
            StartedAt = DateTimeOffset.Parse("2026-06-04T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-06-04T10:00:10Z", CultureInfo.InvariantCulture),
            Steps = [step],
        };

        var payload = CiOutputEmitter.BuildPayload(manifest, new CiEmissionOptions
        {
            IncludeStepOutputs = true,
        });

        Assert.Contains("REXO_STEPS_PACK_FILE_OUTPUTS_PACKAGES_COUNT", payload.Variables.Keys);
        Assert.Contains("REXO_STEPS_PACK_FILE_OUTPUTS_PACKAGES_0", payload.Variables.Keys);
        Assert.Contains("REXO_STEPS_PACK_FILE_OUTPUTS_PACKAGES_1", payload.Variables.Keys);
        Assert.DoesNotContain("REXO_STEPS_PACK_FILE_OUTPUTS", payload.Variables.Keys);
        Assert.Equal("2", payload.Variables["REXO_STEPS_PACK_FILE_OUTPUTS_PACKAGES_COUNT"]);
    }
}
