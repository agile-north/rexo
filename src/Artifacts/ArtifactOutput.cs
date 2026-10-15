namespace Rexo.Artifacts;

using Rexo.Core.Models;

public static class ArtifactOutput
{
    public static string GetEchoMode(ExecutionContext context) =>
        context.CliEchoMode ?? context.CommandEchoMode ?? context.RuntimeEchoMode ?? "summary";

    public static void WriteCommand(ExecutionContext context, string message) =>
        WriteCommand(GetEchoMode(context), message);

    public static void WriteCommand(string? echoMode, string message)
    {
        if (string.Equals(echoMode, "full", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(message);
        }
    }
}
