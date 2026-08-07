# Builtin Patterns & Examples

Common usage patterns, shell equivalents, and concrete examples.

## Common Input Patterns

### Passing push intent from command option to builtin

```json
{
  "id": "push",
  "uses": "builtin:push-artifacts",
  "with": {
    "confirm": "{{options.push}}"
  }
}
```

### Planning with push semantics

```json
{
  "id": "plan",
  "uses": "builtin:plan-artifacts",
  "with": {
    "push": "{{options.push}}"
  }
}
```

### Stage-targeted docker build

```json
{
  "id": "docker-stage",
  "uses": "builtin:docker-stage",
  "with": {
    "stage": "publish"
  }
}
```

## Approximate Shell Equivalents

These mappings are intentionally approximate.

**Important notes:**

- Builtins may apply policy gates, config defaults, provider-specific behavior, and
  richer output handling that plain shell commands do not.
- Use this section as a mental model, not as an exact 1:1 implementation contract.

### Core lifecycle builtins

| Builtin | Approximate shell behavior |
| --- | --- |
| `builtin:resolve-version` | `gitversion /output json` or equivalent provider command, then map fields into execution context |
| `builtin:validate` | Logical validation gate; no direct external command in current implementation |
| `builtin:clean` | Remove `<runtime.output.root>/` recursively (default `artifacts/`) |

### Artifact lifecycle builtins

| Builtin | Approximate shell behavior |
| --- | --- |
| `builtin:plan-artifacts` | Read artifact config + context and print a computed plan (no build/tag/push mutation) |
| `builtin:build-artifacts` | For each artifact provider, run equivalent build (`docker build`, `dotnet pack`, etc.) |
| `builtin:tag-artifacts` | For each artifact provider, apply version tags (`docker tag`, package version tagging flows) |
| `builtin:push-artifacts` | Enforce push gates then run provider push (`docker push`, `dotnet nuget push`, etc.), then write `<runtime.output.root>/manifest.json` when enabled |

### Composite convenience builtins

| Builtin | Approximate shell behavior |
| --- | --- |
| `builtin:ship-artifacts` | `tag-artifacts` then `push-artifacts` |
| `builtin:all-artifacts` | `build-artifacts` then `tag-artifacts` then `push-artifacts` |
| `builtin:plan` | Alias-style wrapper around `plan-artifacts` |
| `builtin:ship` | Alias-style wrapper around `ship-artifacts` |
| `builtin:all` | Alias-style wrapper around `all-artifacts` |

### Docker-scoped builtins

| Builtin | Approximate shell behavior |
| --- | --- |
| `builtin:docker-plan` | `plan-artifacts` filtered to docker artifact type |
| `builtin:docker-ship` | docker-only `tag-artifacts` then docker-only `push-artifacts` |
| `builtin:docker-all` | docker-only `build-artifacts` then `tag-artifacts` then `push-artifacts` |
| `builtin:docker-stage` | Build one named stage from `settings.stages.<name>` for each docker artifact |

### Configuration utility builtins

| Builtin | Approximate shell behavior |
| --- | --- |
| `builtin:config-resolved` | Serialize effective config to JSON and print |
| `builtin:config-materialize` | Generate provider-side helper files (currently `GitVersion.yml` when needed) |

## Concrete Examples

### policy command:test

Approximate (dotnet overlay):

```bash
dotnet test -c Release --logger trx
```

Approximate (node overlay):

```bash
npm test --if-present
```

### builtin:build-artifacts for docker artifact

Approximate, after version resolution:

```bash
docker build -f Dockerfile -t ghcr.io/org/app:1.2.3 .
```

### builtin:push-artifacts for docker artifact

Approximate, after gates pass:

```bash
docker push ghcr.io/org/app:1.2.3
```

### builtin:push-artifacts for nuget artifact

Approximate, after gates pass:

```bash
dotnet nuget push artifacts/packages/*.nupkg --source <source>
```
