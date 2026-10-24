namespace Rexo.Execution;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using Rexo.Core.Models;

internal sealed class GitHubReportBuiltinModule : IConfigBuiltinModule
{
    private static readonly string[] TokenVariables =
        ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "ACTIONS_RUNTIME_TOKEN"];
    private static readonly bool[] ReportKinds = [false, true];

    public void Register(BuiltinRegistry registry, ConfigBuiltinModuleContext context)
    {
        registry.Register("builtin:github-tests", (step, ctx, ct) =>
            ExecuteAsync(step.Id ?? "github-tests", WithDryRun(ctx), false, ct));
        registry.Register("builtin:github-coverage", (step, ctx, ct) =>
            ExecuteAsync(step.Id ?? "github-coverage", WithDryRun(ctx), true, ct));

        ExecutionContext WithDryRun(ExecutionContext ctx) => ctx with
        {
            IsDryRun = ConfigCommandLoader.TryGetOptionBoolean(ctx.Options, "dry-run") ??
                (ctx.IsDryRun || context.Config.Runtime?.DryRun == true),
        };
    }

    internal static async Task<StepResult> ExecuteAsync(string stepId, ExecutionContext context, bool coverage,
        CancellationToken cancellationToken, HttpClient? client = null,
        Func<GitHubReleaseCommand, CancellationToken, Task<ShellRunResult>>? run = null)
    {
        var outputs = new Dictionary<string, object?>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var options = new GitHubReportOptions(context, coverage);
        var errors = new List<string>();
        var files = new List<string>();
        var detectedFiles = new List<object>();
        var annotations = new List<GitHubReportAnnotation>();
        var title = coverage ? "Rexo coverage" : "Rexo tests";
        var markdown = "";
        var conclusion = "success";
        var validated = false;
        try
        {
            options.Validate();
            validated = true;
            if (!options.Boolean("enabled", true) || options.DryRun)
            {
                outputs["status"] = "skipped";
                outputs["skipReason"] = options.DryRun ? "Dry run: report processing and publishing skipped." : "Reporting is disabled.";
                Console.WriteLine(outputs["skipReason"]);
                return Result();
            }
            var explicitPaths = options.Read("paths");
            var paths = explicitPaths.Length > 0 ? explicitPaths : coverage
                ? "{{outputs.tests.coverage}}/**/*;{{outputs.tests.results}}/**/coverage*.xml;{{outputs.tests.results}}/**/lcov.info"
                : "{{outputs.tests.results}}/**/*.trx;{{outputs.tests.results}}/**/*.xml";
            paths = new Templating.TemplateRenderer().Render(paths, context);
            var candidates = ReportFiles.Discover(context.RepositoryRoot, paths, options.Read("exclude"), options.OutputDirectory).ToList();
            if (explicitPaths.Length == 0)
            {
                var outputKind = coverage ? "coverage" : "testResults";
                var declaredOutputs = context.CompletedSteps.Values
                    .Select(step => step.Outputs.GetValueOrDefault("__fileOutputs"))
                    .OfType<IReadOnlyDictionary<string, IReadOnlyList<string>>>()
                    .Where(declared => declared.ContainsKey(outputKind));
                foreach (var declared in declaredOutputs)
                {
                    foreach (var declaredFile in declared[outputKind])
                    {
                        foreach (var path in ReportFiles.Discover(context.RepositoryRoot, declaredFile,
                            options.Read("exclude"), options.OutputDirectory)
                            .Where(path => !candidates.Contains(path, StringComparer.Ordinal)))
                        {
                            candidates.Add(path);
                        }
                    }
                }
            }
            var format = coverage
                ? options.Choice("format", "auto", "auto", "cobertura", "opencover", "lcov", "jacoco", "clover")
                : options.Choice("format", "auto", "auto", "trx", "junit", "nunit", "xunit");
            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detected = coverage ? CoverageReportGenerator.DetectFormat(file) : TestReportReader.DetectFormat(file);
                detectedFiles.Add(new { path = Path.GetRelativePath(context.RepositoryRoot, file).Replace('\\', '/'), format = detected });
                if (detected is null)
                {
                    if (explicitPaths.Length > 0 || format != "auto")
                        throw new InvalidOperationException($"Unsupported report format: {Path.GetRelativePath(context.RepositoryRoot, file)}.");
                    continue;
                }
                if (format != "auto" && !string.Equals(format, detected, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The selected report does not match format '{format}'.");
                files.Add(file);
            }
            outputs["reportCount"] = files.Count;
            if (options.Preview)
            {
                outputs["status"] = "preview";
                outputs["paths"] = paths;
                outputs["exclude"] = options.Read("exclude");
                outputs["detectedReports"] = detectedFiles;
                Console.WriteLine(JsonSerializer.Serialize(outputs, ConfigCommandLoader.IndentedJsonOptions));
                return Result();
            }
            if (files.Count == 0)
            {
                markdown = $"# {title}\n\nNo supported reports were found. No passing result is inferred.\n";
                conclusion = "neutral";
                Console.Error.WriteLine($"{title}: no supported reports found.");
                if (options.Read("missingReports", "warn") == "fail") errors.Add("Expected reports were not found.");
                if (coverage)
                {
                    outputs["lineCoverage"] = null;
                    outputs["branchCoverage"] = null;
                    CheckThreshold("minimumLineCoverage", null, "line");
                    CheckThreshold("minimumBranchCoverage", null, "branch");
                }
            }
            else if (coverage)
            {
                var report = await CoverageReportGenerator.GenerateAsync(files, context.RepositoryRoot, options.OutputDirectory,
                    options.Read("image", CoverageReportGenerator.DefaultImage), options.Read("assemblyFilters"),
                    options.Read("classFilters"), options.Read("fileFilters"),
                    options.Read("reportTypes", "Html;MarkdownSummaryGithub"), cancellationToken, run,
                    SourceDirectories());
                markdown = report.Markdown;
                outputs["lineCoverage"] = report.LineCoverage;
                outputs["branchCoverage"] = report.BranchCoverage;
                CheckThreshold("minimumLineCoverage", report.LineCoverage, "line");
                CheckThreshold("minimumBranchCoverage", report.BranchCoverage, "branch");
            }
            else
            {
                var report = TestReportReader.Read(files, format, options.Boolean("includeOutput", false));
                outputs["total"] = report.Total;
                outputs["passed"] = report.Passed;
                outputs["failed"] = report.Failed;
                outputs["skipped"] = report.Skipped;
                var builder = new StringBuilder($"# {title}\n\n| Total | Passed | Failed | Skipped |\n|---:|---:|---:|---:|\n");
                builder.AppendLine(CultureInfo.InvariantCulture, $"| {report.Total} | {report.Passed} | {report.Failed} | {report.Skipped} |\n");
                foreach (var failure in report.Failures.Take(options.Integer("annotationLimit", 50, 0, 1000)))
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"### {Escape(failure.Name)}\n\n<pre>{Escape(GitHubReportCheckPublisher.Limit(failure.Message, 8000))}</pre>\n");
                    var path = AnnotationPath(context.RepositoryRoot, options.Read("sourceRoot"), failure.File);
                    if (path is not null && failure.Line is > 0)
                        annotations.Add(new GitHubReportAnnotation(path, failure.Line.Value, failure.Name, failure.Message));
                }
                markdown = builder.ToString();
                if (report.Failed > 0) errors.Add("Test reports contain failing tests.");
                if (context.CompletedSteps.Values.Any(step => !step.Success))
                    errors.Add("The test lifecycle reported a failure before report publication.");
            }
        }
        catch (Exception ex) when (Expected(ex))
        {
            errors.Add(Mask(context, ex.Message));
            markdown = $"# {title}\n\nReport processing failed: {Escape(Mask(context, ex.Message))}\n";
        }
        if (!validated)
        {
            outputs["status"] = "failed";
            outputs["error"] = string.Join(" ", errors);
            Console.Error.WriteLine(outputs["error"]);
            return Result();
        }

        if (errors.Count > 0)
        {
            conclusion = "failure";
            markdown += $"\n\n{string.Join("\n\n", errors.Select(Escape))}\n";
        }
        markdown = Mask(context, markdown);
        try
        {
            if (coverage)
            {
                var marker = Path.Join(options.OutputDirectory, CoverageReportGenerator.MarkerFileName);
                if (Directory.Exists(options.OutputDirectory) &&
                    Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any() && !File.Exists(marker))
                    throw new InvalidOperationException("The coverage output directory is non-empty and not owned by Rexo; choose a dedicated outputDirectory.");
                Directory.CreateDirectory(options.OutputDirectory);
                await File.WriteAllTextAsync(marker, "Rexo coverage report directory.", cancellationToken);
            }
            Directory.CreateDirectory(options.OutputDirectory);
            var summaryPath = Path.Join(options.OutputDirectory, "SummaryGithub.md");
            await File.WriteAllTextAsync(summaryPath, markdown, cancellationToken);
            outputs["summaryPath"] = Path.GetRelativePath(context.RepositoryRoot, summaryPath).Replace('\\', '/');
            outputs["__fileOutputs"] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["reportSummary"] = [Path.GetRelativePath(context.RepositoryRoot, summaryPath).Replace('\\', '/')],
            };
            if (options.Publish && options.IsActions && options.Boolean("jobSummary", true))
            {
                var summary = context.GetEnvironmentValue("GITHUB_STEP_SUMMARY");
                if (string.IsNullOrWhiteSpace(summary))
                    Console.Error.WriteLine($"{title}: Actions job summary skipped: GITHUB_STEP_SUMMARY is unavailable.");
                else
                    await File.AppendAllTextAsync(summary, markdown + "\n", cancellationToken);
            }
        }
        catch (Exception ex) when (Expected(ex))
        {
            errors.Add(Mask(context, ex.Message));
            conclusion = "failure";
        }
        try
        {
            var publication = await GitHubReportCheckPublisher.PublishAsync(context, options, title, markdown, conclusion,
                annotations, cancellationToken, client);
            outputs["checkUrl"] = publication.Url;
            outputs["checkStatus"] = publication.Status;
            if (publication.SkipReason is not null) outputs["checkSkipReason"] = publication.SkipReason;
        }
        catch (Exception ex) when (Expected(ex))
        {
            outputs["checkStatus"] = "failed";
            PublishError(ex);
        }
        outputs["artifactStatus"] = "skipped";
        if (options.Publish && options.IsActions && options.ArtifactUpload != "never" && files.Count > 0)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(context.GetEnvironmentValue("ACTIONS_RUNTIME_TOKEN")) ||
                    string.IsNullOrWhiteSpace(context.GetEnvironmentValue("ACTIONS_RESULTS_URL")))
                {
                    outputs["artifactSkipReason"] = "Actions runtime credentials unavailable.";
                    if (options.ArtifactUpload == "required")
                        throw new InvalidOperationException("Actions runtime credentials are unavailable for required artifact upload. GITHUB_TOKEN alone cannot upload workflow artifacts.");
                    Console.WriteLine($"{title}: artifact upload unavailable in this host; local reports retained. Use actions/upload-artifact for shell workflows.");
                }
                else
                {
                    var uploadFiles = files.Concat(Directory.Exists(options.OutputDirectory)
                        ? Directory.EnumerateFiles(options.OutputDirectory, "*", SearchOption.AllDirectories) : []).Distinct().ToArray();
                    var name = $"{options.Read("artifactName", coverage ? "coverage" : "test-results")}-{options.Scope}-{context.GetEnvironmentValue("GITHUB_RUN_ATTEMPT") ?? "1"}";
                    outputs["artifactId"] = await ActionsReportArtifactUploader.UploadAsync(
                        uploadFiles.Select(file => Path.GetRelativePath(context.RepositoryRoot, file)).ToArray(), context.RepositoryRoot,
                        name, options.Integer("retentionDays", 14, 1, 400), context.GetEnvironmentValue, cancellationToken, client);
                    outputs["artifactStatus"] = "published";
                }
            }
            catch (Exception ex) when (Expected(ex))
            {
                outputs["artifactStatus"] = "failed";
                PublishError(ex);
            }
        }
        outputs["conclusion"] = conclusion;
        outputs["status"] = errors.Count > 0 ? "failed" : outputs.GetValueOrDefault("checkUrl") is not null ||
            outputs.GetValueOrDefault("artifactId") is not null ? "published" : "local";
        if (errors.Count > 0)
        {
            outputs["error"] = string.Join(" ", errors);
            Console.Error.WriteLine($"{title}: {outputs["error"]}");
        }
        return Result();

        StepResult Result() => new(stepId, errors.Count == 0, errors.Count > 0 ? 6 : 0, started.Elapsed, outputs);

        IReadOnlyList<string>? SourceDirectories()
        {
            var value = options.Read("sourceDirectories");
            return value.Length == 0 ? null : value == "none" ? [] :
                value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => ReportFiles.Resolve(context.RepositoryRoot, path)).ToArray();
        }

        void CheckThreshold(string setting, decimal? actual, string metric)
        {
            var threshold = options.Threshold(setting);
            if (threshold is not null && (actual is null || actual < threshold))
                errors.Add($"{metric} coverage {(actual?.ToString(CultureInfo.InvariantCulture) ?? "unavailable")}% does not meet {threshold.Value.ToString(CultureInfo.InvariantCulture)}%.");
        }

        void PublishError(Exception error)
        {
            var message = Mask(context, error.Message);
            Console.Error.WriteLine($"{title} publishing failed: {message}");
            if (options.Read("publishErrors", "fail") == "fail") errors.Add(message);
            outputs["publishWarning"] = message;
        }
    }

    private static bool Expected(Exception ex) => ex is InvalidOperationException or IOException or
        UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or
        XmlException or InvalidDataException or HttpRequestException or System.ComponentModel.Win32Exception;

    private static string Mask(ExecutionContext context, string value)
    {
        var customTokens = ReportKinds.Select(coverage =>
                new GitHubReportOptions(context, coverage).Read("tokenEnvironment"))
            .Where(name => name.Length > 0).Select(context.GetEnvironmentValue).OfType<string>();
        return SecretMasker.Mask(value, SecretMasker.CollectSecretValues(context.ResolvedSecrets.Values
            .Concat(context.MappedSecretEnvironment.Values)
            .Concat(context.FileEnvironment.Where(pair => pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
            .Concat(TokenVariables.Select(context.GetEnvironmentValue).OfType<string>()).Concat(customTokens)));
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    internal static string? AnnotationPath(string root, string sourceRoot, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var normalized = file.Replace('\\', '/');
        if (sourceRoot.Length > 0)
        {
            var prefix = sourceRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) return null;
            normalized = normalized[prefix.Length..];
        }
        if (Path.IsPathRooted(normalized))
        {
            normalized = Path.GetRelativePath(root, normalized).Replace('\\', '/');
        }
        if (normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal)) return null;
        var full = ReportFiles.Resolve(root, normalized);
        return File.Exists(full) ? Path.GetRelativePath(root, full).Replace('\\', '/') : null;
    }
}
