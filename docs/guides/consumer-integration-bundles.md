# Consumer Integration: Bundles

A practical guide for services that need to **pin a frozen catalog
version** and resolve policies against it deterministically.
Targeted at: Conductor's admission gate, andy-tasks per-task gates,
andy-mcp-gateway tool policy, and any consumer that wants
*"identical answers across catalog mutations until I say otherwise"*.

For the *what* and *why* — entity shape, design rules, surface
parity — start with [ADR 0008 — Bundle pinning](../adr/0008-bundle-pinning.md).
This guide assumes you've read the headline decisions there.

> **Scope reminder.** andy-policies stores *what* policies are pinned
> in *which* bundle. Whether a run violates a policy is still a
> consumer concern — Conductor's admission gate evaluates the rule
> body, andy-tasks gates the task. The bundle gives you a frozen
> view to evaluate against; *acting on that view* is yours.

## 1. Why pin?

Three reasons, in order of strength:

1. **Reproducibility.** A consumer release pinning bundle `B` makes
   admission decisions identical across the lifetime of that
   release, regardless of whether someone publishes a new
   `Active` policy version mid-week. Your CI green is your
   production green.
2. **Audit linkage.** The `bundle.create` event in the P6 audit
   chain (see [ADR 0006](../adr/0006-audit-hash-chain.md)) carries
   the bundle's `snapshotHash` and the chain-tail hash at create
   time. A compliance officer can walk from a Conductor admission
   decision → the bundle id it referenced → the audit event that
   created the bundle → the chain coordinate that gates further
   verification. End-to-end traceability for free.
3. **Isolation from live publishes.** A new override or binding
   landing in the live catalog does NOT leak into a bundle taken
   before the change. This is the load-bearing guarantee — the
   serializable transaction in `BundleService.CreateAsync` and
   the immutability sweep in `AppDbContext.SaveChangesAsync` are
   what make it true.

## 2. Pinning lifecycle — recommended cadence

**Pin once per consumer release.** That's the headline.

| Step | Operator action |
|---|---|
| Cut a release | `POST /api/bundles` with a release-named slug (e.g. `conductor-v2.14.0-policies`) |
| Reference bundle id | Stamp it in `DelegationContract.bundleId` (andy-tasks Epic U) or your equivalent per-task pinning column |
| Live for that release | Every admission decision / per-task evaluation passes `?bundleId={that-id}` |
| Cut the next release | Create a new bundle; older bundle stays available for replay debugging until you soft-delete it |

**Anti-patterns:**

- ❌ Pinning per request to a fresh bundle — defeats reproducibility, inflates audit chain, costs storage.
- ❌ Hard-deleting old bundles — soft-delete preserves the audit-chain reference; hard-delete is forbidden by the entity contract (see ADR 0008 §4).
- ❌ Diffing a bundle against live state — the bundle pinning surface deliberately doesn't expose that. A drift detector against live state is Conductor's job (admission gate diffs the *current* bundle answer against the *intended* bundle answer for the run's pinning).

## 3. How to create a bundle

Permission required: `andy-policies:bundle:create` (see [Permission
catalog](../reference/permission-catalog.md)). The service hits
andy-rbac on every create; without the grant you get 403.

### REST

```http
POST /api/bundles HTTP/1.1
Authorization: Bearer eyJ…
Content-Type: application/json

{
  "name": "conductor-v2.14.0-policies",
  "description": "Pinned for Conductor v2.14.0 release",
  "rationale": "Cut at 2026-05-06T18:00:00Z; freezes 142 active policies."
}
```

```http
HTTP/1.1 201 Created
Location: /api/bundles/3f7c7a48-19f1-4f4d-8e1e-1c8e1f59b3a2
Content-Type: application/json

{
  "id": "3f7c7a48-19f1-4f4d-8e1e-1c8e1f59b3a2",
  "name": "conductor-v2.14.0-policies",
  "snapshotHash": "9b8a…",
  "state": "Active",
  "createdAt": "2026-05-06T18:00:01.234Z",
  "createdBySubjectId": "user:release-engineer"
}
```

### MCP

```json
{
  "tool": "policy.bundle.create",
  "arguments": {
    "name": "conductor-v2.14.0-policies",
    "rationale": "Cut at 2026-05-06T18:00:00Z; freezes 142 active policies.",
    "description": "Pinned for Conductor v2.14.0 release"
  }
}
```

Returns a JSON-serialised `BundleDto` envelope (same shape as REST).

### gRPC

```proto
BundleService.CreateBundle(CreateBundleRequest{
  name = "conductor-v2.14.0-policies",
  description = "Pinned for Conductor v2.14.0 release",
  rationale = "Cut at 2026-05-06T18:00:00Z; freezes 142 active policies."
})
// → BundleMessage
```

### CLI

```bash
andy-policies-cli bundles create \
  --name conductor-v2.14.0-policies \
  --description "Pinned for Conductor v2.14.0 release" \
  --rationale "Cut at 2026-05-06T18:00:00Z; freezes 142 active policies."
```

The CLI honours the global `--server-url` and `--token` flags from
the shared CLI contract; `--output json` emits the DTO verbatim,
`--output table` is human-friendly.

## 4. How to resolve against a bundle

Permission required: `andy-policies:bundle:read`. Cached HTTP
responses carry `ETag: "<snapshotHash>"` and
`Cache-Control: public, max-age=31536000, immutable` — bundles
are immutable post-insert, so consumers should honour the cache
headers (a 304 round-trip costs only the request line).

### REST

```http
GET /api/bundles/3f7c7a48-19f1-4f4d-8e1e-1c8e1f59b3a2/resolve?targetType=Repo&targetRef=rivoli-ai/conductor HTTP/1.1
Authorization: Bearer eyJ…
If-None-Match: "9b8a…"   # snapshotHash from the previous fetch; saves bytes on hit
```

Response carries the bundle's snapshotHash + capturedAt + the
matched bindings (post-tighten-only fold; same shape as the live
`/api/bindings/resolve`).

The pinned-policy lookup answers "give me the frozen policy I
pinned":

```http
GET /api/bundles/{bundleId}/policies/{policyId} HTTP/1.1
```

Returns a `BundlePinnedPolicyDto` carrying the policy version's
enforcement / severity / scopes / rules JSON exactly as captured
at bundle-create time.

### MCP

```json
{
  "tool": "policy.bundle.resolve",
  "arguments": {
    "bundleId": "3f7c7a48-19f1-4f4d-8e1e-1c8e1f59b3a2",
    "targetType": "Repo",
    "targetRef": "rivoli-ai/conductor"
  }
}
```

### gRPC

```proto
BundleService.ResolveBundle(ResolveBundleRequest{
  id = "3f7c7a48-19f1-4f4d-8e1e-1c8e1f59b3a2",
  target_type = "Repo",
  target_ref = "rivoli-ai/conductor"
})
// → ResolveBundleResponse with bundle_id, snapshot_hash, captured_at, repeated bindings
```

### CLI

```bash
andy-policies-cli bundles resolve \
  3f7c7a48-19f1-4f4d-8e1e-1c8e1f59b3a2 \
  --target-type Repo \
  --target-ref rivoli-ai/conductor
```

## 5. The bundle-pinning gate (default ON in production)

When `andy.policies.bundleVersionPinning` is `true` (the manifest
default; see [ADR 0008 §5](../adr/0008-bundle-pinning.md) for the
gate semantics), the live `/api/policies*` /
`/api/bindings/resolve` / `/api/scopes/{id}/effective-policies`
endpoints **require** `?bundleId=`. Absence yields:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "https://andy.local/problems/bundle-pin-required",
  "title": "Pinning required",
  "status": 400,
  "detail": "Pinning required: pass ?bundleId=<guid>."
}
```

Consumers SHOULD branch on `type` rather than on the detail
message; the URI is the stable contract.

For development environments, the operator can flip
`andy.policies.bundleVersionPinning=false` via andy-settings and
the gate becomes optional — absence falls through to live state.
**Production must keep pinning ON.**

## 6. How to diff two bundles

`bundles diff` emits an RFC-6902 JSON Patch between two bundles'
snapshot bytes. Recommended for release notes ("here's what
changed between v2.13.0's bundle and v2.14.0's bundle").

### CLI (recommended)

```bash
andy-policies-cli bundles diff \
  --from 3f7c7a48-19f1-4f4d-8e1e-1c8e1f59b3a2 \
  --to   d9e1c2a4-5b1c-4f0a-9e8b-7c2d3e4f5a6b
```

Output (truncated):

```json
[
  {"op":"replace","path":"/Policies/0/Enforcement","value":"MUST"},
  {"op":"add","path":"/Bindings/4","value":{"BindingId":"…","TargetRef":"repo:rivoli-ai/new","BindStrength":"Mandatory"}},
  {"op":"remove","path":"/Overrides/2"}
]
```

The patch is canonical: two invocations on the same `(from, to)`
pair produce byte-identical output (covered by
`BundleDiffServiceTests`). Pipe through any RFC-6902-compliant
patch applier — the JSON paths address the materialised entries
directly.

### REST

```http
GET /api/bundles/{fromId}/diff?to={toId} HTTP/1.1
```

### gRPC

```proto
BundleService.DiffBundles(DiffBundlesRequest{ from_id = "…", to_id = "…" })
// → DiffBundlesResponse with rfc6902_patch_json + op_count
```

## 7. Troubleshooting

### "I forgot to pin and got 400"

Pinning is on (the production default). Re-issue with
`?bundleId=<your-pinned-id>`. If you're in a dev environment and
need to read live state, ask your operator to flip
`andy.policies.bundleVersionPinning=false`.

### "The bundle resolution omits a policy I expected"

The snapshot only captures `LifecycleState.Active` versions.
A policy in `Draft` at create time is not in the bundle by
design — drafts aren't promotable. If the policy was Active
when you expected the bundle to capture it but isn't in the
snapshot, check the `capturedAt` timestamp on the bundle and
the policy version's `publishedAt`; the bundle was created
before the publish.

### "The bundle resolution includes an unexpected override"

The snapshot captures `OverrideState.Approved` overrides whose
`expiresAt > capturedAt`. A `Proposed` override is not yet
effective; an `Expired` override has been swept by the reaper
(P5.3). If you see an active override you didn't expect, it
genuinely was approved + unexpired at bundle-create time —
review with the override author.

### "The diff is empty but I changed a policy"

The two bundles you're diffing were both taken before the
change. Re-run `bundles create` and diff the new bundle against
the older one.

### "Bundle creation is slow"

The serializable transaction in `BundleService.CreateAsync`
synchronises with concurrent publishes; expect occasional
retries on a busy catalog. The 100-policy p95 budget is < 1s
in the integration tests; the 1000-policy nightly target is
< 500ms p95 (see ADR 0008 §"Future work").

### "I want to delete a bundle"

Soft-delete only. `andy-policies-cli` doesn't currently expose
a delete sub-command (deliberate scope reduction in P8.6); use
the REST `DELETE /api/bundles/{id}` directly with a
`?rationale=<reason>` query parameter. The bundle row stays in
the table; only the `state` flips.

## 8. Cross-references

- [ADR 0008 — Bundle pinning](../adr/0008-bundle-pinning.md) — architectural decisions
- [ADR 0006 — Audit hash chain](../adr/0006-audit-hash-chain.md) — the canonical-JSON + SHA-256 algorithm bundles share
- [Permission catalog](../reference/permission-catalog.md) — `andy-policies:bundle:*` permissions
- [Edit matrix](../design/edit-matrix.md) — how the bundle endpoints map to author / approver roles
- [`config/registration.json`](https://github.com/rivoli-ai/andy-policies/blob/main/config/registration.json) — the manifest where `andy.policies.bundleVersionPinning` is declared
