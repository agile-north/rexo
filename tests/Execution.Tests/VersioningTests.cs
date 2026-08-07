namespace Rexo.Execution.Tests;

using Rexo.Core.Models;
using Rexo.Versioning;

[Collection("EnvVar Mutation Sequential")]
public sealed class VersioningTests
{
    private static ExecutionContext MakeContext() =>
        ExecutionContext.Empty("C:\\repo");

    [Fact]
    public async Task FixedVersionProviderReturnsFallbackVersion()
    {
        var provider = new FixedVersionProvider();
        var config = new VersioningConfig(Provider: "fixed", Fallback: "3.1.0");

        var result = await provider.ResolveAsync(config, MakeContext(), CancellationToken.None);

        Assert.Equal("3.1.0", result.SemVer);
        Assert.Equal(3, result.Major);
        Assert.Equal(1, result.Minor);
        Assert.Equal(0, result.Patch);
    }

    [Fact]
    public async Task FixedVersionProviderDefaultsWhenNoFallback()
    {
        var provider = new FixedVersionProvider();
        var config = new VersioningConfig(Provider: "fixed");

        var result = await provider.ResolveAsync(config, MakeContext(), CancellationToken.None);

        // Should use 0.1.0 default
        Assert.NotNull(result.SemVer);
        Assert.False(string.IsNullOrEmpty(result.SemVer));
    }

    [Fact]
    public async Task GitTagVersionProviderFallsBackWhenNotAGitRepo()
    {
        var provider = new GitTagVersionProvider();
        var config = new VersioningConfig(Provider: "git", Fallback: "0.9.0");

        // Use a non-git-repo directory so git describe fails
        var ctx = ExecutionContext.Empty(System.IO.Path.GetTempPath());
        var result = await provider.ResolveAsync(config, ctx, CancellationToken.None);

        // Should return fallback gracefully
        Assert.NotNull(result.SemVer);
        Assert.False(string.IsNullOrEmpty(result.SemVer));
    }

    [Fact]
    public async Task EnvVersionProviderReadsValueFromRexoDotEnv()
    {
        const string envVar = "REXO_VERSION_FROM_FILE";
        var original = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, null);

        var dir = Path.Combine(Path.GetTempPath(), $"rexo-version-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".rexo"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, ".rexo", ".env"), $"{envVar}=9.8.7\n");

            var provider = new EnvVersionProvider();
            var config = new VersioningConfig(
                Provider: "env",
                Fallback: "0.1.0",
                Settings: new Dictionary<string, string> { ["variable"] = envVar });

            var result = await provider.ResolveAsync(config, ExecutionContext.Empty(dir), CancellationToken.None);

            Assert.Equal("9.8.7", result.SemVer);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void GitVersionProviderRendersTemplatedDockerImage()
    {
        var config = new VersioningConfig(
            Provider: "gitversion",
            Fallback: "1.0.0",
            Settings: new Dictionary<string, string>
            {
                ["dockerImage"] = "my/gitversion:{{args.channel}}",
            });

        var context = ExecutionContext.Empty("C:\\repo") with
        {
            Args = new Dictionary<string, string>
            {
                ["channel"] = "preview",
            },
        };

        var image = VersionProcessHelper.GetDockerImage(config, context, "gittools/gitversion:6.0.0");

        Assert.Equal("my/gitversion:preview", image);
    }

    [Fact]
    public void VersionProviderRegistryContainsGitProvider()
    {
        var registry = VersionProviderRegistry.CreateDefault();
        var provider = registry.Resolve("git");
        Assert.NotNull(provider);
    }

    [Fact]
    public void VersionProviderRegistryContainsFixedProvider()
    {
        var registry = VersionProviderRegistry.CreateDefault();
        var provider = registry.Resolve("fixed");
        Assert.NotNull(provider);
    }

    [Fact]
    public void VersionProviderRegistryContainsAutoProvider()
    {
        var registry = VersionProviderRegistry.CreateDefault();
        var provider = registry.Resolve("auto");
        Assert.IsType<AutoVersionProvider>(provider);
    }

    [Fact]
    public void AutoDetectReturnsFixedWhenNoEvidence()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-auto-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var detected = AutoVersionProvider.DetectProvider(dir);
            Assert.Equal("fixed", detected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AutoDetectReturnsGitWhenDotGitPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-auto-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            var detected = AutoVersionProvider.DetectProvider(dir);
            Assert.Equal("git", detected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AutoDetectReturnsNbgvWhenVersionJsonPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-auto-nbgv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "version.json"), "{}");
            var detected = AutoVersionProvider.DetectProvider(dir);
            Assert.Equal("nbgv", detected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AutoDetectReturnsGitVersionWhenGitVersionYmlPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-auto-gitversion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "GitVersion.yml"), "mode: ContinuousDelivery");
            var detected = AutoVersionProvider.DetectProvider(dir);
            Assert.Equal("gitversion", detected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AutoDetectReturnsMinVerWhenMinverrcPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-auto-minver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".minverrc"), "tag-prefix: v");
            var detected = AutoVersionProvider.DetectProvider(dir);
            Assert.Equal("minver", detected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AutoDetectPrefersNbgvOverGit()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-auto-priority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            // Both .git and version.json present — nbgv should win
            File.WriteAllText(Path.Combine(dir, "version.json"), "{}");
            var detected = AutoVersionProvider.DetectProvider(dir);
            Assert.Equal("nbgv", detected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AutoVersionProviderFallsBackToFixedWithNoEvidence()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-auto-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var provider = new AutoVersionProvider();
            var config = new VersioningConfig(Provider: "auto", Fallback: "5.6.7");
            var result = await provider.ResolveAsync(config, ExecutionContext.Empty(dir), CancellationToken.None);

            Assert.Equal("5.6.7", result.SemVer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
