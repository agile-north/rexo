namespace Rexo.Configuration.Tests;

using System.Text.Json;
using Rexo.Configuration;
using Rexo.Configuration.Models;

public sealed class ContainerResolverTests
{
    [Fact]
    public void ResolveDefaultsFallbackToError()
    {
        var resolved = ContainerResolver.Resolve(
            new RepoStepContainerConfig(Image: "mcr.microsoft.com/dotnet/sdk:10.0"),
            registry: null,
            static name => name);

        Assert.NotNull(resolved);
        Assert.Equal("error", resolved.Fallback);
    }

    [Fact]
    public void ResolveInheritsFallbackAndAllowsInlineOverride()
    {
        var registry = new Dictionary<string, RepoStepContainerConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["sdk"] = new(Image: "mcr.microsoft.com/dotnet/sdk:10.0") { Fallback = "error" },
            ["sdk-local"] = new() { Extends = "sdk", Fallback = "host" },
        };

        var inherited = ContainerResolver.Resolve(
            new RepoStepContainerConfig { Use = "sdk-local" },
            registry,
            static name => name);
        var overridden = ContainerResolver.Resolve(
            new RepoStepContainerConfig { Use = "sdk", Fallback = "host" },
            registry,
            static name => name);

        Assert.Equal("host", inherited?.Fallback);
        Assert.Equal("host", overridden?.Fallback);
    }

    [Fact]
    public void ResolveRejectsUnsupportedFallbackPolicy()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ContainerResolver.Resolve(
                new RepoStepContainerConfig(Image: "mcr.microsoft.com/dotnet/sdk:10.0")
                {
                    Fallback = "ignore",
                },
                registry: null,
                static name => name));

        Assert.Contains("Supported values are 'error' and 'host'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerJsonConverterReadsAndWritesFallback()
    {
        var container = JsonSerializer.Deserialize<RepoStepContainerConfig>(
            """{"image":"mcr.microsoft.com/dotnet/sdk:10.0","fallback":"host"}""");

        Assert.NotNull(container);
        Assert.Equal("host", container.Fallback);
        Assert.Contains(
            "\"fallback\":\"host\"",
            JsonSerializer.Serialize(container),
            StringComparison.Ordinal);
    }
}
