namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts.Docker;
using Rexo.Core.Models;

[Collection("EnvVar Mutation Sequential")]
public sealed class DockerArtifactProviderTests
{
    [Fact]
    public async Task BuildAsyncUsesBuildxSecretsAndStructuredSettings()
    {
        var invocations = new List<DockerInvocation>();
        var provider = new DockerArtifactProvider(
            runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
            {
                invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                return Task.FromResult((0, string.Empty));
            },
            isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

        var artifact = new ArtifactConfig(
            "docker",
            "sample",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "image": "ghcr.io/acme/widget",
                  "runner": "buildx",
                  "platform": "linux/amd64",
                  "buildTarget": "publish",
                  "buildOutput": "type=local,dest=./out",
                  "buildArgs": {
                    "FROM_CONFIG": "yes"
                  },
                  "secrets": {
                    "npm_token": { "env": "NPM_TOKEN" },
                    "cert": { "file": "./cert.pem" }
                  },
                  "tags": ["semver", "major"]
                }
                """)!);

        var context = new ExecutionContext(
            RepositoryRoot: Path.GetTempPath(),
            Branch: "main",
            CommitSha: "abcdef123456",
            Values: new Dictionary<string, object?>())
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
            {
                DockerVersion = "1.2.3",
            },
            ShortSha = "abcdef",
        };

        var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(invocations);
        Assert.Equal(
            [
                "buildx",
                "build",
                "--progress",
                "plain",
                "-f",
                "Dockerfile",
                "--platform",
                "linux/amd64",
                "--target",
                "publish",
                "--output",
                "type=local,dest=./out",
                "--secret",
                "id=npm_token,env=NPM_TOKEN",
                "--secret",
                "id=cert,src=./cert.pem",
                "--build-arg",
                "FROM_CONFIG=yes",
                "--build-arg",
                "APP_VERSION=1.2.3",
                "-t",
                "ghcr.io/acme/widget:1.2.3",
                "-t",
                "ghcr.io/acme/widget:1",
                "."
            ],
            invocations[0].Arguments);
    }

    [Fact]
    public async Task BuildAsyncUsesDockerSecretEnvOverride()
    {
        const string secretEnvName = "DOCKER_SECRET_API";
        var original = Environment.GetEnvironmentVariable(secretEnvName);
        Environment.SetEnvironmentVariable(secretEnvName, "present");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "sample",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """
                    {
                      "image": "ghcr.io/acme/widget",
                      "runner": "buildx",
                      "secrets": {
                        "api": { "env": "IGNORED" }
                      }
                    }
                    """)!);

            var context = ExecutionContext.Empty(Path.GetTempPath()) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
            };

            var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("id=api,env=DOCKER_SECRET_API", invocations[0].Arguments);
            Assert.DoesNotContain("id=api,env=IGNORED", invocations[0].Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretEnvName, original);
        }
    }

    [Fact]
    public async Task BuildAsyncKeepsFullPrereleaseTagAndNormalizesShorthandTags()
    {
        var invocations = new List<DockerInvocation>();
        var provider = new DockerArtifactProvider(
            runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
            {
                invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                return Task.FromResult((0, string.Empty));
            },
            isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

        var artifact = new ArtifactConfig(
            "docker",
            "sample",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "image": "ghcr.io/acme/widget",
                  "tags": ["full", "majorMinor", "major"]
                }
                """)!);

        var context = new ExecutionContext(
            RepositoryRoot: Path.GetTempPath(),
            Branch: "feature/prerelease-tags",
            CommitSha: "abcdef123456",
            Values: new Dictionary<string, object?>())
        {
            Version = new VersionResult("0.1.322-qa.5", 0, 1, 322, "qa.5", "abcdef123456", "abcdef", true, false)
            {
                DockerVersion = "0.1.322-qa.5",
            },
            ShortSha = "abcdef",
        };

        var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(invocations);
        Assert.Contains("ghcr.io/acme/widget:0.1.322-qa.5", invocations[0].Arguments);
        Assert.Contains("ghcr.io/acme/widget:0.1-qa", invocations[0].Arguments);
        Assert.Contains("ghcr.io/acme/widget:0-qa", invocations[0].Arguments);
    }

    [Fact]
    public async Task BuildAsyncDefersCleanupUntilAfterPushWhenPushIsAllowed()
    {
        var invocations = new List<DockerInvocation>();

        var provider = new DockerArtifactProvider(
            runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
            {
                var arguments = args.ToArray();
                invocations.Add(new DockerInvocation(arguments, envOverrides, standardInput));
                return Task.FromResult((0, string.Empty));
            },
            isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

        var artifact = new ArtifactConfig(
            "docker",
            "sample",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "image": "ghcr.io/acme/widget",
                  "cleanup": { "local": true }
                }
                """)!);

        var context = new ExecutionContext(
            RepositoryRoot: Path.GetTempPath(),
            Branch: "main",
            CommitSha: "abcdef123456",
            Values: new Dictionary<string, object?>())
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
            {
                DockerVersion = "1.2.3",
            },
            ShortSha = "abcdef",
        };

        var buildResult = await provider.BuildAsync(artifact, context, CancellationToken.None);
        var pushResult = await provider.PushAsync(artifact, context, CancellationToken.None);

        Assert.True(buildResult.Success);
        Assert.True(pushResult.Success);
        Assert.Contains(invocations, invocation => invocation.Arguments.Count >= 2 && invocation.Arguments[0] == "build");
        Assert.Contains(invocations, invocation => invocation.Arguments.Count >= 2 && invocation.Arguments[0] == "push");
        Assert.Contains(invocations, invocation => invocation.Arguments.Count >= 3 && invocation.Arguments[0] == "image" && invocation.Arguments[1] == "rm");
        Assert.Equal("image", invocations[^1].Arguments[0]);
    }

    [Fact]
    public async Task BuildAsyncFailsWhenSecretsRequireBuildx()
    {
        var provider = new DockerArtifactProvider(
            runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                Task.FromResult((0, string.Empty)),
            isBuildxAvailableAsync: (_, _, _) => Task.FromResult(false));

        var artifact = new ArtifactConfig(
            "docker",
            "sample",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "image": "ghcr.io/acme/widget",
                  "runner": "build",
                  "secrets": {
                    "npm_token": { "env": "NPM_TOKEN" }
                  }
                }
                """)!);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
        };

        var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BuildAsyncUsesTargetFromRexoDotEnv()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-docker-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(repoRoot, ".rexo", ".env"),
            "DOCKER_TARGET_REGISTRY=ghcr.io\nDOCKER_TARGET_REPOSITORY=acme/service\n");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "fallback-name",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}")!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
                {
                    DockerVersion = "1.2.3",
                },
            };

            var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(invocations);
            Assert.Contains("ghcr.io/acme/service:1.2.3", invocations[0].Arguments);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Fact]
    public async Task TagAsyncUsesTargetFromRexoDotEnv()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-docker-tag-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(repoRoot, ".rexo", ".env"),
            "DOCKER_TARGET_REGISTRY=ghcr.io\nDOCKER_TARGET_REPOSITORY=acme/service\n");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "fallback-name",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}")!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
                {
                    DockerVersion = "1.2.3",
                },
            };

            var result = await provider.TagAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(2, invocations.Count);
            Assert.All(invocations, invocation => Assert.Equal("tag", invocation.Arguments[0]));
            Assert.All(invocations, invocation => Assert.Equal("ghcr.io/acme/service:1.2.3", invocation.Arguments[1]));
            Assert.Contains(invocations, invocation => invocation.Arguments[2] == "ghcr.io/acme/service:1.2");
            Assert.Contains(invocations, invocation => invocation.Arguments[2] == "ghcr.io/acme/service:1");
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Fact]
    public async Task BuildAsyncNormalizesTargetRegistryWithScheme()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-docker-env-scheme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(Path.Combine(repoRoot, ".rexo", ".env"),
            "DOCKER_TARGET_REGISTRY=https://ghcr.io\nDOCKER_TARGET_REPOSITORY=acme/service\n");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "fallback-name",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}")!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
                {
                    DockerVersion = "1.2.3",
                },
            };

            var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(invocations);
            Assert.Contains("ghcr.io/acme/service:1.2.3", invocations[0].Arguments);
            Assert.DoesNotContain("https://ghcr.io/acme/service:1.2.3", invocations[0].Arguments);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Fact]
    public async Task BuildAsyncInfersGhcrImageWhenRegistryNotSpecifiedInGitHubActions()
    {
        using var _ = CiEnvironmentVariableScope.CreateIsolatedCiScope();

        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-docker-ghcr-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "GITHUB_ACTIONS=true\nGITHUB_REPOSITORY=agile-north/rexo\nGITHUB_ACTOR=copilot\nGITHUB_TOKEN=gh-token\n");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "cli",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}")!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
                {
                    DockerVersion = "1.2.3",
                },
            };

            var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(2, invocations.Count);
            Assert.Equal(["login", "ghcr.io", "--username", "copilot", "--password-stdin"], invocations[0].Arguments);
            Assert.Equal("gh-token" + Environment.NewLine, invocations[0].StandardInput);
            Assert.Contains("ghcr.io/agile-north/rexo/cli:1.2.3", invocations[1].Arguments);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Fact]
    public async Task BuildAsyncDoesNotInferCiImageWhenCiInferenceDisabled()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-docker-ghcr-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "GITHUB_ACTIONS=true\nGITHUB_REPOSITORY=agile-north/rexo\nGITHUB_ACTOR=copilot\nGITHUB_TOKEN=gh-token\n");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "cli",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """
                    {
                      "ciInference": false
                    }
                    """)!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
                {
                    DockerVersion = "1.2.3",
                },
            };

            var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(invocations);
            Assert.DoesNotContain("login", invocations[0].Arguments);
            Assert.Contains("cli:1.2.3", invocations[0].Arguments);
            Assert.DoesNotContain("ghcr.io/agile-north/rexo/cli:1.2.3", invocations[0].Arguments);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Fact]
    public async Task BuildAsyncInfersGhcrRepositoryFromGitHubActionsContext()
    {
        using var _ = CiEnvironmentVariableScope.CreateIsolatedCiScope();

        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-docker-ghcr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "DOCKER_TARGET_REGISTRY=ghcr.io\nGITHUB_ACTIONS=true\nGITHUB_REPOSITORY=agile-north/rexo\nGITHUB_ACTOR=copilot\nGITHUB_TOKEN=gh-token\n");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "cli",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}")!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
                {
                    DockerVersion = "1.2.3",
                },
            };

            var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(2, invocations.Count);
            Assert.Equal(["login", "ghcr.io", "--username", "copilot", "--password-stdin"], invocations[0].Arguments);
            Assert.Equal("gh-token" + Environment.NewLine, invocations[0].StandardInput);
            Assert.Contains("ghcr.io/agile-north/rexo/cli:1.2.3", invocations[1].Arguments);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    [Fact]
    public async Task PushAsyncUsesGitLabCiRegistryDefaultsWhenImageIsImplicit()
    {
        using var _ = CiEnvironmentVariableScope.CreateIsolatedCiScope();

        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-docker-gitlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "GITLAB_CI=true\nCI_REGISTRY=registry.gitlab.example.com:5050\nCI_PROJECT_PATH=team/rexo\nCI_REGISTRY_USER=gitlab-ci-token\nCI_JOB_TOKEN=gl-token\n");

        try
        {
            var invocations = new List<DockerInvocation>();
            var provider = new DockerArtifactProvider(
                runDockerAsync: (args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new DockerInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                },
                isBuildxAvailableAsync: (_, _, _) => Task.FromResult(true));

            var artifact = new ArtifactConfig(
                "docker",
                "cli",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{}")!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Branch = "main",
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true)
                {
                    DockerVersion = "1.2.3",
                },
            };

            var result = await provider.PushAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(["login", "registry.gitlab.example.com:5050", "--username", "gitlab-ci-token", "--password-stdin"], invocations[0].Arguments);
            Assert.Equal("gl-token" + Environment.NewLine, invocations[0].StandardInput);
            Assert.Contains(invocations, invocation => invocation.Arguments.SequenceEqual(["push", "registry.gitlab.example.com:5050/team/rexo/cli:1.2.3"]));
            Assert.Contains("registry.gitlab.example.com:5050/team/rexo/cli:1.2.3", result.PublishedReferences);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    private sealed record DockerInvocation(
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string?>? Environment,
        string? StandardInput);
}
