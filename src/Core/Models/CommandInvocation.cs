namespace Rexo.Core.Models;

public sealed record CommandInvocation(
    IReadOnlyDictionary<string, string> Args,
    IReadOnlyDictionary<string, string?> Options,
    bool Json,
    string? JsonFile,
    string WorkingDirectory)
{
    public bool Stdout { get; init; } = true;

    /// <summary>Command names currently on the call stack, used for cross-command cycle detection.</summary>
    public IReadOnlyList<string> CallStack { get; init; } = [];
}
