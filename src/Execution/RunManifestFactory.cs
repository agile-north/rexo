namespace Rexo.Execution;

using System.Security.Cryptography;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Git;

/// <summary>Creates run evidence after a command has completed.</summary>
public static class RunManifestFactory
{
    public static async Task<RunManifest> CreateAsync(
        CommandResult result,
        string commandName,
        string workingDir,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        RepoConfig? config,
        RepoConfig? environmentConfig,
        EnvironmentSelectionResult? environmentSelection,
        CiInfo ciInfo,
        string? toolVersion,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Join(workingDir, ".rexo", "rexo.lock.yaml");
        string? lockHash = null;
        if (File.Exists(lockPath))
        {
            await using var stream = File.OpenRead(lockPath);
            lockHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        var gitInfo = await GitDetector.DetectAsync(workingDir, cancellationToken);
        return new RunManifest
        {
            ToolVersion = toolVersion,
            RepoName = Path.GetFileName(workingDir.TrimEnd(Path.DirectorySeparatorChar)),
            RepoRoot = workingDir,
            Branch = gitInfo.Branch,
            CommitSha = gitInfo.CommitSha,
            RemoteUrl = gitInfo.RemoteUrl,
            CommandExecuted = commandName,
            Environment = environmentSelection?.EnvironmentName,
            EnvironmentSelectionSource = environmentSelection?.SelectionSource,
            SourceBranch = environmentSelection?.SourceBranch,
            SourceBranchOrigin = environmentSelection?.BranchSource,
            MatchedBranchPattern = environmentSelection?.MatchedPattern,
            EnvironmentSelectionExplicit = environmentSelection?.IsExplicit,
            EnvironmentSelectionOverrodeAutomaticMatches = environmentSelection?.OverrodeAutomaticSelection,
            Success = result.Success,
            ExitCode = result.ExitCode,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ConfigHash = config is null ? null : CanonicalConfigHasher.Compute(config),
            EnvironmentConfigHash = environmentConfig is null ? null : CanonicalConfigHasher.Compute(environmentConfig),
            PolicyLockHash = lockHash,
            Version = result.Version,
            AssemblyVersion = result.Version?.AssemblyVersion,
            InformationalVersion = result.Version?.InformationalVersion,
            NuGetVersion = result.Version?.NuGetVersion,
            IsCi = ciInfo.IsCi,
            CiProvider = ciInfo.Provider,
            CiBuildId = ciInfo.BuildId,
            CiRunNumber = ciInfo.RunNumber,
            CiWorkflowName = ciInfo.WorkflowName,
            CiActor = ciInfo.Actor,
            CiTag = ciInfo.Tag,
            CiBuildUrl = ciInfo.BuildUrl,
            Steps = result.Steps.Select(ToManifestStep).ToArray(),
            Artifacts = result.Artifacts,
            PushDecisions = result.PushDecisions,
            Errors = result.StructuredErrors.Select(error => error.Message)
                .Where(message => message is not null).Select(message => message!).ToArray(),
        };
    }

    private static StepManifestEntry ToManifestStep(StepResult step)
    {
        var outputs = step.Outputs;
        var fileOutputs = outputs.TryGetValue("__fileOutputs", out var value)
            && value is IReadOnlyDictionary<string, IReadOnlyList<string>> files
            ? files
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return new StepManifestEntry(step.StepId, step.Success, step.ExitCode, step.Duration.TotalMilliseconds)
        {
            FileOutputs = fileOutputs,
            ExecutionMode = GetString(outputs, "__executionMode"),
            RequestedExecutionMode = GetString(outputs, "__requestedExecutionMode"),
            ContainerImage = GetString(outputs, "__containerImage"),
            ContainerWorkingDirectory = GetString(outputs, "__containerWorkingDirectory"),
            ContainerFallbackPolicy = GetString(outputs, "__containerFallbackPolicy"),
            ContainerFallbackAllowed = GetBool(outputs, "__containerFallbackAllowed"),
            ContainerFallbackUsed = GetBool(outputs, "__containerFallbackUsed"),
            ContainerFallbackReason = GetString(outputs, "__containerFallbackReason"),
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> outputs, string key) =>
        outputs.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool? GetBool(IReadOnlyDictionary<string, object?> outputs, string key)
    {
        if (!outputs.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value is bool boolean ? boolean : bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}
