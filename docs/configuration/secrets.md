# Secrets

Rexo supports first-class secret resolution through a top-level `secrets` section.

Use `secrets.items` to define named secrets, then consume them through:

- template values: `{{secrets.<name>}}`
- mapped runtime environment variables via `mapToEnv`

## Structure

```jsonc
{
  "secrets": {
    "defaults": {
      "provider": "env",
      "required": true,
      "cache": {
        "enabled": false,
        "ttlSeconds": 300
      }
    },
    "providers": {
      "myProvider": {
        "type": "exec",
        "auth": {
          "token": "optional-provider-auth"
        },
        "settings": {
          "command": "my-secret-cli"
        }
      }
    },
    "items": {
      "mySecret": {
        "providerRef": "myProvider",
        "selector": "path/to/secret",
        "required": true,
        "exposeInTemplates": true,
        "mapToEnv": "MY_SECRET"
      }
    }
  }
}
```

## Provider resolution order

For each secret item, provider selection is:

1. `secrets.items.<name>.provider`
2. `secrets.items.<name>.providerRef` -> `secrets.providers.<ref>.type`
3. `secrets.items.<name>.providerChain`
4. `secrets.defaults.providerChain`
5. `secrets.defaults.provider`
6. fallback: `env` when no explicit provider is configured

If a provider chain is configured, Rexo evaluates candidates in order and skips candidates whose `runtime` does not match the current process:

- `local` matches non-CI execution
- `ci` matches any detected CI
- a CI provider name such as `github-actions`, `azure-devops`, `gitlab-ci`, or `bitbucket-pipelines` matches that runtime only

Routes can also override `selector` and `env`, so the same secret item name can keep one logical identity while pointing at different backing names in different runtimes or providers.

The built-in `github-actions` secret provider is env-backed: it resolves from the current job environment, which lets you keep GitHub Actions-specific secret selection explicit without introducing GitHub API calls into the runtime.

Set `stopOnFirstError: true` when you want the first provider failure to stop resolution instead of falling through to the next candidate.

## Required and optional behavior

- Required secrets fail command execution during preflight when unresolved.
- Optional secrets are warmed for template use when `exposeInTemplates` is true.
- `mapToEnv` injects resolved values into runtime environment for command steps and artifact provider auth resolution.
- `providerChain` makes it easy to prefer local tooling such as 1Password on developer machines and native CI secret sources in pipelines without branching scripts.

## Exec provider example

Use `exec` when secrets come from an external command.

```jsonc
{
  "secrets": {
    "providers": {
      "vaultExec": {
        "type": "exec",
        "settings": {
          "command": "vault",
          "args": ["kv", "get", "-format=json", "{selector}"],
          "mode": "json",
          "valuePath": "data.data.value"
        }
      }
    },
    "items": {
      "nugetApiKey": {
        "providerRef": "vaultExec",
        "selector": "secret/feeds/nuget/api-key",
        "mapToEnv": "NUGET_API_KEY"
      }
    }
  }
}
```

Notes:

- `mode` supports `raw` and `json`.
- In `json` mode, `valuePath` selects the value from command output.
- `args` supports token replacement for `{selector}` and `{name}`.

## 1Password provider example

Use `1password` to resolve values via the `op` CLI.

```jsonc
{
  "secrets": {
    "providers": {
      "op": {
        "type": "1password"
      }
    },
    "items": {
      "containerRegistryPassword": {
        "providerRef": "op",
        "selector": "op://ci/docker/password",
        "mapToEnv": "DOCKER_LOGIN_PASSWORD"
      }
    }
  }
}
```

Default command behavior is equivalent to:

```text
op read <selector>
```

Optional provider settings allow custom command layout:

```jsonc
{
  "type": "1password",
  "settings": {
    "command": "op",
    "subcommand": "read",
    "args": ["{selector}"]
  }
}
```

### 1Password auth modes

The provider works with ambient 1Password CLI auth when `op` is already usable in the current environment.

That includes common local and CI cases such as:

- a prior `op signin`
- `OP_SERVICE_ACCOUNT_TOKEN`
- `OP_CONNECT_HOST` plus `OP_CONNECT_TOKEN`
- optional `OP_ACCOUNT`

You can also configure provider auth explicitly so Rexo injects the right environment variables for the `op` process.

Example using a service account token from an existing environment variable:

```jsonc
{
  "secrets": {
    "providers": {
      "op": {
        "type": "1password",
        "auth": {
          "serviceAccountTokenEnv": "OP_SERVICE_ACCOUNT_TOKEN"
        }
      }
    },
    "items": {
      "nugetApiKey": {
        "providerRef": "op",
        "selector": "op://feeds/nuget/api-key"
      }
    }
  }
}
```

Supported `auth` keys for `1password`:

- `serviceAccountToken` or `serviceAccountTokenEnv`
- `connectHost` or `connectHostEnv`
- `connectToken` or `connectTokenEnv`
- `account` or `accountEnv`

Recommendation:

- prefer `...Env` forms so tokens stay out of `rexo.json`
- use service-account auth for CI/non-interactive execution
- use ambient local `op signin` for developer machines unless your team standardizes on service accounts

## Artifact auth integration example

Mapped secret env values can be consumed by existing artifact auth settings without changing artifact schema.

```jsonc
{
  "secrets": {
    "providers": {
      "op": { "type": "1password" }
    },
    "items": {
      "nugetApiKey": {
        "providerRef": "op",
        "selector": "op://feeds/nuget/api-key",
        "mapToEnv": "MY_FEED_API_KEY"
      }
    }
  },
  "artifacts": [
    {
      "type": "nuget",
      "name": "Rexo.Core",
      "settings": {
        "target": {
          "source": "https://api.nuget.org/v3/index.json",
          "apiKeyEnv": "MY_FEED_API_KEY"
        }
      }
    }
  ]
}
```

## Security notes

- Secret values are redacted in logs and output.
- Preflight errors identify missing references without printing secret values.
- Cache is command-scope in-memory only.

## Diagnostics

Use built-in diagnostics commands to validate secret resolution safely:

```bash
rx secrets doctor
rx secrets preflight
```

Behavior:

- Validates configured secret items using the same preflight path as command execution.
- Reports provider, required/optional status, source, template exposure, and `mapToEnv` metadata.
- Never prints secret values.
- Returns non-zero when required secrets are unresolved.

Common 1Password failures the diagnostics can help surface:

- `op` CLI not installed or not on `PATH`
- CLI not signed in locally
- missing service account or Connect auth environment
- bad selector path such as an invalid `op://...` reference
