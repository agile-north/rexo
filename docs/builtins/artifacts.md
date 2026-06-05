# Artifact Lifecycle Builtins

Builtins for building, tagging, and pushing artifacts with planning and policy gates.

## builtin:plan-artifacts

Purpose:

- Produce human-readable plan and structured JSON model for matching artifacts.

Calls:

- Internal planning logic only (no provider build/tag/push calls)

Inputs:

- Artifact selection predicate (caller-provided)
- Context version/branch/commit/PR/clean-tree flags
- Option `push` (typically mapped via `with`) to indicate push intent

Outputs:

- `message`
- `plan` (JSON string with `repo`, `version`, `artifacts`, and `push` sections)
- `pushRequested` (`bool`)
- `canPush` (`bool`)
- `skipReasons` (`string[]`)

Structured `plan` payload includes, per artifact:

- build settings (for example `image`, `dockerfile`, `context`, `project`, `source`)
- planned tags
- expected output references
- required credential hints
- per-artifact push requested/eligible state and skip reasons

Exit behavior:

- Success: exit code `0`
- No artifacts: success with informative message

## builtin:build-artifacts

Purpose:

- Build matching artifacts via provider implementations.

Calls:

- For each artifact: provider `BuildAsync(...)`

Inputs:

- `config.Artifacts`
- Context (includes resolved version for tagging/build args where providers use it)

Outputs:

- `message`

Exit behavior:

- Success: exit code `0`
- Build failure: exit code `5`

## builtin:tag-artifacts

Purpose:

- Tag matching artifacts via provider implementations.

Calls:

- For each artifact: provider `TagAsync(...)`

Inputs:

- `config.Artifacts`
- Context (version/branch metadata)

Outputs:

- `message`

Exit behavior:

- Success: exit code `0`
- Provider errors may bubble and fail execution

## builtin:push-artifacts

Purpose:

- Push matching artifacts with confirmation and push-policy gates.
- Simulate push when dry-run is enabled.

Calls:

- Parse global push rules from `config.runtime.push`
- Resolve dry-run from CLI/config runtime settings
- Merge per-artifact push overrides from artifact settings
- Enforce local explicit confirmation (`confirm`/`push` option)
- For allowed artifacts: provider `PushAsync(...)`
- Writes `<runtime.output.root>/manifest.json` when `runtime.output.emitRuntimeFiles=true` (default)

Dry-run changes the provider call path: the builtin still evaluates push decisions and
produces manifest output, but it skips external push operations and marks artifacts as
successfully simulated.

Inputs:

- `ctx.Options.confirm` and/or `ctx.Options.push`
- `ctx.Options.dry-run`
- CI context (`ctx.IsCi`)
- Global push rules (`runtime.push`)
- Per-artifact settings:
  - `push.enabled`, `push.noPushInPullRequest`, `push.requireCleanWorkingTree`, `push.branches`
  - legacy synonyms (`pushEnabled`, `pushBranches`, etc.)

Outputs:

- `message`
- `__artifacts` (`ArtifactManifestEntry[]`)
- `__pushDecisions` (`PushDecision[]`)
- On failure: `error`

Exit behavior:

- Local, not confirmed: success `0`, push skipped with guidance
- Policy-gated skip: success `0` with decision reasons
- Dry-run: success `0`, no provider calls, simulated push output only
- Provider push failure: exit code `6`
