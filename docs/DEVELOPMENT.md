# Development Guide

## Prerequisites

- .NET SDK 10.0.203+ (see `global.json` for pinned version)
- Git

## Setup

```bash
git clone <repo-url>
cd repoOS
dotnet restore
dotnet build solution.slnx -c Release
dotnet test solution.slnx -c Release
```

## Build rules

The build is strict:

- `TreatWarningsAsErrors=true` — any analyzer warning is a build error
- `AnalysisLevel=latest-recommended` — Roslyn CA rules enforced
- `GenerateDocumentationFile=true` for all `src/` projects (CS1591 suppressed)

Run this before every commit:

```bash
dotnet build solution.slnx -c Release && dotnet test solution.slnx -c Release --no-build
```

---

## Coding Conventions

| Rule | Detail |
| --- | --- |
| Namespaces | Source files use `Rexo.*` prefix (e.g. `namespace Rexo.Cli;`). Match the namespace already used in the file. |
| CancellationToken | Thread through every async method — never pass `CancellationToken.None` except at the outermost call site |
| `int.ToString()` | Always pass `CultureInfo.InvariantCulture` |
| Constant arrays | Never `new[] { ... }` inside a method called in a loop — use `static readonly` |
| `JsonSerializerOptions` | Cache as `static readonly` fields — never instantiate inline in hot paths (CA1869) |
| `IReadOnlyList<T>` | Use `.Count`, not `.Length` |
| Path composition | Use `Path.Join` when every component must be appended. If absolute paths are allowed, handle rooted paths explicitly instead of relying on `Path.Combine` to discard earlier components. |
| LINQ | Use `Where` and `Select` when a loop only filters or maps a sequence; keep imperative loops when they make control flow or side effects clearer. |
| NuGet packages | Add version to `Directory.Packages.props`; reference in `.csproj` without a version |
| `Core` project | Never add a `<ProjectReference>` to `src/Core/Core.csproj` — it must have zero project dependencies |

---

## Project Structure

Source projects are in `src/`, test projects in `tests/`. Folder names are plain
(`Cli/`, `Core/`) and `Directory.Build.props` derives assembly names automatically as
`Rexo.<FolderName>`.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full layer diagram and dependency graph.

---

## Adding a New Version Provider

1. Create a class in `src/Versioning/` implementing `IVersionProvider` (from `Core`):

   ```csharp
   namespace Rexo.Versioning;
   using Rexo.Core.Abstractions;
   using Rexo.Core.Models;

   public sealed class MyVersionProvider : IVersionProvider
   {
       public Task<VersionResult> ResolveAsync(VersioningConfig config, CancellationToken ct)
       {
           // ...
           return Task.FromResult(new VersionResult(...));
       }
   }
   ```

2. Register it in `VersionProviderRegistry.CreateDefault()`:

   ```csharp
   registry.Register("mykey", new MyVersionProvider());
   ```

3. Users set `"provider": "mykey"` in their `rexo.json` `versioning` section.

---

## Adding a New Artifact Provider

1. Create a class in a new `src/Artifacts.MyType/` project implementing `IArtifactProvider`.
2. Add a project reference to `src/Cli/Cli.csproj`.
3. Follow the existing self-registration pattern and add `public static void Register(ArtifactProviderRegistry registry)` to the provider.
4. Register it from `src/Cli/CliBootstrapper.cs`:

   ```csharp
   MyTypeArtifactProvider.Register(artifactProviders);
   ```

---

## Adding a New Built-in Primitive

Built-in primitives are step types used as `uses: builtin:my-primitive`.

1. In `ConfigCommandLoader.RegisterBuiltins` (or the `LoadInto` method body), call:

   ```csharp
   _builtinRegistry.Register("builtin:my-primitive", async (step, ctx, ct) =>
   {
       // implementation
       return new StepResult(step.Id ?? "my-primitive", true, 0, TimeSpan.Zero,
           new Dictionary<string, object?> { ["message"] = "Done." });
   });
   ```

2. Document the new primitive contract in `docs/BUILTINS.md` (and reference it from
   `docs/CONFIGURATION.md` when needed).

---

## Adding a New CLI Sub-command

1. Add a handler registration in `BuiltinCommandRegistration.CreateDefault`.
2. Wire the routing in `Program.ExecuteAsync` switch expression (for top-level commands)
   or in the multi-word resolver.

---

## Schema Versioning

When breaking changes to `rexo.json` are needed:

1. Create the next versioned schema files (for example `rexo.schema.v2.json` and `policy.schema.v2.json`).
2. Add `"2.0"` to the supported schema versions in `RepoConfigurationLoader`.
3. Update `SupportedSchemaUri` / `SupportedSchemaPath` constants or add overloads.
4. Bump the `$schema` URL in documentation and examples.

Current version: **1.0** — schemas at `rexo.schema.json` and `policy.schema.json` (repo root).

---

## Testing

Test projects live in `tests/`:

| Project | What it covers |
| --- | --- |
| `Core.Tests` | Domain model unit tests |
| `Configuration.Tests` | `RepoConfigurationLoader` — happy path, missing schema, bad version, NJsonSchema failures, `extends` merge, circular detection |
| `Execution.Tests` | `DefaultCommandExecutor`, `TemplateRenderer` (10 cases), `BuiltinCommandRegistration` (5 cases), config commands, step model |
| `Integration.Tests` | Smoke: `rx version` exits 0 |

Run a specific test project:

```bash
dotnet test tests/Execution.Tests/Execution.Tests.csproj -c Release
```

---

## Versioning

Versioning uses GitVersion in mainline mode. Config: `GitVersion.yml`.
The version flows through CI via the `GITVERSION_*` environment variables and
`rx` resolves it via the `env` or `gitversion` provider at runtime.

---

## CI

Workflows in `.github/workflows/`:

| File | Triggers |
| --- | --- |
| `build.yml` | All pushes and PRs — build + test |
| `release.yml` | Tags — publish dotnet tool to NuGet |
| `codeql.yml` | Scheduled — security scanning |

---

## Where to look

| Question | File |
| --- | --- |
| Full product design | `docs/scope.md` |
| What's done vs pending | `docs/todo.md` |
| Architecture diagram | `docs/ARCHITECTURE.md` |
| Config system | `docs/CONFIGURATION.md` |
| AI agent context | `AGENTS.md` (root) |
