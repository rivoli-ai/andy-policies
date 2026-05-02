# Audit envelope spec

Canonical wire and at-rest shape of an `AuditEvent` (P6.9, story
rivoli-ai/andy-policies#54). Companion to
[ADR 0006 — Audit hash chain](adr/0006-audit-hash-chain.md), which
pins the *why*; this document pins the *what* — every field, every
encoding, every canonical-JSON rule a downstream consumer needs to
validate an exported bundle byte-for-byte. The accompanying JSON
Schema lives at [`schemas/audit-event.schema.json`](schemas/audit-event.schema.json).

> **Scope.** This is the *external* envelope: what REST / MCP / gRPC
> emit, what the export bundle records line-by-line, what the offline
> verifier (CLI `audit verify --file`) and the hash chain itself
> consume. The internal storage column types (`bytea`, `jsonb`,
> `text[]`, …) are an implementation detail.

## Field table

| JSON field | Type | Hashed? | Notes |
|---|---|---|---|
| `id` | UUID v4 string | yes | Stable random GUID. |
| `seq` | int64 | **no** | Database-assigned monotonic sequence; appears on the wire and in the schema, but is **not** part of the hash envelope so two services never need to agree on a global seq scheme. The chain links via `prevHashHex` instead. |
| `prevHashHex` | 64-char lowercase hex | yes (as bytes) | SHA-256 of the previous row's `hash`. Genesis row uses 64 zero chars. |
| `hashHex` | 64-char lowercase hex | output | `SHA-256(prevHash || canonicalJson(envelope))`. |
| `timestamp` | ISO 8601 UTC, ms precision | yes | Always emitted as `yyyy-MM-ddTHH:mm:ss.fffZ`. The Z suffix is required; the server normalises to UTC before serialising. |
| `actorSubjectId` | string | yes | JWT `sub` claim of the actor (e.g. `user:42`, `system:reaper`). Non-empty. |
| `actorRoles` | array of string | yes (sorted) | RBAC roles snapshot. **Sorted lexicographically** before hashing so iteration order can't change the hash. |
| `action` | string | yes | Dotted action code (`policy.version.publish`, `override.approve`, …). |
| `entityType` | string | yes | Canonical type of the mutated entity (`Policy`, `Override`, `Binding`, …). |
| `entityId` | string | yes | String form of the row's primary key — usually a GUID, occasionally a composite ref. |
| `fieldDiff` | parsed JSON Patch (RFC 6902) | yes | Embedded as a JSON value, **not** as a JSON-encoded string. Defaults to `[]` for create/delete events. |
| `rationale` | string \| null | yes | Free-text rationale; null permitted only when `andy.policies.rationaleRequired` is off (P6.4). |

## Canonical JSON rules (pinned by ADR 0006)

The bytes that feed `SHA-256(prevHash || …)` are produced by a
deterministic UTF-8 JSON serializer with the following invariants:

1. **No BOM.** Output is raw UTF-8.
2. **Lex-sorted keys at every object level.** Sort by the UTF-16
   code unit order (matches RFC 8785 §3.2.3).
3. **No insignificant whitespace.** No space, tab, newline, or CR
   between tokens. `{"a":1}` not `{ "a" : 1 }`.
4. **Strings.** Escape `\b \f \n \r \t \" \\` to their two-char
   sequences; escape other C0 controls as `\uXXXX`. Assignable
   non-ASCII code points emit as their UTF-8 bytes — **no**
   `\uXXXX` for code points outside C0 (RFC 8785 §3.2.2.2,
   "shortest form").
5. **Numbers.** Integers render with no decimal or exponent
   (`42`, `-1`, `0`). Doubles use the shortest round-trip form;
   `NaN` and ±Infinity are **rejected** — neither can ever appear
   in the audit envelope.
6. **Arrays.** Element order is data, not noise — preserved as
   given. (`actorRoles` is sorted *before* serialization, but the
   serializer itself never reorders array elements.)
7. **Booleans / nulls.** `true`, `false`, `null` literals.

The implementation in `Andy.Policies.Shared.Auditing.CanonicalJson`
is the canonical reference; `tests/Andy.Policies.Tests.Unit/Audit/CanonicalJsonTests.cs`
exercises every rule above. Five
[golden vectors](https://github.com/rivoli-ai/andy-policies/blob/main/tests/Andy.Policies.Tests.Unit/Audit/HashChainGoldenVectorsTests.cs)
pin byte-exact `(payload → hash hex)` outputs across releases.

## Hash computation pseudocode

```
HASH(prevHash, payload):
  envelope = {
    "action":         payload.action,
    "actorRoles":     SORT_LEX(payload.actorRoles),
    "actorSubjectId": payload.actorSubjectId,
    "entityId":       payload.entityId,
    "entityType":     payload.entityType,
    "fieldDiff":      PARSE_JSON(payload.fieldDiffJson),
    "id":             payload.id,
    "rationale":      payload.rationale,           // null if absent
    "timestamp":      FORMAT_UTC_MS(payload.timestamp),
  }
  return SHA-256(prevHash || canonicalJson(envelope))
```

`canonicalJson` keys are sorted lex (the keys above already are);
the implementation re-sorts at every level so nested objects are
also stable.

## Wire format example

A pretty-printed REST/MCP `AuditEventDto` for visual reference;
the actual wire bytes are minified per the canonical rules.

```json
{
  "id":             "11111111-1111-1111-1111-111111111111",
  "seq":            1824,
  "prevHashHex":    "0000000000000000000000000000000000000000000000000000000000000000",
  "hashHex":        "1e9953b1a37c2ce4009212ff635da95cc5bee54737c534b20147de62348dc6b7",
  "timestamp":      "2026-05-01T12:00:00.000Z",
  "actorSubjectId": "user:test",
  "actorRoles":     ["admin"],
  "action":         "policy.create",
  "entityType":     "Policy",
  "entityId":       "00000000-0000-0000-0000-000000000001",
  "fieldDiff":      [],
  "rationale":      "first event"
}
```

## Export-bundle line shapes

The NDJSON bundle produced by `policy.audit.export` (MCP),
`AuditService.ExportAudit` (gRPC), and the CLI's
`audit export --out` adds a `type` discriminator on every line:

```jsonl
{"type":"event","id":"…","seq":1,"prevHashHex":"…","hashHex":"…","timestamp":"…","actorSubjectId":"…","actorRoles":["…"],"action":"…","entityType":"…","entityId":"…","fieldDiff":[…],"rationale":"…"}
{"type":"event","id":"…","seq":2,…}
{"type":"summary","fromSeq":1,"toSeq":N,"count":N,"genesisPrevHashHex":"00…00","terminalHashHex":"…","exportedAt":"2026-05-01T12:34:56.789Z"}
```

The summary line is metadata only — it does **not** participate in
the hash chain. `terminalHashHex` equals the final event's `hashHex`
and gives a one-glance integrity check ("does the chain end where
my live verifier says it ends?").

## Field semantics: redaction + ignore

Per [ADR 0005 — Overrides](adr/0005-overrides.md) and P6.3, DTO
properties may be decorated with:

- `[AuditIgnore]` — drops the property from the diff entirely.
  Used for computed / denormalised columns (`ModifiedAt`,
  `RowVersion`).
- `[AuditRedact]` — substitutes `"***"` for the value in
  `add` / `replace` ops; `remove` ops have no value and are
  unaffected. The fact-of-change is still recorded; the value
  is not.

Neither attribute changes the hash envelope itself — they shape
the patch document that ends up *in* `fieldDiff`. The chain still
hashes whatever the diff generator produced.

## Schema

The JSON Schema 2020-12 document at
[`schemas/audit-event.schema.json`](schemas/audit-event.schema.json)
is the authoritative validator: external auditors point a
schema-aware tool at the export and confirm structural conformance
without invoking application code. The schema is hand-authored
(not generated) so its prose is auditor-readable; a future story
may add a drift guard if hand-authored stays brittle.

## Related

- [ADR 0006 — Audit hash chain](adr/0006-audit-hash-chain.md) — *why*.
- [Compliance officer runbook](runbooks/audit-compliance.md) — operational how-to.
- [Schema](schemas/audit-event.schema.json) — JSON Schema 2020-12 validator.
