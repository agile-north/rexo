# Commands, Options, and Steps

Comprehensive reference for defining commands, command options, step definitions, and merge behavior.

---

## `commands`

Each key is a command name (spaces allowed for multi-word commands).

```jsonc
"commands": {
  "build": {
    "description": "Build the project",
    "hidden": false,              // optional: omit from default discovery when true
    "before": "gh",                // optional hook: string command alias
    "options": {
      "configuration": { "type": "string", "default": "Release" }
    },
    "args": {
      "target": { "required": false, "description": "Build target" }
    },
    "maxDepth": 5,                  // optional per-command delegation cap
    "steps": [ ... ]
  },
  "branch feature": {          // invoked as: rx branch feature <name>
    "steps": [ ... ]
  }
}
```

### Hidden commands (`hidden`)

Use `hidden: true` for helper commands that should stay callable but not appear in default discovery output.

- Hidden commands are omitted from `rx list`.
- `rx list --include-hidden` shows them on demand.
- Hidden commands are still executable by name.
- Hidden commands can still be called from other commands.
- `rx explain <command>` still works when the hidden command name is explicitly requested.
- Aliases may target hidden commands.

Example:

```jsonc
"commands": {
  "_preflight": {
    "hidden": true,
    "steps": [
      { "uses": "builtin:validate" },
      { "uses": "builtin:resolve-version" }
    ]
  },
  "release": {
    "steps": [
      { "command": "_preflight" },
      { "uses": "builtin:push-artifacts", "with": { "confirm": "true" } }
    ]
  }
}
```

`hidden` is a discovery-only feature, not an access-control boundary.

### Command hooks (`before` / `after`)

Use hooks to wrap a command with reusable pre/post behavior without repeating step blocks.

Each hook supports either:

- a command name string (`"gh"`)
- an explicit step array (`[{ "uses": "builtin:validate" }]`)

Execution order is always:

1. `before`
2. `steps`
3. `after`

Example:

```jsonc
"commands": {
  "release": {
    "before": "gh",
    "steps": [
      { "id": "publish", "uses": "builtin:push-artifacts" }
    ],
    "after": [
      { "id": "announce", "command": "notify-release" }
    ]
  }
}
```

Forwarding remains explicit: use `with` on hook steps/commands when mapping args/options.

### Command merge and step operations

When commands are merged through `extends` (or policy overlays), you can control
how a child command composes with a base command.

Recommended syntax (unified merge envelope):

```jsonc
"commands": {
  "build": {
    "merge": {
      "mode": "append", // layer | replace | append | prepend | wrap
      "steps": {
        "remove": ["test"],
        "replace": [
          { "id": "compile", "step": { "run": "dotnet build --no-restore" } }
        ],
        "prepend": [
          { "id": "setup", "run": "echo setup" }
        ],
        "append": [
          { "id": "notify", "run": "echo notify" }
        ]
      }
    },
    "steps": []
  }
}
```

`merge.mode` values:

- `layer`: base command wins, child layer does not auto-continue.
- `replace`: child command replaces base command.
- `append`: child steps are appended after base steps.
- `prepend`: child steps are placed before base steps.
- `wrap`: child steps wrap base steps at continuation marker (self-reference step).

`merge.steps` operation order is deterministic:

1. `remove`
2. `replace`
3. `prepend`
4. `append`

Legacy compatibility:

- Legacy scalar `merge` remains supported:

```json
{ "merge": "append" }
```

- Legacy `stepOps` remains supported:

```json
{ "stepOps": { "remove": ["old-step"] } }
```

Precedence rules (highest to lowest):

1. `merge.steps`
2. `stepOps` (legacy)
3. `merge.mode`
4. `runtime.commands.defaultMergeMode` when configured on the merged repo config
5. default behavior (no explicit merge): child replaces base

If both `merge.steps` and legacy `stepOps` are provided, `merge.steps` is used.

Notes:

- `runtime.commands.defaultMergeMode` gives you a repo-level fallback for same-name command collisions.
- Use it when you want additive stacks like `embedded:dotnet` + `embedded:node` to fan out by default.
- Same-name embedded policy continuation steps still work when present; they remain the most specific composition signal.
- The runtime default is inherited across `extends`; a child can override the base default without repeating merge metadata on every command.

---

## Command Option Typing

For `commands.<name>.options.<option>.type`, allowed values are:

- `string`
- `bool`
- `boolean`
- `int`
- `integer`
- `number`

Schema default:

- `type` defaults to `string` when omitted

`default` may be a string, boolean, integer, or number value.

---

## Steps

Each step has one of `run`, `uses`, or `command`:

```jsonc
{
  "id": "my-step",             // optional; enables output referencing
  "run": "echo {{args.name}}", // shell command (template-expanded)
  "when": "{{options.flag}}",  // skip step if value is falsey after rendering
  "with": {                     // optional; per-step option overrides
    "push": "{{options.push}}"
  },
  "continueOnError": true,     // don't fail the command if this step fails
  "parallel": true,            // run concurrently with adjacent parallel steps
  "outputPattern": "v(?P<version>[\\d.]+)", // regex: named groups → step outputs
  "outputFile": "build/version.txt"         // write stdout to this file path
}
```

### Shell Command Steps

```jsonc
{
  "id": "compile",
  "run": "dotnet build"
}
```

Variables in `run` strings are template-expanded. See [Template Variables](templates.md).

Container-wrapped run steps:

```jsonc
{
  "id": "lint-in-container",
  "run": "dotnet --info",
  "container": {
    "image": "mcr.microsoft.com/dotnet/sdk:10.0",
    "workingDirectory": "/work",
    "entrypoint": "dotnet",
    "dockerfile": "Dockerfile",
    "context": ".",
    "build": {
      "target": "publish",
      "args": {
        "APP_VERSION": "1.2.3"
      }
    },
    "env": {
      "DOTNET_CLI_TELEMETRY_OPTOUT": "1"
    }
  }
}
```

Container defaults and behavior:

- Repository root is mounted to `/work`.
- The command runs in `/work` unless `container.workingDirectory` is provided.
- `container.entrypoint` overrides image entrypoint via `docker run --entrypoint`.
- If `container.dockerfile` is provided, Rexo builds `container.image` when missing or stale (hash label drift).
- `container.context` controls docker build context (defaults to repository root).
- `container.build.target` sets docker build stage (`--target`).
- `container.build.args` provides docker build args (`--build-arg key=value`).
- Environment inside the container includes host process environment plus `.env`/`.rexo/.env` overlays, then `container.env` overrides.
- If Docker is unavailable, Rexo logs a warning and falls back to native execution for that step.
- `container` is only supported for `run` steps in v1; `uses` and `command` steps keep their own execution model.

See [Containerized Run Steps](containerized-run.md) for detailed semantics,
validation rules, and extended examples.

### Builtin Steps

```jsonc
{
  "uses": "builtin:resolve-version"  // built-in primitive
}
```

`with` is most useful when invoking reusable built-ins. It lets a command map its
own option names into step-local option names consumed by that builtin.

Resolution precedence for values consumed by built-ins is:

1. `step.with`
2. command options/args
3. execution context defaults
4. provider-specific defaults

Example:

```json
{
  "id": "push",
  "uses": "builtin:push-artifacts",
  "with": {
    "confirm": "{{options.push}}"
  }
}
```

This makes intent explicit without forcing the builtin to understand every command-specific
option name.

### Command Delegation Steps

```jsonc
{
  "command": "build"                 // delegate to another configured command
}
```

---

## Command-level Concurrency

```jsonc
"commands": {
  "build": {
    "maxParallel": 4,
    "steps": [ ... ]
  }
}
```

### Parallel Execution

Consecutive steps marked `parallel: true` are batched and run concurrently via
`Task.WhenAll`. Each parallel step receives a snapshot of the context at the start of
the group (they cannot see each other's outputs within the same group).

### Output Capture

- **`outputPattern`**: a .NET regex with named groups. Matched groups are stored in
  `steps.<id>.outputs.<groupName>` and available to subsequent template steps.
- **`outputFile`**: stdout is written to this path (relative to the repo root).

---

## Command Delegation Depth

Rexo enforces a maximum depth for delegated command chains to prevent runaway recursion.

- Global default: `runtime.commands.maxDepth` (defaults to `5` when omitted)
- Per-command override: `commands.<name>.maxDepth`
- Effective limit in nested calls uses the stricter active limit in the invocation chain

When exceeded, execution fails hard with the existing cycle error code (`CMD-004`).

```jsonc
"runtime": {
  "commands": {
    "maxDepth": 5
  }
},
"commands": {
  "release": {
    "maxDepth": 3,
    "steps": [
      { "command": "publish" }
    ]
  }
}
```

---

## `aliases`

Short command names or multi-word command mappings:

```jsonc
"aliases": {
  "r": "release",
  "b": "build",
  "t": "test"
}
```
