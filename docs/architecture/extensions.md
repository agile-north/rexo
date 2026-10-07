# Extension architecture decision

## Status

**Decision: do not load third-party assemblies into the `rx` process.** Rexo's artifact,
versioning, and secret-provider registries remain statically composed by the CLI. The current
provider interfaces are extension points for first-party projects, not a promise that repository
configuration may load arbitrary code.

## Context

Loading an assembly named by repository configuration would execute code with the CLI process's
filesystem, environment, network, and credential access. A version pin or package hash proves
which bytes were selected; neither constrains what those bytes can do. In-process .NET loading
does not provide a security boundary, and unloadability, trimming, and runtime compatibility
would add further constraints without reducing that authority.

Rexo already has focused provider abstractions (`IArtifactProvider`, `IVersionProvider`, and the
secret-provider contract) and explicit registries. These keep `Core` independent and allow the
CLI to choose the supported implementations at build time.

## Constraints for any future extension work

- Repository configuration must never trigger arbitrary in-process assembly loading.
- A source identity and integrity hash are necessary for reproducibility, but are not a sandbox.
- Extension activation must be opt-in and must fail visibly on an unavailable, incompatible, or
  untrusted extension. It must not silently skip an extension and report success.
- A future external extension should run out of process with a versioned, schema-validated
  request/response protocol, explicit capability declarations, bounded execution, and a clear
  credential policy. The host must not forward ambient credentials by default.
- Any capability that can publish, deploy, modify Git state, or access secrets requires its own
  explicit policy gate and auditable result.
- Compatibility and trimming checks are required before exposing a stable extension contract.

## Consequences

There is no plugin discovery directory, package restore hook, arbitrary assembly loader, or
extension-specific schema in this release. New providers should use the existing interfaces and
be registered by the host application. Revisit external extensions only when a concrete provider
need justifies the protocol, capability, isolation, and compatibility costs above.
