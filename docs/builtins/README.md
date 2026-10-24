# Builtins Reference

This document describes runtime builtins used by `uses: "builtin:<name>"` steps.

Scope:

- Step-level builtins registered in `ConfigCommandLoader.RegisterBuiltins`
- Their inputs, internal calls, outputs, side effects, and exit behavior

For embedded policy command flows, see [Embedded Policies Reference](../embedded/README.md).

## Builtin Categories

- [Lifecycle](lifecycle.md) — Resolve version, validate, clean
- [Artifacts](artifacts.md) — Plan, build, tag, push artifacts
- [Convenience](convenience.md) — Ship, all (composite wrappers)
- [Docker](docker.md) — Docker-scoped builtins
- [Configuration](config.md) — Config-resolved, config-materialize
- [GitHub release](../embedded/github-release.md) — `builtin:github-release` guards, vars, outputs, and side effects
- [GitHub reports](../embedded/github-reports.md) — `builtin:github-tests` / `builtin:github-coverage`, report processing, Checks, summaries and uploads
- [Patterns & Examples](patterns.md) — Input patterns, shell equivalents, concrete examples

## Execution Model

A builtin is invoked by a step such as:

```json
{
  "id": "push",
  "uses": "builtin:push-artifacts",
  "with": {
    "confirm": "{{options.push}}"
  }
}
```

Input channels available to all builtins:

- Step metadata: `id`, `uses`, `when`, `with`, `outputPattern`, `outputFile`
- Execution context: repo root, branch, commit, CI flags, version, completed steps
- Command args/options: `ctx.Args`, `ctx.Options`
- Repository config: artifacts, outputs, versioning, push rules

`with` mapping behavior:

- Step `with` values are rendered and merged into step-local options before `when` evaluation and builtin execution.
- This is how higher-level command options are passed into reusable builtins.

## Source Of Truth

Implementation source:

- `src/Execution/ConfigCommandLoader.cs`
- `src/Execution/StepExecutor.cs`

When behavior appears to differ from docs, treat code as authoritative and update the relevant doc file.
