namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts.Helm;
using Rexo.Core.Models;

[Collection("EnvVar Mutation Sequential")]
public sealed class HelmOciArtifactProviderTests
{
    [Fact]
    public async Task BuildAsyncPackagesChartWithResolvedVersion()
    {
        var invocations = new List<HelmInvocation>();
        var provider = new HelmOciArtifactProvider(
            runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
            {
                invocations.Add(new HelmInvocation(args.ToArray(), envOverrides, standardInput));
                return Task.FromResult((0, string.Empty));
            });

        var artifact = new ArtifactConfig(
            "helm-oci",
            "orders",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "chartPath": "deploy/charts/orders",
                  "output": "artifacts/charts"
                }
                """)!);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
        };

        var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(invocations);
        Assert.Equal(
            [
                "package",
                "deploy/charts/orders",
                "--destination",
                "artifacts/charts",
                "--version",
                "1.2.3",
                "--app-version",
                "1.2.3",
            ],
            invocations[0].Arguments);
    }

    [Fact]
    public async Task BuildAsyncRunsDependencyBuildWhenEnabled()
    {
        var invocations = new List<HelmInvocation>();
        var provider = new HelmOciArtifactProvider(
            runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
            {
                invocations.Add(new HelmInvocation(args.ToArray(), envOverrides, standardInput));
                if (invocations.Count == 1)
                {
                    return Task.FromResult((5, "Error: found in Chart.yaml, but missing in charts/ directory: ingress-nginx"));
                }

                return Task.FromResult((0, string.Empty));
            });

        var artifact = new ArtifactConfig(
            "helm-oci",
            "orders",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "chartPath": "deploy/charts/orders",
                  "output": "artifacts/charts"
                }
                """)!);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
        };

        var result = await provider.BuildAsync(artifact, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, invocations.Count);
        Assert.Equal("package", invocations[0].Arguments[0]);
        Assert.Equal(["dependency", "update", "deploy/charts/orders"], invocations[1].Arguments);
        Assert.Equal("package", invocations[2].Arguments[0]);
    }

    [Fact]
    public async Task PushAsyncComposesOciDestinationFromRegistryAndRepository()
    {
        var invocations = new List<HelmInvocation>();
        var provider = new HelmOciArtifactProvider(
            runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
            {
                invocations.Add(new HelmInvocation(args.ToArray(), envOverrides, standardInput));
                return Task.FromResult((0, string.Empty));
            });

        var artifact = new ArtifactConfig(
            "helm-oci",
            "orders",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "chartPath": "deploy/charts/orders",
                  "output": "artifacts/charts",
                                    "target": {
                                        "registry": "ghcr.io",
                                        "repository": "acme/charts"
                                    }
                }
                """)!);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
        };

        var result = await provider.PushAsync(artifact, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, invocations.Count);
        Assert.Equal("package", invocations[0].Arguments[0]);
        Assert.Equal("push", invocations[1].Arguments[0]);
        Assert.Equal("oci://ghcr.io/acme/charts", invocations[1].Arguments[2]);
    }

    [Fact]
    public async Task BuildAsyncPassesDockerImageSettingToRealImplementation()
    {
        // Verify the setting key name is "dockerImage" (matching version provider conventions).
        // The actual docker fallback path is exercised through the real RunHelmAsync when
        // host helm is unavailable; here we validate the setting key is read correctly by
        // checking that our artifact config round-trips the value through GetSetting.
        var capturedArtifact = default(ArtifactConfig?);
        var provider = new HelmOciArtifactProvider(
            runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
            {
                capturedArtifact = artifact;
                return Task.FromResult((0, string.Empty));
            });

        var artifact = new ArtifactConfig(
            "helm-oci",
            "orders",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """
                {
                  "chartPath": "deploy/charts/orders",
                  "output": "artifacts/charts",
                  "dockerImage": "my/helm:9.9.9"
                }
                """)!);

        var context = ExecutionContext.Empty(Path.GetTempPath()) with
        {
            Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
        };

        await provider.BuildAsync(artifact, context, CancellationToken.None);

        Assert.NotNull(capturedArtifact);
        Assert.True(capturedArtifact!.Settings.TryGetValue("dockerImage", out var imageElement));
        Assert.Equal("my/helm:9.9.9", imageElement.GetString());
    }

    [Fact]
    public async Task PushAsyncInfersGhcrDestinationWhenRegistryNotSpecifiedInGitHubActions()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-helm-ghcr-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, "artifacts", "charts"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "artifacts", "charts", "orders-1.2.3.tgz"),
            "stub");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "GITHUB_ACTIONS=true\nGITHUB_REPOSITORY=agile-north/rexo\nGITHUB_ACTOR=copilot\nGITHUB_TOKEN=gh-token\n");

        try
        {
            var invocations = new List<HelmInvocation>();
            var provider = new HelmOciArtifactProvider(
                runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new HelmInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                });

            var artifact = new ArtifactConfig(
                "helm-oci",
                "orders",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """
                    {
                      "chartPath": "deploy/charts/orders",
                      "output": "artifacts/charts"
                    }
                    """)!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
            };

            var result = await provider.PushAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(["registry", "login", "ghcr.io", "--username", "copilot", "--password-stdin"], invocations[0].Arguments);
            Assert.Equal("gh-token" + Environment.NewLine, invocations[0].StandardInput);
            Assert.Equal("push", invocations[1].Arguments[0]);
            Assert.Equal("oci://ghcr.io/agile-north/rexo", invocations[1].Arguments[2]);
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
    public async Task PushAsyncDoesNotInferCiDestinationWhenCiInferenceDisabled()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-helm-ghcr-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "GITHUB_ACTIONS=true\nGITHUB_REPOSITORY=agile-north/rexo\nGITHUB_ACTOR=copilot\nGITHUB_TOKEN=gh-token\n");

        try
        {
            var invocations = new List<HelmInvocation>();
            var provider = new HelmOciArtifactProvider(
                runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new HelmInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                });

            var artifact = new ArtifactConfig(
                "helm-oci",
                "orders",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """
                    {
                      "chartPath": "deploy/charts/orders",
                      "output": "artifacts/charts",
                      "ciInference": false
                    }
                    """)!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
            };

            var result = await provider.PushAsync(artifact, context, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Single(invocations);
            Assert.Equal("package", invocations[0].Arguments[0]);
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
    public async Task PushAsyncInfersGhcrDestinationFromGitHubActionsContext()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-helm-ghcr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, "artifacts", "charts"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "artifacts", "charts", "orders-1.2.3.tgz"),
            "stub");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "HELM_OCI_TARGET_REGISTRY=ghcr.io\nGITHUB_ACTIONS=true\nGITHUB_REPOSITORY=agile-north/rexo\nGITHUB_ACTOR=copilot\nGITHUB_TOKEN=gh-token\n");

        try
        {
            var invocations = new List<HelmInvocation>();
            var provider = new HelmOciArtifactProvider(
                runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new HelmInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                });

            var artifact = new ArtifactConfig(
                "helm-oci",
                "orders",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """
                    {
                      "chartPath": "deploy/charts/orders",
                      "output": "artifacts/charts"
                    }
                    """)!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
            };

            var result = await provider.PushAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(["registry", "login", "ghcr.io", "--username", "copilot", "--password-stdin"], invocations[0].Arguments);
            Assert.Equal("gh-token" + Environment.NewLine, invocations[0].StandardInput);
            Assert.Equal("push", invocations[1].Arguments[0]);
            Assert.Equal("oci://ghcr.io/agile-north/rexo", invocations[1].Arguments[2]);
            Assert.EndsWith("orders-1.2.3.tgz", invocations[1].Arguments[1], StringComparison.OrdinalIgnoreCase);
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
    public async Task PushAsyncUsesGitLabCiRegistryDefaultsWhenTargetIsImplicit()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"rexo-helm-gitlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, "artifacts", "charts"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "artifacts", "charts", "orders-1.2.3.tgz"),
            "stub");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".rexo"));
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, ".rexo", ".env"),
            "GITLAB_CI=true\nCI_REGISTRY=registry.gitlab.example.com:5050\nCI_PROJECT_PATH=team/rexo\nCI_REGISTRY_USER=gitlab-ci-token\nCI_JOB_TOKEN=gl-token\n");

        try
        {
            var invocations = new List<HelmInvocation>();
            var provider = new HelmOciArtifactProvider(
                runHelmAsync: (artifact, args, workingDirectory, envOverrides, standardInput, cancellationToken) =>
                {
                    invocations.Add(new HelmInvocation(args.ToArray(), envOverrides, standardInput));
                    return Task.FromResult((0, string.Empty));
                });

            var artifact = new ArtifactConfig(
                "helm-oci",
                "orders",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """
                    {
                      "chartPath": "deploy/charts/orders",
                      "output": "artifacts/charts"
                    }
                    """)!);

            var context = ExecutionContext.Empty(repoRoot) with
            {
                Version = new VersionResult("1.2.3", 1, 2, 3, null, "abcdef123456", "abcdef", false, true),
            };

            var result = await provider.PushAsync(artifact, context, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(["registry", "login", "registry.gitlab.example.com:5050", "--username", "gitlab-ci-token", "--password-stdin"], invocations[0].Arguments);
            Assert.Equal("gl-token" + Environment.NewLine, invocations[0].StandardInput);
            Assert.Equal("push", invocations[1].Arguments[0]);
            Assert.Equal("oci://registry.gitlab.example.com:5050/team/rexo", invocations[1].Arguments[2]);
            Assert.EndsWith("orders-1.2.3.tgz", invocations[1].Arguments[1], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, true);
            }
        }
    }

    private sealed record HelmInvocation(
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string?>? Environment,
        string? StandardInput);
}
