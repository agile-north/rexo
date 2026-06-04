namespace Rexo.Ci;

public sealed class GitLabCiOutputDialect : ICiOutputDialect
{
    public string Provider => "gitlab-ci";

    public IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload) => new GenericCiOutputDialect(Provider).FormatStdoutLines(payload);
}
