namespace Rexo.Execution;

using Rexo.Core.Models;

internal sealed class ArtifactBuiltinModule : IConfigBuiltinModule
{
    public void Register(BuiltinRegistry registry, ConfigBuiltinModuleContext context)
    {
        registry.Register("builtin:seal-nuget-handoff", async (step, ctx, ct) =>
        {
            if (ctx.IsDryRun)
            {
                return new StepResult(step.Id ?? "seal-handoff", true, 0, TimeSpan.Zero,
                    new Dictionary<string, object?> { ["message"] = "Dry run: handoff not written." });
            }

            await VerifiedNuGetHandoff.SealAsync(
                step.With?["runManifest"] ?? throw new InvalidOperationException("runManifest is required."),
                step.With?["path"] ?? throw new InvalidOperationException("path is required."),
                context.Config, ctx, ct);
            return new StepResult(step.Id ?? "seal-handoff", true, 0, TimeSpan.Zero,
                new Dictionary<string, object?> { ["message"] = "Verified NuGet handoff sealed." });
        });

        registry.Register("builtin:push-nuget-handoff", async (step, ctx, ct) =>
        {
            if (ConfigCommandLoader.TryGetOptionBoolean(ctx.Options, "confirm") != true)
            {
                throw new InvalidOperationException("Verified publication requires --confirm.");
            }
            var version = await VerifiedNuGetHandoff.VerifyAsync(
                step.With?["path"] ?? throw new InvalidOperationException("path is required."),
                context.Config, ctx, ct);
            var result = await context.Loader.PushArtifactsAsync(
                step.Id ?? "push-handoff", context.Config, context.RepositoryRoot,
                ConfigCommandLoader.ResolveOutputRoot(context.Config, ctx),
                ConfigCommandLoader.ShouldEmitRuntimeFiles(context.Config),
                ctx.WithVersion(version), static _ => true, "Verified packages pushed.", "No packages configured.", ct);
            if (result.Success && result.Outputs.TryGetValue("__pushDecisions", out var decisions) &&
                decisions is List<PushDecision> pushDecisions && pushDecisions.Any(decision => !decision.Allowed))
            {
                return result with
                {
                    Success = false,
                    ExitCode = 6,
                    Outputs = new Dictionary<string, object?>(result.Outputs)
                    {
                        ["error"] = "Verified publication was denied by push policy.",
                    },
                };
            }
            if (result.Success && !ctx.IsDryRun &&
                (!result.Outputs.TryGetValue("__artifacts", out var artifacts) ||
                 artifacts is not List<ArtifactManifestEntry> entries ||
                 entries.Count == 0 || entries.Any(entry => !entry.Pushed)))
            {
                return result with
                {
                    Success = false,
                    ExitCode = 6,
                    Outputs = new Dictionary<string, object?>(result.Outputs)
                    {
                        ["error"] = "Verified publication did not publish every configured package.",
                    },
                };
            }
            return result with
            {
                Outputs = new Dictionary<string, object?>(result.Outputs) { ["__version"] = version },
            };
        });

        registry.Register("builtin:build-artifacts", (step, ctx, ct) =>
            context.Loader.BuildArtifactsAsync(
                step.Id ?? "build-artifacts",
                context.Config,
                ctx,
                includePredicate: static _ => true,
                successMessage: "All artifacts built.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct));

        registry.Register("builtin:tag-artifacts", (step, ctx, ct) =>
            context.Loader.TagArtifactsAsync(
                step.Id ?? "tag-artifacts",
                context.Config,
                ctx,
                includePredicate: static _ => true,
                successMessage: "All artifacts tagged.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct));

        registry.Register("builtin:push-artifacts", (step, ctx, ct) =>
            context.Loader.PushArtifactsAsync(
                step.Id ?? "push-artifacts",
                context.Config,
                context.RepositoryRoot,
                ConfigCommandLoader.ResolveOutputRoot(context.Config, ctx),
                ConfigCommandLoader.ShouldEmitRuntimeFiles(context.Config),
                ctx,
                includePredicate: static _ => true,
                successMessage: "Artifact push phase completed.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct));

        registry.Register("builtin:plan-artifacts", (step, ctx, ct) =>
            Task.FromResult(ConfigCommandLoader.PlanArtifacts(
                step.Id ?? "plan-artifacts",
                context.Config,
                ctx,
                context.Loader.ArtifactProviders,
                pushRequested: ConfigCommandLoader.TryGetOptionBoolean(ctx.Options, "push") == true,
                includePredicate: static _ => true,
                successMessage: "Planned all artifacts.",
                emptyMessage: "No artifacts configured.")));

        registry.Register("builtin:ship-artifacts", async (step, ctx, ct) =>
        {
            var tagResult = await context.Loader.TagArtifactsAsync(
                step.Id ?? "ship-artifacts",
                context.Config,
                ctx,
                includePredicate: static _ => true,
                successMessage: "Artifacts tagged.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct);

            if (!tagResult.Success)
            {
                return tagResult;
            }

            return await context.Loader.PushArtifactsAsync(
                step.Id ?? "ship-artifacts",
                context.Config,
                context.RepositoryRoot,
                ConfigCommandLoader.ResolveOutputRoot(context.Config, ctx),
                ConfigCommandLoader.ShouldEmitRuntimeFiles(context.Config),
                ctx,
                includePredicate: static _ => true,
                successMessage: "Ship completed.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct);
        });

        registry.Register("builtin:all-artifacts", async (step, ctx, ct) =>
        {
            var buildResult = await context.Loader.BuildArtifactsAsync(
                step.Id ?? "all-artifacts",
                context.Config,
                ctx,
                includePredicate: static _ => true,
                successMessage: "Artifacts built.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct);

            if (!buildResult.Success)
            {
                return buildResult;
            }

            var tagResult = await context.Loader.TagArtifactsAsync(
                step.Id ?? "all-artifacts",
                context.Config,
                ctx,
                includePredicate: static _ => true,
                successMessage: "Artifacts tagged.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct);

            if (!tagResult.Success)
            {
                return tagResult;
            }

            return await context.Loader.PushArtifactsAsync(
                step.Id ?? "all-artifacts",
                context.Config,
                context.RepositoryRoot,
                ConfigCommandLoader.ResolveOutputRoot(context.Config, ctx),
                ConfigCommandLoader.ShouldEmitRuntimeFiles(context.Config),
                ctx,
                includePredicate: static _ => true,
                successMessage: "All workflow completed.",
                emptyMessage: "No artifacts configured.",
                cancellationToken: ct);
        });

        registry.Register("builtin:plan", (step, ctx, ct) =>
            Task.FromResult(ConfigCommandLoader.PlanArtifacts(
                step.Id ?? "plan",
                context.Config,
                ctx,
                context.Loader.ArtifactProviders,
                pushRequested: ConfigCommandLoader.TryGetOptionBoolean(ctx.Options, "push") == true,
                includePredicate: static _ => true,
                successMessage: "Planned all artifacts.",
                emptyMessage: "No artifacts configured.")));

        registry.Register("builtin:ship", async (step, ctx, ct) =>
            await (registry.TryResolve("builtin:ship-artifacts", out var ship) && ship is not null
                ? ship(step, ctx, ct)
                : Task.FromResult(new StepResult(step.Id ?? "ship", false, 1, TimeSpan.Zero,
                    new Dictionary<string, object?> { ["error"] = "builtin:ship-artifacts is not registered." }))));

        registry.Register("builtin:all", async (step, ctx, ct) =>
            await (registry.TryResolve("builtin:all-artifacts", out var all) && all is not null
                ? all(step, ctx, ct)
                : Task.FromResult(new StepResult(step.Id ?? "all", false, 1, TimeSpan.Zero,
                    new Dictionary<string, object?> { ["error"] = "builtin:all-artifacts is not registered." }))));
    }
}
