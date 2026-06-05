# Commands, Options, and Steps

Comprehensive reference for defining commands, command options, step definitions, and merge behavior.

---

## `commands`

Each key is a command name (spaces allowed for multi-word commands).

```jsonc
"commands": {
  "build": {
    "description": "Build the project",
    "options": {
      "configuration": { "type": "string", "default": "Release" }
    },
    "args": {
      "target": { "required": false, "description": "Build target" }
    },
    "steps": [ ... ]
  },
  "branch feature": {          // invoked as: rx branch feature <name>
    "steps": [ ... ]
  }
}
```

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
4. default behavior (no explicit merge): child replaces base

If both `merge.steps` and legacy `stepOps` are provided, `merge.steps` is used.

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

## `aliases`

Short command names or multi-word command mappings:

```jsonc
"aliases": {
  "r": "release",
  "b": "build",
  "t": "test"
}
```
