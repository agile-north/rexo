---
applyTo: "rexo.yaml,rexo.yml,rexo.json,.rexo/**,policy.yaml,policy.yml,policy.json"
---

# Rexo configuration context

This repository uses [Rexo](https://github.com/agile-north/rexo) (`rx`) — a config-driven
repository automation CLI. Build, versioning, artifact, and release workflows are defined in a
single config file and run identically locally and in CI.

## Key files

| File | Purpose |
| ---- | ------- |
| `.rexo/rexo.yaml` (or `.rexo/rexo.json`, root `rexo.yaml`/`rexo.json`) | Main repo config: commands, versioning, artifacts, and output paths. YAML is the default; JSON is equally supported |
| `.rexo/policy.yaml` (or `.rexo/policy.json`, root `policy.*`) | Policy overlay: org-level commands and defaults |

## Documentation

- **Full configuration reference**: https://github.com/agile-north/rexo/blob/release/next/docs/configuration/README.md
- **Rexo schema** (all valid config fields with types): https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
- **Policy schema** (all valid policy fields with types): https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json
- **Architecture overview**: https://github.com/agile-north/rexo/blob/release/next/docs/ARCHITECTURE.md
- **CI/CD emission reference** (complete safe/full variables and gates): https://github.com/agile-north/rexo/blob/release/next/docs/configuration/ci-output-emission.md
- **GitHub release policy** (vars, guards, local/container use, Actions example): https://github.com/agile-north/rexo/blob/release/next/docs/embedded/github-release.md

## Quick reference

### Config file structure

YAML files start with a `# yaml-language-server: $schema=...` modeline for editor intellisense; the structure is identical to JSON (shown below).

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "extends": ["./base/rexo.json"],       // optional: inherit from other configs
  "policySources": ["nuget:MyOrg.Policies@1.0.0#policies/base.json"], // optional: remote policy sources (opt-in)
  "commands": { ... },
  "aliases": { ... },
  "versioning": { ... },
  "artifacts": [ ... ],
  "outputs": { ... }
}
```

### Commands and steps

```jsonc
"commands": {
  "build": {
    "description": "Build the project",
    "options": { "configuration": { "type": "string", "default": "Release" } },
    "args":    { "target":        { "required": false } },
    "steps": [
      { "run": "dotnet build -c {{options.configuration}}" },
      { "uses": "builtin:resolve-version" },
      { "command": "test" }
    ]
  }
}
```

Each step uses exactly one of:
- `"run"` — shell command (supports `{{variable}}` templates)
- `"uses"` — built-in primitive (see below)
- `"command"` — delegate to another configured command

### Built-ins and policy commands

| Primitive | Purpose |
| --- | --- |
| `builtin:validate` | Validate the loaded config |
| `builtin:resolve-version` | Run the version provider; populate `context.Version` |
| `builtin:build-artifacts` | Build all configured artifacts |
| `builtin:tag-artifacts` | Tag artifacts with version tags |
| `builtin:push-artifacts` | Push artifacts; write `<outputs.root>/manifest.json` when `outputs.emit` is enabled |
| `builtin:config-resolved` | Print the merged config as JSON |
| `builtin:config-materialize` | Write provider config files (e.g. `GitVersion.yml`) |

`test`, `analyze`, and `verify` are policy-provided commands delegated through
`"command": "..."` steps, not `uses` builtins.

### Template variables in `run` steps

| Variable | Value |
| --- | --- |
| `{{args.<name>}}` | CLI positional/named argument |
| `{{options.<name>}}` | CLI option flag |
| `{{env.<VAR>}}` | Environment variable |
| `{{version.semVer}}` | Resolved semantic version |
| `{{steps.<id>.output.<key>}}` | Output from a completed step |

Filters: `| slug`, `| upper`, `| lower`, `| default(fallback)`, `| coalesce(a, b, 'fallback')`; coalescing operator: `??`

### Versioning

```jsonc
"versioning": {
  "provider": "auto",         // auto | fixed | env | git | gitversion | minver | nbgv
  "settings": { "fallback": "0.1.0" }
}
```

### Artifacts

```jsonc
"artifacts": [
  { "type": "docker", "name": "my-image",   "settings": { "dockerfile": "Dockerfile", "registry": "ghcr.io/org" } },
  { "type": "nuget",  "name": "MyPackage",  "settings": { "project": "src/MyLib/MyLib.csproj" } }
]
```

### Policy files

Policy files have the same schema as `rexo.json`. They provide org-level default commands
(e.g. `ci`, `release`) that are merged with the repository config. Create one with:

```bash
rx init --with-policy --policy dotnet
```

### GitHub test and coverage reports

For GitHub test/coverage display, import `embedded:github-tests` and
`embedded:github-coverage` **after** the test toolchain (`embedded:dotnet` or another producer).
They append `alwaysRun` steps to `test`, so normal test failures still get reported.
Inputs are stack-neutral: TRX/JUnit/NUnit/xUnit for tests, and
Cobertura/OpenCover/LCOV/JaCoCo/Clover for coverage. Configure `vars.githubTests` and
`vars.githubCoverage`, not custom workflow report scripts. Coverage uses pinned
containerized ReportGenerator (Docker required); test Checks use built-in parsing/API calls.

Publishing defaults to automatic in Actions, explicit opt-in (`--publish-reports` or
`publish: always`) locally. `rx reports tests` / `rx reports coverage` process existing
reports. Checks require an explicit GitHub App/GITHUB_TOKEN with `checks: write`;
fork/Dependabot contexts may skip. Set unique scopes for matrix jobs. Artifact uploads
require Actions runtime credentials, not merely GITHUB_TOKEN; never claim local workflow
artifact uploads work. See the complete configuration, output and permission contract:
https://github.com/agile-north/rexo/blob/release/next/docs/embedded/github-reports.md

For ordinary `run: rx verify` workflows needing downloadable reports, set
`vars.githubTests.uploadArtifact: false` and `vars.githubCoverage.uploadArtifact: false`,
then add one `actions/upload-artifact@v4` step with `if: always()`,
`path: artifacts/tests/`, `if-no-files-found: warn`, and `retention-days: 14`.
This uploads local outputs only; keep parsing, Checks, summaries and thresholds in Rexo.
Adjust paths for configured output locations and use unique artifact names for matrix
axes/shards and run attempts. See the shared reporting reference's shell-workflow example.

Use `rx reports tests --report-preview` / `rx reports coverage --report-preview` to inspect
resolved discovery without writes or publishing. `artifactUpload: auto` skips unavailable
runtime upload credentials informationally; `required` makes their absence fail reporting.
Inspect `checkStatus` / `checkSkipReason` / `artifactStatus`, not just command success.
Configured coverage thresholds fail when reports or metrics are missing. GitHub policy
vars have typed schema completion and typo checking; keep new settings synchronized with
the policy schema, runtime validation, documentation and tests.

### GitHub releases without workflow scripts

When the user wants GitHub releases, include the shipped policy alongside standard/toolchain policies:

```yaml
extends:
  - embedded:standard
  - embedded:github-release
vars:
  githubRelease:
    draft: false
```

`rx release --push` runs the lifecycle and then creates an exact-commit tag/GitHub release.
The policy defaults to a pinned `gh` container (Docker required; no host `gh`) with an explicit
`GH_TOKEN`/`GITHUB_TOKEN`. GitHub Actions needs `contents: write` and token mapping.
All selected artifacts must have published; source-only repositories are allowed. Dry runs,
failed lifecycle work, and global push restrictions prevent publication. Group releases use
`<group>/v<version>` tags; default/all-group releases use `v<version>`. Do not also add
`embedded:git-tag`: this policy owns its tags. Configure behavior through `vars.githubRelease`;
read the policy reference above rather than inventing options or copying shell gates.

For external downstream CI steps, use `REXO_SUCCESS` plus `REXO_ARTIFACTS_ALL_PUSHED`;
`REXO_ARTIFACTS_ANY_PUSHED` / `REXO_ARTIFACTS_PUSHED_COUNT` describe partial publication.
For GitHub step outputs set `outputs.ci.github-actions.scope: output` (default is `env`).
The embedded GitHub release policy uses internal context and does not depend on CI emission.

## Useful CLI commands

```bash
rx list                      # list all available commands (config + policy + built-ins)
rx explain <command>         # show description, args, options, and steps
rx graph <command>           # inspect effective steps as text, JSON, or Mermaid
rx completion <shell>        # generate bash, zsh, fish, or PowerShell command completions
rx config sources            # show which config files were loaded
rx config resolved           # show the final merged config as JSON
rx doctor                    # check tool and provider availability
rx check [--strict]          # inspect config/tool readiness; warnings fail only with --strict
rx secrets doctor            # validate configured secret resolution safely
rx policies list             # list importable embedded policies
rx policies show github-release # inspect the shipped GitHub release policy
```

`rx check` does not build, publish, deploy, or resolve secret values. Use `rx secrets preflight`
when you need to verify that required secret providers can currently resolve their values.
