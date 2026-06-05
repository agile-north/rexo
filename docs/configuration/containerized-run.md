# Containerized Run Steps

Detailed reference for running command run steps inside a container image.

---

## Scope

Container wrapping is supported for run steps only in v1.

- Supported: step.run + step.container
- Not supported: step.uses + step.container
- Not supported: step.command + step.container

This keeps builtins and delegated commands in their native execution model.

---

## Minimal Syntax

```jsonc
{
  "id": "lint",
  "run": "dotnet --info",
  "container": {
    "image": "mcr.microsoft.com/dotnet/sdk:10.0"
  }
}
```

## Extended Syntax

```jsonc
{
  "id": "build-in-container",
  "run": "dotnet build solution.slnx -c Release --no-restore",
  "container": {
    "image": "mcr.microsoft.com/dotnet/sdk:10.0",
    "workingDirectory": "/work",
    "env": {
      "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
      "NUGET_PACKAGES": "/work/.nuget/packages"
    }
  }
}
```

---

## Default Behavior

When step.container is set, Rexo executes the rendered run command through Docker with these defaults:

- Repo mount: repository root mounted at /work
- Container working directory: /work unless container.workingDirectory is provided
- Shell inside container: /bin/sh -c
- Container lifecycle: docker run --rm

---

## Environment Inheritance

Environment passed into the container is merged in this order:

1. Host process environment
2. Repo env overlays from .env and .rexo/.env
3. step.container.env overrides

Later values win when keys collide.

This gives native-feeling behavior while still allowing explicit overrides per step.

---

## Fallback Semantics

If Docker is unavailable on the host:

- Rexo logs a warning
- Rexo executes the run step natively
- Step output capture behavior remains unchanged

This fallback is step-local. Other steps continue with their configured behavior.

---

## Output and Templates

Containerized run steps preserve existing run-step features:

- Template expansion in step.run
- stdout/stderr capture
- outputPattern extraction
- outputFile writing

Template rendering still happens before container execution.

---

## Validation Rules

Schema rules enforce that container requires run.

- Valid: run + container
- Invalid: uses + container
- Invalid: command + container

Invalid combinations fail config validation during load.

---

## Practical Notes

- Prefer absolute in-container paths in container.workingDirectory and container.env values.
- Keep container.env scoped to step-specific overrides.
- If your command needs host tooling state, keep that step native or move state into mounted paths.
- For cross-platform consistency, prefer shell commands that run correctly under /bin/sh in the container image.

---

## Example Pattern

A common pattern is to keep builtins native and wrap only toolchain-sensitive run steps:

```jsonc
{
  "commands": {
    "release": {
      "steps": [
        { "id": "resolve-version", "uses": "builtin:resolve-version" },
        {
          "id": "compile",
          "run": "dotnet build solution.slnx -c Release --no-restore",
          "container": { "image": "mcr.microsoft.com/dotnet/sdk:10.0" }
        },
        { "id": "push", "uses": "builtin:push-artifacts" }
      ]
    }
  }
}
```
