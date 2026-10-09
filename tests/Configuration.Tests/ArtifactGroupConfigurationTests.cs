namespace Rexo.Configuration.Tests;

using Rexo.Configuration;
using Rexo.Configuration.Models;

public sealed class ArtifactGroupConfigurationTests
{
    [Theory]
    [InlineData("docker")]
    [InlineData("nuget")]
    [InlineData("helm-oci")]
    [InlineData("npm")]
    [InlineData("pypi")]
    [InlineData("maven")]
    [InlineData("gradle")]
    [InlineData("rubygems")]
    [InlineData("terraform")]
    [InlineData("helm")]
    [InlineData("docker-compose")]
    [InlineData("generic")]
    public async Task ArtifactProvidersAcceptLifecycleGroups(string type)
    {
        var config = await LoadAsync(type, "contracts");

        Assert.Equal("contracts", Assert.Single(config.Artifacts!).Group);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("contracts/api")]
    [InlineData("default")]
    [InlineData("Default")]
    public async Task ArtifactGroupsRejectBlankMalformedAndReservedNames(string group)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LoadAsync("generic", group));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GroupLessArtifactsRemainValid()
    {
        var config = await LoadAsync("generic", null);

        Assert.Null(Assert.Single(config.Artifacts!).Group);
    }

    private static async Task<RepoConfig> LoadAsync(string type, string? group)
    {
        var directory = Path.Join(Path.GetTempPath(), $"rexo-artifact-group-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "rexo.json");
        var artifact = group is null
            ? $$"""{"type":"{{type}}"}"""
            : $$"""{"type":"{{type}}","group":"{{group}}"}""";
        var configJson = $$"""
            {
              "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "artifact-groups",
              "artifacts": [{{artifact}}]
            }
            """;

        try
        {
            await File.WriteAllTextAsync(path, configJson);
            return await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
