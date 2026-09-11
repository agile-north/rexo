namespace Rexo.Artifacts.Helm;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

public sealed class HelmOciArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("helm-oci", new HelmOciArtifactProvider());

    private const string DefaultHelmContainerImage = "alpine/helm:3.17.3";
    private readonly Func<ArtifactConfig, IReadOnlyList<string>, string, IReadOnlyDictionary<string, string?>?, string?, CancellationToken, Task<(int ExitCode, string Output)>> _runHelmAsync;

    public HelmOciArtifactProvider(
        Func<ArtifactConfig, IReadOnlyList<string>, string, IReadOnlyDictionary<string, string?>?, string?, CancellationToken, Task<(int ExitCode, string Output)>>? runHelmAsync = null)
    {
        _runHelmAsync = runHelmAsync ?? RunHelmAsync;
    }

    public string Type => "helm-oci";

    public IReadOnlyList<string> GetPlannedTags(ArtifactConfig artifact, ExecutionContext context) =>
        context.Version is not null
            ? [context.Version.SemVer]
            : Array.Empty<string>();

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var chartPath = GetSetting(artifact, "chartPath") ?? "chart";
        var output = GetSetting(artifact, "output") ?? Path.Combine("artifacts", "charts");
        var chartName = GetSetting(artifact, "chart") ?? artifact.Name;
        var version = context.Version?.SemVer;

        Directory.CreateDirectory(Path.Combine(context.RepositoryRoot, output));

        var args = new List<string>
        {
            "package",
            chartPath,
            "--destination",
            output,
        };

        if (!string.IsNullOrWhiteSpace(version))
        {
            args.Add("--version");
            args.Add(version);
            args.Add("--app-version");
            args.Add(version);
        }

        Console.WriteLine($"  > helm {FormatArguments(args)}");
        var result = await _runHelmAsync(artifact, args, context.RepositoryRoot, null, null, cancellationToken);
        if (result.ExitCode != 0 && ShouldRetryWithDependencyUpdate(result.Output))
        {
            var dependencyArgs = new List<string>
            {
                "dependency",
                "update",
                chartPath,
            };

            Console.WriteLine($"  > helm {FormatArguments(dependencyArgs)}");
            var dependencyResult = await _runHelmAsync(artifact, dependencyArgs, context.RepositoryRoot, null, null, cancellationToken);
            if (dependencyResult.ExitCode != 0)
            {
                return new ArtifactBuildResult(artifact.Name, false, null);
            }

            Console.WriteLine($"  > helm {FormatArguments(args)}");
            result = await _runHelmAsync(artifact, args, context.RepositoryRoot, null, null, cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            return new ArtifactBuildResult(artifact.Name, false, null);
        }

        var packagePath = TryFindPackagePath(context.RepositoryRoot, output, chartName, version);
        if (string.IsNullOrWhiteSpace(packagePath) && !string.IsNullOrWhiteSpace(version))
        {
            packagePath = Path.Combine(context.RepositoryRoot, output, $"{chartName}-{version}.tgz");
        }

        return new ArtifactBuildResult(artifact.Name, true, packagePath);
    }

    public Task<ArtifactTagResult> TagAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var tags = context.Version is not null
            ? new[] { context.Version.SemVer }
            : Array.Empty<string>();

        return Task.FromResult(new ArtifactTagResult(artifact.Name, true, tags));
    }

    public async Task<ArtifactPushResult> PushAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var chartName = GetSetting(artifact, "chart") ?? artifact.Name;
        var output = GetSetting(artifact, "output") ?? Path.Combine("artifacts", "charts");
        var version = context.Version?.SemVer;

        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        await TryHelmRegistryLoginAsync(artifact, context, fileEnv, cancellationToken);

        var packagePath = TryFindPackagePath(context.RepositoryRoot, output, chartName, version);
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            var buildResult = await BuildAsync(artifact, context, cancellationToken);
            if (!buildResult.Success || string.IsNullOrWhiteSpace(buildResult.Location))
            {
                return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
            }

            packagePath = buildResult.Location;
        }

        var destination = ResolveOciDestination(artifact, fileEnv);
        if (string.IsNullOrWhiteSpace(destination))
        {
            Console.Error.WriteLine("Helm OCI destination is required. Set settings.oci or settings.registry + settings.repository.");
            return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
        }

        destination = RemoveDuplicateChartNameLeaf(destination, chartName);

        var pushArgs = new[] { "push", packagePath, destination };
        Console.WriteLine($"  > helm {FormatArguments(pushArgs)}");
        var pushResult = await _runHelmAsync(artifact, pushArgs, context.RepositoryRoot, null, null, cancellationToken);

        if (pushResult.ExitCode != 0)
        {
            return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
        }

        var reference = !string.IsNullOrWhiteSpace(version)
            ? $"{destination}/{chartName}:{version}"
            : $"{destination}/{chartName}";

        return new ArtifactPushResult(artifact.Name, true, [reference]);
    }

    private static string RemoveDuplicateChartNameLeaf(string destination, string chartName)
    {
        if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(chartName))
        {
            return destination;
        }

        var normalized = destination.Trim();
        if (!normalized.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "oci://" + normalized;
        }

        var ociBody = normalized["oci://".Length..].Trim('/');
        var registrySeparator = ociBody.IndexOf('/');
        if (registrySeparator < 0)
        {
            return normalized;
        }

        var registry = ociBody[..registrySeparator].Trim();
        var repository = ociBody[(registrySeparator + 1)..].Trim('/');
        if (string.IsNullOrWhiteSpace(registry) || string.IsNullOrWhiteSpace(repository))
        {
            return normalized;
        }

        var lastSeparator = repository.LastIndexOf('/');
        if (lastSeparator < 0)
        {
            return normalized;
        }

        var leaf = repository[(lastSeparator + 1)..];
        if (!string.Equals(leaf, chartName, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var dedupedRepository = repository[..lastSeparator];
        if (string.IsNullOrWhiteSpace(dedupedRepository))
        {
            return normalized;
        }

        return $"oci://{registry}/{dedupedRepository}";
    }

    private async Task TryHelmRegistryLoginAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        IReadOnlyDictionary<string, string> fileEnv,
        CancellationToken cancellationToken)
    {
        var auth = ResolveRegistryAuth(
            artifact: artifact,
            configuredRegistry: ResolveRegistryHostForLogin(artifact, fileEnv),
            fileEnv: fileEnv);

        if (!string.IsNullOrWhiteSpace(auth.Error))
        {
            Console.Error.WriteLine(auth.Error);
            return;
        }

        if (!auth.HasCredentials)
        {
            if (FeedAuthResolver.ShouldWarnOnMissingContainerRegistryCredentials(auth.Endpoint, fileEnv))
            {
                Console.WriteLine($"  Warning: no Helm registry credentials resolved for '{auth.Endpoint}'. Push may fail unless the registry allows anonymous access.");
            }

            return;
        }

        var registry = auth.Endpoint;
        if (string.IsNullOrWhiteSpace(registry))
        {
            Console.Error.WriteLine("Helm login registry could not be determined. Set settings.target.loginRegistry or HELM_OCI_LOGIN_REGISTRY.");
            return;
        }

        var args = new[] { "registry", "login", registry, "--username", auth.Username!, "--password-stdin" };
        Console.WriteLine($"  > helm {FormatArguments(args)}");
        await _runHelmAsync(artifact, args, context.RepositoryRoot, null, auth.Secret! + Environment.NewLine, cancellationToken);
    }

    private static string? ResolveOciDestination(
        ArtifactConfig artifact,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var oci = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_OCI_TARGET",
            configuredEnvName: GetSetting(artifact, "target.ociEnv"),
            configuredValue: GetSetting(artifact, "target.oci"),
            fileEnv: fileEnv);
        if (!string.IsNullOrWhiteSpace(oci))
        {
            return oci.StartsWith("oci://", StringComparison.OrdinalIgnoreCase)
                ? oci
                : "oci://" + oci;
        }

        var registry = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_OCI_TARGET_REGISTRY",
            configuredEnvName: GetSetting(artifact, "target.registryEnv"),
            configuredValue: GetSetting(artifact, "target.registry"),
            fileEnv: fileEnv);
        var repository = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_OCI_TARGET_REPOSITORY",
            configuredEnvName: GetSetting(artifact, "target.repositoryEnv"),
            configuredValue: GetSetting(artifact, "target.repository"),
            fileEnv: fileEnv);

        if (ciInferenceEnabled
            && !string.IsNullOrWhiteSpace(registry)
            && string.IsNullOrWhiteSpace(repository))
        {
            repository = TryResolveImplicitRepository(registry, fileEnv);
        }

        if (ciInferenceEnabled && (string.IsNullOrWhiteSpace(registry) || string.IsNullOrWhiteSpace(repository)))
        {
            var implicitRegistry = FeedAuthResolver.ResolveImplicitContainerRegistry(fileEnv, ciInferenceEnabled);
            if (!string.IsNullOrWhiteSpace(implicitRegistry))
            {
                registry = implicitRegistry;
                repository ??= TryResolveImplicitRepository(implicitRegistry, fileEnv);
            }
        }

        if (ciInferenceEnabled && (string.IsNullOrWhiteSpace(registry) || string.IsNullOrWhiteSpace(repository)))
        {
            var gitLabRegistry = FeedAuthResolver.GetEnv("CI_REGISTRY", fileEnv);
            var gitLabProjectPath = FeedAuthResolver.GetEnv("CI_PROJECT_PATH", fileEnv);
            var gitLabCi = FeedAuthResolver.GetEnv("GITLAB_CI", fileEnv);
            if (string.Equals(gitLabCi, "true", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(gitLabRegistry)
                && !string.IsNullOrWhiteSpace(gitLabProjectPath))
            {
                registry = gitLabRegistry;
                repository = gitLabProjectPath;
            }
        }

        if (string.IsNullOrWhiteSpace(registry) || string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        return $"oci://{registry.TrimEnd('/')}/{repository.Trim('/')}";
    }

    private static string? TryFindPackagePath(string repoRoot, string output, string chartName, string? version)
    {
        var outputPath = Path.Combine(repoRoot, output);
        if (!Directory.Exists(outputPath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            var expected = Path.Combine(outputPath, $"{chartName}-{version}.tgz");
            if (File.Exists(expected))
            {
                return expected;
            }
        }

        return Directory
            .EnumerateFiles(outputPath, $"{chartName}-*.tgz", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? ResolveRegistryHostForLogin(
        ArtifactConfig artifact,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var loginRegistry = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_OCI_LOGIN_REGISTRY",
            configuredEnvName: GetSetting(artifact, "target.loginRegistryEnv"),
            configuredValue: GetSetting(artifact, "target.loginRegistry"),
            fileEnv: fileEnv);
        if (!string.IsNullOrWhiteSpace(loginRegistry))
        {
            return FeedAuthResolver.ExtractRegistryHost(loginRegistry) ?? loginRegistry;
        }

        var targetRegistry = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_OCI_TARGET_REGISTRY",
            configuredEnvName: GetSetting(artifact, "target.registryEnv"),
            configuredValue: GetSetting(artifact, "target.registry"),
            fileEnv: fileEnv);
        if (!string.IsNullOrWhiteSpace(targetRegistry))
        {
            return FeedAuthResolver.ExtractRegistryHost(targetRegistry) ?? targetRegistry;
        }

        var targetOci = FeedAuthResolver.ResolveTargetValue(
            defaultEnvName: "HELM_OCI_TARGET",
            configuredEnvName: GetSetting(artifact, "target.ociEnv"),
            configuredValue: GetSetting(artifact, "target.oci"),
            fileEnv: fileEnv);
        if (!string.IsNullOrWhiteSpace(targetOci))
        {
            return FeedAuthResolver.ExtractRegistryHost(targetOci);
        }

        return FeedAuthResolver.ResolveImplicitContainerRegistry(fileEnv, ciInferenceEnabled);
    }

    private static string? GetSetting(ArtifactConfig artifact, string key)
    {
        if (!TryGetSettingValue(artifact.Settings, out var value, key))
        {
            return null;
        }

        return GetString(value);
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

    private static bool ShouldRetryWithDependencyUpdate(string output) =>
        output.Contains("found in Chart.yaml, but missing in charts/ directory", StringComparison.OrdinalIgnoreCase);

    private static string FormatArguments(IReadOnlyList<string> args) =>
        string.Join(" ", args.Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static async Task<(int ExitCode, string Output)> RunHelmAsync(
        ArtifactConfig artifact,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? envOverrides,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var native = await TryRunHostHelmAsync(args, workingDirectory, envOverrides, standardInput, cancellationToken);
        if (native is not null)
        {
            return native.Value;
        }

        if (string.Equals(GetSetting(artifact, "useDocker"), "false", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "helm CLI is not installed and Docker fallback is disabled (settings.useDocker = false).");
        }

        var dockerImage = Environment.GetEnvironmentVariable("HELM_CONTAINER_IMAGE")
            ?? GetSetting(artifact, "dockerImage")
            ?? DefaultHelmContainerImage;
        Console.WriteLine($"  > helm not found on host, falling back to dockerized Helm runtime ({dockerImage})");
        return await RunHelmViaDockerAsync(args, workingDirectory, envOverrides, standardInput, dockerImage, cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)?> TryRunHostHelmAsync(
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? envOverrides,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunProcessAsync(
                fileName: "helm",
                args: args,
                workingDirectory: workingDirectory,
                envOverrides: envOverrides,
                standardInput: standardInput,
                cancellationToken: cancellationToken);
        }
        catch (Win32Exception ex) when (IsCommandNotFound(ex))
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunHelmViaDockerAsync(
        IReadOnlyList<string> helmArgs,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? envOverrides,
        string? standardInput,
        string helmContainerImage,
        CancellationToken cancellationToken)
    {
        var dockerArgs = new List<string>
        {
            "run",
            "--rm",
            "-v",
            $"{workingDirectory}:/work",
            "-w",
            "/work",
        };

        if (standardInput is not null)
        {
            dockerArgs.Add("-i");
        }

        if (envOverrides is not null)
        {
            foreach (var (key, value) in envOverrides)
            {
                if (value is null)
                {
                    continue;
                }

                dockerArgs.Add("-e");
                dockerArgs.Add($"{key}={value}");
            }
        }

        dockerArgs.Add(helmContainerImage);
        dockerArgs.AddRange(helmArgs);

        return await RunProcessAsync(
            fileName: "docker",
            args: dockerArgs,
            workingDirectory: workingDirectory,
            envOverrides: null,
            standardInput: standardInput,
            cancellationToken: cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? envOverrides,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (envOverrides is not null)
        {
            foreach (var (key, value) in envOverrides)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
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

    private static bool IsCommandNotFound(Win32Exception exception) =>
        exception.NativeErrorCode is 2 or 3;

    /// <summary>
    /// Resolves Helm OCI registry credentials.  Order: target.usernameEnv +
    /// target.passwordEnv (defaults HELM_REGISTRY_USERNAME/HELM_REGISTRY_PASSWORD)
    /// → GITHUB_ACTOR + GITHUB_TOKEN for ghcr.io registries.
    /// Credentials are used for <c>helm registry login</c> before push.
    /// </summary>
    private static FeedAuthResolution ResolveRegistryAuth(
        ArtifactConfig artifact,
        string? configuredRegistry,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var username = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "HELM_REGISTRY_USERNAME",
            configuredEnvName: GetSetting(artifact, "target.usernameEnv"),
            fileEnv: fileEnv);
        var secret = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "HELM_REGISTRY_PASSWORD",
            configuredEnvName: GetSetting(artifact, "target.passwordEnv"),
            fileEnv: fileEnv);
        var endpoint = configuredRegistry
                       ?? FeedAuthResolver.ResolveTargetValue(
                           defaultEnvName: "HELM_REGISTRY",
                           configuredEnvName: GetSetting(artifact, "target.loginRegistryEnv"),
                           configuredValue: GetSetting(artifact, "target.loginRegistry"),
                           fileEnv: fileEnv);

        return FeedAuthResolver.FinalizeContainerRegistryAuth(
            username,
            secret,
            endpoint,
            fileEnv,
            "HELM_REGISTRY_USERNAME and HELM_REGISTRY_PASSWORD must both be set.",
            "Helm login registry could not be determined. Set settings.target.loginRegistry or HELM_REGISTRY.",
            ciInferenceEnabled);
    }

    private static string? TryResolveImplicitRepository(
        string registry,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var normalizedRegistry = FeedAuthResolver.ExtractRegistryHost(registry) ?? registry;
        if (string.Equals(normalizedRegistry, "ghcr.io", StringComparison.OrdinalIgnoreCase))
        {
            var githubActions = FeedAuthResolver.GetEnv("GITHUB_ACTIONS", fileEnv);
            var githubRepository = FeedAuthResolver.GetEnv("GITHUB_REPOSITORY", fileEnv);
            if (string.Equals(githubActions, "true", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(githubRepository))
            {
                return githubRepository.Trim('/');
            }
        }

        var gitLabRegistry = FeedAuthResolver.ExtractRegistryHost(FeedAuthResolver.GetEnv("CI_REGISTRY", fileEnv));
        var gitLabProjectPath = FeedAuthResolver.GetEnv("CI_PROJECT_PATH", fileEnv);
        if (!string.IsNullOrWhiteSpace(gitLabRegistry)
            && string.Equals(normalizedRegistry, gitLabRegistry, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(gitLabProjectPath))
        {
            return gitLabProjectPath.Trim('/');
        }

        return null;
    }
}
