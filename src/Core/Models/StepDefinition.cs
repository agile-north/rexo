namespace Rexo.Core.Models;

public sealed record StepDefinition(
    string? Id,
    string? Run,
    string? Uses,
    string? Command,
    string? When)
{
    public IReadOnlyDictionary<string, string>? With { get; init; }
    public bool WhenExists { get; init; }
    public bool Parallel { get; init; }
    public bool ContinueOnError { get; init; }
    public string? OutputPattern { get; init; }
    public string? OutputFile { get; init; }
    public StepContainerDefinition? Container { get; init; }

    /// <summary>
    /// Declared output glob patterns produced by this step.
    /// Keys are logical output names (e.g. <c>testResults</c>, <c>coverage</c>);
    /// values are glob patterns that may include <c>{{outputs.*}}</c> template references.
    /// Patterns are resolved against the repository root after step execution.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? StepOutputs { get; init; }
}

public sealed record StepContainerDefinition(
    string Image,
    IReadOnlyDictionary<string, string>? Env = null,
    string? WorkingDirectory = null);
