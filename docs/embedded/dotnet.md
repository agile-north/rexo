# Embedded Policy: dotnet

Purpose:

- Dotnet-focused command surface.
- Adds restore/format/pack-centric workflows.
- Recommended customization path is the `vars.dotnet.*` bag rather than overriding commands.

## Commands

### ci

Description: Validate, resolve version, test, analyze, and build configured artifacts.

Options: none.

### release

Description: Run CI flow, tag artifacts, optionally push.

Options:

- `--push` (`bool`, default `false`)

Behavior notes:

- Push step is guarded by a `when` expression.
- Push intent is forwarded into the builtin via `with.confirm = {{options.push}}`.

### restore

Description: Run `dotnet restore`.

Options: none.

### format

Description: Verify or apply `dotnet format`.

Options:

- `--fix` (`bool`, default `false`)

Behavior notes:

- `--fix` false: `dotnet format --verify-no-changes`
- `--fix` true: `dotnet format`

### pack

Description: Resolve version and build artifacts intended for package workflows.

Options:

- `--configuration` (`string`, default `"Release"`)

## Aliases

- `r` -> `restore`
- `f` -> `format`

## Customization Via vars.dotnet

Coverage is enabled by default for the dotnet overlay using `--collect:"XPlat Code Coverage"` and writes to `outputs.tests.coverage` by default.

Recommended customization path:

```json
{
  "extends": ["embedded:dotnet"],
  "vars": {
    "dotnet": {
      "solution": "solution.slnx",
      "configuration": "Release",
      "restore": {
        "extraArgs": "--locked-mode"
      },
      "build": {
        "extraArgs": "/p:ContinuousIntegrationBuild=true"
      },
      "test": {
        "runsettings": "eng/test.runsettings",
        "extraArgs": "--filter Category!=Slow",
        "coverage": {
          "mode": "xplat"
        }
      },
      "format": {
        "extraArgs": "--severity error"
      },
      "analyze": {
        "formatExtraArgs": "--severity warn",
        "buildExtraArgs": "/p:TreatWarningsAsErrors=true"
      }
    }
  }
}
```

The runtime materializes configured output directories before command steps run, so
`analyze` can write reports and SARIF files without shell-specific setup. Empty directories
that remain unused are removed after the command completes.

The built-in `dotnet build` analysis step also converts the SARIF path to a repo-absolute
path before passing it to Roslyn, which avoids project-local `artifacts/...` lookups when
multi-project solutions write a shared `ErrorLog`.

The analysis `dotnet build` step does not force warnings as errors by default. If you want
that behavior, add `/p:TreatWarningsAsErrors=true` through `vars.dotnet.analyze.buildExtraArgs`.

### Supported Optional vars

- `vars.dotnet.solution`: solution or project path passed to dotnet commands.
- `vars.dotnet.configuration`: build/test configuration. Default: `Release`.
- `vars.dotnet.restore.extraArgs`: appended to `dotnet restore`.
- `vars.dotnet.build.extraArgs`: appended to `dotnet build`.
- `vars.dotnet.test.runsettings`: passed as `--settings <path>`.
- `vars.dotnet.test.extraArgs`: appended to `dotnet test`.
- `vars.dotnet.test.coverage.mode`: `xplat` (default) or `none`.
- `vars.dotnet.format.extraArgs`: appended to `dotnet format`.
- `vars.dotnet.analyze.formatExtraArgs`: appended to `dotnet format --verify-no-changes`.
- `vars.dotnet.analyze.buildExtraArgs`: appended to the analysis `dotnet build` step.

### Coverage Mode Behavior

- Coverage is enabled by default for the dotnet overlay.
- Set `vars.dotnet.test.coverage.mode` to `none` to disable coverage without overriding the `test` command.
- If you need a non-standard collector or a completely custom test invocation, overriding the `test` command is still the fallback.

## Common Workflow

```bash
rx restore
rx ci
rx format --fix
rx release --push
```

See [Common Use Cases](../EMBEDDED.md#use-case-dotnet-developer-convenience) for more examples.
