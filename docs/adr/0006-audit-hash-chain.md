# ADR 0006 — Audit hash chain

## Status

**Accepted** — 2026-04-21. Gates: P6.2 (rivoli-ai/andy-policies#42) and P8.2 (#82) may ship golden-vector tests after this ADR is merged. Phase 0 tracking: rivoli-ai/andy-policies#94.

Supersedes: nothing.
Related: ADR 0001 policy-versioning (P1.12, #92), ADR 0008 bundle-pinning (P8.8, #93 — reuses the canonicalizer from this ADR).

## Context

Epic P6 (rivoli-ai/andy-policies#6) introduces a tamper-evident catalog audit log. Every mutation of a `Policy`, `PolicyVersion`, `Binding`, `Override`, `ScopeNode`, or `Bundle` produces exactly one `AuditEvent` row. The log is the compliance-grade record of "who changed what, when, under what rationale" — read by external auditors, the admin UI audit timeline (P9.7), and compliance officers producing regulatory reports.

Without a strong integrity guarantee, a compromised app — or a careless migration — could silently rewrite history. "The policy was published by Alice with rationale X" is only trustworthy if the audit record cannot be backdated, reordered, or edited post-hoc.

The review flagged three ambiguities that this ADR resolves:

1. **Algorithm specification vs. implementation timing.** P6.2 code pins golden-vector test bytes; if those bytes were written before the canonical-JSON spec was fixed, they become retroactive authority and the ADR becomes descriptive rather than prescriptive.
2. **JCS vs. custom canonical subset.** Draft text across P6.2 / P6.9 / P8.2 / P8.8 used inconsistent language ("sorted keys, UTF-8, no whitespace" vs "RFC 8785 JCS"). Number canonicalization per IEEE 754 (the hard part of JCS) was addressed nowhere.
3. **Concurrency strategy.** P6.2 pseudocode used advisory locks AND serializable transactions simultaneously, which is redundant and creates spurious retry aborts.

## Decision

### 1. Chain structure

Linear hash chain. Each event has a monotonic `Seq` (bigint, per-service global), a `PrevHash` pointing at the previous event's `Hash`, and its own `Hash`:

```
event.Hash = SHA-256(event.PrevHash || canonicalJson(event.Body))
```

where `event.Body` is the canonical JSON serialization of the following fields in insertion order — note JCS sorts keys lexicographically at the byte level, so Go-style insertion-order maps are irrelevant:

```json
{
  "action":           "policy.version.publish",
  "actorRoles":       ["admin"],
  "actorSubjectId":   "6d2f…",
  "entityId":         "f5a1…",
  "entityType":       "PolicyVersion",
  "fieldDiffJson":    "[{\"op\":\"replace\",\"path\":\"/state\",…}]",
  "id":               "9b0c…",
  "rationale":        "quarterly rotation",
  "seq":              1824,
  "timestamp":        "2026-04-21T17:33:02.1234567Z"
}
```

The genesis event (`Seq = 0` or `Seq = 1`, TBD — see §Open questions) has `PrevHash = [0x00; 32]` (32 zero bytes). All subsequent events chain forward.

### 2. Hash algorithm: **SHA-256**

Rationale for SHA-256 over SHA-3:
- Shared library + algorithm with P8 bundle snapshot hashing (rivoli-ai/andy-policies#82) and P8.8 consumer verification tooling. One canonicalizer, one hash.
- Native `System.Security.Cryptography.SHA256` in .NET 8 — no third-party dependency.
- 256 bits is adequate for tamper-evidence; non-repudiation against a motivated adversary with pre-image attacks is NOT our threat model (that's future scope per P6 epic non-goals).
- If collision-resistance concerns arise later, SHA-384 / SHA-3-256 migration is a single-line hasher swap behind the `ICanonicalHasher` interface — tracked as a future ADR, not this one.

Rejected: SHA-3-256 (no ecosystem pay-off for the complexity tax); SHA-512 (128 bytes per event doubles on-disk cost with no threat-model benefit).

### 3. Canonical JSON: **RFC 8785 JSON Canonicalization Scheme (JCS)**

Authoritative normative reference: [RFC 8785 (March 2020)](https://datatracker.ietf.org/doc/html/rfc8785).

Required properties enforced in `src/Andy.Policies.Infrastructure/Audit/CanonicalJson.cs`:

- **UTF-8 without BOM** (§3.2.1)
- **Lexicographic key ordering** at every object level (§3.2.3) — sort by UTF-16 code units per ES2015 `Array.prototype.sort`
- **No insignificant whitespace** — single-line, no trailing newline
- **IEEE 754 number canonicalization** per §3.2.2.3:
  - Integer-valued doubles serialize without decimal point (e.g. `1` not `1.0`)
  - Scientific form uses `e` (not `E`), mantissa has exactly one digit before the decimal, exponent has no leading `+` or zero padding
  - `-0` serializes as `0`
  - `NaN`, `Infinity`, `-Infinity` are REJECTED (canonicalizer throws `CanonicalJsonException`) — the catalog has no legitimate non-finite numeric field; surface-level validation must reject these before they reach the audit path
- **String escaping** per §3.2.2.2:
  - Only U+0000–U+001F and U+0022 and U+005C require escaping
  - Shortest form (`\n` not `
`, etc.)

**Conformance test requirement (load-bearing — blocks P6.2 golden vectors):** `tests/Andy.Policies.Tests.Unit/Audit/CanonicalJsonJcsConformanceTests.cs` MUST exercise the JCS test-vector suite from [RFC 8785 §A.1](https://datatracker.ietf.org/doc/html/rfc8785#appendix-A.1) (the seven worked examples: `input_1.json` through `input_7.json` with their expected byte-exact outputs). If any vector diverges, the canonicalizer is non-conformant and ALL downstream golden-vector tests are invalidated.

Rejected: bespoke canonicalization ("sorted keys, UTF-8, LF endings, no whitespace" without IEEE 754 number rules) — creates a hash forgery vector via number formatting ambiguity (`1.0` vs `1` hashing differently); conflicts with offline verification by third-party JCS implementations (`@cyberphone/json-canonicalization` in JS, `python-jcs`, Go's `github.com/cyberphone/json-canonicalization`).

Rejected: JSON Pointer ordering + custom separators — non-standard, un-reviewable.

### 4. Concurrency strategy: **Postgres advisory lock** (not both)

The hash chain requires that `event[N].PrevHash = event[N-1].Hash`. Two concurrent inserts racing to append at `Seq = N` would each compute `PrevHash` from `event[N-1]`, producing a fork.

**Decision: Postgres advisory lock** held for the duration of the append transaction:

```sql
BEGIN;
SELECT pg_advisory_xact_lock(hashtext('audit_events.append'));
-- Read current tail (SELECT MAX(seq), hash FROM audit_events)
-- Compute new event hash
-- INSERT
COMMIT;  -- lock released
```

The advisory lock serializes appends globally (one at a time) without serializable-transaction retry amplification. Append throughput is bounded to single-writer speed (~1k/s on commodity Postgres) — more than enough for a governance-catalog audit log (expected peak: 10s of events per minute).

**Rejected: `ISOLATION LEVEL SERIALIZABLE`** — under contention, it raises `40001` serialization failures forcing application-side retries; the retry loop re-reads the tail, re-hashes, and re-inserts, which multiplies work under load. The advisory lock eliminates the race entirely at the cost of serialized append (which we want anyway for a chain).

**Rejected: Both advisory lock AND serializable** (P6.2's original pseudocode) — redundant; the serializable protection is rendered unnecessary by the lock, and the `40001` retries become pure overhead with zero additional safety.

**SQLite (embedded mode) fallback:** SQLite has no advisory locks and no multi-writer concurrency. `BEGIN IMMEDIATE` acquires a reserved lock that serializes the append naturally. The fallback is documented in §Consequences.

### 5. Append-only enforcement: **two-user DB role model**

Per P6.1 (rivoli-ai/andy-policies#41), the Postgres deployment uses two roles:

- `andy_policies_migrator` — owns the schema, runs `Database.MigrateAsync()` at deploy time, has `ALL PRIVILEGES`
- `andy_policies_app` — runtime app user, `SELECT, INSERT, UPDATE, DELETE` on non-audit tables; `SELECT, INSERT ONLY` on `audit_events`

The migration (run as `andy_policies_migrator`) explicitly `REVOKE UPDATE, DELETE, TRUNCATE ON audit_events FROM andy_policies_app` — NOT from `PUBLIC`, which the app user wouldn't inherit anyway. A trigger (`audit_events_no_mutate`) is installed as belt-and-braces.

**SQLite degradation (documented, not hidden):** SQLite has no role system. Embedded deployments rely on:
- The `BEFORE UPDATE/DELETE` trigger as the only enforcement mechanism
- The hash chain itself as reactive detection (any mutation breaks the chain and `VerifyChainAsync` flags it)

This is a degraded trust model — SQL-injection that can't defeat the trigger can't rewrite history, but a compromised app with direct file access CAN. Operators running embedded mode in regulated environments MUST lock the SQLite file permissions to the app service account and monitor file-integrity separately (e.g. `aide`, `auditd`).

### 6. Retention: **append-only, no truncation**

Setting `andy.policies.auditRetentionDays` (from `config/registration.json`) defaults to `0` = forever. It does NOT truncate the chain — truncation would break verifiability. It governs:
- Export staleness warnings in P6.7 NDJSON bundles (events older than the threshold are exported with a `stale: true` flag)
- Future cold-storage tiering (events older than the threshold may move to archive storage; the hash chain remains verifiable but reads are slower)

The chain itself is never mutated.

### 7. Non-goals

Explicitly out of scope for this ADR and Epic P6:

- **Cryptographic signing** (non-repudiation). The hash chain provides integrity (tamper-evidence), not non-repudiation. A compromised app-user key could append false events with correct hashes. If a future regulatory requirement needs non-repudiation, a separate ADR will add detached JWS or KMS signing.
- **Merkle tree / sparse verification.** Linear chain requires whole-chain verification. Partial verification (e.g. "prove only seq 1000–1010") is not supported. A Merkle upgrade is possible but unjustified by current requirements.
- **Real-time streaming of audit events** to external systems. Export is batch (P6.7 NDJSON). Event-bus streaming is a future concern.
- **Automated chain-break recovery.** If `VerifyChainAsync` returns `{valid: false, firstDivergenceSeq: N}`, the runbook (`docs/runbooks/audit-compliance.md`) documents the forensic process: quarantine the service, snapshot the DB, escalate to security. There is no automatic "re-chain" logic.

## Consequences

### Positive

- **One canonicalizer, one hasher, one ADR** shared by P6 audit chain AND P8 bundle hashing. Offline verification tools work against both.
- **RFC 8785 is a published internet standard** — any JCS-compliant library in any language can verify the chain independently. Compliance officers can write their own verifier in Python or JS without reading our code.
- **Advisory lock eliminates retry storms** under append contention while preserving serial chain integrity.
- **Two-user DB role** makes `REVOKE UPDATE, DELETE` a meaningful guarantee on Postgres, not theatre.

### Negative / accepted trade-offs

- **Append throughput bounded** to single-writer speed (~1k/s Postgres). Acceptable — catalog audit volume is governance-scale, not telemetry-scale.
- **SQLite embedded mode is a weaker trust model** (no role separation). Documented, with operator guidance. This is why embedded mode is explicitly a development/bundling convenience, not a production governance platform.
- **Whole-chain verification is O(N).** Verifying 10M events requires reading 10M rows. Acceptable for current scale; Merkle upgrade is a future ADR.
- **JCS number canonicalization is non-trivial** to implement correctly. Mitigated by RFC 8785 §A.1 conformance test vectors and the public reference implementations we can cross-check against.

### Follow-ups (tracked elsewhere)

- P6.2 golden-vector implementation tests (#42) are `blocked-by` this ADR.
- P8.2 snapshot hash (#82) uses the same `CanonicalJson` + SHA-256; is `blocked-by` this ADR and P8.8.
- Chain-break forensic runbook is part of P6.9 (#54).

## Considered alternatives

| Alternative | Rejected because |
|---|---|
| **Event-sourcing library** (Marten, EventStoreDB) | Adds infra dependency outside the .NET 8 + PostgreSQL stack (CLAUDE.md). Audit log is write-once query-only; no projections or catch-up needed. |
| **Soft-delete flag + app-side filtering** | Whole point is tamper-evidence against a compromised app. Guarantee must live in the database, not the application. |
| **SHA-3-256 hash** | No ecosystem advantage over SHA-256 for our threat model. Complexity tax without benefit. |
| **SHA-512 hash** | 128 bytes per event doubles storage with no threat-model benefit. |
| **Merkle tree structure** | Enables partial verification. Unjustified by current requirements. Revisit in a future ADR if per-entry verification becomes a real use case. |
| **Bespoke canonical JSON** ("sorted keys, UTF-8, no whitespace") | Number formatting ambiguity creates a hash-forgery vector. Not interoperable with third-party JCS implementations for offline verification. |
| **Serializable transactions (no advisory lock)** | `40001` retries under load amplify work with no benefit over advisory-lock serialized append. |
| **Both serializable AND advisory lock** | Redundant. Serializable adds retry overhead that the advisory lock already prevents. |
| **One DB user for migration and runtime** | `REVOKE UPDATE, DELETE` becomes theatre — the owner-class role keeps privileges regardless of PUBLIC. |
| **Detached JWS signing of each event** | Adds non-repudiation which is out of scope. If required later, a follow-up ADR adds KMS-backed signing as a separate integrity layer. |

## Decisions carried forward from Phase 0 review

The four open questions flagged on the initial draft were resolved during the Phase 0 review (rivoli-ai/andy-policies#94). Each decision is now load-bearing for the conformance vectors in P6.2 (#42) and P8.2 (#82).

- **Genesis event semantics: virtual genesis.** The first real event is `Seq = 1` with `PrevHash = [0x00; 32]` (32 zero bytes). There is no stored `Seq = 0` row. Verification loops start at `Seq = 1` and treat the 32-zero-byte predecessor as implicit. Rejected: a real stored genesis row with a deterministic payload — adds a special-case migration step and a row that every verifier must agree to skip, with no integrity benefit.
- **Timestamp precision: .NET `DateTimeOffset` default (100 ns / 7 decimal places).** Canonical ISO 8601 with `Z` suffix, e.g. `2026-04-21T17:33:02.1234567Z`. JCS hashes the string byte-for-byte — third-party verifiers MUST preserve exactly 7 fractional digits even when a zero could be trimmed. The canonicalizer emits the Roundtrip (`o`) format explicitly. Rejected: millisecond precision — easier for human readers but requires a lossy conversion that some consumer logs would round differently, breaking cross-implementation hashes. Rejected: nanosecond (9-digit) — exceeds .NET's tick resolution; would require a synthetic append of zeros.
- **`actorRoles` canonicalization: sort alphabetically at insert.** The canonicalizer's `BuildAuditBody(...)` helper sorts `actorRoles` with the default ordinal comparer before serialization. JCS preserves array order deterministically, so once sorted, the hash is JWT-claim-order-independent. Rejected: preserving JWT claim order — role order in a token has no security meaning, but two tokens for the same subject issued seconds apart could produce different hashes, breaking replay verification.
- **Rationale field when `rationaleRequired = false`: always include as `null`.** Every `AuditEvent` body serializes `"rationale": null` rather than omitting the field. JCS treats absent and null as distinct; always including the field keeps the canonical shape stable across setting flips. Rejected: omit-when-null — toggling `rationaleRequired` mid-chain would change the canonical shape and produce different hashes for identical semantics.

These four decisions are reflected inline above (Chain body example shows `actorRoles` in sorted form; §Decision 3 references §3.2.2.3 for number rules; all examples carry `rationale` even as `null`).

---

**Authors**: drafted by Claude 2026-04-21; accepted the same day after Phase 0 review. Phase 0 tracker: rivoli-ai/andy-policies#94. Post-acceptance edits require a follow-up ADR (`0006.1-...` or supersede pattern) — decisions above are not retroactively editable without invalidating all audit chains produced under this version.
