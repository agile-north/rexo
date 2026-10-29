# CI/CD Emission Reference

This is the reference for humans and AI agents implementing CI/CD workflows with Rexo:
which variables exist, when they are available, and how to consume them. Do not infer variable
names from command names or treat successful execution as proof of publication.

`CiOutputEmitter` turns a `RunManifest` into CI-friendly variables and provider-specific syntax.
It is used after command execution and to build the environment for shell/container run steps.

## Emission Channels and Policies

These are separate controls, not interchangeable emission policies:

| Channel / policy | Default | Emitted data / destination |
| --- | --- | --- |
| Post-command CI variables: `outputs.ci.emit` | `true`, including when the block is omitted | The safe/full catalog below, through the selected CI provider. `auto` skips outside detected CI. |
| Safe profile: `outputs.ci.scope: safe` | Default profile | Curated run metadata, counts, publication summary, version fields; optional named step file outputs. |
| Full profile: `outputs.ci.scope: full` | Opt-in | Every serialized `RunManifest` leaf, including indexed step/artifact/decision details. Not a superset of safe's synthetic counts or named step paths. |
| Filtered profile: `outputs.ci.scope: {mode, include, exclude}` | Opt-in | Filters fields from the chosen profile before prefixing/casing. |
| Run-step environment | Always supplied to `run` steps | An internal safe snapshot of completed steps; independent of `outputs.ci.emit`. See [Run-Step Variables](#run-step-variables). |
| Command stdout: `outputs.command.stdout` / `outputs.command.json` | Human output enabled, JSON disabled | Human result or `CommandResult` JSON; `--json` selects JSON and `--quiet` suppresses human rendering. |
| Explicit JSON file: `--json-file` / `outputs.command.jsonFile` | No file | `CommandResult` plus a sidecar `RunManifest`; independent of automatic-file permission. |
| Automatic files: `outputs.emit` | `true` | Master permission for automatic runtime files; does not disable CI variables, run-step variables, or explicit JSON files. |
| Automatic manifests: `outputs.manifests.emit` | `false` | Requires `outputs.emit` too. Artifact manifest and command summary/verbose files, described in [Output Contract](output-contract.md#automatic-command-and-artifact-manifests-outputsmanifests). |

Embedded policies compose commands and configuration; they do not define a separate CI-variable
namespace. Effective `outputs.ci` settings select the emission profile. Use `rx config resolved`
to inspect those settings. For JSON payloads and file locations, see [Output Contract](output-contract.md).

## What It Emits

The emitter walks a manifest and flattens it into `path -> value` pairs.

- `safe` mode emits a curated summary of run metadata, counts, version fields, and optional step file outputs.
- `full` mode flattens the entire manifest recursively.
- Include and exclude masks match canonical field paths and accept exact values, wildcards, or `regex:...` patterns.
- Empty or null values are skipped by default.
- Duplicate keys are kept by adding `_2`, `_3`, and so on.
- Sensitive values are redacted by default for post-command CI emission.

## Configuration Defaults

The `outputs.ci` block is optional, but when present these are the runtime defaults:

| Setting | Default | Notes |
| --- | --- | --- |
| `emit` | `true` | Enables post-command CI-native emission. |
| `provider` | `auto` | Resolves to the detected CI provider, or skips outside CI. |
| `prefix` | `REXO_` | Prefix applied to emitted variable names. |
| `keyCasing` | `upperSnake` | Controls how manifest paths become variable keys. |
| `scope` | `safe` | Use `safe`, `full`, or an object with `mode`, `include`, and `exclude`. |
| `includeStepOutputs` | `false` | Adds step file output counts and indexes to safe-mode emission. |
| `emitEmptyValues` | `false` | Empty or null values are omitted unless enabled. |
| `redact` | `true` | Redacts sensitive-looking values before emission. |
| `failOnError` | `false` | Emits a warning instead of failing the command. |
| `maxValueLength` | `8192` | Truncates emitted values after this length. |
| `maxVariables` | `1000` | Stops emission after this many variables. |
| `github-actions.scope` | `env` | Chooses `GITHUB_ENV`, `GITHUB_OUTPUT`, or `GITHUB_STATE`. |

## Complete Safe-Scope Catalog

The following tables list every static safe-scope field. Keys assume the default `REXO_` prefix
and `upperSnake` casing. Booleans are the strings `true` / `false`; counts are decimal strings.
Unset/null/empty values are omitted unless `emitEmptyValues` is true. Zero and false are **not**
empty, so publication summaries are emitted even when nothing was published, unless filtered
or excluded by the variable limit.

### Run, repository, and CI fields

| Source path | Default variable | Value |
| --- | --- | --- |
| `schemaVersion` | `REXO_SCHEMA_VERSION` | Manifest schema version (`1.0`). |
| `toolVersion` | `REXO_TOOL_VERSION` | Rexo tool version. |
| `repoName` | `REXO_REPO_NAME` | Repository-root directory name. |
| `repoRoot` | `REXO_REPO_ROOT` | Absolute repository root. |
| `branch` | `REXO_BRANCH` | Detected branch. |
| `commitSha` | `REXO_COMMIT_SHA` | Detected commit SHA. |
| `remoteUrl` | `REXO_REMOTE_URL` | Git remote URL. |
| `ciProvider` | `REXO_CI_PROVIDER` | Detected CI provider. |
| `isCi` | `REXO_IS_CI` | Whether CI was detected. |
| `ciBuildId` | `REXO_CI_BUILD_ID` | Provider build ID. |
| `ciRunNumber` | `REXO_CI_RUN_NUMBER` | Provider run number. |
| `ciWorkflowName` | `REXO_CI_WORKFLOW_NAME` | Provider workflow name. |
| `ciActor` | `REXO_CI_ACTOR` | Provider actor. |
| `ciTag` | `REXO_CI_TAG` | Provider tag, when supplied. |
| `ciBuildUrl` | `REXO_CI_BUILD_URL` | Provider build URL. |
| `commandExecuted` | `REXO_COMMAND_EXECUTED` | Command executed. |
| `environment` | `REXO_ENVIRONMENT` | Selected execution environment. |
| `environmentSelectionSource` | `REXO_ENVIRONMENT_SELECTION_SOURCE` | CLI, environment-variable, branch, or unresolved selection source. |
| `sourceBranch` | `REXO_SOURCE_BRANCH` | Normalized source branch used for automatic selection. |
| `sourceBranchOrigin` | `REXO_SOURCE_BRANCH_ORIGIN` | Source of branch metadata. |
| `matchedBranchPattern` | `REXO_MATCHED_BRANCH_PATTERN` | Pattern that matched the selected environment. |
| `environmentSelectionExplicit` | `REXO_ENVIRONMENT_SELECTION_EXPLICIT` | Whether the environment was explicitly selected. |
| `environmentSelectionOverrodeAutomaticMatches` | `REXO_ENVIRONMENT_SELECTION_OVERRODE_AUTOMATIC_MATCHES` | Whether an explicit choice overrode another automatic match. |
| `success` | `REXO_SUCCESS` | Overall command success, including successful no-ops. |
| `exitCode` | `REXO_EXIT_CODE` | Command exit code. |
| `startedAt` | `REXO_STARTED_AT` | Round-trip timestamp. |
| `completedAt` | `REXO_COMPLETED_AT` | Round-trip timestamp. |
| `durationMs` | `REXO_DURATION_MS` | Elapsed milliseconds. |
| `configHash` | `REXO_CONFIG_HASH` | Hash of effective, secret-redacted configuration. |
| `assemblyVersion` | `REXO_ASSEMBLY_VERSION` | Resolved assembly version. |
| `informationalVersion` | `REXO_INFORMATIONAL_VERSION` | Resolved informational version. |
| `nugetVersion` | `REXO_NUGET_VERSION` | Resolved NuGet version. |

CI metadata depends on the detected provider; not every provider supplies every field.

### Counts and publication outcomes

| Source path | Default variable | Value |
| --- | --- | --- |
| `stepsCount` | `REXO_STEPS_COUNT` | Number of manifest step entries. |
| `stepsSucceeded` | `REXO_STEPS_SUCCEEDED` | Number of successful step entries. |
| `stepsFailed` | `REXO_STEPS_FAILED` | Number of failed step entries. |
| `artifactsCount` | `REXO_ARTIFACTS_COUNT` | Number of artifact entries, including build-only, skipped, or duplicate lifecycle entries; not a publication count. |
| `artifactsAnyPushed` | `REXO_ARTIFACTS_ANY_PUSHED` | At least one distinct artifact was actually published. |
| `artifactsPushedCount` | `REXO_ARTIFACTS_PUSHED_COUNT` | Number of distinct artifacts actually published, not number of tags or provider calls. |
| `artifactsAllPushed` | `REXO_ARTIFACTS_ALL_PUSHED` | A nonempty publication set was completely published with no denied/failed/unprocessed decisions. |
| `pushDecisionsCount` | `REXO_PUSH_DECISIONS_COUNT` | Number of decisions, not successful pushes. |
| `warningsCount` | `REXO_WARNINGS_COUNT` | Number of manifest warnings. |
| `errorsCount` | `REXO_ERRORS_COUNT` | Number of manifest errors. |

Publication outcomes refer to this invocation, not registry availability from earlier runs.
Providers report publication through nonempty published references. Dry runs retain planned tags
but never set `Pushed=true`. An allowed provider no-op with no published references is not a push.
Duplicate build/nested-step entries are grouped by case-insensitive artifact type and name.
Push decisions identify the selected publication names; build-only artifacts outside that
selection do not prevent `ALL_PUSHED`. A name shared across artifact types is evaluated
conservatively across those types. Any denied/failed/unprocessed decision prevents `ALL_PUSHED`,
even if another artifact was published. An empty or skipped push phase produces false/0/false.
No aggregate `REXO_PUSH_SUCCESS` variable exists.

Command and publication success are independent: a later unrelated step can fail after all
artifacts have been published. Always combine command success with the publication outcome for
a release gate.

### Version fields

These fields appear only after version resolution. Nullable individual values follow
`emitEmptyValues`; the entire version group is absent in safe scope when no version was resolved.

| Source path | Default variable |
| --- | --- |
| `version.semVer` | `REXO_VERSION_SEM_VER` |
| `version.major` | `REXO_VERSION_MAJOR` |
| `version.minor` | `REXO_VERSION_MINOR` |
| `version.patch` | `REXO_VERSION_PATCH` |
| `version.preRelease` | `REXO_VERSION_PRE_RELEASE` |
| `version.commitSha` | `REXO_VERSION_COMMIT_SHA` |
| `version.shortSha` | `REXO_VERSION_SHORT_SHA` |
| `version.isPreRelease` | `REXO_VERSION_IS_PRE_RELEASE` |
| `version.isStable` | `REXO_VERSION_IS_STABLE` |
| `version.buildMetadata` | `REXO_VERSION_BUILD_METADATA` |
| `version.branch` | `REXO_VERSION_BRANCH` |
| `version.nugetVersion` | `REXO_VERSION_NUGET_VERSION` |
| `version.dockerVersion` | `REXO_VERSION_DOCKER_VERSION` |
| `version.preReleaseTag` | `REXO_VERSION_PRE_RELEASE_TAG` |
| `version.preReleaseLabel` | `REXO_VERSION_PRE_RELEASE_LABEL` |
| `version.preReleaseNumber` | `REXO_VERSION_PRE_RELEASE_NUMBER` |
| `version.preReleaseLabelWithDash` | `REXO_VERSION_PRE_RELEASE_LABEL_WITH_DASH` |
| `version.preReleaseTagWithDash` | `REXO_VERSION_PRE_RELEASE_TAG_WITH_DASH` |
| `version.assemblyVersion` | `REXO_VERSION_ASSEMBLY_VERSION` |
| `version.fileVersion` | `REXO_VERSION_FILE_VERSION` |
| `version.informationalVersion` | `REXO_VERSION_INFORMATIONAL_VERSION` |
| `version.commitsSinceVersionSource` | `REXO_VERSION_COMMITS_SINCE_VERSION_SOURCE` |
| `version.weightedPreReleaseNumber` | `REXO_VERSION_WEIGHTED_PRE_RELEASE_NUMBER` |

### Optional named step file outputs

With `includeStepOutputs: true`, safe scope additionally emits:

| Source path pattern | Default variable pattern |
| --- | --- |
| `steps.<stepId>.fileOutputs.<name>.count` | `REXO_STEPS_<STEP_ID>_FILE_OUTPUTS_<NAME>_COUNT` |
| `steps.<stepId>.fileOutputs.<name>[<index>]` | `REXO_STEPS_<STEP_ID>_FILE_OUTPUTS_<NAME>_<INDEX>` |

Indexes are zero-based. An existing empty file-output list emits a count of 0; no configured
file outputs means no such keys. This setting does **not** export arbitrary raw step outputs,
stdout, stderr, or `__artifacts` as individual variables.

## Complete Full-Scope Catalog

Full scope serializes `RunManifest` and recursively emits its leaf values. The three publication
summary variables are the same as safe scope. Other differences matter: full scope uses numeric
step indexes, not step IDs, and does not synthesize safe-scope counts or `DURATION_MS`.

| Full manifest fields | Default key shape / difference |
| --- | --- |
| `SchemaVersion`, `ToolVersion`, `RepoName`, `RepoRoot`, `Branch`, `CommitSha`, `RemoteUrl` | Same keys as their safe equivalents. |
| `CiProvider`, `IsCi`, `CiBuildId`, `CiRunNumber`, `CiWorkflowName`, `CiActor`, `CiTag`, `CiBuildUrl` | Same keys as their safe equivalents. |
| `CommandExecuted`, `Success`, `ExitCode`, `StartedAt`, `CompletedAt`, `ConfigHash`, `AssemblyVersion`, `InformationalVersion`, `nugetVersion` | Same keys as their safe equivalents. |
| `Duration` | `REXO_DURATION`: serialized TimeSpan string, not milliseconds. |
| `PolicyLockHash` | `REXO_POLICY_LOCK_HASH`: full-only policy-lock hash. |
| `ArtifactsAnyPushed`, `ArtifactsPushedCount`, `ArtifactsAllPushed` | `REXO_ARTIFACTS_ANY_PUSHED`, `REXO_ARTIFACTS_PUSHED_COUNT`, `REXO_ARTIFACTS_ALL_PUSHED`. |
| `Version.*` | All fields in the version table; same normalized keys. |
| `Steps[i].StepId`, `.Success`, `.ExitCode`, `.DurationMs` | `REXO_STEPS_<I>_STEP_ID`, `_SUCCESS`, `_EXIT_CODE`, `_DURATION_MS`. |
| `Steps[i].FileOutputs.<name>[j]` | `REXO_STEPS_<I>_FILE_OUTPUTS_<NAME>_<J>`; no synthesized list count. |
| `Steps[i].ExecutionMode`, `.RequestedExecutionMode` | `REXO_STEPS_<I>_EXECUTION_MODE`, `_REQUESTED_EXECUTION_MODE`. |
| `Steps[i].ContainerImage`, `.ContainerWorkingDirectory`, `.ContainerFallbackPolicy` | `REXO_STEPS_<I>_CONTAINER_IMAGE`, `_CONTAINER_WORKING_DIRECTORY`, `_CONTAINER_FALLBACK_POLICY`. |
| `Steps[i].ContainerFallbackAllowed`, `.ContainerFallbackUsed`, `.ContainerFallbackReason` | `REXO_STEPS_<I>_CONTAINER_FALLBACK_ALLOWED`, `_CONTAINER_FALLBACK_USED`, `_CONTAINER_FALLBACK_REASON`. |
| `Artifacts[i].Type`, `.Name`, `.Built`, `.Pushed` | `REXO_ARTIFACTS_<I>_TYPE`, `_NAME`, `_BUILT`, `_PUSHED`. |
| `Artifacts[i].Tags[j]` | `REXO_ARTIFACTS_<I>_TAGS_<J>`. |
| `Artifacts[i].ContentSha256`, `.Location` | `REXO_ARTIFACTS_<I>_CONTENT_SHA256`, `_LOCATION`. |
| `PushDecisions[i].ArtifactName`, `.Allowed`, `.Reason` | `REXO_PUSH_DECISIONS_<I>_ARTIFACT_NAME`, `_ALLOWED`, `_REASON`. |
| `Warnings[i]`, `Errors[i]` | `REXO_WARNINGS_<I>`, `REXO_ERRORS_<I>`. |

All indexes are zero-based. Empty objects/arrays emit no leaves, even with `emitEmptyValues`.
A null object such as unresolved `Version` has no children; with `emitEmptyValues` it instead
emits its parent key as an empty string. `includeStepOutputs` has no effect in full scope:
manifest file outputs are already included. Full scope still does not serialize raw `StepResult.Outputs`.

The JSON structure is defined by `RunManifest`, `StepManifestEntry`, `ArtifactManifestEntry`,
`PushDecision`, and `VersionResult` in `src/Core/Models`. Full scope can grow as those models
grow; prefer safe summary keys for stable workflow gates.

## Selectors, Naming, and Value Rules

- `scope: {mode: safe, include: [...], exclude: [...]}` filters safe fields.
- With a selector object and no `mode`, a nonempty `include` list selects **full** mode;
  otherwise safe mode is used. Specify `mode` explicitly for predictable workflows.
- Masks match canonical source paths, not prefixed environment names. Camel/Pascal words become
  lowercase underscore words, while dots and array brackets are retained:
  `artifactsAnyPushed` becomes `artifacts_any_pushed`;
  `Steps[0].StepId` becomes `steps[0].step_id`.
- Exact masks, `*` / `?` wildcards, and case-insensitive `regex:` masks are supported.
  Excludes win. For example, `include: ["success", "artifacts_*"]` selects safe publication
  summaries; `include: ["artifacts[*].pushed"]` selects full per-artifact flags.
- `prefix` and `keyCasing` are applied after filtering. Available casing styles are
  `upperSnake`, `lowerSnake`, `kebab`, `camel`, and `pascal`; see [CI Key Formatting](output-contract.md#ci-key-formatting).
- Collisions append `_2`, `_3`, etc. and warn; avoid step/output names that normalize identically.
- Redaction is a value-based heuristic, not a guarantee that full scope contains no secrets.
  Values containing sensitive-looking words can become `***`. Do not emit secrets deliberately.
- Values are truncated to `maxValueLength`; reaching `maxVariables` stops emission and warns.
  Keep a small safe profile for gates so limits cannot omit required outputs.

## Provider Behavior

For test and coverage display, prefer the importable [GitHub reporting
policies](../embedded/github-reports.md). They publish Checks/summaries directly;
CI variable emission alone does not render test reports or coverage in GitHub.

`outputs.ci.provider` controls the stdout syntax used when Rexo emits variables after a command.

| Provider | Output shape |
| --- | --- |
| `generic` or any unknown value | `KEY=VALUE` |
| `github-actions` | `KEY=VALUE` by stdout, or GitHub file output when `GITHUB_ENV`, `GITHUB_OUTPUT`, or `GITHUB_STATE` is available |
| `azure-devops` | `##vso[task.setvariable variable=KEY]VALUE` |
| `teamcity` | `##teamcity[setParameter name='env.KEY' value='VALUE']` |
| GitLab CI / Bitbucket Pipelines detected with `provider: auto` | `KEY=VALUE` |

Behavior notes:

- `provider: auto` uses the detected CI provider.
- The schema accepts explicit values `auto`, `github-actions`, `azure-devops`, `teamcity`, and `generic`. GitLab CI and Bitbucket Pipelines are supported through auto-detection and use generic `KEY=VALUE` output.
- When `provider: auto` is used outside CI, post-command CI emission is skipped.
- A non-`auto` provider can be forced locally for testing.
- When GitHub file output is configured but the target environment variable is missing, Rexo falls back to stdout and warns.
- GitHub `env` variables are available to later steps in the same job, not automatically to
  other jobs. `output` creates outputs on the invoking step; declare an `id` and map job outputs
  explicitly. `state` is action state, not a general release-stage output.
- Azure DevOps currently emits ordinary task variables, **not** `isOutput=true` variables.
  Cross-job/stage use needs an explicit pipeline adapter; do not assume a `dependencies` output exists.
- TeamCity emits `env.*` build parameters. Generic stdout, including GitLab/Bitbucket auto-detection,
  is not a persisted dotenv artifact; the workflow must capture/transfer it explicitly.

## Run-Step Variables

Run steps always receive an internal `REXO_*` variable block built from the same manifest contract.

That internal profile uses fixed defaults:

- Provider: `generic`
- Prefix: `REXO_` unless `outputs.ci.prefix` overrides it
- Key casing: `upperSnake`
- Include step outputs: `true`
- Emit empty values: `true`
- Redact: `false`
- Max value length: `8192`
- Max variables: `1000`

This internal step environment is independent from `outputs.ci.emit`.
It always uses the safe profile, regardless of the configured post-command scope, casing, or
provider. Values reflect completed steps **before** the current run step, not the final outcome
of the command. Timing is a zero-duration snapshot; tool/config/top-level version metadata not
copied into that snapshot is empty. Nested `version.*` is available after version resolution.
The current internal step projection does not copy file-output lists, so the
`includeStepOutputs: true` internal setting does not produce named file-output variables.
Use [template step outputs](templates.md) or post-command emission for those lists.

The publication summaries are available here too. Templates expose equivalent
`{{push.anyPushed}}`, `{{push.pushedCount}}`, and `{{push.allPushed}}` values; these are template
expressions, not separately named CI variables.

## Release Gating Recipes

For GitHub release/tag creation itself, prefer [embedded:github-release](../embedded/github-release.md)
instead of a workflow script. It runs after the standard lifecycle, uses internal outcome
gates, and works without CI emission. The recipes below are for external downstream actions.

Use `SUCCESS` **and** `ARTIFACTS_ALL_PUSHED` for a release representing the complete publication
set. Use `ARTIFACTS_ANY_PUSHED` only for intentionally partial-publication notifications/actions.

| Invocation outcome | `SUCCESS` | `ARTIFACTS_ANY_PUSHED` | `ARTIFACTS_PUSHED_COUNT` | `ARTIFACTS_ALL_PUSHED` |
| --- | --- | --- | --- | --- |
| No artifacts / push not run | `true` | `false` | `0` | `false` |
| Dry run | `true` | `false` | `0` | `false` |
| Confirmation missing / all pushes policy-blocked | `true` | `false` | `0` | `false` |
| One published, another policy-blocked or provider-skipped | `true` | `true` | `1` | `false` |
| One published, next failed, remainder not attempted | `false` | `true` | `1` | `false` |
| Both selected artifacts published | `true` | `true` | `2` | `true` |
| Both published, later unrelated step failed | `false` | `true` | `2` | `true` |

### GitHub Actions: same job or downstream job

Repository configuration (merge into an existing config):

```yaml
outputs:
  ci:
    emit: true
    scope: safe
    github-actions:
      scope: output
```

Workflow excerpt (assumes checkout and Rexo installation):

```yaml
jobs:
  publish:
    runs-on: ubuntu-latest
    outputs:
      success: ${{ steps.publish.outputs.REXO_SUCCESS }}
      all_pushed: ${{ steps.publish.outputs.REXO_ARTIFACTS_ALL_PUSHED }}
    steps:
      # Checkout, install rx, and configure registry authentication before this step.
      - name: Publish artifacts
        id: publish
        run: rx release --push
      - name: Same-job release action
        if: >-
          ${{ success()
              && steps.publish.outputs.REXO_SUCCESS == 'true'
              && steps.publish.outputs.REXO_ARTIFACTS_ALL_PUSHED == 'true' }}
        run: echo "The complete artifact set was published."

  release:
    needs: publish
    runs-on: ubuntu-latest
    if: >-
      ${{ needs.publish.result == 'success'
          && needs.publish.outputs.success == 'true'
          && needs.publish.outputs.all_pushed == 'true' }}
    steps:
      - run: echo "Create the release here."
```

These examples assume the standard lifecycle policy supplies `release`; custom commands may
use the same variables. Outputs are strings: compare with `'true'`, not boolean truthiness.
Missing outputs fail these comparisons closed. Keep `success()` / job-result checks even when
using `continue-on-error`; publication flags alone do not establish overall command success.

### Azure DevOps: later step in the same job

With `provider: auto` on Azure DevOps and safe scope, a later task can use:

```yaml
- script: rx release --push
  displayName: Publish artifacts
- script: echo "Create the release here."
  condition: and(succeeded(), eq(variables['REXO_SUCCESS'], 'true'), eq(variables['REXO_ARTIFACTS_ALL_PUSHED'], 'true'))
```

For downstream jobs/stages, add an explicit adapter that re-exports these values with
`isOutput=true`; Rexo's current Azure dialect does not do that automatically.

## Example

```jsonc
{
  "outputs": {
    "ci": {
      "emit": true,
      "provider": "github-actions",
      "prefix": "CI_",
      "keyCasing": "upperSnake",
      "includeStepOutputs": true,
      "scope": "safe",
      "github-actions": {
        "scope": "output"
      }
    }
  }
}
```

Given a manifest like this:

- `repoName`: `orders-api`
- `exitCode`: `0`
- `success`: `true`
- `durationMs`: `1250`
- `version.semVer`: `1.2.3`
- `steps.pack.fileOutputs.packages`: `['artifacts/pkg-a.nupkg', 'artifacts/pkg-b.nupkg']`

With `prefix: "CI_"`, `keyCasing: "upperSnake"`, and `scope: "safe"`, the emitted values look like this:

| Manifest path | Emitted key | Example value |
| --- | --- | --- |
| `repoName` | `CI_REPO_NAME` | `orders-api` |
| `exitCode` | `CI_EXIT_CODE` | `0` |
| `success` | `CI_SUCCESS` | `true` |
| `durationMs` | `CI_DURATION_MS` | `1250` |
| `version.semVer` | `CI_VERSION_SEM_VER` | `1.2.3` |
| `steps.pack.fileOutputs.packages.count` | `CI_STEPS_PACK_FILE_OUTPUTS_PACKAGES_COUNT` | `2` |
| `steps.pack.fileOutputs.packages[0]` | `CI_STEPS_PACK_FILE_OUTPUTS_PACKAGES_0` | `artifacts/pkg-a.nupkg` |
| `steps.pack.fileOutputs.packages[1]` | `CI_STEPS_PACK_FILE_OUTPUTS_PACKAGES_1` | `artifacts/pkg-b.nupkg` |

If `scope` is changed to `full`, the same prefixing and key-casing rules apply, but every manifest field is flattened.

Examples of provider-specific stdout formatting for the same `CI_REPO_NAME=orders-api` value:

```text
CI_REPO_NAME=orders-api
```

```text
##vso[task.setvariable variable=CI_REPO_NAME]orders-api
```

```text
##teamcity[setParameter name='env.CI_REPO_NAME' value='orders-api']
```

## When To Use Full Scope

Use `scope: "full"` when you want every manifest field flattened into CI variables.

Use `scope: "safe"` when you want a compact, stable summary for pipelines and log output.
