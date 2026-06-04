namespace Rexo.Ci;

public sealed class GitHubActionsCiOutputDialect : ICiOutputDialect
{
    public string Provider => "github-actions";

    public IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload)
    {
        return payload.Variables
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();
    }
}
