namespace Rexo.Versioning;

using System.Text.Json;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;

/// <summary>
/// Resolves the version using the GitVersion CLI tool.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>Host <c>gitversion /output json</c></item>
///   <item>Host <c>dotnet-gitversion /output json</c> (some environments install this alias)</item>
///   <item>Docker — <c>docker run --rm -v &lt;repo&gt;:/repo gittools/gitversion:6.0.0 /output json</c>
///     (skipped when <c>settings["useDocker"] = "false"</c>)</item>
///   <item>Fallback version</item>
/// </list>
/// </para>
/// Override the Docker image with <c>settings["dockerImage"]</c>.
/// </summary>
public sealed class GitVersionVersionProvider : IVersionProvider
{
    private const string DefaultDockerImage = "gittools/gitversion:6.0.0";

    public async Task<VersionResult> ResolveAsync(
        VersioningConfig config,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workingDir = context.RepositoryRoot;

        // 1. Try host gitversion
        var result = await TryRunOnHostAsync(workingDir, context, cancellationToken);
        if (result is not null)
            return result;

        // 2. Try Docker fallback
        if (VersionProcessHelper.UseDockerFallback(config))
        {
            var image = VersionProcessHelper.GetDockerImage(config, context, DefaultDockerImage);
            result = await TryRunDockerAsync(image, workingDir, context, cancellationToken);
            if (result is not null)
                return result;
        }

        return FallbackVersion(config, context);
    }

    private static async Task<VersionResult?> TryRunOnHostAsync(
        string workingDir,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Try the native gitversion command first, then the dotnet global tool alias
        foreach (var exe in new[] { "gitversion", "dotnet-gitversion" })
        {
            var (exitCode, output) = await VersionProcessHelper.RunAsync(
                exe, "/output json", workingDir, cancellationToken);

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var result = ParseGitVersionOutput(output, context);
                if (result is not null)
                    return result;
            }
        }

        return null;
    }

    private static async Task<VersionResult?> TryRunDockerAsync(
        string image,
        string workingDir,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var (exitCode, output) = await VersionProcessHelper.RunDockerAsync(
            image, workingDir, ["/output", "json"], cancellationToken);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return ParseGitVersionOutput(output, context);

        return null;
    }

    private static VersionResult? ParseGitVersionOutput(string json, ExecutionContext context)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var semVer = TryGetString(root, "SemVer") ?? TryGetString(root, "FullSemVer") ?? "0.1.0";
            var major = TryGetInt(root, "Major") ?? 0;
            var minor = TryGetInt(root, "Minor") ?? 0;
            var patch = TryGetInt(root, "Patch") ?? 0;
            var preReleaseTag = TryGetString(root, "PreReleaseTag");
            var preReleaseLabel = TryGetString(root, "PreReleaseLabel");
            var preReleaseNumber = TryGetInt(root, "PreReleaseNumber");
            var prerelease = preReleaseTag;
            var sha = TryGetString(root, "Sha") ?? context.CommitSha ?? "unknown";
            var shortSha = TryGetString(root, "ShortSha") ?? context.ShortSha ?? "unknown";

            var isPrerelease = !string.IsNullOrEmpty(prerelease);

            return new VersionResult(
                SemVer: semVer,
                Major: major,
                Minor: minor,
                Patch: patch,
                PreRelease: string.IsNullOrEmpty(prerelease) ? null : prerelease,
                CommitSha: sha,
                ShortSha: shortSha,
                IsPreRelease: isPrerelease,
                IsStable: !isPrerelease)
            {
                PreReleaseLabel = preReleaseLabel,
                PreReleaseNumber = preReleaseNumber,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static int? TryGetInt(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value))
            {
                return value;
            }

            if (prop.ValueKind == JsonValueKind.String &&
                int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static VersionResult FallbackVersion(VersioningConfig config, ExecutionContext context) =>
        FixedVersionProvider.ParseSemVer(config.Fallback ?? "0.1.0-local", context);
}
