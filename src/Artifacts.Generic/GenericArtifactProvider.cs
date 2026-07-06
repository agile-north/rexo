namespace Rexo.Artifacts.Generic;

using System.IO.Compression;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

/// <summary>Preview: Generic file artifact provider — zip / tar.gz packaging using built-in .NET compression.</summary>
public sealed class GenericArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("generic", new GenericArtifactProvider());

    public string Type => "generic";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var source = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "GENERIC_TARGET_SOURCE",
            configuredEnvName: GetSetting(artifact, "target.sourceEnv"),
            configuredValue: GetSetting(artifact, "target.source"),
            fileEnv: fileEnv)
            ?? context.RepositoryRoot;
        var format = GetSetting(artifact, "format") ?? "zip";
        var outputDir = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "GENERIC_TARGET_OUTPUT",
            configuredEnvName: GetSetting(artifact, "target.outputEnv"),
            configuredValue: GetSetting(artifact, "target.output"),
            fileEnv: fileEnv)
            ?? "artifacts/generic";

        var absoluteSource = System.IO.Path.IsPathRooted(source)
            ? source
            : System.IO.Path.Combine(context.RepositoryRoot, source);

        var absoluteOutput = System.IO.Path.IsPathRooted(outputDir)
            ? outputDir
            : System.IO.Path.Combine(context.RepositoryRoot, outputDir);

        Directory.CreateDirectory(absoluteOutput);

        var version = context.Version?.SemVer ?? "0.0.0";
        var archiveName = $"{artifact.Name}-{version}.{format}";
        var archivePath = System.IO.Path.Combine(absoluteOutput, archiveName);

        Console.WriteLine($"  > Creating {format} archive: {archiveName}");

        try
        {
            if (format.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(archivePath)) File.Delete(archivePath);
                ZipFile.CreateFromDirectory(absoluteSource, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            }
            else
            {
                Console.Error.WriteLine($"  Unsupported format '{format}'. Only 'zip' is supported.");
                return new ArtifactBuildResult(artifact.Name, false, null);
            }

            await Task.CompletedTask;
            return new ArtifactBuildResult(artifact.Name, true, archivePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Error creating archive: {ex.Message}");
            return new ArtifactBuildResult(artifact.Name, false, null);
        }
    }

    public Task<ArtifactTagResult> TagAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var tags = context.Version is not null
            ? new[] { context.Version.SemVer }
            : Array.Empty<string>();

        return Task.FromResult(new ArtifactTagResult(artifact.Name, true, tags));
    }

    public Task<ArtifactPushResult> PushAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var destination = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "GENERIC_TARGET_DESTINATION",
            configuredEnvName: GetSetting(artifact, "target.destinationEnv"),
            configuredValue: GetSetting(artifact, "target.destination"),
            fileEnv: fileEnv);

        if (string.IsNullOrWhiteSpace(destination))
        {
            Console.Error.WriteLine("  No destination configured for generic artifact push.");
            return Task.FromResult(new ArtifactPushResult(artifact.Name, false, []));
        }

        var outputDir = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "GENERIC_TARGET_OUTPUT",
            configuredEnvName: GetSetting(artifact, "target.outputEnv"),
            configuredValue: GetSetting(artifact, "target.output"),
            fileEnv: fileEnv)
            ?? "artifacts/generic";
        var absoluteOutput = System.IO.Path.IsPathRooted(outputDir)
            ? outputDir
            : System.IO.Path.Combine(context.RepositoryRoot, outputDir);

        var absoluteDestination = System.IO.Path.IsPathRooted(destination)
            ? destination
            : System.IO.Path.Combine(context.RepositoryRoot, destination);

        try
        {
            Directory.CreateDirectory(absoluteDestination);

            var files = Directory.GetFiles(absoluteOutput, $"{artifact.Name}-*");
            foreach (var file in files)
            {
                var destFile = System.IO.Path.Combine(absoluteDestination, System.IO.Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
                Console.WriteLine($"  Copied: {destFile}");
            }

            return Task.FromResult(new ArtifactPushResult(artifact.Name, true, [absoluteDestination]));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Error copying artifact: {ex.Message}");
            return Task.FromResult(new ArtifactPushResult(artifact.Name, false, []));
        }
    }

    private static string? GetSetting(ArtifactConfig artifact, string key)
    {
        if (!TryGetSettingValue(artifact.Settings, out var value, key))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString(),
        };
    }

    private static bool TryGetSettingValue(
        IReadOnlyDictionary<string, JsonElement> settings,
        out JsonElement value,
        string path)
    {
        value = default;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !settings.TryGetValue(segments[0], out value))
        {
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segments[i], out value))
            {
                return false;
            }
        }

        return true;
    }
}
