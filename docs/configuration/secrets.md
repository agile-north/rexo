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
3. `secrets.defaults.provider`
4. fallback: `env`

## Required and optional behavior

- Required secrets fail command execution during preflight when unresolved.
- Optional secrets are warmed for template use when `exposeInTemplates` is true.
- `mapToEnv` injects resolved values into runtime environment for command steps and artifact provider auth resolution.

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
