# Rexo Implementation Checklist

Last updated: 2026-10-07

This long-lived checklist records implementation against the historical product scope in
`docs/scope.md`. It is not a current completion claim; some sections retain the original `repo`
terminology and counts. The production-hardening tranche has its own current status summary below.

Legend:

- [x] Done
- [ ] Not done
- [~] Partial / implemented in basic form only

## Current Production-Hardening Tranche

| Workstream | Status | Current boundary |
| --- | --- | --- |
| Production execution safety | Delivered with documented limits | Container execution fails closed by default and output is redacted. Built-in push, promotion, policy-lock update, CI scaffolding, and config materialization honor dry-run; arbitrary configured `run` steps still execute unless they inspect `options.dry-run`. NuGet keys are still passed to `dotnet nuget push` as process arguments. |
| Release self-hosting | Delivered | The workflow builds/tests/packs from the checked-out source via an isolated bootstrap. External publish remains explicitly gated and was not used for acceptance. |
| Init lifecycle | Delivered | `rx init --force` removes only superseded same-slot `.rexo` variants and preserves root/legacy candidates. |
| Readiness diagnostics | Delivered within non-mutating scope | `rx check` reports configuration, provider/tool availability, auto/env version readiness, artifact source paths, policy-lock coverage/validity, and push/credential limitations. It does not resolve configured secret providers or contact registries. |
| Config identity and explain | Delivered with attribution limits | Canonical secret-redacted config/lock hashes are recorded; explain lists repository files declaring the requested property (including local `extends`/overlay) plus policy/CLI layers, but does not compute field-level merge ownership, source locations, or individual policy-file attribution. |
| CLI workflow UX | Delivered | Graph, shell completion, color controls, global non-interactive behavior, and actionable initialization errors are implemented. |
| Policy lockfile | Delivered | `rx update` refreshes content hashes; `rx restore` verifies complete lock coverage; normal source failures are surfaced. |
| Artifact provenance | Delivered for available metadata | Run/CI metadata and local artifact SHA-256 are recorded. SBOMs, signed attestations, and provider-reported registry digests require a separate tooling/provider capability tranche. |
| Artifact promotion | Delivered for local files | One verified local file can be copied immutably into a repository-relative environment. Remote deployment and registry-tag promotion are not implemented without a selected deployment/provider contract. |
| Extension design | Delivered as an architecture decision | Arbitrary in-process plugin loading is rejected; any future extension contract must be out-of-process and capability-limited. |
| Documentation and acceptance | Delivered for this worktree | User/developer docs, local Markdown-link tests, workflow YAML parsing, Release build/tests, and local package README generation are verified on Windows. No cross-platform run or upstream SchemaStore change was performed. |

### Explicitly deferred capabilities

These are recorded decisions, not silently successful implementations:

- **NuGet API-key process visibility:** `dotnet nuget push` requires the key as an argument in the current provider. Output is masked, but same-user process inspection may expose it. CI guidance requires a dedicated runner identity; replacing this safely requires an explicit credential-provider/feed-auth contract.
- **Arbitrary command dry-run isolation:** `--dry-run` is not a sandbox for repository-authored shell steps. Such steps must branch on `options.dry-run`; enforcing isolation would require a separate command execution contract.
- **Remote deployment and registry promotion:** the implemented promotion contract verifies and copies a local file artifact only. No deployment target/provider or registry immutable identity contract was selected, so the CLI refuses to imply remote deployment or tag immutability.
- **SBOMs and signed attestations:** these require external generator/signing tools and provider-specific subject identity. The run manifest records available build/CI/config/lock/local-file metadata but does not claim an SBOM or cryptographic attestation.
- **SchemaStore registration:** repository schemas currently use JSON Schema 2020-12 and strict unknown-property rejection. SchemaStore's contribution guidance recommends draft-07 and cautions against blanket `additionalProperties: false`; an upstream catalog change should follow a compatibility review and dedicated positive/negative schema tests. YAML modelines and the canonical raw schema URLs remain available now.
- **Platform matrix:** acceptance was run on Windows only. Linux/macOS CI execution remains an external follow-up; the checked-in workflow YAML is parsed by an integration test, and no external publication or deployment was used.

Final local acceptance for this tranche: `dotnet build solution.slnx -c Release --no-restore` (0 warnings, 0 errors), `dotnet test solution.slnx -c Release --no-build --no-restore` (555 passed), `git diff --check` (clean), and `dotnet pack src\Cli\Cli.csproj -c Release --no-restore` (local package created; packaged generated README verified). No package was published.

## 1) CLI Surface and Routing

- [x] `repo version`
- [x] `repo help`
- [x] `repo doctor`
- [x] `repo list`
- [x] `repo explain <command>`
- [x] `repo run <command>`
- [x] Direct configured command dispatch (`repo <configured-command>`)
- [x] Multi-word command resolution (`repo branch feature my-change`)
- [x] Global flags: `--json`, `--json-file`, `--verbose`
- [x] Global flags: `--debug`, `--quiet`
- [x] Exit code mapping for common failures

## 2) Configuration Engine

- [x] Load `repo.json`
- [x] Config model includes commands, aliases, args, options, steps
- [x] Config model includes versioning, artifacts, tests, analysis
- [x] Validation: full JSON Schema validation via NJsonSchema (`ValidateSchemaAsync`)
- [x] Resolve `extends` from local file paths (breadth-first merge, circular detection)
- [x] Config merge order pipeline (defaults -> policies -> repo -> overlays -> CLI)
- [x] Environment overlays (REXO_OVERLAY env var)
- [x] Merge strategy customization for arrays/objects
- [x] Alternative config file names (`repo.yaml`, `.repo/repo.json`, `.repo/repo.yaml`)
- [x] YAML/JSON parity: YAML 1.2 core-schema typing, modeline `$schema`, duplicate-key/line-numbered errors
- [x] Default config location `.rexo/rexo.yaml` (root still supported), shadowed-file warnings
- [x] `rx init --format yaml|json` (YAML default) for config and policy

## 3) Command Resolution Order

- [x] Built-in command match
- [x] Exact config command match
- [x] Config alias match
- [x] Policy-provided command match (loads policy.json alongside repo.json)
- [x] Not-found suggestions (Levenshtein distance suggestion engine with structured error code)

## 4) Execution Engine

- [x] Sequential step execution
- [x] Step types: `run`, `uses`, `command`
- [x] `when` condition evaluation (truthy/falsey rendering)
- [x] Continue-on-error support (`continueOnError`)
- [x] Step output propagation into execution context
- [x] `parallel` step groups (consecutive parallel steps batched via Task.WhenAll)
- [x] Output capture via `outputPattern` (regex named groups) and `outputFile` (write stdout to file)
- [x] Command-level parallel settings (`parallel`, `maxParallel`) — SemaphoreSlim concurrency cap
- [x] Advanced dependency and fan-in behavior for parallel groups

## 5) Templating

- [x] Variable replacement
- [x] Context references: `args`, `options`, `repo`, `version`, `steps`, `env`
- [x] Basic filters: `slug`, `default(...)`, `upper`, `lower`
- [x] Simple conditions via truthy rendered values (`when:` field evaluated via IsTruthy in StepExecutor)
- [x] Expression operators/comparisons in templates (`==` and `!=` equality operators)
- [x] Path helper functions: `basename`, `dirname`, `filestem`, `fileext`, `urlencode`, `sha256`, `trim`, `replace(old,new)`, `truncate(n)`, `first(n)`

## 6) Built-in Primitives

- [x] `builtin:validate`
- [x] `builtin:resolve-version`
- [x] `command:test`
- [x] `command:analyze`
- [x] `command:verify`
- [x] `builtin:build-artifacts`
- [x] `builtin:tag-artifacts`
- [x] `builtin:push-artifacts`
- [x] `builtin:plan-artifacts`
- [x] `builtin:ship-artifacts`
- [x] `builtin:all-artifacts`
- [x] `builtin:plan` / `builtin:ship` / `builtin:all` aliases
- [x] `builtin:docker-plan` / `builtin:docker-ship` / `builtin:docker-all` / `builtin:docker-stage`
- [x] `builtin:config-resolved`
- [x] `builtin:config-materialize`
- [x] `doctor` built-in command path

## 7) Versioning

- [x] Fixed provider
- [x] Environment variable provider
- [x] GitVersion provider (basic shell + parse + fallback)
- [x] MinVer provider (shells out to `dotnet minver`, registered as `minver`)
- [x] NBGV provider (shells out to `nbgv get-version -f json`, registered as `nbgv`)
- [x] Basic git provider (`git` key — parses most recent git tag as SemVer)
- [x] Version contract fields from scope (all required fields present; optional `weightedPreReleaseNumber` computed from pre-release label)
- [x] Full output contract fields (build metadata, branch, assembly/file/nuget/docker versions, informationalVersion, commitsSinceVersionSource)

## 8) Artifacts

- [x] Artifact provider registry
- [x] Docker provider: build/tag/push
- [x] Docker Compose provider: build/push (via Docker; `docker-compose`)
- [x] NuGet provider: pack/push
- [x] Helm OCI provider: package/push (Docker fallback supported)
- [x] Helm (non-OCI) provider: package/push to HTTP chart repo
- [x] npm provider: pack/publish (Docker fallback; `target.tokenEnv`)
- [x] PyPI provider: build/publish (Docker fallback; twine auth)
- [x] Maven provider: package/deploy (Docker fallback; username/password auth)
- [x] Gradle provider: build/publish (Docker fallback; wrapper-aware; env auth)
- [x] RubyGems provider: build/push (Docker fallback; `target.apiKeyEnv`)
- [x] Terraform provider: init/validate/apply (Docker fallback; `target.varFile`)
- [x] Generic provider: zip/copy artifact packaging
- [x] Tag strategy support (semver/branch/sha/latest-on-main variants)
- [x] Docker fallback (`useDocker`/`dockerImage`) for all tool-based providers
- [x] Extra args passthrough (`extra-build-args`, `extra-push-args`) for all providers
- [x] Feed auth resolution per-provider (private `ResolveAuth()` in each provider; `FeedAuthResolver` holds only shared Docker auth)
- [x] Push policy rules enforced via `runtime.push` (`noPushInPullRequest`, `requireCleanWorkingTree`)
- [x] Artifact manifest file output (`artifacts/manifest.json` written after push)
- [x] Rich artifact metadata capture (manifest written; Artifacts/PushDecisions now flowed into CommandResult and RunManifest)

## 9) Verification and Analysis

- [x] `dotnet test` orchestration
- [x] Basic test result parsing (total/passed/failed/skipped)
- [x] Basic analysis hook (`dotnet format --verify-no-changes` / build analysis path)
- [x] Verify primitive (`test + analyze`)
- [x] TRX and coverage output support (TRX file parsing via `TryParseTrxFiles`, Cobertura coverage thresholds)
- [x] Coverage thresholds enforcement (Cobertura XML parsed; lineCoverageThreshold checked)
- [x] Extended analysis toolchain (SARIF/security scanners)

## 10) CI and Git Context

- [x] Git context detection (branch, commit, short sha, remote, clean state)
- [x] CI detection for common providers (at least core providers)
- [x] CI context attached to execution context
- [x] CI metadata depth (run number, tag, buildUrl, actor, workflowName, runAttempt — all providers)

## 11) Manifests and JSON Output

- [x] JSON output for command execution
- [x] JSON file output via `--json-file`
- [x] Run manifest model exists
- [x] Manifest writing path exists for run invocations
- [x] Manifest content complete: PushDecisions/Artifacts flowed from step results via __artifacts/__pushDecisions outputs; Branch/CommitSha/RemoteUrl/RepoName/Errors populated
- [x] Stable versioned JSON schema contract documentation (`schemas/1.0/README.md`, schema at `rexo.schema.json`)

## 12) Policy and Template Sources

- [x] Local file policy source class exists
- [x] Policy source integration in runtime config load path (policy.json loaded alongside repo.json)
- [x] Embedded policy templates
- [x] Remote policy sources (HTTP/Git/NuGet/company registry)
- [x] Policy caching/version pinning/trust model

## 13) Config Inspection and Explainability

- [x] `list` includes built-ins, config commands, aliases
- [x] `explain <command>` includes args/options/steps
- [x] `repo config resolved`
- [x] `repo config sources`
- [x] `repo config materialize` (CLI sub-command + builtin:config-materialize)
- [x] `repo explain version`
- [x] Explain depth enhanced (step graph with flags, push eligibility, provider config details)

## 14) Safety and Governance

- [x] Push rule engine enforced in `builtin:push-artifacts` via `runtime.push`
- [x] Skip push on PR enforcement (noPushInPullRequest rule)
- [x] Require clean working tree enforcement (requireCleanWorkingTree rule)
- [x] Secret masking/redaction in logs and outputs (auto-masks env vars containing SECRET, TOKEN, PASSWORD, KEY, APIKEY)
- [x] Structured error taxonomy: `RexoError` record with `Code`/`Message`/`Detail`/`SuggestedFix`/`Source`; `ErrorCodes` constants (CFG/CMD/STP/VER/ART/POL/GIT)

## 15) UI/TUI

- [x] Spectre.Console renderer exists
- [x] Basic `repo ui` command path
- [x] Interactive command picker (Spectre.Console SelectionPrompt, via `rx ui`; `rx` with no args shows help)
- [x] Rich command picker with command descriptions and execution dashboard
- [x] Config/policy/resolution browsing UI
- [x] TUI project/features (future phase)

## 16) Testing and Quality Gates

- [x] Build passes (`dotnet build`)
- [x] Original MVP test count was 234 at the time of that snapshot; see the current suite count from `dotnet test solution.slnx -c Release`.
- [x] Added tests for template rendering behavior
- [x] Added tests for built-in command registration paths
- [x] Coverage breadth expanded: REXO_OVERLAY, commands merge, StepExecutor when-condition + unknown builtin tests
- [x] Add tests for run manifest completeness (ErrorTaxonomyAndManifestTests)
- [x] Add tests for policy resolution + merge semantics (overlay, command dict merge, when-condition)
- [x] Add integration tests for branch workflows and alias resolution edge cases (`AliasAndBranchWorkflowTests`)

## 17) Documentation and Samples

- [x] `repo.json` example exists in repository root
- [x] Core docs exist (`ARCHITECTURE.md`, `CONFIGURATION.md`, `DEVELOPMENT.md`)
- [x] Docs document implemented JSON schema validation, version contract fields, and template filters
- [x] Add explicit docs for unresolved features and current limitations (see CONFIGURATION.md "Known Limitations" section)
- [x] Add docs for config inspection commands once implemented

## 18) MVP Completion Snapshot

Items expected in MVP (per scope section 56) and status:

- [x] .NET global tool structure
- [x] `repo.json` loading
- [x] Config commands / aliases / args / options
- [x] Sequential execution engine
- [x] Step types: run/command/uses
- [x] Basic templating
- [x] Version providers: fixed/env
- [x] Docker + NuGet artifact providers
- [x] Basic test command + analysis hook
- [x] `doctor`, `list`, `explain`
- [x] JSON output and `--json-file`
- [x] Run manifest (basic)
- [x] Local file extends/policies/merge (full merge pipeline implemented in RepoConfigurationLoader)
- [x] Basic config validation (full NJsonSchema validation via `ValidateSchemaAsync`)

## 19) Next High-Impact Work (Recommended Order)

- [x] Wire policy sources into runtime load path (policy.json/.yaml/.yml + .repo/ sub-folder candidates)
- [x] Implement `config materialize` standalone CLI sub-command
- [x] Command-level parallel settings (`maxParallel`)
- [x] Expand run manifest with config hash and full version fields
- [x] Secret masking/redaction in logs and outputs
- [x] Not-found command suggestion engine
- [x] Add focused tests for merge/policy/parallel/manifest edge cases (80 tests total)
- [x] Add Helm chart OCI artifact provider (`type: helm-oci`) with build/tag/push lifecycle integration
- [x] Add typed schema + tests + docs for Helm chart OCI artifact settings
- [x] Add CI pipeline scaffolding (`rx init ci`) for GitHub Actions and Azure DevOps thin-wrapper templates
- [x] Add shared feed-auth resolution layer for artifact pushes (Docker/NuGet/Helm) with env-mounted credentials
- [x] Add CI-native identity fallback for feed authentication (OIDC/service connection/token providers) with env fallback
- [x] Add auth preflight validation + secret-safe diagnostics for missing/invalid feed credentials
- [x] Smarter `rx init` repo introspection (language/framework detection, richer templates, policy template recommendations)

## 20) Artifact System Restructuring (see scope §53)

### Provider architecture

- [x] Remove hardcoded `artifactProviders.Register(...)` calls from CLI bootstrapper — replaced with self-registration (`XxxProvider.Register(registry)`)
- [x] Introduce pluggable provider discovery/registration mechanism — static `Register()` method on each provider, called from `CliBootstrapper.cs`
- [x] Ensure provider projects (`Artifacts.Docker`, `Artifacts.NuGet`, `Artifacts.Helm`) depend only on `Rexo.Core` — no CLI/Execution references
- [x] Add provider-availability diagnostics to `rx doctor` — toolchain checks for docker-compose, npm, python/python3, mvn, gradle, gem, terraform
- [x] Clear structured readiness error when a config references an unknown artifact provider type

### Lifecycle builtins

- [x] Confirm `builtin:build-artifacts` / `builtin:tag-artifacts` / `builtin:push-artifacts` / `builtin:plan-artifacts` / `builtin:ship-artifacts` / `builtin:all-artifacts` remain host-owned and delegate only to the registry
- [x] Provider libraries must not register builtins directly

### Provider backlog — high priority

- [x] `Rexo.Artifacts.Npm` — `type: "npm"` provider (npm pack / npm publish)
- [x] `Rexo.Artifacts.PyPi` — `type: "pypi"` provider (build wheel / twine upload)
- [x] `Rexo.Artifacts.Maven` — `type: "maven"` provider (mvn package / mvn deploy)
- [x] `Rexo.Artifacts.Generic` — `type: "generic"` provider (archive zip/tar.gz, copy to output)

### Provider backlog — medium priority

- [x] `Rexo.Artifacts.Gradle` — `type: "gradle"`
- [x] `Rexo.Artifacts.RubyGems` — `type: "rubygems"`
- [x] `Rexo.Artifacts.Terraform` — `type: "terraform"`
- [x] `Rexo.Artifacts.Helm` — `type: "helm"` (non-OCI / generic chart, separate from existing helm-oci)
- [x] `Rexo.Artifacts.DockerCompose` — `type: "docker-compose"`

### Provider backlog — lower priority / niche

- [ ] `rpm`, `deb`, `aws-lambda`, `azure-function`, `gcp-function`, `npm-workspace`, `cargo`, `composer`, `conda`

### Supporting infrastructure

- [ ] Registry-credentials helper / shared auth provider abstraction (usable by any provider)
- [ ] Package-index provider for GitHub Packages / Artifactory / Azure Artifacts
