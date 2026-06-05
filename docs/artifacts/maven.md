# Maven Artifact Provider (`type: "maven"`)

## Command mapping

- Build: `mvn package -DskipTests`
- Push: `mvn deploy -DskipTests`

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `project` | `string` | Path to `pom.xml` (default root `pom.xml`). |
| `profiles` | `string` | Comma-separated Maven profiles (mapped to `-P...`). |
| `target.usernameEnv` | `string` | Env var name containing repository username (default env key `MAVEN_REPO_USERNAME`). |
| `target.passwordEnv` | `string` | Env var name containing repository password/token (default env key `MAVEN_REPO_PASSWORD`). |
| `ciInference` | `boolean` | Enable CI token fallback (`SYSTEM_ACCESSTOKEN`) when explicit credentials are missing (default `true`; alias `target.ciInference`). |
| `useDocker` | `boolean` | Docker fallback toggle (default `true`). |
| `dockerImage` | `string` | Fallback image override (default `maven:3-eclipse-temurin-21`). |
| `extra-build-args` | `string` | Additional args appended to `mvn package`. |
| `extra-push-args` | `string` | Additional args appended to `mvn deploy`. |

## Auth resolution

Credential resolution order:

1. Env values from `settings.target.usernameEnv` + `settings.target.passwordEnv` (defaults `MAVEN_REPO_USERNAME` + `MAVEN_REPO_PASSWORD`)
2. `SYSTEM_ACCESSTOKEN` (shared package CI fallback; username is `VssSessionToken`)

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI token fallback for this artifact

Credentials are passed as process environment variables; reference them from `settings.xml` using `${env.MAVEN_REPO_USERNAME}` and `${env.MAVEN_REPO_PASSWORD}`.

## Example

```json
{
  "type": "maven",
  "name": "service-java",
  "settings": {
    "project": "services/catalog/pom.xml",
    "profiles": "release",
    "target": {
      "usernameEnv": "MAVEN_REPO_USERNAME",
      "passwordEnv": "MAVEN_REPO_PASSWORD"
    },
    "extra-push-args": "-DaltDeploymentRepository=internal::default::https://repo.example.com/maven2"
  }
}
```
