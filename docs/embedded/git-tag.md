# Embedded Policy: git-tag

`embedded:git-tag` provides reusable git tag creation for versioned repositories.

This policy ships a single command override:

- `post-push` — resolves the version, checks whether the tag already exists, and creates/pushes the tag only when needed.

## Usage

Stack this policy on top of `embedded:standard`:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "extends": [
    "embedded:standard",
    "embedded:git-tag"
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

- `git tag` is created only if the tag does not already exist.
- `--force` can be used to recreate the tag.
- The tag step runs inside a container image (`docker.io/alpine/git:latest`) so host git is not required.
- When `--dry-run` is enabled, the tag creation steps are skipped so no remote mutation occurs.

## Lifecycle usage

- If you use `embedded:standard`, `post-push` runs automatically as part of `rx release --push`.
- If you do not use `embedded:standard`, you can still use this policy directly by running `rx post-push` or by composing it into your own command flow.

## Options

- `--remote` — Git remote to push the tag to. Defaults to `origin`.
- `--force` — Recreate the tag if it already exists. Defaults to `false`.
