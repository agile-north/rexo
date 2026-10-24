# Development Guide

## Prerequisites

- .NET SDK 10.0.203+ (see `global.json` for pinned version)
- Git

## Setup

```bash
git clone <repo-url>
cd rexo
dotnet restore
dotnet build solution.slnx -c Release
dotnet test solution.slnx -c Release
```

## Repository release workflow

The single `release` workflow (`release.yml`) has one job (`release`).
It bootstraps the current source and calls the normal
`rx release` lifecycle once. Actions passes `--push=true` only for push events or
explicitly opted-in manual runs with a configured publisher; otherwise it passes
`--push=false`. Rexo's `runtime.push` owns branch and PR restrictions.
Actions selects/authenticates the publisher.
Rexo owns verification, building, pushing, GitHub reports, release tags and releases;
there is no second build, separate publishing job, package transfer between jobs, or
lifecycle wrapper. The workflow uploads a single evidence artifact from this job.
The job has release permissions, including on same-repository PRs; it no longer provides
the previous read-only job boundary. Publisher login and release mutations remain
event-gated; release mutations additionally require Rexo's branch and publication gates.
An opted-in manual run on a blocked branch can authenticate but cannot publish.
Non-publishing runs do not verify real publisher authentication.

`workflow-validation` runs pinned actionlint over all workflows, including shell checks.
Require `workflow-validation` and `release` in the `main` branch rules,
with branches up to date, before merging workflow changes. Repository rules are maintained
in GitHub settings, not by the workflow itself.

There is no separate build workflow: PRs, branch pushes and manual rehearsals share
the same job, with one isolated source bootstrap.
The config imports `standard`, `dotnet`, [github-tests](embedded/github-tests.md),
[github-coverage](embedded/github-coverage.md) and [github-release](embedded/github-release.md).
Test and coverage reporters run even after test failure, create GitHub Checks and append
Actions summaries. Coverage conversion uses the policy's pinned container tooling; there
is no local ReportGenerator tool manifest or separate coverage command.
The reporters use explicit TRX/Cobertura inputs and separate output directories because
this repository intentionally puts raw tests and coverage in the same directory.
Internal report uploads are disabled: the single always-run `actions/upload-artifact`
step uploads their local outputs together with the other lifecycle evidence.
Actions retains checkout, SDK/GitVersion setup, NuGet.org OIDC login when selected,
source bootstrap, lifecycle invocation and evidence upload. It grants `checks: write`
for reporting; fork/read-only contexts use the policies' documented publishing skips.

`github-release` replaces `git-tag` and creates the exact-commit tag and GitHub release
only after successful package publication. The only repository-specific release contribution
is one appended `publish-schema` step: after the GitHub release succeeds, it force-updates
`vars.schemaBranch` to the exact checked-out commit, only when all selected artifacts were
pushed, `--push` is enabled, and the run is neither a PR nor a dry-run.
This preserves the existing mutable schema-branch behavior, not a merge or deployment.
The destination is explicit (`schema/v1.0`); update it when the schema version changes.
An integration assertion keeps it aligned with `rexo.schema.json`, and a local bare-Git test
checks the real branch update without touching GitHub.
Prepared-artifact and evidence-validation primitives remain available for consumers that
need a multi-stage pipeline, but this repository does not use them in its normal lifecycle.

To reproduce PR verification locally from the repository root:

```powershell
dotnet publish src\Cli\Cli.csproj -c Release --output artifacts\rx-bootstrap
dotnet artifacts\rx-bootstrap\Rexo.Cli.dll --non-interactive --json-file artifacts/selfhost/release.json release
```

Set `GITVERSION_SEMVER` to select a version; local execution otherwise uses the configured
fallback. No publishing credentials are needed. This does not certify the full
readiness or deployment roadmap; see [ROADMAP.md](ROADMAP.md).
The repository config uses `~/` output paths relative to `outputs.root` (`artifacts`).
TRX and XPlat coverage attachments share `artifacts/test-results`; merged analyzer output
lives in `artifacts/sarif`. CI uploads the lifecycle output in one evidence artifact.
Local reruns discover existing raw reports too: remove only `artifacts/test-results`
before a fresh rehearsal to avoid including older failures or coverage. Do not clean the
entire `artifacts` directory while running its bootstrap CLI.

The repository release workflow publishes a temporary bootstrap CLI from the checked-out source tree
to an isolated output directory, then runs the repository's `.rexo/rexo.yaml` release command. The
separate bootstrap path lets that command rebuild the CLI on Windows without locking its build
outputs. It does not install an older published Rexo tool to build the current source.

Configure at most one publishing mode:

- Set the `NUGET_ORG_USER` Actions secret to use NuGet.org trusted publishing through OIDC.
- Otherwise, set the `PUBLISH_TO_GITHUB_PACKAGES` Actions variable to `true` to publish through
  GitHub Packages using `GITHUB_TOKEN`.
- If neither is configured, Rexo still builds, tests, and packs the CLI, but the workflow skips
  package publishing, Git version-tag creation, schema-branch publication, and
  GitHub Release creation.
  If both modes are configured, NuGet.org takes precedence.

The self-hosted version comes from GitVersion and is passed to the local CLI as
`GITVERSION_SEMVER`. Keep its fail-fast Actions computation for now: Rexo's native
GitVersion provider can fall back on tool failure, which is unsuitable for selecting a
published version. Do not replace it until native resolution can fail closed.
Local runs can use the configured fallback without publishing.
Never add publishing credentials to repository configuration. NuGet push
output masks API keys, but `dotnet nuget push` receives them as process arguments; use a dedicated
CI identity and avoid sharing the runner with untrusted processes.

## Build rules

The build is strict:

- `TreatWarningsAsErrors=true` — any analyzer warning is a build error
- `AnalysisLevel=latest-recommended` — Roslyn CA rules enforced
- `GenerateDocumentationFile=true` for all `src/` projects (CS1591 suppressed)

## NuGet package README

Edit `src/Cli/PACKAGE_README.md`, the source template for the CLI package README.
During packing, `src/Cli/Cli.csproj` generates `src/Cli/obj/PACKAGE_README.generated.md`,
substituting the versioned documentation and schema link tokens, and includes that generated
file at the package root. The generated file is ignored and recreated before each pack; there
is no tracked root-level generated README to update. Verify package README changes by inspecting
`PACKAGE_README.generated.md` in the resulting `.nupkg`.

The integration suite also checks that repository-local Markdown links resolve. Run this before
every commit:

```bash
dotnet build solution.slnx -c Release && dotnet test solution.slnx -c Release --no-build
```

---

## Coding Conventions

See [CODE_STYLE.md](CODE_STYLE.md) for examples and guidance on platform-neutral paths,
rooted-path handling, cleanup exceptions, LINQ transforms, and condition flow.

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
   `docs/configuration/README.md` when needed).

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
| `Execution.Tests` | Command execution/registration, templates, config commands, artifact and policy workflows, outputs, secrets, and versioning |
| `Integration.Tests` | CLI smoke/output/override scenarios and repository Markdown-link checks |

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
| `release.yml` | PRs and pushes to `main`/`alpha`/`beta`/`edge` branches (including `*-*` variants), plus manual dispatch — one Rexo lifecycle with gated publication |
| `workflow-validation.yml` | PRs to main and manual dispatch — workflow and shell lint |
| `dependabot-auto-merge.yml` | Dependabot pull requests — automated dependency-update handling |

---

## Where to look

| Question | File |
| --- | --- |
| Full product design | `docs/scope.md` |
| What's done vs pending | `docs/todo.md` |
| Architecture diagram | `docs/ARCHITECTURE.md` |
| Config system | `docs/configuration/README.md` |
| AI agent context | `AGENTS.md` (root) |
