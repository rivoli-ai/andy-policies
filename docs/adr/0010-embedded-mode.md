# ADR 0010 — Embedded mode (Conductor bundled deployment)

## Status

**Accepted** — 2026-05-06. Drafted retroactively after the P10.1–P10.4 implementation stories landed (P10.1 #31, P10.2 #35, P10.3 #38, P10.4 #39 already merged on `main`). The decisions captured here describe the implementation as shipped; future divergence requires a follow-up ADR.

Related: ADR 0007 edit-rbac (#56 — provides the per-resource permission codes the manifest registers), ADR 0008 bundle-pinning (#88 — the bundle pinning gate is the production read path that the cross-service smoke exercises).

## Context

Andy-policies must be consumable as a "batteries-included" component inside Conductor, not only as a standalone Mode 1 (`dotnet run`) or Mode 2 (`docker compose up`) service. The Conductor bundled deployment (Mode 3) targets a single host where one operator runs `docker compose -f docker-compose.embedded.yml up -d` and gets a self-contained governance catalog reachable through Conductor's `:9100/policies/` reverse proxy.

Three embedded-mode constraints follow from that target:

1. **No external database.** Mode 3 cannot expect a sibling Postgres container; bootstrap must work with a single-file backing store that lives in a named volume.
2. **URL-prefix isolation.** Conductor hosts multiple services behind one proxy; andy-policies must mount its routes under `/policies/*` and have its outbound URLs (Swagger `servers`, OIDC `redirect_uri`, static asset links, `LinkGenerator` outputs) emit the prefix without operator intervention.
3. **Self-describing registration.** The bundled deployment can't rely on operator-run SQL seeds for OAuth clients, RBAC roles, and settings definitions; andy-policies has to tell andy-auth, andy-rbac, and andy-settings what it needs on first boot.

Epic P10 (rivoli-ai/andy-policies#10) ships the four functional changes that satisfy these constraints (P10.1 SQLite boot, P10.2 base-path, P10.3 manifest registration, P10.4 cross-service smoke). This ADR captures the four architectural commitments behind them and the three Non-goals that scope the work, so future contributors don't unknowingly relax them.

## Decisions

### 1. SQLite for Mode 3; Postgres remains the default for Mode 1/2

`docker-compose.embedded.yml` configures `Database__Provider=Sqlite` against a single file in the `sqlite_data` named volume. The same EF migration set applies on both providers; `BundleMigrationTests` (P8.1) and `SqliteMigrationApplyTests` (P10.1) prove it.

Trade-offs:

- **Pro:** no sibling DB container; single-file backup/restore via `sqlite3 .backup`; trivial bootstrap; the entire catalog is one durable object.
- **Con:** single-writer throughput (file lock); no logical replication; no Postgres-only column types (`jsonb` operators, `timestamptz`, `text[]`).
- **Mitigation:** `SqliteModelCompatibilityTests` fails any change to `AppDbContext.OnModelCreating` that introduces a Postgres-only type without an `IsNpgsql()` branch. Postgres remains the production-throughput target for Mode 1/2; Mode 3 is for embedded deployments where one operator's policy edits are the only writer.

Mode 3 is **single-tenant by design** (Non-goal #1, below). Multi-tenant scale-out remains a Postgres-only feature.

### 2. `UsePathBase` in-process, not nginx-side rewriting

When `ASPNETCORE_PATHBASE=/policies` is set, P10.2's `app.UsePathBase(pathBase)` strips the prefix from inbound requests before route matching and re-prepends it on outbound URL generation. This is **load-bearing for outbound URLs**: Swagger `servers`, OIDC `redirect_uri` rendered from the SPA's `<base href>`, and `LinkGenerator` outputs all need to know the prefix in-process. Nginx-side rewriting (strip on the way in, rewrite on the way out) would create asymmetric bugs — outbound URLs would still emit the bare path because the in-process pipeline never sees the prefix.

Order is load-bearing: `UsePathBase` runs before `UseRouting`, before `UseAuthentication`/`UseAuthorization`, before any `Map*` call. Modes 1/2 leave `ASPNETCORE_PATHBASE` empty; the call no-ops and routes resolve at root. Pinned by `PathBaseTests` and `PathBaseUnsetTests` in `tests/Andy.Policies.Tests.Integration/Embedded/`.

The proxy itself is **Conductor's job, not ours** (Non-goal #3) — we declare the prefix in `config/registration.json` (`embeddedProxyPrefix`) and expect Conductor's `:9100` proxy to honor it.

### 3. Manifest-driven registration, not operator-run SQL seeds

`config/registration.json` is the single source of truth for the OAuth client (`andy-policies-api` + `andy-policies-web`), the RBAC application (`andy-policies` + 7 resource types + 18 permissions + 5 roles), and the settings definitions (5 keys). On first boot under embedded mode, P10.3's `ManifestRegistrationHostedService` POSTs the three blocks to:

- `AndyAuth__ManifestEndpoint` — andy-auth ingests the OAuth client with scopes + redirect URIs (idempotent, keyed on `clientId`; never rotates persisted client secrets).
- `AndyRbac__ManifestEndpoint` — andy-rbac upserts the application + roles + permissions (idempotent, keyed on `applicationCode` + `role.code` + `permission.code`; preserves subject assignments).
- `AndySettings__ManifestEndpoint` — andy-settings upserts the setting definitions (idempotent, keyed on `key`; preserves operator-set values).

Order is auth → rbac → settings — the auth-issued S2S token (P10.4 `client_credentials`) authenticates the rbac and settings calls, so a stack with auth-down can't even attempt rbac/settings registration. Replay is safe by design (each consumer owns idempotent upsert).

The mode 1/2 `auth-seed.sql` + `config/rbac-seed.json` + andy-settings boostrap continues to work for local dev, where an operator typically wants explicit control. `Registration__AutoRegister` defaults to `false` in those modes; embedded mode flips it on.

### 4. Fail-loud on registration failure

If any of the three POSTs returns non-2xx — or the consumer is unreachable, or the response body is unparseable — `ManifestRegistrationException` propagates from `StartAsync` and the host crashes before Kestrel binds. No partial-success state.

This is deliberate, and follows andy-rbac ADR-0001's posture (fail-loud on misconfiguration). A half-registered andy-policies fails confusingly at runtime (403 from rbac on every gated endpoint, missing settings defaults silently flipping behaviour, OIDC callbacks rejected as `invalid_client`); we'd rather see the operator hit a hard boot failure with a clear log line naming the failing block. P10.3's hosted service logs `Manifest registration failed for block {Block}: aborting startup.` at `Critical` precisely so this is grep-able.

The local-dev escape hatch is `Registration__AutoRegister=false`, which skips dispatch entirely — appropriate for working on an isolated andy-policies branch without a live ecosystem stack, never appropriate for production.

## Consequences

### Positive

- **Self-contained boot.** `docker compose -f docker-compose.embedded.yml up -d` is the entire deployment story for Conductor operators; no sibling DB, no manual SQL seed run, no out-of-band OAuth client registration.
- **File-level backup/restore.** `sqlite3 .backup` is a one-liner; the catalog is one durable artifact ([operations.md](../embedded/operations.md) covers the recipe).
- **Reproducible across environments.** The same `config/registration.json` drives auth/rbac/settings across all three modes — a Mode 3 catalog and a Mode 1/2 catalog have identical RBAC and settings shape.
- **Cross-service smoke contract.** P10.4's `EmbeddedCrossServiceSmokeTests` exercises the full create→publish→bind→bundle→resolve→audit→verify lifecycle; Conductor Epic AO (rivoli-ai/conductor#669) consumes it via the `ANDY_POLICIES_E2E_NO_COMPOSE=1` flag.

### Negative / accepted trade-offs

- **Single-writer throughput.** SQLite serializes writers; Mode 3 is not the deployment to use if you expect dozens of concurrent policy authors. The bundle pinning gate (ADR 0008) means most reads bypass the live writer anyway, so the practical bottleneck is publish/transition rate, not lookup rate.
- **All-or-nothing boot dependency on the ecosystem stack.** Fail-loud means andy-policies refuses to start when any of auth/rbac/settings is unreachable. Operators upgrading the bundle must bring up auth → rbac → settings → policies in order, or accept the boot crash and restart once dependencies are healthy.
- **Mode switching requires a manual export.** A deployment is Mode 1 *or* 2 *or* 3 at boot — switching requires `dotnet ef migrations script` + `sqlite3 .dump` + targeted import (Non-goal #2). The catalog can move between modes; it cannot move *live*.
- **No SQLite replication / HA.** A single-file store has no native replication. Operators wanting HA in Mode 3 must run their own filesystem-level replication (e.g. Litestream) outside this repo's scope.

## Considered alternatives

### Postgres in embedded mode

A sibling Postgres container could remove the single-writer constraint and let the same migration set apply unmodified. Rejected because:

- It defeats the "single durable artifact" property — operators now need to back up two volumes and reason about their consistency.
- Conductor's headline framing is *"one container, one bind"*; adding a second persistent service contradicts the bundled-deployment value proposition.
- Mode 1/2 already give operators Postgres if they want it.

### Operator-driven seed scripts in Mode 3

Continue using `auth-seed.sql` + `rbac-seed.json` + andy-settings bootstrap in Mode 3, run by the operator before booting andy-policies. Rejected because:

- It pushes ecosystem coordination onto every operator — they'd have to know which seed runs against which service in which order.
- Idempotent manifest endpoints (P10.3) collapse the same outcome to a single boot-time API call, which is how Conductor's bundled deployment is supposed to feel.

### Silent fallback on manifest failure

Log and continue when a consumer rejects the manifest POST, on the theory that a partially-registered andy-policies is better than no andy-policies. **Explicitly rejected** per andy-rbac ADR-0001 — partial registration causes 403 cascades and missing-setting drift that surface days later in production, far from the actual root cause.

### Conductor-managed proxy implementation in andy-policies

Bind `:9100` ourselves and route `/policies/*` internally. Rejected because the proxy is shared infrastructure across the entire Conductor bundle; baking it into one service would couple all the bundled services to andy-policies' deployment cadence (Non-goal #3).

### Multi-tenant SQLite

Run multiple Conductor tenants behind one andy-policies process via per-tenant SQLite files or a tenant column in the schema. Rejected as a Mode 3 Non-goal (#1) — scale-out is a Postgres-only deployment shape; Mode 3 stays single-tenant for the conceptual simplicity that justifies SQLite in the first place.

## Non-goals from epic P10 (recorded for posterity)

The epic explicitly scopes out four expansions; restating them here so a future change that re-considers them has a clear baseline to argue against:

1. **No multi-tenant SQLite.** One file, one tenant. Multi-tenant is Postgres-only.
2. **No live mode-switching.** A deployment is Mode 1 / 2 / 3 at boot; switching is an export/import operation.
3. **No in-proc proxy.** The reverse proxy on `:9100` is Conductor's responsibility; we declare the prefix and expect it honored.
4. **No backup automation.** Operators back up the SQLite file on whatever cadence their environment demands; we document the recipe in [`operations.md`](../embedded/operations.md).
