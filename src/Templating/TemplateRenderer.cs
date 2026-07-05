namespace Rexo.Templating;

using System.Text.RegularExpressions;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;

public sealed class TemplateRenderer : ITemplateRenderer
{
    private static readonly Regex ExpressionPattern =
        new(@"\{\{([^}]+)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    private static readonly Regex SlugCleanPattern =
        new(@"[^a-z0-9]+", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    public string Render(string templateText, ExecutionContext context)
    {
        var root = BuildContext(context);
        return ExpressionPattern.Replace(templateText, match =>
            EvaluateExpression(match.Groups[1].Value.Trim(), root, context));
    }

    private static string EvaluateExpression(string expr, Dictionary<string, object?> root, ExecutionContext context)
    {
        // Check for equality/inequality expressions before pipe-filter handling
        if (expr.Contains(" == ", StringComparison.Ordinal))
        {
            var idx = expr.IndexOf(" == ", StringComparison.Ordinal);
            var left = ResolveOperand(expr[..idx].Trim(), root, context);
            var right = ResolveOperand(expr[(idx + 4)..].Trim(), root, context);
            return string.Equals(left, right, StringComparison.Ordinal) ? "true" : "false";
        }

        if (expr.Contains(" != ", StringComparison.Ordinal))
        {
            var idx = expr.IndexOf(" != ", StringComparison.Ordinal);
            var left = ResolveOperand(expr[..idx].Trim(), root, context);
            var right = ResolveOperand(expr[(idx + 4)..].Trim(), root, context);
            return !string.Equals(left, right, StringComparison.Ordinal) ? "true" : "false";
        }

        return ResolveOperand(expr, root, context);
    }

    private static string ResolveOperand(string expr, Dictionary<string, object?> root, ExecutionContext context)
    {
        var coalescingSegments = SplitCoalesceChain(expr);
        if (coalescingSegments.Count > 1)
        {
            return ResolveCoalescedValue(coalescingSegments, root, context);
        }

        var segments = SplitFilterChain(expr);
        var path = segments[0].Trim();

        if (segments.Count == 1)
        {
            return ResolveValue(path, root, context);
        }

        var value = ResolvePath(path, root, context);
        var result = value?.ToString() ?? string.Empty;

        for (var i = 1; i < segments.Count; i++)
        {
            result = ApplyFilter(segments[i].Trim(), result, root, context);
        }

        return result;
    }

    /// <summary>Splits a filter-chain expression on <c>|</c> characters that are not inside parentheses.</summary>
    private static List<string> SplitFilterChain(string expr)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') depth--;
            else if (expr[i] == '|' && depth == 0)
            {
                segments.Add(expr[start..i]);
                start = i + 1;
            }
        }

        segments.Add(expr[start..]);
        return segments;
    }

    private static string ApplyFilter(string filterExpr, string result, Dictionary<string, object?> root, ExecutionContext context)
    {
        ParseFilterExpression(filterExpr, out var filterName, out var filterArgs);

        return filterName switch
        {
            "default" => IsEmptyValue(result)
                ? ResolveCoalescedValue(filterArgs, root, context)
                : result,
            "coalesce" => IsEmptyValue(result)
                ? ResolveCoalescedValue(filterArgs, root, context)
                : result,
            "slug" => Slug(result),
            "upper" => result.ToUpperInvariant(),
            "lower" => result.ToLowerInvariant(),
            "trim" => result.Trim(),
            "basename" => Path.GetFileName(result),
            "dirname" => Path.GetDirectoryName(result) ?? string.Empty,
            "fileext" => Path.GetExtension(result),
            "filestem" => Path.GetFileNameWithoutExtension(result),
            "urlencode" => Uri.EscapeDataString(result),
            "sha256" => ComputeSha256Hex(result),
            "prefix" when filterArgs.Count == 1 => IsEmptyValue(result) ? string.Empty : TrimQuotedString(filterArgs[0]) + result,
            "suffix" when filterArgs.Count == 1 => IsEmptyValue(result) ? string.Empty : result + TrimQuotedString(filterArgs[0]),
            "replace" when filterArgs.Count == 2 => ApplyLiteralReplace(result, filterArgs[0], filterArgs[1]),
            "replacePattern" when filterArgs.Count == 2 => ApplyRegexReplace(result, filterArgs[0], filterArgs[1]),
            "truncate" when filterArgs.Count == 1 && int.TryParse(TrimQuotedString(filterArgs[0]), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var len)
                => result.Length > len ? result[..len] : result,
            "first" when filterArgs.Count == 1 && int.TryParse(TrimQuotedString(filterArgs[0]), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                => result.Length > n ? result[..n] : result,
            _ => result,
        };
    }

    private static void ParseFilterExpression(string filterExpr, out string filterName, out IReadOnlyList<string> filterArgs)
    {
        var parenIndex = filterExpr.IndexOf('(', StringComparison.Ordinal);
        if (parenIndex < 0)
        {
            filterName = filterExpr.Trim();
            filterArgs = [];
            return;
        }

        filterName = filterExpr[..parenIndex].Trim();
        var closeIndex = filterExpr.LastIndexOf(')');
        if (closeIndex <= parenIndex)
        {
            filterArgs = [string.Empty];
            return;
        }

        filterArgs = SplitFilterArguments(filterExpr[(parenIndex + 1)..closeIndex]);
    }

    private static IReadOnlyList<string> SplitFilterArguments(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
        {
            return [];
        }

        var args = new List<string>();
        var depth = 0;
        var start = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inRegexLiteral = false;
        var escaped = false;
        var atArgumentStart = true;

        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];

            if (inRegexLiteral)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '/')
                {
                    inRegexLiteral = false;
                }

                continue;
            }

            if (escaped)
            {
                escaped = false;
                atArgumentStart = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                atArgumentStart = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (c == '\'')
                {
                    inSingleQuote = false;
                }

                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '"')
                {
                    inDoubleQuote = false;
                }

                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                atArgumentStart = false;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                atArgumentStart = false;
                continue;
            }

            if (c == '/' && atArgumentStart && HasClosingRegexDelimiter(expr, i))
            {
                inRegexLiteral = true;
                atArgumentStart = false;
                continue;
            }

            if (c == '(')
            {
                depth++;
                atArgumentStart = false;
                continue;
            }

            if (c == ')')
            {
                depth--;
                atArgumentStart = false;
                continue;
            }

            if (c == ',' && depth == 0)
            {
                args.Add(expr[start..i].Trim());
                start = i + 1;
                atArgumentStart = true;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                atArgumentStart = false;
            }
        }

        args.Add(expr[start..].Trim());
        return args;
    }

    private static IReadOnlyList<string> SplitCoalesceChain(string expr)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inRegexLiteral = false;
        var escaped = false;
        var atSegmentStart = true;

        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];

            if (inRegexLiteral)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '/')
                {
                    inRegexLiteral = false;
                }

                continue;
            }

            if (escaped)
            {
                escaped = false;
                atSegmentStart = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                atSegmentStart = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (c == '\'')
                {
                    inSingleQuote = false;
                }

                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '"')
                {
                    inDoubleQuote = false;
                }

                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                atSegmentStart = false;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                atSegmentStart = false;
                continue;
            }

            if (c == '/' && atSegmentStart && HasClosingRegexDelimiter(expr, i))
            {
                inRegexLiteral = true;
                atSegmentStart = false;
                continue;
            }

            if (c == '(')
            {
                depth++;
                atSegmentStart = false;
                continue;
            }

            if (c == ')')
            {
                depth--;
                atSegmentStart = false;
                continue;
            }

            if (c == '?' && i + 1 < expr.Length && expr[i + 1] == '?' && depth == 0)
            {
                segments.Add(expr[start..i].Trim());
                start = i + 2;
                i++;
                atSegmentStart = true;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                atSegmentStart = false;
            }
        }

        if (segments.Count == 0)
        {
            return [expr];
        }

        segments.Add(expr[start..].Trim());
        return segments;
    }

    private static bool HasClosingRegexDelimiter(string expr, int startIndex)
    {
        var escaped = false;
        for (var i = startIndex + 1; i < expr.Length; i++)
        {
            var c = expr[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '/')
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveCoalescedValue(IReadOnlyList<string> filterArgs, Dictionary<string, object?> root, ExecutionContext context)
    {
        for (var i = 0; i < filterArgs.Count; i++)
        {
            var value = ResolveOperand(filterArgs[i], root, context);
            if (!IsEmptyValue(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool IsEmptyValue(string value) => string.IsNullOrWhiteSpace(value);

    private static object? ResolvePath(string path, Dictionary<string, object?> root, ExecutionContext context)
    {
        if (path.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
        {
            return context.GetEnvironmentValue(path[4..]);
        }

        var parts = path.Split('.');
        object? current = root;

        foreach (var part in parts)
        {
            current = current switch
            {
                Dictionary<string, object?> d when d.TryGetValue(part, out var v) => v,
                IReadOnlyDictionary<string, object?> rd when rd.TryGetValue(part, out var v) => v,
                _ => null,
            };

            if (current is null) return null;
        }

        return current;
    }

    /// <summary>
    /// Resolves a value that may be a quoted string literal (single or double quotes)
    /// or a context path reference. Returns the string value in either case.
    /// </summary>
    private static string ResolveValue(string expr, Dictionary<string, object?> root, ExecutionContext context)
    {
        if (expr.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "true";
        }

        if (expr.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return "false";
        }

        if (expr.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (expr.Length >= 2 &&
            ((expr[0] == '\'' && expr[^1] == '\'') ||
             (expr[0] == '"' && expr[^1] == '"')))
        {
            return expr[1..^1];
        }

        return ResolvePath(expr, root, context)?.ToString() ?? string.Empty;
    }

    private static Dictionary<string, object?> BuildContext(ExecutionContext context)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in context.Args) args[kv.Key] = kv.Value;

        var options = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in context.Options) options[kv.Key] = kv.Value;

        var repo = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = context.RepositoryRoot,
            ["branch"] = context.Branch,
            ["commitSha"] = context.CommitSha,
            ["shortSha"] = context.ShortSha,
            ["remoteUrl"] = context.RemoteUrl,
        };

        var git = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["branch"] = context.Branch,
            ["commitSha"] = context.CommitSha,
            ["shortSha"] = context.ShortSha,
            ["remoteUrl"] = context.RemoteUrl,
            ["isCleanWorkingTree"] = context.IsCleanWorkingTree.ToString().ToLowerInvariant(),
        };

        var ci = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["isCi"] = context.IsCi.ToString().ToLowerInvariant(),
            ["provider"] = context.CiProvider,
            ["buildId"] = context.CiBuildId,
            ["runNumber"] = context.CiRunNumber,
            ["workflowName"] = context.CiWorkflowName,
            ["actor"] = context.CiActor,
            ["tag"] = context.CiTag,
            ["buildUrl"] = context.CiBuildUrl,
            ["isPullRequest"] = context.IsPullRequest.ToString().ToLowerInvariant(),
        };

        var steps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in context.CompletedSteps)
        {
            var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in kv.Value.Outputs) outputs[o.Key] = o.Value;
            steps[kv.Key] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["outputs"] = outputs,
                ["exitCode"] = kv.Value.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["success"] = kv.Value.Success.ToString().ToLowerInvariant(),
            };
        }

        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["args"] = args,
            ["options"] = options,
            ["repo"] = repo,
            ["git"] = git,
            ["ci"] = ci,
            ["steps"] = steps,
            ["outputs"] = context.ResolvedOutputs,
            ["settings"] = context.ResolvedSettings,
            ["vars"] = context.ResolvedVars,
            ["push"] = BuildPushContext(context),
        };

        if (context.Version is not null)
        {
            root["version"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["semver"] = context.Version.SemVer,
                ["major"] = context.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["minor"] = context.Version.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["patch"] = context.Version.Patch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["prerelease"] = context.Version.PreRelease,
                ["preReleaseTag"] = context.Version.PreReleaseTag,
                ["preReleaseLabel"] = context.Version.PreReleaseLabel,
                ["preReleaseNumber"] = context.Version.PreReleaseNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["preReleaseLabelWithDash"] = context.Version.PreReleaseLabelWithDash,
                ["preReleaseTagWithDash"] = context.Version.PreReleaseTagWithDash,
                ["commitSha"] = context.Version.CommitSha,
                ["shortSha"] = context.Version.ShortSha,
                ["isPrerelease"] = context.Version.IsPreRelease.ToString().ToLowerInvariant(),
                ["isStable"] = context.Version.IsStable.ToString().ToLowerInvariant(),
            };
        }

        return root;
    }

    private static IReadOnlyDictionary<string, object?> BuildPushContext(ExecutionContext context)
    {
        var artifacts = new List<ArtifactManifestEntry>();
        var decisions = new List<PushDecision>();

        foreach (var step in context.CompletedSteps.Values)
        {
            if (step.Outputs.TryGetValue("__artifacts", out var artifactsObj)
                && artifactsObj is IEnumerable<ArtifactManifestEntry> artifactEntries)
            {
                artifacts.AddRange(artifactEntries);
            }

            if (step.Outputs.TryGetValue("__pushDecisions", out var decisionsObj)
                && decisionsObj is IEnumerable<PushDecision> pushEntries)
            {
                decisions.AddRange(pushEntries);
            }
        }

        var pushedCount = artifacts.Count(a => a.Pushed);
        var deniedDecisions = decisions.Where(d => !d.Allowed).ToList();
        var blockReasons = deniedDecisions
            .Select(d => d.Reason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hasData"] = (artifacts.Count > 0 || decisions.Count > 0).ToString().ToLowerInvariant(),
            ["anyPushed"] = (pushedCount > 0).ToString().ToLowerInvariant(),
            ["pushedCount"] = pushedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["artifactCount"] = artifacts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["decisionCount"] = decisions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["allowedCount"] = decisions.Count(d => d.Allowed).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deniedCount"] = deniedDecisions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["anyBlocked"] = (deniedDecisions.Count > 0).ToString().ToLowerInvariant(),
            ["blockReasons"] = string.Join(" | ", blockReasons),
        };
    }

    private static string Slug(string value) =>
        SlugCleanPattern.Replace(value.ToLowerInvariant(), "-").Trim('-');

    private static string ComputeSha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ApplyLiteralReplace(string value, string oldValue, string newValue)
    {
        oldValue = TrimQuotedString(oldValue);
        newValue = TrimQuotedString(newValue);
        return value.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string ApplyRegexReplace(string value, string oldValue, string newValue)
    {
        oldValue = TrimQuotedString(oldValue);
        newValue = TrimQuotedString(newValue);

        if (!TryParseRegexLiteral(oldValue, out var pattern, out var options))
        {
            return value;
        }

        return Regex.Replace(value, pattern, newValue, options);
    }

    private static string TrimQuotedString(string value)
    {
        value = value.Trim();

        if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '\'' || value[0] == '"'))
        {
            return value[1..^1];
        }

        if (value.Length >= 1 && (value[0] == '\'' || value[0] == '"'))
        {
            return value[1..];
        }

        if (value.Length >= 1 && (value[^1] == '\'' || value[^1] == '"'))
        {
            return value[..^1];
        }

        return value;
    }
    private static bool TryParseRegexLiteral(string input, out string pattern, out RegexOptions options)
    {
        pattern = string.Empty;
        options = RegexOptions.None;

        if (input.Length < 2 || input[0] != '/')
        {
            return false;
        }

        var escaped = false;
        var end = -1;
        for (var i = 1; i < input.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (input[i] == '\\')
            {
                escaped = true;
                continue;
            }

            if (input[i] == '/')
            {
                end = i;
                break;
            }
        }

        if (end < 1)
        {
            return false;
        }

        pattern = input[1..end];
        var flags = end < input.Length - 1 ? input[(end + 1)..] : string.Empty;
        foreach (var flag in flags)
        {
            if (flag == 'i')
            {
                options |= RegexOptions.IgnoreCase;
            }
        }

        return true;
    }
}
