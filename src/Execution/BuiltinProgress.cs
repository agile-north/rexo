namespace Rexo.Execution;

using Rexo.Core.Models;

internal static class BuiltinProgress
{
    public static void Write(ExecutionContext context, string message)
    {
        if (!string.Equals(context.CliEchoMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(message);
        }
    }
}
