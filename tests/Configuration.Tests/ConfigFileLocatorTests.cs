namespace Rexo.Configuration.Tests;

using Rexo.Configuration;

public sealed class ConfigFileLocatorTests
{
    [Fact]
    public void FindConfigPathPrefersRexoOverRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var repoPath = Path.Combine(dir, "repo.json");
            var rexoPath = Path.Combine(dir, "rexo.json");
            File.WriteAllText(repoPath, "{}");
            File.WriteAllText(rexoPath, "{}");

            var found = ConfigFileLocator.FindConfigPath(dir);

            Assert.Equal(rexoPath, found);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindConfigPathFindsDotRexoLocation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-locator-{Guid.NewGuid():N}");
        var hiddenDir = Path.Combine(dir, ".rexo");
        Directory.CreateDirectory(hiddenDir);
        try
        {
            var rexoPath = Path.Combine(hiddenDir, "rexo.json");
            File.WriteAllText(rexoPath, "{}");

            var found = ConfigFileLocator.FindConfigPath(dir);

            Assert.Equal(rexoPath, found);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindConfigPathPrefersDotRexoOverRootAndYamlOverJsonWithinLocation()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rexo-locator-{Guid.NewGuid():N}");
        var hiddenDir = Path.Join(dir, ".rexo");
        Directory.CreateDirectory(hiddenDir);
        try
        {
            var rootJson = Path.Join(dir, "rexo.json");
            var rootYaml = Path.Join(dir, "rexo.yaml");
            var dotRexoJson = Path.Join(hiddenDir, "rexo.json");
            var dotRexoYaml = Path.Join(hiddenDir, "rexo.yaml");
            File.WriteAllText(rootJson, "{}");
            File.WriteAllText(rootYaml, "{}");
            File.WriteAllText(dotRexoJson, "{}");

            Assert.Equal(dotRexoJson, ConfigFileLocator.FindConfigPath(dir));

            File.WriteAllText(dotRexoYaml, "{}");
            Assert.Equal(dotRexoYaml, ConfigFileLocator.FindConfigPath(dir));

            var warnings = ConfigFileLocator.GetShadowedFileWarnings(dir);
            var warning = Assert.Single(warnings);
            Assert.Contains(Path.Join(".rexo", "rexo.yaml"), warning, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(".rexo", "rexo.json"), warning, StringComparison.Ordinal);
            Assert.Contains("rexo.json", warning, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindConfigPathPrefersRootYamlOverRootJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "rexo.json"), "{}");
            var ymlPath = Path.Combine(dir, "rexo.yml");
            File.WriteAllText(ymlPath, "{}");

            Assert.Equal(ymlPath, ConfigFileLocator.FindConfigPath(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetShadowedFileWarningsIsEmptyForSingleConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "rexo.yaml"), "{}");
            Assert.Empty(ConfigFileLocator.GetShadowedFileWarnings(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetDefaultConfigPathIsDotRexoYaml()
    {
        var dir = Path.GetTempPath();
        Assert.Equal(Path.Combine(dir, ".rexo", "rexo.yaml"), ConfigFileLocator.GetDefaultConfigPath(dir));
    }

    [Fact]
    public void FindPolicyPathPrefersDotRexoThenDotRepoFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rexo-policy-locator-{Guid.NewGuid():N}");
        var dotRexo = Path.Combine(dir, ".rexo");
        var dotRepo = Path.Combine(dir, ".repo");
        Directory.CreateDirectory(dotRexo);
        Directory.CreateDirectory(dotRepo);
        try
        {
            var dotRepoPolicy = Path.Combine(dotRepo, "policy.json");
            var dotRexoPolicy = Path.Combine(dotRexo, "policy.json");
            File.WriteAllText(dotRepoPolicy, "{}");
            File.WriteAllText(dotRexoPolicy, "{}");

            var found = ConfigFileLocator.FindPolicyPath(dir);

            Assert.Equal(dotRexoPolicy, found);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
