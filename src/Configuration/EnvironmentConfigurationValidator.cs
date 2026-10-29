namespace Rexo.Configuration;

using System.Text.RegularExpressions;
using Rexo.Configuration.Models;

public static partial class EnvironmentConfigurationValidator
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableNameRegex();

    public static void Validate(RepoConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateSourceBranch(config.EnvironmentSelection?.SourceBranch);

        var environments = config.Environments;
        if (environments is null || environments.Count == 0)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, environment) in environments)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FormatException("Environment names must not be empty.");
            }

            if (!names.Add(name))
            {
                throw new FormatException($"Environment name '{name}' duplicates another name ignoring case.");
            }

            if (environment.Selection?.Branches is { } branches)
            {
                BranchGlobMatcher.ValidatePatterns(branches, $"environments.{name}.selection.branches");
            }

            if (environment.Promotion is { } promotion && string.IsNullOrWhiteSpace(promotion.Path))
            {
                throw new FormatException($"environments.{name}.promotion.path must not be empty.");
            }
        }

        ValidateDuplicateLiteralMappings(environments);
    }

    private static void ValidateSourceBranch(RepoSourceBranchConfig? sourceBranch)
    {
        if (sourceBranch?.Variable is { } variable &&
            !EnvironmentVariableNameRegex().IsMatch(variable))
        {
            throw new FormatException(
                "environmentSelection.sourceBranch.variable must be a valid environment-variable name.");
        }

        if (sourceBranch?.Fallback is { } fallback &&
            !fallback.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !fallback.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                "environmentSelection.sourceBranch.fallback must be 'auto' or 'none'.");
        }
    }

    private static void ValidateDuplicateLiteralMappings(
        IReadOnlyDictionary<string, RepoEnvironmentConfig> environments)
    {
        var literalBranches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var environment in environments.Values)
        {
            foreach (var pattern in environment.Selection?.Branches ?? [])
            {
                if (pattern[0] != '!' && BranchGlobMatcher.TryGetLiteral(pattern, out var literal))
                {
                    _ = literalBranches.Add(literal);
                }
            }
        }

        foreach (var branch in literalBranches)
        {
            var matches = environments
                .Where(entry => entry.Value.Selection?.Branches is { Count: > 0 } patterns &&
                    BranchGlobMatcher.Evaluate(patterns, branch).IsMatch)
                .Select(entry => entry.Key)
                .ToArray();
            if (matches.Length > 1)
            {
                throw new FormatException(
                    $"Branch '{branch}' is mapped to multiple environments: {string.Join(", ", matches)}. " +
                    "Remove duplicate exact-branch mappings or make the selectors mutually exclusive.");
            }
        }
    }
}
