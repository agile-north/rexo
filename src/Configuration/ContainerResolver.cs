namespace Rexo.Configuration;

using Rexo.Configuration.Models;

/// <summary>
/// Resolves step/command container specs against the named <c>containers</c> registry.
/// </summary>
public static class ContainerResolver
{
    /// <summary>
    /// Returns the effective container for a step, or <c>null</c> when the step runs on the host.
    /// Step-level containers win over the command-level default. Only <c>run</c> steps are containerized.
    /// </summary>
    public static RepoStepContainerConfig? ResolveForStep(
        RepoStepConfig step,
        RepoStepContainerConfig? commandDefault,
        IReadOnlyDictionary<string, RepoStepContainerConfig>? registry,
        Func<string, string> renderName)
    {
        if (step.Run is null)
        {
            return null;
        }

        return Resolve(step.Container ?? commandDefault, registry, renderName);
    }

    /// <summary>
    /// Resolves a single container spec. A <c>use</c> reference is looked up in the registry (following
    /// <c>extends</c> chains) and inline fields are layered on top. A reference that renders to empty,
    /// <c>none</c>, or <c>false</c> disables containerization unless an inline image is also provided.
    /// </summary>
    public static RepoStepContainerConfig? Resolve(
        RepoStepContainerConfig? spec,
        IReadOnlyDictionary<string, RepoStepContainerConfig>? registry,
        Func<string, string> renderName)
    {
        ArgumentNullException.ThrowIfNull(renderName);

        if (spec is null)
        {
            return null;
        }

        RepoStepContainerConfig? resolved;
        if (spec.Use is null)
        {
            resolved = spec with { Extends = null };
        }
        else
        {
            var name = renderName(spec.Use).Trim();
            var inline = spec with { Use = null, Extends = null };
            if (IsNoneReference(name))
            {
                resolved = string.IsNullOrWhiteSpace(inline.Image) || string.IsNullOrWhiteSpace(renderName(inline.Image))
                    ? null
                    : inline;
            }
            else
            {
                var definition = ResolveNamed(name, registry, renderName, []);
                resolved = Overlay(definition, inline) with { Use = null, Extends = null };
            }
        }

        if (resolved is not null && string.IsNullOrWhiteSpace(renderName(resolved.Image ?? string.Empty)))
        {
            throw new InvalidOperationException(
                "Resolved container has no image. Specify an image or use 'none' to run on the host.");
        }

        return resolved;
    }

    /// <summary>Layers <paramref name="override"/> on top of <paramref name="base"/> field by field (env and build args merge).</summary>
    public static RepoStepContainerConfig Overlay(RepoStepContainerConfig @base, RepoStepContainerConfig @override)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(@override);

        return new RepoStepContainerConfig(
            Image: @override.Image ?? @base.Image,
            Env: MergeStrings(@base.Env, @override.Env),
            WorkingDirectory: @override.WorkingDirectory ?? @base.WorkingDirectory,
            Entrypoint: @override.Entrypoint ?? @base.Entrypoint,
            Dockerfile: @override.Dockerfile ?? @base.Dockerfile,
            Context: @override.Context ?? @base.Context,
            Build: MergeBuild(@base.Build, @override.Build))
        {
            Use = @override.Use ?? @base.Use,
            Extends = @override.Extends ?? @base.Extends,
        };
    }

    /// <summary>Merges two container registries; entries with the same name are overlaid field by field.</summary>
    public static Dictionary<string, RepoStepContainerConfig>? MergeRegistries(
        Dictionary<string, RepoStepContainerConfig>? @base,
        Dictionary<string, RepoStepContainerConfig>? child)
    {
        if (@base is null or { Count: 0 }) return child;
        if (child is null or { Count: 0 }) return @base;

        var result = new Dictionary<string, RepoStepContainerConfig>(@base, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, container) in child)
        {
            result[name] = result.TryGetValue(name, out var existing) ? Overlay(existing, container) : container;
        }

        return result;
    }

    private static bool IsNoneReference(string name) =>
        name.Length == 0 ||
        string.Equals(name, RepoStepContainerConfig.NoneReference, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "false", StringComparison.OrdinalIgnoreCase);

    private static RepoStepContainerConfig ResolveNamed(
        string name,
        IReadOnlyDictionary<string, RepoStepContainerConfig>? registry,
        Func<string, string> renderName,
        HashSet<string> visited)
    {
        if (!visited.Add(name))
        {
            throw new InvalidOperationException($"Circular container 'extends' reference detected at '{name}'.");
        }

        var definition = Lookup(name, registry)
            ?? throw new InvalidOperationException(
                $"Container '{name}' is not defined. Declare it under the top-level 'containers' map or use 'none'.");

        if (string.IsNullOrWhiteSpace(definition.Extends))
        {
            return definition;
        }

        var parentName = renderName(definition.Extends).Trim();
        var parent = ResolveNamed(parentName, registry, renderName, visited);
        return Overlay(parent, definition with { Extends = null });
    }

    private static RepoStepContainerConfig? Lookup(string name, IReadOnlyDictionary<string, RepoStepContainerConfig>? registry)
    {
        if (registry is null)
        {
            return null;
        }

        if (registry.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in registry)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static Dictionary<string, string>? MergeStrings(Dictionary<string, string>? @base, Dictionary<string, string>? child)
    {
        if (@base is null or { Count: 0 }) return child;
        if (child is null or { Count: 0 }) return @base;

        var result = new Dictionary<string, string>(@base, StringComparer.Ordinal);
        foreach (var (key, value) in child)
        {
            result[key] = value;
        }

        return result;
    }

    private static RepoStepContainerBuildConfig? MergeBuild(RepoStepContainerBuildConfig? @base, RepoStepContainerBuildConfig? child)
    {
        if (@base is null) return child;
        if (child is null) return @base;
        return new RepoStepContainerBuildConfig(child.Target ?? @base.Target, MergeStrings(@base.Args, child.Args));
    }
}
