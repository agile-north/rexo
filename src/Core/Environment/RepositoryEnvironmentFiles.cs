namespace Rexo.Core.Environment;

/// <summary>
/// Loads repository-scoped environment files used as fallbacks to process environment values.
/// Merge precedence is root .env, then .rexo/.env.
/// </summary>
public static class RepositoryEnvironmentFiles
{
    public static IReadOnlyDictionary<string, string> Load(string repositoryRoot)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeFile(Path.Combine(repositoryRoot, ".env"), values);
        MergeFile(Path.Combine(repositoryRoot, ".rexo", ".env"), values);
        return values;
    }

    private static void MergeFile(string path, Dictionary<string, string> target)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadAllLines(path).Select(rawLine => rawLine.Trim()))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }

            target[key] = value;
        }
    }
}
