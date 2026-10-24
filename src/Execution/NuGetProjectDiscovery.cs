namespace Rexo.Execution;

using System.ComponentModel;
using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Configuration;
using Rexo.Configuration.Models;
using Rexo.Core.Models;

internal static class NuGetProjectDiscovery
{
    private static readonly string[] GeneratedDirectories = [".git", "bin", "obj"];
    private static readonly SearchValues<char> UnsupportedPatternCharacters = SearchValues.Create("\\:[]{}!<>\"|");

    internal static async Task<IReadOnlyList<RepoArtifactConfig>> ResolveAsync(
        RepoConfig config, ExecutionContext context, Func<RepoArtifactConfig, bool> predicate,
        CancellationToken cancellationToken, ICollection<string>? skippedProjects = null)
    {
        var selected = ConfigCommandLoader.SelectArtifacts(config, context, predicate);
        var resolved = new List<RepoArtifactConfig>();
        foreach (var declaration in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (declaration.Discover is not { } discovery)
            {
                resolved.Add(declaration);
                continue;
            }
            if (!string.Equals(declaration.Type, "nuget", StringComparison.OrdinalIgnoreCase) ||
                declaration.Name is not null || declaration.Settings?.ContainsKey("project") == true)
            {
                throw new InvalidOperationException("NuGet discovery cannot be combined with name or settings.project, or used with another provider.");
            }

            var effective = ConfigCommandLoader.ToArtifactConfig(declaration, config, ConfigCommandLoader.ResolveOutputRoot(config, context));
            var configuredOutput = effective.Settings["output"].GetString()
                ?? throw new InvalidOperationException("NuGet output must be a nonempty path.");
            if (string.IsNullOrWhiteSpace(configuredOutput))
            {
                throw new InvalidOperationException("NuGet output must be a nonempty path.");
            }
            var output = Path.GetFullPath(Path.IsPathRooted(configuredOutput)
                ? configuredOutput : Path.Join(context.RepositoryRoot, configuredOutput));
            var key = JsonSerializer.Serialize(new
            {
                Root = Path.GetFullPath(context.RepositoryRoot),
                Config = CanonicalConfigHasher.Compute(config),
                Discovery = discovery,
                Version = context.Version?.SemVer,
                PackageVersion = context.Version?.NuGetVersion,
                Output = output,
            });
            var projects = await context.ArtifactDiscoveryCache.GetOrAdd(key, _ =>
                new Lazy<Task<IReadOnlyList<ArtifactProject>>>(() =>
                    EvaluateMatchesAsync(context, discovery, output, cancellationToken))).Value;
            foreach (var project in projects.Where(project => !project.IsPackable))
            {
                skippedProjects?.Add(project.Project);
            }
            foreach (var project in projects.Where(project => project.IsPackable))
            {
                var settings = declaration.Settings is null
                    ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement>(declaration.Settings, StringComparer.Ordinal);
                settings["project"] = JsonSerializer.SerializeToElement(project.Project);
                resolved.Add(declaration with
                {
                    Name = project.PackageId,
                    Settings = settings,
                    Discover = null,
                    SourceProject = project.Project,
                });
            }
        }

        if (selected.Any(artifact => artifact.Discover is not null))
        {
            var duplicate = resolved.Where(artifact => string.Equals(artifact.Type, "nuget", StringComparison.OrdinalIgnoreCase))
                .GroupBy(artifact => ConfigCommandLoader.ResolveArtifactName(artifact, config), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException(
                    $"Duplicate NuGet package identity '{duplicate.Key}' in selected artifacts: " +
                    string.Join(", ", duplicate.Select(artifact => artifact.SourceProject ?? artifact.Name ?? config.Name)));
            }
        }
        return resolved;
    }

    private static async Task<IReadOnlyList<ArtifactProject>> EvaluateMatchesAsync(
        ExecutionContext context, RepoArtifactDiscoveryConfig discovery, string output, CancellationToken cancellationToken)
    {
        var paths = MatchProjects(context.RepositoryRoot, discovery, cancellationToken);
        var projects = new List<ArtifactProject>();
        foreach (var path in paths)
        {
            var properties = await ReadPropertiesAsync(context, path, null, output, cancellationToken);
            var project = ParseProject(properties, path);
            if (project.IsPackable)
            {
                var frameworks = properties.GetValueOrDefault("TargetFrameworks");
                if (!string.IsNullOrWhiteSpace(frameworks))
                {
                    foreach (var framework in frameworks.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        var inner = ParseProject(await ReadPropertiesAsync(context, path, framework, output, cancellationToken), path);
                        if (inner.PackageId != project.PackageId || inner.IsPackable != project.IsPackable)
                        {
                            throw new InvalidOperationException($"NuGet discovery found an ambiguous package identity or IsPackable for '{path}' in target framework '{framework}'.");
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine($"NuGet discovery skipped '{path}': IsPackable=false.");
            }
            projects.Add(project);
        }
        if (!projects.Any(project => project.IsPackable))
        {
            throw new InvalidOperationException("NuGet discovery produced no packable projects. Check discover.include/exclude and evaluated IsPackable.");
        }
        return projects;
    }

    private static ArtifactProject ParseProject(Dictionary<string, string> properties, string project)
    {
        if (!bool.TryParse(properties.GetValueOrDefault("IsPackable"), out var packable))
        {
            throw new InvalidOperationException($"NuGet discovery could not evaluate IsPackable for '{project}'.");
        }
        var id = properties.GetValueOrDefault("PackageId") ?? string.Empty;
        if (packable && (id.Length is 0 or > 100 || id.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))))
        {
            throw new InvalidOperationException($"NuGet discovery evaluated an invalid PackageId '{id}' for '{project}'.");
        }
        return new ArtifactProject(project, id, packable);
    }

    private static async Task<Dictionary<string, string>> ReadPropertiesAsync(
        ExecutionContext context, string project, string? framework, string packageOutput, CancellationToken cancellationToken)
    {
        var full = PreparedArtifactFiles.ResolvePath(context.RepositoryRoot, project);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = context.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(full);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-verbosity:quiet");
        start.ArgumentList.Add("-getProperty:PackageId,IsPackable,TargetFrameworks");
        start.ArgumentList.Add("-property:Configuration=Release");
        start.ArgumentList.Add("-property:_IsPacking=true");
        start.ArgumentList.Add("-property:_CommandLineDefinedOutputPath=true");
        start.ArgumentList.Add($"-property:PackageOutputPath={packageOutput}");
        if (context.Version is not null)
        {
            start.ArgumentList.Add($"-property:Version={context.Version.SemVer}");
            start.ArgumentList.Add($"-property:PackageVersion={context.Version.NuGetVersion ?? context.Version.SemVer}");
        }
        if (framework is not null)
        {
            start.ArgumentList.Add($"-property:TargetFramework={framework}");
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"NuGet discovery requires the installed .NET SDK to evaluate '{project}': {ex.Message}", ex);
        }
        using var registration = cancellationToken.Register(() =>
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    Console.Error.WriteLine($"NuGet discovery process for '{project}' exited during cancellation.");
                }
            }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"NuGet discovery failed to evaluate '{project}'. Restore/setup may be required. {error} {output}");
        }
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.GetProperty("Properties").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"NuGet discovery received invalid MSBuild metadata for '{project}': {output} {error}", ex);
        }
    }

    internal static IReadOnlyList<string> MatchProjects(
        string root, RepoArtifactDiscoveryConfig discovery, CancellationToken cancellationToken)
    {
        if (discovery.Include is not { Count: > 0 })
        {
            throw new InvalidOperationException("NuGet discover.include must contain at least one pattern.");
        }
        var includes = discovery.Include.Select(ParsePattern).ToArray();
        var excludes = (discovery.Exclude ?? []).Select(ParsePattern).ToArray();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        var matches = new List<string>();
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("NuGet discovery repository root must not be a symbolic link.");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                var segments = relative.Split('/');
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                        !GeneratedDirectories.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase) &&
                        includes.Any(pattern => MatchPositions(pattern, segments).Any(position => position < pattern.Length)) &&
                        !excludes.Any(pattern => pattern[^1] == "**" && Matches(pattern, segments)))
                    {
                        pending.Push(entry);
                    }
                    continue;
                }
                if (!includes.Any(pattern => Matches(pattern, segments)) || excludes.Any(pattern => Matches(pattern, segments)))
                {
                    continue;
                }
                if (!relative.EndsWith(".csproj", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"NuGet discovery matched non-project file '{relative}'; include patterns must select .csproj files.");
                }
                _ = PreparedArtifactFiles.ResolvePath(root, relative);
                matches.Add(relative);
                if (matches.Count > 10000)
                {
                    throw new InvalidOperationException("NuGet discovery exceeded 10,000 matched projects; narrow discover.include.");
                }
            }
        }
        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    private static string[] ParsePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 1024 || pattern[0] == '/' ||
            pattern.AsSpan().IndexOfAny(UnsupportedPatternCharacters) >= 0)
        {
            throw new InvalidOperationException($"Invalid NuGet discovery pattern '{pattern}'. Use repository-relative '/' paths with '*', '?' and '**'.");
        }
        var segments = pattern.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." ||
            (segment != "**" && segment.Contains("**", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException($"Invalid NuGet discovery pattern '{pattern}': '**' must be a complete path segment; empty, '.' and '..' segments are not allowed.");
        }
        return segments;
    }

    private static bool Matches(string[] pattern, string[] path)
        => MatchPositions(pattern, path).Contains(pattern.Length);

    private static HashSet<int> MatchPositions(string[] pattern, string[] path)
    {
        var positions = new HashSet<int> { 0 };
        foreach (var segment in path)
        {
            ExpandRecursiveSegments(pattern, positions);
            var next = new HashSet<int>();
            foreach (var position in positions.Where(position => position < pattern.Length))
            {
                if (pattern[position] == "**") next.Add(position);
                else if (FileSystemName.MatchesSimpleExpression(pattern[position], segment, ignoreCase: false)) next.Add(position + 1);
            }
            positions = next;
        }
        ExpandRecursiveSegments(pattern, positions);
        return positions;
    }

    private static void ExpandRecursiveSegments(string[] pattern, HashSet<int> positions)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if (positions.Contains(index) && pattern[index] == "**") positions.Add(index + 1);
        }
    }
}
