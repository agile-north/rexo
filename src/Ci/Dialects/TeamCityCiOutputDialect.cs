namespace Rexo.Ci;

public sealed class TeamCityCiOutputDialect : ICiOutputDialect
{
    public string Provider => "teamcity";

    public IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload)
    {
        return payload.Variables
            .Select(pair => $"##teamcity[setParameter name='env.{pair.Key}' value='{EscapeValue(pair.Value)}']")
            .ToArray();
    }

    private static string EscapeValue(string value) =>
        value.Replace("|", "||", StringComparison.Ordinal)
            .Replace("'", "|'", StringComparison.Ordinal)
            .Replace("\r", "|r", StringComparison.Ordinal)
            .Replace("\n", "|n", StringComparison.Ordinal)
            .Replace("[", "|[", StringComparison.Ordinal)
            .Replace("]", "|]", StringComparison.Ordinal);
}
