# Edit matrix

For every catalog mutation, this table names the required permission code from
[`config/registration.json`](../../config/registration.json), whether the actor
plays the *author* or *approver* role, and whether the action permits a single
caller acting in both roles.

The permission *vocabulary* is in
[Permission catalog](../reference/permission-catalog.md) (auto-generated from
the manifest); the *delegation* and *self-approval* decisions are in
[ADR 0007 — Edit RBAC](../adr/0007-edit-rbac.md).

## Conventions

- **Permission**: the dotted code passed to `IRbacChecker.CheckAsync` (and on
  the wire to `POST {AndyRbac:BaseUrl}/api/check`).
- **Author?**: `yes` if the actor plays the author role for this action.
- **Approver?**: `yes` if the actor plays the approver role.
- **Self-approve?**: whether a single caller may execute this action when the
  same `subjectId` already authored the underlying entity.
  - `n/a` — the action has no separate author identity to compare against.
  - `allowed` — same caller is fine; no domain invariant blocks it.
  - **`forbidden`** — the service rejects the action even when andy-rbac
    answers `Allowed=true` for both author + approver permissions. The check
    runs *before* the RBAC call (fast-path domain invariant — see ADR 0007 §4).
    Returns **HTTP 403** with ProblemDetails `errorCode =
    "policy.publish_self_approval_forbidden"` (publish) or
    `policy.override_self_approval_forbidden` (override approve); no state
    mutation.

## Matrix

### Policy authoring + lifecycle (Epic P1, Epic P2)

| Action | Permission | Author? | Approver? | Self-approve? |
|---|---|---|---|---|
| Create draft policy | `andy-policies:policy:author` | yes | no | n/a |
| Update draft (in-place edit) | `andy-policies:policy:author` | yes | no | n/a |
| Bump from a previous version | `andy-policies:policy:author` | yes | no | n/a |
| Publish a draft (Draft → Active) | `andy-policies:policy:publish` | no | yes | **forbidden** |
| Wind-down (Active → WindingDown) | `andy-policies:policy:transition` | no | yes | allowed |
| Retire (Active/WindingDown → Retired) | `andy-policies:policy:transition` | no | yes | allowed |

### Bindings (Epic P3)

| Action | Permission | Author? | Approver? | Self-approve? |
|---|---|---|---|---|
| Create binding (PolicyVersion → target) | `andy-policies:binding:manage` | n/a | n/a | n/a |
| Soft-delete binding | `andy-policies:binding:manage` | n/a | n/a | n/a |

### Scopes (Epic P4)

| Action | Permission | Author? | Approver? | Self-approve? |
|---|---|---|---|---|
| Create scope node | `andy-policies:scope:manage` | n/a | n/a | n/a |
| Delete scope node (leaf) | `andy-policies:scope:manage` | n/a | n/a | n/a |

### Overrides (Epic P5)

| Action | Permission | Author? | Approver? | Self-approve? |
|---|---|---|---|---|
| Propose an override | `andy-policies:override:propose` | yes | no | n/a |
| Approve a proposed override | `andy-policies:override:approve` | no | yes | **forbidden** |
| Revoke an override | `andy-policies:override:revoke` | n/a | n/a | n/a |

### Audit (Epic P6)

| Action | Permission | Author? | Approver? | Self-approve? |
|---|---|---|---|---|
| Read audit events | `andy-policies:audit:read` | n/a | n/a | n/a |
| Verify the audit hash chain | `andy-policies:audit:verify` | n/a | n/a | n/a |
| Export an NDJSON audit bundle | `andy-policies:audit:export` | n/a | n/a | n/a |

### Bundles (Epic P8)

| Action | Permission | Author? | Approver? | Self-approve? |
|---|---|---|---|---|
| Create a bundle (snapshot) | `andy-policies:bundle:create` | n/a | n/a | n/a |
| Soft-delete a bundle | `andy-policies:bundle:delete` | n/a | n/a | n/a |

## Cross-surface enforcement

Every surface (REST, MCP, gRPC) delegates to the same `IRbacChecker` and
applies the same self-approval domain invariant before the RBAC call:

| Surface | Enforcement seam | Implemented in |
|---|---|---|
| REST | `[Authorize(Policy = "andy-policies:…")]` per controller action; `RbacAuthorizationHandler` extracts subject + groups + route-derived resource instance and delegates. | P7.4 (#57) |
| MCP | `McpRbacGuard.EnsureAsync` invoked at the top of every mutating tool body; denials translate to a typed `policy.<area>.forbidden: <reason>` tool result. `[RbacGuard]` attribute pins the contract for review + reflective coverage tests. | P7.6 (#64) |
| gRPC | Global `RbacServerInterceptor` consults `GrpcMethodPermissionMap`; unmapped RPCs on enforced services hard-fail with `RpcException(Internal)` (fail-closed, never silent allow). `ItemsService` is the one allow-listed bypass (template scaffolding). | P7.6 (#64) |

A reflection-driven coverage test
(`GrpcPermissionMapCoverageTests`) walks every RPC on the proto-generated
`*ServiceBase` classes and asserts a permission code is mapped — adding a new
RPC without a mapping fails CI rather than the runtime.
