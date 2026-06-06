# Configuration Reference

For embedded templates, built-in lifecycle defaults, options, and usage examples,
see [Embedded Policies Reference](../embedded/README.md).

For runtime builtin contracts (inputs, calls, outputs, exit behavior),
see [Builtins Reference](../builtins/README.md).

For machine-readable CLI output and sidecar run-manifest fields,
see [Output Contract](./output-contract.md).

For detailed run-step container wrapping behavior (scope, defaults, env precedence,
fallback, and validation), see [Containerized Run Steps](./containerized-run.md).

For CLI config overrides, see [CLI Overrides](./overrides.md).

Default repository configuration file: **`rexo.json`**

Supported config locations (first match wins):

- `rexo.json`, `rexo.yaml`, `rexo.yml` (repo root)
- `.rexo/rexo.json`, `.rexo/rexo.yaml`, `.rexo/rexo.yml`
- Backward-compatible fallback: `repo.json|yaml|yml` in root or `.repo/`

---

## Schema Contract (required)

Every config file (`rexo.json`/`rexo.yml`) must begin with:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  ...
}
```

- `$schema`: the canonical URL `https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json` (recommended), or the relative `rexo.schema.json` / `../rexo.schema.json` for local-only use
- `schemaVersion`: must be `"1.0"`

The loader validates against the embedded schema (or a local `rexo.schema.json`) via NJsonSchema before
deserializing. Missing/unsupported metadata or schema violations cause a hard failure.

To temporarily bypass rexo schema validation during local experimentation, set
`REXO_DISABLE_SCHEMA_VALIDATION=true` before running Rexo. When enabled, metadata
checks (`$schema`, `schemaVersion`) and NJsonSchema validation are skipped for rexo
configuration loading.

Policy files (`policy.json`/`policy.yml`) follow the same contract, using:

- `$schema`: `https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json` (recommended), or `policy.schema.json` / `../policy.schema.json`
- `schemaVersion`: must be `"1.0"`

When `rx init --schema-source local --with-policy` is used, both schema files are written to `.rexo/`:

- `.rexo/rexo.schema.json`
- `.rexo/policy.schema.json`

---

## Top-level Structure

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "description": "Optional description",

  // Inherit from one or more base configs (local paths, resolved relative to this file)
  "extends": ["./base/rexo.json"],

  // Opt-in remote policy sources (version-controlled, lower priority than REXO_POLICY_SOURCES)
  "policySources": [
    "nuget:MyOrg.Policies@1.2.0#policies/standard.json"
  ],

  "commands": { ... },
  "aliases": { ... },
  "versioning": { ... },
  "artifacts": [ ... ],
  "runtime": { ... },
  "tests": { ... },
  "analysis": { ... }
}
```

## Fully Emitted Effective Defaults

When optional fields are omitted, runtime behavior applies defaults. The example below
shows the effective values used by built-ins (not a requirement to persist every field in your file).

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",

  "commands": {},
  "aliases": {},
  "artifacts": [],

  "versioning": {
    "provider": "auto",
    "fallback": "0.1.0-local",
    "settings": {}
  },

  "runtime": {
    "output": {
      "emitRuntimeFiles": true,
      "root": "artifacts"
    },
    "push": {
      "enabled": true,
      "noPushInPullRequest": false,
      "requireCleanWorkingTree": false,
      "branches": []
    }
  },

  "tests": {
    "enabled": true,
    "projects": null,
    "configuration": "Release",
    "resultsOutput": "<runtime.output.root>/tests",
    "coverageOutput": null,
    "collectCoverage": null,
    "coverageThreshold": null
  },

  "analysis": {
    "enabled": true,
    "failOnIssues": true,
    "tools": [],
    "configuration": "<runtime.output.root>/analysis.sarif.json"
  }
}
```

Notes:

- `versioning` defaults are used by `builtin:resolve-version` when `versioning` is omitted.
- `tests.resultsOutput` and `analysis.configuration` are computed from `runtime.output.root` when omitted.
- `collectCoverage` only becomes active when coverage output collection is configured.
- `commands`, `aliases`, and `artifacts` are shown as empty here for completeness; they are optional in config files.

---

## `extends` — Config Merge Pipeline

`extends` accepts an array containing either local file paths or embedded policy
template references.

Supported entry types:

- Local path: `./base/rexo.json`
- Embedded template: `embedded:standard`, `embedded:dotnet`, `embedded:none`, …

Merge behavior:

- Circular references are detected and rejected.
- Configs are merged breadth-first.
- Child properties win over base properties.
- Commands and aliases are merged (child additions take priority).

### Minimal-by-default lifecycle

Rexo now uses an explicit lifecycle model. If you do not set `extends`, no embedded policy
commands are added automatically. An artifacts-only config remains minimal until you opt in
to a policy template.

This is intentional so Rexo can be used as a lightweight command/alias runtime without becoming a build/release tool unless a policy is selected.

To keep a config explicitly minimal, include `embedded:none` in `extends`:

```json
{ "extends": ["embedded:none"] }
```

`embedded:none` is an empty policy that carries no commands or aliases. It is useful for
making minimal intent explicit in shared templates.

### Policy template stacking

When a project-specific embedded policy is selected (e.g. `dotnet-api`) it should be
stacked *on top of* `embedded:standard` so both the shared lifecycle commands (`build`,
`test`, `verify`, `release`) and the project-specific commands (`ci`, `restore`,
`format`, `stage`) are available together:

```json
{ "extends": ["embedded:standard", "embedded:dotnet-api"] }
```
If you want GitHub release automation for `release --push`, stack `embedded:github-release`
alongside `embedded:standard`:

```json
{ "extends": ["embedded:standard", "embedded:github-release"] }
```

For more reusable versioning and release flow composition, stack `embedded:git-tag`
before `embedded:github-release`:

```json
{ "extends": ["embedded:standard", "embedded:git-tag", "embedded:github-release"] }
```

If you also want commit status publishing for the release flow, add `embedded:github-status`:

```json
{ "extends": ["embedded:standard", "embedded:git-tag", "embedded:github-status", "embedded:github-release"] }
```

For richer GitHub CI metadata and security report uploads, also add `embedded:github-status` and `embedded:github-sarif`:

```json
{
  "extends": [
    "embedded:standard",
    "embedded:git-tag",
    "embedded:github-status",
    "embedded:github-sarif",
    "embedded:github-release"
  ]
}
```

GitHub auth is standardized using `GITHUB_TOKEN` for containerized `gh` operations. `GITHUB_REPOSITORY` is preferred when available, but an authenticated `gh` session and configured git remote can also infer the repository context.

This is what `rx init` generates automatically when `--with-policy` is used with a
recognized policy template that is not `standard`.

### Command naming convention

Lifecycle commands provided by policy templates (`build`, `test`, `verify`, `release`,
etc.) are designed to be the primary entry points. Project-specific developer convenience
commands should use a `local` prefix to avoid name collisions:

- **Policy lifecycle**: `build`, `test`, `verify`, `release` (from `embedded:standard`)
- **Project-specific**: `local build`, `local test` (scaffolded by `rx init`)

This allows both sets of commands to coexist cleanly. `rx local build` runs your
project's custom build step; `rx build` runs the full policy-driven lifecycle.

Examples:

```json
{ "extends": ["embedded:standard"] }
```

```json
{ "extends": ["embedded:standard", "embedded:dotnet-api"] }
```

```json
{ "extends": ["embedded:none"] }
```

```json
{ "extends": ["embedded:dotnet", "../../shared/rexo.json"] }
```

```json
{
  "name": "orders-api",
  "artifacts": [
    {
      "type": "docker",
      "name": "api",
      "settings": { "image": "ghcr.io/acme/orders-api" }
    }
  ]
}
```

The last example remains minimal until you explicitly add an `extends` entry.

---

## Configuration Sections

Detailed reference for each config section:

- [Commands](commands.md) — Define command workflows with options, args, and steps
- [Versioning](versioning.md) — Configure version providers and auto-detection
- [Artifacts](../artifacts/README.md) — Configure artifact build/tag/push workflows
- [Runtime](runtime.md) — Configure output, push policy, tests, and analysis settings
- [Template Variables](templates.md) — Use dynamic variables in step commands

