# Embedded Policies Reference

This content has been reorganized for clarity.

**See the new structure:**

- [Embedded Policies](embedded/README.md) — Overview and index
- [standard policy](embedded/standard.md) — General lifecycle commands
- [dotnet policy](embedded/dotnet.md) — Dotnet-focused command surface

These pages contain the same information, organized by policy for easier navigation.

## What "Embedded" Means

Rexo ships lifecycle policies as embedded resources in the CLI assembly
(see `Rexo.Policies.EmbeddedPolicyTemplates`).

Current embedded policies:

- `standard`
- `dotnet`

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
2. `command:verify` (overlay contribution, when present)
3. `command:test` (when present)
4. `command:analyze` (when present)
5. `command:security` (when present)

Contract note:

- User-facing `verify` command includes validate.
- `verify` now composes command overlays; there is no dedicated core verify builtin.

#### build

Description: Build and tag configured artifacts locally.

Options: none.

Steps:

1. `builtin:validate`
2. `builtin:resolve-version`
3. `builtin:build-artifacts`
4. `builtin:tag-artifacts`

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

1. `builtin:validate`
2. `builtin:resolve-version`
3. `command:verify`
4. `builtin:build-artifacts`
5. `builtin:tag-artifacts`
6. `builtin:push-artifacts` when `{{options.push}}`, with `with.confirm = {{options.push}}`

Behavior notes:

- `rx release` does not push.
- `rx release --push` passes explicit push intent into builtin push logic.

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

Purpose:

- Dotnet-focused command surface.
- Adds restore/format/pack-centric workflows.

### Commands

#### ci

Description: Validate, resolve version, test, analyze, and build configured artifacts.

Options: none.

#### release

Description: Run CI flow, tag artifacts, optionally push.

Options:

- `--push` (`bool`, default `false`)

Behavior notes:

- Push step is guarded by a `when` expression.
- Push intent is forwarded into the builtin via `with.confirm = {{options.push}}`.

#### restore

Description: Run `dotnet restore`.

Options: none.

#### format

Description: Verify or apply `dotnet format`.

Options:

- `--fix` (`bool`, default `false`)

Behavior notes:

- `--fix` false: `dotnet format --verify-no-changes`
- `--fix` true: `dotnet format`

#### pack

Description: Resolve version and build artifacts intended for package workflows.

Options:

- `--configuration` (`string`, default `"Release"`)

### Aliases

- `r` -> `restore`
- `f` -> `format`

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
  "extends": ["embedded:dotnet"],
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
rx ci
rx format --fix
rx release --push
```

Recommended customization path for `embedded:dotnet` is the `vars.dotnet.*` bag rather than overriding commands.

Example:

```json
{
  "extends": ["embedded:dotnet"],
  "vars": {
    "dotnet": {
      "solution": "solution.slnx",
      "configuration": "Release",
      "restore": {
        "extraArgs": "--locked-mode"
      },
      "build": {
        "extraArgs": "/p:ContinuousIntegrationBuild=true"
      },
      "test": {
        "runsettings": "eng/test.runsettings",
        "extraArgs": "--filter Category!=Slow",
        "coverage": {
          "mode": "xplat"
        }
      },
      "format": {
        "extraArgs": "--severity error"
      },
      "analyze": {
        "formatExtraArgs": "--severity warn",
        "buildExtraArgs": "/p:TreatWarningsAsErrors=true"
      }
    }
  }
}
```

Supported optional vars for `embedded:dotnet`:

- `vars.dotnet.solution`: solution or project path passed to dotnet commands.
- `vars.dotnet.configuration`: build/test configuration. Default: `Release`.
- `vars.dotnet.restore.extraArgs`: appended to `dotnet restore`.
- `vars.dotnet.build.extraArgs`: appended to `dotnet build`.
- `vars.dotnet.test.runsettings`: passed as `--settings <path>`.
- `vars.dotnet.test.extraArgs`: appended to `dotnet test`.
- `vars.dotnet.test.coverage.mode`: `xplat` (default) or `none`.
- `vars.dotnet.format.extraArgs`: appended to `dotnet format`.
- `vars.dotnet.analyze.formatExtraArgs`: appended to `dotnet format --verify-no-changes`.
- `vars.dotnet.analyze.buildExtraArgs`: appended to the analysis `dotnet build` step.

Behavior notes:

- Coverage is enabled by default for the dotnet overlay using `--collect:"XPlat Code Coverage"`.
- Set `vars.dotnet.test.coverage.mode` to `none` to disable coverage without overriding the `test` command.
- If you need a non-standard collector or a completely custom test invocation, overriding the `test` command is still the fallback.
- The analysis `dotnet build` step does not force warnings as errors by default; use `vars.dotnet.analyze.buildExtraArgs` to opt in.
- Configured output directories are materialized before command steps run and cleaned up after the command if they stay empty.

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

- You want restore/format/ci convenience commands out of the box.
- You want additive dotnet-specific commands on top of the standard lifecycle baseline.
- You want the dotnet `test` command to emit TRX results and collect XPlat coverage into the configured `outputs.tests.*` locations.

## Practical Notes

- Local push without explicit confirmation is a successful skip, not a hard failure.
- Push eligibility is still governed by push rules and provider constraints.
- `clean` is intentionally explicit and not part of default release pipelines.
- Embedded templates can be overridden by repo commands/aliases as needed.
- Coverage enablement for `embedded:dotnet` lives in the policy command overlay, not in core runtime defaults.

