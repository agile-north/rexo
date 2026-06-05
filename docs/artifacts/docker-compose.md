# Docker Compose Artifact Provider (`type: "docker-compose"`)

## Command mapping

- Build: `docker compose build`
- Push: `docker compose push`

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `file` | `string` | Compose file path (default `docker-compose.yml`). |
| `project-name` | `string` | Compose project name (`-p`). |
| `services` | `string` | Space-separated service names (default all). |
| `target.registry` | `string` | Optional registry for pre-push `docker login`. |
| `target.registryEnv` | `string` | Env var name containing target registry (default env key `DOCKER_COMPOSE_TARGET_REGISTRY`). |
| `target.loginRegistryEnv` | `string` | Env var name overriding login registry endpoint (default env key `DOCKER_LOGIN_REGISTRY`). |
| `target.usernameEnv` | `string` | Env var name containing Docker username (default env key `DOCKER_LOGIN_USERNAME`). |
| `target.passwordEnv` | `string` | Env var name containing Docker password/token (default env key `DOCKER_LOGIN_PASSWORD`). |
| `extra-build-args` | `string` | Additional args appended to `docker compose build`. |
| `extra-push-args` | `string` | Additional args appended to `docker compose push`. |

## Auth resolution

When resolved target registry is set, provider performs `docker login` before push.

Credential resolution uses shared Docker login rules:

1. Env values from `settings.target.usernameEnv` + `settings.target.passwordEnv` (defaults `DOCKER_LOGIN_USERNAME` + `DOCKER_LOGIN_PASSWORD`)
2. `GITHUB_ACTOR` + `GITHUB_TOKEN` for `ghcr.io` when explicit creds are absent
3. `CI_REGISTRY_USER` + `CI_REGISTRY_PASSWORD` (or `CI_JOB_TOKEN`) for GitLab Container Registry targets when explicit creds are absent

Registry host resolution for pre-push `docker login`:

1. `DOCKER_COMPOSE_TARGET_REGISTRY` or configured `target.registry`
2. `CI_REGISTRY` in GitLab CI (cloud or self-hosted)
3. `ghcr.io` in GitHub Actions (`GITHUB_ACTIONS=true`)

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI-based registry/auth inference for this artifact

If Rexo resolves a container registry target (`ghcr.io`, GitLab Container Registry, or `*.azurecr.io`) but no credentials are found, it continues and prints a warning before push.

For Azure DevOps + ACR specifically, keep Docker auth explicit (for example `DOCKER_LOGIN_USERNAME` / `DOCKER_LOGIN_PASSWORD`) or perform a prior `docker login` task in the pipeline. Rexo does not currently infer ACR credentials from `SYSTEM_ACCESSTOKEN`.

Registry resolution order:

1. Env value from `settings.target.registryEnv` (or `DOCKER_COMPOSE_TARGET_REGISTRY`)
2. `settings.target.registry`

## Behavior

- Build: `docker compose build`.
- Push: `docker compose push`.
- `settings.file` controls the compose file path.
- `settings.project-name` maps to `docker compose -p`.
- `settings.services` limits build/push to the named services.

### What gets pushed

- The provider pushes the images referenced by the selected Compose services.
- The actual pushed registry and repository are taken from the services' `image:` values in the compose file.
- The provider does not rewrite image names or implement Docker tag strategy itself.
- It reports a synthetic push result of `artifact.Name:<version|latest>`, not the real Compose image references.

### When to use `docker-compose`

- your repository is a Docker Compose stack
- you want Compose to own build/push ordering for multiple services
- you want to build/push several services together from a single artifact definition

### When not to use `docker-compose`

- you need explicit per-image `image`, `dockerfile`, or `context` control
- you need version-based tag strategy and push gating
- you want Rexo to manage artifact metadata for individual images

## Example

Compose file:

```yaml
version: "3.9"

services:
  api:
    build: ./api
    image: ghcr.io/agile-north/rexo/api:latest

  worker:
    build: ./worker
    image: ghcr.io/agile-north/rexo/worker:latest
```

Artifact config:

```json
{
  "type": "docker-compose",
  "name": "stack",
  "settings": {
    "file": "docker-compose.yml",
    "project-name": "my-stack",
    "services": "api worker",
    "target": {
      "registry": "ghcr.io",
      "registryEnv": "DOCKER_COMPOSE_TARGET_REGISTRY",
      "usernameEnv": "DOCKER_LOGIN_USERNAME",
      "passwordEnv": "DOCKER_LOGIN_PASSWORD"
    },
    "extra-push-args": "--quiet"
  }
}
```

With that config, `docker compose push` will attempt to push:

- `ghcr.io/agile-north/rexo/api:latest`
- `ghcr.io/agile-north/rexo/worker:latest`

It does not create or push a separate “stack” artifact bundle. The provider merely wraps Docker Compose build/push behavior and reports a single synthetic artifact reference.
