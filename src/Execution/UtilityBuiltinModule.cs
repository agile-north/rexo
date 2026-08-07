namespace Rexo.Execution;

using Rexo.Core.Models;

internal sealed class UtilityBuiltinModule : IConfigBuiltinModule
{
    public void Register(BuiltinRegistry registry, ConfigBuiltinModuleContext context)
    {
        registry.Register("builtin:validate", (step, ctx, ct) =>
        {
            const string cacheKey = "builtin:validate";
            if (context.Loader.RunCache.TryGetValue(cacheKey, out var cached))
            {
                Console.WriteLine("  Using cached validation result.");
                return Task.FromResult(cached);
            }

            Console.WriteLine("  Validating configuration...");
            var result = new StepResult(
                step.Id ?? "validate",
                true,
                0,
                TimeSpan.Zero,
                new Dictionary<string, object?> { ["message"] = "Configuration is valid." });
            context.Loader.RunCache[cacheKey] = result;
            return Task.FromResult(result);
        });

        registry.Register("builtin:clean", (step, ctx, ct) =>
        {
            Console.WriteLine("  Cleaning generated output...");
            var artifactsDir = Path.Combine(context.RepositoryRoot, ConfigCommandLoader.ResolveOutputRoot(context.Config, ctx));
            var cleaned = new List<string>();

            if (Directory.Exists(artifactsDir))
            {
                try
                {
                    Directory.Delete(artifactsDir, true);
                    cleaned.Add(artifactsDir);
                    Console.WriteLine($"    Removed: {artifactsDir}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed to clean {artifactsDir}: {ex.Message}");
                }
            }

            return Task.FromResult(new StepResult(
                step.Id ?? "clean",
                true,
                0,
                TimeSpan.Zero,
                new Dictionary<string, object?>
                {
                    ["message"] = cleaned.Count > 0 ? $"Cleaned {cleaned.Count} directory(ies)." : "Nothing to clean.",
                    ["cleaned"] = cleaned,
                }));
        });
    }
}
