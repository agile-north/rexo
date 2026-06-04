namespace Rexo.Ci;

public sealed class BitbucketPipelinesCiOutputDialect : ICiOutputDialect
{
    public string Provider => "bitbucket-pipelines";

    public IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload) => new GenericCiOutputDialect(Provider).FormatStdoutLines(payload);
}
