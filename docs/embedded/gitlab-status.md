# Embedded Policy: gitlab-status

`embedded:gitlab-status` publishes a GitLab commit status for the resolved commit after push.

This policy ships a single command override:

- `post-push` — resolves the version and publishes a commit status using the GitLab CLI.

## Usage

Stack this policy on top of `embedded:standard`:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "extends": [
    "embedded:standard",
    "embedded:gitlab-status"
  ]
}
```

Then run:

```bash
rx release --push
```

## Behavior

- Publishes a GitLab commit status for the resolved commit SHA.
- Uses the `glab` CLI inside a container image (`registry.gitlab.com/gitlab-org/cli/cli:latest`).
- Does not require host GitLab CLI installation.
- When `--dry-run` is enabled, the policy still resolves version and evaluates the publish path, but it outputs a dry-run message instead of applying remote changes.

## Lifecycle usage

- If you use `embedded:standard`, `post-push` runs automatically as part of `rx release --push`.
- If you do not use `embedded:standard`, you can still run `rx post-push` directly or define your own command composition.

## Options

- `--state` — Commit status state. Defaults to `success`.
- `--context` — Status context. Defaults to `rexo/build`.
- `--description` — Status description. Defaults to `Rexo build status`.

## Requirements

- `CI_PROJECT_ID` should be available in the environment when running in GitLab CI.
- When running locally, the policy can infer the project ID from a configured GitLab remote using `glab repo view --json id -q .id`.
- `glab` authentication must be available inside the container, for example via `GITLAB_TOKEN`.
- In GitLab CI, `CI_JOB_TOKEN` may also be used as a fallback credential for `glab` if configured.
