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

        return new RepoStepContainerConfig(
            dto.Image,
            dto.Env,
            dto.WorkingDirectory,
            dto.Entrypoint,
            dto.Dockerfile,
            dto.Context,
            dto.Build)
        {
            Use = dto.Use,
            Extends = dto.Extends,
            Fallback = dto.Fallback,
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
            Dockerfile = value.Dockerfile,
            Context = value.Context,
            Build = value.Build,
            Fallback = value.Fallback,
        };

        JsonSerializer.Serialize(writer, dto, options);
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
        public string? Dockerfile { get; set; }

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
}
