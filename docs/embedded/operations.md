# Embedded mode operations

Mode 3 (Conductor embedded) runs andy-policies as a single container with a SQLite-backed catalog, behind Conductor's reverse proxy on `:9100/policies/`. This page is the operator runbook — how to boot, seed, back up, and upgrade the embedded deployment. The architectural decisions behind these mechanics are captured in [ADR 0010 — Embedded mode](../adr/0010-embedded-mode.md); the cross-service smoke that proves the contract end-to-end is in [`cross-service-smoke.md`](cross-service-smoke.md).

## Prerequisites

The embedded compose only starts andy-policies. It expects the rest of the ecosystem to be reachable from the container — by default at `host.docker.internal:5001` (auth), `:5003` (rbac), `:5300` (settings). For the cross-service smoke + manifest registration to work, those services must be up.

## Boot

```bash
# From the andy-policies repo root.
docker compose -f docker-compose.embedded.yml up -d

# Wait for healthcheck (compose's own probe + an HTTP probe through the prefix).
curl --retry 30 --retry-delay 2 -fsk https://localhost:5112/policies/health
```

On first boot the API:

1. Applies EF migrations (P10.1 — `Database.MigrateAsync`, idempotent against an existing volume).
2. Seeds the six stock policies via `PolicySeeder` (P1.3, idempotent — presence-of-any-row probe; restart-booting against a populated volume preserves operator edits).
3. POSTs `config/registration.json`'s three blocks to andy-auth / andy-rbac / andy-settings if `Registration__AutoRegister=true` (P10.3, fail-loud on any consumer error).

The `/policies` URL prefix on every route comes from `ASPNETCORE_PATHBASE=/policies` (P10.2 — see [`docker-compose.embedded.yml`](https://github.com/rivoli-ai/andy-policies/blob/main/docker-compose.embedded.yml) for the canonical environment block).

## Seed

The boot-time seeder lands the catalog with six stock policies (`read-only`, `write-branch`, `sandboxed`, `draft-only`, `no-prod`, `high-risk`). It is **idempotent**: a restart against the same SQLite file does **not** re-seed and does **not** clobber operator-edited rows. Verified by `SqliteBootTests.SecondBoot_AgainstSamePersistentDb_DoesNotReseed`.

To **reseed from scratch** (drops all operator edits — destructive):

```bash
docker compose -f docker-compose.embedded.yml down -v
docker compose -f docker-compose.embedded.yml up -d
```

`-v` tears down the `sqlite_data` named volume; the next `up` boots against an empty database, which triggers seeding.

## Backup

The catalog lives in a single file: `/data/andy_policies.db` inside the container, backed by the `sqlite_data` named volume. SQLite's built-in `.backup` command takes a consistent copy under a write-ahead snapshot — safe with concurrent reads:

```bash
# 1. Take a consistent copy inside the container.
docker compose -f docker-compose.embedded.yml exec -T api \
  sqlite3 /data/andy_policies.db ".backup /data/andy_policies.db.bak"

# 2. Pull it onto the host.
mkdir -p ./backups
docker cp andy-policies-embedded:/data/andy_policies.db.bak \
  "./backups/andy_policies-$(date -u +%Y%m%dT%H%M%SZ).db"

# 3. (optional) Verify integrity of the copy.
sqlite3 "./backups/andy_policies-…​.db" "PRAGMA integrity_check;"
```

`PRAGMA integrity_check` should print `ok`. Anything else means the backup is corrupt — re-run the `.backup` command.

For automated backups, run the steps above on a cron from the host (the container's filesystem is ephemeral; only the named volume is durable).

## Upgrade

EF migrations apply on every boot (P10.1), so an upgrade is image-level:

```bash
# 1. Back up first (above).

# 2. Pull the new image and recreate. Compose drains the old container,
#    runs migrations on boot, and restarts only after healthcheck passes.
docker compose -f docker-compose.embedded.yml pull
docker compose -f docker-compose.embedded.yml up -d

# 3. Verify.
curl -fsk https://localhost:5112/policies/health
docker compose -f docker-compose.embedded.yml logs api | tail -50
```

**Rollback** is a file-level restore: stop the container, replace `andy_policies.db` in the volume with a known-good backup, start the container. EF will not "down-migrate" — if you've moved past a schema version that the older image expects, the older image will refuse to boot. Plan rollbacks against a backup taken **before** the upgrade.

## Environment variables

The canonical set lives in [`docker-compose.embedded.yml`](https://github.com/rivoli-ai/andy-policies/blob/main/docker-compose.embedded.yml). The table below is the operator-facing summary.

| Variable | Default in compose | Required? | Purpose |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | yes | Enables auto-migrate + seed (P10.1). |
| `ASPNETCORE_URLS` | `https://+:8443;http://+:8080` | yes | Kestrel binding. |
| `ASPNETCORE_PATHBASE` | `/policies` | embedded only | URL prefix (P10.2). Empty in Modes 1/2. |
| `Database__Provider` | `Sqlite` | yes | Switches the DbContext to the SQLite provider. |
| `ConnectionStrings__DefaultConnection` | `Data Source=/data/andy_policies.db` | yes | SQLite file path (volume-backed). |
| `AndyAuth__Authority` | `https://host.docker.internal:5001` | yes | OAuth2/OIDC issuer (no bypass — see #103). |
| `AndyAuth__Audience` | `urn:andy-policies-api` | yes | JWT audience claim. |
| `AndyRbac__BaseUrl` | `https://host.docker.internal:5003` | yes | andy-rbac check endpoint. |
| `AndySettings__ApiBaseUrl` | `https://host.docker.internal:5300` | yes | andy-settings client base URL. |
| `Registration__AutoRegister` | `true` | embedded only | Enables the manifest hosted service (P10.3). |
| `AndyAuth__ManifestEndpoint` | `…/api/manifest` | when AutoRegister=true | andy-auth manifest ingest. |
| `AndyRbac__ManifestEndpoint` | `…/api/manifest` | when AutoRegister=true | andy-rbac manifest ingest. |
| `AndySettings__ManifestEndpoint` | `…/api/manifest` | when AutoRegister=true | andy-settings manifest ingest. |
| `ANDY_POLICIES_API_SECRET` | `_dev_only_not_production_` | yes in production | Confidential client secret for the api OAuth client (used by Conductor's S2S smoke per P10.4). Override via secret store; do not commit. |

## Troubleshooting

See [`troubleshooting.md`](troubleshooting.md) for common failure modes (404 under the prefix, manifest fail-loud, SQLite lock, OIDC redirect mismatch).
