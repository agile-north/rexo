# Embedded Policies Reference

This content has been reorganized for clarity.

**See the new structure:**

- [Embedded Policies](embedded/README.md) — Overview and index
- [standard policy](embedded/standard.md) — General lifecycle commands
- [dotnet policy](embedded/dotnet.md) — .NET toolchain overlay
- [node policy](embedded/node.md) — Node.js toolchain overlay
- [git-tag policy](embedded/git-tag.md) — Version tag creation

Use the split policy pages as the current detailed reference; this overview may not include
newer command steps, options, or policy details.

## What "Embedded" Means

Rexo ships lifecycle policies as embedded resources in the CLI assembly
(see `Rexo.Policies.EmbeddedPolicyTemplates`).

Current embedded policies:

- `standard`
- `dotnet`
- `node`
- `git-tag`

Embedded policies are never applied implicitly.

Artifact-only configs are minimal by default.
Use `extends` to opt into an embedded lifecycle policy explicitly:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "orders-api",
  "extends": [
    "embedded:standard"
  ],
  "artifacts": [
    {
      "type": "docker",
      "name": "api",
      "settings": {
        "image": "ghcr.io/agile-north/orders-api"
      }
    }
  ]
}
```

Design intent:

- Repo config says what this repo emits (artifacts, versioning, analysis/test details).
- Embedded policy says how this repo behaves (lifecycle command shape).
- `embedded:standard` is the recommended lifecycle baseline when you want policy-provided commands.

## Embedded Policy: standard

Purpose:

- General lifecycle policy for most repositories.
- Works well with artifact-only config when explicitly added via `extends`.

### Commands

#### plan

Description: Validate config and print a build/push plan.

Options:

- `--push` (`bool`, default `false`)

Steps:

1. `builtin:validate`
2. `builtin:resolve-version`
3. `builtin:plan-artifacts` with `with.push = {{options.push}}`

Behavior notes:

- `rx plan` reports artifact build plan and push as "not requested".
- `rx plan --push` reports push eligibility and skip reasons.

#### validate

Description: Validate repository configuration.

Options: none.

Steps:

1. `builtin:validate`

#### version

Description: Resolve repository version.

Options: none.

Steps:

1. `builtin:resolve-version`

#### test

Description: Run configured tests.

Options: none.

Steps:

1. `command:test` (overlay contribution, when present)

#### analyze

Description: Run configured analysis.

Options: none.

Steps:

1. `command:analyze` (overlay contribution, when present)

#### verify

Description: Run validation, tests, and analysis.

Options: none.

Steps:

1. `builtin:validate`
2. `command:pre-verify` (when present)
3. `command:verify` (overlay contribution, when present)
4. `command:test` (when present)
5. `command:analyze` (when present)
6. `command:security` (when present)
7. `command:post-verify` (when present; always runs after hard failures)

Contract note:

- User-facing `verify` command includes validate.
- `verify` now composes command overlays; there is no dedicated core verify builtin.

#### build

Description: Build and tag configured artifacts locally.

Options: none.

Steps:

1. `builtin:validate`
2. `builtin:resolve-version`
3. `command:build` (layer continuation; skipped when no inner layer contributes steps)
4. `command:pre-build` (when present)
5. `builtin:build-artifacts`
6. `builtin:tag-artifacts`
7. `command:post-build` (when present; always runs after hard failures)

#### tag

Description: Tag configured artifacts.

Options: none.

Steps:

1. `builtin:resolve-version`
2. `builtin:tag-artifacts`

#### push

Description: Push configured artifacts when explicitly confirmed.

Options:

- `--confirm` (`bool`, default `false`)

Steps:

1. `builtin:push-artifacts` with `with.confirm = {{options.confirm}}`

Behavior notes:

- Push is opt-in everywhere (local and CI) — `--confirm` is always required.
- `rx push` succeeds but skips push with clear guidance when `--confirm` is not passed.
- `rx push --confirm` attempts actual push subject to policy/provider gates.

#### release

Description: Validate, verify, build, tag, and optionally push.

Options:

- `--push` (`bool`, default `false`)

Steps:

1. `command:pre-release` (when present)
2. `command:verify` (when present)
3. `command:build`
4. `command:pre-push` (when `{{options.push}}` and present)
5. `builtin:push-artifacts` when `{{options.push}}`, with `with.confirm = {{options.push}}`
6. `command:post-push` (when `{{options.push}}` and present)
7. `command:post-release` (when present; always runs after hard failures)

Behavior notes:

- `rx release` does not push.
- `rx release --push` passes explicit push intent into builtin push logic.
- The delegated `build` command validates, resolves the version, builds and tags configured artifacts, and runs optional build hooks.

#### clean

Description: Remove generated Rexo output.

Options: none.

Steps:

1. `builtin:clean`

Behavior notes:

- Explicit utility command.
- Not run automatically by release/build/verify.

### Aliases

- `all` -> `release`
- `ship` -> `push`

## Embedded Policy: dotnet

.NET toolchain overlay providing `restore`, `build`, `test`, `analyze`, `format` and `security`.
Essential behavior (restore, build, test with coverage, analyzer build) is on by default; the format
check, SARIF output and vulnerable-package scan are opt-in via `vars.dotnet.*`.
See [dotnet policy](embedded/dotnet.md) for the full vars reference.

## Embedded Policy: node

Node.js toolchain overlay. Install, build, test and lint are on by default; the format check, SARIF
lint and dependency audit are opt-in via `vars.node.*`. See [node policy](embedded/node.md).

## Embedded Policy: git-tag

Creates and pushes a version tag in `post-push`. Configure via `vars.gitTag.*` (`prefix`, `remote`,
`container`). See [git-tag policy](embedded/git-tag.md).

## Builtins Used By Embedded Templates

Core lifecycle builtins:

- `builtin:validate`: Validate loaded configuration.
- `builtin:resolve-version`: Resolve version and place it in execution context.
- `command:test`: Overlay-provided test command.
- `command:analyze`: Overlay-provided analysis command.
- `command:verify`: Overlay-composed quality gate command.
- `builtin:build-artifacts`: Build all matching artifacts.
- `builtin:tag-artifacts`: Tag all matching artifacts.
- `builtin:push-artifacts`: Push artifacts, apply push gates, write artifact manifest.
- `builtin:plan-artifacts`: Print/emit plan model for build and push eligibility.
- `builtin:clean`: Remove generated output (`artifacts/`).
- `builtin:dotnet-sarif-targets`: Write an MSBuild targets file (`with.path`) that makes every
  project/TFM emit its own SARIF 2.1 `ErrorLog` into `$(RexoSarifDirectory)`; optionally clears
  `with.sarifDirectory`. Used by the `embedded:dotnet` `analyze` command.
- `builtin:sarif-merge`: Merge SARIF 2.1.0 logs (`with.input` file or directory, recursive) into
  `with.output` with one run per tool, de-duplicated rules/results, repo-relative URIs, and
  `automationDetails.id` from `with.category`. Suitable for GitHub code scanning upload.

Related utility builtins available for custom commands:

- `builtin:config-resolved`
- `builtin:config-materialize`
- `builtin:plan`, `builtin:ship`, `builtin:all`
- `builtin:docker-plan`, `builtin:docker-ship`, `builtin:docker-all`, `builtin:docker-stage`

## Common Use Cases

### 1. Artifact-only repo with standard lifecycle

Use when you want immediate lifecycle commands with minimal config.

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "orders-api",
  "extends": ["embedded:standard"],
  "artifacts": [
    {
      "type": "docker",
      "name": "api",
      "settings": {
        "image": "ghcr.io/agile-north/orders-api"
      }
    }
  ]
}
```

Common flow:

```bash
rx plan
rx verify
rx build
rx release
rx release --push
```

### 2. Publish already-built artifacts explicitly

Use when build/tag happened earlier and you only want publish phase.

```bash
rx push --confirm
```

Equivalent alias:

```bash
rx ship --confirm
```

### 3. Show push eligibility before release

Use when validating branch/PR/clean-tree gates before running full release.

```bash
rx plan --push
```

### 4. Dotnet developer convenience workflow

Use when you want dotnet-centric command aliases and formatting helpers.

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "billing-service",
  "extends": ["embedded:standard", "embedded:dotnet"],
  "artifacts": [
    {
      "type": "nuget",
      "name": "Billing.Client",
      "settings": {
        "project": "src/Billing.Client/Billing.Client.csproj"
      }
    }
  ]
}
```

Common flow:

```bash
rx restore
rx verify
rx format
rx release --push
```

Recommended customization path for `embedded:dotnet` is the `vars.dotnet.*` bag rather than overriding commands:

```json
{
  "extends": ["embedded:standard", "embedded:dotnet"],
  "vars": {
    "dotnet": {
      "solution": "solution.slnx",
      "analyze": {
        "format": { "enabled": true },
        "sarif": { "enabled": true },
        "build": { "extraArgs": "/p:TreatWarningsAsErrors=true" }
      }
    }
  }
}
```

Policy vars are deep-merged underneath the repository's vars, so only the keys you set change.
See [dotnet policy](embedded/dotnet.md#varsdotnet-reference) for every supported var and default.

## Option Mapping With Step with

Embedded templates now use step-local option mapping so command intent is explicit
and centralized in builtins.

Example from `standard` release:

```json
{
  "id": "push",
  "uses": "builtin:push-artifacts",
  "when": "{{options.push}}",
  "with": {
    "confirm": "{{options.push}}"
  }
}
```

This pattern is recommended for custom policies too.

## Policy Selection Guidance

Choose `embedded:standard` when:

- You want consistent cross-language lifecycle defaults.
- You want release and push semantics aligned with explicit local confirmation.
- You want an artifact-only config to include lifecycle commands via explicit opt-in.

Choose `embedded:dotnet` when:

- You want restore/build/test/analyze/format commands for .NET out of the box.
- You want additive dotnet-specific commands on top of the standard lifecycle baseline.
- You want the dotnet `test` command to emit TRX results and collect XPlat coverage into the configured `outputs.tests.*` locations.

## Practical Notes

- Local push without explicit confirmation is a successful skip, not a hard failure.
- Push eligibility is still governed by push rules and provider constraints.
- `clean` is intentionally explicit and not part of default release pipelines.
- Embedded templates can be overridden by repo commands/aliases as needed.
- Coverage enablement for `embedded:dotnet` lives in the policy command overlay, not in core runtime defaults.
