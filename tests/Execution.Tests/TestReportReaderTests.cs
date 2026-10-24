namespace Rexo.Execution.Tests;

public sealed class TestReportReaderTests : IDisposable
{
    private readonly string _root = Path.Join(Directory.GetCurrentDirectory(), $"test-reports-{Guid.NewGuid():N}");

    public TestReportReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ReadsNamespacedTrxWithoutUsingAggregateCountersOrStdout()
    {
        var path = Write("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="works" outcome="Passed" />
                <UnitTestResult testName="broken" outcome="Failed">
                  <Output><ErrorInfo><Message>Expected true</Message>
                    <StackTrace>at Example.Test() in C:\repo\Test.cs:line 42</StackTrace>
                  </ErrorInfo><StdOut>SECRET OUTPUT</StdOut></Output>
                </UnitTestResult>
                <UnitTestResult testName="later" outcome="NotExecuted" />
                <UnitTestResult testName="crashed" outcome="Error" />
              </Results>
              <ResultSummary><Counters total="900" passed="899" failed="1" /></ResultSummary>
            </TestRun>
            """);

        var summary = TestReportReader.Read([path]);

        AssertCounts(summary, 4, 1, 2, 1);
        var failure = summary.Failures[0];
        Assert.Equal("broken", failure.Name);
        Assert.Contains("Expected true", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET OUTPUT", failure.Message, StringComparison.Ordinal);
        Assert.Equal(@"C:\repo\Test.cs", failure.File);
        Assert.Equal(42, failure.Line);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("junit")]
    [InlineData(" JUNIT ")]
    public void ReadsNestedJunitCasesAndBothFailureKinds(string format)
    {
        var path = Write("""
            <testsuites tests="300" failures="100">
              <testsuite name="outer" tests="300">
                <testsuite name="inner" tests="300">
                  <testcase name="works" classname="Example" />
                  <testcase name="broken" classname="Example" file="tests/example.py" line="12">
                    <failure message="Expected 1">AssertionError: expected 1
                    traceback details</failure><system-out>SECRET OUTPUT</system-out>
                  </testcase>
                  <testcase name="crash"><error message="setup failed">stack details</error></testcase>
                  <testcase name="later"><skipped message="not now" /></testcase>
                </testsuite>
              </testsuite>
            </testsuites>
            """);

        var summary = TestReportReader.Read([path], format);

        AssertCounts(summary, 4, 1, 2, 1);
        Assert.Equal("Example.broken", summary.Failures[0].Name);
        Assert.Contains("Expected 1", summary.Failures[0].Message, StringComparison.Ordinal);
        Assert.Contains("traceback details", summary.Failures[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET OUTPUT", summary.Failures[0].Message, StringComparison.Ordinal);
        Assert.Equal("tests/example.py", summary.Failures[0].File);
        Assert.Equal(12, summary.Failures[0].Line);
        Assert.Contains("setup failed", summary.Failures[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test-results", "Success", "Failure", "Ignored")]
    [InlineData("test-run", "Passed", "Failed", "Skipped")]
    public void ReadsNunitTwoAndThree(string root, string pass, string fail, string skip)
    {
        var path = Write($$"""
            <{{root}} total="999" failures="998">
              <test-suite result="{{fail}}"><results>
                <test-case name="works" result="{{pass}}" />
                <test-case name="broken" fullname="Example.broken" result="{{fail}}">
                  <failure><message><![CDATA[Expected <true>]]></message>
                    <stack-trace>at Example() in /repo/test.cs:line 7</stack-trace>
                  </failure><output>SECRET OUTPUT</output>
                </test-case>
                <test-case name="later" result="{{skip}}" />
                <test-case name="crash" result="Error"><failure><message>setup error</message></failure></test-case>
              </results></test-suite>
            </{{root}}>
            """);

        var summary = TestReportReader.Read([path], "nunit");

        AssertCounts(summary, 4, 1, 2, 1);
        Assert.Equal("Example.broken", summary.Failures[0].Name);
        Assert.Contains("Expected <true>", summary.Failures[0].Message, StringComparison.Ordinal);
        Assert.Equal("/repo/test.cs", summary.Failures[0].File);
        Assert.Equal(7, summary.Failures[0].Line);
        Assert.DoesNotContain("SECRET OUTPUT", summary.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsXunitCasesWithoutCountingAssemblyOrCollectionTotals()
    {
        var path = Write("""
            <assemblies><assembly total="500" failed="499"><collection total="500" failed="499">
              <test name="works" result="Pass" />
              <test name="broken" result="Fail" source-file="Tests.cs" source-line="21">
                <failure exception-type="AssertionException">
                  <message>assertion failed</message><stack-trace>stack details</stack-trace>
                </failure><output>SECRET OUTPUT</output>
              </test>
              <test name="later" result="Skip"><reason><message>not now</message></reason></test>
            </collection></assembly></assemblies>
            """);

        var summary = TestReportReader.Read([path], "xunit");

        AssertCounts(summary, 3, 1, 1, 1);
        var failure = Assert.Single(summary.Failures);
        Assert.Equal("broken", failure.Name);
        Assert.Contains("assertion failed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("stack details", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET OUTPUT", failure.Message, StringComparison.Ordinal);
        Assert.Equal("Tests.cs", failure.File);
        Assert.Equal(21, failure.Line);
    }

    [Theory]
    [InlineData("<TestRun />", "trx")]
    [InlineData("<testsuite />", "junit")]
    [InlineData("<testsuites />", "junit")]
    [InlineData("<test-results />", "nunit")]
    [InlineData("<test-run />", "nunit")]
    [InlineData("<assemblies />", "xunit")]
    [InlineData("<assembly />", "xunit")]
    public void DetectsSupportedRootsWithoutDependingOnExtension(string xml, string expected)
    {
        var path = Write(xml);

        Assert.True(TestReportReader.IsSupported(path));
        Assert.True(TestReportReader.TryDetectFormat(path, out var detected));
        Assert.Equal(expected, detected);
        Assert.Equal(expected, TestReportReader.DetectFormat(path));
        AssertCounts(TestReportReader.Read([path]), 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("<project><testcase name='not a report' /></project>")]
    [InlineData("<testsuite>")]
    [InlineData("")]
    [InlineData("<!DOCTYPE testsuite [<!ENTITY content 'no'>]><testsuite>&content;</testsuite>")]
    public void AutomaticDiscoveryRejectsUnsupportedOrMalformedXml(string xml)
    {
        var path = Write(xml);

        Assert.False(TestReportReader.IsSupported(path));
        Assert.False(TestReportReader.TryDetectFormat(path, out var detected));
        Assert.Null(detected);
        Assert.ThrowsAny<Exception>(() => TestReportReader.Read([path]));
        Assert.ThrowsAny<Exception>(() => TestReportReader.Read([path], "junit"));
    }

    [Fact]
    public void ExplicitFormatRejectsMismatchedReport()
    {
        var path = Write("<TestRun />");

        Assert.Throws<InvalidDataException>(() => TestReportReader.Read([path], "junit"));
    }

    [Fact]
    public void RejectsInvalidFormatEvenWithNoFiles()
    {
        Assert.Throws<ArgumentException>(() => TestReportReader.Read([], "xml"));
    }

    [Fact]
    public void RejectsUnknownOutcomeInsteadOfAssumingSuccess()
    {
        var path = Write("<assemblies><assembly><test name='unknown' result='banana' /></assembly></assemblies>");

        Assert.Throws<InvalidDataException>(() => TestReportReader.Read([path]));
    }

    [Fact]
    public void ReadsNunitLegacyExecutionAttributes()
    {
        var path = Write("""
            <test-results><test-suite><results>
              <test-case name="pass" executed="True" success="True" />
              <test-case name="fail" executed="True" success="False" />
              <test-case name="skip" executed="False" success="False" />
            </results></test-suite></test-results>
            """);

        AssertCounts(TestReportReader.Read([path]), 3, 1, 1, 1);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-line")]
    [InlineData("2147483648")]
    public void DiscardsInvalidSourceLines(string line)
    {
        var path = Write($"""
            <testsuite><testcase name="bad" file="test.js" line="{line}">
              <failure message="failed" />
            </testcase></testsuite>
            """);

        var failure = Assert.Single(TestReportReader.Read([path]).Failures);

        Assert.Equal("test.js", failure.File);
        Assert.Null(failure.Line);
    }

    [Fact]
    public void ReadsFailureLevelLocationsAndExcludesNestedCapturedOutput()
    {
        var path = Write("""
            <testsuite><testcase name="bad">
              <failure file="test.js" line="8" message="failed"><system-out>SECRET</system-out></failure>
            </testcase></testsuite>
            """);

        var failure = Assert.Single(TestReportReader.Read([path]).Failures);

        Assert.Equal("failed", failure.Message);
        Assert.Equal("test.js", failure.File);
        Assert.Equal(8, failure.Line);
    }

    [Fact]
    public void ReadsDirectStackLocation()
    {
        var path = Write("""
            <testsuite><testcase name="bad"><error>at /repo/test.js:13</error></testcase></testsuite>
            """);

        var failure = Assert.Single(TestReportReader.Read([path]).Failures);

        Assert.Equal("/repo/test.js", failure.File);
        Assert.Equal(13, failure.Line);
    }

    [Fact]
    public void DiscoverySkipsMissingFilesButReadFails()
    {
        var path = Path.Join(_root, "missing.xml");

        Assert.False(TestReportReader.IsSupported(path));
        Assert.Throws<FileNotFoundException>(() => TestReportReader.Read([path]));
    }

    [Theory]
    [InlineData("<TestRun><UnitTestResult testName='bad' outcome='Failed'><Output><ErrorInfo><Message>failed</Message></ErrorInfo><StdOut>stdout</StdOut><StdErr>stderr</StdErr></Output></UnitTestResult></TestRun>")]
    [InlineData("<testsuite><testcase name='bad'><failure message='failed'/><system-out>stdout</system-out><system-err>stderr</system-err></testcase></testsuite>")]
    [InlineData("<test-run><test-case name='bad' result='Failed'><failure><message>failed</message></failure><output>stdout and stderr</output></test-case></test-run>")]
    [InlineData("<assemblies><assembly><test name='bad' result='Fail'><failure><message>failed</message></failure><output>stdout and stderr</output></test></assembly></assemblies>")]
    public void IncludesCapturedOutputOnlyWhenExplicitlyRequested(string xml)
    {
        var path = Write(xml);

        var defaultFailure = Assert.Single(TestReportReader.Read([path]).Failures);
        var optedInFailure = Assert.Single(TestReportReader.Read([path], includeOutput: true).Failures);

        Assert.Equal("failed", defaultFailure.Message);
        Assert.Contains("stdout", optedInFailure.Message, StringComparison.Ordinal);
        Assert.Contains("stderr", optedInFailure.Message, StringComparison.Ordinal);
        Assert.Equal(defaultFailure.Name, optedInFailure.Name);
    }

    [Fact]
    public void StrictDetectionSkipsUnknownRootsButThrowsForMalformedXml()
    {
        Assert.Null(TestReportReader.DetectFormat(Write("<project />")));
        Assert.Throws<System.Xml.XmlException>(() => TestReportReader.DetectFormat(Write("<testsuite>")));
        Assert.Throws<System.Xml.XmlException>(() => TestReportReader.DetectFormat(
            Write("<!DOCTYPE testsuite [<!ENTITY secret 'no'>]><testsuite>&secret;</testsuite>")));
    }

    [Fact]
    public void RejectsOversizedXml()
    {
        var path = Write("<testsuite />");
        using (var stream = File.OpenWrite(path))
        {
            stream.SetLength((16 * 1024 * 1024) + 1);
        }

        Assert.False(TestReportReader.IsSupported(path));
        Assert.Throws<InvalidDataException>(() => TestReportReader.Read([path]));
    }

    [Fact]
    public void CombinesDifferentFormatsAndProvidesFallbackFailureText()
    {
        var junit = Write("<testsuite><testcase name='ok' /><testcase name='bad'><failure /></testcase></testsuite>");
        var trx = Write("<TestRun><Results><UnitTestResult testName='later' outcome='NotExecuted' /></Results></TestRun>");

        var summary = TestReportReader.Read([junit, trx]);

        AssertCounts(summary, 3, 1, 1, 1);
        var failure = Assert.Single(summary.Failures);
        Assert.Equal("bad", failure.Name);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.Null(failure.File);
        Assert.Null(failure.Line);
    }

    private string Write(string xml)
    {
        var path = Path.Join(_root, $"{Guid.NewGuid():N}.data");
        File.WriteAllText(path, xml);
        return path;
    }

    private static void AssertCounts(TestReportSummary summary, int total, int passed, int failed, int skipped)
    {
        Assert.Equal(total, summary.Total);
        Assert.Equal(passed, summary.Passed);
        Assert.Equal(failed, summary.Failed);
        Assert.Equal(skipped, summary.Skipped);
        Assert.Equal(failed, summary.Failures.Count);
    }
}
