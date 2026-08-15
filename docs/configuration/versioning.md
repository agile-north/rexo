# Versioning Configuration

Configure how Rexo resolves semantic versions and selects version providers.

---

## `versioning`

```jsonc
"versioning": {
  "provider": "auto",   // auto | fixed | env | gitversion | minver | nbgv | git
  "fallback": "0.1.0-local",
  "settings": {
    "variable": "MY_VERSION_VAR",     // for env provider
    "tagPrefix": "v",                 // for minver provider
    "minimumMajorMinor": "1.0",       // for minver provider
    "useDocker": "true",              // gitversion/nbgv/minver — enable Docker fallback (default true)
    "dockerImage": "gittools/gitversion:6.0.0"  // override the Docker image for this provider; template expressions are rendered first
  }
}
```

## Version Providers

| Provider | Tool | Notes |
| --- | --- | --- |
| `auto` | — | **Default.** Detects provider by config file evidence (see below). |
| `fixed` | — | Returns the configured `fallback` version string |
| `env` | — | Reads `settings.variable` env var; falls back to `fallback` |
| `git` | `git describe` | Resolves from the most recent SemVer git tag |
| `gitversion` | `gitversion /output json` | Parses SemVer2 fields from JSON output; Docker fallback via `gittools/gitversion:6.0.0` |
| `minver` | `dotnet minver` | Single-line SemVer output; Docker fallback via .NET SDK image |
| `nbgv` | `nbgv get-version -f json` | Parses `SemVer2`, `Version`, `GitCommitId`; Docker fallback via .NET SDK image |

---

## `auto` Detection Order

When `provider` is `auto`, Rexo scans the repository root for versioning config file evidence in this order:

1. `version.json` or `nbgv.json` present → uses **nbgv**
2. `GitVersion.yml` or `GitVersion.yaml` present → uses **gitversion**
3. `.minverrc` present → uses **minver**
4. `.git` directory present → uses **git** (tag-based)
5. None of the above → uses **fixed** with the configured `fallback` version

---

## Docker Fallback

`gitversion`, `nbgv`, and `minver` all support a Docker fallback for environments where
the CLI tool is not installed. The fallback is **enabled by default** and is tried after
the host tool fails (tool not found, non-zero exit, or empty output).

| Provider | Default image | Container command |
| --- | --- | --- |
| `gitversion` | `gittools/gitversion:6.0.0` | `docker run --rm -v <repo>:/repo -w /repo <image> /output json` |
| `nbgv` | `mcr.microsoft.com/dotnet/sdk:latest` | `dotnet tool restore && dotnet nbgv get-version --format json` |
| `minver` | `mcr.microsoft.com/dotnet/sdk:latest` | `dotnet tool restore && dotnet minver [args]` |

For `nbgv` and `minver` Docker, the tool must be present in the repo's `.config/dotnet-tools.json` manifest so `dotnet tool restore` can install it inside the container.

Disable Docker for a specific provider with:

```jsonc
{ "versioning": { "provider": "nbgv", "settings": { "useDocker": "false" } } }
```

Override the Docker image:

```jsonc
{ "versioning": { "provider": "gitversion", "settings": { "dockerImage": "gittools/gitversion:{{args.channel}}" } } }
```
