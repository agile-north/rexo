# Output Contract

This page defines exactly what Rexo emits for machine-readable output and when fields are populated.

Template context mapping reference:

- For command template variables, see [templates.md](templates.md).
- `{{push.*}}` values in templates are derived from step output contract fields `__artifacts` and `__pushDecisions`.

## Flags And Emission

Global flags:

- `--json`: write `CommandResult` JSON to stdout.
- `--json-file <path>`: write `CommandResult` JSON to `<path>`.
- `--quiet`: suppress human console rendering.

When `outputs.ci.emit` is enabled, Rexo can emit CI-native variables after command execution. The
`outputs.ci.scope` setting accepts either `"safe"` / `"full"` shortcuts or an object with `mode`,
`include`, and `exclude` masks. Masks match the canonical field path and can be exact names,
wildcards, or `regex:...` expressions.

Configured output paths are materialized before command steps run, which keeps commands
portable while still letting analyzers and test runners write into the resolved paths.

For a defaults-and-provider quick reference, see [CI Output Emission](ci-output-emission.md).

### CI Key Formatting

`outputs.ci.keyCasing` controls how flattened manifest paths become variable keys.

- `upperSnake`: child properties are separated with `_` and uppercased.
- `lowerSnake`: child properties are separated with `_` and lowercased.
- `kebab`: child properties are separated with `-` and lowercased.
- `camel`: child properties are concatenated and each child boundary is capitalized after the first token.
- `pascal`: child properties are concatenated and each child boundary is capitalized.

Array indexes are treated as child segments in all styles.

Examples for `steps[0].stepId`:

- `upperSnake`: `STEPS_0_STEP_ID`
- `lowerSnake`: `steps_0_step_id`
- `kebab`: `steps-0-step-id`
- `camel`: `steps0StepId`
- `pascal`: `Steps0StepId`

Known mixed-case product words are preserved as a single token during normalization.
For example, `nuGetVersion` becomes `NUGET_VERSION` (not `NU_GET_VERSION`).

By default, fields that normalize to an empty value (including `null`) are not emitted.
Set `outputs.ci.emitEmptyValues: true` to always emit those keys as empty strings.

For `github-actions`, use `outputs.ci.github-actions.scope` to choose the target file:

- `env` (default): writes to `$GITHUB_ENV`
- `output`: writes to `$GITHUB_OUTPUT`
- `state`: writes to `$GITHUB_STATE`

When `--json-file` is provided, Rexo also writes a sidecar run manifest next to the JSON file:

- Input file: `artifacts/runs/release.json`
- Sidecar: `artifacts/runs/release-manifest.json`

If the file name does not end in `.json`, sidecar naming falls back to `<path>.manifest.json`.

## CommandResult (`--json` and `--json-file`)

Primary machine payload:

```jsonc
{
  "Command": "release",
  "Success": true,
  "ExitCode": 0,
  "Message": "Command 'release' completed successfully.",
  "Outputs": {},
  "Steps": [
    {
      "StepId": "build",
      "Success": true,
      "ExitCode": 0,
      "Duration": "00:00:12.3456789",
      "Outputs": {
        "message": "Command 'build' completed successfully.",
        "__version": { "SemVer": "1.2.3" }
      }
    }
  ],
  "Version": { "SemVer": "1.2.3" },
  "StructuredErrors": [],
  "Artifacts": [],
  "PushDecisions": []
}
```

### Field Population Rules

`Version`

- Populated when a step resolves/provides version metadata (for example via `builtin:resolve-version`), including nested `command` steps.
- `null` when no executed step produced version metadata.

`Artifacts`

- Populated by `builtin:push-artifacts` output (`__artifacts`).
- Empty when push phase is skipped (for example `release` without `--push`) or when no artifacts are configured.

`PushDecisions`

- Populated by `builtin:push-artifacts` output (`__pushDecisions`).
- Entries include policy allow/deny reasons per artifact.
- Empty when push phase is skipped.

Template linkage:

- `{{push.hasData}}` is true when either `__artifacts` or `__pushDecisions` exists in completed steps.
- `{{push.anyPushed}}`, `{{push.pushedCount}}`, and `{{push.artifactCount}}` are computed from `__artifacts` (`Pushed` flags).
- `{{push.allowedCount}}`, `{{push.deniedCount}}`, `{{push.anyBlocked}}`, and `{{push.blockReasons}}` are computed from `__pushDecisions`.

`Steps[*].Outputs`

- Contains per-step details (messages, skip markers, extracted outputs, `__version`, `__artifacts`, `__pushDecisions`, etc.).

## Run Manifest Sidecar (`*-manifest.json`)

Written only when `--json-file` is provided.

## Command Manifest Files (`outputs.manifests`)

When `outputs.emit` is true (default), Rexo writes command manifests under
`outputs.manifests.path` (default `~/manifests`, which resolves under `outputs.root`).

Paths that begin with `~/` are rooted under `outputs.root`. Plain relative paths are
repo-relative.

Default strategy is optimized for low noise:

- `outputs.manifests.commandMode: "aggregate"` (default): writes `commands.json` containing all commands in one file.
- `outputs.manifests.commandMode: "single"`: alias for aggregate; also writes `commands.json`.
- `outputs.manifests.commandDetail: "summary"` (default): concise payload only

Optional verbose strategies:

- `outputs.manifests.commandMode: "perCommand"`: writes `<command>.json` files
- `outputs.manifests.commandDetail: "verbose"`: includes full `CommandResult`

Example:

```jsonc
"outputs": {
  "root": "artifacts",
  "manifests": {
    "path": "~/manifests",
    "commandMode": "perCommand",
    "commandDetail": "verbose"
  }
}
```

Example shape:

```jsonc
{
  "SchemaVersion": "1.0",
  "ToolVersion": "1.0.0+abcdef",
  "RepoName": "runtime-licensing",
  "RepoRoot": "S:\\repos\\nrth\\runtime-licensing",
  "Branch": "feature/x",
  "CommitSha": "...",
  "RemoteUrl": "https://github.com/...",
  "CommandExecuted": "release",
  "Success": true,
  "ExitCode": 0,
  "StartedAt": "...",
  "CompletedAt": "...",
  "Duration": "00:01:21.5500465",
  "Version": { "SemVer": "1.2.3" },
  "Steps": [
    {
      "StepId": "verify",
      "Success": true,
      "ExitCode": 0,
      "DurationMs": 67364.3721,
      "FileOutputs": {}
    }
  ],
  "Artifacts": [],
  "PushDecisions": [],
  "Warnings": [],
  "Errors": [],
  "ConfigHash": "sha256...",
  "AssemblyVersion": "1.2.3.0",
  "InformationalVersion": "1.2.3",
  "NuGetVersion": "1.2.3"
}
```

### Notes

- Sidecar includes repository and CI context, timing, config hash, and normalized step timing (`DurationMs`).
- `Artifacts`/`PushDecisions` follow the same population rules as `CommandResult`.
- `Version` follows the same population rule: populated only if version metadata is produced during executed steps.

## Practical Scenarios

`rx release --json-file artifacts/runs/release.json`

- Build/verify lifecycle runs.
- Push steps are skipped unless `--push` is provided.
- `Artifacts` and `PushDecisions` are typically empty.

`rx release --push --json-file artifacts/runs/release.json`

- Push phase runs.
- `Artifacts` and `PushDecisions` are populated.
- If policy blocks push, entries are present with deny reasons and `Pushed=false`.
