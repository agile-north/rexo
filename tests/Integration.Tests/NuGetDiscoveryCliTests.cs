namespace Rexo.Integration.Tests;

using System.Text.Json;
using Rexo.Cli;

[Collection("IntegrationSequential")]
public sealed class NuGetDiscoveryCliTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task YamlDiscoveryPlansSelectedGroupOrReturnsExplicitEmptyFailure(bool matchingProject)
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-discovery-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(root, ".rexo"));
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, ".rexo", "rexo.yaml"), """
                $schema: rexo.schema.json
                schemaVersion: "1.0"
                name: discovery-cli
                version: 1.2.3
                versioning:
                  provider: fixed
                  fallback: 1.2.3
                outputs:
                  emit: false
                artifacts:
                  - type: nuget
                    discover:
                      include: ["missing/**/*.csproj"]
                  - type: nuget
                    group: contracts
                    discover:
                      include: ["**/*Contracts.csproj"]
                commands:
                  inventory:
                    steps:
                      - id: version
                        uses: builtin:resolve-version
                      - id: plan
                        uses: builtin:plan-artifacts
                """);
            if (matchingProject)
            {
                await File.WriteAllTextAsync(Path.Join(root, "ExampleContracts.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                      <TargetFramework>net10.0</TargetFramework><PackageId>Acme.Contracts</PackageId>
                    </PropertyGroup></Project>
                    """);
            }
            Environment.CurrentDirectory = root;
            var path = Path.Join(root, "result.json");
            var exit = await Program.ExecuteAsync(
                ["--json-file", path, "inventory", "--artifact-group", "contracts"], CancellationToken.None);
            Assert.Equal(matchingProject ? 0 : 2, exit);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var step = result.RootElement.GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("StepId").GetString() == "plan");
            if (matchingProject)
            {
                using var plan = JsonDocument.Parse(step.GetProperty("Outputs").GetProperty("plan").GetString()!);
                Assert.Equal("contracts", plan.RootElement.GetProperty("ArtifactGroup").GetString());
                Assert.Equal("Acme.Contracts", Assert.Single(plan.RootElement.GetProperty("Artifacts").EnumerateArray()).GetProperty("Name").GetString());
            }
            else
            {
                Assert.Contains("no packable", step.GetProperty("Outputs").GetProperty("error").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, true);
        }
    }
}
