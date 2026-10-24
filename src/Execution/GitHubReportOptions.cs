namespace Rexo.Execution;

using System.Globalization;
using System.Text.RegularExpressions;
using Rexo.Core.Models;
using Rexo.Templating;

internal sealed class GitHubReportOptions(ExecutionContext context, bool coverage)
{
    public string Section { get; } = coverage ? "githubCoverage" : "githubTests";

    public string Read(string name, string fallback = "")
    {
        var renderer = new TemplateRenderer();
        var value = renderer.Render($"{{{{vars.{Section}.{name}}}}}", context);
        return renderer.Render(string.IsNullOrEmpty(value) ? fallback : value, context);
    }

    public bool Boolean(string name, bool fallback) =>
        bool.TryParse(Read(name, fallback ? "true" : "false"), out var value)
            ? value : throw new InvalidOperationException($"vars.{Section}.{name} must be true or false.");

    public int Integer(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Read(name, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
            ? value : throw new InvalidOperationException($"vars.{Section}.{name} must be between {minimum.ToString(CultureInfo.InvariantCulture)} and {maximum.ToString(CultureInfo.InvariantCulture)}.");

    public decimal? Threshold(string name)
    {
        var raw = Read(name);
        if (raw.Length == 0) return null;
        return decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
            value is >= 0 and <= 100 ? value :
            throw new InvalidOperationException($"vars.{Section}.{name} must be a percentage between 0 and 100.");
    }

    public string Choice(string name, string fallback, params string[] allowed)
    {
        var value = Read(name, fallback);
        return allowed.Contains(value, StringComparer.Ordinal) ? value :
            throw new InvalidOperationException($"vars.{Section}.{name} must be {string.Join(", ", allowed)}.");
    }

    public bool IsActions => string.Equals(context.GetEnvironmentValue("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
    public bool DryRun => context.IsDryRun || ConfigCommandLoader.TryGetOptionBoolean(context.Options, "dry-run") == true;
    public bool Preview => ConfigCommandLoader.TryGetOptionBoolean(context.Options, "report-preview") == true;
    public string ArtifactUpload => Boolean("uploadArtifact", true)
        ? Choice("artifactUpload", "auto", "auto", "required", "never") : "never";
    public bool Publish => Choice("publish", "auto", "auto", "always", "never") switch
    {
        "never" => false,
        "always" => !DryRun,
        _ => !DryRun && (IsActions || ConfigCommandLoader.TryGetOptionBoolean(context.Options, "publish-reports") == true),
    };

    public string Scope
    {
        get
        {
            var value = Read("scope", context.GetEnvironmentValue("GITHUB_JOB") ?? "default");
            if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new InvalidOperationException($"vars.{Section}.scope must contain 1-100 letters, digits, dots, underscores or hyphens.");
            return value;
        }
    }

    public string OutputDirectory => ReportFiles.Resolve(context.RepositoryRoot,
        Read("outputDirectory", coverage ? "{{outputs.tests.coverage}}/github-report" : "{{outputs.tests.results}}/github-report"));

    public void Validate()
    {
        _ = Choice("publish", "auto", "auto", "always", "never");
        _ = Choice("missingReports", "warn", "warn", "fail");
        _ = Choice("publishErrors", "fail", "fail", "warn");
        _ = Boolean("enabled", true);
        _ = Boolean("checks", true);
        _ = Boolean("jobSummary", true);
        _ = Boolean("uploadArtifact", true);
        _ = Choice("artifactUpload", "auto", "auto", "required", "never");
        if (!coverage) _ = Boolean("includeOutput", false);
        _ = Integer("retentionDays", 14, 1, 400);
        _ = Integer("annotationLimit", 50, 0, 1000);
        _ = Scope;
        _ = OutputDirectory;
        if (string.Equals(OutputDirectory, Path.GetFullPath(context.RepositoryRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The report outputDirectory must not be the repository root.");
        _ = coverage
            ? Choice("format", "auto", "auto", "cobertura", "opencover", "lcov", "jacoco", "clover")
            : Choice("format", "auto", "auto", "trx", "junit", "nunit", "xunit");
        if (coverage)
        {
            _ = Threshold("minimumLineCoverage");
            _ = Threshold("minimumBranchCoverage");
        }
    }
}
