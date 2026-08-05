# Runtime Configuration

Configure runtime output and push policies.

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

## `runtime.commands`

Controls command delegation depth and the fallback merge behavior for same-name command collisions.

```jsonc
"runtime": {
  "commands": {
    "defaultMergeMode": "append",
    "maxDepth": 5
  }
}
```

- `defaultMergeMode` (optional): fallback merge mode used when a child command does not specify `merge.mode` or legacy `merge`. Accepts the same values as command-level merge modes: `layer`, `replace`, `append`, `prepend`, `wrap`.
- `maxDepth` (default: `5`): maximum delegated command call depth.
- The runtime default is useful for multi-stack repositories that want same-name commands to fan out without repeating merge metadata on every child command.
- Keep replace-style behavior when you want explicit collisions to stay single-owner, especially for side-effect-heavy commands.

---

## `runtime.output`

Controls filesystem artifact emission, the root output folder, and per-category output paths.

```jsonc
"runtime": {
  "output": {
    "emitRuntimeFiles": true,
    "root": "artifacts",
    "tests": {
      "results": "~/tests",
      "coverage": "~/coverage",
      "reports": "~/tests/reports"
    },
    "analysis": {
      "reports": "~/analysis",
      "sarif": "~/analysis/sarif"
    },
    "packages": "~/packages",
    "manifests": {
      "path": "~/manifests",
      "commandMode": "aggregate",
      "commandDetail": "summary"
    },
    "logs": "~/logs",
    "temp": "~/tmp"
  }
}
```

- `emitRuntimeFiles` (default: `true`): when `false`, runtime-generated files (for example artifact manifest files) are not written.
- `root` (default: `artifacts`): root folder used by runtime artifact outputs.
- `tests` — overrides where test results, coverage data, and coverage reports are written. The policy overlay (e.g. `embedded:dotnet`) reads these paths when constructing test commands.
- `analysis` — overrides where analysis reports and SARIF files are written. The policy overlay reads these paths when constructing analysis commands.
- `packages` (default: `artifacts/packages`): NuGet and other package output directory.
- `manifests.path` (default: `~/manifests`): manifest directory. `~/...` resolves under `outputs.root`.
- `manifests.commandMode` (default: `aggregate`): command-manifest file strategy.
  - `single`: alias for aggregate; write one aggregated manifest file (`commands.json`) containing every command run.
  - `perCommand`: write one manifest file per command (`<command>.json`).
  - `aggregate`: write a single aggregated manifest file (`commands.json`) containing every command run.
- `manifests.commandDetail` (default: `summary`): command-manifest detail level.
  - `summary`: concise command/step/file-output summary.
  - `verbose`: includes full command result payload.
- `logs` (default: `artifacts/logs`): log output directory.

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
