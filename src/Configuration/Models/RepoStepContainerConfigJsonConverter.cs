namespace Rexo.Configuration.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Reads a container definition from any of the supported shapes:
/// <list type="bullet">
///   <item><c>"name"</c> — reference to a named container (<c>"none"</c> disables containerization).</item>
///   <item><c>false</c> — run on the host.</item>
///   <item><c>{ "use": "name", ...overrides }</c> or a full inline object.</item>
/// </list>
/// </summary>
internal sealed class RepoStepContainerConfigJsonConverter : JsonConverter<RepoStepContainerConfig>
{
    public override RepoStepContainerConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return new RepoStepContainerConfig { Use = reader.GetString() };
            case JsonTokenType.False:
                return new RepoStepContainerConfig { Use = RepoStepContainerConfig.NoneReference };
            case JsonTokenType.StartObject:
                break;
            default:
                throw new JsonException("A container must be a string reference, false, or an object.");
        }

        var dto = JsonSerializer.Deserialize<ContainerDto>(ref reader, options)
            ?? throw new JsonException("Container definition is empty.");
        var (dockerfile, inlineDockerfile) = ParseDockerfile(dto.Dockerfile);

        return new RepoStepContainerConfig(
            dto.Image,
            dto.Env,
            dto.WorkingDirectory,
            dto.Entrypoint,
            dockerfile,
            dto.Context,
            dto.Build)
        {
            Use = dto.Use,
            Extends = dto.Extends,
            Fallback = dto.Fallback,
            InlineDockerfile = inlineDockerfile,
        };
    }

    public override void Write(Utf8JsonWriter writer, RepoStepContainerConfig value, JsonSerializerOptions options)
    {
        var dto = new ContainerDto
        {
            Use = value.Use,
            Extends = value.Extends,
            Image = value.Image,
            Env = value.Env,
            WorkingDirectory = value.WorkingDirectory,
            Entrypoint = value.Entrypoint,
            Dockerfile = value.InlineDockerfile is not null
                ? JsonSerializer.SerializeToElement(new DockerfileObject { Content = value.InlineDockerfile }, options)
                : value.Dockerfile is not null
                    ? JsonSerializer.SerializeToElement(value.Dockerfile, options)
                    : null,
            Context = value.Context,
            Build = value.Build,
            Fallback = value.Fallback,
        };

        JsonSerializer.Serialize(writer, dto, options);
    }

    private static (string? Path, string? Content) ParseDockerfile(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        if (value.Value.ValueKind == JsonValueKind.String)
        {
            var text = value.Value.GetString();
            return string.IsNullOrEmpty(text) || !text.Contains('\n', StringComparison.Ordinal) &&
                !text.Contains('\r', StringComparison.Ordinal)
                ? (text, null)
                : (null, text);
        }

        if (value.Value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("'dockerfile' must be a path/content string or an object with 'path' or 'content'.");
        }

        string? path = null;
        string? content = null;
        var hasPath = false;
        var hasContent = false;
        foreach (var property in value.Value.EnumerateObject())
        {
            if (property.NameEquals("path"))
            {
                hasPath = true;
                path = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
            else if (property.NameEquals("content"))
            {
                hasContent = true;
                content = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        if (hasPath == hasContent ||
            hasPath && string.IsNullOrWhiteSpace(path) ||
            hasContent && string.IsNullOrWhiteSpace(content))
        {
            throw new JsonException("'dockerfile' object must contain exactly one non-empty string property: 'path' or 'content'.");
        }

        return (path, content);
    }

    private sealed class ContainerDto
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("use")]
        public string? Use { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("extends")]
        public string? Extends { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("env")]
        public Dictionary<string, string>? Env { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("workingDirectory")]
        public string? WorkingDirectory { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("entrypoint")]
        public string? Entrypoint { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("dockerfile")]
        public JsonElement? Dockerfile { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("context")]
        public string? Context { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("build")]
        public RepoStepContainerBuildConfig? Build { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("fallback")]
        public string? Fallback { get; set; }
    }

    private sealed class DockerfileObject
    {
        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }
}
