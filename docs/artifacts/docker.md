# Docker Artifact Provider (`type: "docker"`)

## What this page covers

This page is a provider-specific companion to the detailed configuration reference in [../configuration/README.md](../configuration/README.md).

## Lifecycle mapping

- Build: `docker build` / `docker buildx build`
- Tag: provider tag policy based on resolved version and branch classification
- Push: `docker push` with push-policy gates

## Settings coverage

Docker has the largest settings surface (targets, push gates, classification, tag policy, stages, secrets). Use the canonical reference in [../configuration/README.md](../configuration/README.md).

`buildArgs` values are rendered with the same template context used elsewhere before Rexo passes them to Docker.

### Local OCI handoff

For a no-push candidate, configure an OCI archive:

```yaml
settings:
  runner: buildx
  image: example/app
  ociArchive: artifacts/app.oci.tar
```

The path must be a normalized, repository-relative path without symlinks, commas or
quotes, and must not already exist. Buildx must support the OCI exporter (for example,
a configured `docker-container` builder). This mode cannot be combined with
`buildOutput` or stages. Rexo exports an archive instead of creating local tags,
records its SHA-256 in the release manifest, and does not perform registry login.
Buildx may still access configured base images and build dependencies; use an
offline Dockerfile and builder when network access is prohibited.

`builtin:seal-artifact-handoff` binds the archive to the recorded build hash,
configuration, version and commit. `builtin:verify-artifact-handoff` accepts
`with.path` and verifies the handoff without publication. Rexo checks the OCI
layout, referenced manifests and blob hashes as well as the entire archive hash.
Transfer the archive and receipt together. This local file hash is **not** a
registry digest, signature or proof of a later published image. Run image tests
against the verified archive with tooling that supports OCI archive import; the
handoff itself does not load or execute containers.

Archive mode rejects both ordinary and verified publication before any configured
artifact is pushed. It cannot yet publish the verified image or guarantee
registry tag immutability. Existing non-archive Docker behavior is unchanged.

## Auth

Docker login resolution is shared by Docker and Docker Compose providers:

1. `DOCKER_LOGIN_USERNAME` + `DOCKER_LOGIN_PASSWORD` (or `DOCKER_AUTH_*` aliases)
2. `GITHUB_ACTOR` + `GITHUB_TOKEN` for `ghcr.io` when explicit creds are absent
3. `CI_REGISTRY_USER` + `CI_REGISTRY_PASSWORD` (or `CI_JOB_TOKEN`) for GitLab Container Registry targets when explicit creds are absent

Registry resolution:

- `DOCKER_LOGIN_REGISTRY`
- `DOCKER_AUTH_REGISTRY`
- provider login setting (`loginRegistry`)
- inferred from target image

CI inference toggle:

- `settings.ciInference` (or `settings.target.ciInference`) defaults to `true`
- set to `false` to disable CI-based target/auth inference for this artifact

If Rexo resolves a container registry target (`ghcr.io`, GitLab Container Registry, or `*.azurecr.io`) but no credentials are found, it continues and prints a warning before push.

For Azure DevOps + ACR specifically, keep Docker auth explicit (for example `DOCKER_LOGIN_USERNAME` / `DOCKER_LOGIN_PASSWORD`) or perform a prior `docker login` task in the pipeline. Rexo does not currently infer ACR credentials from `SYSTEM_ACCESSTOKEN`.

## CI-aware target defaults

### GitHub Actions + GHCR

When all of the following are true:

- no explicit image is configured
- no explicit repository is configured
- `GITHUB_ACTIONS=true`
- `GITHUB_REPOSITORY` is set

Rexo infers repository as:

- `ghcr.io/<owner>/<repo>/<artifact-name>`

If the artifact name already matches the repository leaf, Rexo does not repeat it. A single Docker artifact in a repo named `rexo` becomes `ghcr.io/<owner>/rexo`, not `ghcr.io/<owner>/rexo/rexo`.

If the artifact name is an empty string, Rexo treats it as an explicit opt-out and appends nothing. If the name is omitted or null, Rexo falls back to the repo root name first.

Example inferred image for artifact `api` in `agile-north/rexo`:

- `ghcr.io/agile-north/rexo/api`

### GitLab CI (cloud or self-hosted)

When running in GitLab CI with:

- `GITLAB_CI=true`
- `CI_REGISTRY` set
- `CI_PROJECT_PATH` set

and no explicit image/target repository is configured, Rexo infers image as:

- `<CI_REGISTRY>/<CI_PROJECT_PATH>/<artifact-name>`

If the artifact name already matches the project leaf, Rexo does not repeat it.

An empty string name also suppresses the appended leaf here.

This works for both GitLab SaaS and self-hosted registries because `CI_REGISTRY` is used directly.

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

## Minimal CI examples

GitHub Actions with GHCR inferred repository:

```json
{
  "type": "docker",
  "name": "api",
  "settings": {
    "target": {
      "registry": "ghcr.io"
    }
  }
}
```

GitLab CI with fully implicit target from CI env vars:

```json
{
  "type": "docker",
  "name": "api"
}
```
