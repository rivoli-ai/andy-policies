# ADR 0001 — Policy versioning

## Status

**Accepted** — 2026-04-21. Gates: P1.1 (rivoli-ai/andy-policies#71), P1.4 (#74) may proceed after this ADR is merged. Phase 0 tracker: rivoli-ai/andy-policies#94.

Supersedes: **rivoli-ai/andy-rbac#10** (Epic V — Policies as first-class) in part. The allow/deny/flags Rules DSL designed in rivoli-ai/andy-rbac#17 (Epic V1) is preserved as `PolicyVersion.Rules`; the surrounding aggregate shape is redesigned here.

Companion to: ADR 0002 lifecycle-states (drafted this cycle — defines the states the versioning model references), ADR 0006 audit-hash-chain (records every version mutation), ADR 0008 bundle-pinning (pins `{policyId → policyVersionId}` tuples for consumer reproducibility).

## Context

Epic P1 (rivoli-ai/andy-policies#1) introduces the core catalog entities. The aggregate must satisfy four hard requirements:

1. **Stable policy identity.** Consumer references (`DelegationContract.policyVersionId` in rivoli-ai/andy-tasks Epic U, ActionBus in Conductor) must resolve deterministically — consumers pin a specific version and see its rules *as-of that version* regardless of how the catalog evolves afterwards.
2. **Mutable active rules + immutable history.** Authors iterate (draft → publish → supersede → draft v2) without breaking consumers pinned to older versions. Once published, a version's content cannot change — a correction requires a new version, not an in-place edit.
3. **Monotonic version numbers per policy.** Human-readable ordering for audit reports ("v3 was published, then v4 superseded it") and for CLI output; DB-enforced uniqueness on `(policyId, version)` prevents gap-filling.
4. **Three orthogonal content dimensions** per version — Enforcement (MUST/SHOULD/MAY), Severity (Info/Moderate/Critical), Scope (flat string list in P1; hierarchy lives in P4) — each independently reasoned about by consumers.

The Phase 0 review flagged three drift risks this ADR addresses:

- **Enum name drift** between P1.1 (`PolicyVersionState`) and P2.1 (`LifecycleState`). Fixed in the fix-pass (commit ef0ddd0 precursor): P1.1 now uses `LifecycleState` from the start, extended rather than redefined by P2.1.
- **Rules DSL non-goal**: `PolicyVersion.Rules` is a JSON-serialized DSL preserved from the superseded Epic V. The schema is owned by *consumers* (Conductor's ActionBus evaluator interprets it); this service treats it as an opaque `jsonb` blob with byte-for-byte stability.
- **Consumer reference shape**: consumers always reference `policyVersionId` (Guid), never `(policyId, version int)`. This keeps the reference stable under rename/migration and makes it usable as a foreign key.

## Decisions

### 1. Aggregate shape: **`Policy` + `PolicyVersion[]`** split

```
Policy                       1 ──── *  PolicyVersion
├ Id (Guid, pk)                       ├ Id (Guid, pk)
├ Name (slug, unique)                 ├ PolicyId (FK)
├ Description                         ├ Version (int, monotonic per-policy, ≥ 1)
├ CreatedAt                           ├ Enforcement (MUST | SHOULD | MAY)
└ CreatedBySubjectId                  ├ Severity   (Info | Moderate | Critical)
                                      ├ Scopes     (string[], flat)
                                      ├ Rules      (jsonb — opaque DSL, byte-stable)
                                      ├ Summary    (string)
                                      ├ State      (LifecycleState; see ADR 0002)
                                      ├ ProposerSubjectId   (required on draft creation)
                                      ├ PublishedAt         (nullable)
                                      ├ PublishedBySubjectId (nullable)
                                      ├ SupersededByVersionId (nullable; set when newer version goes Active)
                                      ├ CreatedAt
                                      └ CreatedBySubjectId
```

**`Policy` holds only stable identity** — name uniqueness, creation metadata. No version-dependent fields.

**`PolicyVersion` holds all content + lifecycle state.** Every field that ever varies across time lives here.

**The `ProposerSubjectId` on `PolicyVersion` is load-bearing for ADR 0007's self-approval invariant** — set on draft creation, never modified afterwards, used at publish time to enforce `actor != proposer`.

Rejected:
- **Single `Policy` with a `Revision` counter** (the shape drafted in the superseded rivoli-ai/andy-rbac#17): no history of prior rules; consumers pinning "policy X as of March" cannot resolve.
- **Event-sourced policy state** (Marten / EventStoreDB): adds projection/streams/snapshots infra outside the .NET 8 + Postgres/SQLite stack; catalog reads are rare compared to writes, and event sourcing's value (fast projection catch-up) does not pay for itself here.
- **Git-backed version store**: two systems of record; P6 audit chain would need to bridge to git hashes; operator burden.

### 2. Version numbers: **monotonic `int`, starting at 1, unique per policy**

DB constraint: unique index on `(PolicyId, Version)`. Gaps are explicitly disallowed — `V1` → `V2` → `V3`, never `V1` → `V3`. A version-bump under contention uses optimistic concurrency (EF `RowVersion` — provider-specific column below).

Rationale:
- **Humans read policy catalogs.** Audit reports read "v3 was superseded by v4"; monotonic ints surface ordering without a second column.
- **CLI output is cleaner.** `andy-policies-cli versions list` prints an integer column.
- **Cross-service reference is still Guid.** `policyVersionId` is the foreign key; the int is a human label.

Rejected:
- **Semver (`1.0.0` / `1.0.1`)**: consumers would need to parse + compare; value is unclear when the meaning of "major/minor/patch" changes is opaque to this service (it's a content catalog, not a code library).
- **Timestamps as version identity** (e.g. `2026-04-21T17:33:02Z`): clock-skew across deployments; equality comparison fragility; unreadable in tables.
- **UUIDv7 only** (time-ordered Guid): adequate for sorting but doesn't give humans an integer to reference. Compose both: Guid pk + int display.

### 3. Draft mutability window: **`State = Draft` is the only mutable state**

A `PolicyVersion` with `State = Draft` may be edited (`PUT /api/policies/{id}/versions/{vId}`). All other states are immutable; attempting to `PUT` against a non-Draft returns **409 Conflict**.

**Domain invariant enforced at `SaveChangesAsync`**: an `EntityEntry<PolicyVersion>.Property(p => p.Rules).IsModified == true` (or any content property modified) on an entity with `State != Draft` throws `InvalidOperationException("PolicyVersion {id} is immutable (state={state}).")` before the SQL `UPDATE` is issued.

The lifecycle state itself transitions via dedicated endpoints (ADR 0002 §Transition endpoints) — those mutate `State`, `PublishedAt`, `PublishedBySubjectId`, `SupersededByVersionId` which are explicitly whitelisted by the immutability guard.

Rejected:
- **Full immutability (append-only PolicyVersion)**: drafts would need to be separate entities that "become" versions; adds complexity for no benefit. Drafts are versions in the `Draft` state.
- **Copy-on-write semantics** (`UPDATE` creates a new `Version + 1` silently): hides intent; authors expect to iterate on a draft before committing to a version bump.

### 4. Only one open Draft per policy

Enforced via a unique partial index (`UNIQUE (PolicyId) WHERE State = 'Draft'`) on Postgres; SQLite uses a `WHERE` clause on the same index (SQLite supports partial indexes since 3.8.0, 2013). P2.1's state-machine will add the same pattern for `Active`.

Rationale: if two concurrent drafts could coexist, a publish race would supersede whichever landed first, wasting the author's work on the loser. A single active draft per policy forces serialization at the author layer.

Rejected:
- **Multiple drafts allowed, conflicted on publish**: worse UX; authors hit the conflict late.
- **Author-scoped drafts** (one draft per `(PolicyId, ProposerSubjectId)`): opens the door to parallel authoring but complicates the "which draft becomes v{N+1}?" decision. Out of scope for v1.

### 5. Rules DSL: **opaque `jsonb` with byte-stable storage**

`PolicyVersion.Rules` is a `jsonb` column (Postgres) / `TEXT` (SQLite — store as RFC 8785 JCS canonical string per ADR 0006 to preserve byte stability). This service **does not interpret it**. The schema is owned by consumers (Conductor ActionBus, andy-tasks approval gates). The superseded rivoli-ai/andy-rbac#17 shape is preserved:

```json
{
  "allow": ["read"],
  "deny":  ["write", "deploy"],
  "scopes": ["feature-branch"],
  "flags":  { "sandboxed": true, "no_prod": true, "draft_only": false, "high_risk": false }
}
```

**Byte stability constraint**: at save time, the JSON is canonicalised per ADR 0006 (RFC 8785 JCS) before persistence. This way:
- ADR 0006's audit-chain hash over a `PolicyVersion` is deterministic — two identical-looking rule updates always hash identically
- ADR 0008's bundle snapshot is reproducible — a consumer re-hashing the stored `SnapshotJson` gets the same bytes

**Size cap: 64 KB.** Enforced at the validation layer (`CreateVersionRequest`); prevents DoS via unbounded JSON blobs. Exceeds the simulator's reference policies by ~100×; we can revisit if a real use case exceeds it.

### 6. Dimensions: **three enums, canonical values**

| Dimension | Values | Stored as | Wire format |
|---|---|---|---|
| `Enforcement` | `MUST`, `SHOULD`, `MAY` (RFC 2119) | string (per ADR 0006 JCS stability) | string (uppercase RFC 2119 form) |
| `Severity` | `Info`, `Moderate`, `Critical` | string | `"info"`, `"moderate"`, `"critical"` (lowercase) |
| `Scopes` | string[] flat list | jsonb array (Postgres) / canonicalised string (SQLite) | JSON array |

`Enforcement` uses uppercase RFC 2119 strings (`MUST` / `SHOULD` / `MAY`) — standard semantics consumers can reference directly. `Severity` uses lowercase for REST/MCP wire format (`info`/`moderate`/`critical`) matching the criticality mapping carried over from rivoli-ai/andy-rbac#18 reconciliation (`read-only` → Info, `write-branch` → Moderate, `sandboxed` → Moderate, `draft-only` → Info, `no-prod` → Critical, `high-risk` → Critical). Proto wire: see `PolicyServiceProto` in P1.7.

`Scopes` in P1 is a flat string list. Hierarchical scope resolution (Org → Tenant → Team → Repo → Template → Run with the stricter-tightens-only rule) is Epic P4 (rivoli-ai/andy-policies#4). P1 intentionally ships the flat list so consumers have a non-null scope field from day 1; P4 adds the `ScopeNode` entity and resolution.

### 7. Optimistic concurrency on `PolicyVersion`

EF `RowVersion` column:
- **Postgres**: `xmin` (system column, EF Core Npgsql provider's recommended pattern) via `.IsRowVersion()`. No storage cost, no manual increment.
- **SQLite**: `byte[]` column updated in `SaveChangesAsync` via a value converter. Manual but portable.

Concurrent edits to the same Draft produce `DbUpdateConcurrencyException` → HTTP **409 Conflict** with the server's current `RowVersion` in the response; the client retries with the fresh version or merges intent.

Rejected:
- **Uniform `byte[] RowVersion` across providers**: loses the Postgres `xmin` optimisation; adds a write-time trigger. Keep the Npgsql idiom.
- **Pessimistic locking (`SELECT FOR UPDATE`)**: serialises author sessions more than necessary; draft edits should retry-loop, not block.

### 8. Cascade behaviour on `Policy` delete

**`Policy` is never deleted once any `PolicyVersion` exists on it** — attempting `DELETE /api/policies/{id}` when any version has been published (ever) returns **409 Conflict** with `errorCode = "policy.has-history"`. The only legitimate cleanup path for an unused draft is to delete the single `Draft` version first (`DELETE /api/policies/{id}/versions/{vId}`), then delete the empty `Policy`.

Published history must survive in perpetuity — consumers (bundle pins, audit chain, DelegationContract references) depend on it.

Rejected:
- **Cascade delete**: silently orphans audit events and bundle entries.
- **Soft-delete `Policy`** with a `DeletedAt` flag: adds filtering complexity across every read path for a rarely-exercised case.

## Consequences

### Positive

- **Consumer references stable by Guid** — rename a policy, re-slug it, the `policyVersionId` still resolves.
- **Version bumps are cheap** — a draft row plus a unique partial index; no "fork from parent" aggregate copy.
- **Immutability is a domain invariant** — the `SaveChangesAsync` guard fails loud before a bad SQL `UPDATE` reaches the DB.
- **Bytes are stable** — RFC 8785 JCS canonicalisation at save-time means ADR 0006 hashes and ADR 0008 bundle snapshots are byte-for-byte reproducible.
- **RFC 2119 Enforcement + explicit Severity** — consumers can reason about policies using a shared vocabulary that isn't invented here.

### Negative / accepted trade-offs

- **No semantic version numbers** — human readers see `v3 → v4` not `2.0.0 → 2.1.0`. Documented as deliberate; release-note narrative lives in the version's `Summary` and in rationale fields captured per ADR 0002 transitions.
- **Rules DSL is opaque here** — two bugs are possible: (a) a version stores syntactically invalid rules that consumers reject at resolve time; (b) two consumers interpret the same rules differently. (a) is mitigated by consumer-owned validation at publish (Conductor can reject a `publish` if it can't parse the rules); (b) is out of scope — the rules schema is a cross-service contract, not a policies-catalog concern.
- **Postgres `xmin` vs SQLite `byte[]`** — two provider-conditional code paths in `AppDbContext.OnModelCreating`. Minor tax; documented in P1.1 implementation.
- **`Policy` is never deleted post-publish** — if a policy was created in error and never published, operators have a narrow path (delete the Draft first). Published-then-regretted policies live forever. Acceptable for a governance catalog.

### Follow-ups

- ADR 0002 (drafted this cycle) defines the `LifecycleState` enum values and transition semantics referenced above.
- P1.1 (rivoli-ai/andy-policies#71) implementation can now proceed with the aggregate shape pinned.
- P1.4 (`IPolicyService`, #74) implementation can proceed with the service contract shape pinned.
- ADR 0008 (drafted as part of P8 epic; not Phase 0) will reference this ADR for the `SnapshotJson` freeze model — bundles store `{policyId → policyVersionId}` tuples.

## Considered alternatives

| Alternative | Rejected because |
|---|---|
| **Single `Policy` with a `Revision` counter** (superseded Epic V shape) | No history of prior rules; consumers cannot pin "as of date X" |
| **Event-sourced policy state** (Marten / EventStoreDB) | Adds infra dep outside Postgres/SQLite; projections/snapshots don't earn their keep for write-light, read-rare catalog |
| **Git-backed version store** | Two systems of record; P6 audit chain bridge complexity; operator burden |
| **Semver version numbers** (`1.0.0` / `1.0.1`) | Consumer parsing cost; "meaning of minor vs patch" is opaque to a content catalog |
| **Timestamp-as-version** | Clock-skew across deployments; equality comparison fragility |
| **UUIDv7 time-ordered IDs only (no int)** | No human-readable integer column for tables/reports |
| **Full aggregate immutability (drafts as separate entity)** | Drafts ARE versions in `Draft` state; separating them adds complexity for no benefit |
| **Copy-on-write semantics on edit** | Hides intent — authors expect to iterate on a draft |
| **Multiple concurrent drafts per policy** | Publish race wastes the losing author's work |
| **Author-scoped drafts** (one per proposer) | Ambiguous `v{N+1}` publish winner; out of scope for v1 |
| **Typed rules DSL** (validate schema at insert) | Schema is owned by consumers; content catalog must stay opaque |
| **Rules DSL size unbounded** | DoS vector; 64 KB cap matches observed need ×100 |
| **Byte-unstable JSON storage** | Breaks ADR 0006 hash chain + ADR 0008 bundle snapshot reproducibility |
| **Semver-style `Enforcement`** (custom levels) | RFC 2119 is already the industry shared vocabulary |
| **Hierarchical `Scopes` in P1** | Epic P4 owns the hierarchy; P1 ships the flat list so consumers have a non-null field from day 1 |
| **Uniform `byte[] RowVersion` across providers** | Loses Postgres `xmin` optimisation |
| **Pessimistic locking** | Serialises authors more than needed |
| **Cascade delete of `Policy`** | Orphans audit events and bundle entries |
| **Soft-delete `Policy`** | Filtering complexity across every read path for a rarely-exercised case |

---

**Authors**: drafted by Claude 2026-04-21; accepted same day after Phase 0 review. Phase 0 tracker: rivoli-ai/andy-policies#94. Post-acceptance edits require a follow-up ADR (`0001.1-…` or supersede pattern) — this ADR is load-bearing for ADR 0002, ADR 0006, and ADR 0008.
