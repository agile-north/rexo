---
applyTo: "rexo.json,rexo.yaml,rexo.yml,.rexo/**,policy.json"
---

# Rexo configuration context

This repository uses [Rexo](https://github.com/agile-north/rexo) (`rx`) — a config-driven
repository automation CLI. Build, versioning, artifact, and release workflows are defined in a
single config file and run identically locally and in CI.

## Key files

| File | Purpose |
| ---- | ------- |
| `.rexo/rexo.json` (or `rexo.json`) | Main repo config: commands, versioning, artifacts, tests |
| `.rexo/policy.json` (or `policy.json`) | Policy overlay: org-level commands and defaults |

## Documentation

- **Full configuration reference**: https://github.com/agile-north/rexo/blob/release/next/docs/CONFIGURATION.md
- **Rexo schema** (all valid config fields with types): https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json
- **Policy schema** (all valid policy fields with types): https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/policy.schema.json
- **Architecture overview**: https://github.com/agile-north/rexo/blob/release/next/docs/ARCHITECTURE.md

## Quick reference

### Config file structure

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/agile-north/rexo/schema/v1.0/rexo.schema.json",
  "schemaVersion": "1.0",
  "name": "my-repo",
  "extends": ["./base/rexo.json"],       // optional: inherit from other configs
  "policySources": ["nuget:MyOrg.Policies@1.0.0#policies/base.json"], // optional: remote policy sources (opt-in)
  "commands": { ... },
  "aliases": { ... },
  "versioning": { ... },
  "artifacts": [ ... ],
  "tests": { ... },
  "analysis": { ... }
}
```

### Commands and steps

```jsonc
"commands": {
  "build": {
    "description": "Build the project",
    "options": { "configuration": { "type": "string", "default": "Release" } },
    "args":    { "target":        { "required": false } },
    "steps": [
      { "run": "dotnet build -c {{options.configuration}}" },
      { "uses": "builtin:resolve-version" },
      { "command": "test" }
    ]
  }
}
```

Each step uses exactly one of:
- `"run"` — shell command (supports `{{variable}}` templates)
- `"uses"` — built-in primitive (see below)
- `"command"` — delegate to another configured command

### Built-in primitives (`"uses": "builtin:<name>"`)

| Primitive | Purpose |
| --- | --- |
| `builtin:validate` | Validate the loaded config |
| `builtin:resolve-version` | Run the version provider; populate `context.Version` |
| `command:test` | Run overlay-provided test workflow |
| `command:analyze` | Run overlay-provided analysis workflow |
| `command:verify` | Run overlay-composed quality gate workflow |
| `builtin:build-artifacts` | Build all configured artifacts |
| `builtin:tag-artifacts` | Tag artifacts with version tags |
| `builtin:push-artifacts` | Push artifacts; write `artifacts/manifest.json` |
| `builtin:config-resolved` | Print the merged config as JSON |
| `builtin:config-materialize` | Write provider config files (e.g. `GitVersion.yml`) |

### Template variables in `run` steps

| Variable | Value |
| --- | --- |
| `{{args.<name>}}` | CLI positional/named argument |
| `{{options.<name>}}` | CLI option flag |
| `{{env.<VAR>}}` | Environment variable |
| `{{version.semVer}}` | Resolved semantic version |
| `{{steps.<id>.output.<key>}}` | Output from a completed step |

Filters: `| slug`, `| upper`, `| lower`, `| default(fallback)`, `| coalesce(a, b, 'fallback')`; coalescing operator: `??`

### Versioning

```jsonc
"versioning": {
  "provider": "gitversion",   // fixed | env | gitversion | minver | nbgv
  "settings": { "fallback": "0.1.0" }
}
```

### Artifacts

```jsonc
"artifacts": [
  { "type": "docker", "name": "my-image",   "settings": { "dockerfile": "Dockerfile", "registry": "ghcr.io/org" } },
  { "type": "nuget",  "name": "MyPackage",  "settings": { "project": "src/MyLib/MyLib.csproj" } }
]
```

### Policy files

Policy files have the same schema as `rexo.json`. They provide org-level default commands
(e.g. `ci`, `release`) that are merged with the repository config. Create one with:

```bash
rx init --with-policy --policy dotnet
```

## Useful CLI commands

```bash
rx list                      # list all available commands (config + policy + built-ins)
rx explain <command>         # show description, args, options, and steps
rx config sources            # show which config files were loaded
rx config resolved           # show the final merged config as JSON
rx doctor                    # check tool and provider availability
rx secrets doctor            # validate configured secret resolution safely
```





