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

## Notes

- No `useDocker` or `dockerImage` settings: this provider is already Docker-based.
- If `target.registry` is omitted, provider may still resolve a registry from CI context (`CI_REGISTRY` or GitHub Actions `ghcr.io`) when CI inference is enabled.

## Example

```json
{
  "type": "docker-compose",
  "name": "stack",
  "settings": {
    "file": "deploy/compose.yml",
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
