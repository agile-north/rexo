# PyPI Artifact Provider (`type: "pypi"`)

## Command mapping

- Build: `python -m build`
- Push: `python -m twine upload`

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `directory` | `string` | Working directory (default repo root). |
| `target.repositoryUrl` | `string` | Twine repository URL. |
| `target.repositoryUrlEnv` | `string` | Env var name containing repository URL (default env key `PYPI_TARGET_REPOSITORY_URL`). |
| `target.apiTokenEnv` | `string` | Env var name containing API token (default env key `TWINE_API_TOKEN`). |
| `target.usernameEnv` | `string` | Env var name containing username (default env key `TWINE_USERNAME`). |
| `target.passwordEnv` | `string` | Env var name containing password (default env key `TWINE_PASSWORD`). |
| `ciInference` | `boolean` | Enable CI token fallback (`SYSTEM_ACCESSTOKEN` for Azure Artifacts URLs) (default `true`; alias `target.ciInference`). |
| `dist-dir` | `string` | Upload pattern (default `dist/*`). |
| `useDocker` | `boolean` | Docker fallback toggle (default `true`). |
| `dockerImage` | `string` | Fallback image override (default `python:3-slim`). |
| `extra-build-args` | `string` | Additional args appended to `python -m build`. |
| `extra-push-args` | `string` | Additional args appended to `python -m twine upload`. |

## Runtime notes

- Host execution attempts `python`, then `python3`, then Docker fallback.
- The default image may not include `build`/`twine`; use a custom image if needed.

## Auth resolution

Credential resolution order:

1. Env value from `settings.target.apiTokenEnv` (or `TWINE_API_TOKEN`) (username becomes `__token__`)
2. Env value from `settings.target.usernameEnv` + `settings.target.passwordEnv` (defaults `TWINE_USERNAME` + `TWINE_PASSWORD`)
3. `SYSTEM_ACCESSTOKEN` for Azure Artifacts URLs (`pkgs.dev.azure.com` / `.pkgs.visualstudio.com`) via shared package CI fallback

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI token fallback for this artifact

Credentials are injected into the push process as `TWINE_USERNAME` and `TWINE_PASSWORD`.

Repository URL resolution order:

1. Env value from `settings.target.repositoryUrlEnv` (or `PYPI_TARGET_REPOSITORY_URL`)
2. `settings.target.repositoryUrl`
3. Twine default repository behavior

## Example

```json
{
  "type": "pypi",
  "name": "data-utils",
  "settings": {
    "directory": "src/python",
    "target": {
      "repositoryUrl": "https://upload.pypi.org/legacy/",
      "repositoryUrlEnv": "PYPI_TARGET_REPOSITORY_URL",
      "apiTokenEnv": "TWINE_API_TOKEN",
      "usernameEnv": "TWINE_USERNAME",
      "passwordEnv": "TWINE_PASSWORD"
    },
    "extra-build-args": "--sdist --wheel"
  }
}
```
