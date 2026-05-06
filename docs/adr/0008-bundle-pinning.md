# ADR 0008 — Bundle pinning

## Status

**Accepted** — 2026-05-06. Drafted retroactively after the P8.1–P8.7 implementation stories landed (P8.1 #81, P8.2 #82, P8.3 #83, P8.4 #84, P8.5 #85, P8.6 #86, P8.7 #87 already merged on `main`). The decisions captured here describe the implementation as shipped; future divergence requires a follow-up ADR.

Related: ADR 0001 policy-versioning (#92), ADR 0006 audit-hash-chain (#54 — provides the canonical-JSON + SHA-256 helper this ADR reuses), ADR 0007 edit-rbac (#56 — provides the per-resource permission codes the bundle endpoints gate against).

## Context

Epic P8 (rivoli-ai/andy-policies#8) introduces bundle pinning. A `Bundle` is a frozen, materialized snapshot of the live catalog (active `PolicyVersion`s × live `Binding`s × approved `Override`s × all `ScopeNode`s) addressed by a single `bundleId`. Consumers — Conductor's admission gate, andy-tasks per-task gates, andy-mcp-gateway tool policy — pin a bundle id and receive identical answers across catalog mutations until they explicitly re-pin.

Without bundle pinning, a Conductor admission decision made at `T` would yield a different answer at `T+5min` if a publish landed between the two reads. That violates the reproducibility consumers need for release-pinning, replay debugging, and compliance reporting. The README's headline framing — *"consumers pin a bundle version for reproducibility"* — only holds if the bundle is genuinely frozen end to end.

The implementation has shipped across seven stories. Three architectural commitments thread through all of them and need pinning in an ADR so future contributors don't unknowingly break them:

1. **Materialization** — every bundle row carries the entire snapshot bytes; reads never consult live tables.
2. **Atomic capture** — the snapshot is taken under a serializable transaction; a publish that lands mid-snapshot either appears in the bundle entirely or not at all.
3. **Soft-delete** — bundles are never hard-deleted, because the audit chain references them by id.

This ADR captures all three plus the surrounding decisions a reviewer needs to understand the bundle pinning surface as a whole.

## Decisions

### 1. Materialization, not lazy resolution

`Bundle.SnapshotJson` (P8.1 #81) carries the entire frozen graph as canonical-JSON bytes. A bundle read — `GET /api/bundles/{id}/resolve`, `policy.bundle.resolve` (MCP), `BundleService.ResolveBundle` (gRPC) — parses the snapshot into `BundleSnapshot` once per `(bundleId, snapshotHash)` cache entry (P8.3 #83) and answers entirely from memory. **Live catalog state is never consulted on a bundle read.**

The `BundleSnapshot` value object (P8.1) is the authoritative materialized contract:

```csharp
public sealed record BundleSnapshot(
    string SchemaVersion,                // "1" today
    DateTimeOffset CapturedAt,           // clock.GetUtcNow() at create time
    string AuditTailHash,                // P6 chain tail, hex-encoded
    IReadOnlyList<BundlePolicyEntry> Policies,
    IReadOnlyList<BundleBindingEntry> Bindings,
    IReadOnlyList<BundleOverrideEntry> Overrides,
    IReadOnlyList<BundleScopeEntry> Scopes);
```

Each entry carries enough context to answer every resolution-shaped read without re-joining to a live table. A consumer asking *"what policies apply to repo:rivoli-ai/conductor under bundle B?"* needs zero live-state lookups.

### 2. Atomic capture under a serializable transaction

`BundleService.CreateAsync` (P8.2 #82) opens a `Serializable` transaction (when the provider supports it), runs the snapshot builder + insert + audit append inside it, and commits atomically:

```csharp
var ownTxn = ambient is null && _db.Database.IsRelational()
    ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
    : null;
try
{
    // active-name precheck
    // snapshot builder reads Active versions, live bindings, approved overrides, scopes
    // canonical-JSON serialise + SHA-256 hex hash
    // INSERT bundle row
    // append bundle.create audit event with snapshotHash + auditTailHash
    if (ownTxn is not null) await ownTxn.CommitAsync(ct);
}
```

Under Postgres this enables SSI (serializable snapshot isolation): a concurrent publish that conflicts surfaces as `40001 SerializationFailure` and the caller retries. Under SQLite the engine serialises writers behind a process-wide latch; the `IsRelational()` branch makes the in-memory test provider a no-op (it would otherwise reject `BeginTransactionAsync(Serializable)`).

The integration tests in `ConcurrentPublishFreezeTests` (P8.7 #87) prove the freezing guarantee end-to-end: a publish and a binding addition landed *after* the bundle's `CreateAsync` returns are not visible through the bundle's resolver.

### 3. SHA-256 over canonical JSON, shared with the audit chain

`Bundle.SnapshotHash` is `SHA-256(canonicalJson(snapshot))` hex-encoded. The canonical-JSON helper is `Andy.Policies.Shared.Auditing.CanonicalJson`, **the same library P6.2 #42 uses for the audit chain** (pinned in ADR 0006). Algorithm specifics:

- Lexicographic key ordering at every nesting level.
- UTF-8 encoding without BOM.
- No insignificant whitespace.
- IEEE 754 number canonicalization per RFC 8785 §3.2.2.3.

Using the same algorithm and library as the audit chain means a compliance officer can:

1. Read `bundle.SnapshotHash`.
2. Look up the `bundle.create` audit event whose `FieldDiffJson` carries that hash.
3. Walk the chain back from there (or forward to verify `audit.tail` integrity) using the same canonical-JSON helper, with confidence that the bytes hashed match the bytes stored.

This linkage is load-bearing for the audit story and forbids divergent canonicalizers in the bundle path. When future work needs richer JSON (e.g. floating-point IEEE 754 corner cases), the extension goes in the shared `CanonicalJson` and both the audit chain and the bundle hasher inherit it.

### 4. Soft-delete only

`Bundle.State` is one of `Active` / `Deleted` (P8.1). `IBundleService.SoftDeleteAsync` (P8.2) flips the state and stamps `DeletedAt` + `DeletedBySubjectId`; the row itself is never removed. `AppDbContext.SaveChangesAsync` enforces this via an immutability sweep that rejects any non-state mutation on a tracked active bundle (P8.1's `EnforceBundleImmutability`).

Hard-delete is forbidden because:

- The audit chain (P6) carries a `bundle.create` event whose `EntityId` is the bundle id; a separate `bundle.delete` event references the same id. Removing the row would dangle both references.
- A `DelegationContract.bundleId` (filed for andy-tasks Epic U) that survives policy retirement would 404 if the bundle row vanished — defeating the reproducibility promise that bundle pinning *outlasts* live state.
- Storage cost is bounded: bundles are deliberate artefacts (no auto-pinning — see §6); the row count grows with explicit operator action, not with calendar time.

A future ADR may introduce cold-storage tiering for very-old bundles, but it would still preserve the row id; the bytes might move offline.

### 5. Endpoint topology and the pinning gate

Five surfaces (per the four-surface convention from CLAUDE.md plus the gRPC/CLI parity from P8.6):

| Surface | Implementation | Reference |
|---|---|---|
| REST | `BundlesController` (`POST /api/bundles`, `GET /api/bundles[/{id}[/resolve]/diff?to=]`) | P8.3 #83, P8.6 #86 |
| MCP | `BundleTools` static class with `[McpServerTool]` methods | P8.5 #85 |
| gRPC | `BundleGrpcService` with 6 RPCs | P8.6 #86 |
| CLI | `andy-policies-cli bundles {create,list,get,resolve,diff}` | P8.6 #86 |

Every read endpoint that asks *"what's in the catalog right now?"* — `/api/policies*`, `/api/bindings/resolve`, `/api/scopes/{id}/effective-policies` — is gated by `[RequiresBundlePin]` (P8.4 #84). When `andy.policies.bundleVersionPinning` is `true` (the manifest default), absence of `?bundleId=` returns a 400 Problem Details with `type = https://andy.local/problems/bundle-pin-required`. Consumers either pin or get an explicit "you must pin" rejection — silently serving live state would be a reproducibility regression.

### 6. No auto-pinning

Bundles are deliberate artefacts created by an authenticated actor with the `andy-policies:bundle:create` permission (P7 #56 catalog). The service does **not** auto-snapshot on every publish. Auto-snapshotting would:

- Inflate bundle count without operator intent.
- Pollute the audit chain with `bundle.create` events for every catalog change.
- Make pinning meaningless — consumers wouldn't know which bundle to pin.

The recommended cadence — pin a bundle once per consumer release — lives in the [consumer integration guide](../guides/consumer-integration-bundles.md).

### 7. RFC-6902 diff between bundles

`BundleDiffService` (P8.6 #86) emits a deterministic RFC-6902 JSON Patch between two bundles' snapshot bytes. JSON-Patch over JSON-Merge-Patch (RFC 7396) because:

- 6902 expresses precise array element addition / removal at indexed paths; merge-patch can't (`/Bindings/42` semantics matter for auditors greppping for a specific binding change).
- 6902 round-trips cleanly against `jsonb` storage.

Two invocations on the same `(fromId, toId)` pair produce byte-identical patch JSON (covered by `BundleDiffServiceTests` and `BundleGrpcServiceTests`). Diff is read-only by design — the gRPC permission map gates `DiffBundles` against `bundle:read`, not a separate write permission.

## Consequences

### Positive

- **Reproducibility honoured.** A pinned bundle answers identically across catalog churn. The integration test suite proves it via `ConcurrentPublishFreezeTests` (post-create publish doesn't leak in) and `HashDeterminismTests` (same bundle re-read returns the same hash).
- **Audit linkage.** Every bundle id is referenced by exactly two audit events (`bundle.create`, optional `bundle.delete`) carrying the snapshot hash; auditors can walk in either direction.
- **Cross-surface uniformity.** REST, MCP, gRPC, CLI all delegate to `IBundleService` / `IBundleResolver` / `IBundleDiffService`. A consumer that switches surfaces during integration sees identical wire shapes for the same operation.

### Negative / accepted trade-offs

- **Storage grows linearly with deliberate bundle count.** Each row carries the full snapshot bytes — typical 100-policy catalog ≈ 30–60 KB; 1000-policy catalog ≈ 300–600 KB. Cold-storage tiering is a future ADR.
- **No bundle-against-live diff.** The diff RPC compares two stored bundles; consumers wanting "bundle vs. now" must create a fresh bundle and diff against it. Adding a live-diff RPC would tempt consumers to use the service as a drift detector, which is Conductor's job.
- **Bridge-binding hierarchy walk deferred on snapshot effective-policies.** `IBundleResolver.ResolveEffectiveForScopeAsync` matches only `TargetType=ScopeNode` bindings keyed by `scope:{nodeId}`; `Repo` / `Tenant` / etc. bridges to scope-node refs are deferred. Consumers using bridge-typed bindings should keep `bundleVersionPinning=false` until the follow-up lands.

## Considered alternatives

| Alternative | Rejected because |
|---|---|
| **Lazy view (no materialization)**. Store only the bundle id + creation timestamp; resolve against live state on read | Two resolves at different times can return different results for the same bundle if live state changes between them. Defeats the entire reproducibility promise — the headline reason bundles exist. |
| **Row-level snapshot table** (normalized `BundleEntry` join rows, one row per `(bundleId, policyId)`) | Requires a range query + join on every resolve; the full-JSON snapshot answers any resolve with a single PK lookup + cached parse. The normalized variant trades a guaranteed-fast read for storage that's no smaller (still O(bundleCount × catalogSize)). |
| **SHA-3 instead of SHA-256** | Diverges from ADR 0006 (audit chain). Uniformity reduces cognitive load; a future collision-resistance concern can warrant SHA-3, but it goes in its own ADR with a migration plan for both surfaces. |
| **Merkle-tree hash of bundle contents** | Adds complexity without a concrete audit requirement today. A future "verify only one entry without fetching the whole bundle" use case could warrant it; revisit then. |
| **Hard-delete with cascade-block on referenced ids** | Trades a tiny storage win for an ongoing referential-integrity hazard: any process that reads an audit event must check whether the referenced bundle was hard-deleted, and the audit chain becomes lossy. Soft-delete is the only posture compatible with the audit-integrity rule from ADR 0006. |
| **Auto-pin a bundle on every publish** | Inflates bundle count without operator intent; pollutes the audit chain; makes "which bundle should I pin?" ambiguous for consumers. The deliberate-artefact posture is consistent with the rest of the catalog (drafts are explicit; publishes are explicit; bundles are explicit). |

## Future work

- **Cold-storage tiering** for very-old bundles. Same row id; bytes move out-of-band. Filed as a follow-up; no ETA.
- **Snapshot-backed effective-policies bridge resolution.** P8.4's deferred case — handle `Repo` / `Tenant` / `Org` / `Template` target types against the snapshot scope chain, mirroring the live `BindingResolutionService` more completely.
- **1000-policy nightly perf sweep** against a Postgres testcontainer enforcing the strict epic SLOs (500 ms create-p95, 50 ms resolve-p99). The 100-policy budgets in `BundlePerfTests` (P8.7) catch large regressions; the strict budgets need a dedicated runner.

---

**Authors**: drafted by Claude 2026-05-06 retroactively after P8.1–P8.7 shipped. Decisions above are not retroactively editable without a follow-up ADR; in particular, hard-delete and auto-pinning would each need their own ADR with a migration story for existing bundles and audit events.
