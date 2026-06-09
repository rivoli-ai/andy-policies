# Policy → Task Contract (AX.6 design)

> **Story:** AX.6 — DISCOVERY + documented contract.
> **Parent epic:** Agents+Policies execution (rivoli-ai/conductor#2093).
> **Purpose:** Pin the precise "policy → task" contract so the downstream
> policy stories can be built against a known surface:
> - **AX.7** — planner attaches policies to a task.
> - **AX.8** — inject attached policies into the agent container / system prompt.
> - **AX.9** — derive a tool-permission allow-list from attached policies.
> - **AX.10** — verify task compliance against attached policies.
>
> This is a **read-of-existing-state** document. andy-policies already ships a
> full policy catalog, a per-agent default binding graph, a resolve surface, and
> a per-task evaluation surface. AX.7–AX.10 should **consume these**, not invent
> a parallel model. All citations are `file:line` against this repo at the time
> of writing.

---

## TL;DR — the contract in one screen

- A **policy** is a stable identity (`Policy`) + N append-only **versions**
  (`PolicyVersion`). All content lives on the version: `Summary` (human text),
  `RulesJson` (opaque allow/deny DSL), `Enforcement` (MUST/SHOULD/MAY),
  `Severity` (info/moderate/critical), `Scopes` (flat string list).
- Six **stock policies** are seeded Active on first boot and read exactly like
  agent guardrails: `read-only`, `draft-only`, `write-branch`, `sandboxed`,
  `no-prod`, `high-risk`.
- A **default agent→policy binding graph** is also seeded
  (`agent:{slug}` → policy), e.g. `coding → write-branch + sandboxed`,
  everyone → `no-prod + high-risk`.
- **Fetch applicable policies for a target** with
  `GET /api/bindings/resolve?targetType=Agent&targetRef=agent:{slug}` (or
  `Template`/`Repo`/`ScopeNode`/…). Auth: bearer JWT, audience
  `urn:andy-policies-api`, permission `andy-policies:binding:read`.
- The resolve DTO carries **metadata only** (name, enforcement, severity,
  scopes, version id) — **not** the `Summary`/`RulesJson` body. To get the
  injectable text, do a **second** call
  `GET /api/policies/{policyId}/versions/{versionId}` (permission
  `andy-policies:policy:read`).
- **Task attachment reference:** store `{ policyId, policyVersionId, policyKey,
  versionNumber, enforcement, severity, bindStrength }` per attached policy.
  The `policyVersionId` (a GUID, immutable once Active) is the stable resolvable
  key for later verification.
- **Verify compliance** with `POST /api/policies/evaluate-task` (per task /
  per run), permission `andy-policies:plan:evaluate-task`, returns a structured
  `ComplianceAssessment` (violations + risk tier + score).
- **Policy → permission:** there is **no andy-policies→RBAC linkage**.
  andy-policies is the *catalog*, not the *enforcer* (it says so explicitly).
  The tool allow-list for AX.9 must be derived **consumer-side** from the
  `allow`/`deny` arrays inside `RulesJson` — that schema is consumer-owned and
  opaque to andy-policies.

---

## 1. Policy domain model — all fields

The aggregate is split per ADR 0001: `Policy` is stable identity; `PolicyVersion`
holds **all** version-dependent content. (`src/Andy.Policies.Domain/Entities/`.)

### `Policy` — stable identity only

`src/Andy.Policies.Domain/Entities/Policy.cs:16-33`

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | Stable policy identity. |
| `Name` | `string` | Unique slug, regex `^[a-z0-9][a-z0-9-]{0,62}$` (`Policy.cs:24`). This is the "policy key". |
| `Description` | `string?` | Human-readable; editable on the stable row. |
| `CreatedAt` | `DateTimeOffset` | |
| `CreatedBySubjectId` | `string` | |
| `Versions` | `ICollection<PolicyVersion>` | nav. |

> The `Policy` **never** carries rules/enforcement/severity/scopes — those are
> version fields (`Policy.cs:11-15`).

### `PolicyVersion` — all content + lifecycle

`src/Andy.Policies.Domain/Entities/PolicyVersion.cs:24-114`

| Field | Type | Carries… | Notes |
|---|---|---|---|
| `Id` | `Guid` | — | **The cross-service identifier** consumers reference (`PolicyVersion.cs:15-17`). |
| `PolicyId` / `Policy` | `Guid` / nav | — | |
| `Version` | `int` | — | Monotonic from 1; a human label, *not* a cross-service id (`:32`). |
| `State` | `LifecycleState` | — | `Draft`/`Active`/`WindingDown`/`Retired` (`:36`). |
| `Summary` | `string` | **human-readable rule text** | The closest thing to "rule prose". For seeded policies it equals the `Description` (`:38`, seeded at `PolicySeeder.cs:182`). |
| `Enforcement` | `EnforcementLevel` | **enforcement mode** | RFC 2119: `May`/`Should`/`Must`, default `Should` (`:45`). |
| `Severity` | `Severity` | **severity** | `Info`/`Moderate`/`Critical`, default `Moderate` (`:51`). |
| `Scopes` | `IList<string>` | **scope / applicability** | Flat list e.g. `prod`, `repo:rivoli-ai/conductor`, `tool:write-branch` (`:53-61`). |
| `RulesJson` | `string` (JSON) | **structured rules** | Opaque allow/deny/flags DSL. **andy-policies never interprets it** — consumers own the schema (`:63-69`). |
| `CreatedAt` / `CreatedBySubjectId` | | | |
| `ProposerSubjectId` | `string` | — | Author-cannot-self-approve invariant (`:75-82`). |
| `PublishedAt` / `PublishedBySubjectId` | `DateTimeOffset?` / `string?` | — | |
| `ReadyForReview` | `bool` | — | Approver-inbox handoff flag (`:88-97`). |
| `RetiredAt` | `DateTimeOffset?` | — | |
| `SupersededByVersionId` | `Guid?` | — | |
| `Revision` | `uint` | — | Optimistic concurrency token (`:109-114`). |

**Answer to "does a policy carry…":**

- **Human-readable rule TEXT to inject into a prompt** → yes, `Summary`
  (+ `Policy.Description`). There is **no** dedicated long-form markdown body
  beyond `Summary`; the structured rules live in `RulesJson`.
- **Structured rules** → yes, `RulesJson` (opaque allow/deny DSL).
- **Category/area** → only indirectly, via `Scopes` (flat tags) and the slug.
  There is no separate `category` enum.
- **Severity** → yes, `Severity`.
- **Scope (which tasks/agents/areas it applies to)** → `Scopes` is the
  applicability *tag* list; the actual **attachment to an agent/repo/template**
  is expressed by **`Binding`** rows (see §4), not by a field on the version.
- **Enforcement mode** → yes, `Enforcement` (MUST/SHOULD/MAY) **and** the
  per-attachment `BindStrength` (Mandatory/Recommended).
- **Version** → yes, `Version` (label) + `Id` (stable GUID key).

Enums: `src/Andy.Policies.Domain/Enums/EnforcementLevel.cs`,
`Severity.cs`, `LifecycleState.cs`, `BindStrength.cs`, `BindingTargetType.cs`,
`ScopeType.cs`.

---

## 2. Seeded / default policies — the agent guardrails

`src/Andy.Policies.Infrastructure/Data/PolicySeeder.cs:64-126`. Six canonical
policies, each seeded with a single **Active** v1 (`PolicySeeder.cs:177`), so
consumers can bind on first boot. Source of truth: `config/policies-seed.json`.

| Slug | Enforcement | Severity | Scopes | Intent / `RulesJson` highlights |
|---|---|---|---|---|
| `read-only` | MUST | info | (none) | allow `fs.read,git.read,search,review.comment`; deny `fs.write,git.write,git.push,shell.exec,container.exec`. Triage/research/review. (`:66-75`) |
| `draft-only` | MUST | info | `template` | allow `draft.create/update,comment.create,plan.propose`; deny `draft.publish,merge,deploy,release.cut`. Planning agents. (`:76-85`) |
| `write-branch` | SHOULD | moderate | `repo` | allow `fs.write,git.commit,git.push:feature/*`; **deny `git.push:main`/`master`/`release/*`**; `branchPattern ^(feature\|fix\|chore\|spike)/.+`. Coding agents. (`:86-95`) |
| `sandboxed` | MUST | moderate | `tool`,`container` | allow `container.exec,fs.write:/workspace/**`; deny `fs.write:/host/**`,`network.egress:!allowlist`; `resourceCaps{cpu,memoryMiB,wallClockSeconds}`. (`:96-105`) |
| `no-prod` | MUST | **critical** | `prod` | universal guardrail; deny anything targeting prod / release-tagged services. (`:106-115`) |
| `high-risk` | MUST | **critical** | (none) | universal guardrail; `dangerousActions:[git.push.force,schema.migrate,secret.rotate,delete.bulk,tenant.delete]`, `requireTypedConfirmation:true`, `approvers:[{role:maintainer,minApprovals:1,selfApprovalForbidden:true}]`. (`:116-125`) |

**Yes — these read exactly like agent guardrails** ("never push to main" =
`write-branch`; "no secrets/dangerous ops" = `high-risk`; "stay in the sandbox"
= `sandboxed`; "don't touch prod" = `no-prod`).

### Default agent → policy bindings (already seeded)

`src/Andy.Policies.Infrastructure/Data/BindingSeeder.cs:81-107`
(`config/bindings-seed.json`). Each is `agent:{slug}` → policy at Mandatory
strength, target type `Agent` (ordinal 6, `BindingTargetType.cs:Agent`):

| Agent | Policies |
|---|---|
| `triage` | read-only, no-prod, high-risk |
| `research` | read-only, no-prod, high-risk |
| `review` | read-only, no-prod, high-risk |
| `planning` | draft-only, no-prod, high-risk |
| `coding` | write-branch, sandboxed, no-prod, high-risk |
| `validation` | sandboxed, no-prod, high-risk |

**This is the default policy-attachment map AX.7 should consume** — the planner
already has a defensible "which policies apply to a `coding` task" answer
without inventing one.

---

## 3. HTTP API — endpoints, DTO, auth

Controllers: `src/Andy.Policies.Api/Controllers/`.

### Auth (applies to all of the below)

- **Bearer JWT**, audience **`urn:andy-policies-api`**
  (`src/Andy.Policies.Api/Program.cs:34,39`).
- **Permission** enforced per-endpoint via
  `[Authorize(Policy = "andy-policies:…")]`; every code is registered in
  `Program.cs:62-86` and mapped to an `RbacRequirement` → `IRbacChecker`.
- andy-tasks calls these **M2M** (it already holds an `andy-policies:plan:*`
  scope for `evaluate-plan`/`evaluate-task` — `Program.cs:78-86`).

### Policy catalog — `PoliciesController` (route `api/policies`)

`src/Andy.Policies.Api/Controllers/PoliciesController.cs`

| Method + route | Perm | Returns | Body has rule content? |
|---|---|---|---|
| `GET /api/policies` (filters: `namePrefix,scope,enforcement,severity,skip,take,bundleId`) | `policy:read` | `PolicyDto[]` | **No** — identity only. (`:32-60`) |
| `GET /api/policies/{id}` | `policy:read` | `PolicyDto` | **No**. (`:84-97`) |
| `GET /api/policies/by-name/{name}` | `policy:read` | `PolicyDto` | **No** — lookup by slug. (`:99-112`) |
| `GET /api/policies/{id}/versions` | `policy:read` | `PolicyVersionDto[]` | **Yes** (Summary+RulesJson). (`:114-127`) |
| `GET /api/policies/{id}/versions/active` | `policy:read` | `PolicyVersionDto` | **Yes** — resolves the Active version. (`:135-148`) |
| `GET /api/policies/{id}/versions/{versionId}` | `policy:read` | `PolicyVersionDto` | **Yes** — the canonical "give me the body" call. (`:150-163`) |
| `POST/PUT/POST bump` | `policy:author` | `PolicyVersionDto` | (write paths, not needed by AX.7–10) |

**`PolicyDto`** (`src/Andy.Policies.Application/Dtos/PolicyDto.cs:12-18`):
`Id, Name, Description?, CreatedAt, CreatedBySubjectId, VersionCount,
ActiveVersionId?` — **no Summary, no RulesJson**.

**`PolicyVersionDto`** (`…/Dtos/PolicyVersionDto.cs:25-40`) — **this one carries
the injectable content**:
`Id, PolicyId, Version, State, Enforcement, Severity, Scopes[], Summary,
RulesJson, CreatedAt, CreatedBySubjectId, ProposerSubjectId, Revision,
PublisherSubjectId?, ReadyForReview`. Wire casing: `Enforcement` UPPERCASE
(`MUST/SHOULD/MAY`), `Severity` lowercase, `State` PascalCase
(`PolicyVersionDto.cs:8-13`).

---

## 4. How a consumer fetches policies applicable to a task/goal/area

The planner does **not** list-all-and-filter. The first-class question is
"which policies bind to *this target*?", answered by the **resolve** surface.

### `GET /api/bindings/resolve` — the cleanest query

`src/Andy.Policies.Api/Controllers/BindingsController.cs:183-229`

```
GET /api/bindings/resolve?targetType={Template|Repo|ScopeNode|Tenant|Org|Agent}&targetRef={ref}
Authorization: Bearer <m2m-jwt aud=urn:andy-policies-api>
Permission: andy-policies:binding:read
```

It joins `Binding + PolicyVersion + Policy`, filters out Retired versions,
dedups same-target/same-version pairs preferring `Mandatory`, orders
deterministically (policy name ASC, version DESC), and returns **200 with
`count:0` for an unknown target — never 404** (`:171-181`).

**Canonical `targetRef` shapes** (`Binding.cs:23-36`, BindingsController guide):

| `targetType` | `targetRef` |
|---|---|
| `Agent` (6) | `agent:{slug}` — e.g. `agent:coding` |
| `Template` (1) | `template:{guid}` |
| `Repo` (2) | `repo:{org}/{name}` |
| `ScopeNode` (3) | `scope:{guid}` |
| `Tenant` (4) | `tenant:{guid}` |
| `Org` (5) | `org:{guid}` |

**Response — `ResolveBindingsResponse`**
(`…/Dtos/ResolveBindingsResponse.cs:16-20`):
`TargetType, TargetRef, Bindings: ResolvedBindingDto[], Count`.

**`ResolvedBindingDto`** (`…/Dtos/ResolvedBindingDto.cs:17-27`):
`BindingId, PolicyId, PolicyName, PolicyVersionId, VersionNumber,
VersionState, Enforcement, Severity, Scopes[], BindStrength`.

> **Contract gap that AX.7/AX.8 must handle:** `ResolvedBindingDto` carries
> **metadata only — no `Summary`, no `RulesJson`.** To get the injectable body
> and the allow/deny rules you need a **second call** per policy:
> `GET /api/policies/{PolicyId}/versions/{PolicyVersionId}` → `PolicyVersionDto`
> (which has `Summary` + `RulesJson`). So the planner flow is:
> 1. `resolve` for the agent/template/repo target → list of
>    `(PolicyId, PolicyVersionId, …)`.
> 2. For each, `GET …/versions/{versionId}` → `Summary` + `RulesJson`.
> 3. Persist the attachment ref (§6) and build the prompt block (§5) +
>    allow-list (§7).

**Filtering options.** Exact-match on `(targetType, targetRef)` (no hierarchy
walk — that's the P4 `EffectivePolicy` surface). The catalog list endpoint also
supports `?scope=&enforcement=&severity=` filters
(`PoliciesController.cs:35-51`) if a planner wants area-based discovery instead
of binding-based, but **binding resolve is the cleanest and the one the seed
graph is designed for.**

> **Suggested tiny improvement for AX.8 (NOT done here):** the second round-trip
> is wasteful — a future andy-policies story could add `?includeBody=true` to
> `resolve` so `ResolvedBindingDto` optionally carries `Summary`/`RulesJson`.
> Out of scope for AX.6; flagged in Open Questions.

---

## 5. The injectable form — policy → agent system-prompt text

`PolicyVersion.Summary` is the human prose; `RulesJson` is the structured
allow/deny. For a system-prompt block, render **both**: the prose tells the
agent *why*, the allow/deny tells it *what's forbidden*. Proposed format
(AX.8 owns the final wording; this is the contract shape):

```
## Governance policies (enforced by Conductor)

You are operating under the following policies. Treat MUST as a hard rule you
may never violate, SHOULD as a strong default that requires a written rationale
to deviate from, and MAY as advisory.

- [MUST] write-branch (severity: moderate)
  The agent may mutate files and create commits, but only on a feature branch
  matching the goal's branch pattern. Pushes to the repo's default branch
  (main/master) are denied.
  Allowed: fs.write, git.commit, git.push:feature/*
  Denied:  git.push:main, git.push:master, git.push:release/*

- [MUST] sandboxed (severity: moderate)
  All execution must happen inside the container/sandbox the task provides...
  Allowed: container.exec, fs.write:/workspace/**
  Denied:  fs.write:/host/**, network.egress:!allowlist

- [MUST] no-prod (severity: critical)
  Any operation targeting prod or release-tagged services is denied regardless
  of intent.

- [MUST] high-risk (severity: critical)
  Force-push, schema migration, secret rotation, mass delete, and tenant delete
  require an approver's typed confirmation. Do not attempt these unsupervised.

Violating a MUST policy will cause your run to be rejected at verification.
```

**Rendering rule (deterministic, per policy):**

```
- [{enforcement-uppercase}] {policyName} (severity: {severity-lowercase})
  {Summary}
  Allowed: {join(RulesJson.allow, ", ")}        # omit line if absent
  Denied:  {join(RulesJson.deny,  ", ")}        # omit line if absent
```

Source fields: `enforcement`/`severity`/`policyName` from `ResolvedBindingDto`;
`Summary` + `RulesJson.allow`/`.deny` from the `PolicyVersionDto`. Note
`RulesJson` is **opaque to andy-policies** — `allow`/`deny`/`dangerousActions`/
`branchPattern`/`resourceCaps` are the *de-facto* keys used by the seed set
(`PolicySeeder.cs:73-125`); AX.8 should parse defensively and skip unknown keys.

---

## 6. The task-attachment reference shape

A `TaskNode` (andy-tasks) that has policies attached should store, **per
attached policy**, the resolve output minus the volatile bits:

```jsonc
// TaskNode.attachedPolicies[]  (proposed)
{
  "policyId":        "55ab...",          // Guid — stable policy identity
  "policyVersionId": "0a17...",          // Guid — STABLE, IMMUTABLE-once-Active key
  "policyKey":       "write-branch",     // slug — human/audit-stable, matches ComplianceViolation.PolicyKey
  "versionNumber":   1,                  // int  — display label only
  "enforcement":     "MUST",             // snapshot for offline prompt-build / display
  "severity":        "moderate",
  "bindStrength":    "Mandatory",        // from the binding that attached it
  "sourceBindingId": "9d28..."           // optional: provenance for audit
}
```

**Why `policyVersionId` is the resolvable key, not the slug:**

- `PolicyVersion.Id` is the documented cross-service identifier
  (`PolicyVersion.cs:15-17`); `Version` int is "a human-readable label, not a
  cross-service identifier".
- An Active `PolicyVersion` is **immutable** (`PolicyVersion.cs:8-12`), so a
  pinned `policyVersionId` resolves to byte-stable content forever — exactly
  what AX.10 verification needs (verify against *the version that was attached*,
  not whatever is Active later).
- Resolve it later with `GET /api/policies/{policyId}/versions/{policyVersionId}`.
- Keep `policyKey` (slug) too because that is what `ComplianceViolation.PolicyKey`
  reports (`ComplianceAssessment.cs:75-79`) and what audit trails show — it's the
  human-stable join key between an attachment and a violation.

> For full reproducibility across replans, AX.7 could additionally pin a
> **bundle id** (`?bundleId=` on resolve / get — `BundlesController`,
> `PoliciesController.cs:42`) so the whole catalog snapshot is frozen. Optional;
> the per-version GUID already gives content stability for a single policy.

---

## 7. RBAC / permission angle (AX.9)

**There is no andy-policies → RBAC / permission linkage.** Confirmed:

- The repo states it plainly: *"andy-policies stores what policies bind to what
  targets. Whether a run violates a policy is a consumer concern… This service
  is the catalog, not the enforcer."* (`docs/guides/consumer-integration-bindings.md`).
- `EnforcementLevel` doc: *"This service only stores the level — it does NOT
  enforce. Consumers… translate."* (`EnforcementLevel.cs`).
- `RulesJson` doc: *"This service never interprets it — consumers (Conductor
  ActionBus, andy-tasks approval gates) own the schema."* (`PolicyVersion.cs:63-69`).
- The only permission codes in andy-policies
  (`Program.cs:62-86`) are **access-control over the catalog itself**
  (`andy-policies:policy:read`, `:binding:manage`, `:plan:evaluate-task`, …) —
  they are **not** the agent tool permissions a policy would grant/deny.

**So AX.9 derives the tool allow-list consumer-side from `RulesJson`.** The seed
DSL gives the shape to fold over:

- `RulesJson.allow[]` — tool/verb tokens the agent **may** use
  (`fs.read`, `git.commit`, `git.push:feature/*`, `container.exec`, …).
- `RulesJson.deny[]` — tokens the agent **may not** use
  (`git.push:main`, `fs.write:/host/**`, …).
- Combine across all attached policies: **deny wins** (a `deny` token from any
  attached policy removes that capability even if another policy allows it),
  matching the "stricter-tightens-only" philosophy and the seeded guardrails
  (everyone gets `no-prod` + `high-risk` deny lists).
- `dangerousActions[]` + `requireTypedConfirmation` (from `high-risk`) map to a
  **needs-approval** bucket rather than an outright deny.

The token vocabulary (`fs.read`, `git.push:<branch>`, `container.exec`, …) is a
**consumer-owned contract** — AX.9 must define the canonical mapping from these
DSL tokens to Conductor `ActionBus` / container tool permissions, because
andy-policies deliberately does not.

---

## 8. Verification surface (AX.10) — already exists

andy-policies already ships the per-task evaluator the cockpit uses; AX.10
should **call it**, not build a Swift-side rule engine.

`POST /api/policies/evaluate-task`
(`src/Andy.Policies.Api/Controllers/EvaluateTaskController.cs:49-69`)

```
POST /api/policies/evaluate-task
Authorization: Bearer <m2m-jwt aud=urn:andy-policies-api>
Permission: andy-policies:plan:evaluate-task
Body: { "goalId": "<guid>", "taskId": "<guid>" }   // EvaluateTaskRequest
```

- The service fetches the goal view from andy-tasks (M2M), **scopes predicate
  inputs to the single task**, resolves the same effective policy set, and runs
  the registered predicate catalog (`docs/reference/evaluate-task.md`).
- Returns `EvaluateTaskResponse { GoalId, TaskId, ComplianceAssessment }`
  (`…/Dtos/EvaluateTaskDtos.cs:30-32`).
- `ComplianceAssessment` (`…/Dtos/ComplianceAssessment.cs:144-150`):
  `Decision (approve/manual/reject), RiskTier (none..critical), RiskScore (int),
  Violations: ComplianceViolation[], Predicates: PredicateResultDto[],
  EvaluatedAt`.
- `ComplianceViolation` (`ComplianceAssessment.cs:97-102`):
  `PolicyKey (slug), Predicate, Outcome (fail/unevaluable), Decision
  (reject/manual), Reason`.
- Idempotent ~5 min per `(goalId, taskId, planVersion)`.

Companion plan-finalize surface: `POST /api/policies/evaluate-plan`
(`EvaluatePlanController`, perm `andy-policies:plan:evaluate`,
`…/Dtos/EvaluatePlanDtos.cs`). Both share one evaluator core.

> **Caveat for AX.10:** `evaluate-task` resolves the effective policy set
> **itself** from the goal/task's bindings (predicate-based) — it does **not**
> currently take an explicit "verify against *these* attached policy-version
> ids" input. If AX.7 pins a specific `policyVersionId` set on the task and we
> need verification to honour exactly that pinned set (not a freshly-resolved
> one), that's a gap — see Open Questions.

---

## Open questions for AX.7–AX.10

1. **(AX.8) Two round-trips for the body.** `resolve` returns metadata only;
   the injectable `Summary`/`RulesJson` needs a per-policy
   `GET …/versions/{versionId}`. Acceptable for ~4 policies/agent, but consider
   filing an andy-policies story to add `?includeBody=true` to `resolve`.
2. **(AX.9) DSL token vocabulary is consumer-owned.** andy-policies does not
   define what `git.push:feature/*` / `container.exec` / `fs.write:/host/**`
   map to in Conductor's permission space. AX.9 must own that mapping table and
   the deny-wins merge semantics. Where does it live — Conductor `ActionBus`,
   andy-tasks, or the container launcher?
3. **(AX.9) `dangerousActions` / approver chains.** `high-risk` carries
   `requireTypedConfirmation` + an approver spec. Does AX.9 surface these as a
   "needs approval" capability bucket, or is that AX.10's job? Pin the owner.
4. **(AX.10) Pinned-set vs re-resolved-set verification.** `evaluate-task`
   re-resolves the policy set from bindings; AX.7 may pin specific
   `policyVersionId`s on the task. If they can drift (a new policy version is
   published mid-flight), decide whether verification must honour the pinned
   set. May need an andy-policies story to accept explicit policy-version ids.
5. **(AX.7) Agent-slug ↔ task mapping.** The default binding graph is keyed by
   the six agent slugs (`triage…review`). AX.7 needs the task→agent-slug mapping
   (from the planner / andy-agents) to pick the right `agent:{slug}` resolve
   target. Confirm the slug set is stable and matches `BindingSeeder.SeedAgentSlugs`.
6. **(AX.7) Per-repo / per-template overrides.** Beyond the agent defaults, a
   task may also want repo- or template-scoped policies (`repo:…`,
   `template:…`). Decide whether AX.7 resolves **multiple** targets (agent +
   repo + template) and merges, or agent-only for v1.
7. **(all) Bundle pinning for reproducibility.** Should AX.7 pin a `bundleId`
   so a goal's whole policy catalog is frozen for the life of the run, or is
   per-version GUID stability enough? (`?bundleId=` exists on resolve/get.)
