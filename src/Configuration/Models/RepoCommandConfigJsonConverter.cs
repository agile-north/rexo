namespace Rexo.Configuration.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class RepoCommandConfigJsonConverter : JsonConverter<RepoCommandConfig>
{
    public override RepoCommandConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var description = ReadOptionalString(root, "description");
        var optionsValue = ReadOptionalObject<Dictionary<string, RepoOptionConfig>>(root, "options", options) ?? [];
        var steps = ReadOptionalObject<List<RepoStepConfig>>(root, "steps", options) ?? [];

        var command = new RepoCommandConfig(description, optionsValue, steps)
        {
            Hidden = ReadOptionalBool(root, "hidden"),
            Args = ReadOptionalObject<Dictionary<string, RepoArgConfig>>(root, "args", options),
            StepOps = ReadOptionalObject<RepoCommandStepOpsConfig>(root, "stepOps", options),
            MaxParallel = ReadOptionalInt(root, "maxParallel"),
            Before = ReadOptionalHookSteps(root, "before", options),
            After = ReadOptionalHookSteps(root, "after", options),
            MaxDepth = ReadOptionalInt(root, "maxDepth"),
        };

        if (!root.TryGetProperty("merge", out var mergeElement))
        {
            return command;
        }

        return mergeElement.ValueKind switch
        {
            JsonValueKind.String => command with { Merge = mergeElement.GetString() },
            JsonValueKind.Object => command with
            {
                MergeConfig = JsonSerializer.Deserialize<RepoCommandMergeConfig>(mergeElement.GetRawText(), options),
            },
            JsonValueKind.Null or JsonValueKind.Undefined => command,
            _ => throw new JsonException("The 'merge' property must be a string, object, or null."),
        };
    }

    public override void Write(Utf8JsonWriter writer, RepoCommandConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (!string.IsNullOrEmpty(value.Description))
        {
            writer.WriteString("description", value.Description);
        }

        if (value.Hidden.HasValue)
        {
            writer.WriteBoolean("hidden", value.Hidden.Value);
        }

        writer.WritePropertyName("options");
        JsonSerializer.Serialize(writer, value.Options, options);

        writer.WritePropertyName("steps");
        JsonSerializer.Serialize(writer, value.Steps, options);

        if (value.Args is not null)
        {
            writer.WritePropertyName("args");
            JsonSerializer.Serialize(writer, value.Args, options);
        }

        if (value.MergeConfig is not null)
        {
            writer.WritePropertyName("merge");
            JsonSerializer.Serialize(writer, value.MergeConfig, options);
        }
        else if (!string.IsNullOrEmpty(value.Merge))
        {
            writer.WriteString("merge", value.Merge);
        }

        if (value.StepOps is not null)
        {
            writer.WritePropertyName("stepOps");
            JsonSerializer.Serialize(writer, value.StepOps, options);
        }

        if (value.MaxParallel.HasValue)
        {
            writer.WriteNumber("maxParallel", value.MaxParallel.Value);
        }

        if (value.Before is { Count: > 0 })
        {
            writer.WritePropertyName("before");
            JsonSerializer.Serialize(writer, value.Before, options);
        }

        if (value.After is { Count: > 0 })
        {
            writer.WritePropertyName("after");
            JsonSerializer.Serialize(writer, value.After, options);
        }

        if (value.MaxDepth.HasValue)
        {
            writer.WriteNumber("maxDepth", value.MaxDepth.Value);
        }

        writer.WriteEndObject();
    }

    private static T? ReadOptionalObject<T>(JsonElement root, string propertyName, JsonSerializerOptions options)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(element.GetRawText(), options);
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.GetString();
    }

    private static int? ReadOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.GetInt32();
    }

    private static bool? ReadOptionalBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => throw new JsonException($"The '{propertyName}' property must be a boolean or boolean string."),
        };
    }

    private static List<RepoStepConfig>? ReadOptionalHookSteps(
        JsonElement root,
        string propertyName,
        JsonSerializerOptions options)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String =>
            [
                new RepoStepConfig(Command: element.GetString()),
            ],
            JsonValueKind.Array => JsonSerializer.Deserialize<List<RepoStepConfig>>(element.GetRawText(), options),
            _ => throw new JsonException($"The '{propertyName}' property must be a string, array, or null."),
        };
    }
}
