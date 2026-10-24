namespace Rexo.Execution;

using System.Text.Json;
using Rexo.Core.Models;

internal sealed class GitHubReleaseBuiltinModule : IConfigBuiltinModule
{
    public void Register(BuiltinRegistry registry, ConfigBuiltinModuleContext context)
    {
        registry.Register("builtin:github-release", async (step, ctx, ct) =>
        {
            try
            {
                var outputs = await GitHubReleasePublisher.ExecuteAsync(context.Config, ctx, ct);
                return new StepResult(step.Id ?? "github-release", true, 0, TimeSpan.Zero, outputs);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or
                UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException)
            {
                var secrets = SecretMasker.CollectSecretValues(ctx.ResolvedSecrets.Values
                    .Concat(ctx.MappedSecretEnvironment.Values)
                    .Concat(ctx.FileEnvironment.Where(pair => pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase))
                        .Select(pair => pair.Value)));
                var message = SecretMasker.Mask($"GitHub release failed: {ex.Message}", secrets);
                Console.Error.WriteLine(message);
                return new StepResult(step.Id ?? "github-release", false, 6, TimeSpan.Zero,
                    new Dictionary<string, object?> { ["error"] = message });
            }
        });
    }
}
