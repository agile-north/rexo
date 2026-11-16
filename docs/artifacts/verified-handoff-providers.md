# Implementing verified artifact handoffs

Handoffs are optional capabilities alongside `IArtifactProvider`. Standard build,
tag and push continue to use that interface. Implement `IPreparedArtifactProvider`
to prepare and verify outputs; implement `IPreparedArtifactPublisher` as well only
when the provider can publish the supplied verified outputs without rebuilding or
discovering replacements. The publisher extends the preparation interface. A
verification-only provider must not advertise publication.

## Lifecycle and responsibilities

1. A successful build records the complete output inventory and immutable identity
   of each output in `ArtifactBuildResult.HandoffOutputs`. Capture identities as part
   of the build, not later when sealing. A provider can instead return a single
   local file as `Location`: the executor hashes it immediately after build.
   Never claim evidence for an ambiguous, stale or missing output. Return an empty
   inventory when a normal build succeeds but cannot supply reliable handoff evidence.
2. `PrepareAsync` describes the exact release outputs as a `PreparedArtifact`.
   Include every required output, including symbols or secondary files. Each
   `PreparedArtifactOutput` needs a nonempty kind, reference and immutable identity;
   references must be unique within a kind. For local files use `PreparedArtifactFiles`
   to normalize repository-relative paths and compute SHA-256 hashes.
3. Rexo requires a completed, successful unpublished release; matches the artifact
   inventory and source project; compares all prepared identities with build-time
   evidence; and checks config and policy-lock hashes, version, source revision,
   selected group and applicable CI run identity. It calls `ValidatePreparedAsync`
   with the original build record on both seal and verify. The provider must verify
   the complete current contents or immutable remote reference, the expected
   configured identity and version, and any format-specific constraints. Throw an
   explicit error for missing, altered, ambiguous or unsupported outputs.
4. On verified push Rexo verifies the whole receipt, requires confirmation, checks
   every provider supports `IPreparedArtifactPublisher` before any push, and applies
   normal push policies. `PushPreparedAsync` must publish exactly the supplied
   outputs, revalidate them as needed, and fail if it cannot do so. Never invoke
   build, select files with globs, or fall back to ordinary `PushAsync`.

For example, `GenericArtifactProvider` implements the publisher capability. Its
preparation and validation follow this pattern (see the provider for path
resolution and publication code):

```csharp
public async Task<PreparedArtifact> PrepareAsync(
    ArtifactConfig artifact, ExecutionContext context, CancellationToken cancellationToken) =>
    await PreparedArtifactFiles.PrepareAsync(
        artifact, [ResolvePreparedPath(artifact, context)], context.RepositoryRoot, cancellationToken);

public Task ValidatePreparedAsync(
    ArtifactConfig artifact, PreparedArtifact prepared, ArtifactManifestEntry build,
    ExecutionContext context, CancellationToken cancellationToken) =>
    ValidatePreparedFilesAsync(artifact, prepared, context, cancellationToken);

private static async Task ValidatePreparedFilesAsync(
    ArtifactConfig artifact, PreparedArtifact prepared, ExecutionContext context, CancellationToken cancellationToken)
{
    if (!PreparedArtifactFiles.MatchesPaths(prepared, artifact,
        [ResolvePreparedPath(artifact, context)], context.RepositoryRoot))
    {
        throw new InvalidOperationException("Prepared outputs do not match the configured artifact and version.");
    }
    await PreparedArtifactFiles.ValidateAsync(prepared, context.RepositoryRoot, cancellationToken);
}
```

Its build returns the archive's `Location`, so the executor records its hash;
`PushPreparedAsync` revalidates and copies the exact prepared reference.
`DockerArtifactProvider` implements only `IPreparedArtifactProvider`: it checks
the OCI layout and binds the archive hash to the recorded build, but does not
claim registry publication.

## Integrity and compatibility

File references must remain inside the repository and must not traverse symlinks.
SHA-256 identifies local bytes; it is not automatically a registry manifest digest
or a guarantee that a remote feed stores identical bytes. Immutable remote
references require provider-specific validation of their identity and accessibility.
Do not put credentials in artifact references, identities, receipts, output or logs.
Propagate cancellation and report errors rather than silently falling back to a
different output or success.

Receipts are integrity records, not signed attestations: protect their storage and
the artifacts from untrusted modification and concurrent writers. They do not prove
remote tag immutability or collision handling. NuGet verified push disables duplicate
skipping but does not independently attest remote bytes. Use external registry
controls where those guarantees are required.

Existing single-file receipts remain verifiable when their original build records
contain a matching location and SHA-256. Older receipts without enough build-time
identity evidence, including multi-file inventories without recorded hashes, fail
closed and must be rebuilt into fresh output paths and resealed. NuGet handoffs
require all expected package and symbol paths not to exist before packing; ordinary
NuGet builds are unchanged.
