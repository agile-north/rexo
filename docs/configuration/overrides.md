# CLI Overrides

Rexo supports config overrides at invocation time through `--set`.

```bash
rx --set runtime.dryRun=true config resolved
rx --set runtime.push.dryRun=true build
rx --set outputs.ci.prefix=MY_ run release
```

Each override uses dotted JSON paths and the final `=` value is parsed as JSON when possible:

- `true` / `false` become booleans
- numbers become numeric values
- `null` becomes a JSON null
- anything else is treated as a string

Rules:

- `--set` is repeatable; later overrides win for the same path.
- Overrides are applied after config file loading and policy merge, so they are the highest-priority config layer.
- Paths are case-insensitive and can target nested objects.

Common examples:

- `--set runtime.dryRun=true` to simulate a run without mutating external systems.
- `--set runtime.push.enabled=false` to disable pushes for a single invocation.
- `--set outputs.ci.prefix=MY_` to change the emitted CI variable prefix.
- `--set versioning.fallback=1.2.3` to override a version fallback locally.

If a value is malformed, Rexo keeps running and reports a warning for the bad override.
