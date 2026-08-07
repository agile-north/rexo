# CI Output Emission

`CiOutputEmitter` turns a `RunManifest` into CI-friendly variables and provider-specific stdout syntax.

It is used in two places:

- After command execution when `outputs.ci.emit` is enabled.
- Internally to build the `REXO_*` environment variables available to run steps.

This page is the quick reference for defaults, provider behavior, and configuration knobs.

## What It Emits

The emitter walks a manifest and flattens it into `path -> value` pairs.

- `safe` mode emits a curated summary of run metadata, counts, version fields, and optional step file outputs.
- `full` mode flattens the entire manifest recursively.
- Include and exclude masks match canonical field paths and accept exact values, wildcards, or `regex:...` patterns.
- Empty or null values are skipped by default.
- Duplicate keys are kept by adding `_2`, `_3`, and so on.
- Sensitive values are redacted by default for post-command CI emission.

## Configuration Defaults

The `outputs.ci` block is optional, but when present these are the runtime defaults:

| Setting | Default | Notes |
| --- | --- | --- |
| `emit` | `true` | Enables post-command CI-native emission. |
| `provider` | `auto` | Resolves to the detected CI provider, or skips outside CI. |
| `prefix` | `REXO_` | Prefix applied to emitted variable names. |
| `keyCasing` | `upperSnake` | Controls how manifest paths become variable keys. |
| `scope` | `safe` | Use `safe`, `full`, or an object with `mode`, `include`, and `exclude`. |
| `includeStepOutputs` | `false` | Adds step file output counts and indexes to safe-mode emission. |
| `emitEmptyValues` | `false` | Empty or null values are omitted unless enabled. |
| `redact` | `true` | Redacts sensitive-looking values before emission. |
| `failOnError` | `false` | Emits a warning instead of failing the command. |
| `maxValueLength` | `8192` | Truncates emitted values after this length. |
| `maxVariables` | `1000` | Stops emission after this many variables. |
| `github-actions.scope` | `env` | Chooses `GITHUB_ENV`, `GITHUB_OUTPUT`, or `GITHUB_STATE`. |

## Provider Behavior

`outputs.ci.provider` controls the stdout syntax used when Rexo emits variables after a command.

| Provider | Output shape |
| --- | --- |
| `generic` or any unknown value | `KEY=VALUE` |
| `github-actions` | `KEY=VALUE` by stdout, or GitHub file output when `GITHUB_ENV`, `GITHUB_OUTPUT`, or `GITHUB_STATE` is available |
| `azure-devops` | `##vso[task.setvariable variable=KEY]VALUE` |
| `teamcity` | `##teamcity[setParameter name='env.KEY' value='VALUE']` |
| `gitlab-ci` | `KEY=VALUE` |
| `bitbucket-pipelines` | `KEY=VALUE` |

Behavior notes:

- `provider: auto` uses the detected CI provider.
- When `provider: auto` is used outside CI, post-command CI emission is skipped.
- A non-`auto` provider can be forced locally for testing.
- When GitHub file output is configured but the target environment variable is missing, Rexo falls back to stdout and warns.

## Run-Step Variables

Run steps always receive an internal `REXO_*` variable block built from the same manifest contract.

That internal profile uses fixed defaults:

- Provider: `generic`
- Prefix: `REXO_` unless `outputs.ci.prefix` overrides it
- Key casing: `upperSnake`
- Include step outputs: `true`
- Emit empty values: `true`
- Redact: `false`
- Max value length: `8192`
- Max variables: `1000`

This internal step environment is independent from `outputs.ci.emit`.

## Example

```jsonc
{
  "outputs": {
    "ci": {
      "emit": true,
      "provider": "github-actions",
      "prefix": "CI_",
      "keyCasing": "upperSnake",
      "includeStepOutputs": true,
      "scope": {
        "mode": "safe",
        "include": ["repo_*", "version_*"],
        "exclude": ["regex:^repo_root$"]
      },
      "github-actions": {
        "scope": "output"
      }
    }
  }
}
```

Given a manifest like this:

- `repoName`: `orders-api`
- `exitCode`: `0`
- `success`: `true`
- `durationMs`: `1250`
- `version.semVer`: `1.2.3`
- `steps.pack.fileOutputs.packages`: `['artifacts/pkg-a.nupkg', 'artifacts/pkg-b.nupkg']`

With `prefix: "CI_"`, `keyCasing: "upperSnake"`, and `scope: "safe"`, the emitted values look like this:

| Manifest path | Emitted key | Example value |
| --- | --- | --- |
| `repoName` | `CI_REPO_NAME` | `orders-api` |
| `exitCode` | `CI_EXIT_CODE` | `0` |
| `success` | `CI_SUCCESS` | `true` |
| `durationMs` | `CI_DURATION_MS` | `1250` |
| `version.semVer` | `CI_VERSION_SEM_VER` | `1.2.3` |
| `steps.pack.fileOutputs.packages.count` | `CI_STEPS_PACK_FILE_OUTPUTS_PACKAGES_COUNT` | `2` |
| `steps.pack.fileOutputs.packages[0]` | `CI_STEPS_PACK_FILE_OUTPUTS_PACKAGES_0` | `artifacts/pkg-a.nupkg` |
| `steps.pack.fileOutputs.packages[1]` | `CI_STEPS_PACK_FILE_OUTPUTS_PACKAGES_1` | `artifacts/pkg-b.nupkg` |

If `scope` is changed to `full`, the same prefixing and key-casing rules apply, but every manifest field is flattened.

Examples of provider-specific stdout formatting for the same `CI_REPO_NAME=orders-api` value:

```text
CI_REPO_NAME=orders-api
```

```text
##vso[task.setvariable variable=CI_REPO_NAME]orders-api
```

```text
##teamcity[setParameter name='env.CI_REPO_NAME' value='orders-api']
```

## When To Use Full Scope

Use `scope: "full"` when you want every manifest field flattened into CI variables.

Use `scope: "safe"` when you want a compact, stable summary for pipelines and log output.
