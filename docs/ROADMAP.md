# Production hardening: requirements and evidence

PR #76 is a **hardening foundation**, not completion of the full production-hardening
initiative. Status is measured against the supplied scope's behavior and acceptance scenarios,
not simply whether a command exists.

| Scope | Status in foundation | Evidence / remaining acceptance |
| --- | --- | --- |
| Container fallback (section 7; scenario D) | Implemented and tested | Resolver and executor tests cover fail-closed defaults and explicit host fallback. |
| Release modes and self-hosting (8–9) | Single-job normal lifecycle; real remote publication unverified | Source-built CLI runs normal release once; config gates push on publication intent, PR context and branch policy. Prepared publication is a separately tested provider capability for NuGet/symbols and generic archives, not used by this pipeline. Docker/OCI capability implementations remain follow-ups. |
| Documentation consolidation (10) | Partial | The actionable accuracy audit of current user/developer docs is complete; consolidation of the historical scope/archive into a single authoritative reference remains separate work. |
| Init lifecycle (11) | Implemented and tested | Init tests cover superseded same-slot variants and preserved legacy files. |
| Repository readiness (12) | Partial | Tool/provider/path/lock findings exist; required-secret resolution, full container/artifact readiness and version resolution remain. `check` is a basic gate, not proof of full readiness. |
| Property provenance (13; scenario E) | Partial / limited contract | Declaration sources only; winning source, overridden values and per-policy merge ownership remain. |
| CLI usability (14) | Partial | Color and non-interactive controls tested; completions are top-level only. |
| Policy lock (15; scenario F) | Partial / content verification only | Source/hash locking tested; immutable resolved identities and retrieval of the locked dependency remain. |
| Environments and promotion (16–19; scenario G) | Partial / local files only | Verified local-file copying tested; QA deployment records, registry digests and exact QA-to-production promotion remain. |
| Provenance, SBOM, attestations (20, 31) | Partial | Run/config/local-file identity exists; policy references, registry identity and optional tool-capability investigation remain. |
| External extensions (21) | Design decision delivered | See [architecture/extensions.md](architecture/extensions.md); no arbitrary assembly loader. |
| Workflow graph (25) | Partial | Effective top-level steps tested; delegated expansion and parallel graph edges remain. |
| Safety, trust, errors and planning (26–41) | Partial | Explicit container fallback and masked outputs tested; not a certification of every mutation, cache or secret boundary. |
| Final scenarios/platforms (53) | Partial | Windows local acceptance; Linux self-host acceptance is a PR CI gate. macOS and complete scenarios B, C, E, F and G remain. |

## Follow-up boundaries

Complete each feature in a focused PR with model/schema changes, human and JSON output,
tests, documentation and its acceptance scenario:

1. Merge-aware property provenance, including policy contributions and redacted override history.
2. Immutable policy resolution and exact locked restore after a floating upstream changes.
3. Comprehensive safe readiness and accurately expanded workflow graphs.
4. Deployment records and immutable artifact promotion from QA to production without rebuilding.
5. Documentation consolidation and optional supply-chain/SchemaStore capability decisions.

Do not advertise declaration reporting as full provenance, hash verification as exact dependency
restore, or local file copying as remote deployment. Existing limited commands remain available,
but their boundaries must remain explicit in user documentation.
