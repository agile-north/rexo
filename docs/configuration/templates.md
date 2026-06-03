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
| `{{repo.<field>}}` | Top-level config fields (name, version, …) |
| `{{version.<field>}}` | Resolved version after `builtin:resolve-version` |
| `{{steps.<id>.output.<key>}}` | Output from a completed step |

---

## Filters

Pipe syntax: `{{value | slug}}`, `{{value | upper}}`, `{{value | lower}}`,
`{{value | default(fallback)}}`

Supported filters:

- `slug` — Convert to slug format (lowercase, hyphens)
- `upper` — Convert to uppercase
- `lower` — Convert to lowercase
- `default(fallback)` — Use fallback value if variable is empty/missing
- `prefix(text)` — Prepend text if value is non-empty
- `suffix(text)` — Append text if value is non-empty
- `trim` — Remove leading/trailing whitespace
- `replace(pattern, replacement)` — Literal string replace.
- `replacePattern(regex, replacement)` — Regex replace. The regex must be a regex literal like `/foo(\d+)/`, and the replacement may use capture groups such as `$1`.

Chain filters with pipes:

```
{{args.dir | suffix('/dotnet-build.sarif') | prefix('/p:ErrorLog=')}}
```

Regex replace example:

```
{{args.branch | replacePattern(/feature\/(.*)/, '$1')}}
```

---

## Equality Expressions

Supported whole-expression comparisons:

- `==` (equality)
- `!=` (inequality)

Examples:

```
{{version.major == '1'}}        // true if major version is 1
{{options.ci != ''}}             // true if ci option is set
{{vars.dotnet.test.coverage.mode == 'none'}}  // true if coverage disabled
```

Boolean literal support:

```
{{options.confirm == true}}      // true if confirm option is true
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
