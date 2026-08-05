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
- Shell inside container: /bin/sh -c when no custom entrypoint is provided
- Container lifecycle: docker run --rm

Optional container fields:

- `container.entrypoint`: passed to `docker run --entrypoint`.
- `container.dockerfile`: Dockerfile path used to materialize `container.image` before run.
- `container.context`: docker build context directory (default: repository root).
- `container.build.target`: optional docker build stage passed as `--target`.
- `container.build.args`: optional docker build args map passed as repeated `--build-arg key=value`.

When `container.dockerfile` is set, Rexo performs an image preflight:

1. Inspect local `container.image` for label `rexo.container.sourceHash`.
2. Compute current source hash from Dockerfile content, `.dockerignore` (if present), and resolved paths.
3. Build image when missing or when hash differs.
4. Reuse image when hash matches.

---

## Environment Inheritance

Environment passed into the container is merged in this order:

1. Host process environment
2. Repo env overlays from .env and .rexo/.env
3. step.container.env overrides

Later values win when keys collide.

This gives native-feeling behavior while still allowing explicit overrides per step.

Rexo injects a `REXO_*` runtime variable set for run steps (derived from the same
manifest contract used by CI output emission), including values such as command
status, version fields, step counts, and push summary fields.

The `REXO_` prefix is the default. When `outputs.ci.prefix` is configured, run-step
environment variables use that configured prefix instead.

The run-step variable block uses the same manifest contract as post-command CI emission,
but with a fixed internal profile: step outputs are included, empty values are kept,
and sensitive values are not redacted. See [CI Output Emission](ci-output-emission.md)
for the full contract and defaults.

Examples include:

- `REXO_SUCCESS`
- `REXO_VERSION_SEM_VER`
- `REXO_PUSH_DECISIONS_COUNT`

These are available in both native and container-wrapped run steps.

---

## Fallback Semantics

If Docker is unavailable on the host:

- Rexo logs a warning
- Rexo executes the run step natively
- Step output capture behavior remains unchanged

This fallback is step-local. Other steps continue with their configured behavior.

---

## Runtime Visibility

Rexo now emits execution-mode signals for run steps so you can see how a step ran.

Normal output includes:

- container intent line (image, in-container working directory, mount root)
- container command line marker
- explicit native fallback marker when Docker is unavailable

Example markers:

- `[container] image=... workdir=... mount=/work`
- `[container:<image>] <command>`
- `[native:fallback] <command>`

With `--debug`, Rexo also logs container materialization details (Docker argv and
environment merge summary counts).

Run manifests include per-step execution metadata:

- `executionMode`
- `requestedExecutionMode`
- `containerImage`
- `containerWorkingDirectory`
- `containerFallbackUsed`
- `containerFallbackReason`

---

## Output and Templates

Containerized run steps preserve existing run-step features:

- Template expansion in step.run
- Template expansion is also available in container fields such as image, dockerfile,
  context, workingDirectory, entrypoint, env values, and build args.
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
- When `entrypoint` is set, the rendered `run` command is passed as a single argument to that entrypoint.

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
