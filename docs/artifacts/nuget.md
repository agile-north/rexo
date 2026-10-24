# NuGet Artifact Provider (`type: "nuget"`)

## Command mapping

- Build: `dotnet pack`
- Push: `dotnet nuget push`

## Settings

| Key | Type | Notes |
| --- | --- | --- |
| `project` | `string` | Project path for `dotnet pack`. |
| `output` | `string` | Package output directory. |
| `target.source` | `string` | Feed URL/name for push. |
| `target.sourceEnv` | `string` | Env var name containing feed URL/name (default env key `NUGET_TARGET_SOURCE`). |
| `target.apiKeyEnv` | `string` | Env var name containing API key/token (default env key `NUGET_API_KEY`). |
| `ciInference` | `boolean` | Enable CI token fallback (`GITHUB_TOKEN` / `CI_JOB_TOKEN` / `SYSTEM_ACCESSTOKEN`) (default `true`; alias `target.ciInference`). |
| `symbols.enabled` | `boolean` | When `true`, also pushes matching `.snupkg` symbol packages for this artifact. |
| `symbols.source` | `string` | Symbol feed URL/name. Defaults to `target.source` when omitted. |
| `symbols.sourceEnv` | `string` | Env var containing symbol feed URL/name (default env key `NUGET_SYMBOL_TARGET_SOURCE`). |
| `symbols.apiKeyEnv` | `string` | Env var containing symbol API key/token (default env key `NUGET_SYMBOL_API_KEY`, fallback `NUGET_API_KEY`). |
| `symbols.pattern` | `string` | Glob for symbol packages to push (for example `artifacts/packages/*.symbols.nupkg`). Default is `<output>/<artifact>.<version>.snupkg`. |

## Discover projects with globs

Use top-level `discover` instead of `name` and `settings.project` to create one
artifact per matching packable project:

```yaml
artifacts:
  - type: nuget
    group: contracts
    discover:
      include:
        - "**/*Contracts.csproj"
      exclude:
        - "samples/**"
    settings:
      output: artifacts/packages
      target:
        source: https://api.nuget.org/v3/index.json
```

Include this in a normal Rexo config with `$schema` and `schemaVersion: "1.0"`.
`discover.include` is a nonempty array; `exclude` is optional. Patterns are
relative to the **repository root**, not the config file. Existing literal
`settings.project` behavior is unchanged; it does not interpret globs.
Discovery currently supports only `type: nuget`.

Matching is case-sensitive on every OS and uses `/` separators: `*` matches
within a path segment, `?` matches one character, and a complete `**` segment
matches zero or more directories. For example, `**/*Contracts.csproj` also
matches `Contracts.csproj` in the root. Includes are combined, then excludes
are applied; matching the same file through several includes evaluates it only
once per declaration. Results are sorted by repository-relative path.
Rooted paths, `.`/`..` segments, backslashes, brace/character-class expansion,
and negated patterns are not supported. Add exclusions in `exclude` instead.
Patterns must select `.csproj` files, not arbitrary output files.
Each pattern is limited to 1,024 characters and each declaration to 10,000 matches.

Directories named `.git`, `bin`, or `obj` (case-insensitive) are always pruned.
Directory symlinks/junctions are not followed, and matched file symlinks are
rejected. `.gitignore` is not applied. Use explicit excludes for other generated
trees or vendored projects.

For each selected declaration, Rexo evaluates projects through installed
`dotnet msbuild`, using Release configuration, pack/output properties and the
resolved version properties:

- The evaluated `PackageId` becomes the artifact name, honoring imported and
  conditional MSBuild properties rather than guessing from the filename.
- `IsPackable=false` projects are skipped with a diagnostic. Plan step outputs
  also expose `skippedProjects`.
- Multi-targeted projects must evaluate to the same package identity and
  packability for every target framework.
- Zero packable matches, invalid metadata, unavailable SDKs, and evaluation
  errors fail explicitly. Restore/setup required by a project's imports must
  be completed first; discovery does not intentionally restore or build.
- Duplicate NuGet identities are rejected case-insensitively across the selected
  inventory, including overlaps between discovery declarations and literal entries.
  There is no implicit override or duplicate publication.

**Evaluation is not a sandbox:** MSBuild loads repository imports, so planning
and dry runs should only be performed on trusted repositories. `rx plan` can
therefore require the SDK even though it does not pack or publish. `rx check`
checks matching input paths without evaluating package metadata.

Discovered artifacts inherit the declaration's feed, output settings and group.
Group selection happens before discovery: `rx plan --artifact-group contracts`
evaluates only that lane; `--all-artifact-groups` includes every lane. Concrete
project identities are reused within the invocation, including nested lifecycle
commands, rather than rediscovered after building. Declarative config output
(`config resolved`) and config hashes still describe patterns, not local matches.

Builds require a resolved repository version and pack each project separately,
setting `Version` and `PackageVersion` from the resolved version. Plans, manifests
and publication operate on individual packages, not one aggregate wildcard.
Discovered build entries include `SourceProject`, the exact `.nupkg` location and
its SHA-256. Publication selects the exact package/version; unrelated or stale
packages in the output directory are never included. Symbols are supported,
but `settings.symbols.pattern`, if supplied, must be an exact path, not a glob.
Output files must remain within the repository without traversing symbolic links.

Verified handoff seals each package's project association and exact file identities.
Verification in a later invocation reevaluates discovery and rejects changed
packable inventories, identities, project associations or package bytes before
publication. Transfer source projects and required imports along with the sealed
outputs when verifying in a different checkout.

## Auth resolution

Credential resolution order:

1. `settings.target.apiKeyEnv` (or `NUGET_API_KEY`)
2. `NUGET_AUTH_TOKEN`
3. `GITHUB_TOKEN` for `nuget.pkg.github.com`
4. `CI_JOB_TOKEN` when `target.source` points to an explicit GitLab Package Registry NuGet endpoint (`.../api/v4/projects/<id>/packages/nuget/...`)
5. `SYSTEM_ACCESSTOKEN` for non-GitHub CI fallback (for example Azure Artifacts)

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI token fallback for this artifact

Source resolution order:

1. Env value from `settings.target.sourceEnv` (or `NUGET_TARGET_SOURCE`)
2. `settings.target.source`
3. Default `https://api.nuget.org/v3/index.json`

Symbol source resolution order (when `symbols.enabled = true`):

1. Env value from `settings.symbols.sourceEnv` (or `NUGET_SYMBOL_TARGET_SOURCE`)
2. `settings.symbols.source`
3. `settings.target.source` (or its env override)

Symbol credential resolution order (when `symbols.enabled = true`):

1. `settings.symbols.apiKeyEnv` (or `NUGET_SYMBOL_API_KEY`)
2. `NUGET_API_KEY`
3. `NUGET_AUTH_TOKEN`
4. Same token already resolved for primary package push
5. `GITHUB_TOKEN` for `nuget.pkg.github.com`
6. `CI_JOB_TOKEN` when symbol source points to an explicit GitLab Package Registry NuGet endpoint
7. `SYSTEM_ACCESSTOKEN` for non-GitHub CI fallback

## Example

```json
{
  "type": "nuget",
  "name": "Rexo.Core",
  "settings": {
    "project": "src/Core/Core.csproj",
    "output": "artifacts/packages",
    "target": {
      "source": "https://api.nuget.org/v3/index.json",
      "sourceEnv": "NUGET_TARGET_SOURCE",
      "apiKeyEnv": "NUGET_API_KEY"
    },
    "symbols": {
      "enabled": true,
      "sourceEnv": "NUGET_SYMBOL_TARGET_SOURCE",
      "apiKeyEnv": "NUGET_SYMBOL_API_KEY"
    }
  }
}
```

If your symbol package names are not `.snupkg` (for example `*.symbols.nupkg`), set a custom pattern:

```json
{
  "type": "nuget",
  "name": "Rexo.Core",
  "settings": {
    "output": "artifacts/packages",
    "symbols": {
      "enabled": true,
      "pattern": "artifacts/packages/*.symbols.nupkg"
    }
  }
}
```
