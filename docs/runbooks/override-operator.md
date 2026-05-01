# Override operator runbook

For platform operators running the andy-policies service. Covers
the settings gate, reaper tuning, and emergency procedures.
Companion to [Override concepts](../concepts/overrides.md) and
[ADR 0005 — Overrides](../adr/0005-overrides.md).

## Settings gate

`andy.policies.experimentalOverridesEnabled` (Boolean, default
`false`, scopes `Machine | Application | Team`) gates **writes
only**. When the snapshot has not yet observed the key (cold
start, or andy-settings briefly unreachable),
`IExperimentalOverridesGate.IsEnabled` returns `false`.

| Toggle state | Propose / approve / revoke | List / get / active |
|---|---|---|
| `true` | allowed (subject to RBAC + state-machine guards) | allowed |
| `false` | **403** with `errorCode = "override.disabled"` | allowed (always) |

Reads bypass the gate by design — turning the toggle off must not
strand consumers that already rely on an approved override's
effect. The reaper (P5.3) likewise bypasses the gate so flipping
the toggle off doesn't strand approved overrides past their
expiry.

### How to flip the toggle

Via andy-settings admin UI, or programmatically via the settings
client. The per-process snapshot refreshes on the configured
cadence (default 60s — see `AndySettings:Refresh`); a flip in
the andy-settings UI takes effect on the *next* override write
within ~60 seconds. There is no andy-policies restart required.

OTel observable gauge `andy_policies_experimental_overrides_enabled`
exports `1` when the toggle is on, `0` when off, refreshed on
every metrics scrape — verify the value reached this process
before announcing a rollout.

### Per-team rollout

`allowedScopes` includes `Team`. To enable the feature for one
team while leaving every other team gated off, set the value at
the Team scope in andy-settings. The snapshot resolution order is
`Team > Application > Machine > defaultValue`.

## Reaper tuning

`andy.policies.overrideExpiryReaperCadenceSeconds` (Integer,
default `60`, scopes `Machine | Application`) sets the sweep
cadence in seconds. Values below `5` are clamped in code to
prevent hot-looping under operator misconfiguration. Per-sweep
cap is `MaxRowsPerSweep = 500` (constant; not configurable).

OTel metrics emitted on every sweep:

| Metric | Type | Meaning |
|---|---|---|
| `policies.override.reaper.swept` | counter | number of overrides expired by the sweep |
| `policies.override.reaper.sweep_duration` | histogram (s) | wall-clock duration of the sweep |
| `policies.override.reaper.failures` | counter | per-sweep + per-row failures (race-tolerated) |

A healthy reaper:

- `policies.override.reaper.failures` rate is near zero.
- `policies.override.reaper.swept` integrates approximately to
  the count of overrides that crossed `ExpiresAt` in the same
  window (give or take one cadence period of latency).
- `policies.override.reaper.sweep_duration` p99 < 1 second on
  Postgres, < 2 seconds on SQLite.

If `sweep_duration` p99 climbs past 5 seconds, raise the cadence
(longer interval; fewer sweeps) before lowering it (shorter
interval; smaller batches but more transactions).

## Emergency: disable all override writes

When you need to halt all new propose / approve / revoke calls
without restarting the service:

1. Flip `andy.policies.experimentalOverridesEnabled` to `false` at
   the **Application** scope in andy-settings.
2. Wait ≤ refresh cadence (≤60s by default) for the live
   snapshot to observe the flip. Verify via the OTel gauge
   `andy_policies_experimental_overrides_enabled`.
3. New writes immediately return 403 with `override.disabled`.
4. Existing approved overrides keep applying until they expire
   or are revoked.

## Emergency: revoke every approved override

If a misconfiguration or active incident requires that no
approved override apply anymore — *without* waiting for natural
expiry — you also need to revoke each approved row. Combine the
gate flip above (so no new approvals can come in) with a loop:

```bash
# Requires a token with the override:revoke permission.
TOKEN=...

# Page through approved overrides 100 at a time.
curl -s -H "Authorization: Bearer $TOKEN" \
  https://policies.example.com/api/overrides?state=Approved \
  | jq -r '.[].id' \
  | while read id; do
      curl -s -X POST \
        -H "Authorization: Bearer $TOKEN" \
        -H "Content-Type: application/json" \
        -d '{"revocationReason":"emergency_disable_all:see incident #INC-9999"}' \
        https://policies.example.com/api/overrides/$id/revoke \
        > /dev/null
      echo "revoked $id"
  done
```

After the loop, every approved override is `Revoked` with the
incident-tagged reason in the audit chain. Consumers calling
`GetActiveAsync` immediately see an empty set for every scope.

> **Note.** Revocation requires `andy-policies:override:revoke`.
> Once P7 (#51) wires the real andy-rbac client, the operator
> needs the permission grant first. During the development
> placeholder period (`AllowAllRbacChecker`), the JWT layer at
> the API edge is the only authentication; ops still need a
> real token.

## Audit a suspicious override

Cross-reference: see [audit hash chain ADR](../adr/0006-audit-hash-chain.md).
The override workflow emits four event types
(`OverrideProposed`, `OverrideApproved`, `OverrideRevoked`,
`OverrideExpired`); each is hash-chained into the canonical
audit log alongside every other catalog mutation.

To investigate:

1. Pull the override's full audit trail by id (audit endpoints
   land with P6.6; until then, query the `audit_events` table
   directly via the Cockpit DB tools).
2. Verify the chain hash from genesis through the override's
   row range — a mismatch means tampering and should be
   escalated immediately.
3. Cross-check `ApproverSubjectId` against your IdP — was the
   approver expected to hold that role at the time?
4. Look for sibling overrides by the same proposer or in the
   same scope; a pattern often surfaces the actual concern.

## Related

- [Override concepts](../concepts/overrides.md) — semantics + state
  machine.
- [Approver runbook](override-approver.md) — for humans
  approving / revoking individual rows.
- [ADR 0005 — Overrides](../adr/0005-overrides.md) — design
  rationale (reaper, fail-closed gate, self-approval invariant).
