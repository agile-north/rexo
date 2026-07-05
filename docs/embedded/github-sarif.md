# Embedded Policy: github-sarif

`embedded:github-sarif` uploads a GitHub SARIF security scan report for the resolved commit after push.

This policy ships a single command override:

- `post-push` — resolves the version and uploads a SARIF report using the GitHub CLI.

## Usage

Stack this policy on top of `embedded:standard`:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "extends": [
    "embedded:standard",
    "embedded:github-sarif"
  ]
}
```

Then run:

```bash
rx release --push
```

## Behavior

- Uploads a SARIF security scan report for the resolved commit SHA.
- Uses the `gh` CLI inside a container image (`serversideup/github-cli:latest`).
- Does not require host GitHub CLI installation.
- When `--dry-run` is enabled, the SARIF upload step is skipped.

## Lifecycle usage

- If you use `embedded:standard`, `post-push` runs automatically as part of `rx release --push`.
- If you do not use `embedded:standard`, you can still run `rx post-push` directly or define your own command composition.

## Options

- `--sarif-path` — Path to the SARIF file. Defaults to `artifacts/security/security.sarif`.
- `--tool-name` — Tool name reported in GitHub code scanning. Defaults to `Rexo Security`.

## Requirements

- `GITHUB_TOKEN` should be available in the environment for `gh` authentication.
- `GITHUB_REPOSITORY` is preferred when available, but the policy can infer the repository from an authenticated `gh` session and a configured git remote.
- `GITHUB_REF` is used to infer the branch/ref if it is available; otherwise the local git branch is used.
