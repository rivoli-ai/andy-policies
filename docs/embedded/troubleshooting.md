# Embedded mode troubleshooting

Symptoms you're likely to see when something goes wrong in Mode 3, with concrete diagnosis commands and the underlying root cause. Companion to [`operations.md`](operations.md) and [ADR 0010](../adr/0010-embedded-mode.md).

## `GET /policies/api/...` returns 404

**Most likely cause:** `ASPNETCORE_PATHBASE` is unset or wrong, so the API serves at root and the `/policies` prefix is not stripped before route matching.

**Diagnosis:**

```bash
# Confirm the env var landed in the container.
docker compose -f docker-compose.embedded.yml exec api \
  printenv ASPNETCORE_PATHBASE

# Bypass the prefix — if this works, the prefix wiring is the bug.
docker compose -f docker-compose.embedded.yml exec api \
  curl -fsk http://localhost:8080/health
```

**Fix:** Ensure the compose file sets `ASPNETCORE_PATHBASE=/policies` (P10.2). If a reverse proxy ahead of us (Conductor's `:9100`) is also rewriting the path, you may be **double-stripping** — the proxy strips `/policies` from `/policies/api/foo`, then `UsePathBase` strips a second `/policies` that isn't there. Disable the proxy-side rewrite and let `UsePathBase` handle it in-process. Pinned by `PathBaseTests` in `tests/Andy.Policies.Tests.Integration/Embedded/`.

## Startup crashes with `ManifestRegistrationException`

**Most likely cause:** A consumer (andy-auth, andy-rbac, or andy-settings) is unreachable, returned non-2xx to the manifest POST, or rejected the payload. P10.3 is **fail-loud by design** — a half-registered embedded deployment fails confusingly at runtime, so we crash on boot instead.

**Diagnosis:**

```bash
# Logs name the failing block (auth / rbac / settings).
docker compose -f docker-compose.embedded.yml logs api \
  | grep -i 'manifest registration failed for block'

# Probe the manifest endpoints directly.
curl -fsk -X POST https://host.docker.internal:5001/api/manifest \
  -H 'Content-Type: application/json' -d '{}'
# Repeat for :5003 and :5300.
```

**Fix:**

- If a consumer is genuinely down, bring it up before andy-policies. The boot order in a Conductor-managed stack is auth → rbac → settings → policies; same order applies in dev.
- If a consumer is up but rejecting the payload, look at its logs for the parse error — the manifest POST ships `config/registration.json`'s `auth`/`rbac`/`settings` block verbatim.
- **Local-dev-only escape hatch:** set `Registration__AutoRegister=false`. This skips dispatch entirely and falls back to the Mode 1/2 seed-script path. **Do not use in production** — the embedded deployment is then non-self-describing.

## SQLite "database is locked"

**Most likely cause:** A second writer is attached to `/data/andy_policies.db`. SQLite's embedded mode is **single-writer**; concurrent writers serialise on a file lock.

**Diagnosis:**

```bash
# Anyone holding the volume?
docker ps --filter volume=sqlite_data

# Any host-side sqlite3 attached?
lsof | grep andy_policies.db
```

**Fix:**

- Detach any host-side `sqlite3` shell.
- Ensure only one container mounts the `sqlite_data` volume — running two replicas of andy-policies against the same volume is unsupported (epic Non-goal, see ADR 0010).
- The `.backup` command from [operations.md](operations.md) takes a consistent copy without holding the write lock — it should not produce this symptom; if it does, you have a true concurrent writer somewhere.

## OIDC callback fails with `invalid_redirect_uri`

**Most likely cause:** The redirect URI the browser hit isn't in the registered list for `andy-policies-web`. The canonical list is in [`config/registration.json`](https://github.com/rivoli-ai/andy-policies/blob/main/config/registration.json) and includes `http://localhost:9100/policies/callback` for the embedded proxy.

**Diagnosis:**

```bash
# Pull the registered URIs from andy-auth.
curl -fsk https://host.docker.internal:5001/api/clients/andy-policies-web \
  | jq '.redirectUris'
```

**Fix:**

- If Conductor's proxy is bound to a non-default port (not `:9100`), update `auth.webClient.redirectUris` in `config/registration.json` and re-run the manifest registration: restart andy-policies (idempotent — re-POSTs and andy-auth upserts on `clientId`).
- If you're testing through a tunnel (`ngrok`, `cloudflared`), add the tunnel hostname to the redirect URI list before booting; runtime additions need a re-register.

## Health probe times out on first boot

**Most likely cause:** Cold start under embedded mode runs migrate + seed + manifest registration before Kestrel accepts traffic. On a slow disk, this can take 30–60 s.

**Diagnosis:**

```bash
# Watch boot progress; "Now listening on" comes after migrate+seed+register.
docker compose -f docker-compose.embedded.yml logs -f api
```

**Fix:** Increase the compose `start_period` (currently `15s`) if your environment is consistently slow. The cross-service smoke uses `ANDY_POLICIES_E2E_COMPOSE_WAIT_SECONDS` (default 90) for the same reason — see [`cross-service-smoke.md`](cross-service-smoke.md).

## Backup file is 0 bytes / corrupt

**Most likely cause:** `sqlite3 .backup` was interrupted, or the destination filesystem ran out of space.

**Diagnosis:**

```bash
sqlite3 ./backups/andy_policies-<ts>.db "PRAGMA integrity_check;"
# Expect 'ok'. Anything else = corrupt.
```

**Fix:** Re-run the backup command from [operations.md](operations.md). If the disk is full, free space first; do not partial-restore a corrupt copy.
