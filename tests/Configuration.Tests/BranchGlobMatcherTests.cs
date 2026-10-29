namespace Rexo.Configuration.Tests;

using Rexo.Configuration;

public sealed class BranchGlobMatcherTests
{
    [Theory]
    [InlineData("main", "main", true)]
    [InlineData("main", "main-qa", false)]
    [InlineData("feature/*", "feature/login", true)]
    [InlineData("feature/*", "feature/ui/login", false)]
    [InlineData("feature/**", "feature/ui/login", true)]
    [InlineData("release/**", "release/1.0", true)]
    [InlineData("release/**/hotfix-*", "release/hotfix-critical", true)]
    [InlineData("release/**/hotfix-*", "release/2026/hotfix-critical", true)]
    [InlineData("**/rexo.yaml", "rexo.yaml", true)]
    [InlineData("**/rexo.yaml", "repo/rexo.yaml", true)]
    [InlineData("v[12].*", "v2.0", true)]
    [InlineData("v[12].*", "v3.0", false)]
    [InlineData("v[0-9]+", "v123", true)]
    [InlineData("v[0-9]+", "v", false)]
    [InlineData("release/v1+", "release/v1", true)]
    [InlineData("release/v1+", "release/v111", true)]
    [InlineData("release/v1+", "release/v2", false)]
    [InlineData("release/**-alpha", "release/1.0-alpha", true)]
    [InlineData("name?", "nam", true)]
    [InlineData("name?", "name", true)]
    [InlineData("name?", "namex", false)]
    [InlineData("release/v1?", "release/v", true)]
    [InlineData("release/v1?", "release/v1", true)]
    [InlineData("release/v1?", "release/v11", false)]
    [InlineData("name?", "namx", false)]
    [InlineData("name?", "na", false)]
    [InlineData("release/\\[approved\\]", "release/[approved]", true)]
    [InlineData("\\!literal", "!literal", true)]
    [InlineData("Main", "main", false)]
    public void IsMatchSupportsDocumentedSyntax(string pattern, string branch, bool expected) =>
        Assert.Equal(expected, BranchGlobMatcher.IsMatch(pattern, branch));

    [Fact]
    public void EvaluateAppliesOrderedExclusionsAndReInclusions()
    {
        string[] patterns = ["release/**", "!release/**-alpha", "release/approved/**"];

        Assert.True(BranchGlobMatcher.Evaluate(patterns, "release/1.0").IsMatch);
        Assert.False(BranchGlobMatcher.Evaluate(patterns, "release/1.0-alpha").IsMatch);
        Assert.True(BranchGlobMatcher.Evaluate(patterns, "release/approved/1.0").IsMatch);
        Assert.True(BranchGlobMatcher.Evaluate(patterns, "release/approved/1.0-alpha").IsMatch);
        Assert.Equal("release/approved/**", BranchGlobMatcher.Evaluate(patterns, "release/approved/1.0-alpha").MatchedPattern);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!release/**")]
    [InlineData("[abc")]
    [InlineData("[]")]
    [InlineData("[z-a]")]
    [InlineData("v[0-9")]
    [InlineData("name\\")]
    [InlineData("name\\x")]
    [InlineData("+v")]
    [InlineData("*+")]
    [InlineData("***")]
    public void ValidatePatternsRejectsMalformedPatterns(string pattern) =>
        Assert.Throws<FormatException>(() => BranchGlobMatcher.ValidatePatterns([pattern], "environments.qa.selection.branches"));

    [Fact]
    public void IsMatchUsesWholeBranchName()
    {
        Assert.False(BranchGlobMatcher.IsMatch("feature/*", "refs/heads/feature/login"));
    }

    [Fact]
    public void ValidatePatternsEnforcesResourceLimits()
    {
        var tooLong = new string('a', BranchGlobMatcher.MaximumPatternLength + 1);
        var tooMany = Enumerable.Repeat("main", BranchGlobMatcher.MaximumPatternsPerEnvironment + 1).ToArray();

        Assert.Throws<FormatException>(() => BranchGlobMatcher.ValidatePatterns([tooLong], "environments.qa.selection.branches"));
        Assert.Throws<FormatException>(() => BranchGlobMatcher.ValidatePatterns(tooMany, "environments.qa.selection.branches"));
    }
}
