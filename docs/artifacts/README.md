# Artifact Providers

This folder contains provider-specific artifact documentation.

## How to use this section

1. Start here to choose a provider.
2. Open the provider page for settings, auth resolution, and examples.
3. Use [../configuration/README.md](../configuration/README.md) for the complete top-level rexo.json model.

## Provider index

| Type | Provider doc |
| --- | --- |
| `docker` | [docker.md](docker.md) |
| `docker-compose` | [docker-compose.md](docker-compose.md) |
| `nuget` | [nuget.md](nuget.md) |
| `helm-oci` | [helm-oci.md](helm-oci.md) |
| `helm` | [helm.md](helm.md) |
| `npm` | [npm.md](npm.md) |
| `pypi` | [pypi.md](pypi.md) |
| `maven` | [maven.md](maven.md) |
| `gradle` | [gradle.md](gradle.md) |
| `rubygems` | [rubygems.md](rubygems.md) |
| `terraform` | [terraform.md](terraform.md) |
| custom type (`generic`) | [generic.md](generic.md) |

## Shared behavior across many providers

The following settings are intentionally consistent across the tool-based providers (`npm`, `pypi`, `maven`, `gradle`, `rubygems`, `terraform`, `helm`):

- `settings.useDocker`: enable/disable Docker fallback when host CLI is unavailable (default `true`)
- `settings.dockerImage`: override fallback image
- `settings.extra-build-args`: additional space-separated arguments for the provider build command
- `settings.extra-push-args`: additional space-separated arguments for the provider push command

Most providers also use a shared target pattern:

- `settings.target.<value>`: explicit configured endpoint/source/registry/workspace
- `settings.target.<value>Env`: env var name used to source that value at runtime

Value precedence is consistent: env value from `settings.target.*Env` (or provider default env key) takes priority over `settings.target.*`.

`docker-compose` does not expose `useDocker`/`dockerImage` because it runs through Docker directly.
