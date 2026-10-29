# Secrets

Rexo supports first-class secret resolution through a top-level `secrets` section.

Use `secrets.items` to define named secrets, then consume them through:

- template values: `{{secrets.<name>}}`
- mapped runtime environment variables via `mapToEnv`
- Windows host logon credentials via command/run-step `passwordSecret`, a named secret reference

## Windows Host Logon Passwords

[`user`, optional `domain`, and `passwordSecret`](commands.md#host-execution-user-user)
allow host shell commands to run as a Windows account. `passwordSecret` names a `secrets.items`
entry; it is not a password or a password template.

```yaml
secrets:
  items:
    buildLogin:
      provider: env
      env: BUILD_ACCOUNT_PASSWORD
      required: false
      exposeInTemplates: false

commands:
  build:
    user: builder
    domain: "."
    passwordSecret: buildLogin
    steps:
      - run: dotnet build
```

Use any existing provider/provider chain for the named secret. Rexo can retrieve it with
`exposeInTemplates: false` and `required: false` when host execution actually needs it;
a missing/empty password still fails that execution. Global required secrets continue to
participate in normal preflight.

Commands can declare `requiredSecrets: [name, ...]` to require and preflight logical
secrets even when their definitions are optional. These references are checked before any
command step runs. Environment-specific secret definitions are deferred for unrelated
commands; a command that needs one should declare it in `requiredSecrets`. Globally required
secrets retain the existing preflight behavior.

Keep login secrets out of template exposure and environment mappings. Rexo supplies the
resolved password directly to Windows process creation, masks it in captured output/errors,
and does not put it in command arguments or config/run-manifest output. Existing inherited
environment variables are not sanitized: an env-backed password source remains in that
environment. Choose a non-env provider if the child must not inherit the password source.
Secrets are handled in process memory; this is not a guarantee of encrypted memory storage.

Container-only execution does not consume optional host logon credentials; explicit host
fallback does. A step changing accounts must provide credentials for the new account rather
than inherit the command's credentials. Unix host switching does not use Windows passwords.

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

## Provider resolution

Rexo resolves each secret item using the first matching branch below.

### 1. Inline provider or provider reference

If `secrets.items.<name>.provider` or `secrets.items.<name>.providerRef` is set, Rexo resolves that route first.
If the provider fails, it only falls back to environment lookup when `fallbackToEnvironment` is enabled.

### 2. Provider chain

If no inline provider is set, Rexo evaluates these chains in order:

1. `secrets.items.<name>.providerChain`
2. `secrets.defaults.providerChain`
3. `secrets.defaults.provider`

If a provider chain is configured, Rexo evaluates candidates in order and skips candidates whose `runtime` does not match the current process:

- `local` matches non-CI execution
- `ci` matches any detected CI
- a CI provider name such as `github-actions`, `azure-devops`, `gitlab-ci`, or `bitbucket-pipelines` matches that runtime only

Routes can also override `selector` and `env`, so the same secret item name can keep one logical identity while pointing at different backing names in different runtimes or providers.

The built-in `github-actions`, `azure-devops`, and `gitlab-ci` secret providers are env-backed aliases: they resolve from the current job environment, which lets you keep CI-specific selection explicit without introducing provider API calls into the runtime.

For advanced GitLab scenarios, use provider type `gitlab` with mode-specific settings.

### GitLab provider modes

`gitlab` supports these modes:

- `env` (default): selector is treated as an environment variable name.
- `variables` (or `api`): fetches a CI variable from GitLab API.
- `vault`: posts selector + OIDC token to a configured vault endpoint and reads value from response JSON.

Token precedence for `variables` mode is configurable via `tokenPrecedence`.
Default precedence is:

1. `token` (auth/settings)
2. `tokenEnv` (auth/settings environment variable indirection)
3. `CI_JOB_TOKEN`
4. `GITLAB_TOKEN`

OIDC precedence for `vault` mode is configurable via `oidcTokenPrecedence`.
Default precedence is:

1. `oidcToken` (auth/settings)
2. `oidcTokenEnv` (auth/settings environment variable indirection)
3. `CI_JOB_JWT_V2`
4. `CI_JOB_JWT`

#### GitLab variables API example

```jsonc
{
  "secrets": {
    "providers": {
      "gitlabApi": {
        "type": "gitlab",
        "settings": {
          "mode": "variables",
          "baseUrl": "https://gitlab.example.com",
          "projectId": "12345",
          "tokenPrecedence": "ciJobToken,gitlabToken"
        }
      }
    },
    "items": {
      "registryPassword": {
        "providerRef": "gitlabApi",
        "selector": "REGISTRY_PASSWORD",
        "mapToEnv": "REGISTRY_PASSWORD"
      }
    }
  }
}
```

Notes:

- `groupId` can be used instead of `projectId`.
- `environmentScope` can be set to pass GitLab variable environment filtering.
- `variablesEndpoint` can override URL construction when needed.

#### GitLab vault example

```jsonc
{
  "secrets": {
    "providers": {
      "gitlabVault": {
        "type": "gitlab",
        "settings": {
          "mode": "vault",
          "vaultEndpoint": "https://vault-gateway.example.com/gitlab/resolve",
          "oidcTokenEnv": "CI_JOB_JWT_V2"
        }
      }
    },
    "items": {
      "nugetApiKey": {
        "providerRef": "gitlabVault",
        "selector": "kv/data/feeds/nuget/api-key",
        "mapToEnv": "NUGET_API_KEY"
      }
    }
  }
}
```

### 3. Environment fallback

When no provider succeeds, Rexo falls back to `env` by default.
Set `fallbackToEnvironment: false` to disable that fallback.

If an environment value begins with `op://`, Rexo treats it as a 1Password selector and resolves it through the 1Password provider automatically.
That lets you keep the item configured as `env` while storing a 1Password reference in the environment on developer machines.

Set `stopOnFirstError: true` when you want the first provider failure to stop resolution instead of falling through to the next candidate.

Both `fallbackToEnvironment` and `stopOnFirstError` can be set at `secrets.defaults` or on an individual secret item. Item values override defaults.

If you want one secret name to use 1Password locally and env in CI, prefer a runtime-aware provider chain rather than a single global 1Password default.

## Required and optional behavior

- Required secrets fail command execution during preflight when unresolved.
- Optional secrets are warmed for template use when `exposeInTemplates` is true.
- `mapToEnv` injects a primary runtime environment variable, and `mapToEnvs` can add more aliases.
- Those aliases flow into command steps, container-wrapped run steps, and artifact provider auth resolution.
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

If you see repeated auth prompts across separate `rx` runs, that is usually 1Password CLI session state, not Rexo secret caching. The durable mitigation is to use service-account or Connect auth for non-interactive flows; Rexo only resolves secrets within the current process and does not manage a long-lived 1Password session.

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
