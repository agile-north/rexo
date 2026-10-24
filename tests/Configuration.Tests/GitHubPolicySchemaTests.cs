namespace Rexo.Configuration.Tests;

using System.Text.Json;
using Rexo.Policies;

public sealed class GitHubPolicySchemaTests
{
    [Theory]
    [InlineData("githubTests", """{"checks":"yes"}""")]
    [InlineData("githubTests", """{"annotationLimit":1001}""")]
    [InlineData("githubTests", """{"publish":"sometimes"}""")]
    [InlineData("githubTests", """{"artifactUpload":"mandatory"}""")]
    [InlineData("githubCoverage", """{"minimumLineCoverage":101}""")]
    [InlineData("githubCoverage", """{"retentionDays":0}""")]
    [InlineData("githubCoverage", """{"minimumLineCoverge":80}""")]
    [InlineData("githubRelease", """{"artifactGate":"every"}""")]
    [InlineData("githubRelease", """{"generateNote":true}""")]
    public async Task InvalidGitHubPolicyOverridesFailSchemaValidation(string section, string settings)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(section, settings));
        Assert.Contains("Validation errors", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("githubTests", """{"checks":"{{env.PUBLISH_CHECKS}}","retentionDays":"{{vars.retention}}"}""")]
    [InlineData("githubCoverage", """{"minimumLineCoverage":"80.5","minimumBranchCoverage":"{{env.COVERAGE_MIN}}"}""")]
    [InlineData("githubRelease", """{"enabled":"{{env.RELEASE_ENABLED}}","prerelease":true}""")]
    public async Task TemplatesAndSupportedLiteralsRemainValid(string section, string settings)
    {
        var config = await LoadAsync(section, settings);
        Assert.True(config.Vars!.ContainsKey(section));
    }

    [Theory]
    [InlineData("github-tests")]
    [InlineData("github-coverage")]
    [InlineData("github-release")]
    public async Task EveryEmbeddedPolicyDefaultPassesTheSchema(string policy)
    {
        using var json = JsonDocument.Parse(EmbeddedPolicyTemplates.ReadTemplate(policy));
        var vars = json.RootElement.GetProperty("vars");
        var section = Assert.Single(vars.EnumerateObject());
        var config = await LoadAsync(section.Name, section.Value.GetRawText());
        Assert.True(config.Vars!.ContainsKey(section.Name));
    }

    private static async Task<Models.RepoConfig> LoadAsync(string section, string settings)
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-github-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Join(root, "rexo.json");
            await File.WriteAllTextAsync(path, $$"""
                {
                  "$schema":"rexo.schema.json","schemaVersion":"1.0","name":"schema-test",
                  "vars":{ "{{section}}": {{settings}} }
                }
                """);
            return await RepoConfigurationLoader.LoadAsync(path, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
