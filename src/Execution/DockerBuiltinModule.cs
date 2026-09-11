namespace Rexo.Execution;

using System.Text.Json;
using Rexo.Core.Models;

internal sealed class DockerBuiltinModule : IConfigBuiltinModule
{
    public void Register(BuiltinRegistry registry, ConfigBuiltinModuleContext context)
    {
        registry.Register("builtin:docker-plan", (step, ctx, ct) =>
            Task.FromResult(ConfigCommandLoader.PlanArtifacts(
                step.Id ?? "docker-plan",
                context.Config,
                ctx,
                context.Loader.ArtifactProviders,
                pushRequested: ConfigCommandLoader.TryGetOptionBoolean(ctx.Options, "push") == true,
                includePredicate: static a => string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase),
                successMessage: "Planned docker artifacts.",
                emptyMessage: "No docker artifacts configured.")));

        registry.Register("builtin:docker-ship", async (step, ctx, ct) =>
        {
            var tagResult = await context.Loader.TagArtifactsAsync(
                step.Id ?? "docker-ship",
                context.Config,
                ctx,
                includePredicate: static a => string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase),
                successMessage: "Docker artifacts tagged.",
                emptyMessage: "No docker artifacts configured.",
                cancellationToken: ct);

            if (!tagResult.Success)
            {
                return tagResult;
            }

            return await context.Loader.PushArtifactsAsync(
                step.Id ?? "docker-ship",
                context.Config,
                context.RepositoryRoot,
                ConfigCommandLoader.ResolveOutputRoot(context.Config, ctx),
                ConfigCommandLoader.ShouldEmitRuntimeFiles(context.Config),
                ctx,
                includePredicate: static a => string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase),
                successMessage: "Docker ship completed.",
                emptyMessage: "No docker artifacts configured.",
                cancellationToken: ct);
        });

        registry.Register("builtin:docker-all", async (step, ctx, ct) =>
        {
            var buildResult = await context.Loader.BuildArtifactsAsync(
                step.Id ?? "docker-all",
                context.Config,
                ctx,
                includePredicate: static a => string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase),
                successMessage: "Docker artifacts built.",
                emptyMessage: "No docker artifacts configured.",
                cancellationToken: ct);

            if (!buildResult.Success)
            {
                return buildResult;
            }

            var tagResult = await context.Loader.TagArtifactsAsync(
                step.Id ?? "docker-all",
                context.Config,
                ctx,
                includePredicate: static a => string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase),
                successMessage: "Docker artifacts tagged.",
                emptyMessage: "No docker artifacts configured.",
                cancellationToken: ct);

            if (!tagResult.Success)
            {
                return tagResult;
            }

            return await context.Loader.PushArtifactsAsync(
                step.Id ?? "docker-all",
                context.Config,
                context.RepositoryRoot,
                ConfigCommandLoader.ResolveOutputRoot(context.Config, ctx),
                ConfigCommandLoader.ShouldEmitRuntimeFiles(context.Config),
                ctx,
                includePredicate: static a => string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase),
                successMessage: "Docker all completed.",
                emptyMessage: "No docker artifacts configured.",
                cancellationToken: ct);
        });

        registry.Register("builtin:docker-stage", async (step, ctx, ct) =>
        {
            var stageName = ctx.Args.TryGetValue("stage", out var argStage) && !string.IsNullOrWhiteSpace(argStage)
                ? argStage
                : (ctx.Options.TryGetValue("stage", out var optionStage) ? optionStage : null);

            if (string.IsNullOrWhiteSpace(stageName))
            {
                return new StepResult(
                    step.Id ?? "docker-stage",
                    false,
                    2,
                    TimeSpan.Zero,
                    new Dictionary<string, object?> { ["error"] = "Missing stage name. Provide args.stage or --stage." });
            }

            var dockerArtifacts = (context.Config.Artifacts ?? [])
                .Where(a => string.Equals(a.Type, "docker", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (dockerArtifacts.Count == 0)
            {
                return new StepResult(
                    step.Id ?? "docker-stage",
                    true,
                    0,
                    TimeSpan.Zero,
                    new Dictionary<string, object?> { ["message"] = "No docker artifacts configured." });
            }

            foreach (var artifactCfg in dockerArtifacts)
            {
                var provider = context.Loader.ArtifactProviders.Resolve(artifactCfg.Type);
                if (provider is null)
                {
                    continue;
                }

                if (artifactCfg.Settings is null ||
                    !artifactCfg.Settings.TryGetValue("stages", out var stagesValue) ||
                    stagesValue.ValueKind != JsonValueKind.Object ||
                    !stagesValue.TryGetProperty(stageName, out var selectedStage) ||
                    selectedStage.ValueKind != JsonValueKind.Object)
                {
                    var artifactName = ConfigCommandLoader.ResolveArtifactName(artifactCfg, context.Config);
                    return new StepResult(
                        step.Id ?? "docker-stage",
                        false,
                        2,
                        TimeSpan.Zero,
                        new Dictionary<string, object?>
                        {
                            ["error"] = $"Stage '{stageName}' not found for docker artifact '{artifactName}'.",
                        });
                }

                var clonedSettings = ConfigCommandLoader.CloneSettings(artifactCfg.Settings);
                clonedSettings["stages"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    [stageName] = selectedStage.Clone(),
                });
                clonedSettings["stageFallback"] = JsonSerializer.SerializeToElement(false);

                var artifactConfig = new ArtifactConfig(
                    artifactCfg.Type,
                    ConfigCommandLoader.ResolveArtifactName(artifactCfg, context.Config),
                    clonedSettings);

                var result = await provider.BuildAsync(artifactConfig, ctx, ct);
                if (!result.Success)
                {
                    var artifactName = ConfigCommandLoader.ResolveArtifactName(artifactCfg, context.Config);
                    return new StepResult(
                        step.Id ?? "docker-stage",
                        false,
                        5,
                        TimeSpan.Zero,
                        new Dictionary<string, object?>
                        {
                            ["error"] = $"Failed to build docker stage '{stageName}' for artifact '{artifactName}'.",
                        });
                }
            }

            return new StepResult(
                step.Id ?? "docker-stage",
                true,
                0,
                TimeSpan.Zero,
                new Dictionary<string, object?>
                {
                    ["message"] = $"Docker stage '{stageName}' completed.",
                });
        });
    }
}
