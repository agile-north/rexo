namespace Rexo.Execution.Tests;

using System.IO.Compression;
using System.Text.Json;
using Rexo.Core.Models;

public sealed class ReleaseEvidenceValidatorTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("stale")]
    [InlineData("missing-report")]
    [InlineData("missing-entry")]
    [InlineData("tokens")]
    [InlineData("failure")]
    public async Task ConfiguredEvidenceIsValidated(string scenario)
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(root, "tests"));
        try
        {
            var started = DateTimeOffset.UtcNow.AddMinutes(-1);
            await File.WriteAllTextAsync(Path.Join(root, "run.json"),
                JsonSerializer.Serialize(new RunManifest { Success = true, StartedAt = started }));
            await File.WriteAllTextAsync(Path.Join(root, "result.json"),
                JsonSerializer.Serialize(new { Success = scenario != "failure", ExitCode = 0 }));
            await File.WriteAllTextAsync(Path.Join(root, "tests", "results.trx"), "test evidence");
            if (scenario != "missing-report")
            {
                await File.WriteAllTextAsync(Path.Join(root, "tests", "coverage.cobertura.xml"), "coverage evidence");
            }
            await File.WriteAllTextAsync(Path.Join(root, "analysis.sarif"), "analysis evidence");
            var package = Path.Join(root, "package.zip");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(scenario == "missing-entry" ? "other.md" : "README.md");
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync(scenario == "tokens" ? "{{version}}" : "resolved documentation");
            }
            if (scenario == "stale")
            {
                File.SetLastWriteTimeUtc(package, started.UtcDateTime.AddMinutes(-1));
            }
            var inputs = new Dictionary<string, string>
            {
                ["runManifest"] = "run.json",
                ["result"] = "result.json",
                ["tests"] = "tests",
                ["sarif"] = "analysis.sarif",
                ["archive"] = "package.zip",
                ["entry"] = "README.md",
            };
            if (scenario == "valid")
            {
                await ReleaseEvidenceValidator.ValidateAsync(root, inputs, CancellationToken.None);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ReleaseEvidenceValidator.ValidateAsync(root, inputs, CancellationToken.None));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
