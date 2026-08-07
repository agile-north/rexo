# NuGet Artifact Provider (`type: "nuget"`)

## Command mapping

- Build: `dotnet pack`
- Push: `dotnet nuget push`

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `project` | `string` | Project path for `dotnet pack`. |
| `output` | `string` | Package output directory. |
| `target.source` | `string` | Feed URL/name for push. |
| `target.sourceEnv` | `string` | Env var name containing feed URL/name (default env key `NUGET_TARGET_SOURCE`). |
| `target.apiKeyEnv` | `string` | Env var name containing API key/token (default env key `NUGET_API_KEY`). |
| `ciInference` | `boolean` | Enable CI token fallback (`GITHUB_TOKEN` / `CI_JOB_TOKEN` / `SYSTEM_ACCESSTOKEN`) (default `true`; alias `target.ciInference`). |
| `symbols.enabled` | `boolean` | When `true`, also pushes matching `.snupkg` symbol packages for this artifact. |
| `symbols.source` | `string` | Symbol feed URL/name. Defaults to `target.source` when omitted. |
| `symbols.sourceEnv` | `string` | Env var containing symbol feed URL/name (default env key `NUGET_SYMBOL_TARGET_SOURCE`). |
| `symbols.apiKeyEnv` | `string` | Env var containing symbol API key/token (default env key `NUGET_SYMBOL_API_KEY`, fallback `NUGET_API_KEY`). |
| `symbols.pattern` | `string` | Glob for symbol packages to push (for example `artifacts/packages/*.symbols.nupkg`). Default is `<output>/<artifact>.<version>.snupkg`. |

## Auth resolution

Credential resolution order:

1. `settings.target.apiKeyEnv` (or `NUGET_API_KEY`)
2. `NUGET_AUTH_TOKEN`
3. `GITHUB_TOKEN` for `nuget.pkg.github.com`
4. `CI_JOB_TOKEN` when `target.source` points to an explicit GitLab Package Registry NuGet endpoint (`.../api/v4/projects/<id>/packages/nuget/...`)
5. `SYSTEM_ACCESSTOKEN` for non-GitHub CI fallback (for example Azure Artifacts)

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI token fallback for this artifact

Source resolution order:

1. Env value from `settings.target.sourceEnv` (or `NUGET_TARGET_SOURCE`)
2. `settings.target.source`
3. Default `https://api.nuget.org/v3/index.json`

Symbol source resolution order (when `symbols.enabled = true`):

1. Env value from `settings.symbols.sourceEnv` (or `NUGET_SYMBOL_TARGET_SOURCE`)
2. `settings.symbols.source`
3. `settings.target.source` (or its env override)

Symbol credential resolution order (when `symbols.enabled = true`):

1. `settings.symbols.apiKeyEnv` (or `NUGET_SYMBOL_API_KEY`)
2. `NUGET_API_KEY`
3. `NUGET_AUTH_TOKEN`
4. Same token already resolved for primary package push
5. `GITHUB_TOKEN` for `nuget.pkg.github.com`
6. `CI_JOB_TOKEN` when symbol source points to an explicit GitLab Package Registry NuGet endpoint
7. `SYSTEM_ACCESSTOKEN` for non-GitHub CI fallback

## Example

```json
{
  "type": "nuget",
  "name": "Rexo.Core",
  "settings": {
    "project": "src/Core/Core.csproj",
    "output": "artifacts/packages",
    "target": {
      "source": "https://api.nuget.org/v3/index.json",
      "sourceEnv": "NUGET_TARGET_SOURCE",
      "apiKeyEnv": "NUGET_API_KEY"
    },
    "symbols": {
      "enabled": true,
      "sourceEnv": "NUGET_SYMBOL_TARGET_SOURCE",
      "apiKeyEnv": "NUGET_SYMBOL_API_KEY"
    }
  }
}
```

If your symbol package names are not `.snupkg` (for example `*.symbols.nupkg`), set a custom pattern:

```json
{
  "type": "nuget",
  "name": "Rexo.Core",
  "settings": {
    "output": "artifacts/packages",
    "symbols": {
      "enabled": true,
      "pattern": "artifacts/packages/*.symbols.nupkg"
    }
  }
}
```
