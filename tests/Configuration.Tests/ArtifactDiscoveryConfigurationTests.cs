namespace Rexo.Configuration.Tests;

using Rexo.Configuration;
using Rexo.Configuration.Models;

public sealed class ArtifactDiscoveryConfigurationTests
{
    [Fact]
    public async Task DiscoveryLoadsWithoutExpandingProjects()
    {
        var config = await LoadAsync("""
            {"type":"nuget","group":"contracts","discover":{"include":["**/*Contracts.csproj"],"exclude":["samples/**"]},
             "settings":{"target":{"source":"https://api.nuget.org/v3/index.json"}}}
            """);
        var artifact = Assert.Single(config.Artifacts!);
        Assert.Equal("contracts", artifact.Group);
        Assert.Null(artifact.Name);
        Assert.Equal("**/*Contracts.csproj", Assert.Single(artifact.Discover!.Include));
        Assert.Equal("samples/**", Assert.Single(artifact.Discover.Exclude!));
    }

    [Theory]
    [InlineData("""{"type":"nuget","discover":{"include":[]}}""")]
    [InlineData("""{"type":"nuget","discover":{"exclude":["tests/**"]}}""")]
    [InlineData("""{"type":"nuget","discover":{"include":[""]}}""")]
    [InlineData("""{"type":"nuget","name":"Explicit","discover":{"include":["**/*.csproj"]}}""")]
    [InlineData("""{"type":"nuget","settings":{"project":"One.csproj"},"discover":{"include":["**/*.csproj"]}}""")]
    [InlineData("""{"type":"docker","discover":{"include":["**/Dockerfile"]}}""")]
    [InlineData("""{"type":"generic","discover":{"include":["dist/**"]}}""")]
    [InlineData("""{"type":"nuget","discover":{"include":["**/*.csproj"],"unknown":true}}""")]
    public async Task InvalidDiscoveryShapesFailSchemaValidation(string artifact)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(artifact));
        Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtendsPreservesDeclarativeDiscoveryAlongsideLiteralArtifacts()
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-discovery-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "base.json"), """
                {"$schema":"rexo.schema.json","schemaVersion":"1.0","name":"base",
                 "artifacts":[{"type":"nuget","discover":{"include":["**/*Contracts.csproj"]}}]}
                """);
            var path = Path.Join(root, "rexo.json");
            await File.WriteAllTextAsync(path, """
                {"$schema":"rexo.schema.json","schemaVersion":"1.0","name":"child","extends":["base.json"],
                 "artifacts":[{"type":"nuget","name":"Literal","settings":{"project":"Explicit.csproj"}}]}
                """);
            var config = await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
            Assert.Equal(2, config.Artifacts!.Count);
            Assert.NotNull(config.Artifacts[0].Discover);
            Assert.Equal("Literal", config.Artifacts[1].Name);
            Assert.Null(config.Artifacts[1].Discover);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<RepoConfig> LoadAsync(string artifact)
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-discovery-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Join(root, "rexo.json");
            await File.WriteAllTextAsync(path, $$"""
                {"$schema":"rexo.schema.json","schemaVersion":"1.0","name":"discovery","artifacts":[{{artifact}}]}
                """);
            return await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
