# Per-task compliance evaluation (`evaluate-task`)

> Reference for `POST /api/policies/evaluate-task` (rivoli-ai/conductor#1944,
> TX F7.1). Companion to the plan-finalize surface
> `POST /api/policies/evaluate-plan` (`EvaluatePlanController`) — both share
> one evaluator core (`PlanEvaluator`); only the projection differs.

## Why a second endpoint

`evaluate-plan` evaluates a goal **once, at plan finalize**, and returns a
single `approve` / `manual` / `reject` string plus the predicate trace. The
cockpit needs governance **during execution**: each task (and each agent run)
is re-evaluated against the goal's effective policies, and the verdict must be
richer than a binary string. `evaluate-task` adds that per-run entry point and
returns a **structured `ComplianceAssessment`** — the individual violations
plus an aggregate risk tier and numeric score.

The two surfaces reuse the same machinery: the binding-resolution chain
(`IBindingResolutionService.ResolveForTargetAsync`, `BindingTargetType.ScopeNode`,
canonical `scope:{guid}` ref — see [bindings](../design/bindings.md)), the
registered `IPlanPredicate` catalog, and the per-policy decision-rule DSL. The
only difference is that `evaluate-task` **scopes the predicate inputs to the
single task** (the goal view's `Tasks` list is narrowed to the requested task
id) before running the predicates, and then projects the firing-rule trace into
violations + risk.

## Request

```json
POST /api/policies/evaluate-task
Authorization: Bearer <M2M token>
{ "goalId": "…", "taskId": "…" }
```

| Field    | Type | Notes |
|----------|------|-------|
| `goalId` | uuid | Owning goal; resolves the binding chain. 400 if empty GUID. |
| `taskId` | uuid | Task to scope to. 400 if empty GUID. |

Gated by the `andy-policies:plan:evaluate-task` permission (a sibling of
`andy-policies:plan:evaluate`, so the execution-time gate is scoped
independently of the plan-finalize gate). Granted to `admin` via the `*`
wildcard; otherwise M2M-only, like `plan:evaluate`.

## Response (`200`)

```json
{
  "goalId": "…",
  "taskId": "…",
  "assessment": {
    "decision": "reject",
    "riskTier": "critical",
    "riskScore": 20,
    "violations": [
      {
        "policyKey": "no-prod-deploy",
        "predicate": "noProductionDeploy",
        "outcome": "fail",
        "decision": "reject",
        "reason": "task targets a production environment"
      }
    ],
    "predicates": [ { "name": "allTasksReadOnly", "passed": true, "reason": "…" }, … ],
    "evaluatedAt": "2026-05-31T12:00:00+00:00"
  }
}
```

- **`decision`** — back-compat `approve` / `manual` / `reject`, derived from the
  same firing decision rules that produce the violations. Plan-finalize-style
  callers can keep reading this string.
- **`violations`** — one per predicate a firing `reject`/`manual` rule depended
  on that did **not** `Pass`. `approve` rules never produce violations.
- **`riskTier`** / **`riskScore`** — the deterministic fold (below).
- **`predicates`** — the full predicate trace, identical in shape to
  `evaluate-plan`.

### `Unevaluable` is never a silent pass

A predicate whose required data is missing returns `Unevaluable`. Exactly as on
the plan-finalize path (`RuleFires`' "couldn't prove it passed" semantics), an
`Unevaluable` predicate that a firing rule depended on is surfaced as a
violation with `"outcome": "unevaluable"` and `"reason": "data unavailable"`,
and is scored identically to a `fail`. Missing data can never reduce risk.

## Failure modes

| Status | When |
|--------|------|
| `400`  | Missing body, empty `goalId`, or empty `taskId`. |
| `403`  | Principal lacks `andy-policies:plan:evaluate-task`. |
| `404`  | andy-tasks doesn't recognise the goal, **or** the goal exists but does not contain the task. |

## Idempotency

5-minute idempotency cache keyed on `(goalId, taskId, planVersion)` —
independent of the plan-finalize cache key `(goalId, planVersion)`. A per-task
re-eval is cached on its own; different tasks of the same goal are cached
independently; a replan (new `planVersion`) busts the entry. Cache hits return
the verbatim response, including the same `evaluatedAt`.

## Scoring weight table

Risk is a deterministic fold over the violations. Each violation contributes
`criticalityWeight × decisionWeight`; the sum is `riskScore`. The aggregate
`riskTier` is the worst single-violation tier, bumped up one step (capped at
`critical`) when more than one violation accumulates.

| Policy criticality (`severity`) | weight |
|---------------------------------|--------|
| `info`                          | 1      |
| `moderate`                      | 2      |
| `critical`                      | 4      |

| Firing decision grade | weight |
|-----------------------|--------|
| `manual`              | 2      |
| `reject`              | 5      |

Per-violation tier (before the accumulation bump):

| criticality \ decision | `manual` | `reject`  |
|------------------------|----------|-----------|
| `info`                 | low      | medium    |
| `moderate`             | medium   | high      |
| `critical`             | high     | critical  |

Examples:

- No violations → `riskTier: none`, `riskScore: 0`.
- One `reject` on a `critical` policy → `riskTier: critical`, `riskScore: 20` (4×5).
- One `manual` on an `info` policy → `riskTier: low`, `riskScore: 2` (1×2).
- Two `manual` on `moderate` policies → `riskTier: high` (medium + accumulation bump), `riskScore: 8`.

The fold lives in `IComplianceScorer` (`ComplianceScorer`) so the weight table
is unit-testable in isolation.

## Downstream consumers

The structured assessment is designed for two consumers (hence stable
`policyKey` strings, ISO-8601 `evaluatedAt`, and a wire-stable lowercase tier
enum):

- **rivoli-ai/conductor#1945** — injects the assessment into andy-docs under
  `role:Audit`.
- **rivoli-ai/conductor#1946** — renders the assessment per task in the cockpit.
