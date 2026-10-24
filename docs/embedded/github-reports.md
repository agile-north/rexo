# GitHub test and coverage reporting

Import [github-tests](github-tests.md) and/or [github-coverage](github-coverage.md)
**after** the policy that runs tests. They are GitHub destinations, not .NET toolchains.
GitHub renders Checks, Markdown summaries and downloadable artifacts; it does not parse
TRX/JUnit or compute coverage. Rexo parses tests, and containerized ReportGenerator
processes coverage.

```yaml
extends:
  - embedded:standard
  - embedded:dotnet
  - embedded:github-tests
  - embedded:github-coverage
vars:
  githubTests:
    checkName: Application tests
  githubCoverage:
    minimumLineCoverage: 80
```

Run `rx test`, `rx verify`, or `rx release`. Both reporting steps use `alwaysRun`:
normal test-step failures do not prevent either report from being processed. Existing
test failures remain failures even if publishing succeeds. Cancellation, runner termination,
or a failure before the lifecycle reaches `test` cannot guarantee reporting. Custom command
replacement/reordering must preserve the reporting steps at the end of `test`.

To process existing files without rerunning tests:

```text
rx reports tests
rx reports coverage
rx reports tests --publish-reports
rx reports coverage --publish-reports
rx reports tests --report-preview
rx reports coverage --report-preview
```

The first two generate local reports. Outside Actions, `--publish-reports` opts into
publishing; `publish: always` also enables it. Inside Actions, publishing is automatic.
`publish: never` overrides the flag. Reporting authorization is deliberately separate
from release `--push` and `runtime.push` restrictions. `--dry-run` neither processes
reports nor writes summaries, creates Checks, starts ReportGenerator, or uploads artifacts.
`--report-preview` on an explicit `reports` command instead shows resolved globs,
exclusions, candidate files and detected formats without writing or publishing anything.
It does not parse complete test results, run conversion or enforce coverage thresholds;
use it to diagnose discovery. `--dry-run` still takes precedence and skips discovery.
Do not use the preview flag as a validation gate.

## Shared configuration

Set these under **each** of `vars.githubTests` and `vars.githubCoverage`. Defaults are
independent; configuring one policy does not change the other. Scalar strings support
Rexo templates. Lists of paths, exclusions and ReportGenerator settings use semicolon
separators, not shell syntax.

| Variable | Default | Contract |
| --- | --- | --- |
| `enabled` | `true` | Disable the complete policy step when false. |
| `publish` | `auto` | `auto`: publish in Actions or with `--publish-reports`; `always`: also publish locally; `never`: local processing only. |
| `paths` | empty | Automatic bounded discovery under `outputs.tests.*`; override with repository-contained file paths/globs separated by `;`. |
| `exclude` | empty | Repository-relative exclusion globs separated by `;`. |
| `format` | `auto` | Content detection, or one explicitly selected format from the policy's supported list. Mixed formats require `auto`. |
| `outputDirectory` | policy-specific | Repository-contained generated report directory. Generated files here are excluded from input discovery. |
| `missingReports` | `warn` | `warn`: visible diagnostic and neutral Check unless a configured coverage threshold requires the missing metric; `fail`: fail reporting. Never infer a passing test/coverage result. |
| `publishErrors` | `fail` | Attempted publishing errors fail the step; `warn` explicitly opts into warnings. Processing errors and test/threshold failures are never downgraded. |
| `checks` | `true` | Publish a commit Check, shown in commit/PR Checks. Requires explicit authorized token. |
| `jobSummary` | `true` | Append Markdown to `GITHUB_STEP_SUMMARY` when publishing inside Actions. No job-summary file outside Actions. |
| `uploadArtifact` | `true` | Upload selected raw reports and generated output inside Actions when runtime credentials are available; never upload from a local run. |
| `artifactUpload` | `auto` | `auto`: upload when runtime credentials exist, otherwise informational skip; `required`: unavailable runtime credentials fail through `publishErrors`; `never`: disable. `uploadArtifact: false` overrides this setting. |
| `artifactName` | policy-specific | Artifact prefix; final name is `<prefix>-<scope>-<run-attempt>`. Names must satisfy GitHub artifact naming rules. |
| `retentionDays` | `14` | Integer 1–400, capped by repository retention when supplied by Actions. |
| `repository` | origin owner/repo | Override GitHub destination as `owner/repo`. |
| `hostname` | origin host | `github.com` or an Enterprise hostname, without scheme, port or path. |
| `commitSha` | checked-out Git SHA | Full 40-character SHA in the destination repository. Override deliberately when reporting a different checkout/merge SHA. |
| `tokenEnvironment` | `GH_TOKEN` / `GH_ENTERPRISE_TOKEN` | Environment-variable **name**, not a secret value. Defaults also try `GITHUB_TOKEN` / `GITHUB_ENTERPRISE_TOKEN`. Mapped secrets take precedence. |
| `checkName` | policy-specific | Check display name; scope is appended. Combined length must be at most 100 characters. |
| `scope` | `GITHUB_JOB`, else `default` | 1–100 letters/digits/`._-`. Set explicitly for matrix axes/shards. No automatic cross-job aggregation. |
| `annotationLimit` | tests `50`, coverage `0` | Integer 0–1000; maximum test-failure details/annotations. Coverage currently publishes summaries, not source annotations. |

Paths support `*`, `?`, and `**`. Discovery rejects paths outside the repository,
symbolic-link traversal, more than 10,000 selected files, and individual files over 64 MiB
(format parsers may impose stricter bounds). Automatic discovery skips valid unrelated
formats; explicit selections reject unsupported formats. Malformed report data fails
visibly. Keep generated output in its own directory.

Leave `outputs.emit` enabled (the default) for automatic path defaults. If deliberately
disabled, configure explicit `paths` and `outputDirectory` for each reporting policy.
Input reports from distinct files are distinct executions unless the converter can merge
their coverage identities; use a clean per-run test-results directory to avoid selecting
old test reports.

## GitHub authentication and least privilege

For Checks, use a GitHub App installation token with **Checks: write**, or the Actions
`GITHUB_TOKEN` with `checks: write`. A conventional personal access token is not a substitute
for a token authorized to create Checks. Rexo never reads host `gh` credential stores.
Repository/hostname overrides support Enterprise Checks API endpoints.

```yaml
permissions:
  contents: read
  checks: write
steps:
  # Checkout and install rx/.NET as appropriate for your repository.
  - run: rx verify
    env:
      GH_TOKEN: ${{ github.token }}
```

This replaces test-reporter and coverage-summary workflow steps. Coverage processing
requires Docker even when publishing is disabled. Actions job summaries need no Checks
permission. Raw artifact uploads have a separate runtime-credential requirement below.

Missing explicit tokens and known read-only fork PR/Dependabot contexts skip Checks
with a warning; local generation, job summaries and independently authorized uploads
can still run. Other attempted API failures, including 403s in contexts not known to
be read-only, fail by default. Never use `pull_request_target` to execute untrusted
test code with elevated credentials.

## Actions artifacts versus local runs

Workflow artifact storage is **not** a generic GitHub repository upload API. Uploads
require an active job's `ACTIONS_RUNTIME_TOKEN` and `ACTIONS_RESULTS_URL`;
`GH_TOKEN`/`GITHUB_TOKEN` cannot provide that capability. When runtime credentials are
not available, the default `artifactUpload: auto` emits an informational skip and
`artifactSkipReason` rather than claiming
an upload. This is distinct from an attempted upload failing, which follows `publishErrors`.
Outside Actions, raw reports and generated reports remain local.

**Ordinary `run: rx verify` steps normally do not receive these runtime credentials.**
They still publish Checks and job summaries, but downloadable workflow artifacts are
skipped with the informational diagnostic above. JavaScript action hosts can supply the runtime
environment; Rexo does not scrape runner credentials, export them to later steps,
or ship a runner wrapper. Keep an explicit upload action if downloadable artifacts
are essential in an ordinary shell-based workflow.

### Recommended shell workflow: upload the local reports afterwards

Keep report generation, Checks, summaries, and thresholds in Rexo. Let
`actions/upload-artifact` handle only transporting the local files to Actions storage.
Disable Rexo's internal upload attempts to avoid runtime-credential diagnostics or duplicate
uploads:

```yaml
# .rexo/rexo.yaml (alongside your existing policy imports)
vars:
  githubTests:
    uploadArtifact: false
  githubCoverage:
    uploadArtifact: false
```

With the default output layout, **one upload step** captures raw tests, raw coverage
stored under the test-results directory, both Markdown summaries, and coverage HTML:

```yaml
# Workflow job excerpt; checkout and install rx/.NET beforehand.
permissions:
  contents: read
  checks: write
steps:
  - name: Verify with Rexo
    run: rx verify
    env:
      GH_TOKEN: ${{ github.token }}

  - name: Upload Rexo test and coverage reports
    if: always()
    uses: actions/upload-artifact@v4
    with:
      name: test-and-coverage-${{ github.job }}-${{ github.run_attempt }}
      path: artifacts/tests/
      if-no-files-found: warn
      retention-days: 14
```

`if: always()` attempts the upload even when `rx verify` fails; it does not turn failed
tests or threshold failures into success. Missing files warn rather than inventing
reports. Job/runner termination can still prevent upload.

The default generated directories are `artifacts/tests/github-report/` for tests and
`artifacts/tests/coverage/github-report/` for coverage. Adjust `path` to your actual
`outputs.tests.results`, `outputs.tests.coverage`, policy `paths`, and `outputDirectory`
overrides. For directories outside the default tree, use a multiline `path` listing
the selected raw-report paths and generated directories. Prefer narrowly scoped paths,
not the entire repository or output root.

For matrix jobs, include every relevant axis/shard in the artifact name: `github.job`
alone is shared by all matrix instances. For example, add
`${{ matrix.os }}-${{ matrix.framework }}` to the name and set the corresponding Rexo
`scope` as described below. The upload action's `name` and `retention-days` are independent
of Rexo's `artifactName` and `retentionDays` vars, which apply only to Rexo's internal uploader.
Do not enable both upload mechanisms for the same reports.

Uploaded raw reports may contain secrets, test data, machine paths, or attachments.
Known-secret masking applies to Check text and generated GitHub Markdown, **not to raw
files or generated HTML**. Review inputs and disable uploads when reports are sensitive.
Artifacts are downloadable archives, not automatically hosted coverage websites.

## Matrix jobs and reruns

Give every concurrent matrix/shard execution a distinct scope, for example:

```yaml
vars:
  githubTests:
    scope: "{{env.REPORT_SCOPE}}"
  githubCoverage:
    scope: "{{env.REPORT_SCOPE}}"
```

Set `REPORT_SCOPE` in the workflow to a safe value such as `linux-net10-shard1`.
Check identity includes policy, repository, commit, scope, and Actions run ID/attempt/job.
Attempts are distinct; identical reruns update the matching Check without duplicating
annotations. GitHub cannot replace annotations, so a changed annotation set creates a
new completed Check rather than appending stale failures. Only after its replacement
publishes completely, Rexo marks older matching scoped results neutral with a
"Superseded report" summary and a link to the current result. Other scopes and attempts
are never superseded. Artifact names also include
scope and attempt; publishing the same artifact name twice in a job is an explicit
upload error, not an overwrite. There is no cross-job or historical-baseline aggregation.

## Output and emission contract

Step IDs are `github-tests` and `github-coverage`. Successful processing writes
`<outputDirectory>/SummaryGithub.md`, including when a valid report contains failures.
Scalar outputs are available through `steps.<id>.outputs.<key>` and command `--json`
step outputs. Run manifests preserve step success/exit status and the
`reportSummary` file-output path, **not** arbitrary scalar step outputs.

| Output | Meaning |
| --- | --- |
| `status` | `skipped`, `preview`, `local`, `published`, or `failed`; `published` means a Check URL or uploaded artifact ID was obtained, not that tests passed. |
| `skipReason` | Present for disabled/dry-run steps. |
| `reportCount` | Number of selected supported input files when discovery completes. |
| `summaryPath` | Repository-relative Markdown file when written successfully. |
| `conclusion` | Report conclusion: `success`, `failure`, or `neutral`. Overall step failure may additionally reflect publishing errors. |
| `checkUrl` | GitHub Check URL, or null when disabled/skipped. |
| `checkStatus` | `published`, `skipped`, or `failed`; independent of test/coverage conclusion. |
| `checkSkipReason` | Machine-readable explanation when Check publication is skipped, including disabled publishing or unavailable credentials. |
| `artifactId` | Uploaded Actions artifact ID, only after successful finalization. |
| `artifactStatus` | `published`, `skipped`, or `failed`; a local summary does not imply upload. |
| `artifactSkipReason` | Runtime credentials missing for a requested Actions upload. |
| `publishWarning` | Publishing failure text, including when configured to warn. |
| `error` | Visible processing/publication failure message when the step fails. |
| `total`, `passed`, `failed`, `skipped` | Test counts, only after successful test parsing. |
| `lineCoverage`, `branchCoverage` | Coverage percentages, nullable when the source format has no corresponding metric. |
| `paths`, `exclude`, `detectedReports` | Preview-only discovery settings and repository-relative candidate paths/formats; null format identifies an unrelated file. |

Known GitHub policy variable names have schema completion and reject misspelled keys.
Literal types/ranges are validated before execution; template expressions remain supported
and their rendered values are validated by the policy at runtime.

No report-specific scalar variables are added to CI emission: neither safe nor full
scope emits test counts or coverage percentages. With step file outputs enabled, safe
scope exposes `REXO_STEPS_GITHUB_TESTS_FILE_OUTPUTS_REPORT_SUMMARY_COUNT` and
`REXO_STEPS_GITHUB_TESTS_FILE_OUTPUTS_REPORT_SUMMARY_0` (and equivalent `GITHUB_COVERAGE`
keys). Full scope exposes indexed `Steps[i].FileOutputs.reportSummary[j]` paths;
indices depend on command composition. For scalar gates, inspect command JSON by step
ID or use an in-pipeline template rather than inventing CI variable names.
Prefix/filter/destination settings still apply.
See the [complete emission reference](../configuration/ci-output-emission.md).
GitHub Checks and job summaries do not depend on enabling CI variable emission.
