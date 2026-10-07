namespace Rexo.Execution;

using System.Text.Json;
using Rexo.Core.Models;

internal sealed class ConfigBuiltinModule : IConfigBuiltinModule
{
    public void Register(BuiltinRegistry registry, ConfigBuiltinModuleContext context)
    {
        registry.Register("builtin:config-resolved", (step, ctx, ct) =>
        {
            var json = JsonSerializer.Serialize(context.Config, ConfigCommandLoader.IndentedJsonOptions);
            Console.WriteLine(json);
            return Task.FromResult(new StepResult(
                step.Id ?? "config-resolved",
                true,
                0,
                TimeSpan.Zero,
                new Dictionary<string, object?> { ["json"] = json }));
        });

        registry.Register("builtin:config-materialize", async (step, ctx, ct) =>
        {
            var materialized = new List<string>();

            if (string.Equals(context.Config.Versioning?.Provider, "gitversion", StringComparison.OrdinalIgnoreCase))
            {
                var gvPath = Path.Join(context.RepositoryRoot, "GitVersion.yml");
                if (!File.Exists(gvPath))
                {
                    var gvContent = """
                        mode: ContinuousDeployment
                        branches: {}
                        ignore:
                          sha: []
                        """;
                    await File.WriteAllTextAsync(gvPath, gvContent, ct);
                    materialized.Add(gvPath);
                    Console.WriteLine($"  Materialized: {gvPath}");
                }
            }

            var message = materialized.Count > 0
                ? $"Materialized {materialized.Count} file(s)."
                : "Nothing to materialize.";

            return new StepResult(
                step.Id ?? "config-materialize",
                true,
                0,
                TimeSpan.Zero,
                new Dictionary<string, object?>
                {
                    ["message"] = message,
                    ["files"] = string.Join(", ", materialized),
                });
        });
    }
}
