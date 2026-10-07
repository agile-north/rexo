# Rexo CLI (`rx`)

Rexo is a config-driven repository automation CLI. It runs the same workflow model locally and in CI from a repository config file.

## Install

```bash
dotnet tool install --global Rexo.Cli
```

To update:

```bash
dotnet tool update --global Rexo.Cli
```

## Run Without Installing (dnx)

If you prefer not to install globally, run Rexo directly from NuGet:

```bash
dotnet dnx Rexo.Cli -- --help
dotnet dnx Rexo.Cli -- init --yes --stack auto
```

Use `--` after `Rexo.Cli` so remaining arguments are passed to `rx`.

## Quick Start

1. Run `rx init` (defaults to `.rexo/rexo.yaml`; pass `--format json` for `.rexo/rexo.json`).
2. Add commands and steps.
3. Run commands with `rx`.

Example:

```bash
rx init
rx list
```

Non-interactive example with policy:

```bash
rx init --yes --stack auto --with-policy --policy dotnet
```

By default, `init` uses a local schema file to avoid editor trust prompts:

```bash
rx init --yes --schema-source local
```

Use remote schema URL instead:

```bash
rx init --yes --schema-source remote
```

Write JSON instead of the default YAML:

```bash
rx init --yes --format json
```

Non-interactive example that also downloads AI instructions into the repo:

```bash
rx init --yes --with-instructions
```

Custom destination for instructions file:

```bash
rx init --yes --with-instructions --instructions-path .github/instructions/rexo.instructions.md
```

### Docker artifact scaffolding

When `rx init` detects a `Dockerfile` in the repository, it automatically scaffolds a minimal docker artifact in the generated config (defaulting to **yes** in interactive mode, and opting in automatically in non-interactive mode).

To opt out in non-interactive mode:

```bash
rx init --yes --without-docker-artifact
```

To force docker artifact scaffolding even when no Dockerfile is detected:

```bash
rx init --yes --with-docker-artifact
```

> `--with-docker-artifact` and `--without-docker-artifact` cannot be combined — passing both is an error.

Minimal example (`.rexo/rexo.yaml`):

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/agile-north/rexo/__REXO_SCHEMA_TAG__/rexo.schema.json
$schema: https://raw.githubusercontent.com/agile-north/rexo/__REXO_SCHEMA_TAG__/rexo.schema.json
schemaVersion: "1.0"
name: my-repo
commands:
  build:
    description: Build the project
    steps:
      - run: dotnet build -c Release
aliases: {}
```

The same config in JSON (`.rexo/rexo.json`):

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/__REXO_SCHEMA_TAG__/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "commands": {
    "build": {
      "description": "Build the project",
      "options": {},
      "steps": [
        { "run": "dotnet build -c Release" }
      ]
    }
  },
  "aliases": {}
}
```

Run it:

```bash
rx build
```

## Common Commands

```bash
rx list
rx explain build
rx config sources
rx config resolved --json
rx config resolved --provenance
rx config explain versioning.provider
rx doctor
rx check
rx graph release --format mermaid
rx completion powershell
rx update
rx restore
```

Lifecycle commands are opt-in: initialize with `--with-policy` or extend
`embedded:standard` in your config.

```bash
rx plan
rx release          # Standard policy: verify, build and tag without pushing
rx release --push   # Request publication, subject to runtime push policy
```

Repositories may override the release command and configure publication gates.
The Rexo repository uses one CI job calling plain `release`; its config controls
push eligibility. This is a repository-specific override, not the standard policy default.

`rx check` is non-publishing readiness diagnostics; `rx plan --push` (when the selected policy
provides it) reports release-flow push eligibility without publishing. `rx update` refreshes remote
policy content hashes in `.rexo/rexo.lock.yaml`; `rx restore` verifies the lock without updating it.

For local promotion, configure an `environments.<name>.path`, build with `--json-file`, then run
`rx promote <run-manifest.json> <environment>`. This currently supports exactly one locally
available file artifact with a verified SHA-256. It does not deploy remotely or promote mutable
registry tags. `--dry-run` validates the promotion request without writing promotion objects.

## Configuration Discovery

Rexo looks for configuration in this order:

1. `.rexo/rexo.yaml`, `.rexo/rexo.yml`, `.rexo/rexo.json` (default location)
2. `rexo.yaml`, `rexo.yml`, `rexo.json` (repo root)
3. Backward-compatible fallback: `repo.yaml|yml|json` (`.repo/` and root)

Policy files are discovered in `.rexo/`, root, and legacy `.repo/` locations (YAML before JSON).
If several candidates exist, the first match wins and Rexo warns about the ignored files.

YAML and JSON are validated against the same schema. Add a
`# yaml-language-server: $schema=...` modeline (written by `rx init`) for editor intellisense.

## Notes

- Use `rx` directly to run configured commands.
- Use `rx run <command>` if you want explicit run semantics.
- Use `--json` or `--json-file <path>` for machine-readable output.
- Full JSON and manifest field semantics: [Output Contract](https://github.com/agile-north/rexo/blob/__REXO_DOC_TAG__/docs/configuration/output-contract.md).

## Documentation

| Resource | Link |
| --- | --- |
| GitHub repository | <https://github.com/agile-north/rexo> |
| Configuration reference | <https://github.com/agile-north/rexo/blob/__REXO_DOC_TAG__/docs/configuration/README.md> |
| Lifecycle policies | <https://github.com/agile-north/rexo/blob/__REXO_DOC_TAG__/docs/embedded/README.md> |
| Artifact providers | <https://github.com/agile-north/rexo/blob/__REXO_DOC_TAG__/docs/artifacts/README.md> |
| Rexo config schema | <https://raw.githubusercontent.com/agile-north/rexo/__REXO_SCHEMA_TAG__/rexo.schema.json> |
| Policy schema | <https://raw.githubusercontent.com/agile-north/rexo/__REXO_SCHEMA_TAG__/policy.schema.json> |
| Architecture overview | <https://github.com/agile-north/rexo/blob/__REXO_DOC_TAG__/docs/ARCHITECTURE.md> |
| Contributing / dev guide | <https://github.com/agile-north/rexo/blob/__REXO_DOC_TAG__/docs/DEVELOPMENT.md> |

> **AI assistant tip**: `rx init --with-instructions` downloads
> [`rexo.instructions.md`](https://github.com/agile-north/rexo/blob/__REXO_DOC_TAG__/docs/rexo.instructions.md)
> into `.github/instructions/rexo.instructions.md` by default. Use `--instructions-path` to choose
> another repo-relative destination.
