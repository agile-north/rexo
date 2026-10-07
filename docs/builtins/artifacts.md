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
## Verified artifact handoff

`builtin:validate-release-evidence` checks configured `with.runManifest`, `result`,
`tests`, `sarif`, `archive` and `entry` inputs. It requires successful result/manifest
outputs, fresh TRX/coverage/SARIF/package files since the release started, and an archive
entry without unresolved template tokens. Paths and entry names belong in config,
not an external lifecycle script. It runs before sealing in this repository's `ci handoff`.

`builtin:seal-artifact-handoff` accepts `with.runManifest` and `with.path` as portable,
repository-relative paths. It requires a successful, unpublished `release` run with
the same commit, effective config hash, policy lock hash, resolved version and CI run
identity. Providers describe their prepared outputs through `IPreparedArtifactProvider`;
the receipt records artifact type/name and each output's kind, reference and identity.

`builtin:push-artifact-handoff` accepts `with.path`, requires `--confirm`, verifies the
entire inventory before pushing, and uses existing provider and push-policy gates.
Missing/changed outputs, identity mismatches and denied pushes fail explicitly.
Global `--dry-run` verifies evidence and simulates publication without calling providers.
It does not invoke build/test/pack. Configured post-push hooks must be gated separately.

The repository demonstrates these primitives through `rx ci handoff` and
`rx ci publish --confirm`. NuGet packages (including exact-path symbols) and generic
archives implement the capability. Both use shared repository-contained file hashes,
reject symbolic-link traversal, and publish only their validated outputs.
Unsupported providers fail explicitly rather than falling back to build or discovery.
Remote artifact providers can implement their own immutable reference validation and
publication through the same interface; Docker/OCI support is not implemented here.
The receipt is integrity evidence, not a signature or remote promotion/deployment record.
Do not consume a receipt from an untrusted PR run in a privileged release run.
