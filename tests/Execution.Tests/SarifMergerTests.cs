namespace Rexo.Execution.Tests;

using System.Text.Json.Nodes;

public sealed class SarifMergerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rexo-sarif-{Guid.NewGuid():N}");

    public SarifMergerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string FileUri(string relative) =>
        new Uri(Path.Combine(_root, relative)).AbsoluteUri;

    private string WriteLog(string name, string version, string ruleIds, string results)
    {
        var path = Path.Combine(_root, "in", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rules = string.Join(',', ruleIds.Split(',').Select(id => $"{{\"id\":\"{id}\"}}"));
        File.WriteAllText(path, $$"""
            {
              "$schema": "https://json.schemastore.org/sarif-{{version}}.json",
              "version": "{{version}}",
              "runs": [{
                "tool": { "driver": { "name": "Microsoft (R) Visual C# Compiler", "rules": [{{rules}}] } },
                "results": [{{results}}],
                "columnKind": "utf16CodeUnits"
              }]
            }
            """);
        return path;
    }

    private string Result(string ruleId, int ruleIndex, string relativeFile) =>
        $$"""
        { "ruleId": "{{ruleId}}", "ruleIndex": {{ruleIndex}}, "level": "warning",
          "message": { "text": "m" },
          "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{FileUri(relativeFile)}}" },
            "region": { "startLine": 1 } } }] }
        """;

    [Fact]
    public async Task MergesSameToolIntoSingleRunWithRemappedRuleIndices()
    {
        var a = WriteLog("A.sarif", "2.1.0", "CS0168,CA1822", Result("CA1822", 1, "A/W.cs") + "," + Result("CS0168", 0, "A/W.cs"));
        var b = WriteLog("B.sarif", "2.1.0", "CA1822,CS9999", Result("CS9999", 1, "B/W.cs") + "," + Result("CA1822", 0, "B/W.cs"));
        var output = Path.Combine(_root, "out", "merged.sarif");

        var result = await SarifMerger.MergeAsync([a, b], output, "dotnet-build", _root, CancellationToken.None);

        Assert.True(result.Written);
        Assert.Equal(1, result.Runs);
        Assert.Equal(4, result.Results);

        var doc = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Assert.Equal("2.1.0", doc["version"]!.GetValue<string>());
        var run = Assert.Single(doc["runs"]!.AsArray())!;
        var rules = run["tool"]!["driver"]!["rules"]!.AsArray();
        Assert.Equal(3, rules.Count);
        foreach (var r in run["results"]!.AsArray())
        {
            var idx = r!["ruleIndex"]!.GetValue<int>();
            Assert.Equal(r["ruleId"]!.GetValue<string>(), rules[idx]!["id"]!.GetValue<string>());
            var location = r["locations"]![0]!["physicalLocation"]!["artifactLocation"]!;
            Assert.Equal("%SRCROOT%", location["uriBaseId"]!.GetValue<string>());
            Assert.DoesNotContain("file:", location["uri"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Equal("dotnet-build/", run["automationDetails"]!["id"]!.GetValue<string>());
        Assert.NotNull(run["originalUriBaseIds"]?["%SRCROOT%"]);
    }

    [Fact]
    public async Task DeduplicatesIdenticalResults()
    {
        var a = WriteLog("A.net8.0.sarif", "2.1.0", "CS0168", Result("CS0168", 0, "Shared/W.cs"));
        var b = WriteLog("A.net10.0.sarif", "2.1.0", "CS0168", Result("CS0168", 0, "Shared/W.cs"));
        var output = Path.Combine(_root, "merged.sarif");

        var result = await SarifMerger.MergeAsync([a, b], output, null, _root, CancellationToken.None);

        Assert.Equal(1, result.Results);
    }

    [Fact]
    public async Task SkipsNon21InputsWithWarning()
    {
        var legacy = WriteLog("old.sarif", "1.0.0", "CS0168", Result("CS0168", 0, "A/W.cs"));
        var output = Path.Combine(_root, "merged.sarif");

        var result = await SarifMerger.MergeAsync([legacy], output, null, _root, CancellationToken.None);

        Assert.False(result.Written);
        Assert.NotEmpty(result.Warnings);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task NoInputsRemovesStaleOutput()
    {
        var output = Path.Combine(_root, "merged.sarif");
        await File.WriteAllTextAsync(output, "stale");

        var result = await SarifMerger.MergeAsync([], output, null, _root, CancellationToken.None);

        Assert.False(result.Written);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TargetsContentWritesPerProjectSarif21ErrorLog()
    {
        var content = SarifBuiltinModule.SarifTargetsContent;

        Assert.Contains("$(RexoSarifDirectory)", content, StringComparison.Ordinal);
        Assert.Contains("$(MSBuildProjectName)", content, StringComparison.Ordinal);
        Assert.Contains("$(TargetFramework)", content, StringComparison.Ordinal);
        Assert.Contains(",version=$(RexoSarifVersion)", content, StringComparison.Ordinal);
        Assert.Contains(">2.1</RexoSarifVersion>", content, StringComparison.Ordinal);
    }
}
