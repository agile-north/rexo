namespace Rexo.Configuration;

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

public sealed class BranchGlobMatcher
{
    public const int MaximumPatternLength = 512;
    public const int MaximumPatternsPerEnvironment = 128;
    public const int MaximumBranchLength = 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Regex> CompiledPatterns = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string branch) =>
        CompiledPatterns.GetOrAdd(pattern, static value => Compile(value)).IsMatch(branch);

    public static BranchGlobEvaluation Evaluate(
        IReadOnlyList<string> patterns,
        string branch)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(branch);
        if (branch.Length > MaximumBranchLength)
        {
            throw new ArgumentException($"Branch names cannot exceed {MaximumBranchLength} characters.", nameof(branch));
        }

        var matched = false;
        string? matchedPattern = null;
        var evaluations = new List<BranchPatternEvaluation>(patterns.Count);
        foreach (var pattern in patterns)
        {
            var compiled = CompiledPatterns.GetOrAdd(pattern, static value => Compile(value));
            var patternMatched = compiled.IsMatch(branch);
            if (patternMatched)
            {
                var isExclusion = pattern[0] == '!';
                matched = !isExclusion;
                matchedPattern = matched ? pattern : null;
            }

            evaluations.Add(new BranchPatternEvaluation(pattern, patternMatched, matched));
        }

        return new BranchGlobEvaluation(matched, matchedPattern, evaluations);
    }

    public static void ValidatePatterns(IReadOnlyList<string> patterns, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count > MaximumPatternsPerEnvironment)
        {
            throw new FormatException(
                $"{sourcePath} may contain at most {MaximumPatternsPerEnvironment} branch patterns.");
        }

        var hasPositivePattern = false;
        for (var index = 0; index < patterns.Count; index++)
        {
            var pattern = patterns[index];
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new FormatException($"{sourcePath}[{index}] must not be empty.");
            }

            if (pattern.Length > MaximumPatternLength)
            {
                throw new FormatException(
                    $"{sourcePath}[{index}] exceeds the {MaximumPatternLength}-character limit.");
            }

            hasPositivePattern |= pattern[0] != '!';
            _ = CompiledPatterns.GetOrAdd(pattern, static value => Compile(value));
        }

        if (!hasPositivePattern)
        {
            throw new FormatException($"{sourcePath} must contain at least one positive branch pattern.");
        }
    }

    public static bool TryGetLiteral(string pattern, out string literal)
    {
        var start = pattern.StartsWith('!') ? 1 : 0;
        var builder = new StringBuilder(pattern.Length);
        for (var index = start; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '\\')
            {
                if (++index >= pattern.Length)
                {
                    literal = string.Empty;
                    return false;
                }

                builder.Append(pattern[index]);
            }
            else if (character is '*' or '?' or '+' or '[' or ']')
            {
                literal = string.Empty;
                return false;
            }
            else
            {
                builder.Append(character);
            }
        }

        literal = builder.ToString();
        return literal.Length > 0;
    }

    private static Regex Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaximumPatternLength)
        {
            throw new FormatException($"Branch patterns must contain 1 to {MaximumPatternLength} characters.");
        }

        var exclusion = pattern[0] == '!';
        var patternStart = exclusion ? 1 : 0;
        if (patternStart == pattern.Length)
        {
            throw new FormatException("An exclusion branch pattern must include a pattern after '!'.");
        }

        var atoms = new List<PatternAtom>();
        for (var index = patternStart; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '\\')
            {
                if (++index >= pattern.Length || !IsEscapable(pattern[index]))
                {
                    throw new FormatException($"Branch pattern '{pattern}' contains an invalid escape sequence.");
                }

                atoms.Add(new PatternAtom(Regex.Escape(pattern[index].ToString()), true));
                continue;
            }

            if (character == '*')
            {
                var doubleStar = index + 1 < pattern.Length && pattern[index + 1] == '*';
                if (doubleStar)
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        throw new FormatException($"Branch pattern '{pattern}' contains unsupported '***' syntax.");
                    }
                }

                if (doubleStar && index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    index++;
                    atoms.Add(new PatternAtom("(?:.*/)?", false));
                }
                else
                {
                    atoms.Add(new PatternAtom(doubleStar ? ".*" : "[^/]*", false));
                }

                continue;
            }

            if (character == '[')
            {
                var (classPattern, endIndex) = ParseCharacterClass(pattern, index);
                atoms.Add(new PatternAtom(classPattern, true));
                index = endIndex;
                continue;
            }

            if (character == '?')
            {
                atoms.Add(new PatternAtom("[^/]", false));
                continue;
            }

            if (character == '+')
            {
                if (atoms.Count == 0 ||
                    !atoms[^1].CanBeRepeated ||
                    atoms[^1].Quantifier != PatternQuantifier.None)
                {
                    throw new FormatException(
                        $"Branch pattern '{pattern}' uses '+' without one preceding literal or character class.");
                }

                atoms[^1] = atoms[^1] with { Quantifier = PatternQuantifier.OneOrMore };
                continue;
            }

            if (character is ']' or '{' or '}' or '(' or ')')
            {
                throw new FormatException(
                    $"Branch pattern '{pattern}' contains unsupported syntax '{character}'.");
            }

            atoms.Add(new PatternAtom(Regex.Escape(character.ToString()), true));
        }

        var expression = new StringBuilder(@"\A");
        foreach (var atom in atoms)
        {
            expression.Append(atom.Expression);
            expression.Append(atom.Quantifier switch
            {
                PatternQuantifier.OneOrMore => "+",
                _ => string.Empty,
            });
        }

        expression.Append(@"\z");
        try
        {
            return new Regex(
                expression.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking,
                RegexTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException($"Branch pattern '{pattern}' is malformed.", exception);
        }
    }

    private static (string pattern, int endIndex) ParseCharacterClass(string pattern, int startIndex)
    {
        var values = new List<(char Character, bool Escaped)>();
        var index = startIndex + 1;
        var negated = index < pattern.Length && pattern[index] == '!';
        if (negated)
        {
            index++;
        }

        var closed = false;
        while (index < pattern.Length)
        {
            if (pattern[index] == ']')
            {
                closed = true;
                break;
            }

            if (pattern[index] == '\\')
            {
                if (++index >= pattern.Length || !IsClassEscapable(pattern[index]))
                {
                    throw new FormatException($"Branch pattern '{pattern}' contains an invalid character-class escape.");
                }

                values.Add((pattern[index], true));
            }
            else
            {
                values.Add((pattern[index], false));
            }

            index++;
        }

        if (!closed || values.Count == 0)
        {
            throw new FormatException($"Branch pattern '{pattern}' contains a malformed character class.");
        }

        for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
        {
            if (values[valueIndex].Character != '-' || values[valueIndex].Escaped)
            {
                continue;
            }

            if (valueIndex == 0 || valueIndex == values.Count - 1 ||
                values[valueIndex - 1].Character > values[valueIndex + 1].Character)
            {
                throw new FormatException($"Branch pattern '{pattern}' contains a malformed character range.");
            }
        }

        var expression = new StringBuilder("[");
        if (negated)
        {
            expression.Append('^');
        }

        for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
        {
            var (character, escaped) = values[valueIndex];
            if (escaped || character is '\\' or ']' || (character == '^' && valueIndex == 0))
            {
                expression.Append('\\');
            }

            expression.Append(character);
        }

        expression.Append(']');
        return (expression.ToString(), index);
    }

    private static bool IsEscapable(char character) => character is '*' or '?' or '+' or '[' or ']' or '!' or '\\';

    private static bool IsClassEscapable(char character) => character is ']' or '-' or '\\' or '^' or '!';

    private sealed record PatternAtom(
        string Expression,
        bool CanBeRepeated)
    {
        public PatternQuantifier Quantifier { get; init; }
    }

    private enum PatternQuantifier
    {
        None,
        OneOrMore,
    }
}

public sealed record BranchPatternEvaluation(string Pattern, bool PatternMatched, bool MatchedAfterPattern);

public sealed record BranchGlobEvaluation(
    bool IsMatch,
    string? MatchedPattern,
    IReadOnlyList<BranchPatternEvaluation> Patterns);
