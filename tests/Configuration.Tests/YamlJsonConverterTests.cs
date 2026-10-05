namespace Rexo.Configuration.Tests;

using System.Text.Json.Nodes;
using Rexo.Configuration;

public sealed class YamlJsonConverterTests
{
    [Fact]
    public void ToJsonNodeResolvesCoreSchemaScalars()
    {
        const string yaml = """
            int: 30
            negative: -5
            leadingZero: 01
            float: 1.5
            versionLike: 1.0.0
            boolTrue: true
            boolFalse: False
            nothing: null
            tilde: ~
            empty:
            quotedInt: "30"
            quotedBool: "true"
            singleQuoted: 'false'
            plain: hello world
            yes: yes
            hex: 0x1F
            octal: 0o17
            """;

        var node = Assert.IsType<JsonObject>(YamlJsonConverter.ToJsonNode(yaml));

        Assert.Equal(30, node["int"]!.GetValue<long>());
        Assert.Equal(-5, node["negative"]!.GetValue<long>());
        Assert.Equal("01", node["leadingZero"]!.GetValue<string>());
        Assert.Equal("1.5", node["float"]!.ToJsonString());
        Assert.Equal("1.0.0", node["versionLike"]!.GetValue<string>());
        Assert.True(node["boolTrue"]!.GetValue<bool>());
        Assert.False(node["boolFalse"]!.GetValue<bool>());
        Assert.Null(node["nothing"]);
        Assert.Null(node["tilde"]);
        Assert.Null(node["empty"]);
        Assert.Equal("30", node["quotedInt"]!.GetValue<string>());
        Assert.Equal("true", node["quotedBool"]!.GetValue<string>());
        Assert.Equal("false", node["singleQuoted"]!.GetValue<string>());
        Assert.Equal("hello world", node["plain"]!.GetValue<string>());
        Assert.Equal("yes", node["yes"]!.GetValue<string>());
        Assert.Equal(31, node["hex"]!.GetValue<long>());
        Assert.Equal(15, node["octal"]!.GetValue<long>());
    }

    [Fact]
    public void ToJsonNodeHonoursExplicitStringTag()
    {
        var node = Assert.IsType<JsonObject>(YamlJsonConverter.ToJsonNode("value: !!str 42"));
        Assert.Equal("42", node["value"]!.GetValue<string>());
    }

    [Fact]
    public void ToJsonNodeSupportsAnchorsAliasesAndMergeKeys()
    {
        const string yaml = """
            base: &base
              timeout: 10
              shell: bash
            derived:
              <<: *base
              shell: pwsh
            copy: *base
            """;

        var node = Assert.IsType<JsonObject>(YamlJsonConverter.ToJsonNode(yaml));

        Assert.Equal(10, node["derived"]!["timeout"]!.GetValue<long>());
        Assert.Equal("pwsh", node["derived"]!["shell"]!.GetValue<string>());
        Assert.Equal("bash", node["copy"]!["shell"]!.GetValue<string>());
    }

    [Fact]
    public void ToJsonNodeRejectsDuplicateKeys()
    {
        var ex = Assert.ThrowsAny<Exception>(() => YamlJsonConverter.ToJsonNode("a: 1\na: 2\n", "dup.yaml"));
        Assert.Contains("dup.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJsonNodeReportsLineForInvalidYaml()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            YamlJsonConverter.ToJsonNode("a: [1, 2\nb: 3\n", "bad.yaml"));
        Assert.Contains("bad.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJsonNodeReportsLineForBadIndentation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            YamlJsonConverter.ToJsonNode("a:\n  b: 1\n c: 2\n", "indent.yaml"));
        Assert.Contains("indent.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJsonInjectsSchemaFromModelineWhenKeyMissing()
    {
        const string yaml = """
            # yaml-language-server: $schema=rexo.schema.json
            schemaVersion: "1.0"
            """;

        var node = Assert.IsType<JsonObject>(JsonNode.Parse(YamlJsonConverter.ToJson(yaml)));
        Assert.Equal("rexo.schema.json", node["$schema"]!.GetValue<string>());
    }

    [Fact]
    public void ToJsonRejectsModelineThatDisagreesWithSchemaKey()
    {
        const string yaml = """
            # yaml-language-server: $schema=rexo.schema.json
            $schema: ./rexo.schema.json
            """;

        Assert.Throws<InvalidOperationException>(() => YamlJsonConverter.ToJson(yaml, "rexo.yaml"));
    }

    [Fact]
    public void FromJsonRoundTripsWithModelineAndQuotesAmbiguousStrings()
    {
        const string json = """
            {
              "$schema": "rexo.schema.json",
              "schemaVersion": "1.0",
              "name": "sample",
              "flag": "true",
              "count": 3,
              "enabled": false,
              "nothing": null,
              "number": "42",
              "script": "echo one\necho two",
              "empty": {},
              "list": [],
              "steps": [ { "run": "dotnet build" }, { "uses": "builtin:validate" } ]
            }
            """;

        var yaml = YamlJsonConverter.FromJson(json, "rexo.schema.json");

        Assert.StartsWith("# yaml-language-server: $schema=rexo.schema.json\n", yaml, StringComparison.Ordinal);
        Assert.Contains("schemaVersion: \"1.0\"", yaml, StringComparison.Ordinal);
        Assert.Contains("flag: \"true\"", yaml, StringComparison.Ordinal);
        Assert.Contains("number: \"42\"", yaml, StringComparison.Ordinal);

        var roundTripped = YamlJsonConverter.ToJsonNode(yaml);
        var original = JsonNode.Parse(json);
        Assert.True(JsonNode.DeepEquals(original, roundTripped), yaml);
    }
}
