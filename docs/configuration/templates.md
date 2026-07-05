# Template Variables and Filters

Use dynamic variables and filters in `run` step commands.

---

## Template Variable Source

Environment variable source behavior:

- Rexo resolves environment values from process environment first.
- If a value is not present in process env, Rexo falls back to repository env files:
  - `.rexo/.env` (higher file precedence)
  - `.env` (root)
- This applies to template `{{env.<VAR>}}` lookups and provider environment-driven behavior.

---

## Available Variables

Available in any `run` step string:

| Path | Source |
| --- | --- |
| `{{args.<name>}}` | Positional/named args from CLI |
| `{{options.<name>}}` | Option flags from CLI |
| `{{env.<VAR>}}` | Environment variables |
| `{{repo.<field>}}` | Repository/runtime metadata (root, branch, commitSha, shortSha, remoteUrl) |
| `{{git.<field>}}` | Git metadata (branch, commitSha, shortSha, remoteUrl, isCleanWorkingTree) |
| `{{ci.<field>}}` | CI metadata (isCi, provider, buildId, runNumber, workflowName, actor, tag, buildUrl, isPullRequest) |
| `{{outputs.<path>}}` | Resolved output paths from `outputs` config |
| `{{settings.<path>}}` | Resolved `settings` values |
| `{{vars.<path>}}` | Resolved `vars` values |
| `{{version.<field>}}` | Resolved version after `builtin:resolve-version` |
| `{{steps.<id>.outputs.<key>}}` | Raw output from a completed step |
| `{{steps.<id>.success}}` | Step success (`true`/`false`) |
| `{{steps.<id>.exitCode}}` | Step exit code |
| `{{push.<field>}}` | Aggregated push summary derived from step outputs |

`repo.branch`, `repo.commitSha`, `repo.shortSha`, and `repo.remoteUrl` remain available for compatibility. Prefer `git.*` for new templates.

Dry-run is exposed through the options bag as `{{options.dry-run}}`, so commands and
steps can branch on it directly in `run` strings and `when` expressions.

### `version.*` fields

When version is resolved, these fields are available:

- `semver`
- `major`, `minor`, `patch`
- `prerelease`
- `preReleaseTag`
- `preReleaseLabel`
- `preReleaseNumber`
- `preReleaseLabelWithDash`
- `preReleaseTagWithDash`
- `commitSha`, `shortSha`
- `isPrerelease`, `isStable`

### `push.*` fields

The push summary is derived from `__artifacts` and `__pushDecisions` emitted by push-related builtins/commands.

- `hasData` (`true`/`false`)
- `anyPushed` (`true`/`false`)
- `pushedCount`
- `artifactCount`
- `decisionCount`
- `allowedCount`
- `deniedCount`
- `anyBlocked` (`true`/`false`)
- `blockReasons` (distinct denied reasons joined with ` | `)

### `git.*` fields

- `branch`
- `commitSha`
- `shortSha`
- `remoteUrl`
- `isCleanWorkingTree` (`true`/`false`)

---

## Filters

Pipe syntax: `{{value | slug}}`, `{{value | upper}}`, `{{value | lower}}`,
`{{value | default(fallback)}}`, `{{value | coalesce(a, b, 'fallback')}}`

Coalescing operator syntax: `{{args.tag ?? vars.release.tag ?? 'dev'}}`

Supported filters:

- `slug` — Convert to slug format (lowercase, hyphens)
- `upper` — Convert to uppercase
- `lower` — Convert to lowercase
- `default(fallback)` — Use fallback value if variable is empty, whitespace-only, or missing
- `coalesce(a, b, c)` — Return the first non-empty value from the current value, then each fallback candidate in order. Whitespace-only values count as empty.
- `prefix(text)` — Prepend text if value is non-empty
- `suffix(text)` — Append text if value is non-empty
- `trim` — Remove leading/trailing whitespace
- `replace(pattern, replacement)` — Literal string replace.
- `replacePattern(regex, replacement)` — Regex replace. The regex must be a regex literal like `/foo(\d+)/`, and the replacement may use capture groups such as `$1`.

Chain filters with pipes:

```text
{{args.dir | suffix('/dotnet-build.sarif') | prefix('/p:ErrorLog=')}}
```

Coalescing examples:

```text
{{args.version | coalesce(vars.release.version, env.RELEASE_VERSION, '0.1.0-dev')}}
{{args.dir | coalesce(outputs.analysis.reports, 'artifacts/analysis') | suffix('/report.sarif')}}
{{args.tag ?? vars.release.tag ?? env.RELEASE_TAG ?? 'dev'}}
```

Regex replace example:

```text
{{args.branch | replacePattern(/feature\/(.*)/, '$1')}}
```

---

## Equality Expressions

Supported whole-expression comparisons:

- `==` (equality)
- `!=` (inequality)

Examples:

```text
{{version.major == '1'}}        // true if major version is 1
{{options.ci != ''}}             // true if ci option is set
{{vars.dotnet.test.coverage.mode == 'none'}}  // true if coverage disabled
```

Boolean literal support:

```text
{{options.confirm == true}}      // true if confirm option is true
{{options.dry-run == true}}      // true if dry-run is enabled for the invocation
```

When a variable is missing/undefined:

- `{{missing.var == 'value'}}` → `false`
- `{{missing.var != 'value'}}` → `true`

This design enables policy-layer branching with missing vars defaulting gracefully.

---

## Common Patterns

### Conditional step execution

```json
{
  "id": "push",
  "uses": "builtin:push-artifacts",
  "when": "{{options.push}}",
  "with": {
    "confirm": "{{options.push}}"
  }
}
```

### Push-aware branching

```json
{
  "id": "notify-push-blocks",
  "run": "echo Push blocked: {{push.blockReasons}}",
  "when": "{{push.anyBlocked}}"
}
```

### Branch selection based on missing var

```json
{
  "id": "dotnet-test-no-coverage",
  "when": "{{vars.dotnet.test.coverage.mode == 'none'}}"
}
```

When `vars.dotnet.test.coverage.mode` is not set, this expression evaluates to `false` (coverage enabled by default).

### Build arg composition

```json
{
  "run": "dotnet build {{args.target | prefix('--target ')}} {{options.extraArgs}}"
}
```

Both args map gracefully when missing (returns empty string).
