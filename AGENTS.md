# Rexo — Agent Context

This file provides structured context for AI coding agents (GitHub Copilot, Claude Code,
Codex, etc.) to continue work on this repository without re-reading conversation history.

---

## Project Identity

| Property | Value |
| ---------- | ------- |
| Product name | **Rexo** |
| CLI command | **`rx`** |
| Repository | `agile-north/rexo` |
| Language / SDK | C# / .NET 10 (`net10.0`) |
| Solution file | `solution.slnx` (slnx format) |

Rexo is a **config-driven repository runtime CLI**. A single config file (`.rexo/rexo.yaml` by default; JSON and the
repository root are also supported) drives build, versioning, artifact production, verification, analysis,
and release orchestration. The CLI is identical whether run locally or in CI.

---

## Quick Commands

```bash
# Build
dotnet build solution.slnx -c Release

# Test
dotnet test solution.slnx -c Release

# Build + test (no rebuild)
dotnet test solution.slnx -c Release --no-build
```

Build must produce **0 errors, 0 warnings** (`TreatWarningsAsErrors=true`).
All 234 tests must pass before any PR can be merged.

---

## Repository Layout

```text
solution.slnx               # Solution file (slnx format)
Directory.Build.props       # Central build conventions + branding
Directory.Build.targets     # Shared MSBuild targets
Directory.Packages.props    # Central NuGet version management
.rexo/rexo.yaml             # Example / self-describing config (YAML)
rexo.schema.json           # JSON Schema for rexo config (YAML or JSON) v1.0
policy.schema.json         # JSON Schema for policy config (YAML or JSON) v1.0
src/
  Artifacts/                # IArtifactProvider abstraction + registry
  Artifacts.Docker/         # Docker build/tag/push provider
  Artifacts.Helm/           # Helm OCI chart build/tag/push provider
  Artifacts.NuGet/          # dotnet pack/push provider
  Ci/                       # CI environment detector (GHA/AzDO/GitLab/Bitbucket)
  Cli/                      # CLI entry point (Program.cs) — packs as `rx` tool
  Configuration/            # rexo config loader (YAML/JSON) + NJsonSchema validation
  Core/                     # Domain models, interfaces (no external deps)
  Execution/                # Step executor, command registry, built-in primitives
  Git/                      # Git info detector (branch/SHA/remote/clean)
  Policies/                 # LocalFilePolicySource
  Templating/               # {{variable.path}} template engine with filters
  Tui/                      # Blazor/RazorConsole interactive TUI (`rx ui`)
  Ui/                       # ConsoleRenderer (Spectre.Console rich output)
  Versioning/               # VersionProviderRegistry + built-in providers
tests/
  Configuration.Tests/      # RepoConfigurationLoader tests
  Core.Tests/               # Domain model tests
  Execution.Tests/          # Executor, TemplateRenderer, BuiltinCommandRegistration
  Integration.Tests/        # Smoke: `rx version` exits 0
docs/
  scope.md                  # Full product scope (~2300 lines) — source of truth
  todo.md                   # Implementation checklist with ✅/⬜ status
  ARCHITECTURE.md           # Architecture narrative
  CONFIGURATION.md          # Config system reference
  DEVELOPMENT.md            # Developer guide
.github/
  workflows/                # build.yml, release.yml, codeql.yml
  copilot-instructions.md   # GitHub Copilot workspace instructions
```

---

## Naming Conventions

### Branding (centralized in `Directory.Build.props`)

```xml
<ProductRootName>Rexo</ProductRootName>
<CliToolCommandName>rx</CliToolCommandName>
```

### Assembly and namespace derivation

- Project folders are **plain names**: `Cli/`, `Core/`, `Configuration/`, etc.
- `AssemblyName` auto-derives as `Rexo.<FolderName>` (e.g. `Rexo.Cli`)
- `RootNamespace` auto-derives as `Rexo.<FolderName>` (e.g. `Rexo.Cli`)
- **IMPORTANT**: Source file namespaces use the `Rexo.*` prefix
  (e.g. `namespace Rexo.Cli;`), matching the `Rexo.<FolderName>` assembly names
  derived by `Directory.Build.props`. New files should match the namespace already
  used in the target project.

### Test projects

- Condition: project path contains `\tests\`
- `IsTestProject=true`, `IsPackable=false`, `GenerateDocumentationFile=false`

### CLI tool pack

- Condition: project path contains `\src\Cli`
- `PackAsTool=true`, `ToolCommandName=rx`

---

## Configuration System

### Config file location and format

Discovery order (first match wins, warning if several exist): `.rexo/rexo.yaml`,
`.rexo/rexo.yml`, `.rexo/rexo.json`, root `rexo.yaml|yml|json`, then legacy `.repo/repo.*`/`repo.*`
(`ConfigFileLocator`). YAML is converted to JSON by `YamlJsonConverter` (YAML 1.2 core schema,
modeline `# yaml-language-server: $schema=...` accepted in place of a `$schema` key) and then
validated exactly like JSON. `rx init` writes YAML by default (`--format json` for JSON).

### Required fields

Every config must start with (JSON shown; YAML is equivalent):

```json
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  ...
}
```

Alternative `$schema` values accepted:

- Remote canonical URL (once the repository is published)
- `https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json`
- `rexo.schema.json`
- `./rexo.schema.json`

### Validation flow (`RepoConfigurationLoader`)

1. For YAML files, convert to JSON (`YamlJsonConverter.ToJson`; modeline fills a missing `$schema`)
2. Check `$schema` is present and matches an allowed URI
3. Check `schemaVersion` is present and equals `"1.0"`
4. Validate full JSON against `rexo.schema.json` (embedded in assembly) via NJsonSchema
5. Deserialize to `RepoConfig`

The schema file is embedded in `Rexo.Configuration.dll`; a local `rexo.schema.json` at the repo root
can override it for development.

---

## Execution Model

```text
CLI args
  → Program.cs (global flag parse, multi-word command resolve)
  → DefaultCommandExecutor.ExecuteAsync(commandName, invocation)
  → CommandRegistry lookup
  → ICommandHandler.ExecuteAsync(invocation, cancellationToken)
     → For config commands: StepExecutor runs each StepDefinition
        → ShellRunner for `run` steps
        → BuiltinRegistry dispatch for `uses` steps
        → Sub-command dispatch for `command` steps
     → ExecutionContext accumulates step results + version
  → CommandResult (exit code, outputs, step results, version)
  → ConsoleRenderer (rich output or JSON)
```

### Multi-word command resolution

Given args `branch feature my-change`, the CLI tries longest prefix first:

1. Try `branch feature` as command name → found → arg `my-change`
2. Try `branch` as command name → would be tried if step 1 fails
3. Try `branch feature my-change` as exact name

### Built-in primitives (`uses:` step type)

| Primitive | Purpose |
| ----------- | --------- |
| `builtin:validate` | Config validation |
| `builtin:resolve-version` | Run version provider, set `context.Version` |
| `command:test` | Policy-provided test command (toolchain overlay) |
| `command:analyze` | Policy-provided analysis command (toolchain overlay) |
| `command:verify` | Policy-composed validation/test/analyze/security gate |
| `builtin:build-artifacts` | Build all configured artifacts |
| `builtin:tag-artifacts` | Tag artifacts with version tags |
| `builtin:push-artifacts` | Push artifacts to registries |

### Template engine (`TemplateRenderer`)

Variables in step `run` strings: `{{context.path}}`

Available context paths:

- `args.<name>` — positional/named args from CLI
- `options.<name>` — option flags from CLI
- `env.<VARIABLE>` — environment variables
- `repo.<field>` — repo config fields
- `version.<field>` — resolved version (after `builtin:resolve-version`)
- `steps.<stepId>.output.<key>` — output from earlier step

Filters (pipe syntax): `{{value | slug}}`, `{{value | upper}}`, `{{value | lower}}`,
`{{value | default(fallback)}}`

---

## Version Providers

| Provider key | Class | Notes |
| --- | --- | --- |
| `fixed` | `FixedVersionProvider` | Returns static version string from config |
| `env` | `EnvVersionProvider` | Reads env var, falls back to config fallback |
| `gitversion` | `GitVersionVersionProvider` | Runs `gitversion /output json`, parses SemVer 2.0 |

---

## Artifact Providers

| Provider key | Class | Notes |
| --- | --- | --- |
| `docker` | `DockerArtifactProvider` | `docker build`, `docker tag`, `docker push` |
| `docker-compose` | `DockerComposeArtifactProvider` | `docker compose build`, `docker compose push` |
| `nuget` | `NuGetArtifactProvider` | `dotnet pack`, `dotnet nuget push` |
| `helm-oci` | `HelmOciArtifactProvider` | `helm package` + OCI push/login, Docker fallback supported |
| `helm` | `HelmArtifactProvider` | classic chart package/push to HTTP chart repos |
| `npm` | `NpmArtifactProvider` | `npm pack`, `npm publish`, Docker fallback supported |
| `pypi` | `PyPiArtifactProvider` | `python -m build`, `twine upload`, Docker fallback supported |
| `maven` | `MavenArtifactProvider` | `mvn package/deploy`, Docker fallback supported |
| `gradle` | `GradleArtifactProvider` | `gradle build/publish`, wrapper-aware, Docker fallback supported |
| `rubygems` | `RubyGemsArtifactProvider` | `gem build`, `gem push`, Docker fallback supported |
| `terraform` | `TerraformArtifactProvider` | module/package workflow with Docker fallback |
| `generic` | `GenericArtifactProvider` | zip/file packaging and copy/publish flow |

---

## Key Interfaces (all in `src/Core/Abstractions/`)

```csharp
ICommandExecutor          // ExecuteAsync(commandName, invocation, ct)
IStepExecutor             // ExecuteAsync(step, context, ct)
ITemplateRenderer         // Render(templateText, context)
IVersionProvider          // ResolveAsync(config, ct)
IArtifactProvider         // BuildAsync / TagAsync / PushAsync
IPolicySource             // LoadAsync(root, ct)
```

---

## What Is Not Yet Implemented

See `docs/todo.md` for the full checklist. The implementation is feature-complete per `docs/scope.md`.
No known gaps remain at this time.

---

## Coding Standards

- `TreatWarningsAsErrors=true` — zero warnings policy
- `AnalysisLevel=latest-recommended` — Roslyn analyzers enforced
- Always pass `CancellationToken` through async call chains
- Use `CultureInfo.InvariantCulture` for `int.ToString()` and similar
- Avoid `new[] { ... }` literal arrays in hot paths — use `static readonly`
- `IReadOnlyList<T>` → use `.Count` not `.Length`
- No circular dependencies between projects — `Core` has zero project references

---

## Where to Look First

| Question | File |
| --- | --- |
| Full product scope & design decisions | `docs/scope.md` |
| What's done vs what's pending | `docs/todo.md` |
| Config model structure | `src/Configuration/Models/` |
| CLI routing | `src/Cli/Program.cs` |
| Built-in command registration | `src/Execution/BuiltinCommandRegistration.cs` |
| Config command loading | `src/Execution/ConfigCommandLoader.cs` |
| Step execution | `src/Execution/StepExecutor.cs` |
| Template rendering | `src/Templating/TemplateRenderer.cs` |
| Version resolution | `src/Versioning/` |
| Artifact build/push | `src/Artifacts.Docker/`, `src/Artifacts.NuGet/` |

