namespace Rexo.Policies;

/// <summary>
/// Provides access to the embedded policy template files shipped with the tool.
/// Templates can be listed and their JSON content read for use as policy sources or starting points.
/// </summary>
public static class EmbeddedPolicyTemplates
{
    private static readonly System.Reflection.Assembly Assembly = typeof(EmbeddedPolicyTemplates).Assembly;

    private const string ResourcePrefix = "Rexo.Policies.EmbeddedTemplates.";

    /// <summary>
    /// Returns the names of all embedded policy templates (without the .json extension).
    /// </summary>
    public static IReadOnlyList<string> TemplateNames { get; } = GetTemplateNames();

    private static IReadOnlyList<string> GetTemplateNames()
    {
        var names = new List<string>();
        foreach (var name in Assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                name.EndsWith(".policy.json", StringComparison.OrdinalIgnoreCase))
            {
                var templateName = name[ResourcePrefix.Length..^".policy.json".Length];
                names.Add(templateName);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Reads the JSON content of an embedded policy template by name.
    /// </summary>
    /// <param name="templateName">The template name (e.g. "standard", "dotnet").</param>
    /// <returns>The raw JSON content of the template.</returns>
    /// <exception cref="ArgumentException">Thrown if the template name is not found.</exception>
    public static string ReadTemplate(string templateName)
    {
        var resourceName = $"{ResourcePrefix}{templateName}.policy.json";
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new ArgumentException($"Embedded policy template '{templateName}' not found.", nameof(templateName));

        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Extracts an embedded policy template to a file path, returning the written path.
    /// </summary>
    /// <param name="templateName">The template name (e.g. "standard", "dotnet").</param>
    /// <param name="outputPath">Directory or file path where the template should be written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The absolute path where the template was written.</returns>
    public static async Task<string> ExtractTemplateAsync(
        string templateName,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var content = ReadTemplate(templateName);

        var filePath = System.IO.Directory.Exists(outputPath)
            ? System.IO.Path.Join(outputPath, $"{templateName}.policy.json")
            : outputPath;

        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        await System.IO.File.WriteAllTextAsync(filePath, content, cancellationToken);
        return filePath;
    }
}
