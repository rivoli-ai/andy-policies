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

## Ports

Per the ecosystem registry at [`../andy-service-template/docs/ports.md`](../andy-service-template/docs/ports.md). Three deployment modes; the same host can run any combination because each mode uses a distinct port range.

| Service | Mode 1 (dotnet) | Mode 2 (docker) | Mode 3 (Conductor) |
|---|---|---|---|
| API HTTPS | 5112 | 7112 | via proxy `/policies` |
| API HTTP | 5113 | 7113 | — |
| PostgreSQL | 5439 | 7439 | (SQLite embedded) |
| Angular client | 4206 | 6206 | via proxy `/policies` |

The docker-compose in this repo binds Mode 2 ports so it can coexist with a native `dotnet run` on Mode 1.

## Architecture

| Layer | Project | Purpose |
|-------|---------|---------|
| Domain | `Andy.Policies.Domain` | Policy, PolicyVersion, AuditEvent, Binding, Override — entities and enums |
| Application | `Andy.Policies.Application` | Interfaces, DTOs |
| Infrastructure | `Andy.Policies.Infrastructure` | EF Core, services |
| API | `Andy.Policies.Api` | REST, MCP, gRPC, auth |
| Shared | `Andy.Policies.Shared` | Shared types |
| CLI | `Andy.Policies.Cli` | Command-line tool |

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

**One-time prerequisite** (re-run when `andy-settings/src/Andy.Settings.Client/*`
changes):

```bash
bash ../andy-settings/scripts/pack-local.sh
```

Then:

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
