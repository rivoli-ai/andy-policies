# Compliance/audit injection into andy-docs

How andy-policies persists a compliance decision into **andy-docs** so
audit/compliance is queryable across services (story
[rivoli-ai/conductor#1945](https://github.com/rivoli-ai/conductor/issues/1945)
— TX F7.2). Companion to the [audit envelope spec](../audit-envelope.md)
(the *at-rest* hash-chain shape) and the
[per-task compliance evaluation](evaluate-task.md) (the structured
`ComplianceAssessment` from #1944 that this injection writes).

## Why

The compliance assessment + hash-chained audit trail #1944 produces stays
inside andy-policies — it is never persisted into andy-docs, so there is
no cross-service, queryable record tying "the compliance verdict / audit
export" to the goal or run it belongs to. This path closes that gap: on
**plan-finalize** and **per-run/per-task** evaluation, andy-policies
writes the assessment (and a tamper-evident audit-chain segment) into
andy-docs as `role:Audit` documents linked to the goal (and run/task), so
Conductor (#1946) can discover and render them with a single by-target
link query.

## What gets written

Two artifacts per decision, both attached with `role: Audit`:

1. **Structured compliance assessment** — `application/json`. A
   self-describing envelope wrapping the #1944 `ComplianceAssessment`:

   ```json
   {
     "goalId": "<guid>",
     "taskId": "<guid|null>",
     "planVersion": "1",
     "assessment": { /* ComplianceAssessment: decision, riskTier,
                        riskScore, violations[], predicates[],
                        evaluatedAt */ }
   }
   ```

   The embedded `assessment` object round-trips back into the #1944
   `ComplianceAssessment` record (asserted by the unit/integration tests),
   so a reader does not need the link rows to know which
   goal/task/planVersion the assessment belongs to.

2. **Audit-chain export segment** — the NDJSON the existing
   `IAuditExporter.WriteNdjsonAsync` already emits (the hash-chained
   envelope per [`audit-envelope.md`](../audit-envelope.md), verifiable
   offline). This makes the andy-docs copy tamper-evident, not just a
   loose JSON blob. Optional — gated on
   `ComplianceAudit:IncludeAuditChainSegment` (default `true`).

### Seq-range bounding

andy-policies' audit chain records **catalog mutations**, not
per-decision events, so a compliance decision does not itself create an
audit row. The export segment is therefore bounded to the **chain head
observed at decision time** (`fromSeq=null, toSeq=MAX(seq)`): the full
verifiable chain up to the moment of the verdict. The terminal hash pins
chain state at decision time, making the andy-docs copy tamper-evident
without inventing a synthetic per-decision event. An empty chain bounds
to `toSeq=0` and the exporter still emits a well-formed (empty) bundle
with a summary line.

## How it's written

andy-policies calls `docs.put` (`POST /api/documents:put`, multipart:
`file` bytes + `meta` JSON with `links`) via `IDocsClient` /
`HttpDocsClient`. `meta.links` carries:

| Scope | Links attached |
|---|---|
| plan-finalize | `[{ targetType: "Goal", targetId: <goalId>, role: "Audit" }]` |
| per-task | the Goal link **plus** `{ targetType: "Task", targetId: <taskId>, role: "Audit" }` |

`targetType` and `role` are the PascalCase closed-enum wire forms per
andy-docs `docs-ref-contract.md`; `targetId` is the GUID in `D` format.
andy-docs does **not** validate the target exists — the trust boundary
stays with andy-policies. The call runs under andy-policies' M2M /
run-scoped bearer (the same `Andy.Auth.M2MClient.ServiceBearerHandler`
the RBAC/tasks clients use, per andy-docs Epic Y5).

## Idempotency

The attach is idempotent at andy-docs on
`(documentId, targetType, targetId, role)`, so re-uploading the same
assessment for the same `(goal, planVersion)` re-attaches rather than
duplicating. On the andy-policies side, the 5-minute evaluation
idempotency cache short-circuits a repeated `evaluate-task` /
`evaluate-plan` for the same key before the publisher is even invoked, so
a re-eval does not call out a second time.

## Best-effort / non-blocking (conservative invariant preserved)

Publishing is decoupled from the policy verdict. A `docs.put` failure (or
disabled publisher, or missing `AndyDocs:BaseUrl`) **never** blocks or
changes the decision the evaluator returns. Failures are logged with a
greppable source code and surfaced as a degraded-audit signal
(`ComplianceAuditPublishResult.Degraded`), never silently swallowed:

| Source code | Site |
|---|---|
| `[ANDY-POLICIES-DOCS-PUT-1]` | `HttpDocsClient` — non-2xx from andy-docs (status + bounded body). |
| `[ANDY-POLICIES-DOCS-PUT-2]` | `HttpDocsClient` — empty/undeserialisable `docs.put` body. |
| `[ANDY-POLICIES-AUDIT-DOCS-1]` | `ComplianceAuditPublisher` — assessment upload failed; whole publish degrades. |
| `[ANDY-POLICIES-AUDIT-DOCS-2]` | `ComplianceAuditPublisher` — audit-chain segment failed; assessment ref kept, segment degraded. |

If the assessment write succeeds but the chain segment fails, the result
keeps the assessment `DocsRef` and marks `Degraded` — the signal is not
lost, but the policy decision is still unaffected.

## Configuration

`ComplianceAudit` section (`appsettings.json`):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch. Ships **dark** — the publisher is wired but inert (returns `Skipped`) until an operator flips it on. |
| `IncludeAuditChainSegment` | `true` | Also attach the NDJSON audit-chain segment as a second `role:Audit` doc. |

`AndyDocs:BaseUrl` points at andy-docs (dev fallback
`http://localhost:5400`). Optional — embedded-only stacks that do not
ship andy-docs simply degrade best-effort.

## What Conductor consumes

The returned `DocsRef` (`{ documentId, linkId, versionHash, sizeBytes,
additionalLinkIds }`) is the join key #1946 uses. Conductor queries
`GET /api/links?targetType=Goal&targetId=<goalId>&role=Audit` (and
`Run`/`Task`) to find the audit doc(s) for a goal/run and link to them,
plus reads the assessment JSON for per-task compliance status. Because
agent artifacts have `ParentFolderId = null`, the by-target link query —
not tree browsing — is the discovery path.

## Where it's triggered

`IComplianceAuditPublisher` (impl `ComplianceAuditPublisher`) is invoked
from `PlanEvaluator`:

- `PublishPlanAsync` from the plan-finalize path (`EvaluatePlanAsync`,
  on cache miss);
- `PublishTaskAsync` from the per-task path (`EvaluateTaskAsync`, the
  #1944 surface, on cache miss).

## Tests

- **Unit** — `ComplianceAuditPublisherTests`: link tuples
  (Goal-only / Goal+Task), MIME types, the disabled skip path, the
  best-effort failure path (degrade without throwing), the
  assessment-only chain toggle, the assessment-degrades-on-chain-failure
  path, and the JSON round-trip back into the #1944 model.
- **Contract** — `HttpDocsClientContractTests` (WireMock): the multipart
  request shape (`file` + `meta` parts, PascalCase enum link values), the
  `DocsRef` response shape, `additionalLinkIds` propagation, and the
  `DocsPutException` source code on non-2xx.
- **Integration** — `ComplianceAuditInjectionTests`
  (`WebApplicationFactory` + capturing `IDocsClient`): the full chain
  (controller → `PlanEvaluator` → `ComplianceAuditPublisher` →
  `IDocsClient`) requests the right docs/links for plan and task evals,
  and the idempotency cache prevents a re-eval from re-publishing.
