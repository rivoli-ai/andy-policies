# Cross-service embedded smoke (P10.4)

This is the andy-policies side of the [Conductor Epic AO](https://github.com/rivoli-ai/conductor/issues/669) cross-service integration suite. It boots the full ecosystem (andy-auth + andy-rbac + andy-settings + andy-policies), drives a complete catalog lifecycle through the live REST surface, and verifies the audit hash chain.

The fixture lives in [`tests/Andy.Policies.Tests.E2E/EmbeddedSmoke/`](../../tests/Andy.Policies.Tests.E2E/EmbeddedSmoke/) — the `EmbeddedCrossServiceSmokeTests` class is the one Conductor's harness invokes.

## What it proves

`FullLifecycle_DraftPublishBindBundleResolveAuditVerify` walks seven HTTP calls in order:

1. `POST /api/policies` — create stable policy + first draft version (P1).
2. `POST /api/policies/{id}/versions/{vId}/publish` — promote to `Active` (P2).
3. `POST /api/bindings` — bind the active version to a synthetic `repo:` target (P3).
4. `POST /api/bundles` — snapshot the catalog (required because the manifest default for `andy.policies.bundleVersionPinning` is `true`; without a pinned bundle, resolve 400s — P8.4 gate).
5. `GET /api/bindings/resolve?targetType=Repo&targetRef=…&bundleId={bid}` — Conductor-style read (P3.4 + P8.3).
6. `GET /api/audit?pageSize=200` — pull the audit page; client-side `AuditChainVerifier.Verify` checks **link integrity** (no seq gaps, every `prevHash` matches the prior row's `hash`, genesis row has the 64-zero sentinel).
7. `GET /api/audit/verify` — server-side **hash integrity** (recomputes SHA-256 over canonical-JSON payload bytes; the algorithm authority).

`ResolveOnUnboundTarget_Returns404OrEmpty` proves the resolve path doesn't accidentally match-all on miss.

`AuditChainVerifier_DetectsTamperedLink` is the negative-tamper assertion required by the acceptance criteria — it constructs a chain whose second event's `prevHash` is corrupted and proves the verifier rejects it.

## Run recipe (local)

```bash
# 1. From the andy-policies repo root, boot the full stack.
docker compose -f docker-compose.e2e.yml up -d --build

# 2. Wait for andy-policies (compose's --wait already gates on healthcheck;
#    this is just defensive).
curl --retry 30 --retry-delay 2 -fsk http://localhost:7113/health

# 3. Run the smoke (skipped silently without E2E_ENABLED=1).
E2E_ENABLED=1 dotnet test tests/Andy.Policies.Tests.E2E \
  --filter "FullyQualifiedName~EmbeddedCrossServiceSmoke"

# 4. Teardown.
docker compose -f docker-compose.e2e.yml down -v
```

The fixture itself can also drive `docker compose up`/`down` if you'd rather not orchestrate it manually — leave the stack down and just run step 3. The fixture invokes `docker compose -f $(ANDY_POLICIES_E2E_COMPOSE_FILE) up -d --wait` on `InitializeAsync` and `down -v` on disposal.

## Conductor Epic AO harness contract

The Conductor harness manages its own compose stack (a single combined compose for the full ecosystem behind a `:9100` reverse proxy). It points the smoke at that stack via env vars — **no andy-policies code change**:

```bash
export E2E_ENABLED=1
export ANDY_POLICIES_E2E_NO_COMPOSE=1                              # harness owns compose
export ANDY_POLICIES_E2E_POLICIES_BASE_URL=http://localhost:9100/policies/
export ANDY_POLICIES_E2E_AUTH_BASE_URL=http://localhost:9100/auth/
export ANDY_POLICIES_E2E_API_CLIENT_SECRET=<from harness's vault>

dotnet test tests/Andy.Policies.Tests.E2E \
  --filter "FullyQualifiedName~EmbeddedCrossServiceSmoke"
```

The fixture honors `ANDY_POLICIES_E2E_NO_COMPOSE=1` by skipping both compose `up` and `down` — Conductor manages the stack lifecycle.

## Configurable env vars

Defined in [`EmbeddedTestEnvironment.cs`](../../tests/Andy.Policies.Tests.E2E/EmbeddedSmoke/EmbeddedTestEnvironment.cs). All optional; defaults track `docker-compose.e2e.yml`'s exposed ports.

| Var | Default | Purpose |
|---|---|---|
| `E2E_ENABLED` | unset | Master gate. Must be `1` or the suite skips silently. |
| `ANDY_POLICIES_E2E_NO_COMPOSE` | unset | When `1`, fixture skips `docker compose up`/`down`. |
| `ANDY_POLICIES_E2E_POLICIES_BASE_URL` | `http://localhost:7113/` | Policies REST root. |
| `ANDY_POLICIES_E2E_AUTH_BASE_URL` | `http://localhost:7002/` | andy-auth root for the token endpoint. |
| `ANDY_POLICIES_E2E_API_CLIENT_ID` | `andy-policies-api` | OAuth client id for `client_credentials`. |
| `ANDY_POLICIES_E2E_API_CLIENT_SECRET` | `e2e-test-secret-not-for-production` | Dev-only default; production overrides via vault. |
| `ANDY_POLICIES_E2E_AUDIENCE` | `urn:andy-policies-api` | Resource scope on the issued JWT. |
| `ANDY_POLICIES_E2E_COMPOSE_FILE` | `docker-compose.e2e.yml` | Compose file the fixture brings up. |
| `ANDY_POLICIES_E2E_COMPOSE_WAIT_SECONDS` | `90` | Health-probe deadline after `compose up`. |

URLs without trailing slashes are normalised — `http://example/policies` becomes `http://example/policies/` so `Uri` composition with relative paths (`health`, `api/policies`) lands correctly.

## Why a synthetic repo target?

The smoke uses `repo:rivoli-ai/smoke-{guid}` as the binding target instead of one of the seeded scope nodes. Two reasons:

- Reruns against the same persistent volume don't collide — every run gets a fresh GUID-suffixed slug + target.
- Avoids depending on stock-policy seeding ordering or scope-tree shape, both of which can shift epic-to-epic without breaking this story's contract.

## Known gaps / follow-ups

- **CI gate** — there's no `e2e-embedded-smoke` job in `ci.yml` yet. The pattern from `docker-compose.e2e.yml`'s existing usage applies; gate behind a `run-e2e` PR label or `workflow_dispatch` so it doesn't slow day-to-day CI. Tracked as a P10.4 follow-up.
- **Conductor :9100 proxy** — `docker-compose.e2e.yml` exposes services directly on host ports; the embedded `:9100` reverse proxy is owned by Conductor and is not booted here. The smoke targets the direct ports locally and is overridable for harness use.
