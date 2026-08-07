namespace Rexo.Ci;

public sealed class GenericCiOutputDialect : ICiOutputDialect
{
    public GenericCiOutputDialect(string? provider)
    {
        Provider = string.IsNullOrWhiteSpace(provider) ? "generic" : provider!;
    }

    public string Provider { get; }

    public IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload)
    {
        return payload.Variables.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
    }
}
