# `rx` CLI – Comprehensive Build Plan

## 1. Product Summary

`rx` is a repository-native automation CLI.

It provides a single standard interface for:

- Repository workflows
- Versioning
- Validation
- Testing
- Static analysis
- Artifact building
- Artifact tagging
- Artifact pushing
- Release orchestration
- Local developer workflows
- CI/CD execution

The core idea:

```text
rx = repository execution engine
```

The tool should allow every repository to define its behaviour through configuration, policy templates, and provider templates, while keeping the CLI interface minimal and stable.

---

## 2. Core Design Principles

```text
CLI is the contract.
Config defines repo behaviour.
Policies define company standards.
Engine executes behaviour.
UI visualises and guides behaviour.
CI is only a runner.
```

### Principles

- One tool
- One config model
- One execution engine
- Same behaviour locally and in CI
- Minimal hardcoded CLI surface
- Config-defined command surface
- Policy-driven defaults
- Provider configs can be generated
- Everything produced is an artifact
- Everything important is inspectable
- Human-readable and machine-readable output
- Safe by default
- Explicit push/release intent
- Modular internal architecture

---

## 3. Target Usage

### Local

```bash
rx list
rx explain release
rx release
rx release --push
rx branch feature customer-search
rx ui
```

### CI

```bash
rx release --push --json-file artifacts/manifests/release.json
```

### With explicit run command

```bash
rx run release --push
rx run branch feature customer-search
```

---

## 4. Minimal Base CLI

The fixed CLI should remain small.

```bash
rx run <command>
rx list
rx explain <command>
rx doctor
rx version
rx help
rx ui
```

Optional direct command execution:

```bash
rx <configured-command>
```

Examples:

```bash
rx release --push
rx verify
rx branch feature my-change
rx config resolved
```

These are resolved from config, not hardcoded.

---

## 5. Command Resolution

Command resolution order:

```text
1. Built-in kernel command
2. Exact config command match
3. Config alias match
4. Policy-provided command match
5. Error with suggestions
```

Example:

```bash
rx branch feature customer-search
```

Could resolve to the configured command:

```text
branch feature
```

With argument:

```text
name = customer-search
```

---

## 6. Configuration Model

Default config file:

```text
repo.json
```

Alternative supported names later:

```text
repo.yaml
.repo/repo.json
.repo/config.json
```

Initial recommendation:

```text
repo.json only
```

to keep v1 simple.

---

## 7. Config Responsibilities

`repo.json` can define:

- Metadata
- Policy templates
- Provider templates
- Commands
- Aliases
- Versioning
- Artifacts
- Tests
- Coverage
- Analysis
- Security scanning
- Push rules
- Branching workflows
- Environment overlays
- Provider materialisation
- Output paths

---

## 8. Config Example

```json
{
  "name": "orders-api",
  "extends": [
    "company:policies/dotnet-api",
    "company:versioning/gitversion-mainline",
    "company:analysis/strict-dotnet",
    "company:artifacts/docker-ghcr"
  ],
  "artifacts": [
    {
      "type": "docker",
      "name": "api",
      "image": "ghcr.io/company/orders-api"
    },
    {
      "type": "nuget",
      "name": "client",
      "project": "src/Orders.Client/Orders.Client.csproj",
      "source": "github-packages"
    }
  ],
  "commands": {
    "release": {
      "description": "Run full release",
      "options": {
        "push": {
          "type": "bool",
          "default": false
        }
      },
      "steps": [
        { "command": "validate" },
        { "uses": "builtin:resolve-version", "id": "version" },
        { "command": "verify" },
        { "command": "build" },
        { "command": "tag" },
        {
          "command": "push",
          "when": "{{options.push}}"
        }
      ]
    }
  }
}
```

---

## 9. Policy and Template System

Any config section can come from:

```text
inline repo.json
embedded template
local file
NuGet package
Git URL
HTTP URL
company registry
generated config
environment overlay
```

This applies to:

- Commands
- Aliases
- Versioning
- Provider configs
- GitVersion.yml
- Artifacts
- Docker tagging
- NuGet push rules
- Tests
- Coverage
- Analysis
- Branching workflows
- Release rules
- Security rules

---

## 10. Policy Template Examples

### Dotnet API Policy

```json
{
  "commands": {
    "validate": {
      "uses": "builtin:validate"
    },
    "test": {
      "uses": "command:test"
    },
    "analyze": {
      "uses": "command:analyze"
    },
    "verify": {
      "steps": [
        { "command": "test" },
        { "command": "analyze" }
      ]
    },
    "build": {
      "uses": "builtin:build-artifacts"
    },
    "tag": {
      "uses": "builtin:tag-artifacts"
    },
    "push": {
      "uses": "builtin:push-artifacts"
    }
  }
}
```

### GitHub Flow Policy

```json
{
  "commands": {
    "branch feature": {
      "args": {
        "name": {
          "required": true
        }
      },
      "steps": [
        { "run": "git checkout main" },
        { "run": "git pull" },
        { "run": "git checkout -b feature/{{args.name}}" }
      ]
    }
  }
}
```

### GitVersion Mainline Policy

```json
{
  "versioning": {
    "provider": "gitversion",
    "mode": "mainline",
    "tagPrefix": "v",
    "fallback": "0.1.0-local",
    "providerConfig": {
      "materialize": true,
      "path": ".repo/generated/GitVersion.yml",
      "template": "company:providers/gitversion-mainline"
    }
  }
}
```

---

## 11. Config Merge Rules

Config merge order:

```text
1. Built-in defaults
2. Embedded policy templates
3. Remote/local policy templates
4. Repo config
5. Environment overlays
6. CLI overrides
```

Later items override earlier items.

### Object Merge

```text
Objects merge recursively.
Scalars override.
Arrays default to replace unless configured otherwise.
```

Optional array merge strategy:

```json
{
  "merge": {
    "artifacts": "append",
    "commands": "merge",
    "analysis.tools": "append"
  }
}
```

Recommended v1:

```text
Objects merge.
Arrays replace.
Support explicit append later.
```

---

## 12. Config Inspection Commands

```bash
rx config resolved
rx config sources
rx config materialize
rx explain config
rx explain version
```

These can be config-defined, but the engine should provide built-in primitives for them.

---

## 13. Provider Config Materialisation

Provider configs can be generated on the fly from effective config.

Examples:

```text
GitVersion.yml
sonar-project.properties
trivy.yaml
coverlet.runsettings
NuGet.config
Docker metadata files
```

Materialisation modes:

```text
memory
temporary file
repo-generated file
explicit write
```

Recommended default:

```text
Generate into .repo/generated/
```

This folder should usually be ignored by Git.

Example:

```text
.repo/generated/GitVersion.yml
.repo/generated/sonar-project.properties
.repo/generated/trivy.yaml
```

---

## 14. Command Model

Commands are defined in config.

A command can have:

```text
description
args
options
env
workingDirectory
steps
parallel
maxParallel
when
outputs
```

---

## 15. Command Arguments

Example:

```json
{
  "commands": {
    "branch feature": {
      "args": {
        "name": {
          "required": true,
          "description": "Feature name"
        }
      },
      "steps": [
        { "run": "git checkout main" },
        { "run": "git pull" },
        { "run": "git checkout -b feature/{{args.name}}" }
      ]
    }
  }
}
```

Usage:

```bash
rx branch feature customer-search
```

---

## 16. Command Options

Example:

```json
{
  "commands": {
    "release": {
      "options": {
        "push": {
          "type": "bool",
          "default": false
        },
        "environment": {
          "type": "string",
          "default": "dev",
          "allowed": ["dev", "staging", "prod"]
        }
      }
    }
  }
}
```

Usage:

```bash
rx release --push --environment prod
```

Supported option types:

```text
bool
string
int
decimal
enum
array
path
secret
```

---

## 17. Step Types

Supported step types:

```text
run       shell command
uses      built-in primitive
command   configured command
parallel  parallel step group
```

Example:

```json
{
  "steps": [
    { "uses": "builtin:validate" },
    { "run": "dotnet restore" },
    { "command": "verify" },
    {
      "parallel": [
        { "command": "build docker" },
        { "command": "build nuget" }
      ]
    }
  ]
}
```

---

## 18. Sequential Execution

Default behaviour:

```text
Steps run sequentially.
A failed step stops the command unless continueOnError is true.
```

Example:

```json
{
  "steps": [
    { "command": "validate" },
    { "command": "verify" },
    { "command": "build" }
  ]
}
```

---

## 19. Parallel Execution

Example:

```json
{
  "steps": [
    {
      "parallel": [
        { "uses": "command:test" },
        { "uses": "command:analyze" }
      ]
    }
  ]
}
```

Optional:

```json
{
  "parallel": true,
  "maxParallel": 2,
  "steps": [
    { "uses": "command:test" },
    { "uses": "command:analyze" }
  ]
}
```

Rules:

```text
Parallel siblings cannot depend on each other.
Steps after a parallel group can consume all parallel outputs.
Parallel group fails if any child fails unless configured otherwise.
```

---

## 20. Conditions

Steps and commands can have conditions.

```json
{
  "command": "push",
  "when": "{{options.push}}"
}
```

Examples:

```text
{{options.push}}
{{version.isStable}}
{{repo.branch == 'main'}}
{{env.CI == 'true'}}
{{artifacts.docker.api.exists}}
```

---

## 21. Output Capture

Steps can capture outputs for later steps.

### Capture stdout

```json
{
  "id": "git",
  "run": "git rev-parse --short HEAD",
  "capture": {
    "shortSha": "stdout"
  }
}
```

### Capture JSON

```json
{
  "id": "version",
  "uses": "builtin:resolve-version",
  "outputs": {
    "semver": "$.semver",
    "shortSha": "$.shortSha"
  }
}
```

### Capture regex

```json
{
  "id": "extract",
  "run": "git describe --tags",
  "capture": {
    "tag": {
      "from": "stdout",
      "regex": "v?(?<version>\\d+\\.\\d+\\.\\d+)",
      "group": "version"
    }
  }
}
```

### Capture file

```json
{
  "id": "coverage",
  "run": "cat artifacts/tests/coverage/summary.json",
  "capture": {
    "coverage": {
      "from": "file",
      "path": "artifacts/tests/coverage/summary.json",
      "format": "json"
    }
  }
}
```

Supported captures:

```text
stdout
stderr
exitCode
JSONPath
regex
file
```

---

## 22. Execution Context

Available context:

```text
args
options
env
repo
version
steps
artifacts
outputs
policies
config
```

References:

```text
{{args.name}}
{{options.push}}
{{env.GITHUB_REF}}
{{repo.branch}}
{{version.semver}}
{{steps.git.outputs.shortSha}}
{{steps.version.outputs.semver}}
{{artifacts.docker.api.tags[0]}}
```

---

## 23. Templating

Use a small, deterministic templating engine.

Required capabilities:

```text
variable replacement
simple expressions
conditions
string functions
path helpers
default values
```

Examples:

```text
{{args.name}}
{{repo.branch | slug}}
{{version.semver}}
{{env.NUGET_API_KEY}}
{{options.environment | default('dev')}}
```

Avoid making this a full programming language.

---

## 24. Built-in Engine Primitives

Internal built-ins:

```text
builtin:validate
builtin:resolve-version
command:test
command:analyze
command:verify
builtin:build-artifacts
builtin:tag-artifacts
builtin:push-artifacts
builtin:config-resolved
builtin:config-materialize
builtin:doctor
```

These can be exposed through config commands.

---

## 25. Recommended Config Commands

Most repos will expose:

```text
validate
version
test
analyze
verify
build
tag
push
release
```

Recommended release flow:

```text
validate
version
verify
build
tag
push
```

---

## 26. Artifact Model

Everything produced is an artifact.

Artifact categories:

```text
verification artifacts
supply artifacts
metadata artifacts
```

Verification artifacts:

```text
test results
coverage reports
static analysis reports
security scan reports
```

Supply artifacts:

```text
docker images
nuget packages
zip/tar archives later
helm charts later
sbom files later
provenance attestations later
```

Metadata artifacts:

```text
run manifest
artifact manifest
release manifest
version manifest
```

---

## 27. Artifact Config

Example:

```json
{
  "artifacts": [
    {
      "type": "docker",
      "name": "api",
      "image": "ghcr.io/company/orders-api",
      "dockerfile": "Dockerfile",
      "context": "."
    },
    {
      "type": "nuget",
      "name": "client",
      "project": "src/Orders.Client/Orders.Client.csproj",
      "source": "github-packages"
    }
  ]
}
```

---

## 28. Docker Artifact

Docker artifact config:

```json
{
  "type": "docker",
  "name": "api",
  "image": "ghcr.io/company/orders-api",
  "dockerfile": "Dockerfile",
  "context": ".",
  "platforms": ["linux/amd64"],
  "tags": [
    "semver",
    "major-minor",
    "major",
    "branch",
    "sha",
    "latest-on-main"
  ],
  "labels": {
    "org.opencontainers.image.source": "{{repo.remoteUrl}}",
    "org.opencontainers.image.revision": "{{repo.commitSha}}",
    "org.opencontainers.image.version": "{{version.semver}}"
  },
  "push": {
    "enabled": true,
    "branches": ["main", "release/*"],
    "tags": ["v*"],
    "skipPullRequests": true
  }
}
```

---

## 29. NuGet Artifact

NuGet artifact config:

```json
{
  "type": "nuget",
  "name": "client",
  "project": "src/Orders.Client/Orders.Client.csproj",
  "source": "github-packages",
  "symbols": true,
  "version": "{{version.semver}}",
  "output": "artifacts/packages",
  "push": {
    "enabled": true,
    "branches": ["main", "release/*"],
    "tags": ["v*"],
    "skipPullRequests": true
  }
}
```

---

## 29.1 Helm Chart OCI Artifact (Later)

Helm chart OCI support is a planned artifact provider so a single repo can deliver
Docker images, NuGet packages, and Helm charts through the same artifact lifecycle.

Planned config shape:

```json
{
  "type": "helm-oci",
  "name": "orders-chart",
  "chartPath": "deploy/charts/orders",
  "registry": "ghcr.io/company",
  "repository": "orders",
  "version": "{{version.semver}}",
  "push": {
    "enabled": true,
    "branches": ["main", "release/*"]
  }
}
```

Planned lifecycle mapping:

```text
Build -> helm dependency update (optional) + helm package
Tag   -> chart/version tagging policy alignment
Push  -> helm push oci://<registry>/<repository>
```

This provider will reuse global push policy gates and manifest output behavior.

---

## 30. Artifact Tags

Docker tag strategies:

```text
semver           -> 1.4.2
major-minor      -> 1.4
major            -> 1
branch           -> main
sha              -> sha-abc1234
latest-on-main   -> latest
custom           -> custom templates
```

Example custom tags:

```json
{
  "tags": [
    "{{version.semver}}",
    "{{repo.branch | slug}}",
    "sha-{{repo.shortSha}}"
  ]
}
```

---

## 31. Push Rules

Push behaviour is config-driven.

Examples:

```text
push only on main
push only on release/*
push only on tags
never push on pull requests
require --push locally
require clean working tree
require signed tag
require verification success
```

Example:

```json
{
  "pushRules": {
    "requireExplicitPush": true,
    "skipPullRequests": true,
    "allowedBranches": ["main", "release/*"],
    "allowedTags": ["v*"],
    "requireCleanWorkingTree": true
  }
}
```

---

## 32. Versioning

Supported providers:

```text
gitversion
nbgv
minver
env
fixed
custom
```

Example:

```json
{
  "versioning": {
    "extends": "company:versioning/gitversion-mainline",
    "provider": "gitversion",
    "fallback": "0.1.0-local"
  }
}
```

---

## 33. Version Provider Output Contract

Every provider must return:

```text
semver
major
minor
patch
prerelease
buildMetadata
branch
commitSha
shortSha
isPrerelease
isStable
```

Optional:

```text
assemblyVersion
fileVersion
informationalVersion
nugetVersion
dockerVersion
weightedPreReleaseNumber
commitsSinceVersionSource
```

---

## 34. Version Provider Templates

Version provider configs can come from templates.

Example:

```json
{
  "versioning": {
    "extends": "company:versioning/gitversion-trunk",
    "provider": "gitversion",
    "overrides": {
      "tagPrefix": "v"
    }
  }
}
```

The template can define:

```text
GitVersion mode
branch rules
tag prefix
pre-release labels
fallback version
assembly versioning
NuGet versioning
Docker tag strategy
```

---

## 35. Testing

Test config:

```json
{
  "tests": {
    "enabled": true,
    "projects": ["tests/**/*.csproj"],
    "configuration": "Release",
    "results": {
      "enabled": true,
      "format": "trx",
      "output": "artifacts/tests"
    },
    "coverage": {
      "enabled": true,
      "format": ["cobertura", "lcov"],
      "output": "artifacts/tests/coverage",
      "threshold": {
        "line": 80,
        "branch": 70
      }
    }
  }
}
```

Test outputs:

```text
artifacts/tests/*.trx
artifacts/tests/coverage/coverage.cobertura.xml
artifacts/tests/coverage/lcov.info
artifacts/tests/coverage/summary.json
```

---

## 36. Static Analysis

Analysis config:

```json
{
  "analysis": {
    "enabled": true,
    "failOnIssues": true,
    "tools": [
      {
        "type": "dotnet-format"
      },
      {
        "type": "dotnet-build",
        "warnAsError": true
      },
      {
        "type": "trivy",
        "targets": ["filesystem", "docker"],
        "output": "artifacts/analysis/trivy.sarif"
      }
    ]
  }
}
```

Supported analysis tools over time:

```text
dotnet format
dotnet build warnings
Roslyn analyzers
SonarScanner
Trivy
Snyk
Hadolint
GitHub SARIF output
```

---

## 37. Verification

Verification is a first-class concept.

Recommended command:

```text
verify = test + analyze
```

Verification artifacts:

```text
test results
coverage reports
analysis reports
security scan reports
```

Release should not push if verification fails.

---

## 38. Output Folder Structure

Recommended:

```text
artifacts/
  tests/
  coverage/
  analysis/
  packages/
  docker/
  manifests/
  logs/
```

---

## 39. Run Manifest

Every execution should produce a manifest.

Default:

```text
artifacts/manifests/repo-run.json
```

Manifest should include:

```text
tool version
repo name
repo root
branch
commit SHA
remote URL
CI provider
resolved config hash
resolved version
command executed
options
steps executed
step timings
step exit codes
artifacts produced
artifact tags
push results
verification results
warnings
errors
```

---

## 40. JSON Output

Support:

```bash
rx run release --json
rx run release --json-file artifacts/manifests/release.json
rx list --json
rx explain release --json
rx config resolved --json
```

JSON output should be stable and versioned.

Example:

```json
{
  "schemaVersion": "1.0",
  "command": "release",
  "success": true,
  "version": {
    "semver": "1.4.2",
    "commitSha": "abc1234"
  },
  "artifacts": [
    {
      "type": "docker",
      "name": "api",
      "tags": [
        "ghcr.io/company/orders-api:1.4.2",
        "ghcr.io/company/orders-api:latest"
      ],
      "pushed": true
    }
  ]
}
```

---

## 41. Local and CI Parity

The same command must work locally and in CI.

Local:

```bash
rx release --push
```

CI:

```bash
rx release --push --json-file artifacts/manifests/release.json
```

CI config should be thin:

```yaml
steps:
  - checkout
  - setup dotnet
  - setup feed auth (platform-native where available)
  - dotnet tool restore
  - repo release --push
```

The logic lives in:

```text
repo.json
policy templates
provider templates
repo engine
```

---

## 42. CI Detection

Detect common CI providers:

```text
GitHub Actions
Azure DevOps
GitLab CI
Bitbucket Pipelines
TeamCity
Jenkins
local
```

Expose context:

```text
ci.isCi
ci.provider
ci.buildId
ci.runNumber
ci.pullRequest
ci.branch
ci.tag
```

---

## 43. Safety Rules

Default safety posture:

```text
No push unless explicitly allowed.
No local push unless --push is supplied.
No push on pull requests.
No push with dirty working tree if policy requires clean tree.
No release if verification fails.
No secrets in logs.
No interactive prompts in CI.
```

---

## 44. Secrets

Secrets can come from:

```text
environment variables
local secret store later
CI secrets
provider-specific auth
```

Secret options should be masked.

Feed authentication strategy (planned):

```text
Rexo resolves feed auth for artifact providers from env-mounted credentials.
When CI-native identity is available (OIDC/service connection/token provider), use it first.
Fallback to explicit env credentials when native identity is unavailable.
```

Expected behavior:

```text
Preflight checks validate required feed credentials before push steps.
Errors identify missing env names without printing secret values.
Logs remain redacted for all secret-bearing settings and command arguments.
```

Example:

```json
{
  "secrets": {
    "nugetApiKey": {
      "env": "NUGET_API_KEY"
    },
    "dockerPassword": {
      "env": "DOCKER_PASSWORD"
    }
  }
}
```

---

## 45. UI Strategy

Use the same engine and command registry.

```text
CLI = scriptable contract
UI = rich local presentation
```

CLI examples:

```bash
rx release --push
rx explain release
rx list
```

UI examples:

```bash
rx ui
rx init --interactive
rx release --interactive
rx explain release --interactive
```

UI features:

```text
command picker
policy browser
config browser
resolved config viewer
release plan preview
artifact status
test result viewer
coverage viewer
analysis viewer
log filtering
confirmation screens
progress dashboards
```

Rule:

```text
No UI-only behaviour.
UI invokes the same commands.
```

---

## 46. Technology Stack

Recommended:

```text
.NET global tool
Spectre.Console.Cli
Spectre.Console
custom execution engine
System.Text.Json
JSON Schema support
Scriban or Fluid for templating
CliWrap or custom process runner
```

Later:

```text
RazorConsole for richer TUI
```

---

## 47. Proposed Solution Structure

```text
Repo.sln

src/
  Repo.Cli/
  Repo.Core/
  Repo.Configuration/
  Repo.Execution/
  Repo.Templating/
  Repo.Policies/
  Repo.Versioning/
  Repo.Artifacts/
  Repo.Artifacts.Docker/
  Repo.Artifacts.NuGet/
  Repo.Verification/
  Repo.Analysis/
  Repo.Git/
  Repo.Ci/
  Repo.Ui/
  Repo.Tui/

tests/
  Repo.Core.Tests/
  Repo.Configuration.Tests/
  Repo.Execution.Tests/
  Repo.Integration.Tests/
```

---

## 48. Package Responsibilities

### Repo.Cli

```text
Spectre.Console.Cli setup
base command routing
global options
direct command dispatch
exit codes
```

### Repo.Core

```text
domain models
runtime context
result models
diagnostics
common abstractions
```

### Repo.Configuration

```text
load repo.json
resolve extends
merge configs
validate schema
produce effective config
```

### Repo.Execution

```text
command registry
command resolution
step execution
parallel execution
conditions
output capture
context updates
```

### Repo.Templating

```text
template rendering
expression evaluation
filters
safe variable access
```

### Repo.Policies

```text
template resolution
policy sources
policy caching
policy versioning
```

### Repo.Versioning

```text
version provider abstraction
GitVersion provider
NBGV provider
MinVer provider
env provider
fixed provider
custom provider
```

### Repo.Artifacts

```text
artifact abstraction
artifact build/tag/push lifecycle
artifact manifest
```

### Repo.Artifacts.Docker

```text
docker build
docker tag
docker push
docker labels
docker metadata
```

### Repo.Artifacts.NuGet

```text
dotnet pack
nuget push
symbols
package metadata
```

### Repo.Verification

```text
test orchestration
coverage handling
verification artifact model
```

### Repo.Analysis

```text
static analysis tools
security scans
SARIF handling
```

### Repo.Git

```text
branch info
commit info
tag info
working tree status
remote URL
branch workflow helpers
```

### Repo.Ci

```text
CI detection
CI context
pull request detection
provider-specific metadata
```

### Repo.Ui

```text
Spectre output
tables
trees
progress
log rendering
JSON rendering
```

### Repo.Tui

```text
RazorConsole UI later
```

---

## 49. Core Interfaces

### ICommandExecutor

```csharp
public interface ICommandExecutor
{
    Task<CommandResult> ExecuteAsync(
        string commandName,
        CommandInvocation invocation,
        CancellationToken cancellationToken);
}
```

### IStepExecutor

```csharp
public interface IStepExecutor
{
    Task<StepResult> ExecuteAsync(
        StepDefinition step,
        ExecutionContext context,
        CancellationToken cancellationToken);
}
```

### IVersionProvider

```csharp
public interface IVersionProvider
{
    Task<VersionResult> ResolveAsync(
        VersioningConfig config,
        ExecutionContext context,
        CancellationToken cancellationToken);
}
```

### IArtifactProvider

```csharp
public interface IArtifactProvider
{
    string Type { get; }

    Task<ArtifactBuildResult> BuildAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken);

    Task<ArtifactTagResult> TagAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken);

    Task<ArtifactPushResult> PushAsync(
        ArtifactConfig artifact,
        ExecutionContext context,
        CancellationToken cancellationToken);
}
```

### IPolicySource

```csharp
public interface IPolicySource
{
    Task<PolicyDocument> LoadAsync(
        string reference,
        CancellationToken cancellationToken);
}
```

### ITemplateRenderer

```csharp
public interface ITemplateRenderer
{
    string Render(string template, ExecutionContext context);
}
```

---

## 50. Error Handling

Use structured errors.

Error categories:

```text
configuration
policy-resolution
command-resolution
validation
versioning
execution
artifact-build
artifact-push
verification
analysis
auth
environment
```

Each error should include:

```text
code
message
details
suggested fix
source config path if available
step id if available
```

Example:

```text
REPO-CONFIG-001
Missing required artifact field: image
Location: artifacts[0].image
```

---

## 51. Exit Codes

Recommended:

```text
0   success
1   general failure
2   configuration error
3   validation error
4   verification failed
5   artifact build failed
6   artifact push failed
7   version resolution failed
8   command not found
9   environment/doctor failed
10  cancelled
```

---

## 52. Logging

Support levels:

```text
trace
debug
info
warn
error
silent
```

Global flags:

```bash
rx release --verbose
rx release --debug
rx release --quiet
rx release --json
```

Human output via Spectre.Console.

Machine output via JSON.

---

## 53. Artifact System Architecture

### Current state

`IArtifactProvider` is defined in `src/Core/Abstractions/`. Built-in providers (Docker, NuGet, Helm OCI) live in dedicated projects (`src/Artifacts.Docker/`, `src/Artifacts.NuGet/`, `src/Artifacts.Helm/`). Provider registration is hardcoded in the CLI bootstrapper (`CliBootstrapper.cs`/`Program.cs`). All providers are registered eagerly.

### Required structural changes

#### 1. Treat artifact implementations as true provider libraries

- Keep `IArtifactProvider` in `src/Core/Abstractions/`
- Each provider lives in its own package/project
- Provider projects depend only on `Rexo.Core` — no reference to CLI or Execution internals
- Provider implementation detail is isolated from the CLI and command runtime

#### 2. Make provider registration pluggable

- Remove hardcoded `artifactProviders.Register(...)` calls from CLI bootstrap
- Introduce plugin/discovery mechanism for providers
- Support provider registration from:
  - Configuration-driven type names
  - Provider assemblies dropped into a plugin folder
  - NuGet package references / package-based extension model
  - Explicit host registration API

#### 3. Keep runtime contract stable

`IArtifactProvider` contract remains minimal:

```csharp
string Type { get; }
BuildAsync(...)
TagAsync(...)
PushAsync(...)
GetPlannedTags(...)  // optional
```

Provider libraries implement that contract only.

#### 4. Separate artifact metadata from provider runtime

- Artifact type schema is provider-agnostic at the core level
- Provider-specific settings are validated by each provider
- Provider-specific settings namespaced under `artifacts[].settings`

#### 5. Support optional provider loading

- If a repo config references `type: "npm"` and provider is unavailable, error clearly
- CLI can run in command-aggregator mode without requiring all provider packages
- `rx doctor` reports "provider available" vs "provider missing" diagnostics

#### 6. Preserve built-in artifact lifecycle semantics

- `builtin:build-artifacts`, `builtin:tag-artifacts`, `builtin:push-artifacts`, `builtin:plan-artifacts`, `builtin:ship-artifacts`, `builtin:all-artifacts` remain host primitives
- Those builtins delegate only to providers resolved from the registry
- Provider libraries do not register builtins directly; the host wires artifact lifecycles to provider behaviour

### Host / provider boundary

**Host owns:**

- Artifact registry / discovery
- Command lifecycle builtins
- Artifact planning and filtering
- Execution context + version data

**Provider owns:**

- Build/tag/push implementation for its artifact type
- Provider-specific config validation and defaults
- Tag planning logic if needed (`GetPlannedTags`)

### Provider backlog

#### High-priority candidates

| Package | Type key |
| --- | --- |
| `Rexo.Artifacts.Npm` | `npm` |
| `Rexo.Artifacts.PyPi` | `pypi` |
| `Rexo.Artifacts.Maven` | `maven` |
| `Rexo.Artifacts.Generic` | `generic` |

#### Medium-priority candidates

| Package | Type key |
| --- | --- |
| `Rexo.Artifacts.Gradle` | `gradle` |
| `Rexo.Artifacts.RubyGems` | `rubygems` |
| `Rexo.Artifacts.Terraform` | `terraform` |
| `Rexo.Artifacts.Helm` | `helm` (non-OCI / generic chart) |
| `Rexo.Artifacts.DockerCompose` | `docker-compose` |

#### Lower-priority / niche candidates

`rpm`, `deb`, `aws-lambda`, `azure-function`, `gcp-function`, `npm-workspace`, `cargo`, `composer`, `conda`

#### Supporting providers for broader adoption

- Registry-credentials helper / shared auth provider abstraction
- Artifact-manifest producer (if output format differs per ecosystem)
- Package-index provider for GitHub Packages / Artifactory / Azure Artifacts

---

## 53. `doctor`

`repo doctor` should check:

```text
repo config exists
config is valid
policy templates resolve
provider configs materialize
git is available
dotnet is available
docker is available if needed
nuget auth is available if needed
docker auth is available if needed
CI context if in CI
required env vars exist
working tree state
branch/tag push eligibility
```

---

## 54. `explain`

`repo explain <command>` should show:

```text
resolved command
source policy/config
args/options
step graph
conditions
parallel groups
built-ins used
artifacts affected
push eligibility
provider configs
expected outputs
```

This is essential for trust.

---

## 55. `plan`

Although not a kernel command, most policies should expose:

```bash
rx plan
```

`plan` should show:

```text
resolved version
commands to run
verification steps
artifacts to build
tags to apply
push decisions
skipped steps and reasons
```

This can be a config command using built-ins.

---

## 56. Initial MVP Scope

MVP should include:

```text
.NET global tool
Spectre.Console.Cli
repo.json loading
config commands
args/options
sequential steps
run steps
command steps
uses built-ins
basic templating
basic output capture
version provider: fixed
version provider: env
Docker artifact build/tag/push
NuGet artifact pack/push
test command using dotnet test
basic artifacts folder
run manifest
doctor
list
explain
JSON output
```

Avoid in MVP:

```text
remote policy templates
RazorConsole
complex expression language
GitVersion integration
parallel execution
advanced analysis
materialisation
NuGet package policy source
```

---

## 57. MVP Milestones

### Milestone 1: CLI Shell

Deliver:

```text
rx version
rx help
rx doctor
rx list
rx run <command>
direct config command dispatch
```

### Milestone 2: Config Engine

Deliver:

```text
load repo.json
validate config
commands
aliases
args
options
basic schema
```

### Milestone 3: Execution Engine

Deliver:

```text
sequential steps
run step
command step
uses step
exit handling
step result model
context model
```

### Milestone 4: Templating

Deliver:

```text
{{args.name}}
{{options.push}}
{{env.X}}
{{steps.id.outputs.x}}
basic filters
```

### Milestone 5: Built-ins

Deliver:

```text
builtin:validate
builtin:resolve-version
command:test
builtin:build-artifacts
builtin:tag-artifacts
builtin:push-artifacts
```

### Milestone 6: Artifacts

Deliver:

```text
Docker artifact provider
NuGet artifact provider
artifact manifest
```

### Milestone 7: Versioning

Deliver:

```text
fixed provider
env provider
basic git provider
version context
```

### Milestone 8: Manifest + JSON

Deliver:

```text
run manifest
--json
--json-file
stable result schema
```

### Milestone 9: Policies

Deliver:

```text
local file extends
embedded policies
config merge
resolved config command
```

### Milestone 10: Verification

Deliver:

```text
dotnet test
trx output
coverage output
basic analysis hook
verify command via policy
```

---

## 58. Post-MVP Roadmap

### Phase 2

```text
GitVersion provider
NBGV provider
MinVer provider
parallel steps
conditions
regex capture
JSONPath capture
file capture
policy templates from Git
policy templates from NuGet packages
provider config materialisation
shared feed-auth resolution layer (env + CI-native fallback)
auth preflight validation for artifact push providers
```

### Phase 3

```text
RazorConsole UI
interactive init
interactive release
CI pipeline scaffolding via init command (GitHub Actions, Azure DevOps)
artifact browser
coverage viewer
analysis viewer
command graph viewer
```

### Phase 4

```text
SBOM
provenance
cosign signing
SLSA attestations
OCI artifact support
Helm charts (OCI provider)
Terraform modules
npm packages
container scanning
```

---

## 59. Example Minimal Repo

```text
repo.json
Dockerfile
src/
tests/
.config/dotnet-tools.json
```

`.config/dotnet-tools.json`:

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "repo": {
      "version": "0.1.0",
      "commands": ["repo"]
    }
  }
}
```

Run:

```bash
dotnet tool restore
rx release --push
```

---

## 60. Example GitHub Actions

```yaml
name: release

on:
  push:
    branches:
      - main
      - 'release/**'
    tags:
      - 'v*'
  pull_request:

permissions:
  contents: read
  packages: write

jobs:
  release:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v5
        with:
          fetch-depth: 0

      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '8.0.x'

      - name: Restore tools
        run: dotnet tool restore

      - name: Docker login
        if: github.event_name != 'pull_request'
        uses: docker/login-action@v4
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Release
        run: repo release --push --json-file artifacts/manifests/release.json
```

Future enhancement:

```text
Generate this file from `repo init ci --provider github` with repo-specific defaults.
Keep generated CI YAML thin and call repo commands for verify/release behavior.
```

---

## 60.1 Example Azure DevOps (Later)

```yaml
trigger:
  branches:
    include:
      - main
      - release/*

pr:
  branches:
    include:
      - '*'

pool:
  vmImage: ubuntu-latest

steps:
  - checkout: self
    fetchDepth: 0

  - task: UseDotNet@2
    inputs:
      packageType: sdk
      version: 8.0.x

  - script: dotnet tool restore
    displayName: Restore tools

  - script: rx release --push --json-file artifacts/manifests/release.json
    displayName: Release
```

Planned scaffolding command:

```text
rx init ci --provider azdo
rx init ci --provider github
rx init ci --provider both
```

---

## 61. Key Risks

### Config becoming too powerful

Mitigation:

```text
Keep expression language small.
Prefer built-ins over complex scripts.
Add explain/plan tooling early.
```

### Security risks from remote templates

Mitigation:

```text
Pin versions.
Support hashes.
Allow trusted sources.
Show resolved config.
Materialise before execution.
```

### Debuggability

Mitigation:

```text
Every step has id, source, timing, output.
repo explain is first-class.
run manifest is always produced.
```

### Scope creep

Mitigation:

```text
MVP supports Docker, NuGet, tests, basic versioning.
Everything else is post-MVP.
```

### Shell portability

Mitigation:

```text
Support shell selection.
Prefer built-ins for common operations.
Document cross-platform behaviour.
```

---

## 62. Non-Goals for v1

```text
Replacing Git entirely
Replacing CI providers
Replacing package registries
Full programming language in config
Complex UI first
Kubernetes deployment
Cloud provisioning
Secret management platform
```

---

## 63. Definition of Done for v1

v1 is successful when:

```text
A .NET API repo can run repo release locally.
The same repo can run repo release in GitHub Actions.
Docker image is built, tagged, and pushed.
NuGet package is packed and pushed.
Tests run and produce artifacts.
Version is resolved once and used everywhere.
Run manifest is produced.
Config can define custom commands.
Policy templates can provide defaults.
repo explain shows what will happen.
repo doctor catches missing prerequisites.
```

---

## 64. Final Positioning

```text
repo is not just a build tool.
repo is not just a release tool.
repo is not just a workflow tool.

repo is a config-driven repository runtime.
```

It standardises:

```text
how a repository is validated
how a repository is versioned
how a repository is verified
how a repository creates artifacts
how a repository pushes artifacts
how a repository exposes workflows
how humans and CI interact with it
```

