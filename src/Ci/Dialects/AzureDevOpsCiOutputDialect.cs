namespace Rexo.Ci;

public sealed class AzureDevOpsCiOutputDialect : ICiOutputDialect
{
    public string Provider => "azure-devops";

    public IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload)
    {
        return payload.Variables
            .Select(pair => $"##vso[task.setvariable variable={pair.Key}]{EscapeValue(pair.Value)}")
            .ToArray();
    }

    private static string EscapeValue(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
