# Configuration Reference

For embedded templates, built-in lifecycle defaults, options, and usage examples,
see [Embedded Policies Reference](../embedded/README.md).

For runtime builtin contracts (inputs, calls, outputs, exit behavior),
see [Builtins Reference](../builtins/README.md).

For machine-readable CLI output and sidecar run-manifest fields,
see [Output Contract](./output-contract.md).

For a focused CI output emission quick reference, see [CI Output Emission](./ci-output-emission.md).

For detailed run-step container wrapping behavior (scope, defaults, env precedence,
fallback, and validation), see [Containerized Run Steps](./containerized-run.md).

For CLI config overrides, see [CLI Overrides](./overrides.md).

Default repository configuration file: **`.rexo/rexo.yaml`**

YAML and JSON are equally supported: both formats are validated against the same JSON Schema
and deserialized identically. `rx init` writes YAML by default (`--format json` for JSON).

Supported config locations (first match wins; YAML before JSON in each location):

1. `.rexo/rexo.yaml`, `.rexo/rexo.yml`, `.rexo/rexo.json` (default location)
2. `rexo.yaml`, `rexo.yml`, `rexo.json` (repo root)
3. Backward-compatible fallback: `.repo/repo.yaml|yml|json`, then `repo.yaml|yml|json` in root

Policy files use the same order: `.rexo/policy.*`, root `policy.*`, then `.repo/policy.*`.

If more than one candidate exists, Rexo uses the first match and prints a warning naming the
ignored files (also reported by `rx doctor` as `config-duplicates`).

Use `rx config explain <property.path>` to inspect a value from the effective merged
configuration. Sensitive property names and nested credentials are redacted. Its JSON output
identifies applicable merged policy, repository config files that declare the property (including
resolved local `extends` and overlays), and CLI `--set` layers in precedence order. Policy source
files remain grouped; the repository file list identifies declarations rather than resolving
field-level merge behavior.

Use `rx config resolved --provenance` to emit the effective configuration with sensitive values
redacted alongside repository config files, the merged policy group, and CLI `--set` property paths
(override values are omitted). Repository file paths indicate declarations rather than exact
field-level merge ownership.

## Remote policy lockfile

Policy sources declared in `policySources` or `REXO_POLICY_SOURCES` can be content-locked in
`.rexo/rexo.lock.yaml`:

```bash
rx update   # resolve configured sources and write source SHA-256 entries
rx restore  # require and verify a lock entry for each configured source
```

The lockfile records source references and content hashes only; URLs containing embedded
user information are rejected. When a lockfile exists, matching sources must retain their locked
content. Unlocked sources remain allowed for ordinary commands in existing repositories unless
strict locking is enabled with `REXO_POLICY_REQUIRE_LOCKED=true`; `rx restore` always requires
complete lock coverage. It never updates the lockfile; `rx update` deliberately refreshes it.
A source-resolution or hash error fails loading rather
than silently dropping that policy. Network cache fallback is reported and locked HTTP content is
still hash-checked.

`rx check` reports whether a policy lockfile is present and warns when configured sources are not
locked (or fails in strict-lock mode). `rx doctor` reports the CLI/config schema identity and
whether a local policy and policy lockfile are present.
`rx check` validates configured artifact source paths and provider availability, including automatic
version-provider detection and direct environment-backed version inputs, without printing values.
It does not resolve configured secret providers or contact artifact registries. Credential preflight
reports direct process/environment-file presence only; warnings may not account for provider-backed
secrets. Use `rx plan --push` when the configured lifecycle provides it to inspect policy and
credential eligibility without publishing.

## Local artifact promotion

Named environments currently identify repository-relative local artifact directories:

```yaml
environments:
  staging:
    path: deployments/staging
```

Run a build with `--json-file` to create its sidecar manifest, then promote its single verified
file artifact with `rx promote <run-manifest.json> staging`. Promotion verifies the recorded
SHA-256 and copies the same bytes into an immutable content-addressed directory; it never rebuilds.
`--dry-run` validates the source and reports the destination without writing files. This initial
contract supports one local file artifact at a time; providers that only report remote references
(such as container image tags) are rejected rather than treated as immutable identities. A JSON
record is written beside the promoted object with the source commit, version, config hash, lockfile
hash, target environment, and timestamp. This is local file promotion, not a remote deploy workflow.

## CLI workflow and safety

- `rx doctor` performs lightweight runtime/tool availability checks. `rx check [--strict]` inspects
  the effective repository configuration without building, publishing, deploying, resolving configured
  secret providers, or contacting artifact registries. It reports direct credential presence only,
  never values. Errors fail the check; warnings fail only in strict mode.
- `rx graph <command> [--format text|json|mermaid]` displays the effective configured steps without
  printing shell command bodies. `rx completion bash|zsh|fish|powershell` prints a basic completion
  script for the selected shell.
- `rx config explain <property.path>` displays an effective value with sensitive values redacted and
  reports applicable policy, repository files declaring the property (including local `extends`
  and overlays), and CLI `--set` source layers. Policy source files remain grouped.
- `NO_COLOR` disables color by default; `--no-color` and `--color` override it. `--non-interactive`
  disables prompts and prevents opening `rx ui`.
- `rx update` refreshes `.rexo/rexo.lock.yaml`; `rx restore` verifies every configured remote policy
  source against it without updating the lock. `REXO_POLICY_REQUIRE_LOCKED=true` requires lock
  coverage during ordinary commands.
- `rx promote <run-manifest.json> <environment>` copies exactly one local file artifact after
  verifying its recorded SHA-256. `--dry-run` performs validation without writing promotion files.
  This does not deploy to a remote environment or promote a mutable registry tag.

---

## Schema Contract (required)

Every config file must declare its schema and version. In YAML:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
$schema: https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
schemaVersion: "1.0"
name: my-repo
```

In JSON:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  ...
}
```

- `$schema`: the canonical URL `https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json` (recommended), or the relative `rexo.schema.json` / `../rexo.schema.json` for local-only use
- `schemaVersion`: must be `"1.0"` (in YAML, an unquoted `1.0` is also accepted)

### YAML specifics

- The `# yaml-language-server: $schema=...` modeline gives editors intellisense (completion, hover,
  validation). Rexo also accepts the modeline **instead of** a `$schema` key; if both are present
  they must match.
- Scalars follow the YAML 1.2 core schema: `true`/`false`, integers, floats, and `null`/`~` are typed;
  everything else (including `yes`/`no`/`on`/`off`) is a string. Quote values to force a string
  (for example `"true"` or `"1.0"`), or use an explicit `!!str` tag.
- Anchors, aliases, and `<<` merge keys are supported. Duplicate keys and multi-document files are errors,
  and parse errors report the file, line, and column.

### Editor intellisense

- **VS Code**: install the Red Hat *YAML* extension (`redhat.vscode-yaml`). The modeline is picked up
  automatically. Relative modeline paths resolve against the YAML file's directory, so
  `rx init --schema-source local` writes the schemas next to the config in `.rexo/`.
- **JetBrains IDEs**: the modeline is honoured natively; alternatively map `.rexo/rexo.yaml` to the schema under
  *Settings → Languages & Frameworks → Schemas and DTDs → JSON Schema Mappings*.
- Without a modeline you can map files in VS Code settings:

  ```json
  "yaml.schemas": {
    "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json": [".rexo/rexo.yaml", ".rexo/rexo.yml", "rexo.yaml", "rexo.yml"],
    "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json": [".rexo/policy.yaml", ".rexo/policy.yml", "policy.yaml", "policy.yml"]
  }
  ```

The schema annotations now include both `description` and `markdownDescription` on the main config sections. Editors that understand JSON Schema Markdown will show richer hover text and defaults; simpler tools can keep using the plain `description` text.

The loader validates against the embedded schema (or a local `rexo.schema.json`) via NJsonSchema before
deserializing. Missing/unsupported metadata or schema violations cause a hard failure.

To temporarily bypass rexo schema validation during local experimentation, set
`REXO_DISABLE_SCHEMA_VALIDATION=true` before running Rexo. When enabled, metadata
checks (`$schema`, `schemaVersion`) and NJsonSchema validation are skipped for rexo
configuration loading.

Policy files (`policy.yaml`/`policy.yml`/`policy.json`) follow the same contract, using:

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
  "vars": { ... },        // template vars ({{vars.*}}), deep-merged across layers
  "containers": { ... },  // reusable container definitions (see containerized-run.md)
  "versioning": { ... },
  "artifacts": [ ... ],
  "secrets": { ... },
  "runtime": { ... },
  "outputs": { ... }
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

  "outputs": {
    "emit": true,
    "root": "artifacts",
    "tests": {
      "results": "~/tests",
      "coverage": "~/tests/coverage",
      "reports": "~/tests/reports"
    },
    "analysis": {
      "reports": "~/analysis",
      "sarif": "~/analysis/sarif"
    },
    "security": {
      "audit": "~/security/audit.json",
      "reports": "~/security",
      "sarif": "~/security/sarif"
    },
    "packages": "~/packages",
    "manifests": {
      "path": "~/manifests",
      "commandMode": "aggregate",
      "commandDetail": "summary"
    },
    "logs": "~/logs",
    "temp": "~/tmp"
  },

  "runtime": {
    "dryRun": false,
    "push": {
      "enabled": true,
      "noPushInPullRequest": false,
      "requireCleanWorkingTree": false,
      "branches": []
    },
    "commands": {
      "maxDepth": 5
    }
  }
}
```

Notes:

- These are defaults for supported `outputs` and `runtime` fields, not fields that must be written to a config file.
- `outputs.tests`, `outputs.analysis`, and `outputs.security` configure output locations only; test and analysis commands are provided by policy overlays.
- Paths beginning with `~/` resolve under `outputs.root`.
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
- `vars` and `settings` are **deep-merged**: nested objects merge key by key, so a child can change
  `vars.dotnet.analyze.sarif.enabled` without restating the rest of `vars.dotnet`. Scalars and arrays
  are replaced.
- `containers` registries merge by name, field by field (child wins).

### Policy parity: policies supply defaults

Policies (embedded, local `policy.json`, or `policySources`) may declare the same sections a
repository config can: `vars`, `settings`, `containers`, `outputs`, `runtime`, `versioning`,
`secrets`, and their own `extends`. Policy values are **only defaults** — they are layered underneath
the repository config, and the repository always wins. This is how stack policies such as
`embedded:dotnet` ship opt-in switches (e.g. `vars.dotnet.analyze.sarif.enabled: false`) that you
flip in your own config without overriding the policy's commands.

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

When a toolchain policy such as `embedded:dotnet` is selected, stack it after
`embedded:standard` so the shared lifecycle commands (`verify`, `release`, and artifact
operations) compose with toolchain commands such as `restore`, `format`, and `security`:

```json
{ "extends": ["embedded:standard", "embedded:dotnet"] }
```
If you want generic git tag creation on push, stack `embedded:git-tag` alongside `embedded:standard`:

```json
{ "extends": ["embedded:standard", "embedded:git-tag"] }
```

The shared embedded policies compose by command name. The order in `extends` controls
the final command sequence and any merged option metadata.

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
{ "extends": ["embedded:standard", "embedded:dotnet"] }
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
- [Secrets](secrets.md) — Configure first-class secret providers and named secret items
- [Runtime](runtime.md) — Configure output, push policy, tests, and analysis settings
- [Template Variables](templates.md) — Use dynamic variables in step commands
