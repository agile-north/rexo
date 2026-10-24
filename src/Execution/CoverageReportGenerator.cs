namespace Rexo.Execution;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

internal sealed record CoverageReportSummary(
    string Markdown, string OutputDirectory, decimal? LineCoverage, decimal? BranchCoverage);

/// <summary>
/// Generates coverage reports with a pinned ReportGenerator release inside a digest-pinned .NET SDK
/// container. Overlapping inputs are merged by ReportGenerator (per assembly/class/file and line), so
/// duplicated coverage is not double counted. Inputs from different formats that describe the same
/// source under different assembly names are reported separately, as ReportGenerator documents.
/// Source directories are mounted read-only; ReportGenerator maps each recorded source path (host
/// absolute, Windows or relative) by trying progressively shorter path suffixes under each directory,
/// first match wins. Symbolic links inside a mounted directory resolve within the container and
/// cannot reach host paths outside the mounts.
/// </summary>
internal static class CoverageReportGenerator
{
    // mcr.microsoft.com/dotnet/sdk:10.0.402 manifest list digest (verified 2026-10-24).
    public const string DefaultImage =
        "mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2";
    public const string ReportGeneratorVersion = "5.5.11";
    // SHA-512 of dotnet-reportgenerator-globaltool.5.5.11.nupkg (matches nuget.org catalog packageHash).
    public const string ReportGeneratorPackageSha512 =
        "5a971b175a894b20c276a7f892d4f24d0b2a5c7f16c29d1698fdc501d967e4e5ef5d60b9dc84a9ceda321994c7e288786352d1f91f5afc82ad97cf74db9edad5";
    public const string DefaultReportTypes = "Html;MarkdownSummaryGithub";
    internal const string MarkerFileName = ".rexo-coverage-report";
    private const string MarkdownFileName = "SummaryGithub.md";
    private const string JsonFileName = "Summary.json";
    private const string HtmlFileName = "index.html";
    private const string OutputMount = "/rexo/out";
    private const string SourceMountPrefix = "/rexo-source/";
    private const int MaxSourceDirectories = 64;
    private const long MaxMarkdownBytes = 16L * 1024 * 1024;
    private const long MaxJsonBytes = 64L * 1024 * 1024;

    private static readonly string[] SupportedReportTypes =
    [
        "Badges", "Clover", "Cobertura", "OpenCover", "CsvSummary", "Html", "Html_Dark", "Html_Light",
        "Html_BlueRed", "HtmlChart", "HtmlInline", "HtmlInline_AzurePipelines", "HtmlInline_AzurePipelines_Dark",
        "HtmlInline_AzurePipelines_Light", "HtmlSummary", "Html_BlueRed_Summary", "JsonSummary", "Latex",
        "LatexSummary", "lcov", "MarkdownSummary", "MarkdownSummaryGithub", "MHtml", "SvgChart", "SonarQube",
        "TeamCitySummary", "TextSummary", "Xml", "XmlSummary",
    ];

    private static readonly string[] IndexHtmlReportTypes =
    [
        "Html", "Html_Dark", "Html_Light", "Html_BlueRed", "HtmlInline", "HtmlInline_AzurePipelines",
        "HtmlInline_AzurePipelines_Dark", "HtmlInline_AzurePipelines_Light",
    ];

    private static readonly Regex PinnedImage = new(
        @"\A[a-z0-9][a-z0-9._\-/:]*@sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex FilterEntry = new(
        @"\A[+-][^;\x00-\x1f""]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly string InstallScript = string.Join('\n',
        "set -eu",
        "pkg=/tmp/rg-src/dotnet-reportgenerator-globaltool." + ReportGeneratorVersion + ".nupkg",
        "mkdir -p /tmp/rg-src",
        "curl --fail --silent --show-error --location --proto '=https' --max-time 300 -o \"$pkg\" " +
            "https://api.nuget.org/v3-flatcontainer/dotnet-reportgenerator-globaltool/" + ReportGeneratorVersion +
            "/dotnet-reportgenerator-globaltool." + ReportGeneratorVersion + ".nupkg",
        "echo \"" + ReportGeneratorPackageSha512 + "  $pkg\" | sha512sum -c --quiet -",
        "printf '<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /><add key=\"local\" value=\"/tmp/rg-src\" /></packageSources></configuration>' > /tmp/rg-nuget.config",
        "dotnet tool install dotnet-reportgenerator-globaltool --version " + ReportGeneratorVersion +
            " --tool-path /tmp/rg --configfile /tmp/rg-nuget.config >/dev/null",
        "exec /tmp/rg/reportgenerator \"$@\"");

    internal static string? DetectFormat(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var first = FirstSignificantByte(stream);
        stream.Position = 0;
        if (first is null) return null;
        return first == (byte)'<' ? DetectXml(stream, path) : DetectText(stream);
    }

    internal static async Task<CoverageReportSummary> GenerateAsync(
        IReadOnlyList<string> files, string repositoryRoot, string outputDirectory, string image,
        string assemblyFilters, string classFilters, string fileFilters, string? reportTypes,
        CancellationToken cancellationToken,
        Func<GitHubReleaseCommand, CancellationToken, Task<ShellRunResult>>? run = null,
        IReadOnlyList<string>? sourceDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) throw new InvalidOperationException("No coverage files were provided.");
        if (files.Count > 10000) throw new InvalidOperationException("Coverage generation accepts at most 10,000 files.");
        if (string.IsNullOrWhiteSpace(image) || !PinnedImage.IsMatch(image))
        {
            throw new InvalidOperationException(
                "The coverage image must be pinned by an immutable digest (name[:tag]@sha256:<64 hex>).");
        }
        var types = NormalizeReportTypes(reportTypes);
        var filters = new List<string>();
        AddFilter(filters, "assemblyfilters", assemblyFilters);
        AddFilter(filters, "classfilters", classFilters);
        AddFilter(filters, "filefilters", fileFilters);

        var root = Path.GetFullPath(repositoryRoot);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inputs = new List<string>();
        var seen = new HashSet<string>(comparer);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = ReportFiles.Resolve(root, file);
            if (!File.Exists(full)) throw new InvalidOperationException($"Coverage file '{file}' does not exist.");
            if (!seen.Add(full)) continue;
            if (DetectFormat(full) is null)
            {
                throw new InvalidOperationException(
                    $"Coverage file '{file}' is not a supported Cobertura, OpenCover, LCOV, JaCoCo or Clover report.");
            }
            EnsureMountable(full);
            inputs.Add(full);
        }

        var output = ReportFiles.Resolve(root, outputDirectory);
        if (string.Equals(output.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), comparison))
        {
            throw new InvalidOperationException("The coverage output directory must not be the repository root.");
        }
        if (inputs.Any(input => input.StartsWith(output + Path.DirectorySeparatorChar, comparison)))
        {
            throw new InvalidOperationException("Coverage inputs must not be inside the coverage output directory.");
        }
        EnsureMountable(output);
        var sources = ResolveSourceDirectories(root, output, sourceDirectories, comparer, comparison);
        PrepareOutput(output);

        var args = new List<string>
        {
            "run", "--rm", "--read-only", "--tmpfs", "/tmp:rw,exec,size=1g", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp", "--env", "NUGET_PACKAGES=/tmp/nuget",
            "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "--env", "DOTNET_NOLOGO=1",
            "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "--env", "DOTNET_NUGET_SIGNATURE_VERIFICATION=true",
        };
        var user = LinuxUser();
        if (user is not null) args.AddRange(["--user", user]);
        var reports = new List<string>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var target = $"/rexo/in/{index.ToString(CultureInfo.InvariantCulture)}/coverage";
            args.AddRange(["--mount", $"type=bind,source={inputs[index]},target={target},readonly"]);
            reports.Add(target);
        }
        var sourceTargets = new List<string>(sources.Count);
        for (var index = 0; index < sources.Count; index++)
        {
            var target = SourceMountPrefix + index.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["--mount", $"type=bind,source={sources[index]},target={target},readonly"]);
            sourceTargets.Add(target);
        }
        args.AddRange(["--mount", $"type=bind,source={output},target={OutputMount}"]);
        args.AddRange(["--entrypoint", "sh", image, "-c", InstallScript, "rexo-coverage",
            $"-reports:{string.Join(';', reports)}", $"-targetdir:{OutputMount}",
            $"-reporttypes:{string.Join(';', types)}", "-verbosity:Warning"]);
        if (sourceTargets.Count > 0) args.Add($"-sourcedirs:{string.Join(';', sourceTargets)}");
        args.AddRange(filters);

        var command = new GitHubReleaseCommand("docker", args, root, new Dictionary<string, string?>());
        ShellRunResult result;
        try
        {
            result = await (run ?? RunAsync)(command, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException("Docker is required to generate coverage reports.", ex);
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ReportGenerator failed (exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}): {Tail(result)}");
        }

        var markdownPath = RequireOutput(output, MarkdownFileName);
        var jsonPath = RequireOutput(output, JsonFileName);
        RequireOutput(output, HtmlFileName);
        if (new FileInfo(markdownPath).Length > MaxMarkdownBytes)
            throw new InvalidOperationException("The generated coverage Markdown summary exceeds 16 MiB.");
        if (new FileInfo(jsonPath).Length > MaxJsonBytes)
            throw new InvalidOperationException("The generated coverage JSON summary exceeds 64 MiB.");
        var markdown = await File.ReadAllTextAsync(markdownPath, Encoding.UTF8, cancellationToken);
        var (line, branch) = ReadSummary(await File.ReadAllBytesAsync(jsonPath, cancellationToken));
        return new CoverageReportSummary(markdown, output, line, branch);
    }

    private static string[] NormalizeReportTypes(string? reportTypes)
    {
        var requested = string.IsNullOrWhiteSpace(reportTypes) ? DefaultReportTypes : reportTypes;
        var types = new List<string>();
        foreach (var entry in requested.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var canonical = SupportedReportTypes.FirstOrDefault(type => string.Equals(type, entry, StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException($"Unsupported coverage report type '{entry}'.");
            if (!types.Contains(canonical, StringComparer.Ordinal)) types.Add(canonical);
        }
        if (!types.Any(type => IndexHtmlReportTypes.Contains(type, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Coverage report types must include an HTML report ({string.Join(", ", IndexHtmlReportTypes)}).");
        }
        if (!types.Contains("MarkdownSummaryGithub", StringComparer.Ordinal)) types.Add("MarkdownSummaryGithub");
        if (!types.Contains("JsonSummary", StringComparer.Ordinal)) types.Add("JsonSummary");
        return [.. types];
    }

    private static void AddFilter(List<string> args, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var entries = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0 || entries.Any(entry => !FilterEntry.IsMatch(entry)))
        {
            throw new InvalidOperationException(
                $"Coverage {name} must be ';'-separated entries starting with '+' or '-'.");
        }
        args.Add($"-{name}:{string.Join(';', entries)}");
    }

    private static List<string> ResolveSourceDirectories(string root, string output,
        IReadOnlyList<string>? configured, StringComparer comparer, StringComparison comparison)
    {
        var result = new List<string>();
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        if (configured is null)
        {
            // Default: <repo>/src only when it is a plain directory; never the whole repository.
            var fallback = Path.Join(trimmedRoot, "src");
            if (Directory.Exists(fallback) && (File.GetAttributes(fallback) & FileAttributes.ReparsePoint) == 0 &&
                (File.GetAttributes(trimmedRoot) & FileAttributes.ReparsePoint) == 0 &&
                !HasGitMetadata(fallback) && !Overlaps(fallback, output, comparison))
            {
                EnsureMountable(fallback);
                result.Add(fallback);
            }
            return result;
        }
        if (configured.Count > MaxSourceDirectories)
        {
            throw new InvalidOperationException("Coverage generation accepts at most 64 source directories.");
        }
        var seen = new HashSet<string>(comparer);
        foreach (var directory in configured)
        {
            var full = ReportFiles.Resolve(root, directory).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(full))
                throw new InvalidOperationException($"Coverage source directory '{directory}' does not exist.");
            if (string.Equals(full, trimmedRoot, comparison))
                throw new InvalidOperationException(
                    "The repository root cannot be a coverage source directory; list its source folders instead.");
            if (HasGitMetadata(full))
                throw new InvalidOperationException(
                    $"Coverage source directory '{directory}' contains Git metadata and will not be mounted.");
            if (Overlaps(full, output, comparison))
                throw new InvalidOperationException(
                    $"Coverage source directory '{directory}' overlaps the coverage output directory.");
            EnsureMountable(full);
            if (seen.Add(full)) result.Add(full);
        }
        return result;
    }

    private static bool HasGitMetadata(string directory) =>
        Directory.Exists(Path.Join(directory, ".git")) || File.Exists(Path.Join(directory, ".git"));

    private static bool Overlaps(string first, string second, StringComparison comparison)
    {
        var a = first.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var b = second.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, comparison) || b.StartsWith(a, comparison);
    }

    private static void EnsureMountable(string path)
    {
        if (path.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            throw new InvalidOperationException($"Coverage path '{path}' cannot contain commas, quotes or newlines.");
        }
    }

    private static void PrepareOutput(string output)
    {
        if (File.Exists(output)) throw new InvalidOperationException("The coverage output directory is a file.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            if (!File.Exists(Path.Join(output, MarkerFileName)))
            {
                throw new InvalidOperationException(
                    "The coverage output directory is not empty and was not created by Rexo; refusing to overwrite it.");
            }
            Directory.Delete(output, recursive: true);
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Join(output, MarkerFileName), "Generated by Rexo coverage reporting.\n");
    }

    private static string RequireOutput(string output, string name)
    {
        var path = Path.Join(output, name);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"ReportGenerator did not produce '{name}'.");
        }
        return path;
    }

    private static (decimal? Line, decimal? Branch) ReadSummary(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The ReportGenerator JSON summary has no 'summary' object.");
            }
            return (Percent(summary, "coveredlines", "coverablelines"), Percent(summary, "coveredbranches", "totalbranches"));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The ReportGenerator JSON summary is not valid JSON.", ex);
        }
    }

    private static decimal? Percent(JsonElement summary, string coveredName, string totalName)
    {
        var total = Count(summary, totalName);
        var covered = Count(summary, coveredName);
        if (total is null or 0) return null;
        if (covered is null || covered < 0 || covered > total)
        {
            throw new InvalidOperationException($"The ReportGenerator JSON summary has an invalid '{coveredName}' value.");
        }
        return covered.Value * 100m / total.Value;
    }

    private static long? Count(JsonElement summary, string name)
    {
        if (!summary.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0)
        {
            throw new InvalidOperationException($"The ReportGenerator JSON summary has an invalid '{name}' value.");
        }
        return count;
    }

    private static string? LinuxUser()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/status")) return null;
        string? uid = null, gid = null;
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal)) uid = FirstField(line);
            else if (line.StartsWith("Gid:", StringComparison.Ordinal)) gid = FirstField(line);
        }
        return uid is null || gid is null || uid == "0" ? null : $"{uid}:{gid}";
    }

    private static string? FirstField(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts[1].All(char.IsAsciiDigit) ? parts[1] : null;
    }

    private static string Tail(ShellRunResult result)
    {
        var text = string.Join('\n', new[] { result.Stderr, result.Stdout }.Where(value => value.Length > 0));
        return text.Length <= 4000 ? text : text[^4000..];
    }

    private static int? FirstSignificantByte(Stream stream)
    {
        Span<byte> bom = stackalloc byte[3];
        var read = stream.Read(bom);
        var offset = read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF ? 3 : 0;
        stream.Position = offset;
        int value;
        while ((value = stream.ReadByte()) >= 0)
        {
            if (value is not (' ' or '\t' or '\r' or '\n')) return value;
        }
        return null;
    }

    private static string? DetectXml(Stream stream, string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = 128L * 1024 * 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };
        string? root = null;
        string? firstChild = null;
        var rootAttributes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (root is null)
                {
                    root = reader.LocalName;
                    if (reader.MoveToFirstAttribute())
                    {
                        do rootAttributes.Add(reader.LocalName);
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                }
                else if (firstChild is null && reader.Depth == 1)
                {
                    firstChild = reader.LocalName;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException($"Coverage file '{path}' is not well-formed XML: {ex.Message}", ex);
        }
        return root switch
        {
            "CoverageSession" => "opencover",
            "report" when firstChild is null or "sessioninfo" or "group" or "package" or "counter" => "jacoco",
            "coverage" when rootAttributes.Contains("clover") || firstChild == "project" => "clover",
            "coverage" when rootAttributes.Contains("line-rate") || firstChild is "sources" or "packages" => "cobertura",
            _ => null,
        };
    }

    private static string? DetectText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var started = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!started)
            {
                if (!trimmed.StartsWith("TN:", StringComparison.Ordinal) && !trimmed.StartsWith("SF:", StringComparison.Ordinal))
                    return null;
                started = true;
            }
            if (trimmed == "end_of_record") return "lcov";
        }
        return null;
    }

    private static Task<ShellRunResult> RunAsync(GitHubReleaseCommand command, CancellationToken cancellationToken) =>
        ShellRunner.RunProcessAsync(command.FileName, command.Arguments, command.WorkingDirectory,
            command.Environment, cancellationToken: cancellationToken);
}
