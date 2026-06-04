namespace Rexo.Ci;

public interface ICiOutputDialect
{
    string Provider { get; }

    IReadOnlyList<string> FormatStdoutLines(CiEmissionPayload payload);
}
