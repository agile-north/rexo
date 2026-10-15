# Runtime Configuration

Configure runtime behavior, output paths, and push policies.

---

## `runtime.dryRun`

Controls dry-run mode for the current run.

```jsonc
"runtime": {
  "dryRun": true
}
```

- `dryRun` (default: `false`): when `true`, Rexo plans or simulates actions instead of performing side effects where supported.
- `--dry-run` / `--no-dry-run` on the CLI override the config value for a given invocation.
- Commands can read the resolved flag through `{{options.dry-run}}`.
- For broader config mutation at invocation time, use [CLI Overrides](./overrides.md).

`runtime.push.dryRun` can be used to force push-related commands into dry-run mode even when the global runtime flag is off.

Dry-run is not a sandbox for arbitrary configured commands: `run` steps still execute unless the command
uses `{{options.dry-run}}` to guard them. Built-in artifact pushes, policy-lock updates, artifact
promotion, CI scaffolding, and config materialization honor dry-run by avoiding their respective
external or persistent writes; read-only checks and planning continue normally.

## Interactive and color output

- `--non-interactive` disables prompts and prevents `rx ui` from opening.
- `NO_COLOR` disables ANSI colors by default when set to a non-empty value.
- `--no-color` and `--color` override the environment preference for one invocation.

## `runtime.commands`

Controls command delegation depth, fallback merge behavior for same-name command collisions,
and the default execution detail level.

```jsonc
"runtime": {
  "commands": {
    "defaultMergeMode": "append",
    "maxDepth": 5,
    "echo": "summary"
  }
}
```

- `defaultMergeMode` (optional): fallback merge mode used when a child command does not specify `merge.mode` or legacy `merge`. Accepts the same values as command-level merge modes: `layer`, `replace`, `append`, `prepend`, `wrap`.
- `maxDepth` (default: `5`): maximum delegated command call depth.
- `echo` (default: `summary`): controls Rexo-generated execution descriptions. `none` hides step metadata and command lines, `summary` shows step metadata without command bodies, and `full` also shows the rendered command body. Known secret values are masked in displayed commands and process output.
- `--echo none|summary|full` overrides configuration for one invocation. Precedence is CLI flag, step `echo`, command `echo`, `runtime.commands.echo`, then `summary`. If a configured command declares its own `echo` option, the corresponding `--echo <value>` is preserved as that command option rather than consumed as the global flag.
- Echo controls descriptions, not process output: stdout, stderr, and failures remain visible.
- The runtime default is useful for multi-stack repositories that want same-name commands to fan out without repeating merge metadata on every child command.
- Keep replace-style behavior when you want explicit collisions to stay single-owner, especially for side-effect-heavy commands.

---

## `outputs`

Controls filesystem artifact emission, the root output folder, and per-category output paths.
This is a top-level config section, separate from `runtime`.

```jsonc
"outputs": {
  "emit": true,
  "precreateDirectories": false,
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
  "packages": "~/packages",
  "manifests": {
    "emit": false,
    "path": "~/manifests",
    "commandMode": "aggregate",
    "commandDetail": "summary"
  },
  "logs": "~/logs",
  "temp": "~/tmp"
}
```

- `emit` (default: `true`): master permission for automatic runtime output files. Explicit `--json-file` results and their run-manifest sidecars are unaffected.
- `precreateDirectories` (default: `false`): when `true` and `emit` is enabled, prepare the full configured output directory tree before steps run, then clean up newly created directories that remain empty. Otherwise file-producing operations create only the parents they need. Custom shell commands must prepare their own directories when required.
- `root` (default: `artifacts`): root folder used by runtime artifact outputs.
- `tests` — overrides where test results, coverage data, and coverage reports are written. The policy overlay (e.g. `embedded:dotnet`) reads these paths when constructing test commands.
- `analysis` — overrides where analysis reports and SARIF files are written. The policy overlay reads these paths when constructing analysis commands.
- `packages` (default: `~/packages` under `outputs.root`): NuGet and other package output directory.
- `manifests.path` (default: `~/manifests`): manifest directory. `~/...` resolves under `outputs.root`.
- `manifests.emit` (default: `false`): opt in to both automatic command and artifact manifests. Both require `outputs.emit` to be enabled. Artifact manifests remain at `outputs.root/manifest.json`; `manifests.path` controls only command manifests.
- `manifests.commandMode` (default: `aggregate`): command-manifest file strategy.
  - `single`: alias for aggregate; write one aggregated manifest file (`commands.json`) containing every command run.
  - `perCommand`: write one manifest file per command (`<command>.json`).
  - `aggregate`: write a single aggregated manifest file (`commands.json`) containing every command run.
- `manifests.commandDetail` (default: `summary`): command-manifest detail level.
  - `summary`: concise command/step/file-output summary.
  - `verbose`: includes full command result payload.
- `logs` (default: `~/logs` under `outputs.root`): log output directory.

Path behavior under `outputs`:

- Omitted path values default under `outputs.root`.
- `~/sub/path` means "under root" explicitly.
- Plain relative paths (for example `tests/results`) are repo-relative.
- Absolute paths remain absolute.

> **Note**: test and analysis *execution* (toolchain, arguments, triggers) is not configured here. It is provided by the active policy overlay — see [Embedded policies](../EMBEDDED.md).

---

## `runtime.push`

Push policy and eligibility rules enforced by `builtin:push-artifacts`.

```jsonc
"runtime": {
  "push": {
    "enabled": true,
    "noPushInPullRequest": true,
    "requireCleanWorkingTree": true,
    "branches": ["main", "release/*"]
  }
}
```

`builtin:push-artifacts` enforces these rules globally, then merges per-artifact
overrides from `artifacts[].settings.push.*`.

When dry-run is enabled, `builtin:push-artifacts` does not call artifact providers. It
simulates a successful push, records the planned references in the runtime manifest,
and keeps the run side-effect free.

| Rule | Effect |
| --- | --- |
| `enabled` | Enables/disables push globally |
| `noPushInPullRequest` | Rejects push when the CI environment reports a PR build |
| `requireCleanWorkingTree` | Rejects push when the git working tree has uncommitted changes |
| `branches` | Allows push only when branch matches listed patterns |
| `dryRun` | Forces push into simulation mode without provider calls |
