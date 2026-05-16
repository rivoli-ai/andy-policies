# ADR 0007 — Edit RBAC (catalog mutation authorization)

## Status

**Accepted** — 2026-04-21. Gates: P7.1 (rivoli-ai/andy-policies#47), P7.2 (#51), P7.3 (#55), P7.4 (#57) may proceed after this ADR is merged. Phase 0 tracker: rivoli-ai/andy-policies#94.

Supersedes: nothing. Companion to: ADR 0001 policy-versioning (#92), ADR 0006 audit-hash-chain (drafted this cycle).

## Context

Epic P7 (rivoli-ai/andy-policies#7) wires every catalog mutation through an authorization check. The README fixes the boundary: *"Subject→permission checks delegate to Andy RBAC; the edit matrix itself lives here."* andy-policies does not store roles or permission assignments; it calls out to andy-rbac's `CheckController` (`POST /api/check` — see `rivoli-ai/andy-rbac/src/Andy.Rbac.Api/Controllers/CheckController.cs`) on every mutation and honours the `{Allowed, Reason}` response.

Four cross-cutting decisions needed review artefacts before implementation:

1. **Delegation vs. local evaluation** — how do we decide who can mutate what?
2. **Service-to-service authentication** — how does andy-policies identify itself to andy-rbac when calling `/api/check`? **This was the highest-leverage open question from the Phase 0 review** (#94) — getting it wrong means fail-closed production denial on first deploy.
3. **Failure mode under andy-rbac unavailability** — fail-open or fail-closed?
4. **Author-cannot-self-approve invariant** — enforced by andy-rbac or by this service?

The Phase 0 review also flagged a fifth item — **cache invalidation strategy** — which is graduated (acceptable staleness in v1, upgraded later via Epic AL event bus).

## Decisions

### 1. Delegation: call andy-rbac, don't re-implement locally

`IRbacChecker.CheckAsync(subject, permission, resourceInstanceId, ct)` is implemented by `HttpRbacChecker` in `src/Andy.Policies.Infrastructure/Services/Rbac/`, which:

- Sends `POST {AndyRbac:BaseUrl}/api/check` with body `{SubjectId, Permission, Groups, ResourceInstanceId}`
- Returns the `{Allowed, Reason}` response body verbatim

Rejected: local permission evaluation — andy-rbac owns the durable permission store + the role inheritance logic; duplicating either creates two sources of truth that will diverge.

### 2. S2S auth: **per-service `client_credentials`** (with mTLS as future target)

The open question was *which identity does andy-policies use when calling andy-rbac?* Options considered:

| Option | Summary | Decision |
|---|---|---|
| **(a) Per-service `client_credentials`** | andy-policies has its own OAuth client (`andy-policies-api` already declared in `config/registration.json`); acquires a service token from andy-auth, caches it, refreshes before expiry, uses it on outbound calls | **Accepted for v1** |
| **(b) Shared machine identity** | Single well-known token used across all Andy services | Rejected — compromise of one secret compromises the ecosystem; no per-service audit trail at andy-rbac |
| **(c) mTLS** | Services present certificates from a shared PKI; andy-rbac validates client certs | **Accepted as long-term target** — filed as rivoli-ai/andy-auth story (linked below) for a dedicated migration epic |

**Implementation contract for option (a):**

- Config keys (read from andy-settings at boot, hot-reloadable):
  - `AndyRbac:BaseUrl` — e.g. `http://andy-rbac:5003` in docker mode, `http://localhost:9100/rbac` in embedded mode
  - `AndyAuth:Authority` — andy-auth token endpoint base
  - `AndyAuth:ServiceClientId` — `andy-policies-api` (from `registration.json`)
  - `AndyAuth:ServiceClientSecret` — from env `ANDY_POLICIES_API_SECRET` (per `registration.json` auth block)
- `HttpRbacChecker` uses a typed `HttpClient` with a `ServiceTokenHandler` delegating handler that:
  1. On cache miss, requests a `client_credentials` grant from `AndyAuth:Authority/connect/token` with scope `scp:urn:andy-rbac-api` (andy-rbac's audience)
  2. Caches the token with 90% of its `expires_in` as TTL (leaves 10% margin for clock skew)
  3. Attaches `Authorization: Bearer {token}` to every request
  4. On 401 from andy-rbac, discards cache and retries once (covers key rotation races)
  5. Fail-loud on third consecutive 401 (config error, not transient)
- The token cache is **per-process** (`IMemoryCache`). In multi-replica deployments each replica acquires independently; acquisition is cheap enough (~100ms) that this is acceptable.

**Why (a) over (b):** (b) shares blast radius. A compromised andy-policies container with the shared secret could impersonate andy-tasks or andy-issues when calling andy-rbac. With (a), each service has its own client record in andy-auth; revocation is per-service and per-secret.

**Why (a) over (c) today:** (c) requires a PKI that does not exist yet (no CA infrastructure in the ecosystem as of 2026-04-21; `certs/` directory in this repo carries only the self-signed dev cert for HTTPS). (a) reuses the OpenIddict OAuth infrastructure andy-auth already ships. (c) is the strongest design and is filed as a follow-up story on andy-auth — see §Future work.

**Dev-mode bypass:** When `AndyAuth:Authority` is empty (per CLAUDE.md dev bypass convention), `HttpRbacChecker` short-circuits to `{Allowed = true, Reason = "dev-bypass"}` and emits a stderr warning. This is the same bypass logic as the rest of the service and MUST be disabled in production via `AndyAuth:Authority` being configured.

### 3. Fail-closed on rbac unreach

If `POST /api/check` fails (network error, 5xx, timeout), `HttpRbacChecker` returns `{Allowed = false, Reason = "rbac-unreachable"}`. **No fallback to "allow" ever.**

- 3-second HTTP timeout on `/api/check` calls (tight; this is a synchronous mutation path)
- Unhandled exception from the delegating handler → fail-closed
- Circuit breaker: after 5 consecutive failures, short-circuit for 30s (returns `{Allowed = false, Reason = "rbac-circuit-open"}`) to prevent thundering herd on recovery

Rejected: fail-open. A 30-second andy-rbac outage would otherwise degrade the entire catalog into an unauthenticated free-for-all; that's a worse failure mode than temporary denial. Operators receive alerts via OTel metric `andy_policies_rbac_check_failures_total` + circuit-open spans.

### 4. Self-approval invariant lives in this service

Per P7.3 (rivoli-ai/andy-policies#55), `PolicyVersion.ProposerSubjectId` is set on draft creation; the publish endpoint (`POST /api/policies/{id}/versions/{vId}/publish`) rejects `actor == proposer` even when the actor has `andy-policies:policy:publish` permission. The check:

```csharp
if (version.ProposerSubjectId == actorSubjectId)
    return Forbidden(reason: "self-approval-forbidden", errorCode: "policy.self-approval-forbidden");
```

runs **before** the RBAC check (fast-path domain invariant). Same pattern applies to overrides (P5.2 — override proposer ≠ approver).

Rejected: encode the rule as an andy-rbac condition. andy-rbac is subject-centric, not action-context-centric; modeling "this subject may approve except when they are the proposer" would require per-row runtime data that andy-rbac doesn't have. Domain invariants live with the domain.

### 5. Cache semantics (graduated)

P7.2 proposes a 60s in-memory cache keyed `(subject, permission, instance)` on both allow AND deny decisions.

**v1 (this ADR):** accept 60s staleness. Per-process cache. No invalidation. Documented in this ADR + the operator runbook as an explicit revocation window. Rationale: most mutations are human-driven and rare; a 60s revocation delay is acceptable for a governance catalog. Multi-replica deployments have per-replica windows but since each replica hits /api/check independently, worst-case one replica denies and another allows for ≤60s during a revocation transient — unfortunate but not a safety violation.

**v2 (tracked, not scoped here):** subscribe to andy-rbac NATS events via Epic AL (rivoli-ai/andy-rbac#11). On `role.updated` / `permission.revoked` / `subject.removed` events, invalidate matching cache entries. This collapses the 60s window to ~1s (NATS latency). Gated on Epic AL landing; not a blocker for P7 code.

Rejected: no cache at all — every mutation adds 50–200ms network hop; interactive authoring in P9 UI becomes sluggish.

Rejected: distributed cache (Redis) — introduces a new infra dep for marginal gain; per-process is simpler.

### 6. Permission codes sourced from `registration.json`

The manifest is the **single source of truth** for permission codes. P7.1 (#47) finalizes the catalog. andy-rbac seeds the codes from the manifest on first boot (Epic AL or the ingestion endpoint per andy-auth#41 pattern). ASP.NET authorization policies (`AuthorizationOptions.AddPolicy`) read the codes at startup so a code-registration drift (manifest says `andy-policies:policy:publish`, handler says `policies:publish`) fails loud in CI.

Namespacing convention enforced across all surfaces: `andy-policies:<resource>:<action>` (e.g. `andy-policies:audit:verify`, `andy-policies:policy:publish`). The 2026-04-21 review caught permission-code drift between P6.5/P6.7 (bare `audit.verify`) and P7.1 (namespaced `andy-policies:audit:verify`); fix was applied to normalize on the namespaced form.

### 7. JWT audience validation pinned

`Program.cs` configures JWT bearer options with:

```csharp
options.TokenValidationParameters.ValidateAudience = true;
options.TokenValidationParameters.ValidAudience = "urn:andy-policies-api"; // from registration.json
options.TokenValidationParameters.ValidateIssuer = true;
options.TokenValidationParameters.ValidIssuer = configuration["AndyAuth:Authority"];
```

A token issued by andy-auth for a *different* service's audience (e.g. andy-tasks: `urn:andy-tasks-api`) will be rejected with 401 — the review flagged this as a gap where a cross-scope token could have authenticated against andy-policies endpoints without this setting.

## Consequences

### Positive

- **Production works on first deploy.** With option (a) pinned and wired, fail-closed no longer means "deny 100% of requests" — andy-policies has an identity to present to andy-rbac.
- **Per-service audit at andy-rbac.** Every `/api/check` call carries a `sub` claim naming the caller; andy-rbac's own audit log can distinguish andy-policies from andy-tasks from andy-issues.
- **Domain invariants stay with the domain.** Self-approval and rationale-required are evaluated before andy-rbac is consulted; andy-rbac stays simple (subject→permission) and this service stays fast on the common deny path.
- **Fail-closed by default** preserves the tamper-evidence story: a compromised andy-rbac can't silently grant more than its permission data; a failing andy-rbac denies until fixed.

### Negative / accepted trade-offs

- **60s revocation window** in v1. A revoked subject keeps permission for up to 60s; documented as known staleness.
- **Token acquisition adds ~100ms to cold-start mutations.** First mutation after container boot pays the andy-auth round-trip. Acceptable.
- **No mTLS yet.** A compromised andy-auth could mint service tokens with arbitrary client IDs; our trust in andy-rbac's `/api/check` is ultimately anchored in andy-auth. mTLS would shift the trust anchor to the PKI. Tracked as future work.
- **Per-process cache stores decisions pre-revocation event bus.** On multi-replica scale-out, replica A may allow while replica B denies during revocation transients. Not a safety violation (both decisions honour a version of andy-rbac state) but visible as latency-dependent behaviour.

### Follow-ups

- **mTLS migration** — filed as rivoli-ai/andy-auth story for a follow-up epic. When the PKI is ready, andy-policies (and every other consumer of andy-rbac) swaps `ServiceTokenHandler` for a certificate-attaching handler and drops the `client_credentials` flow. Same `IRbacChecker` interface, different wire protocol.
- **NATS-driven cache invalidation (cache v2)** — blocked on Epic AL (rivoli-ai/andy-rbac#11) landing. Once events flow, we subscribe + invalidate keyed entries.
- **Token cache observability** — OTel metric `andy_policies_rbac_token_cache_hit_ratio`, `andy_policies_rbac_token_refresh_duration_ms`. Belongs in P7.2 (#51) implementation.

## Considered alternatives

| Alternative | Rejected because |
|---|---|
| **Local permission evaluation** (no /api/check round-trip) | andy-rbac owns the durable permission store; duplicating creates divergent truth |
| **Shared machine identity** (one token across all Andy services) | Single-secret blast radius; no per-service audit at andy-rbac |
| **Fail-open on rbac unreach** | 30s outage → unauthenticated catalog mutation free-for-all |
| **Self-approval check in andy-rbac** | andy-rbac is subject-centric, not action-context-centric; requires per-mutation row data that doesn't belong there |
| **No cache at all** | 50–200ms per mutation; interactive authoring in P9 UI becomes sluggish |
| **Distributed cache (Redis)** | New infra dep for marginal gain; per-process is simpler and adequate |
| **Permission codes stored in-repo (C# const strings)** | `registration.json` must be the single source of truth so andy-rbac seeding doesn't drift from handler attributes |
| **Audience validation deferred** (relying on andy-auth to only issue valid-scope tokens) | Defence in depth — if andy-auth misconfigures or is compromised to issue cross-scope tokens, we still reject at the resource |

## Future work — mTLS migration

The mTLS path (option c) remains the long-term target for intra-ecosystem S2S. It requires:

- A shared CA under rivoli-ai/andy-auth — cert issuance, rotation, CRL / OCSP
- Per-service client certificates generated from `registration.json` service identities
- Trust-store distribution (how consumers get the CA cert into their trust chain)
- `HttpClient` wiring on every consumer to present its client cert
- andy-rbac (and every other /api/check host) configured to require + validate client certs
- Decision on TLS termination: do ingresses (Conductor proxy on :9100) terminate client TLS, or is it end-to-end service-to-service?

These are out of scope for Epic P7. Filed as a follow-up story on rivoli-ai/andy-auth for future sequencing: **rivoli-ai/andy-auth#44** ("mTLS for intra-ecosystem S2S authentication").

On migration, andy-policies will:
1. Replace `ServiceTokenHandler` with `ClientCertificateHandler` (presents cert from a trusted store path)
2. Drop the `AndyAuth:ServiceClientSecret` env var
3. Keep `IRbacChecker` unchanged — this is a transport-layer change
4. Add a "Migration to mTLS" row in `docs/runbooks/auth-migration.md`

---

**Authors**: drafted by Claude 2026-04-21; accepted the same day after Phase 0 review resolved the S2S auth question as option (a). Phase 0 tracker: rivoli-ai/andy-policies#94. Decisions above are not retroactively editable without a follow-up ADR — mTLS migration in particular will be `0007.1-mtls-migration.md` or a supersede-pattern ADR when the PKI lands.
