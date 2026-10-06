# Embedded Policy: dotnet

`embedded:dotnet` is a .NET toolchain overlay. It supplies the `restore`, `build`, `test`, `analyze`,
`format` and `security` commands that `embedded:standard` lifecycle commands (`verify`, `build`, …) call into.

Design principle: **only essential behavior is on by default**. Everything else is an opt-in switch in
`vars.dotnet.*`, so you can tailor the policy without overriding its commands.

| Feature | Default | Switch |
| --- | --- | --- |
| `dotnet restore` / `dotnet build` | on | — |
| `dotnet test` with TRX results | on | — |
| Code coverage (XPlat Code Coverage) | **on** | `vars.dotnet.test.coverage.enabled` |
| Analyzer build during `analyze` | **on** | `vars.dotnet.analyze.build.enabled` |
| `dotnet format --verify-no-changes` during `analyze` | **off** | `vars.dotnet.analyze.format.enabled` |
| SARIF output (per-project + merge) | **off** | `vars.dotnet.analyze.sarif.enabled` |
| Vulnerable package scan in `security` | **off** | `vars.dotnet.security.enabled` |
| Run inside a container | **off** (host) | `vars.dotnet.container` |

## Usage

```yaml
extends:
  - embedded:dotnet
  - embedded:standard
vars:
  dotnet:
    solution: solution.slnx
    analyze:
      format: { enabled: true }   # opt in to the format check
      sarif: { enabled: true }    # opt in to SARIF for code scanning
```

Policy vars are **deep-merged underneath** your repository's `vars`: you only set the keys you want to
change, and every other default stays in place. Your repository config always wins.

## Commands

| Command | Steps (ids) | Notes |
| --- | --- | --- |
| `restore` | `dotnet-restore` | |
| `build` | `dotnet-build` | `--no-restore` |
| `test` | `dotnet-test` / `dotnet-test-no-coverage` | Exactly one runs depending on coverage. TRX to `outputs.tests.results`, coverage to `outputs.tests.coverage`. |
| `analyze` | `dotnet-format-check`, `dotnet-sarif-targets`, `dotnet-build-warnings`, `dotnet-build-warnings-no-sarif`, `dotnet-sarif-merge` | See [Analysis](#analysis). |
| `format` | `dotnet-format` | Applies formatting. |
| `security` | `dotnet-vulnerable-packages` | Opt-in. |

Step ids are stable, so you can still target them with command `merge` step operations.

## vars.dotnet reference

| Var | Default | Description |
| --- | --- | --- |
| `solution` | `""` | Solution/project path passed to dotnet commands (empty = dotnet discovery). |
| `configuration` | `Release` | Build/test configuration. |
| `container` | `""` | Container registry name (e.g. `dotnet-sdk`) to run every dotnet step in; empty/`none` = host. |
| `restore.extraArgs` | `""` | Appended to `dotnet restore`. |
| `build.extraArgs` | `""` | Appended to `dotnet build`. |
| `test.runsettings` | `""` | Passed as `--settings <path>`. |
| `test.extraArgs` | `""` | Appended to `dotnet test`. |
| `test.coverage.enabled` | `true` | Collect XPlat Code Coverage. |
| `analyze.build.enabled` | `true` | Run the analyzer build. |
| `analyze.build.extraArgs` | `""` | Appended to the analyzer build (e.g. `/p:TreatWarningsAsErrors=true`). |
| `analyze.format.enabled` | `false` | Run `dotnet format --verify-no-changes`. |
| `analyze.format.extraArgs` | `""` | Appended to the format check. |
| `analyze.sarif.enabled` | `false` | Emit merged SARIF 2.1.0 to `outputs.analysis.sarif`. |
| `analyze.sarif.version` | `2.1` | Roslyn `ErrorLog` SARIF version. |
| `analyze.sarif.category` | `dotnet-build` | SARIF `automationDetails.id` category. |
| `format.extraArgs` | `""` | Appended to `dotnet format` in the `format` command. |
| `security.enabled` | `false` | Run `dotnet list package --vulnerable --include-transitive`. |
| `security.extraArgs` | `""` | Appended to the vulnerable-package scan. |

### Legacy keys (still honored)

| Legacy key | Replacement |
| --- | --- |
| `test.coverage.mode: none` | `test.coverage.enabled: false` |
| `analyze.formatExtraArgs` | `analyze.format.extraArgs` |
| `analyze.buildExtraArgs` | `analyze.build.extraArgs` |
| `analyze.sarifVersion` | `analyze.sarif.version` |
| `analyze.sarifCategory` | `analyze.sarif.category` |

Legacy keys take precedence over their replacements when both are set. Note that setting a legacy
key such as `sarifVersion` no longer enables SARIF by itself — set `analyze.sarif.enabled: true`.

## Analysis

By default `analyze` runs a single incremental `dotnet build` (`dotnet-build-warnings-no-sarif`).
It does not force warnings as errors; add `/p:TreatWarningsAsErrors=true` through
`vars.dotnet.analyze.build.extraArgs` if you want that.

### SARIF (opt-in)

With `analyze.sarif.enabled: true` (and a non-empty `outputs.analysis.sarif`), `analyze` emits
GitHub-compatible **SARIF 2.1.0**, including for multi-project and multi-targeted solutions:

1. `builtin:dotnet-sarif-targets` writes `<outputs.temp>/msbuild/Rexo.Sarif.targets` and clears
   `<outputs.temp>/sarif/dotnet`.
2. The analysis `dotnet build` runs with `--no-incremental` (so the compiler always runs and logs)
   and injects the targets file via `-p:CustomAfterMicrosoftCommonTargets=...`. Each project/TFM
   writes its own `ErrorLog` (`<Project>.<TFM>.sarif`).
3. `builtin:sarif-merge` (always runs, even if the build fails) merges the per-project logs into
   `<outputs.analysis.sarif>/dotnet-build.sarif`: one run per tool, rules de-duplicated, identical
   results de-duplicated, file URIs made repository-relative, and `automationDetails.id` set from the
   category. GitHub rejects uploads containing multiple runs with the same tool and category, so the
   merge is required.

```yaml
- run: rx analyze
- uses: github/codeql-action/upload-sarif@v3
  if: always()
  with:
    sarif_file: artifacts/analysis/sarif/dotnet-build.sarif
```

Caveat: during a SARIF-enabled `rx analyze`, the global `CustomAfterMicrosoftCommonTargets` property
overrides any value your repository sets for it (`Directory.Build.targets` is unaffected).

## Security (opt-in)

`dotnet list package --vulnerable` exits `0` even when vulnerabilities are found, so this step is
informational. To gate builds on vulnerabilities, enable NuGet Audit in your projects instead
(`<NuGetAudit>true</NuGetAudit>` with `<WarningsAsErrors>NU1903;NU1904</WarningsAsErrors>`).

## Containers

The policy ships a `dotnet-sdk` container definition (`mcr.microsoft.com/dotnet/sdk:10.0`, working
directory `/work`). Every command uses `container: "{{vars.dotnet.container}}"`, so:

```yaml
vars:
  dotnet:
    container: dotnet-sdk          # run all dotnet steps in the SDK image
containers:
  dotnet-sdk:
    image: mcr.microsoft.com/dotnet/sdk:9.0   # optionally override the image (other fields are kept)
```

See [Containerized run steps](../configuration/containerized-run.md).

## Common workflow

```bash
rx restore
rx verify
rx format
rx release --push
```
