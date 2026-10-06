namespace Rexo.Configuration;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Converts between YAML and JSON using YAML 1.2 core-schema scalar resolution, so that
/// YAML configuration files are validated and deserialized exactly like their JSON equivalents.
/// </summary>
public static partial class YamlJsonConverter
{
    public const string ModelinePrefix = "# yaml-language-server: $schema=";

    private const string StringTag = "tag:yaml.org,2002:str";
    private const string IntTag = "tag:yaml.org,2002:int";
    private const string FloatTag = "tag:yaml.org,2002:float";
    private const string BoolTag = "tag:yaml.org,2002:bool";
    private const string NullTag = "tag:yaml.org,2002:null";

    public static bool IsYamlPath(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses YAML text into a <see cref="JsonNode"/>. Returns <c>null</c> for an empty document.
    /// </summary>
    public static JsonNode? ToJsonNode(string yamlText, string? sourcePath = null)
    {
        var stream = new YamlStream();
        Parser? parser = null;
        try
        {
            using var reader = new StringReader(yamlText);
            parser = new Parser(reader);
            stream.Load(parser);
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException(FormatYamlError(sourcePath, ex), ex);
        }
        catch (InvalidOperationException ex)
        {
            // YamlDotNet's scanner occasionally throws a bare InvalidOperationException on malformed input.
            var near = parser?.Current is { } current
                ? $" near line {current.End.Line.ToString(CultureInfo.InvariantCulture)}, column {current.End.Column.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty;
            throw new InvalidOperationException($"Invalid YAML in {DescribeSource(sourcePath)}{near}: the document is malformed.", ex);
        }

        if (stream.Documents.Count == 0)
        {
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            throw new InvalidOperationException(
                $"{DescribeSource(sourcePath)} contains {stream.Documents.Count.ToString(CultureInfo.InvariantCulture)} YAML documents; only a single document is supported.");
        }

        return ConvertNode(stream.Documents[0].RootNode, sourcePath);
    }

    /// <summary>
    /// Converts YAML text to a JSON string. If the root mapping has no <c>$schema</c> key, the
    /// <c># yaml-language-server: $schema=...</c> modeline (if present) is used instead.
    /// </summary>
    public static string ToJson(string yamlText, string? sourcePath = null)
    {
        var node = ToJsonNode(yamlText, sourcePath);
        if (node is JsonObject root)
        {
            ApplyModelineSchema(root, yamlText, sourcePath);
        }

        return node is null ? "null" : node.ToJsonString();
    }

    /// <summary>
    /// Returns the schema reference from a <c># yaml-language-server: $schema=...</c> modeline, if any.
    /// </summary>
    public static string? ReadModelineSchema(string yamlText)
    {
        var match = ModelineRegex().Match(yamlText);
        return match.Success ? match.Groups["schema"].Value : null;
    }

    /// <summary>
    /// Converts a JSON document to block-style YAML, optionally prefixed with a
    /// <c># yaml-language-server: $schema=...</c> modeline for editor intellisense.
    /// </summary>
    public static string FromJson(string jsonText, string? modelineSchema = null)
    {
        var node = JsonNode.Parse(jsonText, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(modelineSchema))
        {
            builder.Append(ModelinePrefix).Append(modelineSchema).Append('\n');
        }

        using (var writer = new StringWriter(builder, CultureInfo.InvariantCulture))
        {
            var emitter = new Emitter(writer, EmitterSettings.Default.WithBestIndent(2).WithIndentedSequences());
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart(null, null, isImplicit: true));
            EmitNode(emitter, node);
            emitter.Emit(new DocumentEnd(isImplicit: true));
            emitter.Emit(new StreamEnd());
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void ApplyModelineSchema(JsonObject root, string yamlText, string? sourcePath)
    {
        var modeline = ReadModelineSchema(yamlText);
        if (modeline is null)
        {
            return;
        }

        if (!root.TryGetPropertyValue("$schema", out var existing) || existing is null)
        {
            root["$schema"] = modeline;
            return;
        }

        if (existing.GetValueKind() == JsonValueKind.String &&
            !string.Equals(existing.GetValue<string>(), modeline, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{DescribeSource(sourcePath)} declares '$schema: {existing.GetValue<string>()}' but its yaml-language-server modeline points to '{modeline}'. They must match.");
        }
    }

    private static JsonNode? ConvertNode(YamlNode node, string? sourcePath)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var obj = new JsonObject();
                var mergeSources = new List<YamlNode>();
                foreach (var entry in mapping.Children)
                {
                    if (entry.Key is not YamlScalarNode keyNode)
                    {
                        throw new InvalidOperationException(
                            $"{DescribeSource(sourcePath)} line {entry.Key.Start.Line.ToString(CultureInfo.InvariantCulture)}: mapping keys must be scalars.");
                    }

                    if (IsMergeKey(keyNode))
                    {
                        mergeSources.Add(entry.Value);
                        continue;
                    }

                    var key = keyNode.Value ?? string.Empty;
                    if (obj.ContainsKey(key))
                    {
                        throw new InvalidOperationException(
                            $"{DescribeSource(sourcePath)} line {keyNode.Start.Line.ToString(CultureInfo.InvariantCulture)}: duplicate key '{key}'.");
                    }

                    obj[key] = ConvertNode(entry.Value, sourcePath);
                }

                foreach (var source in mergeSources)
                {
                    ApplyMerge(obj, source, sourcePath);
                }

                return obj;
            }

            case YamlSequenceNode sequence:
            {
                var array = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    array.Add(ConvertNode(item, sourcePath));
                }

                return array;
            }

            case YamlScalarNode scalar:
                return ConvertScalar(scalar, sourcePath);

            default:
                throw new InvalidOperationException(
                    $"{DescribeSource(sourcePath)} line {node.Start.Line.ToString(CultureInfo.InvariantCulture)}: unsupported YAML node.");
        }
    }

    private static bool IsMergeKey(YamlScalarNode keyNode) =>
        keyNode.Value == "<<" &&
        (keyNode.Tag.IsEmpty ? keyNode.Style == ScalarStyle.Plain : keyNode.Tag.Value == "tag:yaml.org,2002:merge");

    // YAML merge keys: explicit keys win over merged ones, and earlier merge sources win over later ones.
    private static void ApplyMerge(JsonObject target, YamlNode source, string? sourcePath)
    {
        switch (source)
        {
            case YamlMappingNode:
                if (ConvertNode(source, sourcePath) is JsonObject merged)
                {
                    foreach (var (key, value) in merged.ToList())
                    {
                        if (!target.ContainsKey(key))
                        {
                            merged.Remove(key);
                            target[key] = value;
                        }
                    }
                }

                break;

            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children)
                {
                    if (item is not YamlMappingNode)
                    {
                        throw new InvalidOperationException(
                            $"{DescribeSource(sourcePath)} line {item.Start.Line.ToString(CultureInfo.InvariantCulture)}: merge key '<<' sequence items must be mappings.");
                    }

                    ApplyMerge(target, item, sourcePath);
                }

                break;

            default:
                throw new InvalidOperationException(
                    $"{DescribeSource(sourcePath)} line {source.Start.Line.ToString(CultureInfo.InvariantCulture)}: merge key '<<' value must be a mapping or a sequence of mappings.");
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar, string? sourcePath)
    {
        var value = scalar.Value ?? string.Empty;
        var tag = scalar.Tag.IsEmpty ? null : scalar.Tag.Value;

        switch (tag)
        {
            case StringTag:
                return JsonValue.Create(value);
            case NullTag:
                return null;
            case BoolTag:
                return TryResolveBool(value, out var b)
                    ? JsonValue.Create(b)
                    : throw InvalidTagged(scalar, sourcePath, "bool");
            case IntTag:
                return TryResolveInt(value) ?? throw InvalidTagged(scalar, sourcePath, "int");
            case FloatTag:
                return TryResolveFloat(value) ?? throw InvalidTagged(scalar, sourcePath, "float");
            default:
                break;
        }

        if (tag is not null && !tag.Equals("!", StringComparison.Ordinal))
        {
            return JsonValue.Create(value);
        }

        if (scalar.Style != ScalarStyle.Plain || tag is not null)
        {
            return JsonValue.Create(value);
        }

        if (IsNull(value))
        {
            return null;
        }

        if (TryResolveBool(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        return TryResolveInt(value) ??
            (FloatRegex().IsMatch(value)
                ? TryResolveFloat(value) ?? throw InvalidTagged(scalar, sourcePath, "float")
                : JsonValue.Create(value));
    }

    private static InvalidOperationException InvalidTagged(YamlScalarNode scalar, string? sourcePath, string type) =>
        new($"{DescribeSource(sourcePath)} line {scalar.Start.Line.ToString(CultureInfo.InvariantCulture)}: value '{scalar.Value}' is not a valid {type}.");

    private static bool IsNull(string value) =>
        value.Length == 0 || value is "~" or "null" or "Null" or "NULL";

    private static bool TryResolveBool(string value, out bool result)
    {
        switch (value)
        {
            case "true" or "True" or "TRUE":
                result = true;
                return true;
            case "false" or "False" or "FALSE":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static JsonNode? TryResolveInt(string value)
    {
        if (DecimalIntRegex().IsMatch(value))
        {
            return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)
                ? JsonValue.Create(l)
                : null;
        }

        if (value.StartsWith("0o", StringComparison.Ordinal) && value.Length > 2 && value[2..].All(c => c is >= '0' and <= '7'))
        {
            try
            {
                return JsonValue.Create(Convert.ToInt64(value[2..], 8));
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        if (value.StartsWith("0x", StringComparison.Ordinal) && value.Length > 2 &&
            long.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
        {
            return JsonValue.Create(hex);
        }

        return null;
    }

    private static JsonNode? TryResolveFloat(string value)
    {
        if (!FloatRegex().IsMatch(value))
        {
            return null;
        }

        // Preserve the lexical form when it is already valid JSON (e.g. "1.0" stays "1.0").
        if (JsonNumberRegex().IsMatch(value))
        {
            return JsonNode.Parse(value);
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? JsonValue.Create(d)
            : null;
    }

    private static void EmitNode(IEmitter emitter, JsonNode? node)
    {
        switch (node)
        {
            case null:
                emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
                break;

            case JsonObject obj:
                emitter.Emit(new MappingStart(null, null, isImplicit: true, obj.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in obj)
                {
                    EmitString(emitter, key);
                    EmitNode(emitter, value);
                }

                emitter.Emit(new MappingEnd());
                break;

            case JsonArray array:
                emitter.Emit(new SequenceStart(null, null, isImplicit: true, array.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in array)
                {
                    EmitNode(emitter, item);
                }

                emitter.Emit(new SequenceEnd());
                break;

            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        EmitString(emitter, value.GetValue<string>());
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                    case JsonValueKind.Number:
                        emitter.Emit(new Scalar(null, null, value.ToJsonString(), ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
                        break;
                    default:
                        emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
                        break;
                }

                break;
        }
    }

    private static void EmitString(IEmitter emitter, string text)
    {
        var style = ScalarStyle.Any;
        if (IsNull(text) || TryResolveBool(text, out _) || TryResolveInt(text) is not null || FloatRegex().IsMatch(text))
        {
            style = ScalarStyle.DoubleQuoted;
        }
        else if (text.Contains('\n', StringComparison.Ordinal) && !text.Contains('\r', StringComparison.Ordinal) &&
                 !text.Any(c => c != '\n' && char.IsControl(c)) && !text.EndsWith(' '))
        {
            style = ScalarStyle.Literal;
        }

        emitter.Emit(new Scalar(null, null, text, style, isPlainImplicit: true, isQuotedImplicit: true));
    }

    private static string DescribeSource(string? sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath) ? "YAML document" : $"'{sourcePath}'";

    private static string FormatYamlError(string? sourcePath, YamlException ex) =>
        $"Invalid YAML in {DescribeSource(sourcePath)} at line {ex.Start.Line.ToString(CultureInfo.InvariantCulture)}, column {ex.Start.Column.ToString(CultureInfo.InvariantCulture)}: {ex.InnerException?.Message ?? ex.Message}";

    [GeneratedRegex(@"^#[ \t]*yaml-language-server:[ \t]*\$schema=(?<schema>\S+)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ModelineRegex();

    [GeneratedRegex(@"^[-+]?(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalIntRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+\.[0-9]*)([eE][-+]?[0-9]+)?$|^[-+]?[0-9]+[eE][-+]?[0-9]+$|^[-+]?\.(inf|Inf|INF)$|^\.(nan|NaN|NAN)$", RegexOptions.CultureInvariant)]
    private static partial Regex FloatRegex();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumberRegex();
}
