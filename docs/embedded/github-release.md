# embedded:github-release

Opt-in GitHub release automation for the standard Rexo lifecycle. Run `rx release --push`:
Rexo verifies/builds/publishes the selected artifacts, runs existing `post-push` and
`post-release` hooks, then creates the GitHub tag and release using `gh`.
No release script is needed in the CI workflow.

This policy works locally and in CI, including outside GitHub Actions. It uses Rexo's internal
execution/version/publication context, not exported `REXO_*` variables. Disabling CI emission,
changing its scope/prefix, or choosing a different CI provider does not change eligibility.

## Quick Start

Merge this into `.rexo/rexo.yaml` (retain your existing artifacts, versioning, and toolchain vars):

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
schemaVersion: "1.0"
name: example
extends:
  - embedded:standard
  # Add embedded:dotnet or embedded:node here when needed.
  - embedded:github-release
versioning:
  provider: fixed
  fallback: 1.2.3
runtime:
  push:
    noPushInPullRequest: true
    branches: [main]
```

With an explicit token in the environment and Docker running:

```text
rx release --push
```

`fixed` is illustrative; use your normal [version provider](../configuration/versioning.md).
The resolved commit must already exist in the target GitHub repository. The policy never
pushes commits or guesses the default branch as the release target.

## Lifecycle and Eligibility

List `embedded:github-release` **after** `embedded:standard` and other lifecycle contributors.
It appends the `github-release` step (`uses: builtin:github-release`) to `release` using
`merge: append`. It does not replace standard lifecycle work, add a separate user-facing command,
or run during standalone `push`, `build`, `tag`, or `version`.

The standard release's `post-release` hook is always-run for cleanup/reporting. The new step is
**not** always-run: a hard failure prevents it from executing. It also checks completed-step
success itself, so a failed `continueOnError` step does not accidentally authorize publication.
Resolved version and artifact outcomes include results propagated by nested commands.

Default requirements:

1. `vars.githubRelease.enabled` is true.
2. The current command is `release` and `--push` was explicitly requested. This is required
   even for source-only repositories; ordinary `rx release` never creates GitHub tags/releases.
3. No dry run (`--dry-run`, `runtime.dryRun`, or `runtime.push.dryRun`).
4. All preceding lifecycle steps succeeded and a semantic version was resolved.
5. Global `runtime.push` rules allow publication: `enabled`, `noPushInPullRequest`,
   `requireCleanWorkingTree`, and `branches` use the same checks as artifact publication.
   Per-artifact overrides do not override the global gate on GitHub releases.
6. With selected artifacts, all must actually have been published; blocked, failed, provider-no-op,
   or unprocessed selected artifacts do not qualify. Without configured artifacts, source-only
   publication is allowed.

Ineligible runs are successful skips with a visible reason and no `gh`/Docker invocation.
Once eligible, invalid configuration, missing credentials/tooling, API failures, missing commits,
and conflicting tags **fail the command with exit code 6**; they are not success-shaped skips.
Cancellation propagates.

Use the ordinary `post-release` command for checks/cleanup that must complete **before** the
GitHub release. If you customize/replace/reorder the `release` command or append additional
`release.after` work, preserve the final position of `github-release` yourself: steps deliberately
placed after it cannot retroactively undo an external release. Inspect `rx config resolved` or
`rx graph release` when composing custom policies.

## All Configuration Vars

Every setting lives under `vars.githubRelease`; repository values override policy defaults.
Do not put tokens in vars. No arbitrary shell command or extra-arguments string is evaluated.

| Var | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Enable the final lifecycle publication step. |
| `allowSourceOnly` | `true` | Allow releases when no artifacts are configured. Does not bypass `--push` or global push rules. |
| `artifactGate` | `all` | `all`: every selected artifact published. `any`: at least one published. `none`: do not require artifact publication. No mode bypasses lifecycle failure, dry run, or global push rules. |
| `execution` | `container` | `container` or `native`. No automatic fallback. |
| `image` | `docker.io/maniator/gh:v2.103.2@sha256:a76d7b905cc3cb07cd158eff872494bc29f8f64aee8d6e50456307b285dfab97` | Immutable, multi-architecture Linux CLI container. Override with your approved image containing a `gh` executable. |
| `repository` | empty: derive from origin | `owner/repository`, without hostname or URL. |
| `hostname` | empty: derive from origin | GitHub hostname, e.g. `github.com` or `github.example.com`; not a URL. |
| `tagTemplate` | empty: automatic naming below | Template-rendered Git tag. |
| `titleTemplate` | empty: automatic naming below | Template-rendered release title. |
| `draft` | `false` | Create a draft rather than publishing immediately. |
| `prerelease` | `auto` | `auto`, `true`, or `false`; auto uses the resolved version's `IsPreRelease`. |
| `generateNotes` | `true` | Pass `--generate-notes` to `gh release create`. |
| `notesFile` | empty | Optional UTF-8 notes file, relative to repository root or absolute. Supports templates; file must exist before publication. |
| `notesStartTag` | empty | Optional starting tag for generated notes; supports templates. Use with `generateNotes: true`. |

An empty tag/title template selects automatic naming, not an empty tag/title.
Supported template sources are listed in [Template Variables](../configuration/templates.md);
examples include `{{version.semver}}`, `{{options.artifact-group}}`, and `{{vars.*}}`.
Invalid booleans, execution/gate/prerelease modes, repository/hostname, and Git tag names fail explicitly.

Example overrides:

```yaml
vars:
  githubRelease:
    execution: native
    draft: true
    tagTemplate: "sdk/v{{version.semver}}"
    titleTemplate: "SDK {{version.semver}}"
    notesFile: docs/release-notes.md
    notesStartTag: sdk/v1.2.2
```

`artifactGate: any` is intentionally weaker: a policy-blocked sibling can coexist with a release.
`artifactGate: none` can release source even when configured artifact providers report successful
no-ops. Use these overrides only when that is the repository's intended release contract.

## Artifact Groups and Naming

| Invocation | Default tag | Default title |
| --- | --- | --- |
| `rx release --push` | `v1.2.3` | `1.2.3` |
| `rx release --push --artifact-group contracts` | `contracts/v1.2.3` | `contracts 1.2.3` |
| `rx release --push --all-artifact-groups` | `v1.2.3` | `1.2.3` |

Selected-group releases are first-class releases. Unrelated groups do not need to publish.
The resolved repository version is shared; the group prefix prevents independent lanes from
colliding on one tag/release. Group names used in automatic tags must produce valid Git tag names.

Default-group and all-group runs intentionally share the version-only tag. To distinguish those
lanes too, configure explicit templates. GitHub-generated notes are **repository-wide**, not filtered
by artifact group. Supply `notesFile` and/or `notesStartTag` when a lane needs different notes.
No group-aware changelog generator or GitHub release asset upload is included.

## Exact-Commit Tags, Existing Releases, and Failures

The publisher uses argument-list process execution, not interpolated shell scripts:

1. Resolve origin's hostname and `owner/repository` (HTTPS, SSH URL, or scp-style SSH).
   Explicit overrides take precedence. Check authenticated repository access and verify the
   exact full released commit SHA exists in that repository.
2. Read the remote tag via `gh api`. Resolve annotated tag chains to a commit as well as
   lightweight tags. If an existing tag resolves elsewhere, fail without moving/deleting it.
3. Read the published release by tag and, when absent, search the authenticated release list
   (including drafts, paginated up to 100,000 releases). If it exists and the tag matches, report `existing` and leave it
   completely unchanged (including draft status, notes, title, and prerelease status).
4. If neither exists, create a lightweight tag reference at the exact released commit through
   `gh api`. A matching existing tag can be reused when its release does not exist.
5. Re-read and verify the tag, then run `gh release create` with `--verify-tag`, `--target`,
   `--title`, and configured notes/draft/prerelease arguments. Read back the release URL.

An explicit HTTP 404 is treated as absence only for tag/release lookups **after** checking
repository access. Authentication/permission failures, rate limits, transport errors, invalid
responses, and server errors fail explicitly. Repository permissions must allow reading refs
and releases and writing tags/releases.

No force operation, deletion, or rollback is attempted. A tag may remain if release creation
fails; rerunning verifies and reuses it. Concurrent tag/release creation can fail a run rather
than overwriting another publisher; a subsequent rerun is idempotent. Prefer CI concurrency
groups to serialize publishers for the same version/group.

The tag is lightweight; tag signing and annotated-tag creation are not supported in this policy.
Tags are created remotely; fetch tags into your local checkout when your versioning workflow needs them.
GitHub release creation is not transactional with registry pushes or later external actions.

## Execution and Authentication

Container mode requires Docker, **not host `gh`**. The default is the third-party
[maniator/gh image](https://github.com/maniator/gh), containing upstream GitHub CLI; it is not
an official GitHub-published container. Its version and multi-architecture digest are pinned.
Review it under your supply-chain policy or override `image` with your approved equivalent.

Rexo runs `docker run --rm --entrypoint gh` with explicit environment-variable names.
It does not pass token values in command arguments, mount the host `gh` credential store,
mount the Docker socket, or mount the repository. Only a configured notes file is mounted,
read-only, when creating the release. Registry credentials used for image pulls remain Docker's
responsibility. Missing Docker or image/daemon failures do not fall back to host execution.

With `execution: native`, `gh` must be on PATH. All GitHub calls use an explicit hostname and
repository; neither mode relies on CLI working-directory repository detection or `GH_REPO`.

Token selection:

| Destination | Token precedence |
| --- | --- |
| `github.com` | `GH_TOKEN`, then `GITHUB_TOKEN` |
| Enterprise hostname | `GH_ENTERPRISE_TOKEN`, `GITHUB_ENTERPRISE_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN` |

Values can come from mapped Rexo secret environment, process environment, or repository env
files (do not commit real credentials). The selected token is passed only in the child
environment, as `GH_TOKEN` / `GH_ENTERPRISE_TOKEN`. Interactive prompts and inherited `GH_DEBUG`
are disabled. An explicit token is required even in native mode; a prior host `gh auth login`
is not a substitute. Error diagnostics mask tokens.

Local example, after securely supplying `GH_TOKEN` to your shell:

```powershell
rx release --push
rx release --push --artifact-group contracts
```

For Enterprise, provide an enterprise token and configure origin accordingly, or set
`vars.githubRelease.hostname` and `repository`. HTTPS GitHub API access, registry access for
the container pull, and the released commit on the remote are prerequisites.

## Minimal GitHub Actions Workflow

The policy handles creation; the workflow only checks out, installs Rexo, grants permissions,
and supplies credentials. This example assumes the repository config extends standard and
github-release as shown above:

```yaml
name: Release
on:
  workflow_dispatch:
permissions:
  contents: write
concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false
jobs:
  release:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - run: dotnet tool install --global Rexo.Cli
      # Configure credentials for your artifact registries here when needed.
      - name: Release selected artifacts and GitHub release
        env:
          GH_TOKEN: ${{ github.token }}
        run: rx release --push
```

Docker is available on GitHub-hosted Ubuntu runners. Self-hosted runners must provide it, or
explicitly choose native execution and install `gh`. Version providers may require complete
history/tags; `fetch-depth: 0` avoids shallow-checkout surprises.

`contents: write` authorizes repository tags/releases; registry providers may need additional
permissions/secrets. GitHub rulesets, protected tags, or insufficient fine-grained token grants
can still reject publication. For cross-repository releases, supply a token authorized for
the configured target. Releases/tags created with `GITHUB_TOKEN` follow GitHub's restrictions
on triggering further workflows; use an appropriately scoped GitHub App token when required.

The policy does not configure Actions permissions, create credentials, or infer permission from
the CI environment. Do not use privileged release jobs on untrusted pull-request code.

## Machine-Readable Results and AI Discovery

`github-release` step outputs:

| Outcome | Outputs |
| --- | --- |
| Ineligible | `skipped: true`, `skipReason`, `message` |
| Created or existing | `status: created` / `existing`, `tag`, `url`, `repository` (hostname/owner/repo), `commitSha` |
| Failure | `error`; step fails with exit code 6 |

Use `--json` / `--json-file` for these raw step outputs. They are **not** additional automatic
CI summary variables: safe/full manifest emission does not export arbitrary step outputs.
Existing `REXO_SUCCESS` reflects GitHub release failure too; artifact summary variables still
describe registry publication, not GitHub release creation.
See [CI/CD Emission Reference](../configuration/ci-output-emission.md).

Discover and inspect the policy through `rx policies list`, `rx policies show github-release`,
and `rx config resolved`. The source is
[`github-release.policy.json`](../../src/Policies/EmbeddedTemplates/github-release.policy.json);
runtime behavior is implemented by `GitHubReleasePublisher` and `GitHubReleaseBuiltinModule`.
AI workflow authors should prefer this policy over copying shell scripts into Actions when
the user wants GitHub releases, retain the explicit `--push` authorization, and configure
vars rather than replacing lifecycle steps.
