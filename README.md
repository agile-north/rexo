# Rexo

[![Release](https://github.com/agile-north/rexo/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/agile-north/rexo/actions/workflows/release.yml)
[![Workflow validation](https://github.com/agile-north/rexo/actions/workflows/workflow-validation.yml/badge.svg?branch=main)](https://github.com/agile-north/rexo/actions/workflows/workflow-validation.yml)
[![NuGet version](https://img.shields.io/nuget/v/Rexo.Cli)](https://www.nuget.org/packages/Rexo.Cli)
[![NuGet downloads](https://img.shields.io/nuget/dt/Rexo.Cli)](https://www.nuget.org/packages/Rexo.Cli)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Rexo is a config-driven repository command runtime for local and CI workflows. Use it as a lightweight command/alias system, or opt into lifecycle policies for build, verification, artifact production, and release orchestration.

## Quickstart

### Minimal command runtime

```bash
# Install Rexo globally
dotnet tool install --global Rexo.Cli

# Initialize — the wizard asks what you need
rx init

# Or non-interactive with no policy (pure command/alias runtime)
rx init --stack blank --yes

# Run a configured command
rx hello

# Inspect effective repository readiness and the configured command graph
rx check
rx graph hello --format mermaid
```

### Standard lifecycle (opt-in)

```bash
# Initialize — answer yes to "Will this repo build and publish artifacts?"
# and the wizard adds embedded:standard automatically
rx init

# Or non-interactive with explicit policy
rx init --stack auto --with-policy --yes

# See what would happen
rx plan

# Run the full release pipeline (build + tag, no push)
rx release

# Request push with the default standard policy (runtime push gates still apply)
rx release --push
```

The default standard policy does not push from plain `release`. Repositories can override
the lifecycle in configuration. This repository dogfoods a single plain `release` call;
its config gates publication on CI publication intent, PR context and allowed branches.
See [Repository release workflow](docs/DEVELOPMENT.md#repository-release-workflow).

## Stack vs Policy

These two `rx init` concepts are distinct:

**`--stack`** — the technology stack of the repository. Tells the wizard what kind of project you have so it can scaffold an appropriate starter `.rexo/rexo.yaml`. Valid values: `auto` (detect from disk), `dotnet`, `node`, `python`, `go`, `java`, `ruby`, `generic`, `blank`. The stack shapes the generated config — which artifact type to add, what project-specific convenience commands to include (`local build`, `local test`), and so on. The stack choice is a one-time scaffolding decision.

**`--policy`** — which embedded policy to adopt. When you pass `--with-policy`, a `.rexo/policy.yaml` file is written alongside `.rexo/rexo.yaml`. `standard` provides shared lifecycle commands (`build`, `test`, `verify`, `release`, `plan`, `push`, etc.); `dotnet` and `node` add toolchain overlays, and `git-tag` adds post-push Git tagging. See [Embedded Policies](docs/embedded/README.md). A project's stack and policy are independent: you can have a `node` stack with a `standard` policy, or a `dotnet` stack with no policy at all. Artifact detection may automatically add `embedded:standard`; use `--stack blank` for a policy-free scaffold.

## Config Format and Location

Rexo config can be YAML or JSON — both are validated against the same JSON Schema and behave identically. `rx init` writes YAML to `.rexo/rexo.yaml` by default (`--format json` writes `.rexo/rexo.json`).

Discovery order (first match wins): `.rexo/rexo.yaml`, `.rexo/rexo.yml`, `.rexo/rexo.json`, then the repo root (`rexo.yaml`, `rexo.yml`, `rexo.json`). Rexo warns if more than one exists.

Generated YAML starts with a `# yaml-language-server: $schema=...` modeline, so editors such as VS Code (Red Hat YAML extension) and JetBrains IDEs provide completion, hover docs, and validation. See [Configuration Reference](docs/configuration/README.md#editor-intellisense).

## Goals

- Keep the CLI contract minimal and stable
- Drive behavior through repository configuration and policies
- Keep local and CI behavior identical
- Produce inspectable run and release artifacts

## Documentation

**Configuration & Behavior:**

- [Configuration Reference](docs/configuration/README.md) — Schema, structure, defaults
- [CLI workflow commands](docs/configuration/README.md#cli-workflow-and-safety) — check, explain, graph, shell completion, policy locks, and local artifact promotion
- [Commands & Steps](docs/configuration/commands.md) — Commands, options, merge semantics
- [Versioning](docs/configuration/versioning.md) — Version providers, auto-detection
- [Runtime Settings](docs/configuration/runtime.md) — Output, push policy, tests, analysis
- [Output Contract](docs/configuration/output-contract.md) — `--json`, `--json-file`, sidecar manifest fields
- [Template Variables](docs/configuration/templates.md) — Variables, filters, expressions

**Lifecycle & Execution:**

- [Builtins Reference](docs/builtins/README.md) — Overview and index of all builtins
- [Embedded Policies](docs/embedded/README.md) — standard and dotnet lifecycle policies
  - [standard policy](docs/embedded/standard.md)
  - [dotnet policy](docs/embedded/dotnet.md)

**Resources:**

- [Artifact Providers](docs/artifacts/README.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Development Guide](docs/DEVELOPMENT.md)

## Minimal Configuration

Rexo does not apply lifecycle commands automatically.

No extends means no policy commands.

A config can be as small as a command and alias map:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "commands": {
    "hello": {
      "steps": [
        {
          "run": "echo hello"
        }
      ]
    },
    "local build": {
      "steps": [
        {
          "run": "dotnet build"
        }
      ]
    }
  },
  "aliases": {
    "b": "local build"
  }
}
```

To opt into the standard lifecycle, extend embedded:standard:

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-api",
  "extends": [
    "embedded:standard"
  ],
  "artifacts": [
    {
      "type": "docker",
      "name": "api",
      "settings": {
        "image": "ghcr.io/my-org/my-api",
        "dockerfile": "Dockerfile",
        "context": "."
      }
    }
  ]
}
```

With embedded:standard, Rexo provides these commands:

```bash
rx plan                 # What would happen
rx validate             # Check config
rx version              # Show resolved version
rx test                 # Run tests
rx analyze              # Run analysis
rx verify               # Quality gate (test + analyze)
rx build                # Build and tag artifacts locally
rx tag                  # Tag artifacts
rx push                 # Push artifacts (requires --confirm locally)
rx release              # Full pipeline (validate → verify → build → tag)
rx release --push       # Full pipeline + push
rx clean                # Remove generated output
```

## Current Structure

- `src/` implementation projects
- `tests/` unit and integration test projects
- `docs/` architecture and design docs

## Prerequisites

- .NET SDK 10.0.203+
- Git

## Build

```bash
dotnet restore
dotnet build solution.slnx -c Release
dotnet test solution.slnx -c Release
```

## CLI

The packaged tool command is `rx`.

Run directly from NuGet without a permanent install (.NET 10 SDK):

```bash
dotnet dnx Rexo.Cli -- --help
dotnet dnx Rexo.Cli -- init --yes --stack auto
```

For a repository-local install, run `dotnet new tool-manifest` (if needed),
`dotnet tool install Rexo.Cli`, then `dotnet tool run rx -- --help`.

## Versioning

This repository uses GitVersion's GitHubFlow configuration via [GitVersion.yml](GitVersion.yml).
Consumers can choose a [version provider](docs/configuration/versioning.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

See [SECURITY.md](SECURITY.md).
