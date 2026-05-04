# Security

## Authentication

### Andy Auth (OAuth2/OIDC)

This service integrates with [Andy Auth](https://github.com/rivoli-ai/andy-auth) for authentication:

- **Protocol**: OAuth 2.0 Authorization Code with PKCE
- **Token format**: JWT Bearer tokens
- **Authority**: Configured via `AndyAuth:Authority`
- **Audience**: `urn:andy-policies-api`

### OAuth Client Registration

Two OAuth clients are registered in Andy Auth:

1. **`andy-policies-api`** (Confidential) - For service-to-service communication
2. **`andy-policies-web`** (Public) - For the Angular SPA

See `config/auth-seed.sql` for the seed data.

### Test User

- **Email**: `test@andy.local`
- **Password**: `Test123!`
- **Role**: User (with super-admin in RBAC)

## Authorization

### Andy RBAC

Role-based access control is provided by [Andy RBAC](https://github.com/rivoli-ai/andy-rbac):

- **Application code**: `andy-policies`
- **Roles**: admin, user, viewer
- **Actions**: read, write, delete, admin

See `config/rbac-seed.json` for the RBAC configuration.

## Transport Security

- **HTTPS everywhere**: TLS is enforced from development to production
- **Self-signed certs**: Generated automatically in Docker for development
- **Corporate CAs**: Supported via the `certs/` directory
- **Certificate injection**: At build time and runtime in Docker

## API Security

- **Swagger**: Bearer authentication scheme configured
- **MCP**: Requires authorization
- **gRPC**: Uses the same Bearer authentication
- **Health endpoint**: Unauthenticated (for load balancer probes)

## Best Practices

- Never commit secrets to the repository
- Use environment variables for sensitive configuration
- Rotate tokens and passwords regularly
- Review RBAC permissions periodically

## Audit chain — integrity, not non-repudiation

The catalog audit chain (Epic P6) is **tamper-evident** — every
mutation is hashed into a SHA-256 linear chain so any
post-hoc edit, deletion, or re-ordering of rows surfaces as a
verification failure (P6.5). The chain provides:

- **Integrity** ✓ — historical rows cannot be altered without detection.
- **Append-only at the DB layer** ✓ — Postgres triggers + role
  grants reject `UPDATE`/`DELETE` on `audit_events` even from
  the runtime app role.
- **Auditable export** ✓ — NDJSON bundles round-trip through
  the offline verifier (P6.5 / P6.7) byte-for-byte.

The chain explicitly does **not** provide:

- **Non-repudiation** ✗ — there is no detached cryptographic
  signature on the export. The recorded `actorSubjectId` is the
  JWT `sub` claim at write time, but the chain alone cannot
  prove who held that subject identity. Cross-reference Andy
  Auth (the IdP) when the question is "who was Alice on
  2026-04-21".
- **Confidentiality** ✗ — the chain is plaintext storage.
  PII / secrets must be excluded from auditable DTOs via
  `[AuditIgnore]` (drops the field) or `[AuditRedact]`
  (records a `"***"` placeholder); the chain still hashes the
  redacted value, so the *fact* of mutation is preserved
  without leaking the value (P6.3, P6.9).

If your deployment requires non-repudiation, raise it as an
amendment to [ADR 0006 — Audit hash chain](adr/0006-audit-hash-chain.md);
it is an explicit non-goal of v1. The
[compliance officer runbook](runbooks/audit-compliance.md)
walks through the verification, export, and incident-handling
flows in detail.
