# Helm OCI Artifact Provider (`type: "helm-oci"`)

## Command mapping

- Build: `helm package`
- Push: `helm push <chart.tgz> oci://...`
- Optional pre-push auth: `helm registry login`

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `chart` | `string` | Chart name for archive resolution (default artifact name). |
| `chartPath` | `string` | Chart root path (default `chart`). |
| `output` | `string` | Package output directory (default `artifacts/charts`). |
| `target.oci` | `string` | Full destination (`oci://registry/repository`). |
| `target.ociEnv` | `string` | Env var name for OCI destination (default env key `HELM_OCI_TARGET`). |
| `target.registry` | `string` | Registry host used with `target.repository` if `target.oci` not set. |
| `target.registryEnv` | `string` | Env var name for registry host (default env key `HELM_OCI_TARGET_REGISTRY`). |
| `target.repository` | `string` | Repository path used with `target.registry` if `target.oci` not set. |
| `target.repositoryEnv` | `string` | Env var name for repository path (default env key `HELM_OCI_TARGET_REPOSITORY`). |
| `target.loginRegistry` | `string` | Optional override for `helm registry login`. |
| `target.loginRegistryEnv` | `string` | Env var name for login registry override (default env key `HELM_OCI_LOGIN_REGISTRY`). |
| `target.usernameEnv` | `string` | Env var name for username (default env key `HELM_REGISTRY_USERNAME`). |
| `target.passwordEnv` | `string` | Env var name for password/token (default env key `HELM_REGISTRY_PASSWORD`). |
| `ciInference` | `boolean` | Enable CI-based destination/auth inference (default `true`; alias `target.ciInference`). |
| `useDocker` | `boolean` | Docker fallback toggle (default `true`). |
| `dockerImage` | `string` | Fallback image override (default `alpine/helm:3.17.3`). |

## Destination resolution

1. Use `settings.target.oci` when present (normalized to `oci://...`)
2. Else compose from resolved `settings.target.registry` + resolved `settings.target.repository`
3. If registry is resolved but repository is not:

- `ghcr.io` + GitHub Actions (`GITHUB_ACTIONS=true`) + `GITHUB_REPOSITORY` -> infer repository as `<owner>/<repo>`
- GitLab registry (`CI_REGISTRY`) + `CI_PROJECT_PATH` -> infer repository as `<CI_PROJECT_PATH>`

1. If still unresolved, infer registry from CI context:

- GitHub Actions (`GITHUB_ACTIONS=true`) -> `ghcr.io`
- GitLab CI (`GITLAB_CI=true`) -> `CI_REGISTRY`

1. If registry is now known but repository is still unknown, infer repository using step 3 rules

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI-based destination/auth inference for this artifact

Resolved values check environment first using the env var names in `settings.target.*Env` (or provider defaults).

## Auth resolution

Credential resolution order:

1. Env values from `settings.target.usernameEnv` + `settings.target.passwordEnv` (defaults `HELM_REGISTRY_USERNAME` + `HELM_REGISTRY_PASSWORD`)
2. `GITHUB_ACTOR` + `GITHUB_TOKEN` for `ghcr.io` when explicit creds are absent
3. `CI_REGISTRY_USER` + `CI_REGISTRY_PASSWORD` (or `CI_JOB_TOKEN`) for GitLab registry targets when explicit creds are absent

Registry endpoint resolution:

1. `HELM_REGISTRY` (legacy compatibility fallback)
2. Env value from `settings.target.loginRegistryEnv` (or `HELM_OCI_LOGIN_REGISTRY`)
3. `settings.target.loginRegistry`
4. Resolved destination registry

If Rexo resolves a container registry target (`ghcr.io`, GitLab Container Registry, or `*.azurecr.io`) but no credentials are found, it continues and prints a warning before push.

## Dependency handling

Rexo packages charts directly by default.

- If `helm package` fails because dependencies declared in `Chart.yaml` are missing from `charts/`, Rexo runs `helm dependency update <chartPath>` and retries packaging once.
- This keeps the fast path unchanged for charts that already vendor dependencies, while still handling charts that expect Helm to materialize them during packaging.
- `helm dependency update` is used for the retry because it can resolve repository-backed dependencies without requiring prior `helm repo add ...` state.

## Example

```json
{
  "type": "helm-oci",
  "name": "my-chart",
  "settings": {
    "chartPath": "deploy/charts/my-chart",
    "target": {
      "registry": "ghcr.io",
      "repository": "org/charts",
      "registryEnv": "HELM_OCI_TARGET_REGISTRY",
      "repositoryEnv": "HELM_OCI_TARGET_REPOSITORY",
      "usernameEnv": "HELM_REGISTRY_USERNAME",
      "passwordEnv": "HELM_REGISTRY_PASSWORD"
    }
  }
}
```

## Minimal CI examples

GitHub Actions with GHCR repository inference:

```json
{
  "type": "helm-oci",
  "name": "orders",
  "settings": {
    "chartPath": "deploy/charts/orders",
    "target": {
      "registry": "ghcr.io"
    }
  }
}
```

GitLab CI with destination inferred from CI env vars:

```json
{
  "type": "helm-oci",
  "name": "orders",
  "settings": {
    "chartPath": "deploy/charts/orders"
  }
}
```
