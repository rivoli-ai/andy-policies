# Audit compliance runbook

For compliance officers and external auditors investigating the
andy-policies catalog audit chain (P6.9, story
rivoli-ai/andy-policies#54). Companion to the
[envelope spec](../audit-envelope.md) and
[ADR 0006 — Audit hash chain](../adr/0006-audit-hash-chain.md).

> **Firewall.** The hash chain provides **integrity**: nobody can
> rewrite history without detection. It does **not** provide
> non-repudiation: there is no detached cryptographic signature
> that proves *who* signed. If you suspect a false-actor scenario,
> the chain alone is insufficient — you need to cross-reference
> the IdP audit trail (Andy Auth) and the JWT issuer.

## 1. Verify the chain is intact

```bash
# Live, against the running service
andy-policies-cli audit verify --from 1 --to 100000

# Offline, against an exported NDJSON
andy-policies-cli audit verify --file ./audit-export.ndjson
```

Output is a JSON object with `valid`, `firstDivergenceSeq`,
`inspectedCount`, `lastSeq`. Exit codes:

| Exit | Meaning |
|---|---|
| 0 | Chain valid |
| 1 | Transport / unexpected |
| 2 | Bad arguments / file not found |
| 3 | Auth (token rejected) |
| 6 | **AuditDivergence** — chain itself reports `valid=false` |

A non-zero exit other than 6 is an operational error (server
down, wrong token, malformed file). Exit 6 is a content finding —
treat it as a security incident and proceed to §4.

## 2. Export a date-range bundle

```bash
# Via CLI (REST under the hood; walks the cursor for you)
andy-policies-cli audit export --out audit-2026-q2.ndjson

# Via MCP — base64-encoded NDJSON, one tool call
mcp call policy.audit.export --from-seq 1 --to-seq 100000

# Via gRPC — server-streaming chunks of UTF-8 NDJSON
grpcurl -d '{"from_seq":1,"to_seq":100000}' \
  policies.example.com:5113 \
  andy_policies.AuditService/ExportAudit > audit-2026-q2.ndjson
```

The bundle is one JSON object per line, with the last line being a
`"type":"summary"` trailer carrying the genesis prev-hash and the
terminal hash. The three surfaces produce **byte-identical**
bundles for the same range — they share the same exporter under
the hood.

Round-trip an export through offline verify before archiving:

```bash
andy-policies-cli audit verify --file audit-2026-q2.ndjson
# Expect exit 0
```

If the round-trip fails, do not archive — the export was
corrupted in flight. Re-export.

## 3. Find all events by an actor

```bash
# CLI
andy-policies-cli audit list --actor user:alice --page-size 500

# REST (when paginating manually)
curl -s -H "Authorization: Bearer $TOKEN" \
  "https://policies.example.com/api/audit?actor=user:alice&pageSize=500" \
  | jq '.items[] | {seq, action, entityType, entityId, timestamp}'

# Walk the cursor for the full history
nextCursor=$(curl -s -H "Authorization: Bearer $TOKEN" \
  "https://policies.example.com/api/audit?actor=user:alice&pageSize=500" \
  | jq -r '.nextCursor')
while [ -n "$nextCursor" ] && [ "$nextCursor" != "null" ]; do
  curl -s -H "Authorization: Bearer $TOKEN" \
    "https://policies.example.com/api/audit?actor=user:alice&pageSize=500&cursor=$nextCursor"
  nextCursor=$(... | jq -r '.nextCursor')
done
```

Filter combinations are AND'd. Useful pairings:

- `actor=user:alice&action=policy.version.publish` — every publish
  by Alice.
- `entityType=Policy&entityId=<id>` — every mutation of one
  policy.
- `from=2026-04-01T00:00:00Z&to=2026-04-30T23:59:59Z` — a calendar
  month. Inclusive both ends.

## 4. Suspected tampering — preserve forensics

When `audit verify` reports `valid=false`, **do not attempt to
"fix" rows**. The chain's value is precisely that it surfaces a
divergence; rewriting around it would destroy the evidence.

Step-by-step:

1. **Capture the divergence seq.** `firstDivergenceSeq=N`.
2. **Pull the surrounding range** (10 rows on either side):
   ```bash
   andy-policies-cli audit export --from $((N-10)) --to $((N+10)) --out incident.ndjson
   ```
3. **Hash-snapshot the file.** `sha256sum incident.ndjson > incident.sha256`.
4. **Notify security on-call.** They will route to the incident
   commander; do **not** post the file to public channels.
5. **Open an investigation ticket** with the export + sha256 +
   the verify output as attachments.
6. **Preserve the live chain as-is.** Do not delete, restore from
   backup, or re-run migrations until security signs off.
7. **Cross-reference Andy Auth** for the actor at row N — was the
   `sub` claim's owner expected to hold their role at that
   moment? IdP-side mismatches are the most common cause.

What the chain *can* prove:
- Integrity of every row from row 1 through row N-1.
- Integrity of every row after the next valid hash boundary
  (zero, in the worst case).
- The exact byte at row N that doesn't match the recomputed
  hash.

What the chain *cannot* prove:
- That the actor identified by `actorSubjectId` is the human
  who made the change (impersonation lives outside this surface).
- That row N's data is "correct" — only that the row matches
  its own committed hash.

## 5. How long are events kept?

**Forever, in v1.** The chain is never truncated. The setting
`andy.policies.auditRetentionDays` exists in andy-settings but
governs *export-freshness warnings* (P6.7) — whether the operator
console flags a chain that hasn't been exported in N days. It
does **not** delete rows.

If retention enforcement (true row removal) is required for a
specific deployment, raise it as an ADR amendment to ADR 0006.
The current design's chain-as-source-of-truth invariant
explicitly excludes retention truncation.

## 6. PII in audit diffs

The diff generator (P6.3) honours two attributes on DTO
properties:

- `[AuditIgnore]` — the property never appears in the patch.
- `[AuditRedact]` — the property's value is replaced with `"***"`
  in `add`/`replace` ops; `remove` ops have no value and are
  unaffected.

If you discover PII in an audit diff that *should* have been
redacted:

1. **Capture the row range** as in §4 (do not delete).
2. **Open a security ticket** flagging the property name +
   leaking module.
3. **Patch the DTO** to add `[AuditRedact]` to the offending
   property and merge the fix.
4. **Document the gap** in a security advisory; the historical
   rows still contain the PII (the chain prevents removal). For
   regulated environments this may require a data-subject
   notification — confer with legal.

The chain's append-only invariant is deliberate: the audit
record is more valuable than convenience cleanup of historical
data. If your environment's retention rules conflict with this,
raise it during the ADR 0006 amendment cycle, not via direct
row mutation.

## Related

- [Audit envelope spec](../audit-envelope.md) — exact field shapes.
- [ADR 0006 — Audit hash chain](../adr/0006-audit-hash-chain.md) — design rationale.
- [Schema](../schemas/audit-event.schema.json) — JSON Schema 2020-12 validator.
- Andy Auth audit trail — for IdP-side actor verification.
