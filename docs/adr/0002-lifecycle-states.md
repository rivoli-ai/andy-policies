# ADR 0002 — Lifecycle states

## Status

**Accepted** — 2026-04-21. Gates: P2.1 (rivoli-ai/andy-policies#11), P2.2 (#12), P2.3 (#13), P2.4 (#14) may proceed after this ADR is merged. Phase 0 tracker: rivoli-ai/andy-policies#94.

Supersedes: nothing. Companion to: ADR 0001 policy-versioning (defines the `PolicyVersion` aggregate whose `State` column this ADR specifies), ADR 0006 audit-hash-chain (records every transition), ADR 0007 edit-rbac (transitions are RBAC-gated).

## Context

Epic P2 (rivoli-ai/andy-policies#2) introduces lifecycle state on `PolicyVersion`. A published version cannot be edited (per ADR 0001 §3) — corrections require a new version. But "published" itself has phases: a version starts as a draft, becomes the single active version for a policy when published, eventually gets superseded by a newer active version, and finally retires when it's no longer referenceable by new bindings.

Consumers care about these phases:
- **Conductor** pins `policyVersionId` on `DelegationContract` — must resolve even if the version has been superseded (the consumer pinned this specific version)
- **andy-tasks Epic AC** (per-task gates) filters bindings by `State != Retired` — retired versions don't produce new gates
- **andy-policies P3 Bindings** refuses `CreateAsync` against a Retired version (rivoli-ai/andy-policies#20); Active + WindingDown are OK
- **andy-policies P4 resolution** returns Active bindings; WindingDown are "still resolvable for legacy reads but new bindings can't target them"
- **andy-policies P6 audit** records every state transition with actor + rationale + hash-chained diff

Four decisions needed resolution before P2 code:

1. **State machine shape** — which states exist, which transitions are legal?
2. **Auto-supersede semantics** — does publishing v4 automatically WindingDown v3?
3. **Rationale enforcement** — required on every transition? Conditional on setting?
4. **Only-one-Active invariant enforcement** — DB-level (unique partial index) or service-level?

The Phase 0 review flagged two adjacent concerns this ADR addresses:

- **Enum name drift between P1.1 and P2.1** (`PolicyVersionState` vs `LifecycleState`) — fix-pass already normalised P1.1 to use `LifecycleState`; this ADR defines the canonical values.
- **Serializable-txn-on-SQLite "fallback" wording** in P2's original drafts — tightened here; SQLite uses `BEGIN IMMEDIATE` which is an exclusive-lock equivalent, not true SERIALIZABLE isolation.

## Decisions

### 1. Four-state machine

```
Draft ──publish──▶ Active ──supersede──▶ WindingDown ──retire──▶ Retired
                                                                    ▲
                                                 no transitions out
```

Transition matrix:

| From \ To    | Draft | Active | WindingDown | Retired |
|---|:---:|:---:|:---:|:---:|
| **Draft**    | —     | ✓ (publish) | ✗           | ✗       |
| **Active**   | ✗     | —     | ✓ (supersede — auto or manual) | ✓ (manual, rare) |
| **WindingDown** | ✗     | ✗     | —           | ✓ (retire) |
| **Retired**  | ✗     | ✗     | ✗           | —       |

`Active → Retired` is allowed but rare (emergency recall of a policy with no successor). In normal flow, published versions transit `Active → WindingDown → Retired`. The state machine enforcement lives in `LifecycleTransitionService.IsTransitionAllowed(from, to)`.

**Semantic definitions:**

- **Draft** — mutable per ADR 0001 §3. Not visible to consumer resolution; not bindable. No consumer commitments.
- **Active** — immutable. Exactly one per policy at a time. Bindable (P3); resolvable (P4); included in bundle snapshots (P8).
- **WindingDown** — immutable. Still resolvable for legacy reads (consumer pinned to this specific version keeps working); **P3 `BindingService.CreateAsync` refuses to bind new bindings to it**; P8 bundles created AFTER this transition do NOT include it.
- **Retired** — immutable. Resolution returns **410 Gone** unless `?include-retired=true` is passed on read endpoints. New bindings to Retired versions are refused (P3). Kept in DB for audit-chain integrity (never deleted; ADR 0006 non-goal).

Rejected:
- **Three states (Draft / Active / Retired)**: loses the WindingDown tier where legacy reads still work but new bindings refuse. WindingDown is a genuine consumer-facing semantic.
- **Five+ states** (adding e.g. Deprecated separate from WindingDown): no consumer asked for the distinction; YAGNI.
- **No explicit WindingDown — just auto-retire on supersede**: breaks consumer pins. A consumer who pinned v3 needs to keep resolving it even after v4 goes Active; immediate retire would return 410 to every pinned consumer on publish.

### 2. Auto-supersede on publish

**Publishing a new version automatically transitions the previous Active version to WindingDown, in the same transaction.** Atomically.

Sequence (inside a single serializable txn on Postgres / `BEGIN IMMEDIATE` on SQLite):

```
1. Load the target PolicyVersion (must be State = Draft).
2. Load the current Active version for the same PolicyId (may be null on first publish).
3. If a current Active exists:
     currentActive.State = WindingDown
     currentActive.SupersededByVersionId = target.Id
     currentActive.SupersededAt = now
4. target.State = Active
   target.PublishedAt = now
   target.PublishedBySubjectId = actor   (enforced !== target.ProposerSubjectId; see ADR 0007)
5. Emit in-process domain events: PolicyVersionPublished, PolicyVersionSuperseded (if applicable).
6. Commit.
```

The only-one-Active invariant (§4 below) makes step 3 → step 4 atomic; a concurrent publish attempt sees the updated state post-commit and fails the invariant check.

Rejected:
- **Two-step "publish then supersede"** (publish marks target Active; a separate transition later moves the old Active to WindingDown): leaves the DB with two Active rows for a policy — violates the invariant. Only-one-Active must hold across every commit boundary.
- **Manual-only supersede** (author must explicitly transition the old Active): error-prone; the "publishing means the old one is superseded" semantic is unambiguous.

### 3. Rationale enforcement

**Every transition requires a non-empty rationale when `andy.policies.rationaleRequired = true`** (setting registered in `config/registration.json`, default `true`).

- Empty, whitespace-only, or missing `rationale` → **HTTP 400** with `errorCode = "policy.rationale-required"` before any state mutation.
- Rationale is persisted on the `AuditEvent` (per ADR 0006), not on `PolicyVersion` itself. The `PublishedBySubjectId + PublishedAt` on the version row are the "who/when"; the audit chain holds the "why".
- When `rationaleRequired = false` (dev mode, escape hatch), transitions succeed with empty rationale; the audit event carries `rationale = null` (per ADR 0006's "always include as null" decision).

**Setting is hot-reloaded** — the middleware reads from `IOptionsMonitor<PoliciesSettings>` so a flip of the setting in andy-settings propagates within the Andy ecosystem's normal propagation window (~60s via Epic AL v2; immediate via direct andy-settings reload in v1).

Rejected:
- **Rationale always required, no setting toggle**: blocks dev/test workflows where bulk-seed scripts need to publish without interactive prompts.
- **Rationale stored on PolicyVersion**: duplicates audit data; edits to the version row (which are forbidden) would need to also carry rationale, confusing the immutability story.

### 4. Only-one-Active invariant: **DB-level partial unique index**

```sql
-- Postgres
CREATE UNIQUE INDEX ix_policy_versions_one_active_per_policy
  ON policy_versions (policy_id)
  WHERE state = 'Active';

-- SQLite (same syntax, supported since 3.8.0)
CREATE UNIQUE INDEX ix_policy_versions_one_active_per_policy
  ON policy_versions (policy_id)
  WHERE state = 'Active';
```

A concurrent publish attempt that would produce two Active rows for the same policy raises `23505` (Postgres) / `19` (SQLite — SQLITE_CONSTRAINT). The auto-supersede transaction (§2) includes the old-Active update BEFORE the new-Active write, so under contention the losing commit raises the constraint violation and the caller retries with refreshed state.

EF Core: `.HasIndex(v => v.PolicyId).HasFilter("state = 'Active'").IsUnique()` with a provider check — both providers support the same SQL shape, no branching needed.

Rejected:
- **Service-level check inside a read-then-write block**: TOCTOU race between the check and the `UPDATE` under concurrency. DB constraint is the authoritative guard.
- **Separate `ActivePolicyVersion` table** with foreign key: same result but adds a join on every read; partial index is strictly superior.

### 5. Transition endpoints

Each transition is a **dedicated `POST` endpoint** on `PoliciesController`, not a PATCH of `state`:

| Endpoint | Effect | RBAC permission (ADR 0007) |
|---|---|---|
| `POST /api/policies/{id}/versions/{vId}/publish` | Draft → Active (+ previous Active → WindingDown) | `andy-policies:policy:publish` |
| `POST /api/policies/{id}/versions/{vId}/winding-down` | Active → WindingDown (manual; rare — auto path goes via `publish`) | `andy-policies:policy:transition` |
| `POST /api/policies/{id}/versions/{vId}/retire` | WindingDown → Retired (or Active → Retired, emergency) | `andy-policies:policy:transition` |

All three take body `{ rationale: string }` and run through the same `LifecycleTransitionService.TransitionAsync(vId, targetState, rationale, actor, ct)` core, which:

1. Validates the transition against the matrix (§1)
2. Enforces rationale per §3
3. Enforces ADR 0007 self-approval invariant on publish
4. Opens serializable txn
5. Applies `UPDATE` + (for publish) the auto-supersede side-effect
6. Appends an ADR 0006 audit event
7. Commits and emits in-process domain events

Rejected:
- **Single `PATCH /state` with a target-state body field**: obscures the allowed transitions in the HTTP surface; harder to RBAC-gate per-transition.
- **Dedicated endpoints but without a shared service**: each controller method would duplicate the validation/audit/commit logic.

### 6. Domain events emitted per transition

```csharp
public sealed record PolicyVersionPublished(Guid PolicyId, Guid PolicyVersionId, int Version, string ActorSubjectId, DateTimeOffset At);
public sealed record PolicyVersionSuperseded(Guid PolicyId, Guid PolicyVersionId, Guid SupersededByVersionId, DateTimeOffset At);
public sealed record PolicyVersionRetired(Guid PolicyId, Guid PolicyVersionId, string ActorSubjectId, DateTimeOffset At);
```

**In-process only for v1** — dispatched via `MediatR` (already in the .NET 8 stack) *inside the same transaction* (not post-commit fire-and-forget — the Phase 0 review flagged fire-and-forget as risking audit row loss). Handlers that write audit events (P6) or update projections commit with the transition.

**v2 (future, blocked on Epic AL rivoli-ai/andy-rbac#11 + rivoli-ai/andy-tasks messaging)**: events also publish to NATS so Conductor and andy-tasks can react (e.g. invalidate caches, emit user notifications). Not scoped here; tracking issue TBD.

Rejected:
- **Fire-and-forget post-commit dispatch**: if the process dies between commit and dispatch, audit events go missing. Transactional outbox or in-txn handler is the only correct option.
- **Raw DB triggers** for event emission: complects domain logic with schema.

### 7. Concurrency model

**Postgres: `IsolationLevel.Serializable`** for every transition. On `40001` (serialization failure), the caller retries once with 50ms jitter backoff; second failure returns 409 with `errorCode = "policy.transition-conflict"` and the caller refreshes.

**SQLite: `BEGIN IMMEDIATE`** — acquires a reserved lock at txn start; concurrent writers serialise naturally. Not true SERIALIZABLE isolation (SQLite has one writer at a time by design), but functionally equivalent for our single-writer concurrency requirement. The Phase 0 review flagged the earlier draft's "serializable fallback" wording as misleading — this ADR clarifies that SQLite's single-writer model IS the serialisation, not an approximation.

Rejected:
- **Optimistic concurrency only** (no isolation level bump): the only-one-Active partial index catches violations but the error surface is confusing (23505 vs a clean 409); explicit SERIALIZABLE gives better control.
- **Application-level advisory lock**: adds a second locking mechanism atop the DB's own; more surface area to go wrong.

## Consequences

### Positive

- **Consumer pins stay resolvable.** WindingDown preserves legacy resolution while blocking new bindings — the core reason for having four states instead of three.
- **Only-one-Active is a DB invariant, not a service-level check** — no TOCTOU races under concurrency.
- **Auto-supersede is atomic** — no window where two Active versions coexist.
- **Rationale enforcement is gated by a setting** — dev bulk-seed scripts work without interactive prompts; production requires rationale.
- **Transitions are RBAC-gated per state change** (ADR 0007) — `publish` and `retire` can have different permission assignments.

### Negative / accepted trade-offs

- **Four states is more than strictly necessary** for services that don't care about WindingDown semantics — they see Active and Retired as the "real" states, WindingDown as a weird middle tier. Documented in the consumer integration guide.
- **Retired versions stay in DB forever** — storage grows linearly with versions created. Bounded by author workflow pace; ADR 0006 non-goal of truncation applies (can't safely drop rows that audit events reference).
- **SQLite single-writer serialisation limits throughput** under bulk-transition load. Acceptable — embedded mode is single-tenant single-instance; bulk transitions are a bulk-seed concern, not a steady-state one.
- **In-process domain events only in v1** — external systems (andy-tasks, Conductor) can't react automatically to a publish until Epic AL lands. Workaround: consumers poll the active-version endpoint. Documented.

### Follow-ups

- P2.1–P2.4 (rivoli-ai/andy-policies#11–#14) implementation proceeds with the state machine pinned.
- v2 event dispatch via NATS (when Epic AL lands) is out of scope here; file a story against P2 or a new epic at that time.
- `IOptionsMonitor<PoliciesSettings>` wiring for `rationaleRequired` hot-reload is a P2.4 implementation concern; no ADR change needed.

## Considered alternatives

| Alternative | Rejected because |
|---|---|
| **Three states (Draft / Active / Retired)** | Loses WindingDown semantics; consumer pins would 410 Gone immediately on publish |
| **Five+ states** (Deprecated / Archived / etc.) | No consumer asked for the distinction; YAGNI |
| **No auto-supersede — manual only** | Error-prone; two-Active window possible |
| **Auto-supersede in a separate transaction** | Two-Active window between publishes |
| **Rationale always required, no toggle** | Blocks dev bulk-seed scripts |
| **Rationale stored on PolicyVersion** | Duplicates audit; complicates immutability story |
| **Service-level only-one-Active check** | TOCTOU race |
| **Separate ActivePolicyVersion table** | Join-on-read cost; partial index is strictly superior |
| **PATCH `/state` single endpoint** | Obscures allowed transitions; harder to RBAC-gate per-transition |
| **Fire-and-forget domain events post-commit** | Event loss on crash → missing audit rows |
| **DB triggers for events** | Complects domain with schema |
| **Optimistic concurrency only, no SERIALIZABLE** | Confusing 23505 error surface |
| **Advisory lock instead of SERIALIZABLE** | Two locking mechanisms → more failure modes |

---

**Authors**: drafted by Claude 2026-04-21; accepted same day after Phase 0 review. Phase 0 tracker: rivoli-ai/andy-policies#94. Post-acceptance edits require a follow-up ADR — this ADR is load-bearing for ADR 0006's per-transition audit events and ADR 0007's per-transition RBAC gating.
