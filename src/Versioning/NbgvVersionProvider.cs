namespace Rexo.Versioning;

using System.Text.Json;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;

/// <summary>
/// Resolves the version using the Nerdbank.GitVersioning (<c>nbgv</c>) CLI tool.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>Host <c>nbgv get-version -f json</c></item>
///   <item>Docker — mounts the repository into <c>mcr.microsoft.com/dotnet/sdk:latest</c>
///     and runs <c>dotnet tool restore &amp;&amp; dotnet nbgv get-version --format json</c>.
///     Requires <c>nbgv</c> (or an alias) to be present in the repo's
///     <c>.config/dotnet-tools.json</c> manifest.
///     Skipped when <c>settings["useDocker"] = "false"</c>.</item>
///   <item>Fallback version</item>
/// </list>
/// </para>
/// Override the Docker image with <c>settings["dockerImage"]</c>.
/// </summary>
public sealed class NbgvVersionProvider : IVersionProvider
{
    private const string DefaultDockerImage = "mcr.microsoft.com/dotnet/sdk:latest";

    public async Task<VersionResult> ResolveAsync(
        VersioningConfig config,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workingDir = context.RepositoryRoot;

        // 1. Try host nbgv
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

        return Fallback(config, context);
    }

    private static async Task<VersionResult?> TryRunOnHostAsync(
        string workingDir,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var (exitCode, output) = await VersionProcessHelper.RunAsync(
            "nbgv", "get-version -f json", workingDir, cancellationToken);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return ParseNbgvOutput(output, context);

        return null;
    }

    private static async Task<VersionResult?> TryRunDockerAsync(
        string image,
        string workingDir,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Restore from the repo tool manifest first, then run nbgv.
        // dotnet tool restore is non-fatal (|| true) so the command doesn't abort
        // when a manifest is missing or has no nbgv entry; the nbgv call itself will
        // then fail and return a non-zero exit code.
        const string script =
            "dotnet tool restore 1>&2 || true && dotnet nbgv get-version --format json";

        var (exitCode, output) = await VersionProcessHelper.RunSdkDockerScriptAsync(
            image, workingDir, script, cancellationToken);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return ParseNbgvOutput(output, context);

        return null;
    }

    private static VersionResult? ParseNbgvOutput(string json, ExecutionContext context)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // nbgv output: { "Version": "1.2.3", "SemVer2": "1.2.3-alpha.0.1", ... }
            var semVer = TryGetString(root, "SemVer2") ?? TryGetString(root, "Version") ?? "0.1.0";
            var versionStr = TryGetString(root, "Version") ?? semVer;

            // Parse major/minor/patch from Version field (e.g. "1.2.3.0")
            var vparts = versionStr.Split('.');
            var major = vparts.Length > 0 && int.TryParse(vparts[0], out var maj) ? maj : 0;
            var minor = vparts.Length > 1 && int.TryParse(vparts[1], out var min) ? min : 0;
            var patch = vparts.Length > 2 && int.TryParse(vparts[2], out var pat) ? pat : 0;

            var sha = TryGetString(root, "GitCommitId") ?? context.CommitSha ?? "unknown";
            var shortSha = sha.Length >= 7 ? sha[..7] : (context.ShortSha ?? "unknown");

            // Determine prerelease from SemVer2
            var dashIndex = semVer.IndexOf('-', StringComparison.Ordinal);
            var prerelease = dashIndex >= 0 ? semVer[(dashIndex + 1)..] : null;
            var isPrerelease = !string.IsNullOrEmpty(prerelease);

            return new VersionResult(
                SemVer: semVer,
                Major: major,
                Minor: minor,
                Patch: patch,
                PreRelease: prerelease,
                CommitSha: sha,
                ShortSha: shortSha,
                IsPreRelease: isPrerelease,
                IsStable: !isPrerelease);
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

    private static VersionResult Fallback(VersioningConfig config, ExecutionContext context) =>
        FixedVersionProvider.ParseSemVer(config.Fallback ?? "0.1.0-local", context);
}
