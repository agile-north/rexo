namespace Rexo.Execution;

/// <summary>
/// Collects secret values from environment variables and masks them in text output.
/// Env vars whose names (case-insensitive) contain any of the sentinel substrings
/// — SECRET, TOKEN, PASSWORD, KEY, APIKEY — are treated as secrets.
/// </summary>
public static class SecretMasker
{
    private static readonly string[] SentinelSubstrings =
        ["SECRET", "TOKEN", "PASSWORD", "APIKEY", "KEY"];

    private const string Redacted = "***";

    /// <summary>
    /// Scans the current process environment and returns the set of values that should
    /// be masked. Non-empty values from env vars whose names contain a sentinel
    /// substring are included.
    /// </summary>
    public static IReadOnlySet<string> CollectSecretValues()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;

            var nameUpper = name.ToUpperInvariant();
            foreach (var sentinel in SentinelSubstrings)
            {
                if (nameUpper.Contains(sentinel, StringComparison.Ordinal))
                {
                    secrets.Add(value);
                    break;
                }
            }
        }

        return secrets;
    }

    /// <summary>
    /// Scans the current process environment and includes additional runtime-resolved secret values.
    /// </summary>
    public static IReadOnlySet<string> CollectSecretValues(IEnumerable<string>? additionalSecrets)
    {
        var secrets = new HashSet<string>(CollectSecretValues(), StringComparer.Ordinal);
        if (additionalSecrets is null)
        {
            return secrets;
        }

        foreach (var value in additionalSecrets)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                secrets.Add(value);
            }
        }

        return secrets;
    }

    /// <summary>
    /// Replaces each secret value that appears in <paramref name="text"/> with
    /// <c>***</c>. Returns the original string if the secret set is empty.
    /// </summary>
    public static string Mask(string text, IReadOnlySet<string> secrets)
    {
        if (secrets.Count == 0 || string.IsNullOrEmpty(text)) return text;

        // Sort by descending length so longer secrets are replaced first, preventing
        // partial masking of a secret that is a prefix of a longer one.
        foreach (var secret in secrets.OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        }

        return text;
    }
}
