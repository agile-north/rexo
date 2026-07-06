namespace Rexo.Core.Models;

/// <summary>
/// Structured error with a machine-readable code, human-readable message,
/// optional detail, and a suggested fix.
/// </summary>
public sealed record RexoError(
    string Code,
    string Message)
{
    /// <summary>Extended diagnostic information.</summary>
    public string? Detail { get; init; }

    /// <summary>Suggested action to resolve the error.</summary>
    public string? SuggestedFix { get; init; }

    /// <summary>The source file or context where the error occurred (e.g. step id, config path).</summary>
    public string? Source { get; init; }

    public override string ToString() =>
        SuggestedFix is not null
            ? $"[{Code}] {Message}  →  {SuggestedFix}"
            : $"[{Code}] {Message}";
}

/// <summary>
/// Well-known error codes emitted by the Rexo runtime.
/// </summary>
public static class ErrorCodes
{
    // Configuration errors (CFG-*)
    public const string ConfigNotFound = "CFG-001";
    public const string ConfigSchemaInvalid = "CFG-002";
    public const string ConfigVersionMismatch = "CFG-003";
    public const string ConfigExtendCycle = "CFG-004";

    // Command errors (CMD-*)
    public const string CommandNotFound = "CMD-001";
    public const string CommandArgMissing = "CMD-002";
    public const string CommandOptionInvalid = "CMD-003";
    public const string CommandCycle = "CMD-004";

    // Step errors (STP-*)
    public const string StepFailed = "STP-001";
    public const string StepShellError = "STP-002";
    public const string StepBuiltinNotFound = "STP-003";
    public const string StepOutputPatternNoMatch = "STP-004";

    // Version errors (VER-*)
    public const string VersionProviderNotFound = "VER-001";
    public const string VersionResolutionFailed = "VER-002";
    public const string VersionInvalidFormat = "VER-003";
    public const string VersionProviderUnsupported = "VER-004";

    // Artifact errors (ART-*)
    public const string ArtifactProviderNotFound = "ART-001";
    public const string ArtifactBuildFailed = "ART-002";
    public const string ArtifactPushFailed = "ART-003";
    public const string ArtifactPushPolicyViolation = "ART-004";
    public const string ArtifactProviderUnsupported = "ART-005";

    // Policy errors (POL-*)
    public const string PolicyLoadFailed = "POL-001";
    public const string PolicyViolation = "POL-002";

    // Secret errors (SEC-*)
    public const string SecretProviderNotFound = "SEC-001";
    public const string SecretResolutionFailed = "SEC-002";
    public const string SecretProviderUnavailable = "SEC-003";

    // Capability errors (CAP-*)
    public const string CapabilityRequirementNotSupported = "CAP-001";
    public const string CapabilityContractMismatch = "CAP-002";

    // Git errors (GIT-*)
    public const string GitNotFound = "GIT-001";
    public const string GitDirtyWorkingTree = "GIT-002";
}
