# Execution Environments

Execution environments provide target-specific variables, settings, and secrets to commands
that opt into them with `usesEnvironment: true`. They are selected explicitly with `--environment`, by
`REXO_ENVIRONMENT`, or automatically from a safe source-branch match. Branch-based
selection is a convenience, not an authorization boundary.

```yaml
environmentSelection:
  sourceBranch:
    variable: REXO_SOURCE_BRANCH # default
    fallback: auto                # default; use none to disable CI/Git discovery

environments:
  development:
    description: Development builds
    selection:
      branches:
        - feature/**
        - '!feature/production-*'
    vars:
      deploy:
        region: eastus
    settings:
      docker:
        registry: dev.example.test
    secrets:
      items:
        deployToken:
          provider: env
          env: DEV_DEPLOY_TOKEN
          required: false
    promotion:
      path: deployments/development
  production:
    description: Production releases
    selection:
      branches: [main]
    promotion:
      path: deployments/production
```

For a command with `usesEnvironment: true`, environment `vars` and `settings` are
deep-merged over repository values, and environment secret definitions overlay repository
secrets by logical name. Environment-specific secrets are preflighted when a command
declares them in `requiredSecrets` or a container file mount; globally required secrets
retain their normal preflight behavior.

CLI `--set` overrides remain the final configuration layer, including over selected
environment `vars` and `settings`. Environment selection and configuration are not an
authorization boundary; use CI permissions and deployment credentials to control access.

## Selection precedence

1. `--environment <name>`
2. `REXO_ENVIRONMENT`
3. Exactly one environment whose ordered `selection.branches` matches the source branch
4. No environment when there is no safe, unique match

An unset, empty, or whitespace-only `REXO_ENVIRONMENT` is ignored, including empty
values exported to child processes when no environment is selected. An explicitly
empty `--environment` remains an error.

An explicit environment overrides an automatic branch match. If multiple environments
match, Rexo fails with the matching names and patterns instead of choosing one. Configure
mutually exclusive selectors or pass an explicit environment.

Branch patterns are case-sensitive and ordered: `*` matches zero or more characters within
one path segment, `**` spans `/`, `**/` also matches zero directory levels, `?` matches one
character within a path segment, and `+` repeats the preceding literal or character class
one or more times. Character classes such as `[0-9]` are supported. A leading `!` excludes
a match, and a later positive pattern can re-include it. Escape supported metacharacters
with `\`. Patterns are bounded to 512 characters and 128 entries per environment.

Source-branch precedence is `--source-branch`, the configured source-branch environment
variable (default `REXO_SOURCE_BRANCH`), then CI metadata and local Git. GitHub Actions,
Azure DevOps, GitLab CI, and Bitbucket Pipelines branch refs are normalized. Tags,
detached `HEAD`, pull-request builds, and GitHub `workflow_run` do not automatically select
an environment. An explicit `--environment` can still be used for a PR when appropriate.
Set `environmentSelection.sourceBranch.fallback: none` to disable CI/local Git discovery;
an explicit `--source-branch` or configured branch variable still takes precedence.

Use the diagnostic command to inspect the effective choice:

```powershell
rx environment resolve --json
rx --environment production environment resolve --json
rx --source-branch refs/heads/feature/new-api environment resolve --json
```

The JSON result includes the selected environment, selection source, normalized source
branch and its origin, matching environments and patterns, and whether an explicit choice
overrode automatic matches.

Commands remain environment-optional by default. Set `requiresEnvironment: true` on a
command that must not run without a resolved environment. This is independent of
`usesEnvironment: true`, which applies the selected environment's `vars`, `settings`, and
`secrets` only to that command. Use both for environment-targeted operations. Environment-
independent build and release commands should omit `usesEnvironment`. Add command-level
`requiredSecrets` to preflight logical secrets before any step runs:

```yaml
commands:
  deploy:
    requiresEnvironment: true
    usesEnvironment: true
    requiredSecrets: [deployToken]
    steps:
      - run: ./scripts/deploy.sh
```

The selected environment is available to templates as `{{environment.name}}`,
`{{environment.sourceBranch}}`, `{{environment.selectionSource}}`,
`{{environment.branchSource}}`, `{{environment.matchedPattern}}`,
`{{environment.isExplicit}}`, and `{{environment.isResolved}}`.

## Use the same config in GitHub Actions and Azure Pipelines

The repository config below selects staging for `feature/**` branches and production for
`main`. Both CI providers pass the full source ref through the same `REXO_SOURCE_BRANCH`
variable and run the same Rexo command:

```yaml
environmentSelection:
  sourceBranch:
    variable: REXO_SOURCE_BRANCH
environments:
  staging:
    selection:
      branches: [feature/**]
    vars:
      deploy:
        target: staging
  production:
    selection:
      branches: [main]
    vars:
      deploy:
        target: production
commands:
  report-target:
    requiresEnvironment: true
    usesEnvironment: true
    steps:
      - run: echo target={{vars.deploy.target}} environment={{environment.name}}
```

GitHub Actions:

```yaml
name: Rexo environment
on:
  push:
    branches: [main, 'feature/**']
jobs:
  report:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.x
      - run: dotnet run --project src/Cli/Cli.csproj -- report-target
        env:
          REXO_SOURCE_BRANCH: ${{ github.ref }}
```

Azure Pipelines:

```yaml
trigger:
  branches:
    include:
      - main
      - feature/*
pool:
  vmImage: ubuntu-latest
steps:
  - checkout: self
  - task: UseDotNet@2
    inputs:
      packageType: sdk
      version: 10.x
  - script: dotnet run --project src/Cli/Cli.csproj -- report-target
    displayName: Resolve and report Rexo environment
    env:
      REXO_SOURCE_BRANCH: $(Build.SourceBranch)
```

For a deployed CLI tool rather than this repository's source project, replace the `dotnet
run` command with `rx report-target`; the config and source-branch variable remain the same.

## Container mounts and network

Container configuration supports an explicit `network` mode and `mounts`. Host mounts
must resolve to an existing path inside the repository unless
`allowOutsideRepository: true` is set. Mounts are read-only by default; set
`readOnly: false` only when the step needs to write to the mounted path. Secret mounts
materialize a logical secret into a temporary file and mount it read-only:

```yaml
containers:
  deploy:
    image: example/deployer:1
    network: bridge
    mounts:
      - source: config/deploy.yaml
        target: /run/rexo/deploy.yaml
      - secret: deployToken
        target: /run/secrets/deploy-token
```

See [Containerized Run Steps](containerized-run.md) for path checks, supported network
modes, secret-file lifecycle, and execution details.

## Local artifact promotion

Promotion uses the selected environment and its nested `promotion.path`. The old
`environments.<name>.path` form is not supported:

```powershell
rx --environment production promote artifacts/run-manifest.json
```

Migrate an existing environment by moving `path` under `promotion` and pass the
environment through the global `--environment` option:

```yaml
# Before
environments:
  staging:
    path: deployments/staging

# After
environments:
  staging:
    promotion:
      path: deployments/staging
```

Rexo verifies the manifest and the single local file artifact's SHA-256, then stores the
same bytes in a content-addressed directory under `promotion.path`. It does not rebuild,
deploy remotely, or promote mutable registry tags. `--dry-run` validates the source and
reports the destination without writing files.
