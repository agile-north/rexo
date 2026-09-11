namespace Rexo.Versioning;

using Rexo.Core.Abstractions;
using Rexo.Core.Models;

/// <summary>
/// Resolves the version using the MinVer dotnet tool.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>Host <c>dotnet minver</c></item>
///   <item>Docker — mounts the repository into <c>mcr.microsoft.com/dotnet/sdk:latest</c>
///     and runs <c>dotnet tool restore &amp;&amp; dotnet minver</c>.
///     Requires <c>minver-cli</c> to be present in the repo's
///     <c>.config/dotnet-tools.json</c> manifest.
///     Skipped when <c>settings["useDocker"] = "false"</c>.</item>
///   <item>Fallback version</item>
/// </list>
/// </para>
/// Supported settings: <c>tagPrefix</c>, <c>minimumMajorMinor</c>,
/// <c>useDocker</c>, <c>dockerImage</c>.
/// </summary>
public sealed class MinVerVersionProvider : IVersionProvider
{
    private const string DefaultDockerImage = "mcr.microsoft.com/dotnet/sdk:latest";

    public async Task<VersionResult> ResolveAsync(
        VersioningConfig config,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workingDir = context.RepositoryRoot;
        var tagPrefix = config.Settings?.GetValueOrDefault("tagPrefix");
        var minMajor = config.Settings?.GetValueOrDefault("minimumMajorMinor");

        // 1. Try host dotnet minver
        var result = await TryRunOnHostAsync(workingDir, tagPrefix, minMajor, context, cancellationToken);
        if (result is not null)
            return result;

        // 2. Try Docker fallback
        if (VersionProcessHelper.UseDockerFallback(config))
        {
            var image = VersionProcessHelper.GetDockerImage(config, context, DefaultDockerImage);
            result = await TryRunDockerAsync(image, workingDir, tagPrefix, minMajor, context, cancellationToken);
            if (result is not null)
                return result;
        }

        return Fallback(config, context);
    }

    private static async Task<VersionResult?> TryRunOnHostAsync(
        string workingDir,
        string? tagPrefix,
        string? minMajor,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var arguments = BuildMinVerArguments(tagPrefix, minMajor);

        var (exitCode, output) = await VersionProcessHelper.RunAsync(
            "dotnet", arguments, workingDir, cancellationToken);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return FixedVersionProvider.ParseSemVer(output.Trim(), context);

        return null;
    }

    private static async Task<VersionResult?> TryRunDockerAsync(
        string image,
        string workingDir,
        string? tagPrefix,
        string? minMajor,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var minverCommand = BuildMinVerArguments(tagPrefix, minMajor);
        // Restore tools from the repo's tool manifest (non-fatal), then run minver.
        var script = $"dotnet tool restore 1>&2 || true && {minverCommand}";

        var (exitCode, output) = await VersionProcessHelper.RunSdkDockerScriptAsync(
            image, workingDir, script, cancellationToken);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return FixedVersionProvider.ParseSemVer(output.Trim(), context);

        return null;
    }

    private static string BuildMinVerArguments(string? tagPrefix, string? minMajor)
    {
        var arguments = "minver";
        if (!string.IsNullOrEmpty(tagPrefix))
            arguments += $" --tag-prefix {tagPrefix}";
        if (!string.IsNullOrEmpty(minMajor))
            arguments += $" --minimum-major-minor {minMajor}";
        return arguments;
    }

    private static VersionResult Fallback(VersioningConfig config, ExecutionContext context) =>
        FixedVersionProvider.ParseSemVer(config.Fallback ?? "0.1.0-local", context);
}
