# Embedded Policy: github-release

`embedded:github-release` provides lightweight GitHub release automation for repos using the
standard lifecycle policy.

This policy ships a single command override:

- `post-push` — creates or updates a Git tag for the resolved version and publishes a GitHub
  release using the GitHub CLI (`gh`).

## Usage

Stack this policy on top of `embedded:standard`:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "extends": [
    "embedded:standard",
    "embedded:github-release"
  ],
  "artifacts": [
    {
      "type": "docker",
      "name": "app",
      "settings": {
        "image": "ghcr.io/example/app"
      }
    }
  ]
}
```

Then run:

```bash
rx release --push
```

## Behavior

- `git` is run inside a container image, so host git is not required.
- `gh` is run inside a container image, so host `gh` is also not required.
- Tag existence lookup and version resolution still run during `--dry-run`.
- Tag creation, release publication, and asset upload are logged instead of applied when `--dry-run` is enabled.

Because `embedded:standard` defines the `release` workflow and `embedded:github-release`
provides `post-push`, the GitHub release step runs automatically when `--push` is requested.

If you do not use `embedded:standard`, the policy can still be used directly via `rx post-push`
or composed into a custom command sequence.

## Requirements

- `git` is run inside a container image, so host git is not required.
- `gh` (GitHub CLI) is run inside a container image, so host `gh` is also not required.
- The container runtime must be available when the `gh` or git step runs.
- A GitHub authenticated environment is still required, for example `GITHUB_TOKEN` or an authenticated `gh` session.
- The configured git remote must be reachable from the current environment.
- `GITHUB_REPOSITORY` is preferred when available, but the policy can infer the repository from an authenticated `gh` session and a configured git remote.

## Options

- `--remote` — Git remote to push the tag to. Defaults to `origin`.
- `--test-report-asset-path` — Optional path to a test results asset to upload to the GitHub release.
- `--coverage-report-asset-path` — Optional path to a coverage report asset to upload to the GitHub release.

## Prerelease behavior

- The release is automatically marked prerelease when the resolved version is a prerelease.
- This is driven from `{{version.isPrerelease}}`, so no extra flag is needed for normal semver prerelease versions.
