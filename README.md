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
- **Experimental scopes** — per-principal or per-cohort overrides with approver + expiry; a periodic reaper transitions approved overrides past their `expiresAt` into the `Expired` state automatically (P5.3, [#53](https://github.com/rivoli-ai/andy-policies/issues/53)). Write operations (propose / approve / revoke) are gated behind the andy-settings toggle `andy.policies.experimentalOverridesEnabled` (default `false`); reads remain available regardless so the resolution algorithm keeps working when the toggle is off (P5.4, [#56](https://github.com/rivoli-ai/andy-policies/issues/56)).
- **Edit RBAC** — who may author, who must approve a publish. Subject→permission checks delegate to [Andy RBAC](https://github.com/rivoli-ai/andy-rbac); the edit matrix itself lives here.
- **Catalog change audit** — every edit, publish, transition, binding, and override recorded with actor, timestamp, structured field-level diff, required rationale, and tamper-evident chain. Reads are not audited.
- **Bundle pinning** — consumers pin a bundle version for reproducibility.

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
| Domain | `src/Andy.Policies.Domain` | `Policy`, `PolicyVersion`, `Binding` (P3.1, [#19](https://github.com/rivoli-ai/andy-policies/issues/19)), `ScopeNode` (P4.1, [#28](https://github.com/rivoli-ai/andy-policies/issues/28)), `Override` (P5.1, [#49](https://github.com/rivoli-ai/andy-policies/issues/49)), dimension enums (`EnforcementLevel`, `Severity`, `LifecycleState`, `BindingTargetType`, `BindStrength`, `ScopeType`, `OverrideScopeKind`, `OverrideEffect`, `OverrideState`); `AuditEvent` (P6), `Bundle` (P8) land with their respective epics |
| Application | `src/Andy.Policies.Application` | `IPolicyService`, `IBindingService`, `IScopeService`, `IOverrideService` (P5.2, [#52](https://github.com/rivoli-ai/andy-policies/issues/52)), `IRbacChecker`; remaining per-epic interfaces (`IAuditChain`, `IBundleService`) added by later stories |
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
