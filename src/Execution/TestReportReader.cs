namespace Rexo.Execution;

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal sealed record TestReportSummary(
    int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestReportFailure> Failures);

internal sealed record TestReportFailure(string Name, string Message, string? File, int? Line);

/// <summary>Reads case-level results only; captured output is excluded unless explicitly requested.</summary>
internal static partial class TestReportReader
{
    internal static TestReportSummary Read(
        IReadOnlyList<string> paths,
        string format = "auto",
        bool includeOutput = false,
        long maximumDocumentSize = ReportSizeLimits.DefaultBytes,
        Action<string>? oversizedReport = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (maximumDocumentSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDocumentSize), "Report document size limits must be positive.");
        var selected = NormalizeFormat(format);
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<TestReportFailure>();

        foreach (var path in paths)
        {
            try
            {
                using var stream = OpenReport(path, maximumDocumentSize);
                using var reader = CreateReader(stream, maximumDocumentSize);
                var rootSeen = false;
                string? detected = null;
                string? caseName = null;
                var compatible = true;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (!rootSeen)
                    {
                        rootSeen = true;
                        detected = DetectFormatFromRoot(reader.LocalName);
                        compatible = detected is not null && (selected == "auto" || selected == detected);
                        if (compatible)
                        {
                            caseName = detected switch
                            {
                                "trx" => "UnitTestResult",
                                "junit" => "testcase",
                                "nunit" => "test-case",
                                _ => "test",
                            };
                        }
                    }

                    if (!compatible || reader.LocalName != caseName) continue;
                    using var subtree = reader.ReadSubtree();
                    var test = XElement.Load(subtree);
                    switch (Outcome(test, detected!))
                    {
                        case "passed":
                            passed++;
                            break;
                        case "skipped":
                            skipped++;
                            break;
                        default:
                            failed++;
                            failures.Add(ReadFailure(test, detected!, includeOutput));
                            break;
                    }
                }

                if (!rootSeen)
                    throw new InvalidDataException($"Test report '{path}' has no root element.");
                if (detected is null || !compatible)
                    throw new InvalidDataException($"Unsupported test report '{path}' for format '{selected}'.");
            }
            catch (ReportFileTooLargeException ex)
            {
                oversizedReport?.Invoke(ex.Path);
            }
        }

        return new TestReportSummary(checked(passed + failed + skipped), passed, failed, skipped, failures);
    }

    /// <summary>Returns false for unsupported, malformed, oversized, or unreadable automatic candidates.</summary>
    internal static bool IsSupported(string path) => TryDetectFormat(path, out _);

    /// <summary>Returns null for an unknown root; malformed or unreadable XML throws.</summary>
    internal static string? DetectFormat(string path, long maximumDocumentSize = ReportSizeLimits.DefaultBytes)
    {
        if (maximumDocumentSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDocumentSize), "Report document size limits must be positive.");
        using var stream = OpenReport(path, maximumDocumentSize);
        using var reader = CreateReader(stream, maximumDocumentSize);
        string? format = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                format = DetectFormatFromRoot(reader.LocalName);
                break;
            }
        }

        while (reader.Read()) { }
        return format;
    }

    internal static bool TryDetectFormat(string path, out string? format)
    {
        try
        {
            format = DetectFormat(path, ReportSizeLimits.DefaultBytes);
            return format is not null;
        }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            format = null;
            return false;
        }
    }

    private static string NormalizeFormat(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var normalized = format.Trim().ToLowerInvariant();
        return normalized is "auto" or "trx" or "junit" or "nunit" or "xunit"
            ? normalized
            : throw new ArgumentException($"Unknown test report format '{format}'.", nameof(format));
    }

    private static FileStream OpenReport(string path, long maximumDocumentSize)
    {
        var stream = File.OpenRead(path);
        if (stream.Length > maximumDocumentSize)
        {
            stream.Dispose();
            throw new ReportFileTooLargeException(path, maximumDocumentSize);
        }

        return stream;
    }

    private static XmlReader CreateReader(Stream stream, long maximumDocumentSize) =>
        XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maximumDocumentSize,
            MaxCharactersFromEntities = maximumDocumentSize,
        });

    private static string? DetectFormatFromRoot(string rootName) => rootName switch
    {
        "TestRun" => "trx",
        "testsuite" or "testsuites" => "junit",
        "test-results" or "test-run" => "nunit",
        "assemblies" or "assembly" => "xunit",
        _ => null,
    };

    private static string Outcome(XElement test, string format)
    {
        if (format == "junit")
        {
            if (Children(test, "failure").Any() || Children(test, "error").Any())
            {
                return "failed";
            }

            return Children(test, "skipped").Any()
                || Attribute(test, "status") is "disabled" or "notrun" or "skipped"
                ? "skipped"
                : "passed";
        }

        var result = Attribute(test, format == "trx" ? "outcome" : "result")?.ToLowerInvariant();
        if (format == "nunit" && result is null)
        {
            if (string.Equals(Attribute(test, "executed"), "false", StringComparison.OrdinalIgnoreCase))
            {
                return "skipped";
            }

            result = Attribute(test, "success")?.ToLowerInvariant() switch
            {
                "true" => "success",
                "false" => "failure",
                _ => null,
            };
        }

        return result switch
        {
            "passed" or "pass" or "success" or "completed" => "passed",
            "failed" or "fail" or "failure" or "error" or "timeout" or "aborted"
                or "notrunnable" or "invalid" or "disconnected" => "failed",
            "skipped" or "skip" or "ignored" or "inconclusive" or "notexecuted"
                or "pending" or "inprogress" or "warning" or "cancelled" => "skipped",
            _ => throw new InvalidDataException($"Unsupported {format} test outcome '{result ?? "<missing>"}'."),
        };
    }

    private static TestReportFailure ReadFailure(XElement test, string format, bool includeOutput)
    {
        var name = Attribute(test, "testName") ?? Attribute(test, "fullname")
            ?? Attribute(test, "name") ?? "<unnamed>";
        if (format == "junit" && Attribute(test, "classname") is { Length: > 0 } className)
        {
            name = $"{className}.{name}";
        }

        var details = test.Descendants().Where(e => e.Name.LocalName is "failure" or "error" or "ErrorInfo")
            .Where(e => !e.Ancestors().TakeWhile(a => a != test)
                .Any(a => a.Name.LocalName is "failure" or "error" or "ErrorInfo"))
            .ToList();
        var messages = new List<string>();
        foreach (var detail in details)
        {
            AddMessage(messages, Attribute(detail, "message"));
            foreach (var text in detail.DescendantNodes().OfType<XText>()
                .Where(t => !t.Ancestors().TakeWhile(a => a != detail)
                    .Any(a => a.Name.LocalName is "system-out" or "system-err" or "output" or "StdOut" or "StdErr")))
            {
                AddMessage(messages, text.Value);
            }
        }

        var message = messages.Count > 0
            ? string.Join(Environment.NewLine, messages)
            : $"Test failed ({Attribute(test, "outcome") ?? Attribute(test, "result") ?? "failure"}).";
        var file = SourceFile(test) ?? details.Select(SourceFile).FirstOrDefault(f => f is not null);
        var line = SourceLine(test) ?? details.Select(SourceLine).FirstOrDefault(l => l is not null);
        if (file is null)
        {
            var match = StackLocation().Match(message);
            if (match.Success)
            {
                file = match.Groups["file"].Value.Trim();
                line = ParseLine(match.Groups["line"].Value);
            }
        }

        if (includeOutput)
        {
            var output = test.Descendants().Where(IsCapturedOutput)
                .Where(e => !e.Ancestors().TakeWhile(a => a != test).Any(IsCapturedOutput))
                .Select(e => e.Value.Trim()).Where(value => value.Length > 0);
            var captured = string.Join(Environment.NewLine, output);
            if (captured.Length > 0)
            {
                message = string.Concat(message, Environment.NewLine, captured);
            }
        }

        return new TestReportFailure(name, message, file, line);
    }

    private static bool IsCapturedOutput(XElement element) =>
        element.Name.LocalName is "system-out" or "system-err" or "output" or "StdOut" or "StdErr";

    private static void AddMessage(List<string> messages, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) && !messages.Contains(text.Trim(), StringComparer.Ordinal))
        {
            messages.Add(text.Trim());
        }
    }

    private static string? SourceFile(XElement element) =>
        Attribute(element, "file") ?? Attribute(element, "source-file") ?? Attribute(element, "filename");

    private static int? SourceLine(XElement element) =>
        ParseLine(Attribute(element, "line") ?? Attribute(element, "source-line"));

    private static int? ParseLine(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line > 0
            ? line : null;

    private static string? Attribute(XElement element, string name) => element.Attribute(name)?.Value;

    private static IEnumerable<XElement> Children(XElement element, string name) =>
        element.Elements().Where(e => e.Name.LocalName == name);

    [GeneratedRegex(@"(?:\bin\s+|\bat\s+(?![^\r\n]*\bin\s+))(?<file>[^\r\n]+?):(?:line\s+)?(?<line>\d+)(?:\b|$)",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StackLocation();
}
