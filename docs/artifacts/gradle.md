# Gradle Artifact Provider (`type: "gradle"`)

## Command mapping

- Build: `<gradle executable> <tasks>`
- Push: `<gradle executable> publish`

`<gradle executable>` resolves to wrapper (`gradlew` / `gradlew.bat`) when enabled and present; otherwise `gradle`.

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `tasks` | `string` | Build tasks (default `build`). |
| `wrapper` | `boolean` | Use Gradle wrapper when present (default `true`). |
| `directory` | `string` | Working directory containing build files (default repo root). |
| `target.mavenUsernameEnv` | `string` | Env var name containing Maven username (default env key `ORG_GRADLE_PROJECT_mavenUsername`). |
| `target.mavenPasswordEnv` | `string` | Env var name containing Maven password (default env key `ORG_GRADLE_PROJECT_mavenPassword`). |
| `target.publishKeyEnv` | `string` | Env var name containing Gradle publish key (default env key `GRADLE_PUBLISH_KEY`). |
| `target.publishSecretEnv` | `string` | Env var name containing Gradle publish secret (default env key `GRADLE_PUBLISH_SECRET`). |
| `ciInference` | `boolean` | Enable CI token fallback (`SYSTEM_ACCESSTOKEN`) when explicit credentials are missing (default `true`; alias `target.ciInference`). |
| `useDocker` | `boolean` | Docker fallback toggle (default `true`). |
| `dockerImage` | `string` | Fallback image override (default `gradle:8-jdk21`). |
| `extra-build-args` | `string` | Additional args appended to build task invocation. |
| `extra-push-args` | `string` | Additional args appended to `publish`. |

## Auth resolution

Credential resolution order:

1. Env values from `settings.target.mavenUsernameEnv` + `settings.target.mavenPasswordEnv`
2. Env values from `settings.target.publishKeyEnv` + `settings.target.publishSecretEnv`
3. `SYSTEM_ACCESSTOKEN` fallback (username `VssSessionToken`) when explicit credentials are absent

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI token fallback for this artifact

Defaults are `ORG_GRADLE_PROJECT_mavenUsername`, `ORG_GRADLE_PROJECT_mavenPassword`, `GRADLE_PUBLISH_KEY`, and `GRADLE_PUBLISH_SECRET`.

Resolved values are passed as environment variables to Gradle.

## Example

```json
{
  "type": "gradle",
  "name": "platform-java",
  "settings": {
    "directory": "services/platform",
    "tasks": "clean build",
    "target": {
      "mavenUsernameEnv": "ORG_GRADLE_PROJECT_mavenUsername",
      "mavenPasswordEnv": "ORG_GRADLE_PROJECT_mavenPassword",
      "publishKeyEnv": "GRADLE_PUBLISH_KEY",
      "publishSecretEnv": "GRADLE_PUBLISH_SECRET"
    },
    "extra-push-args": "--info"
  }
}
```
