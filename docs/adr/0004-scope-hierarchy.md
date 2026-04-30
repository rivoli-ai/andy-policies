# ADR 0004 — Scope hierarchy + tighten-only resolution

## Status

**Accepted** — 2026-04-30. Closes Epic P4 (rivoli-ai/andy-policies#4).
The implementation stories (P4.1 #28, P4.2 #29, P4.3 #30, P4.4 #32,
P4.5 #33, P4.6 #34, P4.7 #36) shipped ahead of this ADR's acceptance
because the aggregate shape and resolution semantics were already pinned
in the per-story specs. This ADR captures the decisions authoritatively
for downstream readers (Conductor's ActionBus, andy-tasks per-task
gates, andy-mcp-gateway tool policy).

Supersedes: nothing. Companion to:

- ADR 0001 — Policy versioning (defines the `PolicyVersion` aggregate
  the bindings + resolver join against).
- ADR 0002 — Lifecycle states (the Retired state the resolver
  surfaces transparently — see §6 below for the deliberate
  divergence from P3.4's exact-match behaviour).
- ADR 0003 — Bindings are content-only metadata (the binding rows
  the chain walker gathers on every resolve).
- ADR 0006 — Audit hash chain (records every scope mutation; the
  no-op hooks in P4.2 are pre-wired).
- ADR 0007 — Edit RBAC (per-mutation permission gates that wrap
  scope writes; lands with P7).

## Context

Epic P4 introduces the hierarchical scope graph that consumers — Conductor,
andy-tasks, andy-mcp-gateway — use to ask "which policies apply to this
target?" with structural awareness, not just byte-exact match. The model
has to balance four concerns:

1. **Determinism.** The same input must produce the same effective
   policy set across reads, across consumers, across catalog snapshots.
   Consumers that pin a `policyVersionId` for reproducibility need a
   resolution path that doesn't drift under concurrent mutation.
2. **Stricter-tightens-only.** A `Mandatory` binding at an Org cannot
   be downgraded to `Recommended` at a Team — that's the P4 epic's
   headline rule. Consumers expect ancestor mandates to propagate
   downward; relaxation requires an explicit Override (Epic P5).
3. **Surface parity.** REST, MCP, gRPC, and CLI must all return the
   same answer for the same logical question. The tighten-only fold
   lives in `IBindingResolutionService`; surfaces are thin wire
   adapters.
4. **Read performance.** A 6-level walk + dedup must complete in
   < 50 ms (p99) on production-sized fixtures. The chosen storage
   shape needs to support indexed prefix scans across both Postgres
   and SQLite (the embedded mode targets P10).

Three drift risks this ADR addresses, flagged during P4 implementation:

- **Cycle prevention.** Early designs allowed re-parenting; rejected
  because cycle prevention then becomes a runtime check on every
  mutation. The chosen model treats `ParentId` as immutable post-
  insert, so cycles are impossible by construction (§3 below).
- **Tighten-only at write vs read time.** P4.3 enforces the rule
  silently at read; P4.4 enforces it at write. Both are needed —
  read-time keeps stale violators from poisoning consumers; write-
  time prevents the catalog from accumulating unreachable rows that
  confuse audit logs and the Cockpit tree view.
- **Delete is NOT a tighten-only vector.** The reviewer-flagged
  reconciliation in P4.4: a delete cannot produce a weaker downstream
  binding (it can only remove one). The "team admin deletes a team
  Mandatory" scenario the epic body originally described is a P7
  governance-role concern, not a P4 invariant.

## Decisions

### 1. Typed 6-level hierarchy: `Org → Tenant → Team → Repo → Template → Run`

```
Org ──▶ Tenant ──▶ Team ──▶ Repo ──▶ Template ──▶ Run
0       1         2        3        4            5
```

`ScopeType` is a closed enum with ordinals 0..5; the ordinal doubles as
the canonical depth. Service-layer invariant: `(int)Type == Depth`.
The ladder is enforced at create time — child `Type` must equal
`parent.Type + 1`; root must be `Org`. Mismatches return
`InvalidScopeTypeException` (HTTP 400, `errorCode = "scope.parent-type-mismatch"`).

Rejected:

- **Untyped tree.** Loses the "at what level does this binding apply?"
  semantic that consumers depend on. Conductor's story-admission flow
  asks "is this policy bound at `Team` or higher?" — that question
  needs typed nodes.
- **Five or seven levels.** Five collapses Repo + Template, which
  matters for andy-tasks (a Repo is a deployable, a Template is a
  reusable pipeline definition; both bind policies independently).
  Seven adds a level no consumer asked for; YAGNI.
- **Allow re-parenting.** Adds runtime cycle-detection cost and
  changes resolution semantics under in-flight reads. Rejected; subtree
  lift/shift is a P5 Override concern (escape hatch with approver +
  expiry).

### 2. Storage: materialized path (not closure table, not adjacency-only)

Each row stores `MaterializedPath = "/{rootId}/.../{selfId}"`. Descendant
queries become indexed `LIKE '/root/%'` prefix scans on both Postgres and
SQLite; ancestor queries parse the path tail-to-head and load the IDs in
a single `WHERE Id IN (...)` round-trip. `Depth` is denormalised for
constant-time level checks.

The path is built on insert by `ScopeService.CreateAsync`:

```
parent.MaterializedPath + "/" + selfId   (when ParentId != null)
"/" + selfId                              (root Org)
```

`ParentId` is treated as immutable post-insert, so the path never needs
recomputation. Re-parenting is out of scope for Epic P4.

Rejected:

- **Closure table** (`scope_closure(ancestor, descendant, depth)`).
  Richer ancestor queries via join, but maintenance on insert is
  O(depth) writes for no practical gain at our cardinality. Materialized
  path is simpler at our depth budget (max 6).
- **Adjacency-list-only** (just `ParentId`, walked via recursive CTE
  on every read). Postgres-only ergonomics; SQLite's `WITH RECURSIVE`
  has prepared-statement-caching quirks that would force provider-
  specific code paths. Materialized path is provider-agnostic.
- **Postgres `ltree`.** Elegant but unavailable on SQLite; embedded
  mode (P10) needs the same query path.

### 3. Cycle prevention is structural, not runtime

`ParentId` is set on `CreateAsync` and never mutated afterwards.
`UpdateAsync` only takes `DisplayName`; the request DTO doesn't expose
`ParentId`. With this constraint:

- A new node's `ParentId` must reference a row that already exists
  (FK Restrict on `scope_nodes.ParentId`).
- Therefore, at the moment of insertion, the new node cannot be its
  own ancestor — its parent (and all parent-ancestors) were committed
  before it.
- Therefore, no cycle can ever exist.

`ScopeCycleRejectionTests` (P4.7) document this contract and guard
against future record-shape drift that would re-introduce a re-parent
path.

Rejected:

- **Runtime cycle check on every write.** Defensive but unnecessary
  given the immutability guarantee. Adds query cost without changing
  behaviour.
- **DB trigger that recomputes path.** Couples domain logic with
  schema; unavailable on SQLite without provider-specific SQL.

### 4. Tighten-only is enforced at *both* read and write

**Read time** (`IBindingResolutionService.ResolveForScopeAsync`, P4.3).
Walks the chain root-to-leaf, gathers every binding that targets a node
in the chain (or the chain's external `Ref` via the bridge to P3 non-
scope bindings), and folds with stricter-tightens-only:

> For each `PolicyId` seen, the deepest `Mandatory` binding wins.
> If no `Mandatory`, the deepest `Recommended` binding wins.
> Tiebreak: earliest `CreatedAt`.

A descendant `Recommended` binding shadowed by an ancestor `Mandatory`
is silently dropped — consumers never see the would-be downgrade.

**Write time** (`ITightenOnlyValidator.ValidateCreateAsync`, P4.4).
Refuses to commit a `Recommended` binding whose `PolicyId` is bound
`Mandatory` at any ancestor scope. Returns 409 with
`errorCode = "binding.tighten-only-violation"` and the offending
ancestor binding id + scope node id so admins can triage from the
error response.

**Both checks are required.** Read alone leaves stale violators in the
catalog (confusing audit logs and the Cockpit tree). Write alone
doesn't protect against bindings authored before the tighten-only
check existed (legacy data) or against bindings where the ancestor
binding lands after the descendant. Read keeps consumers honest;
write keeps the catalog clean.

**Delete is NOT a tighten-only vector** (P4.4 §reviewer-flagged
reconciliation). Tighten-only is a CREATE-time invariant only — a
delete cannot produce a weaker downstream binding. The
`ITightenOnlyValidator.ValidateDeleteAsync` hook returns null today and
is retained for P5 / P6 to layer side-effect checks later.

Rejected:

- **Read-time only enforcement.** Stale violators accumulate; admins
  who try to write a Recommended binding that's silently shadowed
  get no feedback that something's wrong.
- **Write-time only enforcement.** Doesn't protect against legacy
  data or race conditions where the ancestor binding commits after
  the descendant.

### 5. Resolution returns Retired versions; consumers handle deprecation

This deliberately diverges from P3.4 exact-match behaviour, which
filters Retired versions out of the resolve response. The chain walker
surfaces the entire policy story — what bindings exist, what their
strength is, what version they pin — and lets consumers decide.

Rationale: a Retired version that's still bound at `Org` is
semantically meaningful — it tells Conductor "this policy *was* in
force here; the deprecation needs an explicit Override or the binding
needs deletion." Filtering it would hide the audit-relevant signal.

Single-target reads (P3.4) filter Retired because the consumer's
question is "what applies right now?"; chain reads (P4.3) don't filter
because the consumer's question is "what's the full policy story for
this target?". Both are surface-stable contracts.

Rejected:

- **Filter Retired to match P3.4.** Would hide audit-relevant rows.
  P5 Overrides will need to surface "this Override targets a Retired
  policy" explicitly; pre-filtering would break that flow.

### 6. Ladder violation maps to 400 (not 409)

`InvalidScopeTypeException` returns HTTP 400 with `errorCode =
"scope.parent-type-mismatch"`. The proposed write violates the
canonical type ladder, which is a *request shape* error — the client
can fix it by passing the correct `Type`. Distinct from
`ScopeRefConflictException` (409, ref already exists — concurrency
conflict) and `ScopeHasDescendantsException` (409, cannot delete a
non-leaf — server state conflict).

Rejected:

- **All scope errors as 409.** Conflates "client sent wrong type"
  with "server state prevents the action"; harder for clients to
  distinguish retryable from non-retryable.

### 7. Same service layer powers REST, MCP, gRPC, CLI

`IScopeService` and `IBindingResolutionService` are the single source
of truth. Every controller, MCP tool, gRPC service, and CLI command
delegates here — no business logic anywhere outside. Surface drift is
caught by `ScopeToolsTests` + `ScopesGrpcServiceTests` +
`CliScopesEndToEndTests` (P4.6) running the same logical request
through each surface.

The CLI is a thin REST client; its parity is implied by the REST
assertion (mirrors the rationale established in
`CrossSurfaceParityTests` from P1).

Rejected:

- **Duplicate validation across surfaces.** Surface drift opportunity;
  the parity tests are the prevention.

## Consequences

### Positive

- **Deterministic resolution.** Same input → same output across reads,
  consumers, and snapshots. The tighten-only fold + (Mandatory >
  Recommended, deepest, earliest `CreatedAt`) priority order is total.
- **No cycles, ever.** Structural impossibility removes a class of
  runtime errors and lets the materialized-path indexing stay
  invariant-free.
- **Provider-agnostic.** Materialized-path `LIKE` works identically on
  Postgres and SQLite; embedded mode (P10) gets the same resolution
  semantics.
- **Performance budget met.** `ScopeWalkPerfTests` (P4.7) demonstrates
  p99 < 50ms on a 6-level chain with 200+ bindings.
- **Surface parity is a testing invariant.** The cross-surface tests
  fail when surfaces drift, so the rule is enforced rather than
  aspirational.

### Negative / accepted trade-offs

- **No re-parenting.** Subtree lift/shift requires deleting and
  recreating the affected nodes; bindings against the old ids are
  orphaned. Rationale: the simpler invariant (cycles impossible) is
  worth the operational cost. Consumers that need re-parenting use
  Overrides (P5) to point a new scope at a different policy version.
- **Dual enforcement (read + write) means two code paths.** Slightly
  more cost on write; consumers benefit from the cleaner catalog. The
  cost is bounded — write-time validation is one chain walk per
  create, sub-millisecond on production scale.
- **Resolution returns Retired versions.** Consumers must filter or
  surface deprecated rows themselves; this is by design (§5) but
  introduces a small interpretation burden.

### Follow-ups

- ADR 0005 — Overrides (Epic P5) introduces the `Mandatory:Forbid`
  shape that lets a leaf scope explicitly remove an inherited
  Mandatory (with approver + expiry); the only escape from
  tighten-only.
- P7 (Edit RBAC) gates scope mutations per role — `andy-policies:scope:write`,
  `andy-policies:scope:delete` scoped to a `ScopeNode` resource
  instance. The "team admin can't delete a team Mandatory imposed
  from above" governance concern lives there.
- P8 (Bundle pinning) snapshots the effective policy set per scope
  for reproducibility under the policy-catalog evolution.

## Considered alternatives

| Alternative                                                         | Rejected because |
|---------------------------------------------------------------------|------------------|
| Untyped tree                                                        | Loses "at what level does this bind?" semantic |
| Five-level hierarchy (collapse Repo + Template)                     | Repo and Template bind independently in andy-tasks |
| Allow re-parenting                                                  | Adds runtime cycle detection; subtree shifts use P5 Overrides |
| Closure table                                                       | O(depth) maintenance writes for no practical gain |
| Adjacency list + recursive CTE                                      | Postgres-only ergonomics; SQLite has CTE quirks |
| Postgres `ltree`                                                    | Unavailable on SQLite (embedded mode P10) |
| Runtime cycle check on every write                                  | Unnecessary given immutability guarantee |
| DB trigger for path maintenance                                     | Couples domain with schema; SQLite-incompatible |
| Read-time only tighten-only                                         | Stale violators accumulate in catalog |
| Write-time only tighten-only                                        | Doesn't protect against legacy data + races |
| Tighten-only on delete                                              | Delete cannot produce a weaker downstream binding |
| Filter Retired in chain resolve (parity with P3.4)                  | Would hide audit-relevant rows |
| All scope errors as 409                                             | Conflates request-shape errors with state conflicts |
| Duplicate validation across surfaces "just in case"                 | Surface drift opportunity; parity tests are the prevention |

---

**Authors**: drafted by Claude during P4.8 implementation. Post-acceptance
edits require a follow-up ADR — this ADR is load-bearing for ADR 0005
(Overrides escape hatch) and ADR 0007 (Edit RBAC permission set).
