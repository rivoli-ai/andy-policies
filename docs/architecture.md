# Architecture

## Overview

Andy Policies follows Clean Architecture with the following layers:

```
┌─────────────────────────────────────────────┐
│                Angular SPA                   │
│           (client/ directory)                │
├─────────────────────────────────────────────┤
│              API Layer                       │
│    REST Controllers │ MCP Tools │ gRPC       │
├─────────────────────────────────────────────┤
│           Application Layer                  │
│       Interfaces │ DTOs │ Contracts          │
├─────────────────────────────────────────────┤
│         Infrastructure Layer                 │
│   EF Core │ Services │ External Integrations │
├─────────────────────────────────────────────┤
│            Domain Layer                      │
│         Entities │ Enums │ Value Objects      │
└─────────────────────────────────────────────┘
```

## Project Structure

| Layer | Project | Purpose |
|-------|---------|---------|
| Domain | `Andy.Policies.Domain` | Entities, enums, value objects |
| Application | `Andy.Policies.Application` | Interfaces, DTOs, contracts |
| Infrastructure | `Andy.Policies.Infrastructure` | EF Core, service implementations |
| API | `Andy.Policies.Api` | REST, MCP, gRPC endpoints |
| Shared | `Andy.Policies.Shared` | Shared types across projects |
| CLI | `Andy.Policies.Cli` | Command-line interface |

## API Protocols

### REST (Swagger)
Standard HTTP API with OpenAPI documentation available at `/swagger`.

### MCP (Model Context Protocol)
AI assistant integration endpoint at `/mcp`. MCP tools share the same service layer as REST controllers.

### gRPC
High-performance RPC defined in `Protos/items.proto`. Uses the same service layer.

## Database Strategy

- **PostgreSQL** (default): Used in standalone deployment
- **SQLite** (embedded): Used when bundled with Conductor

Configured via `Database:Provider` in appsettings or environment variable.

## Authentication Flow

```
User → Angular SPA → Andy Auth (OIDC) → JWT Token → API (Bearer Auth)
```

## External Dependencies

- **Andy Auth** (port 5001) - OAuth2/OIDC identity provider
- **Andy RBAC** (port 5003) - Role-based access control
- **Andy Settings** (port 5300) - Centralized configuration (optional)

## Audit store

Every catalog mutation — policy create / edit / publish / transition,
binding create / delete, scope create / delete, override propose /
approve / revoke / expire — inserts exactly one row into
`audit_events`. The table backs the tamper-evident hash chain
(P6.2+) that downstream compliance reporting and audit-export
flows consume. Reads are not audited.

The append-only invariant is enforced at the database layer, not in
application code:

- **Postgres** — a BEFORE UPDATE/DELETE/TRUNCATE trigger raises an
  exception. The two-user model — schema migrations run as
  `andy_policies_migrator` (owns the schema), runtime app connects
  as `andy_policies_app` (granted SELECT + INSERT only on
  `audit_events`) — means even a SQL-injection chain through the
  app role cannot mutate history. Single-user development boots fall
  back to the trigger-only mode with a documented degradation.
- **SQLite** — embedded mode is single-user by design; the trigger
  is the only enforcement mechanism. Two triggers (UPDATE, DELETE)
  raise `ABORT`. ADR 0006 documents the trade-off relative to
  Postgres.

The hash-chain algorithm (`hash[n] = SHA-256(hash[n-1] ||
canonicalJson(payload[n]))`) lands in P6.2 and is pinned in
ADR 0006. The genesis row's `prev_hash` is 32 zero bytes; chain
verification (P6.5) walks rows ordered by `seq` (the
database-assigned monotonic counter, not `Timestamp` — clocks can
skew, sequence cannot).
