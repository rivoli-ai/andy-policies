# Resolution Algorithm

How andy-policies answers "which policies apply to this target?" when
the answer involves a hierarchy walk. Targeted at: a contributor about
to touch `BindingResolutionService`, and a consumer engineer
(Conductor's ActionBus, andy-tasks per-task gates,
andy-mcp-gateway tool-policy) who needs to predict what an effective-
policies call will return.

For the *what* and *why* — entity shape, cycle-impossibility, dual
enforcement, surface parity — start with [ADR 0004 — Scope hierarchy](../adr/0004-scope-hierarchy.md)
and [Bindings (design)](bindings.md). For the underlying aggregate, see
[Policy Document Core](policy-document-core.md).

> **Scope reminder.** This document specifies *what the algorithm
> returns*. It does not specify *what consumers should do with the
> result* — that's a consumer concern (Conductor's ActionBus
> evaluator, andy-tasks per-task gate). andy-policies is the catalog,
> not the enforcer.

## Inputs

| Input              | Source                                                |
|--------------------|-------------------------------------------------------|
| `scopeNodeId`      | The leaf node from which to walk up                   |
| Scope chain        | `IScopeService.GetAncestorsAsync(scopeNodeId)` + leaf |
| Binding rows       | All non-deleted `Binding`s targeting any node in the chain (via `scope:{id}` or via the bridge to a chain node's external `Ref`) |
| `PolicyVersion`    | Joined for state, dimension fields, scopes            |
| `Policy`           | Joined for stable identity (`PolicyKey`)              |

## Algorithm

```text
fn ResolveForScope(scopeNodeId):
    chain = GetAncestors(scopeNodeId) + [scopeNodeId]   # root-to-leaf

    # 1. Gather candidate bindings.
    bindings = []
    for node in chain:
        bindings += BindingsTargeting(scope:{node.Id})
        if node.Type maps to BindingTargetType:        # bridge to P3 non-scope bindings
            bindings += BindingsTargeting(node.Type, node.Ref)
    bindings = bindings.where(b => b.DeletedAt == null)

    # 2. Hydrate Policy + PolicyVersion for each binding.
    versions  = LoadVersions([b.PolicyVersionId for b in bindings])
    policies  = LoadPolicies([v.PolicyId for v in versions])

    # 3. Tighten-only fold per PolicyId.
    grouped = bindings.groupBy(b => versions[b.PolicyVersionId].PolicyId)
    effective = []
    for (policyId, group) in grouped:
        mandatory = group.where(b => b.BindStrength == Mandatory)
        winner    = (mandatory if mandatory.any() else group)
                    .orderByDescending(b => DepthOf(b))            # most-specific
                    .thenByDescending(b => b.BindStrength)         # Mandatory(1) before Recommended(2)
                    .thenBy(b => b.CreatedAt)                      # tiebreak earliest
                    .first()
        effective.append(MakeEntry(winner, policies, versions, chain))

    # 4. Deterministic ordering for snapshotting consumers.
    effective.sort((a, b) =>
        compare(a.BindStrength, b.BindStrength)        # Mandatory before Recommended
        ?? compare(a.PolicyKey, b.PolicyKey, Ordinal)) # alpha within strength

    return EffectivePolicySet(scopeNodeId, effective)
```

The fold rule is the headline decision of Epic P4 (ADR 0004 §4):
**for each `PolicyId`, the deepest `Mandatory` binding wins; if no
`Mandatory` exists, the deepest `Recommended` wins; tiebreak earliest
`CreatedAt`.** A descendant `Recommended` shadowed by an ancestor
`Mandatory` is silently dropped — consumers never see the would-be
downgrade. Write-time validation (P4.4) prevents the catalog from
accumulating these silent drops in the first place.

## Worked example 1 — simple cascade

The canonical worked example from the Epic P4 issue body.

```text
Org(rivoli)
└── Tenant(prod)
    └── Team(payments)
        └── Repo(payments-svc)
```

**Bindings:**

| Bound at         | PolicyKey   | Version | BindStrength  | CreatedAt |
|------------------|-------------|--------:|---------------|-----------|
| `Org(rivoli)`    | `no-prod`   | 2       | `Mandatory`   | t1        |
| `Team(payments)` | `sandboxed` | 1       | `Recommended` | t2        |
| `Repo(payments-svc)` | `high-risk` | 3   | `Mandatory`   | t3        |

**Resolve at `Repo(payments-svc)`:**

```json
{
  "scopeNodeId": "<repo-id>",
  "policies": [
    {
      "policyKey": "high-risk",
      "version": 3,
      "bindStrength": "Mandatory",
      "sourceScopeType": "Repo"
    },
    {
      "policyKey": "no-prod",
      "version": 2,
      "bindStrength": "Mandatory",
      "sourceScopeType": "Org"
    },
    {
      "policyKey": "sandboxed",
      "version": 1,
      "bindStrength": "Recommended",
      "sourceScopeType": "Team"
    }
  ]
}
```

Three distinct `PolicyId`s → three entries. Mandatories first
(`high-risk`, `no-prod`), then Recommended (`sandboxed`); within each
strength group, sorted alphabetically by `PolicyKey`. Each entry's
`source` points at the binding that won — `high-risk` and `sandboxed`
have only one binding each, `no-prod` has only one binding (at Org).

The full REST response includes `policyId`, `policyVersionId`,
`sourceBindingId`, `sourceScopeNodeId`, and `sourceDepth` per entry —
omitted above for brevity.

## Worked example 2 — upgrade at leaf

Demonstrates the `Recommended → Mandatory` upgrade path: a descendant
binding *raises* the strength compared to its ancestor.

```text
Org(rivoli)
└── Tenant(prod)
    └── Team(payments)
        └── Repo(payments-svc)
```

**Bindings:**

| Bound at             | PolicyKey   | Version | BindStrength  | CreatedAt |
|----------------------|-------------|--------:|---------------|-----------|
| `Team(payments)`     | `sandboxed` | 1       | `Recommended` | t1        |
| `Repo(payments-svc)` | `sandboxed` | 1       | `Mandatory`   | t2        |

**Resolve at `Repo(payments-svc)`:**

```json
{
  "scopeNodeId": "<repo-id>",
  "policies": [
    {
      "policyKey": "sandboxed",
      "version": 1,
      "bindStrength": "Mandatory",
      "sourceScopeType": "Repo"
    }
  ]
}
```

One `PolicyId` (`sandboxed`) → one entry. The fold:

1. Group bindings by `PolicyId` — both rows are in the same group.
2. Pick the deepest `Mandatory` if any. The Repo binding is Mandatory;
   the Team binding is Recommended → `Mandatory` wins, source is
   the Repo binding.

The Team Recommended is **not** a tighten-only violation — it was
authored first, when no upstream Mandatory existed. The subsequent
Repo Mandatory upgraded the effective set without contradicting the
ancestor.

## Worked example 3 — forbidden downgrade (rejected at write)

Demonstrates the write-time tighten-only validator (P4.4). Read-time
fold would have silently dropped the row, but the catalog should
never contain it.

```text
Org(rivoli)
└── Tenant(prod)
    └── Team(payments)
        └── Repo(payments-svc)
```

**Existing binding:**

| Bound at      | PolicyKey | Version | BindStrength  |
|---------------|-----------|--------:|---------------|
| `Org(rivoli)` | `no-prod` | 2       | `Mandatory`   |

**Attempted write:** `POST /api/bindings`

```json
{
  "policyVersionId": "<no-prod-v2-id>",
  "targetType": "ScopeNode",
  "targetRef": "scope:<team-payments-id>",
  "bindStrength": "Recommended"
}
```

**Response:** HTTP 409

```json
{
  "type": "/problems/binding-tighten-only-violation",
  "title": "Tighten-only violation",
  "status": 409,
  "detail": "Cannot create a Recommended binding for policy 'no-prod' at this scope — ancestor Org 'rivoli' binds it as Mandatory (binding 8d1a...).",
  "errorCode": "binding.tighten-only-violation",
  "offendingAncestorBindingId": "8d1a...",
  "offendingScopeNodeId": "<org-rivoli-id>",
  "offendingScopeDisplayName": "rivoli",
  "policyKey": "no-prod"
}
```

The write is rejected. Admins can:

- **Tighten the proposal**: change `bindStrength` to `Mandatory` —
  upgrade is allowed.
- **Remove the ancestor binding**: delete the Org-level Mandatory
  first (subject to P7 RBAC).
- **Use an Override (P5)**: when P5 lands, an explicit Override with
  approver + expiry is the documented escape hatch from tighten-only.

## Soft-ref fallback

When `ResolveForTargetAsync(targetType, targetRef)` is called with a
`(targetType, targetRef)` that **does not** map to any `ScopeNode`,
the resolver degrades to P3 exact-match semantics: it returns whatever
bindings target the exact pair, with `scopeNodeId = null` on the
envelope so callers can tell the difference. No 404 — the service
recognises that some consumers maintain bindings against external
refs (template ids from andy-tasks, repo slugs from GitHub) without
mirroring them as `ScopeNode`s.

## Deterministic ordering — why it matters

The resolver returns the policy list sorted by:

1. `BindStrength` — `Mandatory` (1) before `Recommended` (2).
2. `PolicyKey` — alphabetical, ordinal comparison.

Both fields are stable across invocations. Consumers that snapshot the
response (Conductor's bundle pinning per Epic P8, andy-tasks
DelegationContract reproducibility per Epic U) can rely on byte-for-
byte order without re-sorting.

## Cross-references

- [ADR 0004 — Scope hierarchy](../adr/0004-scope-hierarchy.md) — the
  decisions captured authoritatively (typed 6-level + materialized
  path + dual enforcement + ordering rule).
- [Scope hierarchy (design)](scope-hierarchy.md) — the entity shape,
  index strategy, and surface parity table.
- [Bindings (design)](bindings.md) — the binding aggregate the resolver
  reads from.
- [P3.4 single-target resolve](bindings.md#resolve-semantics-p34) — the
  exact-match counterpart that filters Retired versions.
- `BindingResolutionService.cs` (under `src/Andy.Policies.Infrastructure/Services/`) — the implementation.
- `BindingResolutionServiceTests.cs` (under `tests/Andy.Policies.Tests.Unit/Services/`) — eleven unit tests pinning each fold rule.
