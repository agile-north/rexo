namespace Rexo.Artifacts.NuGet;

using System.Diagnostics;
using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Core.Abstractions;
using Rexo.Core.Environment;
using Rexo.Core.Models;

public sealed class NuGetArtifactProvider : IArtifactProvider
{
    public static void Register(ArtifactProviderRegistry registry) =>
        registry.Register("nuget", new NuGetArtifactProvider());

    private readonly Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>> _runDotnetAsync;

    public NuGetArtifactProvider(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>>? runDotnetAsync = null)
    {
        _runDotnetAsync = runDotnetAsync ?? RunDotnetAsync;
    }

    public string Type => "nuget";

    public async Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var project = GetSetting(artifact, "project") ?? string.Empty;
        var output = GetSetting(artifact, "output") ?? "artifacts/packages";
        var version = context.Version?.SemVer;

        Directory.CreateDirectory(Path.Combine(context.RepositoryRoot, output));

        var args = $"pack {project} --configuration Release --output {output}";
        if (!string.IsNullOrEmpty(version))
        {
            args += $" /p:Version={version}";
        }

        Console.WriteLine($"  > dotnet {args}");

        var result = await _runDotnetAsync(args, context.RepositoryRoot, cancellationToken);

        return new ArtifactBuildResult(
            Name: artifact.Name,
            Success: result.ExitCode == 0,
            Location: result.ExitCode == 0 ? Path.Combine(context.RepositoryRoot, output) : null);
    }

    public Task<ArtifactTagResult> TagAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // NuGet packages are versioned at pack time, no separate tagging step needed
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
        var output = GetSetting(artifact, "output") ?? "artifacts/packages";
        var packageVersion = context.Version?.NuGetVersion ?? context.Version?.SemVer;
        var packageTargets = ResolvePackageTargets(context.RepositoryRoot, output, artifact.Name, packageVersion);
        var symbolPattern = ResolveSymbolPattern(artifact, output, packageVersion);
        var fileEnv = FeedAuthResolver.OverlayMappedEnvironment(RepositoryEnvironmentFiles.Load(context.RepositoryRoot), context.MappedSecretEnvironment);
        var source = ResolveSource(artifact, fileEnv);
        var auth = ResolveAuth(artifact, source, GetSetting(artifact.Settings, "target.apiKeyEnv"), fileEnv);
        if (!auth.HasCredentials)
        {
            Console.Error.WriteLine("NuGet auth preflight failed: no API token resolved from env/CI identity.");
            return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
        }

        foreach (var packageTarget in packageTargets)
        {
            var args = $"nuget push \"{packageTarget}\" --source {source} --skip-duplicate";
            if (!string.IsNullOrEmpty(auth.Secret))
            {
                args += $" --api-key {auth.Secret}";
            }

            Console.WriteLine($"  > dotnet {args}");

            var result = await _runDotnetAsync(args, context.RepositoryRoot, cancellationToken);
            if (result.ExitCode != 0)
            {
                return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
            }
        }

        var published = new List<string> { $"{source}/{artifact.Name}" };

        var pushSymbols = IsTrue(GetSetting(artifact.Settings, "symbols.enabled"));
        if (pushSymbols && HasMatchingFiles(context.RepositoryRoot, symbolPattern))
        {
            var symbolSource = ResolveSymbolSource(artifact, source, fileEnv);
            var symbolAuth = ResolveSymbolAuth(
                artifact,
                symbolSource,
                GetSetting(artifact.Settings, "symbols.apiKeyEnv"),
                fileEnv,
                auth.Secret);

            if (!symbolAuth.HasCredentials)
            {
                Console.Error.WriteLine("NuGet symbol auth preflight failed: no API token resolved from env/CI identity.");
                return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
            }

            var symbolArgs = $"nuget push \"{symbolPattern}\" --source {symbolSource} --skip-duplicate";
            if (!string.IsNullOrEmpty(symbolAuth.Secret))
            {
                symbolArgs += $" --api-key {symbolAuth.Secret}";
            }

            Console.WriteLine($"  > dotnet {symbolArgs}");

            var symbolResult = await _runDotnetAsync(symbolArgs, context.RepositoryRoot, cancellationToken);
            if (symbolResult.ExitCode != 0)
            {
                return new ArtifactPushResult(artifact.Name, false, Array.Empty<string>());
            }

            published.Add($"{symbolSource}/{artifact.Name} (symbols)");
        }

        return new ArtifactPushResult(artifact.Name, true, published);
    }

    private static bool HasMatchingFiles(string repositoryRoot, string pattern)
    {
        var normalized = pattern.Replace('/', Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(normalized);
        var searchPattern = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            return false;
        }

        var absoluteDirectory = string.IsNullOrWhiteSpace(directory)
            ? repositoryRoot
            : Path.Combine(repositoryRoot, directory);

        return Directory.Exists(absoluteDirectory)
            && Directory.EnumerateFiles(absoluteDirectory, searchPattern, SearchOption.TopDirectoryOnly).Any();
    }

    private static string ResolveSymbolSource(
        ArtifactConfig artifact,
        string primarySource,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        return FeedAuthResolver.ResolveTargetValue(
                   defaultEnvName: "NUGET_SYMBOL_TARGET_SOURCE",
                   configuredEnvName: GetSetting(artifact.Settings, "symbols.sourceEnv"),
                   configuredValue: GetSetting(artifact.Settings, "symbols.source"),
                   fileEnv: fileEnv)
               ?? primarySource;
    }

    private static string ResolveSymbolPattern(ArtifactConfig artifact, string output, string? packageVersion)
    {
        var configuredPattern = GetSetting(artifact.Settings, "symbols.pattern");
        if (!string.IsNullOrWhiteSpace(configuredPattern))
        {
            return configuredPattern;
        }

        return string.IsNullOrWhiteSpace(packageVersion)
            ? $"{output}/{artifact.Name}.*.snupkg"
            : $"{output}/{artifact.Name}.{packageVersion}.snupkg";
    }

    private static IReadOnlyList<string> ResolvePackageTargets(
        string repositoryRoot,
        string output,
        string artifactName,
        string? packageVersion)
    {
        if (!string.IsNullOrWhiteSpace(packageVersion))
        {
            return [$"{output}/{artifactName}.{packageVersion}.nupkg"];
        }

        var discovered = DiscoverArtifactPackageFiles(repositoryRoot, output, artifactName, ".nupkg", includeSymbols: false);
        if (discovered.Count > 0)
        {
            return discovered;
        }

        // Backward-compatible fallback when no local files can be discovered.
        return [$"{output}/{artifactName}.*.nupkg"];
    }

    private static IReadOnlyList<string> DiscoverArtifactPackageFiles(
        string repositoryRoot,
        string output,
        string artifactName,
        string extension,
        bool includeSymbols)
    {
        var outputDirectory = Path.Combine(repositoryRoot, output.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(outputDirectory))
        {
            return Array.Empty<string>();
        }

        var artifactPrefix = artifactName + ".";
        var matches = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(outputDirectory, "*" + extension, SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(filePath);
            if (!fileName.StartsWith(artifactPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = fileName[artifactPrefix.Length..];
            if (suffix.Length == 0)
            {
                continue;
            }

            if (!includeSymbols && suffix.StartsWith("symbols.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!char.IsDigit(suffix[0]))
            {
                continue;
            }

            var relative = Path.GetRelativePath(repositoryRoot, filePath).Replace(Path.DirectorySeparatorChar, '/');
            matches.Add(relative);
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return matches;
    }

    private static FeedAuthResolution ResolveSymbolAuth(
        ArtifactConfig artifact,
        string source,
        string? configuredApiKeyEnv,
        IReadOnlyDictionary<string, string> fileEnv,
        string? primarySecret)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var secret = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "NUGET_SYMBOL_API_KEY",
            configuredEnvName: configuredApiKeyEnv,
            fileEnv: fileEnv,
            "NUGET_API_KEY",
            "NUGET_AUTH_TOKEN");

        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = primarySecret;
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            var githubFallback = FeedAuthResolver.ResolveGitHubPackagesTokenAuth(
                source,
                fileEnv,
                "nuget.pkg.github.com",
                ciInferenceEnabled);
            if (githubFallback.HasCredentials)
            {
                return githubFallback;
            }

            var gitLabFallback = FeedAuthResolver.ResolveGitLabPackageTokenAuth(
                source,
                fileEnv,
                username: null,
                ciInferenceEnabled: ciInferenceEnabled);
            if (gitLabFallback.HasCredentials)
            {
                return gitLabFallback;
            }

            var azureFallback = FeedAuthResolver.ResolveAzureArtifactsTokenAuth(
                source,
                fileEnv,
                username: null,
                ciInferenceEnabled: ciInferenceEnabled);
            if (azureFallback.HasCredentials)
            {
                return azureFallback;
            }

            return new FeedAuthResolution(false, null, null, source, null, "none");
        }

        return new FeedAuthResolution(true, null, secret, source, null, "env");
    }

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);

        return (process.ExitCode, stdout + stderr);
    }

    private static string ResolveSource(
        ArtifactConfig artifact,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        return FeedAuthResolver.ResolveTargetValue(
                   defaultEnvName: "NUGET_TARGET_SOURCE",
                   configuredEnvName: GetSetting(artifact.Settings, "target.sourceEnv"),
                   configuredValue: GetSetting(artifact.Settings, "target.source"),
                   fileEnv: fileEnv)
               ?? "https://api.nuget.org/v3/index.json";
    }

    private static string? GetSetting(ArtifactConfig artifact, string key) =>
        GetSetting(artifact.Settings, key);

    private static string? GetSetting(IReadOnlyDictionary<string, JsonElement> settings, string path)
    {
        if (!TryGetSettingValue(settings, out var value, path))
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

    /// <summary>
    /// Resolves NuGet push credentials. Order: target.apiKeyEnv (or NUGET_API_KEY) /
    /// NUGET_AUTH_TOKEN → GITHUB_TOKEN for nuget.pkg.github.com → SYSTEM_ACCESSTOKEN
    /// for Azure Artifacts.
    /// </summary>
    private static FeedAuthResolution ResolveAuth(
        ArtifactConfig artifact,
        string source,
        string? configuredApiKeyEnv,
        IReadOnlyDictionary<string, string> fileEnv)
    {
        var ciInferenceEnabled = FeedAuthResolver.IsArtifactCiInferenceEnabled(artifact.Settings);
        var secret = FeedAuthResolver.ResolveSecret(
            defaultEnvName: "NUGET_API_KEY",
            configuredEnvName: configuredApiKeyEnv,
            fileEnv: fileEnv,
            "NUGET_AUTH_TOKEN");

        if (string.IsNullOrWhiteSpace(secret))
        {
            var githubFallback = FeedAuthResolver.ResolveGitHubPackagesTokenAuth(
                source,
                fileEnv,
                "nuget.pkg.github.com",
                ciInferenceEnabled);
            if (githubFallback.HasCredentials)
            {
                return githubFallback;
            }

            var gitLabFallback = FeedAuthResolver.ResolveGitLabPackageTokenAuth(
                source,
                fileEnv,
                username: null,
                ciInferenceEnabled: ciInferenceEnabled);
            if (gitLabFallback.HasCredentials)
            {
                return gitLabFallback;
            }

            var azureFallback = FeedAuthResolver.ResolveAzureArtifactsTokenAuth(
                source,
                fileEnv,
                username: null,
                ciInferenceEnabled: ciInferenceEnabled);
            if (azureFallback.HasCredentials)
            {
                return azureFallback;
            }

            return new FeedAuthResolution(false, null, null, source, null, "none");
        }

        return new FeedAuthResolution(true, null, secret, source, null, "env");
    }
}
