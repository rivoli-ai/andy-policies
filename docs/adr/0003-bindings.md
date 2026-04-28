# ADR 0003 — Bindings are content-only metadata

## Status

**Accepted** — 2026-04-28. Closes Epic P3 (rivoli-ai/andy-policies#3).
The implementation stories (P3.1 #19, P3.2 #20, P3.3 #21, P3.4 #22, P3.5
#23, P3.6 #24, P3.7 #25, P3.8 #26) shipped ahead of this ADR's
acceptance because the aggregate shape and `targetRef` contract were
already pinned in the per-story specs. This ADR captures the decisions
authoritatively for downstream readers.

Supersedes: nothing. Companion to:

- ADR 0001 — Policy versioning (defines the `PolicyVersion` aggregate
  the binding's FK points at).
- ADR 0002 — Lifecycle states (the retired-version refusal cited in
  Decision 5 lives there).
- ADR 0006 — Audit hash chain (records every binding mutation;
  `IAuditWriter` call sites in `BindingService` from P3.2 are
  pre-wired for the real implementation).
- ADR 0007 — Edit RBAC (per-mutation permission gates that wrap binding
  writes; lands with P7).

## Context

Epic P3 (rivoli-ai/andy-policies#3) introduces the third
governance-catalog entity: a metadata link between an immutable
`PolicyVersion` and a foreign target (template, repo, scope node,
tenant, org). Consumers — Conductor's ActionBus, andy-tasks per-task
gates, andy-mcp-gateway tool policy — need to ask "which policies apply
to this target?" and get back a stable, reproducible answer.

The binding model has to balance four concerns:

1. **Cross-service consistency** must be the consumer's contract, not
   andy-policies'. We don't validate that a `template:{guid}` actually
   exists in andy-tasks; that's not our service boundary.
2. **Audit completeness.** Every mutation is captured by Epic P6's
   hash-chained audit; that requires the binding row to survive delete
   so the audit chain can refer back to it.
3. **Lifecycle correctness.** A binding to a Retired version is
   semantically invalid (the policy is no longer in force) and must
   never appear in resolve responses.
4. **Surface parity.** REST, MCP, gRPC, and CLI must all return the
   same answer for the same logical question — no surface gets to add
   business logic.

Three drift risks this ADR addresses, flagged during P3 implementation:

- **`targetRef` validation creep.** Early P3.2 drafts considered
  validating `targetRef` against a strict regex; rejected because the
  shapes are consumer-defined and would couple this service to the
  shape of every consumer.
- **Hard delete vs soft delete.** Hard delete simplifies queries by
  removing the row entirely but breaks the audit chain (P6 can't refer
  to a row that no longer exists). Soft delete with a `DeletedAt`
  tombstone is the only option that preserves audit semantics.
- **Three-valued bind strength** (Forbidden / Mandatory /
  Recommended). Rejected because "forbid" is an override concern (Epic
  P5), not a baseline binding semantic. A Recommended binding combined
  with a P5 Override of strength `Mandatory:Forbid` is the documented
  way to express "this policy must NOT apply here."

## Decisions

### 1. A `Binding` is a typed metadata row linking a `PolicyVersion` to a target reference.

```
Binding(
  Id, PolicyVersionId, TargetType, TargetRef,
  BindStrength, CreatedAt, CreatedBySubjectId,
  DeletedAt?, DeletedBySubjectId?
)
```

`TargetType` is a closed enum of five values
(`Template / Repo / ScopeNode / Tenant / Org`); `BindStrength` is a
closed enum of two values (`Mandatory / Recommended`). Both ordinals
are persisted via `HasConversion<int>` and are load-bearing on disk —
renaming an enum member is safe, reordering is not.

The aggregate intentionally has no compound uniqueness across
`(PolicyVersionId, TargetType, TargetRef)` — the same target can
intentionally bind to multiple versions of the same policy during
rollout. Duplicate-active detection is a consumer concern.

Rejected:
- **Embed `targetRef` as a typed object per `TargetType`** (e.g., a
  `RepoRef { org, name }` discriminated union). Adds proto/JSON
  serialization complexity for no benefit; the canonical string shapes
  documented in the design doc are easier for cross-language consumers
  to produce and parse.
- **Inline the target on `PolicyVersion`** (bindings as a JSON column).
  Loses the index path on `(TargetType, TargetRef)` and prevents P4
  hierarchy walks from joining on the binding row.

### 2. andy-policies does not validate `targetRef` against foreign systems.

`BindingService.CreateAsync` validates only that `targetRef` is
non-empty (after trim) and ≤512 chars. Cross-service consistency of
the target is the consumer's contract.

The canonical shapes per `TargetType` are documented in the design
doc but **not enforced** by this service:

- `template:{guid}` (andy-tasks template id)
- `repo:{org}/{name}` (GitHub-style slug)
- `scope:{guid}` (`ScopeNode.Id`, P4)
- `tenant:{guid}`, `org:{guid}`

Consumers can validate as strictly as they want before calling create;
this service treats `targetRef` as opaque.

Rejected:
- **Synchronous resolution against andy-tasks / andy-issues on every
  create.** Service-boundary firewall break — andy-policies would
  develop a hard dependency on services it has no operational control
  over, and a transient andy-tasks outage would cascade into binding
  failures here. The operational fan-in is a non-goal.
- **Validation regex per `TargetType`.** Pushes the consumer's shape
  decisions into our service contract. If andy-tasks changes its
  template id format, our regex would either reject valid bindings or
  drift behind. Consumer-side validation is the right boundary.

### 3. Resolution is exact-match in P3.

`GET /api/bindings/resolve?targetType=&targetRef=` returns the
deduplicated set of `(PolicyVersion, BindStrength)` pairs whose
binding row has byte-exact `(TargetType, TargetRef)` match. No prefix,
no case-folding, no hierarchy walk.

The endpoint is shipped this epic so consumers have a stable URL.
Hierarchical walk lands in P4 behind a `?mode=hierarchy` flag on the
same endpoint, so consumers written against exact-match resolve do
not have to move when P4 ships.

Rejected:
- **Combine exact + hierarchy in one mode at launch.** Hierarchy
  semantics are non-trivial (stricter-tightens-only resolution, cycle
  prevention, walk-up traversal) and gate further design decisions
  that aren't ready in P3. Shipping exact-match first lets consumers
  integrate now.

### 4. Bind-strength has two values: `Mandatory` and `Recommended`.

- **Mandatory** — the binding's policy *must* apply; consumers reject
  runs that violate it.
- **Recommended** — the binding's policy applies as guidance;
  consumers warn but do not block.

When two bindings on the same target reference the same `PolicyVersion`,
`Mandatory` wins the dedup over `Recommended` (tiebreak earliest
`CreatedAt`). The administrative duplicate this guards against ("oops,
two bindings for the same target and version") would otherwise show up
to consumers as redundant rows.

Rejected:
- **Forbidden as a third value.** Forbid semantics are an override
  concern (Epic P5). A Recommended binding paired with a P5 override
  of strength `Mandatory:Forbid` expresses "must NOT apply here"
  cleanly, with the override carrying its own approval workflow and
  expiry. Three-valued bind-strength would conflate "applicability"
  with "exclusion."
- **Numeric priority** (e.g., 0–10). Encourages bikeshedding over
  fairly arbitrary numbers and introduces a comparator dimension that
  consumers don't actually need. The two-value enum is sufficient.

### 5. Retired versions cannot be bound; existing bindings to Retired versions are filtered out of resolve.

`BindingService.CreateAsync` refuses bindings whose target version is
in `LifecycleState.Retired` and throws
`BindingRetiredVersionException` (HTTP 409, gRPC `FailedPrecondition`,
MCP `policy.binding.retired_target`). `Active` and `WindingDown` both
accept new bindings — `WindingDown` lets consumers author bindings
during a sunset window.

`BindingResolver.ResolveExactAsync` filters out bindings whose target
version is currently `Retired`, even if the binding row was created
when the version was still `Active`. Consumers never see retired
versions in resolve responses.

The retired-version guard runs inside the create's serializable
transaction. A concurrent retire can race a concurrent create — one of
two outcomes: (a) the retire commits first, the create observes
`State == Retired` and throws; (b) the create commits first, the row
is alive against the newly-Retired version but is filtered from
subsequent resolves. Consumers see the same end state either way.

Rejected:
- **Delete bindings on retire.** Breaks the audit chain (P6); a
  forensic "what bindings existed for this version when it was
  Retired?" query becomes impossible.
- **CHECK constraint via DB trigger.** Couples two domain concerns
  (lifecycle and binding) at the DB layer; the service-layer guard is
  testable in isolation and colocates with the invariant's owner.

### 6. Bindings use soft-delete to preserve the audit chain.

`DeleteAsync` stamps `DeletedAt = now` and `DeletedBySubjectId = actor`
rather than removing the row. Already-tombstoned bindings are treated
as not-found for both read and delete (a second delete returns 404).

Read endpoints filter `DeletedAt IS NULL` by default. The
version-rooted enumeration (`/api/policies/{id}/versions/{vid}/bindings`)
opts in via `?includeDeleted=true` for forensic queries.

Rejected:
- **Hard delete + audit-only preservation.** P6's audit chain would
  hold the deleted row's content but the binding's stable id would no
  longer resolve from any read endpoint. Investigators would have to
  cross-reference via the audit log, which is a worse UX than
  preserving the row and stamping a tombstone.

### 7. The same service layer powers REST, MCP, gRPC, CLI surfaces.

`IBindingService` and `IBindingResolver` are the single source of
truth. Every controller, MCP tool, gRPC service, and CLI command
delegates here — there is no business logic anywhere outside
`BindingService` and `BindingResolver`. Surface drift is caught by
`BindingCrossSurfaceParityTests` (P3.8), which drives the same logical
request through REST, MCP, and gRPC and asserts the response set is
identical.

The CLI (P3.7) is a thin REST client; its parity is implied by the
REST assertion, mirroring the rationale established in
`CrossSurfaceParityTests` from P1.

Rejected:
- **Duplicate validation across surfaces.** Any "let me re-check this
  invariant on the gRPC layer just in case" pattern violates the rule
  and creates drift opportunities. The cross-surface parity test
  catches the obvious cases; the rule itself is the prevention.

## Consequences

### Positive

- **Consumers own target semantics.** A consumer can choose to validate
  `targetRef` strictly or loosely; this service does not constrain
  them. New target types can be added without breaking existing
  consumers (additive enum extension).
- **Audit completeness.** Soft-delete + retired-version filter together
  mean every mutation is recoverable from the audit chain (P6) and no
  retired version "haunts" downstream evaluations.
- **Surface parity is a testing invariant, not a coding intent.** The
  cross-surface parity test fails when surfaces drift, so the rule is
  enforced rather than aspirational.
- **Forward compatibility with P4.** The exact-match resolve URL stays
  stable; hierarchy walk is additive behind a query flag.

### Negative / accepted trade-offs

- **No referential integrity across services.** A binding can point at
  a `template:{guid}` that no longer exists in andy-tasks. Consumers
  must handle stale refs themselves (treat as "no policy applies" or
  surface a UI warning).
- **Soft-delete grows the table monotonically.** Storage scales with
  binding-mutation volume, not active binding count. Bounded by author
  workflow pace; ADR 0006's non-goal of audit truncation applies.
- **Two-valued bind-strength is coarse.** Consumers that want finer
  gradients have to encode the nuance in the policy rule body
  (`PolicyVersion.RulesJson`), not the binding. Acceptable because the
  binding model stays simple and the rule body is the right home for
  policy-specific gradients.

### Follow-ups

- ADR 0004 — Scope hierarchy + tighten-only resolution (Epic P4) extends
  the resolver with `?mode=hierarchy` semantics; this ADR's exact-match
  contract stays the default.
- ADR 0005 — Overrides (Epic P5) introduces the `Mandatory:Forbid`
  shape that pairs with `Recommended` binding to express exclusion.
- ADR 0006 — Audit hash chain replaces the no-op `IAuditWriter` with
  the real implementation; the call sites added in P3.2 are
  pre-wired.

## Considered alternatives

| Alternative                                                           | Rejected because |
|-----------------------------------------------------------------------|------------------|
| Embed `targetRef` as a typed discriminated union per `TargetType`     | Cross-language serialization complexity; canonical string shapes are easier |
| Inline bindings as a JSON column on `PolicyVersion`                   | Loses the `(TargetType, TargetRef)` index; prevents P4 join semantics |
| Synchronous resolution against foreign systems on create              | Service-boundary firewall break; transient outage cascades |
| Validation regex per `TargetType`                                     | Pushes consumer shape decisions into our service contract |
| Hierarchy walk in P3                                                  | Non-trivial design (stricter-tightens-only, cycle prevention) blocks per-story progress |
| Three-valued bind-strength (Forbidden / Mandatory / Recommended)      | Forbid is an override concern (P5); conflates applicability with exclusion |
| Numeric priority for bind-strength                                    | Encourages bikeshedding; consumers don't need a comparator |
| Hard-delete bindings                                                  | Breaks audit chain |
| Delete bindings on retire                                             | Breaks the "bindings to Retired version" forensic query |
| CHECK constraint trigger for retired-version refusal                  | Couples lifecycle and binding at the DB layer |
| Hard delete + audit-only preservation                                 | Stable id no longer resolves from read endpoints |
| Duplicate validation across surfaces "just in case"                   | Surface drift opportunity; the parity test is the prevention |

---

**Authors**: drafted by Claude during P3.9 implementation. Post-acceptance edits require a follow-up ADR — this ADR is load-bearing for ADR 0004's hierarchy walk semantics and ADR 0006's audit-row mapping.
