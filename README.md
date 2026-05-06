# Andy Policies

> **ALPHA RELEASE WARNING**
>
> This software is in ALPHA stage. **NO GUARANTEES** are made about its functionality, stability, or safety.
>
> **CRITICAL WARNINGS:**
> - APIs, schemas, and storage formats may change without notice between releases.
> - Audit chain, RBAC enforcement, and lifecycle transitions are **NOT FULLY TESTED** for production-grade compliance use.
> - **DO NOT USE** in production environments.
> - **DO NOT USE** as the sole source of truth for regulated or safety-critical decisions.
>
> Track stabilisation progress in the [Epic P-series issues](https://github.com/rivoli-ai/andy-policies/issues?q=is%3Aissue+label%3Aroadmap%3Asimulator-parity).

**Status:** Alpha (pre-release; under active development)
**Last Updated:** 2026-04-25

Governance policy catalog for the Andy ecosystem. A versioned registry of structured policy documents with lifecycle state, ownership, and audit trail. Consumed by [Conductor](https://github.com/rivoli-ai/conductor) for story admission, work verification, and compliance reporting.

**Scope: content only.** This service defines *what* policies are; enforcement (evaluating runs against policies, gating actions, writing decision logs) lives in consumers.

## What this service owns

- **Versioned policy documents** — structured envelope with immutable published versions.
- **Lifecycle states** — `draft` / `active` / `winding-down` / `retired`.
- **Bindings as metadata** — policy ↔ story template / repo / scope as structured data; no evaluation.
- **Experimental scopes** — per-principal or per-cohort overrides with approver + expiry; a periodic reaper transitions approved overrides past their `expiresAt` into the `Expired` state automatically (P5.3, [#53](https://github.com/rivoli-ai/andy-policies/issues/53)). Write operations (propose / approve / revoke) are gated behind the andy-settings toggle `andy.policies.experimentalOverridesEnabled` (default `false`); reads remain available regardless so the resolution algorithm keeps working when the toggle is off (P5.4, [#56](https://github.com/rivoli-ai/andy-policies/issues/56)). See [Override concepts](docs/concepts/overrides.md), [ADR 0005 — Overrides](docs/adr/0005-overrides.md), and the [approver](docs/runbooks/override-approver.md) + [operator](docs/runbooks/override-operator.md) runbooks.
- **Edit RBAC** — who may author, who must approve a publish. Subject→permission checks delegate to [Andy RBAC](https://github.com/rivoli-ai/andy-rbac); the edit matrix itself lives here.
- **Catalog change audit** — every edit, publish, transition, binding, and override recorded with actor, timestamp, structured field-level diff, required rationale, and tamper-evident chain. Reads are not audited. See the [audit envelope spec](docs/audit-envelope.md), [ADR 0006 — Audit hash chain](docs/adr/0006-audit-hash-chain.md), and the [compliance officer runbook](docs/runbooks/audit-compliance.md).
- **Bundle pinning** — consumers pin a bundle version for reproducibility. See [ADR 0008](docs/adr/0008-bundle-pinning.md) for the architectural commitments and the [consumer integration guide](docs/guides/consumer-integration-bundles.md) for the adoption recipe.

## What this service does NOT own

- Evaluation of whether a run violates a policy.
- Enforcement hooks (admission checks, verification gates).
- Decision logging — that lives at the enforcement point (e.g. Conductor).

## Policy dimensions

Every policy carries three independent axes:

- **Enforcement** — `MUST` / `SHOULD` / `MAY` (RFC 2119). Consumers interpret.
- **Severity** — `info` / `moderate` / `critical`. Blast radius.
- **Scope** — hierarchy (org → tenant → team → repo → template → run); stricter scope can tighten, not loosen, without an explicit override.

## Quick Start

```bash
# Full stack (PostgreSQL + API + Angular SPA)
docker compose up -d

# Admin UI
open http://localhost:6206
```

### Lifecycle endpoints

Three action-shaped endpoints drive `PolicyVersion` lifecycle transitions
(P2.3, [#13](https://github.com/rivoli-ai/andy-policies/issues/13)). All three
require a `rationale` body and a Bearer token; they share
`ILifecycleTransitionService` with the MCP / gRPC / CLI surfaces.

```http
POST /api/policies/{id}/versions/{versionId}/publish
POST /api/policies/{id}/versions/{versionId}/winding-down
POST /api/policies/{id}/versions/{versionId}/retire
Content-Type: application/json
Authorization: Bearer <jwt>

{ "rationale": "promote v3 — passed canary" }
```

Publishing auto-supersedes the prior `Active` version inside the same DB
transaction (it transitions to `WindingDown` with `SupersededByVersionId` set
to the new version). Disallowed transitions return `409 Conflict`; an empty
rationale returns `400 Bad Request`; missing or unknown ids return `404`.

The `rationale` field is enforced when the andy-settings toggle
`andy.policies.rationaleRequired` is on (default; P2.4,
[#14](https://github.com/rivoli-ai/andy-policies/issues/14)). Operators flip
the toggle in the andy-settings admin UI; the live process picks it up on the
next snapshot refresh (default 60s) without a restart. When the toggle is on
and rationale is empty, the API returns
`400` with `type=/problems/rationale-required` and `errors.rationale`
populated; the current toggle value is exported as the OpenTelemetry gauge
`andy_policies_rationale_required_toggle_value` (1 = on, 0 = off).

The same lifecycle operations are exposed to LLM agents through the MCP
endpoint at `/mcp` (P2.5,
[#15](https://github.com/rivoli-ai/andy-policies/issues/15)):
`policy.version.publish` (Draft → Active shortcut),
`policy.version.transition` with a case-insensitive `targetState`, and the
read-only `policy.lifecycle.matrix`. All three delegate to the same
`ILifecycleTransitionService` as REST, so wire behavior — auto-supersede,
rationale enforcement, the four-edge state matrix — is identical across
surfaces.

The gRPC `LifecycleService` (P2.6,
[#16](https://github.com/rivoli-ai/andy-policies/issues/16)) lives in
`lifecycle.proto` alongside the existing `policies.proto` (same
`andy_policies` package + `Andy.Policies.Api.Protos` namespace) and exposes
`PublishVersion`, `TransitionVersion`, and `GetMatrix` with the same
delegation. Service exceptions map to gRPC status codes:
`RationaleRequiredException`/`ValidationException` → `InvalidArgument`,
`NotFoundException` → `NotFound`, `InvalidLifecycleTransitionException` →
`FailedPrecondition`, `ConcurrentPublishException` → `Aborted`.

The CLI (`andy-policies-cli versions {publish,wind-down,retire}`, P2.7,
[#17](https://github.com/rivoli-ai/andy-policies/issues/17)) drives the same
REST endpoints with a `--rationale` / `-r` flag:

```bash
andy-policies-cli versions publish   <policyIdOrName> <versionId> -r "promote v3"
andy-policies-cli versions wind-down <policyIdOrName> <versionId> -r "sunset"
andy-policies-cli versions retire    <policyIdOrName> <versionId> -r "tomb"
```

Exit codes follow the federated-CLI contract from Conductor Epic AN: `0`
success, `1` transport / generic, `3` auth (401/403), `4` not found, `5`
conflict (409/412 — covers invalid-transition).

For the full design — state diagram, transition matrix, only-one-Active
invariant, the auto-supersede atomicity argument, and surface parity table
— see [`docs/design/lifecycle.md`](docs/design/lifecycle.md). For the
*why* behind each design decision (four states, DB-level uniqueness,
serializable transactions, in-process events), see [ADR 0002 — Lifecycle
states](docs/adr/0002-lifecycle-states.md).

### Binding endpoints

REST surface for `Binding` mutation, single-id read, and target-side query
(P3.3, [#21](https://github.com/rivoli-ai/andy-policies/issues/21)).
Bindings are metadata-only links between an immutable `PolicyVersion` and
a foreign target (template, repo, scope node, tenant, org); andy-policies
never resolves the target against the foreign system.

```http
POST   /api/bindings                                              # create
GET    /api/bindings/{id}                                          # read
DELETE /api/bindings/{id}?rationale=...                            # soft-delete
GET    /api/bindings?targetType=Repo&targetRef=repo:org/name       # exact-match query
GET    /api/policies/{id}/versions/{vId}/bindings?includeDeleted=  # version-rooted list
```

Create returns 201 with `Location: /api/bindings/{id}`. Bindings to a
`Retired` version are refused with 409 (P3.2 service contract). Delete is
soft — the row stays for the audit chain (P6); a second delete returns
404. Target-side query is exact-equality on `(targetType, targetRef)`,
no case-folding.

#### Resolving bindings (exact match)

```http
GET /api/bindings/resolve?targetType=Template&targetRef=template:abc
```

`resolve` is the consumer-facing read for "what policies apply to this
target?" (P3.4, [#22](https://github.com/rivoli-ai/andy-policies/issues/22)).
It joins each binding to its `Policy` and `PolicyVersion` so callers get
policy name, version state, enforcement, severity, and scopes in one
round-trip. Retired versions are filtered out; same-target/same-version
duplicates dedup with `Mandatory > Recommended` (tiebreak earliest
`CreatedAt`); the result is ordered by policy name ASC, then version
number DESC. **Exact-match only — no hierarchy walk.** That lands in P4.

The same operations are exposed to LLM agents through the MCP endpoint at
`/mcp` (P3.5, [#23](https://github.com/rivoli-ai/andy-policies/issues/23)):
`policy.binding.list`, `policy.binding.create`, `policy.binding.delete`,
and `policy.binding.resolve`. All four delegate to the same
`IBindingService` / `IBindingResolver` as REST, so retired-version
refusal, soft-delete semantics, and the dedup/order rules behave
identically across surfaces. Errors come back as prefixed codes the
gateway can route on (`policy.binding.not_found`,
`policy.binding.retired_target`, `policy.binding.invalid_target`).

The gRPC `BindingService` (P3.6,
[#24](https://github.com/rivoli-ai/andy-policies/issues/24)) lives in
`bindings.proto` alongside the existing `policies.proto`,
`lifecycle.proto` (same `andy_policies` package +
`Andy.Policies.Api.Protos` namespace) and exposes six RPCs:
`CreateBinding`, `DeleteBinding`, `GetBinding`,
`ListBindingsByPolicyVersion`, `ListBindingsByTarget`, `ResolveBindings`.
`TargetType` and `BindStrength` are proto3 enums (UNSPECIFIED rejected as
`InvalidArgument`); state/enforcement/severity stay strings on
`ResolvedBindingMessage` so proto evolution doesn't couple to internal
enum renames. Service exceptions map:
`BindingRetiredVersionException` → `FailedPrecondition`,
`ConflictException` → `AlreadyExists`, `ValidationException` →
`InvalidArgument`, `NotFoundException` → `NotFound`.

The CLI exposes the same operations via
`andy-policies-cli bindings {list,create,delete,resolve}` (P3.7,
[#25](https://github.com/rivoli-ai/andy-policies/issues/25)):

```bash
# List by version (with optional --include-deleted) or by target
andy-policies-cli bindings list --policy-version-id <vid>
andy-policies-cli bindings list --target-type Repo --target-ref repo:org/name

# Create
andy-policies-cli bindings create \
  --policy-version-id <vid> \
  --target-type Template --target-ref template:abc \
  --bind-strength Mandatory

# Soft-delete with optional rationale
andy-policies-cli bindings delete <bindingId> -r "replaced by v3"

# Joined resolve (filters Retired, dedups Mandatory>Recommended)
andy-policies-cli bindings resolve --target-type Template --target-ref template:abc --output json
```

Exit codes follow the federated-CLI contract from Conductor Epic AN: `0`
success, `2` bad arguments (missing filter on `list`), `3` auth (401/403),
`4` not found, `5` conflict (covers `BindingRetiredVersionException`).

### Scope endpoints

REST surface for the hierarchical scope tree (P4.5,
[#33](https://github.com/rivoli-ai/andy-policies/issues/33)). Six
endpoints sit on top of the `IScopeService` (P4.2,
[#29](https://github.com/rivoli-ai/andy-policies/issues/29)) +
`IBindingResolutionService` (P4.3,
[#30](https://github.com/rivoli-ai/andy-policies/issues/30)):

```http
GET    /api/scopes?type=Tenant                       # list (optional type filter)
GET    /api/scopes/tree                              # full forest, nested
GET    /api/scopes/{id}                              # single node
GET    /api/scopes/{id}/effective-policies            # tighten-only resolved set
POST   /api/scopes                                    # create (canonical ladder enforced)
DELETE /api/scopes/{id}                               # leaf-only delete
```

Create enforces the `Org → Tenant → Team → Repo → Template → Run`
ladder; mismatched parent type returns `400` with
`errorCode=scope.parent-type-mismatch`. Duplicate `(Type, Ref)` returns
`409` with `errorCode=scope.ref-conflict`. Deleting a non-leaf returns
`409` with `errorCode=scope.has-descendants` and `childCount` in the
ProblemDetails extensions. Effective-policies resolution filters
Retired versions, dedups same-target/same-version pairs preferring
`Mandatory`, and orders mandatories first. Write-time tighten-only
validation (P4.4,
[#32](https://github.com/rivoli-ai/andy-policies/issues/32)) refuses to
commit a `Recommended` binding that would shadow an upstream
`Mandatory`; the `409` response carries `offendingAncestorBindingId`
and `offendingScopeNodeId` so admins can triage from the error.

The same scope operations are exposed across MCP, gRPC, and CLI
(P4.6, [#34](https://github.com/rivoli-ai/andy-policies/issues/34)),
all delegating to the same `IScopeService` + `IBindingResolutionService`
as REST. **MCP**: `policy.scope.{list,get,tree,create,delete,effective}`
return formatted strings or JSON envelopes with prefixed error codes
(`policy.scope.{not_found,parent_type_mismatch,ref_conflict,has_descendants,invalid_input}`).
**gRPC**: `andy_policies.ScopesService` exposes six RPCs in
`scopes.proto`; service exceptions map to `FailedPrecondition`
(ladder violation / has-descendants), `AlreadyExists` (ref conflict),
`NotFound` (missing parent or scope), and `InvalidArgument` (bad
GUID / `SCOPE_TYPE_UNSPECIFIED`). **CLI**:
`andy-policies-cli scopes {list,get,tree,create,delete,effective}`
follows the same federated-CLI exit-code contract as `bindings` and
`versions` (0 success / 1 transport / 3 auth / 4 not-found /
5 conflict).

#### Performance budget

The hierarchy-aware resolver targets **p99 < 50 ms** for the full
chain walk (P4.7,
[#36](https://github.com/rivoli-ai/andy-policies/issues/36)). The
`ScopeWalkPerfTests` Postgres-testcontainer suite seeds a 6-level
tree with 200+ bindings sprinkled along the chain and runs each hot
path 100 times: `GetAncestorsAsync` p99 ≤ 50 ms, `ResolveForScopeAsync`
p99 ≤ 150 ms (50 ms target plus headroom for noisy CI runners). The
suite is tagged `Category=Perf` so PR CI can skip via filter; nightly
sweeps run the budget check.

For the full design — canonical `TargetRef` shapes, retired-version
refusal, soft-delete tombstone, dedup rules on resolve, surface parity
table, and concurrency model — see [`docs/design/bindings.md`](docs/design/bindings.md).
The `BindingCrossSurfaceParityTests` integration suite (P3.8,
[#26](https://github.com/rivoli-ai/andy-policies/issues/26)) asserts
that REST, MCP, and gRPC `resolve` return identical results for a shared
fixture; `BindingConcurrencyStressTests` exercises 50-way parallel
create/delete workloads against Postgres without deadlocks. For the
*why* behind the design (metadata-only firewall, soft-delete to
preserve audit, two-valued bind-strength, exact-match before P4
hierarchy walk), see [ADR 0003 — Bindings are content-only metadata](docs/adr/0003-bindings.md).
A step-by-step guide for services that consume the binding surface
lives at [`docs/guides/consumer-integration-bindings.md`](docs/guides/consumer-integration-bindings.md).

### Scope hierarchy

The 6-level `Org → Tenant → Team → Repo → Template → Run` graph that
hierarchy-aware reads walk over (Epic P4,
[#4](https://github.com/rivoli-ai/andy-policies/issues/4)). For the
storage shape, walk paths, and surface parity table, see
[`docs/design/scope-hierarchy.md`](docs/design/scope-hierarchy.md).
For the stricter-tightens-only resolution algorithm with three worked
examples (simple cascade, upgrade at leaf, forbidden downgrade), see
[`docs/design/resolution-algorithm.md`](docs/design/resolution-algorithm.md).
For the *why* behind each design decision (typed 6-level, materialized
path, dual write+read enforcement, structural cycle-impossibility),
see [ADR 0004 — Scope hierarchy + tighten-only resolution](docs/adr/0004-scope-hierarchy.md).

### Override endpoints

REST surface for `Override` propose/approve/revoke + list/get/active
(P5.5, [#58](https://github.com/rivoli-ai/andy-policies/issues/58)).
Six endpoints sit on top of `IOverrideService`:

```http
POST  /api/overrides                                              # propose → 201
POST  /api/overrides/{id}/approve                                 # approve → 200
POST  /api/overrides/{id}/revoke                                  # revoke → 200
GET   /api/overrides?state=&scopeKind=&scopeRef=&policyVersionId= # list
GET   /api/overrides/{id}                                          # get → 200/404
GET   /api/overrides/active?scopeKind=&scopeRef=                   # effective set
```

The three writes carry `[OverrideWriteGate]` (P5.4, [#56](https://github.com/rivoli-ai/andy-policies/issues/56))
— when `andy.policies.experimentalOverridesEnabled` is `false` they
return 403 with `errorCode: override.disabled`. Reads bypass the gate
so the resolution algorithm (P4.3) keeps working when the toggle is
off. `GET /api/overrides/active` returns only rows where
`State == Approved` AND `ExpiresAt > now`; expired rows are excluded
even before the next reaper tick.

Approve-by-proposer returns 403 with `errorCode: override.self_approval_forbidden`
(distinct from a generic 403 so MCP/gRPC/CLI can mirror the same
contract). Revoke requires a non-empty `RevocationReason`. The reaper
(P5.3, [#53](https://github.com/rivoli-ai/andy-policies/issues/53))
is the only path into the `Expired` state — explicit revocation goes
to `Revoked`.

The same operations are exposed to LLM agents through the MCP
endpoint at `/mcp` (P5.6, [#59](https://github.com/rivoli-ai/andy-policies/issues/59)):
`policy.override.propose`, `policy.override.approve`,
`policy.override.revoke`, `policy.override.list`, `policy.override.get`,
and `policy.override.active`. All six delegate to the same
`IOverrideService` as REST, so gate semantics, self-approval rejection,
state-machine enforcement, and `active` time-gating behave identically
across surfaces. Errors come back as prefixed codes the gateway can
route on (`policy.override.disabled`,
`policy.override.self_approval_forbidden`,
`policy.override.invalid_state`, `policy.override.not_found`,
`policy.override.invalid_argument`, `policy.override.rbac_denied`),
matching the REST `errorCode` extension members from P5.5.

The gRPC `OverridesService` (P5.7,
[#60](https://github.com/rivoli-ai/andy-policies/issues/60)) lives in
`overrides.proto` alongside the existing service protos (same
`andy_policies` package + `Andy.Policies.Api.Protos` namespace) and
exposes six RPCs: `ProposeOverride`, `ApproveOverride`,
`RevokeOverride`, `ListOverrides`, `GetOverride`,
`GetActiveOverrides`. `ProtoScopeKind`, `ProtoEffectKind`, and
`ProtoOverrideState` are proto3 enums (UNSPECIFIED rejected as
`InvalidArgument`); subject ids and timestamps travel as strings on
`OverrideMessage` so proto evolution doesn't couple to internal
renames. Surface-parity error contracts: `PERMISSION_DENIED` carries
trailer `override_disabled=1` when the gate is off and
`reason=self_approval` or `reason=forbidden` for self-approval / RBAC
denials.

`andy-policies-cli overrides {propose,approve,revoke,list,get,active}`
(P5.7, [#60](https://github.com/rivoli-ai/andy-policies/issues/60))
talks to the REST surface and inherits its auth + gate enforcement.
`--expires-at` accepts ISO 8601 (`2026-05-01T00:00:00Z`) or relative
durations (`+30d`, `+8h`, `+45m`); past values fail-fast at the CLI
layer with a clear stderr message before round-tripping to the
server.

### Audit chain verification

REST endpoint for verifying the catalog audit hash chain (P6.5,
[#45](https://github.com/rivoli-ai/andy-policies/issues/45)):

```http
GET /api/audit/verify?fromSeq=&toSeq=
```

Returns 200 with a `ChainVerificationDto` (`{ valid,
firstDivergenceSeq, inspectedCount, lastSeq }`) regardless of
outcome — divergence is a queryable state, not an HTTP error.
400 ProblemDetails (`type=/problems/audit-verify-range`,
`errorCode=audit.verify.invalid_range`) on \`fromSeq > toSeq\` or
non-positive bounds.

The CLI mirrors the endpoint with both live and offline modes:

```bash
# Live — hits /api/audit/verify on the configured server
andy-policies-cli audit verify --from 1 --to 10000

# Offline — re-runs the chain verifier locally against an
# NDJSON export. Required for external auditors handed an
# archival copy; uses the same Shared CanonicalJson +
# AuditEnvelopeHasher the live writer uses, so live and
# offline produce byte-identical results.
andy-policies-cli audit verify --file ./audit-export.ndjson
```

Exit code 0 on a valid chain, 6 (\`AuditDivergence\`) on
divergence — distinct from 1 (transport) so cron / CI jobs can
branch on "the chain itself is broken" vs "the API is unreachable."

### Audit query

Cursor-paginated query over the catalog audit chain (P6.6,
[#46](https://github.com/rivoli-ai/andy-policies/issues/46)):

```http
GET /api/audit?actor=&from=&to=&entityType=&entityId=&action=&cursor=&pageSize=
```

Returns 200 with `AuditPageDto` (`{ items, nextCursor, pageSize }`).
Filters are AND'd; cursor is opaque base64 from a previous page's
`nextCursor`. Default `pageSize` 50, max 500. Hashes travel as
lowercase hex (`prevHashHex`, `hashHex`); `fieldDiff` travels
as a parsed JSON Patch array, not a JSON-encoded string.

Cursor pagination over offset is non-negotiable here: the audit
table is append-only and grows monotonically — offset windows
would shift under concurrent inserts, and skipping the head of
a million-row table is a scan, not a seek.

The same operations are exposed to LLM agents through MCP
(P6.7, [#48](https://github.com/rivoli-ai/andy-policies/issues/48)):
`policy.audit.list`, `policy.audit.get`, `policy.audit.verify`,
and `policy.audit.export`. All four delegate to the same
`IAuditQuery` / `IAuditChain` / `IAuditExporter` services as
REST. Errors come back as prefixed codes
(`policy.audit.invalid_argument`, `policy.audit.not_found`).
`policy.audit.export` returns a base64-encoded UTF-8 NDJSON
bundle: one `"type":"event"` line per audit row plus a trailing
`"type":"summary"` line carrying `fromSeq`, `toSeq`, `count`,
`genesisPrevHashHex`, and `terminalHashHex`. The bundle is
verifiable offline by `andy-policies-cli audit verify --file`
(P6.5) — integrity rests on the embedded hash chain; v1 ships
no external KMS / detached signature.

The gRPC `AuditService` (P6.8,
[#50](https://github.com/rivoli-ai/andy-policies/issues/50)) lives
in `audit.proto` alongside the existing service protos and exposes
four RPCs: `ListAudit`, `GetAudit`, `VerifyAudit`,
`ExportAudit` (server-streaming). Status mapping: `INVALID_ARGUMENT`
for bad inputs (page size, GUID, range, cursor); `NOT_FOUND` for
unknown ids. Export streams ≥16 KiB chunks; concatenating chunks
yields a bundle byte-identical to the MCP / REST exports.

The CLI mirrors the gRPC + REST surfaces with four verbs:

```bash
andy-policies-cli audit list --actor user:alice --entity-type Policy
andy-policies-cli audit get <event-guid>
andy-policies-cli audit verify [--from N] [--to M] [--file export.ndjson]
andy-policies-cli audit export --out audit.ndjson
```

## Edit RBAC

Subject→permission evaluation delegates to [Andy RBAC](https://github.com/rivoli-ai/andy-rbac); the permission *vocabulary* lives here in [`config/registration.json`](config/registration.json) (`rbac` block) and is mirrored in [`config/rbac-seed.json`](config/rbac-seed.json) for direct seeding. andy-rbac upserts the application, resource types, permissions, and role↔permission edges from this manifest on first boot.

The runtime adapter is `HttpRbacChecker` (P7.2, [#51](https://github.com/rivoli-ai/andy-policies/issues/51)) — a typed `HttpClient` that calls `POST {AndyRbac:BaseUrl}/api/check` with a 3-second timeout and a 60-second in-memory cache for successful decisions. **Fail-closed by default**: transport errors, timeouts, and non-2xx responses all collapse to `Allowed=false` so a governance catalog never opens up under adversity. The fail-closed branch is *not* cached, so a recovered andy-rbac is picked up on the very next call.

**Author cannot self-approve** (P7.3, [#55](https://github.com/rivoli-ai/andy-policies/issues/55)). The publish endpoint rejects when the actor matches the version's `ProposerSubjectId` — even when andy-rbac would say *yes* to both `:author` and `:publish`. Domain invariant; admin override is deliberately absent. Returns **HTTP 403** with ProblemDetails `errorCode = "policy.publish_self_approval_forbidden"`, no state mutation. `WindingDown` and `Retire` are administrative hygiene transitions and do not apply this check.

**Per-action policy attributes** (P7.4, [#57](https://github.com/rivoli-ai/andy-policies/issues/57)). Every REST controller action carries `[Authorize(Policy = "andy-policies:…")]` naming a permission code from the catalog above. `RbacAuthorizationHandler` extracts the subject id (`sub` / `NameIdentifier` / `Identity.Name`), the JWT `groups` claim, and a route-derived resource instance (`{type}:{routeId}`), then delegates to `IRbacChecker`. A denied or fail-closed decision means the action returns 403; an allowed decision lets the request proceed. MCP and gRPC interceptors (P7.6) reuse the same handler.

**Test harness** (P7.5, [#61](https://github.com/rivoli-ai/andy-policies/issues/61)). `tests/Andy.Policies.Tests.Integration/Fixtures/RbacStubFixture` starts a `WireMockServer` with a default-deny catch-all; tests call `Allow(subject, permission, instance?)`, `Deny(...)`, or `SimulateOutage()` to drive the **real** `HttpRbacChecker` end-to-end. `RbacTestApplicationFactory` rewrites `AndyRbac:BaseUrl` to the stub's port — unlike `PoliciesApiFactory`, it does *not* swap in an allow-all stub, so the full `[Authorize(Policy=…)] → handler → checker → wire` path is under test.

### Permission catalog

| Code | Resource | Held by |
|---|---|---|
| `andy-policies:policy:read`        | policy   | author, approver, risk, viewer |
| `andy-policies:policy:author`      | policy   | author |
| `andy-policies:policy:publish`     | policy   | approver |
| `andy-policies:policy:transition`  | policy   | approver |
| `andy-policies:binding:read`       | binding  | author, approver, risk, viewer |
| `andy-policies:binding:manage`     | binding  | approver |
| `andy-policies:scope:read`         | scope    | author, approver, risk, viewer |
| `andy-policies:scope:manage`       | scope    | (admin only) |
| `andy-policies:override:read`      | override | author, approver, risk, viewer |
| `andy-policies:override:propose`   | override | author |
| `andy-policies:override:approve`   | override | approver |
| `andy-policies:override:revoke`    | override | approver |
| `andy-policies:bundle:read`        | bundle   | author, approver, risk, viewer |
| `andy-policies:bundle:create`      | bundle   | approver |
| `andy-policies:bundle:delete`      | bundle   | (admin only) |
| `andy-policies:audit:read`         | audit    | author, approver, risk |
| `andy-policies:audit:export`       | audit    | risk |
| `andy-policies:audit:verify`       | audit    | risk |

`admin` holds the `*` wildcard. Authoritative source: `config/registration.json`.

**Reference docs** (P7.7, [#65](https://github.com/rivoli-ai/andy-policies/issues/65)):

- [Permission catalog](docs/reference/permission-catalog.md) — auto-generated from `config/registration.json` via `dotnet run --project tools/GenerateRbacDocs`. CI runs the generator with `--check` to fail builds on drift between the manifest and the published page.
- [Edit matrix](docs/design/edit-matrix.md) — one row per catalog mutation across P1–P8 with permission code, author/approver flags, and the self-approval column flagging where a single caller acting in both roles is forbidden.
- [ADR 0007 — Edit RBAC](docs/adr/0007-edit-rbac.md) — delegation to andy-rbac, fail-closed default, the author-cannot-self-approve domain invariant, and the per-service `client_credentials` S2S contract.

## Ports

Per the ecosystem registry at [`../andy-service-template/docs/ports.md`](../andy-service-template/docs/ports.md). Three deployment modes; the same host can run any combination because each mode uses a distinct port range.

| Service | Mode 1 (dotnet) | Mode 2 (docker) | Mode 3 (Conductor) |
|---|---|---|---|
| API HTTPS | 5112 | 7112 | via proxy `/policies` |
| API HTTP | 5113 | 7113 | — |
| PostgreSQL | 5439 | 7439 | (SQLite embedded) |
| Angular client | 4206 | 6206 | via proxy `/policies` |

The docker-compose in this repo binds Mode 2 ports so it can coexist with a native `dotnet run` on Mode 1.

## Project Structure

| Layer | Project | Entities / responsibilities |
|-------|---------|-----------------------------|
| Domain | `src/Andy.Policies.Domain` | `Policy`, `PolicyVersion`, `Binding` (P3.1, [#19](https://github.com/rivoli-ai/andy-policies/issues/19)), `ScopeNode` (P4.1, [#28](https://github.com/rivoli-ai/andy-policies/issues/28)), `Override` (P5.1, [#49](https://github.com/rivoli-ai/andy-policies/issues/49)), `AuditEvent` (P6.1, [#41](https://github.com/rivoli-ai/andy-policies/issues/41) — append-only via DB triggers + role grants), dimension enums (`EnforcementLevel`, `Severity`, `LifecycleState`, `BindingTargetType`, `BindStrength`, `ScopeType`, `OverrideScopeKind`, `OverrideEffect`, `OverrideState`); `Bundle` (P8) lands with its respective epic |
| Application | `src/Andy.Policies.Application` | `IPolicyService`, `IBindingService`, `IScopeService`, `IOverrideService` (P5.2, [#52](https://github.com/rivoli-ai/andy-policies/issues/52)), `IRbacChecker`, `IAuditChain` + `IAuditDiffGenerator` (P6.2/P6.3, [#42](https://github.com/rivoli-ai/andy-policies/issues/42)/[#43](https://github.com/rivoli-ai/andy-policies/issues/43)); `IBundleService` lands with its respective epic |
| Infrastructure | `src/Andy.Policies.Infrastructure` | EF Core (`AppDbContext` + migrations), `PolicyService` implementation, `PolicySeeder` for the six stock policies, andy-rbac / andy-settings adapters |
| API | `src/Andy.Policies.Api` | REST controllers, MCP tools, gRPC services, OIDC/JWT auth, OpenAPI generation, OpenTelemetry wiring |
| Shared | `src/Andy.Policies.Shared` | Cross-project DTOs, common enums |
| CLI | `tools/Andy.Policies.Cli` | `policies` and `versions` subcommands; thin REST client over the API |

## Core concepts

- **Policy + PolicyVersion split** — stable identity on `Policy`; immutable, version-monotonic content on `PolicyVersion`.
- **Three orthogonal dimensions** per version — Enforcement (RFC 2119), Severity (triage tier), Scope (applicability tags).
- **Lifecycle** — every version starts as `Draft`; promotion to `Active` lives in Epic P2.
- **Six stock policies** seeded at boot (`read-only`, `write-branch`, `sandboxed`, `draft-only`, `no-prod`, `high-risk`).
- **Four parity surfaces** — REST, MCP, gRPC, CLI — all backed by a single `IPolicyService` instance, asserted by `CrossSurfaceParityTests`.

For the full design, including aggregate diagrams, dimension wire formats, the rules DSL, versioning invariants, and the four-surface access table, see [`docs/design/policy-document-core.md`](docs/design/policy-document-core.md). For the *why* behind the aggregate split, see [ADR 0001 — Policy versioning](docs/adr/0001-policy-versioning.md).

## Development

```bash
# Start infrastructure only (Mode 2 postgres on 7439)
docker compose up -d postgres

# Run the API natively (Mode 1 on 5112/5113)
dotnet run --project src/Andy.Policies.Api

# Run the client natively (Mode 1 on 4206)
cd client && npm install && npm start
```

## Testing

```bash
dotnet test
cd client && npm test -- --watch=false --browsers=ChromeHeadless
```

### End-to-end smoke test (full ecosystem stack)

Brings up andy-auth + andy-rbac + andy-settings + andy-policies (each with its
own Postgres) and proves the OAuth client + RBAC application + settings
definitions in `config/registration.json` round-trip end-to-end. Skipped
silently unless `E2E_ENABLED=1`.

```bash
docker compose -f docker-compose.e2e.yml up -d --build
E2E_ENABLED=1 dotnet test tests/Andy.Policies.Tests.E2E
docker compose -f docker-compose.e2e.yml down -v
```

First build is slow (8 images: 4 services × build + 4 Postgres pulls).
Subsequent rebuilds are incremental.

## Docker modes

```bash
# Full stack — PostgreSQL + API + Angular client (nginx)
docker compose up -d

# Embedded mode — SQLite, for Conductor bundling
docker compose -f docker-compose.embedded.yml up -d
```

## Documentation

Full documentation at [rivoli-ai.github.io/andy-policies](https://rivoli-ai.github.io/andy-policies/).

## License

Apache 2.0 — see [LICENSE](LICENSE).

Copyright (c) Rivoli AI 2026
