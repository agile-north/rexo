namespace Rexo.Execution;

using System.IO.Compression;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Core.Models;

/// <summary>Checks configured release evidence before transferring prepared outputs.</summary>
public static class ReleaseEvidenceValidator
{
    public static async Task ValidateAsync(
        string root, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken)
    {
        string PathFor(string key) => PreparedArtifactFiles.ResolvePath(root,
            inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new InvalidOperationException($"{key} is required."));

        var run = JsonSerializer.Deserialize<RunManifest>(
            await File.ReadAllTextAsync(PathFor("runManifest"), cancellationToken))
            ?? throw new InvalidOperationException("Release manifest is null.");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(PathFor("result"), cancellationToken));
        if (!run.Success || run.ExitCode != 0 ||
            !result.RootElement.GetProperty("Success").GetBoolean() ||
            result.RootElement.GetProperty("ExitCode").GetInt32() != 0)
        {
            throw new InvalidOperationException("Release evidence reports failure.");
        }
        void RequireFresh(string path)
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < run.StartedAt.UtcDateTime)
            {
                throw new InvalidOperationException($"Release evidence is missing or stale: '{path}'.");
            }
        }
        RequireFresh(PathFor("runManifest"));
        RequireFresh(PathFor("result"));
        var testRoot = PathFor("tests");
        foreach (var pattern in ReportPatterns)
        {
            if (!Directory.Exists(testRoot) || !Directory.EnumerateFiles(testRoot, pattern, SearchOption.AllDirectories)
                .Any(path => File.GetLastWriteTimeUtc(path) >= run.StartedAt.UtcDateTime))
            {
                throw new InvalidOperationException($"Release did not produce fresh '{pattern}' evidence.");
            }
        }
        RequireFresh(PathFor("sarif"));
        var archivePath = PathFor("archive");
        RequireFresh(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        var entryName = inputs.TryGetValue("entry", out var name) ? name
            : throw new InvalidOperationException("entry is required.");
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"Archive entry '{entryName}' is missing.");
        using var reader = new StreamReader(entry.Open());
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (text.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Archive entry '{entryName}' contains unresolved template tokens.");
        }
    }

    private static readonly string[] ReportPatterns = ["*.trx", "coverage.cobertura.xml"];
}
