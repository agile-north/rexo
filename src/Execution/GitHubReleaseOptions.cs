namespace Rexo.Execution;

using System.Text.RegularExpressions;
using Rexo.Core.Models;
using Rexo.Templating;

internal sealed record GitHubReleaseOptions(
    string Execution, string Image, string Hostname, string Repository, string Tag, string Title,
    bool Draft, bool Prerelease, bool GenerateNotes, string NotesFile, string NotesStartTag)
{
    internal const string DefaultImage =
        "docker.io/maniator/gh:v2.103.2@sha256:a76d7b905cc3cb07cd158eff872494bc29f8f64aee8d6e50456307b285dfab97";

    internal static string Read(ExecutionContext context, string name, string fallback = "")
    {
        var renderer = new TemplateRenderer();
        var value = renderer.Render($"{{{{vars.githubRelease.{name}}}}}", context);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    internal static bool ReadBoolean(ExecutionContext context, string name, bool fallback)
    {
        var value = Read(context, name, fallback ? "true" : "false");
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"vars.githubRelease.{name} must be true or false.");
    }

    internal static GitHubReleaseOptions From(ExecutionContext context)
    {
        var (remoteHost, remoteRepository) = ParseRemote(context.RemoteUrl);
        var host = Read(context, "hostname", remoteHost);
        var repository = Read(context, "repository", remoteRepository);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            host.Contains(':', StringComparison.Ordinal) ||
            !Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)) ||
            repository.Split('/').Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException("Cannot determine a GitHub hostname and owner/repository from origin. Set vars.githubRelease.hostname and repository.");
        }

        var execution = Read(context, "execution", "container");
        if (execution is not ("container" or "native"))
        {
            throw new InvalidOperationException("vars.githubRelease.execution must be container or native.");
        }
        var group = context.Options.GetValueOrDefault("artifact-group");
        var grouped = !string.IsNullOrWhiteSpace(group) &&
            ConfigCommandLoader.TryGetOptionBoolean(context.Options, "all-artifact-groups") != true;
        var defaultTag = grouped ? $"{group}/v{context.Version!.SemVer}" : $"v{context.Version!.SemVer}";
        var defaultTitle = grouped ? $"{group} {context.Version!.SemVer}" : context.Version!.SemVer;
        var renderer = new TemplateRenderer();
        var tag = renderer.Render(Read(context, "tagTemplate", defaultTag), context);
        var title = renderer.Render(Read(context, "titleTemplate", defaultTitle), context);
        ValidateTag(tag);
        if (string.IsNullOrWhiteSpace(title) || title.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The GitHub release title must not be empty or contain NUL.");
        }
        var prerelease = Read(context, "prerelease", "auto") switch
        {
            "auto" => context.Version!.IsPreRelease,
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException("vars.githubRelease.prerelease must be auto, true or false."),
        };
        var image = Read(context, "image", DefaultImage);
        if (image.StartsWith('-') || image.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("vars.githubRelease.image must be a container image reference.");
        }
        var notesFile = renderer.Render(Read(context, "notesFile"), context);
        if (notesFile.Length > 0)
        {
            notesFile = Path.GetFullPath(Path.IsPathRooted(notesFile)
                ? notesFile : Path.Join(context.RepositoryRoot, notesFile));
            if (!File.Exists(notesFile))
            {
                throw new FileNotFoundException("GitHub release notes file does not exist.", notesFile);
            }
        }
        var notesStartTag = renderer.Render(Read(context, "notesStartTag"), context);
        if (notesStartTag.Length > 0)
        {
            ValidateTag(notesStartTag);
        }
        var generateNotes = ReadBoolean(context, "generateNotes", true);
        if (notesStartTag.Length > 0 && !generateNotes)
        {
            throw new InvalidOperationException("vars.githubRelease.notesStartTag requires generateNotes: true.");
        }
        return new GitHubReleaseOptions(execution, image, host, repository, tag, title,
            ReadBoolean(context, "draft", false), prerelease, generateNotes,
            notesFile, notesStartTag);
    }

    internal static (string Host, string Repository) ParseRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return ("", "");
        }
        if (!remote.Contains("://", StringComparison.Ordinal))
        {
            var colon = remote.IndexOf(':');
            if (colon <= 0)
            {
                return ("", "");
            }
            remote = $"ssh://{remote[..colon]}/{remote[(colon + 1)..]}";
        }
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http" or "ssh"))
        {
            return ("", "");
        }
        var repository = uri.AbsolutePath.Trim('/');
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repository = repository[..^4];
        }
        return (uri.Host, repository);
    }

    private static void ValidateTag(string tag)
    {
        var invalid = string.IsNullOrWhiteSpace(tag) || tag is "@" ||
            tag.StartsWith('-') || tag.EndsWith('.') || tag.EndsWith('/') ||
            tag.Contains("..", StringComparison.Ordinal) || tag.Contains("@{", StringComparison.Ordinal) ||
            tag.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
                character is '~' or '^' or ':' or '?' or '*' or '[' or '\\') ||
            tag.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') ||
                part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
        if (invalid)
        {
            throw new InvalidOperationException("The GitHub release tag is not a valid Git tag name.");
        }
    }
}
