# embedded:github-coverage

An opt-in, stack-neutral coverage destination. Content detection supports Cobertura,
OpenCover, LCOV, JaCoCo and Clover; containerized ReportGenerator merges/converts inputs
into GitHub Markdown, browsable HTML, and machine-readable coverage totals. GitHub does
not calculate coverage or provide a native HTML coverage view.

```yaml
extends:
  - embedded:standard
  - embedded:dotnet
  - embedded:github-coverage
vars:
  githubCoverage:
    paths: "{{outputs.tests.results}}/**/coverage.cobertura.xml"
    minimumLineCoverage: 80
    minimumBranchCoverage: 70
    fileFilters: "-**/Generated/**"
```

Read the [shared configuration, permissions, Actions, matrix, privacy and output
reference](github-reports.md). All shared settings live under `vars.githubCoverage`.
For downloadable HTML and raw reports from ordinary shell steps, use the documented
[upload-action example](github-reports.md#recommended-shell-workflow-upload-the-local-reports-afterwards).

## Coverage-specific defaults and overrides

| Variable | Default | Contract |
| --- | --- | --- |
| `paths` | empty | Automatically examine `outputs.tests.coverage/**/*`, plus `coverage*.xml` and `lcov.info` recursively under `outputs.tests.results`. |
| `format` | `auto` | `auto`, `cobertura`, `opencover`, `lcov`, `jacoco`, `clover`. Explicit selection verifies detected format. |
| `outputDirectory` | `{{outputs.tests.coverage}}/github-report` | Generated report directory; excluded from input discovery. |
| `artifactName` | `coverage` | Selected raw coverage plus generated reports. |
| `checkName` | `Rexo coverage` | Scope is appended. |
| `image` | pinned built-in ReportGenerator image | Explicit container override; no native/global tool installation and no silent fallback. |
| `sourceDirectories` | `src` when present | Optional `;`-separated repository-contained source directories mounted read-only for HTML source views. Empty uses automatic `src`; `none` disables source mounts. Set directories for other layouts. |
| `reportTypes` | `Html;MarkdownSummaryGithub` | ReportGenerator output types; JSON totals are always generated internally. |
| `assemblyFilters` | empty | ReportGenerator semicolon-separated inclusion/exclusion filters. |
| `classFilters` | empty | ReportGenerator class filters. |
| `fileFilters` | empty | ReportGenerator source file filters. |
| `minimumLineCoverage` | empty | Optional 0–100 percentage. A missing line metric or missing reports fails an explicitly configured threshold, even with `missingReports: warn`. |
| `minimumBranchCoverage` | empty | Optional 0–100 percentage. A missing branch metric or missing reports fails an explicitly configured threshold, even with `missingReports: warn`. |

Docker is required when coverage inputs exist, including local-only reporting.
Inputs are mounted read-only and generated output is writable; GitHub credentials
are not passed to the converter. The helper has no host tool installation fallback.
An overridden image is executable third-party code: trust and pin it deliberately.

The built-in image is the Microsoft .NET SDK, pinned to
`mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2`.
The helper downloads **ReportGenerator 5.5.11** inside this disposable container,
verifies its pinned NuGet SHA-512, and installs only from the verified local package.
No tools are installed on the host. This requires network access to nuget.org on each run.
There is no claimed official ReportGenerator image. Custom images must be digest-pinned
and provide `sh`, `curl`, `sha512sum`, and a compatible .NET SDK.

Source views depend on coverage file source identities resolving against the mounted
source directories. Keep secrets outside those directories; HTML can include source code.
If sources cannot be resolved, metrics still work but HTML marks source files unavailable.
ReportGenerator matches progressively shorter recorded path suffixes under these directories,
including paths recorded on another OS. If several sources have the same suffix, the first
source directory wins; order overrides deliberately. Directories outside `src` (such as
`tests` or `lib`) require an explicit override. The repository root and directories directly
containing `.git` cannot be mounted as source directories.

Use a dedicated output directory: the helper refuses a non-empty directory unless it
contains its `.rexo-coverage-report` ownership marker. On subsequent runs, **all files in
that owned directory are replaced**, including files manually added there. Do not place
input reports or hand-maintained files in it.

For LCOV from another stack:

```yaml
vars:
  githubCoverage:
    paths: artifacts/coverage/lcov.info
    format: lcov
```

Run `rx reports coverage` locally, or add `--publish-reports` to publish the resulting
Check with explicit credentials. HTML remains local outside Actions; inside Actions,
it is downloadable with the coverage artifact when runtime upload credentials are available.
It is not automatically deployed to Pages.

Coverage percentages come from ReportGenerator's merged counters, never arithmetic
averages of input percentages. Keep reports in consistent source/project identities;
do not mix unrelated builds, source roots, or copies of converted and original reports.
Formats differ in branch information and project identity. No PR-diff coverage,
coverage-change baseline, or automatic cross-job aggregation is implemented.
