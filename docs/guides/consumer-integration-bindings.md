# Consumer Integration: Bindings

A practical guide for services that need to ask **"which policies apply
to this target?"** and act on the answer. Targeted at: Conductor's
ActionBus, andy-tasks per-task gates, andy-mcp-gateway tool policy, and
any future consumer of andy-policies bindings.

For the *what* and *why* — entity shape, design rules, surface parity —
start with [Bindings (design)](../design/bindings.md) and
[ADR 0003 — Bindings are content-only metadata](../adr/0003-bindings.md).
This guide assumes you've read those.

> **Scope reminder.** andy-policies stores *what* policies bind to *what*
> targets. Whether a run violates a policy is a consumer concern —
> Conductor's ActionBus evaluates the rule body, andy-tasks gates the
> task, etc. This service is the catalog, not the enforcer.

## 1. Build a canonical `targetRef`

Each `TargetType` has a canonical shape. andy-policies does not enforce
the shape (per ADR 0003 §2), but stick to the table below so P4
hierarchy walks and P8 bundle resolves can join on the same key.

| `TargetType` | Canonical `targetRef`        | Example                                         |
|--------------|------------------------------|-------------------------------------------------|
| `Template`   | `template:{guid}`            | `template:3f2e5c1a-1010-4d6c-9d83-001e842b9013` |
| `Repo`       | `repo:{org}/{name}`          | `repo:rivoli-ai/andy-policies`                  |
| `ScopeNode`  | `scope:{guid}`               | `scope:b87...` (the `ScopeNode.Id` from P4)     |
| `Tenant`     | `tenant:{guid}`              | `tenant:00000000-0000-0000-0000-000000000001`   |
| `Org`        | `org:{guid}`                 | `org:00000000-0000-0000-0000-000000000001`      |

For `ScopeNode`, P4.2 will expose path → id resolution. Until then,
consumers that want to bind to a path resolve the path to its
`ScopeNode.Id` first via `IScopeService` (when P4 lands), then build
`scope:{id}`.

Service-side validation: non-empty (after trim), ≤ 512 chars. Anything
else passes — the consumer is responsible for shape correctness.

## 2. Resolve before deciding

Always call `resolve` to get the consumer-ready set of `(PolicyVersion,
BindStrength)` tuples for a target. The endpoint joins `Binding +
PolicyVersion + Policy` and returns a deduplicated, ordered response.

### REST

```bash
curl -H "Authorization: Bearer $TOKEN" \
  "$API/api/bindings/resolve?targetType=Template&targetRef=template:abc"
```

```json
{
  "targetType": "Template",
  "targetRef": "template:abc",
  "count": 1,
  "bindings": [
    {
      "bindingId": "9d28...",
      "policyId": "55ab...",
      "policyName": "write-branch-only",
      "policyVersionId": "0a17...",
      "versionNumber": 3,
      "versionState": "Active",
      "enforcement": "MUST",
      "severity": "critical",
      "scopes": ["prod"],
      "bindStrength": "Mandatory"
    }
  ]
}
```

### MCP

```text
policy.binding.resolve targetType=Template targetRef=template:abc
```

The tool returns the same JSON envelope (web casing, string enums) so
agents can pipe through deterministic parsers.

### gRPC

```bash
grpcurl -plaintext -d '{
  "target_type": "TARGET_TYPE_TEMPLATE",
  "target_ref":  "template:abc"
}' $API andy_policies.BindingService/ResolveBindings
```

### CLI

```bash
andy-policies-cli bindings resolve \
  --target-type Template \
  --target-ref template:abc \
  --output json
```

All four surfaces return identical results for the same target; the
parity is verified by `BindingCrossSurfaceParityTests` (P3.8).

## 3. Interpret `bindStrength`

| Value          | Consumer behaviour                                                           |
|----------------|------------------------------------------------------------------------------|
| `Mandatory`    | The policy *must* apply. Reject runs that violate it; surface as an error.  |
| `Recommended`  | The policy applies as guidance. Warn / annotate UI; do not block.           |

Same-target/same-version duplicates dedup with `Mandatory > Recommended`
(tiebreak earliest `CreatedAt`), so consumers see at most one row per
`(target, policyVersion)`. If you need finer gradients, encode them in
the policy rule body — that's not the binding's job (ADR 0003 §4).

## 4. Handle zero results

A target with no live bindings returns `200 OK` with `count: 0` and an
empty `bindings` array — never `404`. Treat this as **"no policy
constraints"**.

andy-policies is fail-open for this resolution: zero bindings means the
catalog has nothing to say about the target. Consumers can choose to
fail-closed at their own layer (e.g., "no binding = reject the run")
but that's an enforcement decision, not something the catalog encodes.

## 5. Cache locally

The resolve response is safe to cache by `(targetType, targetRef)` for
short-to-medium TTLs. Two invalidation hints:

- **Active version flips.** When a publisher promotes a new
  `PolicyVersion` to `Active`, the prior version transitions to
  `WindingDown` in the same DB transaction (Epic P2 lifecycle). The
  resolve response will contain the new `policyVersionId` next time
  you ask. If your TTL is longer than your tolerance for stale rule
  bodies, listen for the `PolicyVersionPublished` /
  `PolicyVersionSuperseded` events (today in-process; v2 over NATS via
  Epic AL).
- **Bundle pinning.** Epic P8 introduces immutable bundle snapshots —
  consumers that need reproducible decisions across cache windows can
  pin a bundle id and resolve through `?bundleId=…` instead. Until P8,
  use a TTL ≤ your acceptable staleness window.

Recommended TTL: **30–120s** for steady-state; **invalidate on event**
when available.

## 6. Edge cases

### A binding's target version transitions to Retired

The row stays in the DB, but `resolve` filters it out (Epic P2 §retire
+ ADR 0003 §5). Consumers see the binding disappear from resolve
responses; no client-side action needed.

If you cached the resolve response, the cache will refresh on TTL
expiry. If you need to react immediately, listen for the
`PolicyVersionRetired` domain event.

### A binding is soft-deleted

`DeleteAsync` stamps `DeletedAt` rather than removing the row.
Tombstoned bindings:

- Are visible via `GET /api/bindings/{id}` (forensic / audit) — you
  can inspect `deletedAt` and `deletedBySubjectId`.
- Are invisible in the default `GET /api/bindings?...` and in
  `resolve`.
- Are visible in the version-rooted enumeration with
  `?includeDeleted=true`.

A tombstoned binding's id never resurrects. Creating a "new" binding
to the same `(targetType, targetRef, policyVersionId)` mints a fresh
`Binding.Id`.

### A target has bindings to multiple versions of the same policy

This is the rollout scenario: a target was bound to `write-branch v1`,
then someone added a binding to `write-branch v2` for canary. Both
versions appear in the resolve response (different `policyVersionId`s),
ordered by version number DESC.

Consumers typically use the highest-numbered `Active` version per
policy; check `versionState == "Active"` and ignore `WindingDown`
unless your read path explicitly tolerates legacy versions (e.g.,
Conductor pinning to `policyVersionId` for reproducibility).

### A target ref points at a foreign id that no longer exists

andy-policies has no referential integrity with andy-tasks /
andy-issues / etc. (ADR 0003 §2). A binding to `template:gone-guid`
will continue to appear in resolve. The consumer is responsible for
detecting the stale ref:

- Treat it as "no policy applies" (silent), or
- Surface a UI warning ("stale binding to deleted template"), or
- Run a periodic reconciler that soft-deletes orphaned bindings.

andy-policies will not perform this reconciliation.

## 7. Forward compatibility: P4 hierarchy mode

Today, `resolve` is exact-match: only bindings whose `(targetType,
targetRef)` match byte-for-byte are returned. P4 (Scope hierarchy +
tighten-only resolution) extends the same endpoint with a
`?mode=hierarchy` flag that walks up the scope tree and applies
stricter-tightens-only resolution semantics.

**Consumer code written against the exact-match resolve will continue
to work without changes.** The default mode stays exact-match;
hierarchy mode is opt-in.

When P4 ships, you can choose to:

1. Keep exact-match. Your consumer continues to see exactly the
   bindings whose target matches the ref it sends.
2. Opt into hierarchy. Your consumer asks "what policies apply to
   this target *and its ancestors*?" and gets back the
   stricter-tightens-only resolved set.

The decision is per-call. ActionBus might use hierarchy for "which
policies govern this run?", while a forensic UI might use exact-match
to show only the directly-attached bindings.

## 8. Cross-references

- [Bindings (design)](../design/bindings.md) — entity shape,
  invariants, surface parity table.
- [ADR 0003 — Bindings are content-only metadata](../adr/0003-bindings.md)
  — the decisions captured authoritatively.
- [Policy Document Core (design)](../design/policy-document-core.md) —
  the `Policy` + `PolicyVersion` aggregate the binding's FK points at.
- [Lifecycle States (design)](../design/lifecycle.md) — the
  retired-version refusal cited in §6 and ADR 0003 §5.

For consumer-side integrations:

- **Conductor ActionBus** —
  [rivoli-ai/conductor#647 (Epic AF)](https://github.com/rivoli-ai/conductor/issues/647)
  for the Cockpit binding UX deep-link.
- **andy-tasks** —
  [rivoli-ai/andy-tasks#10 (Epic U)](https://github.com/rivoli-ai/andy-tasks/issues/10)
  for `DelegationContract.PolicyVersionId` usage.
- **andy-mcp-gateway** —
  [rivoli-ai/andy-mcp-gateway#2 (Epic AM)](https://github.com/rivoli-ai/andy-mcp-gateway/issues/2)
  for tool-policy binding patterns.
