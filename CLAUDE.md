# Rexo — Claude Code Instructions

This file is read automatically by Claude Code when working in this repository.
It complements `AGENTS.md`, which contains the full technical reference.

---

## Build

```bash
dotnet build solution.slnx -c Release
dotnet test solution.slnx -c Release --no-build
```

Build must be clean: **0 errors, 0 warnings**. Tests must all pass before committing.

---

## Project at a glance

- **Product**: Rexo — config-driven repository automation CLI
- **CLI command**: `rx`
- **Stack**: .NET 10, C#, xUnit, Spectre.Console, NJsonSchema
- **Solution**: `solution.slnx`
- **Config file**: `.rexo/rexo.yaml` (default; `.rexo/rexo.json` and root `rexo.yaml|json` also supported) — requires `$schema` (key or YAML modeline) + `schemaVersion: "1.0"`
- **Schema**: `rexo.schema.json` (repo root)

---

## Critical conventions

1. **`src/Core/` has zero project references** — never add a `<ProjectReference>` here.
2. **Namespace prefix in source files is `Rexo.*`** (e.g. `namespace Rexo.Cli;`).
   Follow the namespace that already exists in a file when adding new types.
3. **CancellationToken** must be threaded through every async method.
4. **`int.ToString()`** must always pass `CultureInfo.InvariantCulture`.
5. **No inline constant arrays** (`new[] { ... }`) inside methods called in a loop —
   move to `static readonly`.
6. Use `.Count` (not `.Length`) on `IReadOnlyList<T>`.
7. New packages: add version to `Directory.Packages.props`; reference in `.csproj`
   without a version.

---

## What is implemented

`docs/todo.md` is a historical scope checklist; current production-hardening status is in
`docs/ROADMAP.md`. Implemented behavior includes:

- CLI routing (built-in + config commands, multi-word resolution, global flags)
- Config loading with JSON Schema validation
- Template engine with filters (`slug`, `upper`, `lower`, `default(...)`)
- Core built-in step primitives (validate, resolve-version, artifacts lifecycle, config-resolved, config-materialize, etc.)
- Policy-overlay commands for toolchains (`test`, `analyze`, `verify` via embedded overlays)
- Version providers: `fixed`, `env`, `gitversion`, `minver`, `nbgv`, `git`
- Artifact providers: Docker, NuGet, Helm/Helm OCI, npm, PyPI, Maven, Gradle, RubyGems, Terraform, and generic file packaging
- Git + CI environment detection
- Spectre.Console rich output renderer + Blazor/RazorConsole interactive TUI (`rx ui`)
- Dotnet and node policy overlays for test/analyze/verify flows
- `extends` config merge, policy-provided commands, parallel step execution, output capture
- `config resolved` / `config sources` / `config materialize` sub-commands
- Artifact manifest file output, secret masking, structured error taxonomy
- Full-suite counts change as coverage grows; run the Release build and tests before handoff.

## What is not yet implemented

Production-hardening status and remaining acceptance are tracked in [docs/ROADMAP.md](docs/ROADMAP.md);
older scope/checklist entries are historical and must not be treated as proof that the roadmap is
complete. Current limits include local-file-only promotion (no remote deploy or registry-tag
promotion), no generated SBOM or signed attestation, and layer-level rather than exact per-file
configuration provenance. NuGet push masks API keys in Rexo output, but the key is still passed to
`dotnet nuget push` as a process argument.

---

## Key files

| File | Purpose |
| ------ | --------- |
| `AGENTS.md` | Full technical reference for AI agents |
| `docs/scope.md` | Complete product scope (source of truth) |
| `docs/todo.md` | Implementation checklist |
| `src/Cli/Program.cs` | CLI entry point and routing |
| `src/Core/Abstractions/` | All interfaces |
| `src/Core/Models/` | All domain models |
| `src/Execution/ConfigCommandLoader.cs` | Built-in primitive registration |
| `src/Execution/StepExecutor.cs` | Step execution loop |
| `src/Templating/TemplateRenderer.cs` | Template variable/filter engine |
| `src/Configuration/RepoConfigurationLoader.cs` | Config load + schema validation |
| `rexo.schema.json` | JSON Schema for the rexo config (YAML or JSON) |
