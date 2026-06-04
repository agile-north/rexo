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
            result = ApplyFilter(segments[i].Trim(), result);
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

    private static string ApplyFilter(string filterExpr, string result)
    {
        var parenIndex = filterExpr.IndexOf('(', StringComparison.Ordinal);
        string filterName;
        string? filterArg;

        if (parenIndex >= 0)
        {
            filterName = filterExpr[..parenIndex].Trim();
            var closeIndex = filterExpr.LastIndexOf(')');
            filterArg = closeIndex > parenIndex
                ? filterExpr[(parenIndex + 1)..closeIndex].Trim().Trim('\'', '"')
                : string.Empty;
        }
        else
        {
            filterName = filterExpr.Trim();
            filterArg = null;
        }

        return filterName switch
        {
            "default" => string.IsNullOrEmpty(result) ? (filterArg ?? string.Empty) : result,
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
            "prefix" when filterArg is not null => string.IsNullOrEmpty(result) ? string.Empty : filterArg + result,
            "suffix" when filterArg is not null => string.IsNullOrEmpty(result) ? string.Empty : result + filterArg,
            "replace" when filterArg is not null => ApplyLiteralReplace(result, filterArg),
            "replacePattern" when filterArg is not null => ApplyRegexReplace(result, filterArg),
            "truncate" when filterArg is not null && int.TryParse(filterArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var len)
                => result.Length > len ? result[..len] : result,
            "first" when filterArg is not null && int.TryParse(filterArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                => result.Length > n ? result[..n] : result,
            _ => result,
        };
    }

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
            ["ci"] = ci,
            ["steps"] = steps,
            ["outputs"] = context.ResolvedOutputs,
            ["settings"] = context.ResolvedSettings,
            ["vars"] = context.ResolvedVars,
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

    private static string Slug(string value) =>
        SlugCleanPattern.Replace(value.ToLowerInvariant(), "-").Trim('-');

    private static string ComputeSha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ApplyLiteralReplace(string value, string filterArg)
    {
        if (!TrySplitReplaceArgs(filterArg, out var oldValue, out var newValue))
        {
            return value;
        }

        oldValue = TrimQuotedString(oldValue);
        newValue = TrimQuotedString(newValue);
        return value.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string ApplyRegexReplace(string value, string filterArg)
    {
        if (!TrySplitReplaceArgs(filterArg, out var oldValue, out var newValue))
        {
            return value;
        }

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

    private static bool TrySplitReplaceArgs(string filterArg, out string oldValue, out string newValue)
    {
        oldValue = string.Empty;
        newValue = string.Empty;

        var hasRegexLiteral = false;
        var regexEnd = -1;
        if (filterArg.Length > 0 && filterArg[0] == '/')
        {
            var escaped = false;
            for (var i = 1; i < filterArg.Length; i++)
            {
                var c = filterArg[i];
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
                    hasRegexLiteral = true;
                    regexEnd = i;
                    break;
                }
            }
        }

        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapedChar = false;

        for (var i = 0; i < filterArg.Length; i++)
        {
            var c = filterArg[i];
            if (escapedChar)
            {
                escapedChar = false;
                continue;
            }

            if (c == '\\')
            {
                escapedChar = true;
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
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (c == ',' && (!hasRegexLiteral || i > regexEnd))
            {
                oldValue = filterArg[..i];
                newValue = filterArg[(i + 1)..];
                return true;
            }
        }

        return false;
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
