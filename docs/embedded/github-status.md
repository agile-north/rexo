# Embedded Policy: github-status

`embedded:github-status` publishes a GitHub commit status for the resolved commit after push.

This policy ships a single command override:

- `post-push` — resolves the version and publishes a commit status using the GitHub CLI.

## Usage

Stack this policy on top of `embedded:standard`:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "extends": [
    "embedded:standard",
    "embedded:github-status"
  ]
}
```

Then run:

```bash
rx release --push
```

## Behavior

- Publishes either a GitHub commit status or a GitHub check run for the resolved commit SHA.
- Uses the `gh` CLI inside a container image (`ghcr.io/cli/cli:latest`).
- Does not require host GitHub CLI installation.
- When `--dry-run` is enabled, the policy still resolves version and evaluates the publish path, but it outputs a dry-run message instead of applying remote changes.

## CI-aware mode

- `auto` (default): in GitHub Actions, the policy does not publish an external status/check because the native workflow check is already available.
- `status`: publish a legacy GitHub commit status explicitly.
- `checks`: publish a separate GitHub check run.

## Lifecycle usage

- If you use `embedded:standard`, `post-push` runs automatically as part of `rx release --push`.
- If you do not use `embedded:standard`, you can still run `rx post-push` directly or define your own command composition.

## Options

- `--mode` — Publish mode. Defaults to `auto`.
- `--state` — Commit status state. Defaults to `success`.
- `--context` — Status context. Defaults to `rexo/build`.
- `--description` — Status description. Defaults to `Rexo build status`.
- `--name` — Check run name. Defaults to `Rexo CI`.
- `--title` — Check run title. Defaults to `Rexo check run`.
- `--summary` — Check run summary. Defaults to `Rexo reported test and coverage results.`.
- `--conclusion` — Check run conclusion. Defaults to `success`.

## Requirements

- `GITHUB_TOKEN` should be available in the environment for `gh` authentication.
- `GITHUB_REPOSITORY` is preferred when available, but the policy can infer the repository from an authenticated `gh` session and a configured git remote.
