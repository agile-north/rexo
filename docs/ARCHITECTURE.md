# Architecture

Rexo is a modular .NET 10 solution with a small CLI kernel and a configuration-driven
execution engine. See [scope.md](scope.md) for the full product specification and
[AGENTS.md](../AGENTS.md) for the AI-agent-friendly technical reference.

---

## Layer Map

```text
┌─────────────────────────────────────────────────────┐
│  CLI  (src/Cli)                                     │
│  Program.cs — arg parse, multi-word resolve, output │
└────────────────────┬────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────┐
│  Configuration  (src/Configuration)                 │
│  RepoConfigurationLoader — load, schema-validate    │
│  Models — RepoConfig, CommandConfig, StepDefinition │
└────────────────────┬────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────┐
│  Execution  (src/Execution)                         │
│  CommandRegistry — maps name → ICommandHandler      │
│  DefaultCommandExecutor — dispatch + error boundary │
│  ConfigCommandLoader — builds handlers from config  │
│  StepExecutor — runs steps, threads ExecutionContext│
│  ShellRunner — spawns processes for `run` steps     │
│  BuiltinRegistry — `uses:` step dispatch            │
│  BuiltinCommandRegistration — wires built-ins       │
└────────┬───────────┬──────────┬─────────────────────┘
         │           │          │
┌────────▼──┐ ┌──────▼──┐ ┌─────▼────────────────────┐
│Versioning │ │Artifacts│ │Policy command overlays   │
│fixed/env/ │ │docker/  │ │dotnet/node/etc test,     │
│gitversion/│ │compose/ │ │analyze, verify behavior  │
│minver/nbgv│ │nuget/   │ └──────────────────────────┘
│git/auto   │ │helm*/   │
└───────────┘ │npm/pypi/ │
              │maven/    │
              │gradle/   │
              │rubygems/ │
              │terraform/│
              │generic   │
              └──────────┘
                     │
┌────────────────────▼────────────────────────────────┐
│  Core  (src/Core)  — zero project references        │
│  Abstractions: ICommandExecutor, IStepExecutor,     │
│    ITemplateRenderer, IVersionProvider,             │
│    IArtifactProvider, IPolicySource                 │
│  Models: ExecutionContext, CommandResult, StepResult│
│    VersionResult, RunManifest, CommandInvocation    │
└─────────────────────────────────────────────────────┘
```

Support services used by the runtime:

- `src/Git` — branch/SHA/remote/clean via `git` CLI
- `src/Ci` — detects GitHub Actions, Azure DevOps, GitLab, Bitbucket from env vars
- `src/Templating` — `{{variable | filter}}` interpolation in step `run` strings
- `src/Ui` — Spectre.Console rich output for all result types
- `src/Policies` — policy source and embedded-template support for policy-provided defaults and commands

---

## Request Lifecycle

```text
rx branch feature my-change
        │
        ▼
Program.ExecuteAsync
  1. Parse global flags (--json, --json-file, --verbose, --debug, --quiet/-q)
  2. Load the discovered Rexo config (YAML or JSON) → RepoConfigurationLoader
      a. Validate $schema + schemaVersion metadata
      b. NJsonSchema validation against embedded schema (or local `rexo.schema.json` / `policy.schema.json`)
       c. JsonSerializer.Deserialize<RepoConfig>
       d. Resolve `extends` chain (breadth-first merge, child wins, circular detection)
  3. Build service graph (BuildServicesAsync)
       a. BuiltinCommandRegistration.CreateDefault(config, configPath)
       b. ConfigCommandLoader.LoadInto(registry, config, ...)
  4. Multi-word resolve: "branch feature" → command, "my-change" → arg
  5. DefaultCommandExecutor.ExecuteAsync("branch feature", invocation)
       a. CommandRegistry lookup → ICommandHandler
       b. StepExecutor: run step groups (sequential or parallel)
            - Consecutive `parallel: true` steps run via Task.WhenAll
            - "run" steps: ShellRunner.RunAsync (template-expanded shell cmd)
              · stdout captured via outputPattern (regex named groups)
              · stdout written to outputFile if configured
            - "uses" steps: BuiltinRegistry.DispatchAsync
            - "command" steps: recursive executor dispatch
       c. ExecutionContext accumulates step outputs + VersionResult
  6. CommandResult → ConsoleRenderer (rich or JSON)
  7. Return exit code
```

---

## Key Design Decisions

### `Core` has zero project references

`src/Core` only takes framework and built-in .NET packages. All abstractions live here,
ensuring no circular deps and making the domain model independently testable.

### Branding centralized in `Directory.Build.props`

```xml
<ProductRootName>Rexo</ProductRootName>      <!-- → assembly Rexo.Cli etc. -->
<CliToolCommandName>rx</CliToolCommandName>  <!-- → dotnet tool command    -->
```

Project folder names are plain (`Cli/`, `Core/`); MSBuild derives full names.

### Namespace

Source files use `Rexo.*` namespaces matching the `Rexo.<FolderName>` assembly names
derived by `Directory.Build.props`. New files should match the namespace already used in
the target project.

### Schema versioning

Config and policy schemas live at the repo root as `rexo.schema.json` and `policy.schema.json`. The loader validates the
`$schema` URI and `schemaVersion` string before NJsonSchema structural validation.
This allows future versions to ship new schema files (for example `rexo.schema.v2.json` and `policy.schema.v2.json`)
while the loader rejects old configs cleanly.

### Multi-word command resolution

Config commands can have spaces in their names (`"branch feature"`). The CLI resolves
by trying longest space-delimited prefixes first, allowing natural English-like
invocations: `rx branch feature my-ticket`.

---

## Project Dependency Graph

```text
Cli ──────────────────────────────────────────────────┐
    └→ Configuration, Execution, Artifacts.Docker,       │
      Artifacts.DockerCompose, Artifacts.Generic,       │
      Artifacts.Gradle, Artifacts.Helm,                 │
      Artifacts.Maven, Artifacts.Npm,                   │
      Artifacts.NuGet, Artifacts.PyPi,                  │
      Artifacts.RubyGems, Artifacts.Terraform,          │
      Versioning, Ui                                    │
                                                        │
Execution ─────────────────────────────────────────────┤
  └→ Core, Configuration, Templating, Versioning,      │
  Artifacts, Git, Ci,                               │
     Policies, Ui                                      │
                                                        │
Configuration ─────────────────────────────────────────┤
  └→ Core                                              │
                                                        │
Versioning, Artifacts.* ───────────────────────────────┤
  └→ Core                                              │
                                                        │
Core ─────────────────────────────────── (no deps) ────┘
```

---

## Extension Points

| To add… | Implement… | Register in… |
| --- | --- | --- |
| A new version provider | `IVersionProvider` | `VersionProviderRegistry.CreateDefault()` |
| A new artifact type | `IArtifactProvider` | `CliBootstrapper.RegisterConfigCommands` |
| A new built-in primitive | lambda in `ConfigCommandLoader.RegisterBuiltins` | `_builtinRegistry.Register("builtin:name", ...)` |
| A new built-in CLI command | method in `BuiltinCommandRegistration` | `registry.Register("name", ...)` |
| A new policy source | `IPolicySource` | (future — not wired yet) |

See [todo.md](todo.md) for the current implementation backlog.
