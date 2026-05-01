# Override approver runbook

For humans holding the `andy-policies:override:approve` permission.
Companion to [Override concepts](../concepts/overrides.md) and
[ADR 0005 — Overrides](../adr/0005-overrides.md).

## Pre-approval checklist

Before approving, verify each of the five:

1. **Rationale is concrete.** "expedite vendor-blocked story" with no
   incident link is not concrete; "vendor licensing portal down — see
   incident #INC-1142" is. Reject anything that won't help an auditor
   six months from now answer "why was this granted?"
2. **Expiry is short.** Default cap: **90 days**. Anything longer
   should make you stop. Long expiries are a frequent foot-gun (the
   override outlives the reason for it). The propose-time `+1m`
   floor is just a guardrail; the *spirit* of the rule is "every
   override has an end date you know in advance."
3. **Scope is narrow.** Single principal or a real, well-defined
   cohort. Be skeptical of `Cohort:*:all-users`,
   `Cohort:everyone`, or any cohort whose membership you can't
   enumerate. Cohort membership is opaque to this service — it's
   a consumer responsibility — so an over-broad cohort is a wide
   blast radius.
4. **No self-approval.** The system rejects this with
   `override.self_approval_forbidden` (HTTP 403). Don't try to
   work around it by asking a colleague to approve "as you" — the
   audit chain records the actual approver subject id.
5. **Underlying policy is in `Active` or `WindingDown`.** The
   service refuses overrides against `Retired` versions, but
   `WindingDown` is allowed (the override may apply during the
   wind-down window). If the policy is being wound down, ask
   whether the override is the right move at all — the new policy
   may already address the use case.

## Red flags

If any of these are true, **don't approve** without a second
opinion from security or the policy owner:

- **Same proposer within the past week.** A single principal
  repeatedly needing exceptions usually means the policy is wrong
  or the principal needs a different role, not an override.
- **Very long expiry** (>30 days). Especially `Replace` overrides —
  a long-lived `Replace` is effectively a parallel policy that
  never went through the review the original got.
- **`Replace` pointing to a dramatically more permissive policy.**
  Compare the `Enforcement` and `Severity` of the original vs the
  replacement (Cockpit shows both). `Must/Critical` → `May/Info`
  is almost never legitimate.
- **`Cohort:*:all-users` / `Cohort:everyone`.** Should never reach
  the approver queue. Reject with a `revocationReason`
  pointing at this runbook.
- **Rationale references a ticket you can't find.** Verify the
  ticket exists in the linked tracker before approving. If the
  rationale points at "INC-9999" and INC-9999 is closed/duplicate,
  the override is on stale grounds.
- **Expiry crosses a known freeze window.** If your team enforces
  release freezes (e.g. holidays), an override that expires
  *during* the freeze means the relaxation may apply throughout
  the freeze; pull the expiry forward or reject.

## How to approve

Three surfaces — pick whichever fits your workflow. All three
delegate to the same service layer, so the resulting state is
identical.

### REST

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  https://policies.example.com/api/overrides/{id}/approve
```

200 on success; the response body is the updated `OverrideDto`
with `state = "Approved"`. 403 with `errorCode: override.self_approval_forbidden`
if you happen to be the proposer; 409 if another approver beat
you to the row.

### CLI

```bash
andy-policies-cli overrides approve {id}
```

Inherits authentication via the standard `--token` / `ANDY_CLI_TOKEN`
mechanism. Exit code 0 on success; non-zero on any 4xx/5xx with the
ProblemDetails detail printed to stderr.

### MCP (LLM agents / Cockpit)

```
policy.override.approve { id: "{id}" }
```

Returns the JSON-serialized DTO on success; a prefixed error
string on failure (`policy.override.self_approval_forbidden`,
`policy.override.invalid_state`, etc.).

## How to revoke

Same three surfaces; revoke requires a non-empty
`revocationReason`. **Approver-driven rejection of a `Proposed`
row is also a revocation** — there is no separate `Reject`
operation. Convention: stamp the reason with a
`rejected_by_approver:{your-subject-id}` prefix so the audit log
is unambiguous.

```bash
# REST
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"revocationReason":"rejected_by_approver:user:42 — scope too broad"}' \
  https://policies.example.com/api/overrides/{id}/revoke

# CLI
andy-policies-cli overrides revoke {id} --reason "rejected_by_approver:user:42 — scope too broad"
```

## Worked examples

### Legitimate request — approve

```
Proposer:  user:alice
Scope:     Principal | user:alice
Effect:    Exempt
Policy:    must-pin-vendor-license-v3
Expires:   2026-05-02T18:00:00Z   (24 hours from now)
Rationale: vendor licensing portal down — see incident #INC-1142
```

Walking the checklist:

1. ✅ Rationale links a specific incident.
2. ✅ Expiry is 24 hours.
3. ✅ Single principal.
4. ✅ Approver is the on-call EM (different subject from `user:alice`).
5. ✅ `must-pin-vendor-license-v3` is the current Active version.

Approve. The approval log shows the EM as approver and the
incident in the rationale; the override expires the next day and
the reaper rotates the row to `Expired` automatically.

### Suspicious request — revoke

```
Proposer:  user:bob
Scope:     Cohort | cohort:everyone
Effect:    Replace
Replacement: branch-protection-relaxed-v1   (Should/Moderate)
Original:    branch-protection-v3           (Must/Critical)
Expires:   2026-08-01T00:00:00Z   (90 days from now)
Rationale: Speed up releases for Q3 push.
```

Walking the checklist:

1. ❌ "Speed up releases" is not a concrete justification — no
   incident, no PR, no measured baseline.
2. ❌ Expiry is at the cap (90 days).
3. ❌ `cohort:everyone` is the broadest possible scope.
4. (n/a — different proposer)
5. ✅ Underlying policy is Active.

Three of five fail. Plus two red flags (`Replace` to a much
weaker policy, broadest possible cohort). **Revoke** with reason
`rejected_by_approver:{your-id} — scope too broad; resubmit
narrower with a concrete rationale and ≤14d expiry`.

## Audit

Every approve / revoke / expiry emits a domain event the audit
chain (P6) consumes. The Cockpit screen (Conductor#647) and the
audit endpoints surface:

- Who proposed each override.
- Who approved / revoked it (and when).
- The full rationale and (if revoked) revocation reason.
- The exact `PolicyVersion` ids the override referenced.
- The `ExpiresAt` timestamp.

Consult the audit endpoints (lands with P6.6) — or, once
P9 lands, the Cockpit override timeline — when reviewing
historical patterns of overrides for a principal or cohort.
