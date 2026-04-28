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
- **Experimental scopes** — per-principal or per-cohort overrides with approver + expiry.
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
| Domain | `src/Andy.Policies.Domain` | `Policy`, `PolicyVersion`, dimension enums (`EnforcementLevel`, `Severity`, `LifecycleState`); `Binding` (P3), `ScopeNode` (P4), `Override` (P5), `AuditEvent` (P6), `Bundle` (P8) land with their respective epics |
| Application | `src/Andy.Policies.Application` | `IPolicyService`; per-epic interfaces (`IBindingService`, `IScopeService`, `IOverrideService`, `IAuditChain`, `IBundleService`) added by later stories |
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
