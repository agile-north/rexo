namespace Rexo.Cli;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Rexo.Configuration.Models;

internal static class CanonicalConfigHasher
{
    public static string Compute(RepoConfig config)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(config));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .Where(property => !IsSensitiveProperty(property.Name))
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    writer.WriteNumberValue(integer);
                }
                else if (element.TryGetDecimal(out var decimalValue))
                {
                    writer.WriteRawValue(decimalValue.ToString("G29", CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteNumberValue(element.GetDouble());
                }

                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind '{element.ValueKind}' in configuration hash.");
        }
    }

    private static bool IsSensitiveProperty(string name)
    {
        var normalized = new string(name
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();

        if (normalized == "secrets")
        {
            return false;
        }

        return normalized is "auth" or "password" or "passwd" or "token" or
            "apikey" or "clientsecret" or "credential" or "credentials" or "privatekey" ||
            normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.EndsWith("token", StringComparison.Ordinal) ||
            normalized.EndsWith("apikey", StringComparison.Ordinal) ||
            normalized.EndsWith("credential", StringComparison.Ordinal) ||
            normalized.EndsWith("privatekey", StringComparison.Ordinal);
    }
}
