# Artifact Lifecycle Builtins

Builtins for building, tagging, and pushing artifacts with planning and policy gates.

## Artifact groups

Each configured artifact may have one top-level `group` string. Artifact groups scope
planning, provider build/tag/push calls, manifests, and verified handoff inventories; they do
not create independent version providers or change repository verification.

Artifacts without `group` belong to the implicit `default` group:

```yaml
artifacts:
  - type: nuget
    name: Acme.Runtime
    settings:
      project: src/runtime/Acme.Runtime.csproj
  - type: nuget
    name: Acme.Abstractions
    group: contracts
    settings:
      project: src/abstractions/Acme.Abstractions.csproj
  - type: nuget
    name: Acme.DependencyInjection
    group: contracts
    settings:
      project: src/dependency-injection/Acme.DependencyInjection.csproj
```

This is useful when an API/abstractions package has a different publication cadence from the
runtime packages that implement it. For example, downstream plugin authors may need the updated
contracts as soon as the public interfaces change, while runtime packages do not need to be
repacked and re-published for that contracts-only release. Conversely, runtime-only fixes can
ship without publishing unchanged contracts packages.

```powershell
# Review only the contracts lane, then publish it after repository verification.
rx plan --artifact-group contracts --push
rx release --artifact-group contracts --push

# For a coordinated release, include the default and every named artifact group.
rx release --all-artifact-groups --push
```

With this example, the default commands select `Acme.Runtime`; the contracts command selects
`Acme.Abstractions` and `Acme.DependencyInjection`; all-groups selects all three. These lanes
share Rexo's resolved repository version: grouping controls which artifacts participate, not
their version or repository verification.

The standard `rx build`, `rx tag`, `rx push`, `rx plan`, and `rx release` commands select
ungrouped artifacts by default. `--artifact-group <name>` selects only that named group, while
`--all-artifact-groups` includes every configured artifact, grouped and ungrouped. The selectors
cannot be combined. Matching is case-insensitive. Group names use ASCII letters, digits, `.`,
`_`, or `-`; `default` is reserved for ungrouped artifacts.

An unknown group or an empty default group in a repository with configured artifacts fails
with the available groups instead of succeeding with an empty lifecycle. Repositories with no
artifacts retain the existing empty-artifact behavior. Source verification, tests, build hooks,
and repository-defined commands continue to run normally; hooks can inspect
`{{options.artifact-group}}` or `{{options.all-artifact-groups}}` if they need group-specific
behavior.

## builtin:plan-artifacts

Purpose:

- Produce human-readable plan and structured JSON model for matching artifacts.

Calls:

- Internal planning logic only (no provider build/tag/push calls)

Inputs:

- Artifact selection predicate (caller-provided)
- Optional `ctx.Options.artifact-group` or `ctx.Options.all-artifact-groups` selector (composed with the caller predicate)
- Context version/branch/commit/PR/clean-tree flags
- Option `push` (typically mapped via `with`) to indicate push intent

Outputs:

- `message`
- `plan` (JSON string with `Repo`, `Version`, `Artifacts`, `ArtifactGroup`, and `Push` sections)
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
- Writes `<outputs.root>/manifest.json` when `outputs.emit=true` (default)

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
not an external lifecycle script. A consumer can place it before sealing in a multi-stage
pipeline; this repository does not use it in its normal single-job lifecycle.

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

Consumers can configure separate seal/publish stages with these primitives.
This repository uses the normal single-job release lifecycle instead.
NuGet packages (including exact-path symbols) and generic
archives implement the capability. Both use shared repository-contained file hashes,
reject symbolic-link traversal, and publish only their validated outputs.
Unsupported providers fail explicitly rather than falling back to build or discovery.
Remote artifact providers can implement their own immutable reference validation and
publication through the same interface; Docker/OCI support is not implemented here.
The receipt records the selected artifact group as well as the exact artifact inventory, and
publication must use the same group. It is integrity evidence, not a signature or remote
promotion/deployment record.
Do not consume a receipt from an untrusted PR run in a privileged release run.
