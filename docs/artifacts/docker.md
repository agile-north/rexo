# Docker Artifact Provider (`type: "docker"`)

## What this page covers

This page is a provider-specific companion to the detailed configuration reference in [../configuration/README.md](../configuration/README.md).

## Lifecycle mapping

- Build: `docker build` / `docker buildx build`
- Tag: provider tag policy based on resolved version and branch classification
- Push: `docker push` with push-policy gates

## Settings coverage

Docker has the largest settings surface (targets, push gates, classification, tag policy, stages, secrets). Use the canonical reference in [../configuration/README.md](../configuration/README.md).

## Auth

Docker login resolution is shared by Docker and Docker Compose providers:

1. `DOCKER_LOGIN_USERNAME` + `DOCKER_LOGIN_PASSWORD` (or `DOCKER_AUTH_*` aliases)
2. `GITHUB_ACTOR` + `GITHUB_TOKEN` for `ghcr.io` when explicit creds are absent

Registry resolution:

- `DOCKER_LOGIN_REGISTRY`
- `DOCKER_AUTH_REGISTRY`
- provider login setting (`loginRegistry`)
- inferred from target image

## Example

```json
{
  "type": "docker",
  "name": "api",
  "settings": {
    "image": "ghcr.io/org/api",
    "dockerfile": "Dockerfile",
    "context": ".",
    "latest": true
  }
}
```
