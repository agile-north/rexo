# npm Artifact Provider (`type: "npm"`)

## Command mapping

- Build: `npm pack`
- Push: `npm publish`

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `directory` | `string` | Working directory containing `package.json` (default repo root). |
| `target.registry` | `string` | Registry URL (default npmjs). |
| `target.registryEnv` | `string` | Env var name containing registry URL (default env key `NPM_TARGET_REGISTRY`). |
| `target.tokenEnv` | `string` | Env var name containing auth token (default env key `NPM_TOKEN`). |
| `ciInference` | `boolean` | Enable CI token fallback (`GITHUB_TOKEN` / `CI_JOB_TOKEN` / `SYSTEM_ACCESSTOKEN`) (default `true`; alias `target.ciInference`). |
| `access` | `public` \| `restricted` | Publish access level. |
| `tag` | `string` | Dist-tag for publish (default resolved version). |
| `useDocker` | `boolean` | Docker fallback toggle (default `true`). |
| `dockerImage` | `string` | Fallback image override (default `node:lts-alpine`). |
| `extra-build-args` | `string` | Additional args appended to `npm pack`. |
| `extra-push-args` | `string` | Additional args appended to `npm publish`. |

## Auth resolution

Token resolution order:

1. `settings.target.tokenEnv` (or `NPM_TOKEN` when not set)
2. `NODE_AUTH_TOKEN`
3. `GITHUB_TOKEN` when resolved registry targets `npm.pkg.github.com` (shared package CI fallback)
4. `CI_JOB_TOKEN` when resolved registry is an explicit GitLab Package Registry npm endpoint (`.../api/v4/projects/<id>/packages/npm/`)
5. `SYSTEM_ACCESSTOKEN` when resolved registry is an Azure Artifacts endpoint (`pkgs.dev.azure.com` / `.pkgs.visualstudio.com`)

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI token fallback for this artifact

Registry resolution order:

1. Env value from `settings.target.registryEnv` (or `NPM_TARGET_REGISTRY`)
2. `settings.target.registry`
3. npm CLI default registry

The resolved token is passed to the publish process as `NPM_TOKEN`.

## Example

```json
{
  "type": "npm",
  "name": "web-sdk",
  "settings": {
    "directory": "src/sdk",
    "target": {
      "registry": "https://npm.pkg.github.com",
      "registryEnv": "NPM_TARGET_REGISTRY",
      "tokenEnv": "NPM_TOKEN"
    },
    "access": "restricted",
    "tag": "next",
    "extra-push-args": "--provenance"
  }
}
```
