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
| `generic` | [generic.md](generic.md) |

## Provider capabilities

Standard build and push use the provider's normal commands and policies; they do not
imply verified handoff or publication. Verified handoff prepares and validates the
exact release outputs. Verified publication sends those previously verified outputs
without rebuilding or discovering a replacement.

| Type | Standard build | Standard push | Verified handoff | Verified publication |
| --- | --- | --- | --- | --- |
| `nuget` | Yes | Yes | Yes | Yes |
| `generic` | Yes | Yes | Yes | Yes |
| `docker` | Yes | Yes | OCI archive only | No |
| `docker-compose` | Yes | Yes | No | No |
| `helm-oci` | Yes | Yes | No | No |
| `helm` | Yes | Yes | No | No |
| `npm` | Yes | Yes | No | No |
| `pypi` | Yes | Yes | No | No |
| `maven` | Yes | Yes | No | No |
| `gradle` | Yes | Yes | No | No |
| `rubygems` | Yes | Yes | No | No |
| `terraform` | Yes (plan) | Yes (apply) | No | No |

Capabilities remain subject to each provider's configuration, tool availability and
push policies. Docker OCI archive mode expressly refuses publication, including ordinary
push; standard Docker push applies to its normal image mode. NuGet handoff requires
fresh exact output paths, including symbols when enabled. A local file hash is not a
remote registry digest; verified publication does not attest the remote registry's state.
See the [provider implementation guide](verified-handoff-providers.md).

## Artifact lifecycle groups

Set the top-level `group` on an artifact to select it independently during plan/build/tag/push
operations. Ungrouped artifacts belong to the implicit `default` group, selected when no
group selector is supplied. For example, `rx release --artifact-group contracts --push` publishes
only artifacts marked `group: contracts`; `rx release --all-artifact-groups --push` publishes all
configured artifacts. Both still use the repository's normal resolved version and verification
lifecycle. This is useful when a smaller artifact lane—such as public contracts consumed by
integrators—needs a release without republishing unchanged runtime packages. A coordinated
release can include every lane with `--all-artifact-groups`; groups do not create separate
version tracks.

Groups are exclusive, case-insensitive selectors; they are not artifact labels, project-build
filters, or separate versioning tracks. See [artifact lifecycle builtins](../builtins/artifacts.md)
for selection and empty-group failure behavior.

## Shared behavior across many providers

NuGet also supports [project discovery with include/exclude globs](nuget.md#discover-projects-with-globs),
such as `**/*Contracts.csproj`. Each packable match becomes an independent artifact
using its evaluated `PackageId`, inheriting shared settings and the lifecycle group.
This is input-project discovery, not wildcard collection of prebuilt packages;
other providers do not yet support `discover`.

The following settings are intentionally consistent across the tool-based providers (`npm`, `pypi`, `maven`, `gradle`, `rubygems`, `terraform`, `helm`):

- `settings.useDocker`: enable/disable Docker fallback when host CLI is unavailable (default `true`)
- `settings.dockerImage`: override fallback image
- `settings.extra-build-args`: additional space-separated arguments for the provider build command
- `settings.extra-push-args`: additional space-separated arguments for the provider push command

Most providers also use a shared target pattern:

- `settings.target.<value>`: explicit configured endpoint/source/registry/workspace
- `settings.target.<value>Env`: env var name used to source that value at runtime

Value precedence is consistent: env value from `settings.target.*Env` (or provider default env key) takes priority over `settings.target.*`.

## Shared auth and CI inference

For providers that support feed/registry token fallback, auth resolution is implemented through shared feed-auth providers in [src/Artifacts/Auth/](../../src/Artifacts/Auth/).

Supported shared CI fallback families:

- GitHub Packages tokens (`GITHUB_TOKEN`)
- GitLab package tokens (`CI_JOB_TOKEN` for explicit GitLab package endpoints)
- Azure Artifacts tokens (`SYSTEM_ACCESSTOKEN`)
- Container registry credentials for GitHub/GitLab CI contexts

Where supported, CI inference is controlled per artifact:

- `settings.ciInference` (preferred)
- `settings.target.ciInference` (compatibility alias)

Default is `true`.

`docker-compose` does not expose `useDocker`/`dockerImage` because it runs through Docker directly.
