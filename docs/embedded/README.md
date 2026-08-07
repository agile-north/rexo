# Embedded Policies

This guide documents every embedded lifecycle policy currently shipped with Rexo,
what each command does, which options it accepts, and common use cases.

Use this alongside [Configuration Reference](../configuration/README.md).

For per-builtin runtime contract details (inputs, outputs, internal calls, exit behavior),
see [Builtins Reference](../builtins/README.md).

For command-line mental models of each builtin, see the
[Approximate Shell Equivalents](../builtins/patterns.md#approximate-shell-equivalents)
section in Builtins Reference.

## What "Embedded" Means

Rexo ships lifecycle policies as embedded resources in the CLI assembly
(see `Rexo.Policies.EmbeddedPolicyTemplates`).

Current embedded policies:

- [standard](standard.md) — General lifecycle commands (`build`, `test`, `verify`, `release`, `push`, etc.)
- [dotnet](dotnet.md) — Dotnet-focused command surface with `restore`, `build`, `test`, `analyze`, `format`, `release` helpers
- [git-tag](git-tag.md) — Generic git tag creation for versioned repositories

The `post-push` template in this family is intentionally composable by name. It uses
`merge: append`, so the `extends` order determines the final step sequence and merged
options. Keep `embedded:standard` first, then add `embedded:git-tag` when you want
version tagging as part of the composed flow.

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

- Repo config says what this repo emits (artifacts, versioning, outputs, vars).
- Embedded policy says how this repo behaves (lifecycle command shape).
- `embedded:standard` is the recommended lifecycle baseline when you want policy-provided commands.
- `embedded:standard`'s `build` command includes a continuation point so toolchain overlays can slot their build step between version resolution and artifact packaging.

## Policy Selection Guidance

Choose `embedded:standard` when:

- You want consistent cross-language lifecycle defaults.
- You want release and push semantics aligned with explicit local confirmation.
- You want an artifact-only config to include lifecycle commands via explicit opt-in.

Choose `embedded:dotnet` when:

- You want restore/format/ci convenience commands out of the box.
- You want additive dotnet-specific commands on top of the standard lifecycle baseline.
- You want the dotnet `test` command to emit TRX results and collect XPlat coverage into the configured `outputs.tests.*` locations.

## Policy Details

- [standard](standard.md) — lifecycle commands, plan/verify/build/release/push
- [dotnet](dotnet.md) — dotnet-specific commands, restore/format/ci/pack with optional var-driven customization
- [git-tag](git-tag.md) — generic git tag creation for versioned repositories

## Builtins Used By Embedded Templates

Core lifecycle builtins:

- `builtin:validate`: Validate loaded configuration.
- `builtin:resolve-version`: Resolve version and place it in execution context.
- `builtin:build-artifacts`: Build all matching artifacts.
- `builtin:tag-artifacts`: Tag all matching artifacts.
- `builtin:push-artifacts`: Push artifacts, apply push gates, write artifact manifest.
- `builtin:plan-artifacts`: Print/emit plan model for build and push eligibility.
- `builtin:clean`: Remove generated output (`artifacts/`).

Policy-overlay lifecycle commands:

- `test`: Provided by an overlay policy command implementation.
- `analyze`: Provided by an overlay policy command implementation.
- `verify`: Composes validate + overlay `test`/`analyze` (and optional `security`).

Related utility builtins available for custom commands:

- `builtin:config-resolved`
- `builtin:config-materialize`
- `builtin:plan`, `builtin:ship`, `builtin:all`
- `builtin:docker-plan`, `builtin:docker-ship`, `builtin:docker-all`, `builtin:docker-stage`

See [Builtins Reference](../builtins/README.md) for complete builtin documentation.

## Common Use Cases

### Use case: Artifact-only repo with standard lifecycle

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

### Use case: Publish already-built artifacts explicitly

Use when build/tag happened earlier and you only want publish phase.

```bash
rx push --confirm
```

Equivalent alias:

```bash
rx ship --confirm
```

### Use case: Show push eligibility before release

Use when validating branch/PR/clean-tree gates before running full release.

```bash
rx plan --push
```

### Use case: Dotnet developer convenience workflow

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

See [dotnet policy](dotnet.md#customization-via-varsdotnet) for var-driven customization.

## Option Mapping With Step with

Embedded templates use step-local option mapping so command intent is explicit
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

## Practical Notes

- Local push without explicit confirmation is a successful skip, not a hard failure.
- Push eligibility is still governed by push rules and provider constraints.
- `clean` is intentionally explicit and not part of default release pipelines.
- Embedded templates can be overridden by repo commands/aliases as needed.
- Coverage enablement for `embedded:dotnet` lives in the policy command overlay, not in core runtime defaults.

