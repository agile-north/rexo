namespace Rexo.Artifacts.Docker;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

public sealed class DockerArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("docker", new DockerArtifactProvider());

    private const string DefaultRunner = "build";
    private const string BuildxRunner = "buildx";
    private const string CleanupModeAuto = "auto";
    private readonly Func<IReadOnlyList<string>, string, IReadOnlyDictionary<string, string?>?, string?, CancellationToken, Task<(int ExitCode, string Output)>> _runDockerAsync;
    private readonly Func<string, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<bool>> _isBuildxAvailableAsync;

    public DockerArtifactProvider(
        Func<IReadOnlyList<string>, string, IReadOnlyDictionary<string, string?>?, string?, CancellationToken, Task<(int ExitCode, string Output)>>? runDockerAsync = null,
        Func<string, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<bool>>? isBuildxAvailableAsync = null)
    {
        _runDockerAsync = runDockerAsync ?? RunDockerAsync;
        _isBuildxAvailableAsync = isBuildxAvailableAsync ?? IsBuildxAvailableAsync;
    }

    public string Type => "docker";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var dotEnv = RepositoryEnvironmentFiles.Load(context.RepositoryRoot);
            var settings = ResolveBuildSettings(artifact, dotEnv, context);
            var tags = BuildTags(settings.Image, artifact, context);
            var primaryTag = context.Version is not null
                ? $"{settings.Image}:{GetVersionTag(context.Version)}"
                : settings.Image;

            IReadOnlyDictionary<string, string?>? envOverrides = null;
            string? tempDockerConfig = null;

            try
            {
                var auth = await PrepareDockerAuthAsync(settings, context.RepositoryRoot, dotEnv, cancellationToken);
                if (!auth.Success)
                {
                    return new ArtifactBuildResult(artifact.Name, false, null);
                }

                envOverrides = auth.Environment;
                tempDockerConfig = auth.TempDockerConfigDirectory;

                var stageRequests = BuildStageRequests(settings, tags);
                foreach (var request in stageRequests)
                {
                    var result = await RunBuildRequestAsync(request, context, envOverrides, cancellationToken);
                    if (result.ExitCode != 0)
                    {
                        return new ArtifactBuildResult(artifact.Name, false, null);
                    }
                }

                if (settings.CleanupLocal && tags.Count > 0)
                {
                    if (!ShouldPush(settings, context, out _))
                    {
                        await CleanupLocalImagesAsync(tags, context.RepositoryRoot, envOverrides, cancellationToken);
                    }
                }

                return new ArtifactBuildResult(
                    Name: artifact.Name,
                    Success: true,
                    Location: primaryTag);
            }
            finally
            {
                CleanupDockerConfigDirectory(tempDockerConfig);
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return new ArtifactBuildResult(artifact.Name, false, null);
        }
    }

    public async Task<ArtifactTagResult> TagAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var dotEnv = RepositoryEnvironmentFiles.Load(context.RepositoryRoot);
        var settings = ResolveBuildSettings(artifact, dotEnv, context);
        var image = settings.Image;
        var tags = BuildTags(image, artifact, context);
        var appliedTags = new List<string>();

        if (context.Version is null)
        {
            return new ArtifactTagResult(artifact.Name, false, Array.Empty<string>());
        }

        var sourceTag = $"{image}:{GetVersionTag(context.Version)}";

        foreach (var tag in tags)
        {
            if (tag == sourceTag) continue;
            var tagArgs = new[] { "tag", sourceTag, tag };
            Console.WriteLine($"  > docker {FormatArguments(tagArgs)}");
            var result = await _runDockerAsync(tagArgs, context.RepositoryRoot, null, null, cancellationToken);
            if (result.ExitCode == 0) appliedTags.Add(tag);
        }

        return new ArtifactTagResult(artifact.Name, true, tags);
    }

    public IReadOnlyList<string> GetPlannedTags(ArtifactConfig artifact, ExecutionContext context)
    {
        var dotEnv = RepositoryEnvironmentFiles.Load(context.RepositoryRoot);
        var settings = ResolveBuildSettings(artifact, dotEnv, context);
        return BuildTags(settings.Image, artifact, context);
    }

    public async Task<ArtifactPushResult> PushAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var dotEnv = RepositoryEnvironmentFiles.Load(context.RepositoryRoot);
            var settings = ResolveBuildSettings(artifact, dotEnv, context);
            var image = settings.Image;
            var tags = BuildTags(image, artifact, context);
            var pushed = new List<string>();
            IReadOnlyDictionary<string, string?>? envOverrides = null;
            string? tempDockerConfig = null;

            if (!ShouldPush(settings, context, out var skipReason))
            {
                Console.WriteLine($"  Skipping docker push for '{artifact.Name}': {skipReason}");
                if (settings.CleanupLocal && tags.Count > 0)
                {
                    await CleanupLocalImagesAsync(tags, context.RepositoryRoot, envOverrides, cancellationToken);
                }
                return new ArtifactPushResult(artifact.Name, true, Array.Empty<string>());
            }

            try
            {
                var auth = await PrepareDockerAuthAsync(settings, context.RepositoryRoot, dotEnv, cancellationToken);
                if (!auth.Success)
                {
                    if (settings.CleanupLocal && tags.Count > 0)
                    {
                        await CleanupLocalImagesAsync(tags, context.RepositoryRoot, envOverrides, cancellationToken);
                    }

                    return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
                }

                envOverrides = auth.Environment;
                tempDockerConfig = auth.TempDockerConfigDirectory;

                foreach (var tag in tags)
                {
                    var pushArgs = new[] { "push", tag };
                    Console.WriteLine($"  > docker {FormatArguments(pushArgs)}");
                    var result = await _runDockerAsync(pushArgs, context.RepositoryRoot, envOverrides, null, cancellationToken);
                    if (result.ExitCode == 0) pushed.Add(tag);
                }
            }
            finally
            {
                CleanupDockerConfigDirectory(tempDockerConfig);
                if (settings.CleanupLocal && tags.Count > 0)
                {
                    await CleanupLocalImagesAsync(tags, context.RepositoryRoot, envOverrides, cancellationToken);
                }
            }

            return new ArtifactPushResult(artifact.Name, pushed.Count > 0, pushed);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
        }
    }

    private static IReadOnlyList<string> BuildTags(
        string image,
        ArtifactConfig artifact,
        ExecutionContext context)
    {
        var settings = ResolveBuildSettings(artifact, new Dictionary<string, string>(StringComparer.Ordinal), context);
        var tags = new List<string>();
        var versionTag = context.Version is not null ? GetVersionTag(context.Version) : null;
        var kindStrategies = settings.TagStrategies;

        if (kindStrategies.Count == 0 && versionTag is not null)
        {
            kindStrategies = ResolveTagKinds(settings, context, versionTag);
        }

        foreach (var strategy in kindStrategies)
        {
            var tag = strategy.Trim() switch
            {
                "full" when versionTag is not null => $"{image}:{versionTag}",
                "majorminor" when context.Version is not null => $"{image}:{FormatMajorMinorTag(context.Version, settings.Classification)}",
                "majorMinor" when context.Version is not null => $"{image}:{FormatMajorMinorTag(context.Version, settings.Classification)}",
                "semver" when versionTag is not null => $"{image}:{versionTag}",
                "major-minor" when context.Version is not null => $"{image}:{FormatMajorMinorTag(context.Version, settings.Classification)}",
                "major" when context.Version is not null => $"{image}:{FormatMajorTag(context.Version)}",
                "branch" when context.Branch is not null => $"{image}:{Slug(context.Branch)}",
                "sha" when context.ShortSha is not null => $"{image}:sha-{context.ShortSha}",
                "latest-on-main" when context.Branch == "main" => $"{image}:latest",
                _ when strategy.StartsWith("{{", StringComparison.Ordinal) => null,
                _ => null,
            };

            if (tag is not null) tags.Add(tag);
        }

        if (settings.TagLatest)
        {
            tags.Add($"{image}:latest");
        }

        foreach (var aliasTag in BuildAliasTags(image, context, settings))
        {
            tags.Add(aliasTag);
        }

        if (tags.Count == 0 && context.Version is not null)
        {
            tags.Add($"{image}:{versionTag}");
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<string> ResolveDockerRunnerAsync(
        string repositoryRoot,
        string configuredRunner,
        IReadOnlyDictionary<string, string?>? envOverrides,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(configuredRunner, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeRunner(configuredRunner);
        }

        var buildxAvailable = await _isBuildxAvailableAsync(repositoryRoot, envOverrides, cancellationToken);
        return buildxAvailable ? BuildxRunner : DefaultRunner;
    }

    private async Task<(bool Success, IReadOnlyDictionary<string, string?>? Environment, string? TempDockerConfigDirectory)> PrepareDockerAuthAsync(
        DockerBuildSettings settings,
        string workingDirectory,
        IReadOnlyDictionary<string, string> dotEnv,
        CancellationToken cancellationToken)
    {
        var envOverrides = BuildEnvironmentOverrides(dotEnv);
        var auth = FeedAuthResolver.ResolveDocker(
            configuredRegistry: settings.LoginRegistry,
            inferredRegistry: InferRegistryFromImage(settings.Image),
            fileEnv: dotEnv);

        if (!string.IsNullOrWhiteSpace(auth.Error))
        {
            Console.Error.WriteLine(auth.Error);
            return (false, null, null);
        }

        if (!auth.HasCredentials)
        {
            return (true, envOverrides.Count > 0 ? envOverrides : null, null);
        }

        var registry = auth.Endpoint;
        if (string.IsNullOrWhiteSpace(registry))
        {
            Console.Error.WriteLine("Docker login registry could not be determined. Set settings.loginRegistry or DOCKER_LOGIN_REGISTRY.");
            return (false, null, null);
        }

        var tempDockerConfig = Path.Combine(Path.GetTempPath(), $"rexo-docker-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDockerConfig);

        envOverrides["DOCKER_CONFIG"] = tempDockerConfig;

        var loginArgs = new[] { "login", registry, "--username", auth.Username!, "--password-stdin" };
        Console.WriteLine($"  > docker {FormatArguments(loginArgs)}");
        var loginResult = await _runDockerAsync(
            loginArgs,
            workingDirectory,
            envOverrides,
            auth.Secret! + Environment.NewLine,
            cancellationToken);

        if (loginResult.ExitCode != 0)
        {
            CleanupDockerConfigDirectory(tempDockerConfig);
            return (false, null, null);
        }

        return (true, envOverrides, tempDockerConfig);
    }

    private static DockerBuildSettings ResolveBuildSettings(
        ArtifactConfig artifact,
        IReadOnlyDictionary<string, string> dotEnv,
        ExecutionContext context) =>
        new(
            Image: ResolveImage(artifact, dotEnv),
            Dockerfile: GetEnvironmentValue("DOCKERFILE_PATH", dotEnv) ?? GetStringSetting(artifact.Settings, "dockerfile", "file") ?? "Dockerfile",
            BuildContext: GetEnvironmentValue("DOCKER_CONTEXT", dotEnv) ?? GetStringSetting(artifact.Settings, "context") ?? ".",
            Runner: GetEnvironmentValue("DOCKER_RUNNER", dotEnv) ?? GetStringSetting(artifact.Settings, "runner") ?? DefaultRunner,
            Platform: GetEnvironmentValue("DOCKER_PLATFORM", dotEnv) ?? GetStringSetting(artifact.Settings, "platform"),
            BuildTarget: GetEnvironmentValue("DOCKER_BUILD_TARGET", dotEnv) ?? GetStringSetting(artifact.Settings, "buildTarget"),
            BuildOutputFlags: ResolveBuildOutput(artifact.Settings, dotEnv),
            BuildArgFlags: ResolveBuildArgs(artifact.Settings, dotEnv),
            BuildSecretFlags: ResolveBuildSecrets(artifact.Settings, dotEnv),
            LoginRegistry: GetStringSetting(artifact.Settings, "loginRegistry", "login.registry"),
            CleanupLocal: ResolveCleanupLocal(artifact.Settings, dotEnv, context.IsCi),
            PushEnabled: ResolvePushEnabled(artifact.Settings, dotEnv),
            PushBranches: ResolvePushBranches(artifact.Settings, dotEnv),
            DenyNonPublicPush: GetBoolSetting(artifact.Settings, "push.denyNonPublicPush", "denyNonPublicPush") ?? false,
            TagLatest: ResolveTagLatest(artifact.Settings, dotEnv),
            Classification: ClassifyBuild(artifact.Settings, context.Branch),
            TagPolicy: ResolveTagPolicy(artifact.Settings),
            NonPublicMode: GetStringSetting(artifact.Settings, "nonPublicMode"),
            TagStrategies: ResolveExplicitTagStrategies(artifact.Settings),
            Aliases: ResolveAliasSettings(artifact.Settings),
            Stages: ResolveStages(artifact.Settings),
            StageFallback: GetBoolSetting(artifact.Settings, "stageFallback") ?? true);

    private static IReadOnlyList<string> ResolveBuildOutput(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, string> dotEnv)
    {
        var envValue = GetEnvironmentValue("DOCKER_BUILD_OUTPUT", dotEnv);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return SplitCommandArgs(envValue);
        }

        if (!TryGetSettingValue(settings, out var value, "buildOutput"))
        {
            return Array.Empty<string>();
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => SplitCommandArgs(value.GetString() ?? string.Empty),
            JsonValueKind.Array => value.EnumerateArray()
                .SelectMany(item => SplitCommandArgs(GetString(item) ?? string.Empty))
                .ToArray(),
            JsonValueKind.Null or JsonValueKind.Undefined => Array.Empty<string>(),
            _ => throw new InvalidOperationException("Docker setting 'buildOutput' must be a string or array of strings."),
        };
    }

    private static IReadOnlyList<string> ResolveBuildArgs(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, string> dotEnv)
    {
        var envValue = GetEnvironmentValue("DOCKER_BUILD_ARGS", dotEnv);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return ParseBuildArgsValue(envValue, "DOCKER_BUILD_ARGS");
        }

        var envArgsFile = GetEnvironmentValue("DOCKER_BUILD_ARGS_FILE", dotEnv);
        if (!string.IsNullOrWhiteSpace(envArgsFile))
        {
            return ParseBuildArgsFile(envArgsFile);
        }

        if (!TryGetSettingValue(settings, out var value, "buildArgs"))
        {
            return Array.Empty<string>();
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => ParseBuildArgsObject(value, "settings.buildArgs"),
            JsonValueKind.Array => value.EnumerateArray().SelectMany(ParseBuildArgArrayItem).ToArray(),
            JsonValueKind.String => ParseBuildArgsValue(value.GetString() ?? string.Empty, "settings.buildArgs"),
            JsonValueKind.Null or JsonValueKind.Undefined => Array.Empty<string>(),
            _ => throw new InvalidOperationException("Docker setting 'buildArgs' must be an object, string, or array."),
        };
    }

    private static IReadOnlyList<string> ResolveBuildSecrets(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, string> dotEnv)
    {
        var envValue = GetEnvironmentValue("DOCKER_SECRETS", dotEnv);
        var configuredSecrets = !string.IsNullOrWhiteSpace(envValue)
            ? ParseSecretsJson(envValue)
            : TryGetSettingValue(settings, out var value, "secrets")
                ? ParseSecretsElement(value, "settings.secrets")
                : [];

        var resolvedSecrets = new Dictionary<string, BuildSecretSpec>(StringComparer.Ordinal);
        foreach (var secret in configuredSecrets)
        {
            resolvedSecrets[secret.Id] = secret;
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || !key.StartsWith("DOCKER_SECRET_", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Value is not string valueText || string.IsNullOrWhiteSpace(valueText))
            {
                continue;
            }

            var id = NormalizeSecretId(key["DOCKER_SECRET_".Length..], "DOCKER_SECRET_<ID>");
            resolvedSecrets[id] = BuildSecretSpec.FromEnvironment(id, key);
        }

        foreach (var (key, valueText) in dotEnv)
        {
            if (!key.StartsWith("DOCKER_SECRET_", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(valueText))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                continue;
            }

            var id = NormalizeSecretId(key["DOCKER_SECRET_".Length..], "DOCKER_SECRET_<ID>");
            resolvedSecrets[id] = BuildSecretSpec.FromEnvironment(id, key);
        }

        return resolvedSecrets.Values.SelectMany(secret => secret.ToDockerFlags()).ToArray();
    }

    private async Task<(int ExitCode, string Output)> RunBuildRequestAsync(
        DockerBuildRequest request,
        ExecutionContext context,
        IReadOnlyDictionary<string, string?>? envOverrides,
        CancellationToken cancellationToken)
    {
        var resolvedRunner = await ResolveDockerRunnerAsync(
            context.RepositoryRoot,
            request.Runner,
            envOverrides,
            cancellationToken);

        if (request.BuildSecretFlags.Count > 0 && !string.Equals(resolvedRunner, BuildxRunner, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Docker build secrets require runner 'buildx'. Set settings.runner to 'buildx' or DOCKER_RUNNER=buildx.");
            return (-1, string.Empty);
        }

        var args = new List<string>(resolvedRunner == BuildxRunner
            ? [BuildxRunner, "build"]
            : ["build"])
        {
            "--progress",
            "plain",
            "-f",
            request.Dockerfile,
        };

        if (!string.IsNullOrWhiteSpace(request.Platform))
        {
            args.Add("--platform");
            args.Add(request.Platform);
        }

        if (!string.IsNullOrWhiteSpace(request.BuildTarget))
        {
            args.Add("--target");
            args.Add(request.BuildTarget);
        }

        if (request.BuildOutputFlags.Count > 0)
        {
            args.Add("--output");
            args.AddRange(request.BuildOutputFlags);
        }

        args.AddRange(request.BuildSecretFlags);
        args.AddRange(request.BuildArgFlags);

        if (context.Version is not null)
        {
            args.Add("--build-arg");
            args.Add($"APP_VERSION={GetVersionTag(context.Version)}");
        }

        foreach (var tag in request.Tags)
        {
            args.Add("-t");
            args.Add(tag);
        }

        args.Add(request.BuildContext);

        Console.WriteLine($"  > docker {FormatArguments(args)}");
        return await _runDockerAsync(args, context.RepositoryRoot, envOverrides, null, cancellationToken);
    }

    private async Task CleanupLocalImagesAsync(
        IReadOnlyList<string> tags,
        string repositoryRoot,
        IReadOnlyDictionary<string, string?>? envOverrides,
        CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            var rmArgs = new[] { "image", "rm", tag };
            Console.WriteLine($"  > docker {FormatArguments(rmArgs)}");
            _ = await _runDockerAsync(rmArgs, repositoryRoot, envOverrides, null, cancellationToken);
        }
    }

    private static IReadOnlyList<DockerBuildRequest> BuildStageRequests(
        DockerBuildSettings settings,
        IReadOnlyList<string> finalTags)
    {
        var requests = new List<DockerBuildRequest>();

        foreach (var stage in settings.Stages)
        {
            requests.Add(new DockerBuildRequest(
                Dockerfile: settings.Dockerfile,
                BuildContext: settings.BuildContext,
                Runner: stage.Runner ?? settings.Runner,
                Platform: stage.Platform ?? settings.Platform,
                BuildTarget: stage.Target ?? settings.BuildTarget,
                BuildOutputFlags: stage.Output.Count > 0 ? stage.Output : settings.BuildOutputFlags,
                BuildArgFlags: settings.BuildArgFlags,
                BuildSecretFlags: settings.BuildSecretFlags,
                Tags: Array.Empty<string>()));
        }

        if (settings.StageFallback || requests.Count == 0)
        {
            requests.Add(new DockerBuildRequest(
                Dockerfile: settings.Dockerfile,
                BuildContext: settings.BuildContext,
                Runner: settings.Runner,
                Platform: settings.Platform,
                BuildTarget: settings.BuildTarget,
                BuildOutputFlags: settings.BuildOutputFlags,
                BuildArgFlags: settings.BuildArgFlags,
                BuildSecretFlags: settings.BuildSecretFlags,
                Tags: finalTags));
        }

        return requests;
    }

    private static bool ShouldPush(
        DockerBuildSettings settings,
        ExecutionContext context,
        out string reason)
    {
        if (!settings.PushEnabled)
        {
            reason = "push.disabled";
            return false;
        }

        if (settings.DenyNonPublicPush && settings.Classification == BuildClassification.NonPublic)
        {
            reason = "push.denied_non_public";
            return false;
        }

        if (settings.PushBranches.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(context.Branch))
            {
                reason = "push.branch_unknown";
                return false;
            }

            if (!settings.PushBranches.Any(pattern => BranchMatches(pattern, context.Branch!)))
            {
                reason = "push.branch_not_eligible";
                return false;
            }
        }

        reason = "push.allowed";
        return true;
    }

    private static string ResolveImage(ArtifactConfig artifact, IReadOnlyDictionary<string, string> dotEnv)
    {
        var explicitImage = GetStringSetting(artifact.Settings, "image");
        if (!string.IsNullOrWhiteSpace(explicitImage))
        {
            return explicitImage;
        }

        var registry = GetEnvironmentValue("DOCKER_TARGET_REGISTRY", dotEnv)
            ?? GetStringSetting(artifact.Settings, "target.registry", "registry");
        var repository = GetEnvironmentValue("DOCKER_TARGET_REPOSITORY", dotEnv)
            ?? GetStringSetting(artifact.Settings, "target.repository", "repository");

        if (!string.IsNullOrWhiteSpace(registry) && !string.IsNullOrWhiteSpace(repository))
        {
            var normalizedRegistry = NormalizeRegistry(registry);
            var normalizedRepository = NormalizeRepository(repository);
            return $"{normalizedRegistry}/{normalizedRepository}";
        }

        return artifact.Name;
    }

    private static string NormalizeRegistry(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://".Length..];
        }
        else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["http://".Length..];
        }

        normalized = normalized.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Docker target registry is empty. Set DOCKER_TARGET_REGISTRY to a registry host such as 'ghcr.io'.");
        }

        if (normalized.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Docker target registry '{value}' is invalid. Use only a registry host (for example 'ghcr.io' or 'myregistry.example.com:5000').");
        }

        var looksLikeRegistryHost = normalized.Contains('.', StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal)
            || string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeRegistryHost)
        {
            throw new InvalidOperationException($"Docker target registry '{value}' is not a valid registry host. Docker would treat it as a Docker Hub namespace (docker.io/{normalized}/...). Use a host like 'ghcr.io' or 'myregistry.example.com'.");
        }

        return normalized;
    }

    private static string NormalizeRepository(string value)
    {
        var normalized = value.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Docker target repository is empty. Set DOCKER_TARGET_REPOSITORY or settings.target.repository.");
        }

        return normalized;
    }

    private static bool ResolveCleanupLocal(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, string> dotEnv,
        bool isCi)
    {
        var configured = GetEnvironmentValue("DOCKER_CLEANUP_LOCAL", dotEnv)
            ?? GetStringSetting(settings, "cleanup.local", "cleanupLocal")
            ?? CleanupModeAuto;

        return configured.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            CleanupModeAuto => isCi,
            _ => isCi,
        };
    }

    private static bool ResolvePushEnabled(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, string> dotEnv)
    {
        var configured = GetEnvironmentValue("DOCKER_PUSH_ENABLED", dotEnv);
        if (!string.IsNullOrWhiteSpace(configured) && bool.TryParse(configured, out var enabledFromEnv))
        {
            return enabledFromEnv;
        }

        return GetBoolSetting(settings, "push.enabled", "pushEnabled") ?? true;
    }

    private static IReadOnlyList<string> ResolvePushBranches(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, string> dotEnv)
    {
        var configured = GetEnvironmentValue("DOCKER_PUSH_BRANCHES", dotEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return SplitDelimitedList(configured);
        }

        var branches = GetStringArraySetting(settings, "push.branches", "pushBranches");
        if (branches.Count > 0)
        {
            return branches;
        }

        var shortcut = GetStringSetting(settings, "push.branchesShortcut");
        return string.IsNullOrWhiteSpace(shortcut) ? Array.Empty<string>() : SplitDelimitedList(shortcut);
    }

    private static bool ResolveTagLatest(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyDictionary<string, string> dotEnv)
    {
        var configured = GetEnvironmentValue("DOCKER_TAG_LATEST", dotEnv);
        if (!string.IsNullOrWhiteSpace(configured) && bool.TryParse(configured, out var latest))
        {
            return latest;
        }

        return GetBoolSetting(settings, "tags.latest", "latest") ?? false;
    }

    private static BuildClassification ClassifyBuild(
        IReadOnlyDictionary<string, JsonElement> settings,
        string? branch)
    {
        var explicitPublic = GetBoolSetting(settings, "build.public", "publicBuild");
        if (explicitPublic.HasValue)
        {
            return explicitPublic.Value ? BuildClassification.Public : BuildClassification.NonPublic;
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            return BuildClassification.None;
        }

        var publicPatterns = GetStringArraySetting(settings, "publicBranches", "classification.publicBranches");
        var publicShortcut = GetStringSetting(settings, "publicBranchesShortcut", "classification.publicBranchesShortcut");
        if (!string.IsNullOrWhiteSpace(publicShortcut))
        {
            publicPatterns = publicPatterns.Concat(SplitDelimitedList(publicShortcut)).ToArray();
        }

        if (publicPatterns.Any(pattern => BranchMatches(pattern, branch)))
        {
            return BuildClassification.Public;
        }

        var nonPublicPatterns = GetStringArraySetting(settings, "nonPublicBranches", "classification.nonPublicBranches");
        var nonPublicShortcut = GetStringSetting(settings, "nonPublicBranchesShortcut", "classification.nonPublicBranchesShortcut");
        if (!string.IsNullOrWhiteSpace(nonPublicShortcut))
        {
            nonPublicPatterns = nonPublicPatterns.Concat(SplitDelimitedList(nonPublicShortcut)).ToArray();
        }

        return nonPublicPatterns.Any(pattern => BranchMatches(pattern, branch))
            ? BuildClassification.NonPublic
            : BuildClassification.None;
    }

    private static TagPolicySettings ResolveTagPolicy(IReadOnlyDictionary<string, JsonElement> settings) =>
        new(
            PublicKinds: GetStringArraySetting(settings, "tagPolicy.public"),
            NonPublicKinds: GetStringArraySetting(settings, "tagPolicy.nonPublic"));

    private static IReadOnlyList<string> ResolveExplicitTagStrategies(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!TryGetSettingValue(settings, out var value, "tags"))
        {
            return Array.Empty<string>();
        }

        return value.ValueKind is JsonValueKind.String or JsonValueKind.Array
            ? GetStringArrayValue(value)
            : Array.Empty<string>();
    }

    private static DockerAliasSettings ResolveAliasSettings(IReadOnlyDictionary<string, JsonElement> settings) =>
        new(
            Branch: GetBoolSetting(settings, "aliases.branch") ?? false,
            SanitizedBranch: GetBoolSetting(settings, "aliases.sanitizedBranch") ?? false,
            Sanitize: GetBoolSetting(settings, "aliases.sanitize") ?? false,
            Prefix: GetStringSetting(settings, "aliases.prefix"),
            Suffix: GetStringSetting(settings, "aliases.suffix"),
            NonPublicPrefix: GetStringSetting(settings, "aliases.nonPublicPrefix"),
            Rules: ResolveAliasRules(settings));

    private static IReadOnlyList<AliasRule> ResolveAliasRules(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!TryGetSettingValue(settings, out var value, "aliases.rules") || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AliasRule>();
        }

        var rules = new List<AliasRule>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var match = item.TryGetProperty("match", out var matchValue) ? GetString(matchValue) : null;
            var template = item.TryGetProperty("template", out var templateValue) ? GetString(templateValue) : null;
            var sanitize = item.TryGetProperty("sanitize", out var sanitizeValue) ? GetString(sanitizeValue) : null;

            if (!string.IsNullOrWhiteSpace(match) && !string.IsNullOrWhiteSpace(template))
            {
                rules.Add(new AliasRule(match!, template!, sanitize));
            }
        }

        return rules;
    }

    private static IReadOnlyList<DockerStageDefinition> ResolveStages(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!TryGetSettingValue(settings, out var value, "stages") || value.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<DockerStageDefinition>();
        }

        var stages = new List<DockerStageDefinition>();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            stages.Add(new DockerStageDefinition(
                Name: property.Name,
                Target: property.Value.TryGetProperty("target", out var targetValue) ? GetString(targetValue) : null,
                Output: property.Value.TryGetProperty("output", out var outputValue) ? GetStringArrayValue(outputValue) : Array.Empty<string>(),
                Runner: property.Value.TryGetProperty("runner", out var runnerValue) ? GetString(runnerValue) : null,
                Platform: property.Value.TryGetProperty("platform", out var platformValue) ? GetString(platformValue) : null));
        }

        return stages;
    }

    private static IReadOnlyList<string> ResolveTagKinds(
        DockerBuildSettings settings,
        ExecutionContext context,
        string versionTag)
    {
        var kinds = settings.Classification == BuildClassification.NonPublic
            ? settings.TagPolicy.NonPublicKinds
            : settings.TagPolicy.PublicKinds;

        if (settings.Classification == BuildClassification.NonPublic &&
            string.Equals(settings.NonPublicMode, "full-only", StringComparison.OrdinalIgnoreCase))
        {
            kinds = ["full"];
        }

        if (kinds.Count == 0)
        {
            kinds = ["full", "majorMinor", "major"];
        }

        return kinds;
    }

    private static IReadOnlyList<string> BuildAliasTags(
        string image,
        ExecutionContext context,
        DockerBuildSettings settings)
    {
        if (string.IsNullOrWhiteSpace(context.Branch))
        {
            return Array.Empty<string>();
        }

        var branch = context.Branch!;
        var aliases = new List<string>();
        var aliasSettings = settings.Aliases;

        if (aliasSettings.Branch)
        {
            aliases.Add(FormatAlias(branch.Replace('/', '-'), aliasSettings));
        }

        if (aliasSettings.SanitizedBranch)
        {
            aliases.Add(FormatAlias(Slug(branch), aliasSettings));
        }

        foreach (var rule in aliasSettings.Rules)
        {
            if (!TryApplyAliasRule(rule, branch, out var alias))
            {
                continue;
            }

            aliases.Add(FormatAlias(alias, aliasSettings, rule.Sanitize));
            break;
        }

        if (settings.Classification == BuildClassification.NonPublic && !string.IsNullOrWhiteSpace(aliasSettings.NonPublicPrefix))
        {
            aliases = aliases.Select(alias => $"{aliasSettings.NonPublicPrefix}{alias}").ToList();
        }

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => $"{image}:{alias}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatAlias(string alias, DockerAliasSettings settings, string? ruleSanitize = null)
    {
        var value = $"{settings.Prefix}{alias}{settings.Suffix}";
        var shouldSanitize = settings.Sanitize || string.Equals(ruleSanitize, "sanitized", StringComparison.OrdinalIgnoreCase);
        return shouldSanitize ? Slug(value) : value;
    }

    private static bool TryApplyAliasRule(AliasRule rule, string branch, out string alias)
    {
        alias = string.Empty;
        Match match;

        if (rule.Match.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = rule.Match["regex:".Length..];
            match = Regex.Match(branch, pattern, RegexOptions.CultureInvariant);
        }
        else
        {
            var pattern = "^" + Regex.Escape(rule.Match).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            match = Regex.Match(branch, pattern, RegexOptions.CultureInvariant);
        }

        if (!match.Success)
        {
            return false;
        }

        alias = rule.Template
            .Replace("$BRANCH", branch, StringComparison.Ordinal)
            .Replace("$BRANCH_SANITIZED", Slug(branch), StringComparison.Ordinal);

        for (var i = 1; i < match.Groups.Count; i++)
        {
            alias = alias.Replace($"${i}", match.Groups[i].Value, StringComparison.Ordinal);
        }

        return true;
    }

    private static string FormatMajorMinorTag(VersionResult version, BuildClassification classification)
    {
        var value = $"{version.Major}.{version.Minor}";
        if (!string.IsNullOrWhiteSpace(version.PreReleaseLabel))
        {
            return value + "-" + version.PreReleaseLabel;
        }

        return classification == BuildClassification.NonPublic && string.IsNullOrWhiteSpace(version.PreReleaseLabel)
            ? value + "-pre"
            : value;
    }

    private static string FormatMajorTag(VersionResult version) =>
        !string.IsNullOrWhiteSpace(version.PreReleaseLabel)
            ? $"{version.Major}-{version.PreReleaseLabel}"
            : version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool BranchMatches(string pattern, string branch)
    {
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(branch, pattern["regex:".Length..], RegexOptions.CultureInvariant);
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(branch, regex, RegexOptions.CultureInvariant);
    }

    private static Dictionary<string, string?> BuildEnvironmentOverrides(IReadOnlyDictionary<string, string> dotEnv)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in dotEnv)
        {
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                overrides[key] = value;
            }
        }

        return overrides;
    }

    private static string? GetEnvironmentValue(string name, IReadOnlyDictionary<string, string> dotEnv) =>
        Environment.GetEnvironmentVariable(name)
        ?? (dotEnv.TryGetValue(name, out var value) ? value : null);

    private static bool? GetBoolSetting(IReadOnlyDictionary<string, JsonElement> settings, params string[] paths)
    {
        var value = GetStringSetting(settings, paths);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? GetStringSetting(IReadOnlyDictionary<string, JsonElement> settings, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (TryGetSettingValue(settings, out var value, path))
            {
                return GetString(value);
            }
        }

        return null;
    }

    private static bool TryGetSettingValue(
        IReadOnlyDictionary<string, JsonElement> settings,
        out JsonElement value,
        string path)
    {
        value = default;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !settings.TryGetValue(segments[0], out value))
        {
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segments[i], out value))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> GetStringArraySetting(
        IReadOnlyDictionary<string, JsonElement> settings,
        params string[] paths)
    {
        foreach (var path in paths)
        {
            if (TryGetSettingValue(settings, out var value, path))
            {
                return GetStringArrayValue(value);
            }
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> GetStringArrayValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => SplitDelimitedList(value.GetString() ?? string.Empty),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(item => GetString(item))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray(),
            _ => Array.Empty<string>(),
        };

    private static IReadOnlyList<string> SplitDelimitedList(string value) =>
        value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> ParseBuildArgsFile(string filePath)
    {
        var resolvedPath = Path.GetFullPath(filePath);
        if (!File.Exists(resolvedPath))
        {
            throw new InvalidOperationException($"DOCKER_BUILD_ARGS_FILE file not found: {resolvedPath}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(resolvedPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("DOCKER_BUILD_ARGS_FILE must point to a JSON object.");
        }

        return ParseBuildArgsObject(document.RootElement, "DOCKER_BUILD_ARGS_FILE");
    }

    private static IReadOnlyList<string> ParseBuildArgsValue(string value, string sourceName)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return Array.Empty<string>();
        }

        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(trimmed);
            return ParseBuildArgsObject(document.RootElement, sourceName);
        }

        if (trimmed.Contains("--build-arg", StringComparison.Ordinal))
        {
            return SplitCommandArgs(trimmed);
        }

        return trimmed.Split([";", "\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(pair =>
            {
                var normalized = pair.Trim();
                return string.IsNullOrWhiteSpace(normalized)
                    ? Array.Empty<string>()
                    : ["--build-arg", normalized];
            })
            .ToArray();
    }

    private static IReadOnlyList<string> ParseBuildArgsObject(JsonElement value, string sourceName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{sourceName} must be a JSON object of KEY=value pairs.");
        }

        return value.EnumerateObject()
            .SelectMany(property =>
            {
                var rendered = GetString(property.Value) ?? string.Empty;
                return new[] { "--build-arg", $"{property.Name}={rendered}" };
            })
            .ToArray();
    }

    private static IEnumerable<string> ParseBuildArgArrayItem(JsonElement item)
    {
        var text = GetString(item);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text.Contains("--build-arg", StringComparison.Ordinal)
            ? SplitCommandArgs(text)
            : ["--build-arg", text];
    }

    private static IReadOnlyList<BuildSecretSpec> ParseSecretsJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseSecretsElement(document.RootElement, "DOCKER_SECRETS");
    }

    private static IReadOnlyList<BuildSecretSpec> ParseSecretsElement(JsonElement value, string sourceName)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<BuildSecretSpec>();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{sourceName} must be an object mapping secret ids to env/file definitions.");
        }

        return value.EnumerateObject()
            .Select(property => ParseSecretSpec(property.Name, property.Value, sourceName))
            .ToArray();
    }

    private static BuildSecretSpec ParseSecretSpec(string rawId, JsonElement value, string sourceName)
    {
        var id = NormalizeSecretId(rawId, sourceName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{sourceName}.{rawId} must be an object with either 'env' or 'file'.");
        }

        var envName = value.TryGetProperty("env", out var envValue) ? GetString(envValue) : null;
        var filePath = value.TryGetProperty("file", out var fileValue) ? GetString(fileValue) : null;

        if (!string.IsNullOrWhiteSpace(envName) && !string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException($"{sourceName}.{rawId} must not specify both 'env' and 'file'.");
        }

        if (!string.IsNullOrWhiteSpace(envName))
        {
            return BuildSecretSpec.FromEnvironment(id, envName);
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return BuildSecretSpec.FromFile(id, filePath);
        }

        throw new InvalidOperationException($"{sourceName}.{rawId} must specify either 'env' or 'file'.");
    }

    private static string NormalizeSecretId(string value, string sourceName)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{sourceName} includes an empty secret id.");
        }

        return normalized;
    }

    private static string NormalizeRunner(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "auto" => "auto",
            BuildxRunner => BuildxRunner,
            _ => DefaultRunner,
        };

    private static string? InferRegistryFromImage(string image)
    {
        var slashIndex = image.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex <= 0)
        {
            return null;
        }

        var candidate = image[..slashIndex];
        return candidate.Contains('.', StringComparison.Ordinal)
            || candidate.Contains(':', StringComparison.Ordinal)
            || string.Equals(candidate, "localhost", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static void CleanupDockerConfigDirectory(string? tempDockerConfigDirectory)
    {
        if (string.IsNullOrWhiteSpace(tempDockerConfigDirectory) || !Directory.Exists(tempDockerConfigDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(tempDockerConfigDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<bool> IsBuildxAvailableAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? envOverrides,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync([BuildxRunner, "version"], workingDirectory, envOverrides, null, cancellationToken);
        return result.ExitCode == 0;
    }

    private static async Task<(int ExitCode, string Output)> RunDockerAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? envOverrides,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (envOverrides is not null)
        {
            foreach (var (key, value) in envOverrides)
            {
                if (value is null)
                {
                    _ = process.StartInfo.Environment.Remove(key);
                }
                else
                {
                    process.StartInfo.Environment[key] = value;
                }
            }
        }

        process.Start();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);

        return (process.ExitCode, stdout + stderr);
    }

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        artifact.Settings.TryGetValue(key, out var value) ? GetString(value) : null;

    private static string? GetString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString(),
        };

    private static string FormatArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument) =>
        argument.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;

    private static IReadOnlyList<string> SplitCommandArgs(string value) =>
        System.Text.RegularExpressions.Regex.Matches(value, "\"([^\"]*)\"|'([^']*)'|(\\S+)")
            .Select(match =>
            {
                var groups = match.Groups;
                return groups[1].Success ? groups[1].Value : groups[2].Success ? groups[2].Value : groups[3].Value;
            })
            .ToArray();

    private static string Slug(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private static string GetVersionTag(VersionResult version) =>
        version.DockerVersion ?? version.SemVer;

    private sealed record DockerBuildSettings(
        string Image,
        string Dockerfile,
        string BuildContext,
        string Runner,
        string? Platform,
        string? BuildTarget,
        IReadOnlyList<string> BuildOutputFlags,
        IReadOnlyList<string> BuildArgFlags,
        IReadOnlyList<string> BuildSecretFlags,
        string? LoginRegistry,
        bool CleanupLocal,
        bool PushEnabled,
        IReadOnlyList<string> PushBranches,
        bool DenyNonPublicPush,
        bool TagLatest,
        BuildClassification Classification,
        TagPolicySettings TagPolicy,
        string? NonPublicMode,
        IReadOnlyList<string> TagStrategies,
        DockerAliasSettings Aliases,
        IReadOnlyList<DockerStageDefinition> Stages,
        bool StageFallback);

    private sealed record DockerBuildRequest(
        string Dockerfile,
        string BuildContext,
        string Runner,
        string? Platform,
        string? BuildTarget,
        IReadOnlyList<string> BuildOutputFlags,
        IReadOnlyList<string> BuildArgFlags,
        IReadOnlyList<string> BuildSecretFlags,
        IReadOnlyList<string> Tags);

    private sealed record DockerAliasSettings(
        bool Branch,
        bool SanitizedBranch,
        bool Sanitize,
        string? Prefix,
        string? Suffix,
        string? NonPublicPrefix,
        IReadOnlyList<AliasRule> Rules);

    private sealed record TagPolicySettings(
        IReadOnlyList<string> PublicKinds,
        IReadOnlyList<string> NonPublicKinds);

    private sealed record DockerStageDefinition(
        string Name,
        string? Target,
        IReadOnlyList<string> Output,
        string? Runner,
        string? Platform);

    private sealed record AliasRule(
        string Match,
        string Template,
        string? Sanitize);

    private enum BuildClassification
    {
        None,
        Public,
        NonPublic,
    }

    private sealed record BuildSecretSpec(string Id, string? EnvName, string? FilePath)
    {
        public static BuildSecretSpec FromEnvironment(string id, string envName) => new(id, envName, null);

        public static BuildSecretSpec FromFile(string id, string filePath) => new(id, null, filePath);

        public IReadOnlyList<string> ToDockerFlags() =>
            EnvName is not null
                ? ["--secret", $"id={Id},env={EnvName}"]
                : ["--secret", $"id={Id},src={FilePath}"];
    }
}
