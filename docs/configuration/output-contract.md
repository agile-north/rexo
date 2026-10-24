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

`outputs.emit` defaults to `true` and is the master permission for automatic runtime output files.
`outputs.precreateDirectories` defaults to `false`: Rexo file-producing operations create their
needed parent directories, not unrelated output directories. Custom shell commands and external
tools must create their own parents if needed, or explicitly opt in to
`outputs.precreateDirectories: true`. With that opt-in and `outputs.emit` enabled, Rexo prepares
the full configured output directory tree before steps run (including the manifest directory,
even when manifest persistence is disabled). Newly created directories are cleaned up after
success or failure if they remain empty; existing directories and produced files are preserved.

The `embedded:node` policy explicitly enables precreation because its audit commands redirect
JSON into `outputs.security.audit`. Repositories can override it with `false` when their commands
prepare their own directories. The .NET policy and Rexo's self-hosted configuration do not need
precreation: test runners and SARIF builtins create their required directories.

For all safe/full variables, emission policies, provider destinations, and release gates, see
[CI/CD Emission Reference](ci-output-emission.md).

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
  "PushDecisions": [],
  "ArtifactsAnyPushed": false,
  "ArtifactsPushedCount": 0,
  "ArtifactsAllPushed": false
}
```

### Field Population Rules

`Version`

- Populated when a step resolves/provides version metadata (for example via `builtin:resolve-version`), including nested `command` steps.
- `null` when no executed step produced version metadata.

`Artifacts`

- Populated by build and push builtin output (`__artifacts`).
- May include build-only entries when the push phase is skipped, and repeated lifecycle entries.
- Push output includes the complete selected publication set, including artifacts not processed
  after an earlier failure. No configured artifacts means an empty list.
- A selected artifact with no registered push provider fails with exit code 6 instead of being
  silently omitted from the publication set.

`PushDecisions`

- Populated by `builtin:push-artifacts` output (`__pushDecisions`).
- Entries include policy allow/deny reasons per artifact.
- Empty when push phase is skipped.

`ArtifactsAnyPushed`, `ArtifactsPushedCount`, `ArtifactsAllPushed`

- Computed consistently for command JSON, run manifests, CI safe/full profiles, and templates.
- Count distinct case-insensitive artifact type/name identities selected for publication.
- `AllPushed` requires a nonempty publication set with every selected artifact published and
  no denied, failed, or unprocessed push decisions.
- Dry runs keep planned tags but set `Pushed=false`; simulations never open a release gate.
- These outcomes do not replace `Success`: a later step can fail after successful publication.
- Existing artifacts in a registry are not counted unless the provider reports publication
  during this invocation. A provider no-op is not publication.

Template linkage:

- `{{push.hasData}}` is true when either `__artifacts` or `__pushDecisions` exists in completed steps.
- `{{push.anyPushed}}`, `{{push.pushedCount}}`, and `{{push.allPushed}}` use the shared publication
  summary from `__artifacts` and `__pushDecisions`; `{{push.artifactCount}}` counts raw entries.
- `{{push.allowedCount}}`, `{{push.deniedCount}}`, `{{push.anyBlocked}}`, and `{{push.blockReasons}}` are computed from `__pushDecisions`.

`Steps[*].Outputs`

- Contains per-step details (messages, skip markers, extracted outputs, `__version`, `__artifacts`, `__pushDecisions`, etc.).

## Run Manifest Sidecar (`*-manifest.json`)

Written only when `--json-file` is provided.

## Automatic Command and Artifact Manifests (`outputs.manifests`)

Automatic command and artifact manifests are both opt-in with `outputs.manifests.emit: true`
(default: `false`). Both also require `outputs.emit` to be true (default). Command manifests use
`outputs.manifests.path` (default `~/manifests`, which resolves under `outputs.root`).
Artifact manifests retain their existing location at `outputs.root/manifest.json`, including
when a push is skipped or fails. Disabling persistence does not remove artifact entries or push
decisions from in-memory command results, JSON output, or CI emission.
Setting `outputs.emit: false` disables both automatic manifest types along with other automatic
runtime output files. Neither switch disables an explicitly requested `--json-file` result or
its run-manifest sidecar.

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
    "emit": true,
    "path": "~/manifests",
    "commandMode": "perCommand",
    "commandDetail": "verbose"
  }
}
```

Repositories upgrading from releases that wrote command or artifact manifests automatically
should set `outputs.manifests.emit: true` to retain that behavior. Workflows that relied on eager
directory creation must also set `outputs.precreateDirectories: true`. Direct `--json-file`
output and its sidecar remain available regardless of these settings.

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
  "ArtifactsAnyPushed": false,
  "ArtifactsPushedCount": 0,
  "ArtifactsAllPushed": false,
  "PolicyLockHash": "sha256...",
  "Warnings": [],
  "Errors": [],
  "ConfigHash": "sha256...",
  "AssemblyVersion": "1.2.3.0",
  "InformationalVersion": "1.2.3",
  "NuGetVersion": "1.2.3"
}
```

`ConfigHash` is calculated from the effective configuration using canonical JSON ordering, so
equivalent YAML and JSON configurations produce the same value. Credential-like fields are
omitted recursively before hashing; secret declarations and provider references remain part of
the identity, but the hash does not attest to resolved secret values.

`PolicyLockHash` is the SHA-256 of `.rexo/rexo.lock.yaml` when present. Build-artifact entries
include `Location` and `ContentSha256` when the provider produced a local file that Rexo can hash.
`rx promote <run-manifest.json> <environment>` uses this exact local artifact identity and refuses
to rebuild, overwrite different bytes, or promote provider outputs without a verified file hash.
The manifest does not currently generate SBOMs or cryptographic provenance attestations; registry
outputs without a provider-reported immutable digest are not promoted as if they were verified.

### Notes

- Sidecar includes repository and CI context, timing, config hash, and normalized step timing (`DurationMs`).
- `Artifacts`/`PushDecisions` follow the same population rules as `CommandResult`.
- `Version` follows the same population rule: populated only if version metadata is produced during executed steps.

## Practical Scenarios

`rx release --json-file artifacts/runs/release.json`

- Build/verify lifecycle runs.
- Push steps are skipped unless `--push` is provided.
- `Artifacts` can contain build-only entries; `PushDecisions` is empty.
- Publication summaries are false/0/false.

`rx release --push --json-file artifacts/runs/release.json`

- Push phase runs.
- `Artifacts` and `PushDecisions` are populated.
- If policy blocks push, entries are present with deny reasons and `Pushed=false`.
