namespace Rexo.Artifacts.Terraform;

using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

/// <summary>
/// Terraform artifact provider — <c>terraform init</c> + <c>plan</c> for build,
/// <c>terraform apply</c> for push.  Type key: <c>terraform</c>.
/// <para>
/// Backend auth is expected to come from environment variables configured by the caller.
/// No additional credential injection is performed by this provider.
/// </para>
/// </summary>
public sealed class TerraformArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("terraform", new TerraformArtifactProvider());

    private const string DefaultContainerImage = "hashicorp/terraform:1.9";

    public string Type => "terraform";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = GetWorkDir(artifact, context);
        var dockerImage = ResolveDockerImage(artifact);
        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var varFile = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "TERRAFORM_TARGET_VAR_FILE",
            configuredEnvName: GetSetting(artifact, "target.varFileEnv"),
            configuredValue: GetSetting(artifact, "target.varFile"),
            fileEnv: fileEnv);

        // terraform init
        var initArgs = new List<string> { "init" };
        Console.WriteLine($"  > terraform {ToolRunner.FormatArgs(initArgs)}");
        var initResult = await ToolRunner.RunAsync("terraform", initArgs, workDir, artifact, dockerImage, cancellationToken);
        if (initResult.ExitCode != 0)
        {
            return new ArtifactBuildResult(artifact.Name, false, null);
        }

        // terraform plan
        var planArgs = new List<string> { "plan", "-out=plan.tfplan" };
        if (!string.IsNullOrWhiteSpace(varFile))
        {
            planArgs.Add($"-var-file={varFile}");
        }

        planArgs.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-build-args")));

        Console.WriteLine($"  > terraform {ToolRunner.FormatArgs(planArgs)}");
        var planResult = await ToolRunner.RunAsync("terraform", planArgs, workDir, artifact, dockerImage, cancellationToken);

        return new ArtifactBuildResult(artifact.Name, planResult.ExitCode == 0, planResult.ExitCode == 0 ? workDir : null);
    }

    public Task<ArtifactTagResult> TagAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Terraform workspaces serve as environment tags.
        var workspace = GetSetting(artifact, "workspace");
        var tags = workspace is not null ? new[] { workspace } : Array.Empty<string>();
        return Task.FromResult(new ArtifactTagResult(artifact.Name, true, tags));
    }

    public async Task<ArtifactPushResult> PushAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workDir = GetWorkDir(artifact, context);
        var dockerImage = ResolveDockerImage(artifact);
        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var workspace = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "TERRAFORM_TARGET_WORKSPACE",
            configuredEnvName: GetSetting(artifact, "target.workspaceEnv"),
            configuredValue: GetSetting(artifact, "target.workspace"),
            fileEnv: fileEnv);

        // Optionally select workspace
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var wsArgs = new List<string> { "workspace", "select", workspace };
            Console.WriteLine($"  > terraform {ToolRunner.FormatArgs(wsArgs)}");
            var wsResult = await ToolRunner.RunAsync("terraform", wsArgs, workDir, artifact, dockerImage, cancellationToken);
            if (wsResult.ExitCode != 0)
            {
                return new ArtifactPushResult(artifact.Name, false, []);
            }
        }

        var applyArgs = new List<string> { "apply", "plan.tfplan" };
        applyArgs.AddRange(ToolRunner.ParseExtraArgs(GetSetting(artifact, "extra-push-args")));

        Console.WriteLine($"  > terraform {ToolRunner.FormatArgs(applyArgs)}");
        var result = await ToolRunner.RunAsync("terraform", applyArgs, workDir, artifact, dockerImage, cancellationToken);

        return new ArtifactPushResult(
            artifact.Name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? [workspace ?? "default"] : []);
    }

    private static string GetWorkDir(ArtifactConfig artifact, ExecutionContext context)
    {
        var dir = GetSetting(artifact, "directory");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return context.RepositoryRoot;
        }

        return Path.IsPathRooted(dir) ? dir : Path.Combine(context.RepositoryRoot, dir);
    }

    private static string ResolveDockerImage(ArtifactConfig artifact) =>
        Environment.GetEnvironmentVariable("TERRAFORM_CONTAINER_IMAGE")
            ?? ToolRunner.GetSetting(artifact, "dockerImage")
            ?? DefaultContainerImage;

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        ToolRunner.GetSetting(artifact, key);
}
