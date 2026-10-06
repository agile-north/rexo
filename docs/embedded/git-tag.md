# Embedded Policy: git-tag

`embedded:git-tag` provides reusable git tag creation for versioned repositories.

This policy ships a single command:

- `post-push` — resolves the version, checks whether the tag already exists, and creates/pushes the tag only when needed.

## Usage

Stack this policy on top of `embedded:standard`:

```yaml
extends:
  - embedded:standard
  - embedded:git-tag
vars:
  gitTag:
    prefix: v          # tags become v1.2.3
```

Then run:

```bash
rx release --push
```

## vars.gitTag reference

| Var | Default | Description |
| --- | --- | --- |
| `prefix` | `""` | Prefix prepended to the tag name (e.g. `v` → `v1.2.3`). |
| `remote` | `origin` | Remote to fetch/push tags when `--remote` is not given. |
| `container` | `git` | Container registry name for the git steps; set to `none` (or `""`) to use host git. |

The policy registers a `git` container (`docker.io/alpine/git:latest`, working directory `/work`).
Override it under `containers.git` in your config to change the image.

## Behavior

- The tag is `{{vars.gitTag.prefix}}{{version.SemVer}}`.
- The tag is created only if it does not already exist on the remote.
- `--force` deletes and recreates the tag.
- `--dry-run` skips every git step, so no remote mutation occurs.

## Lifecycle usage

- With `embedded:standard`, `post-push` runs automatically as part of `rx release --push`.
- Without it, run `rx post-push` directly or compose it into your own command flow.

When stacked with other `post-push` policies, this template uses `merge: append` so the
tagging steps stay in the composed release-hook chain.

## Options

- `--remote` — Git remote to push the tag to. Defaults to `vars.gitTag.remote` (`origin`).
- `--force` — Recreate the tag if it already exists. Defaults to `false`.
