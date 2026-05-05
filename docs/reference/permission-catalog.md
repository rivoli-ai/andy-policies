# Permission catalog

<!-- GENERATED — do not edit. Source: config/registration.json. Regenerate via `dotnet run --project tools/GenerateRbacDocs`. -->

Application code: `andy-policies`

## Resource types

| Code | Name | Supports instances |
|------|------|--------------------|
| `policy` | Policy | yes |
| `binding` | Policy Binding | yes |
| `override` | Policy Override | yes |
| `scope` | Scope Node | yes |
| `bundle` | Policy Bundle | yes |
| `audit` | Audit Event | yes |
| `settings` | Settings | no |

## Permissions

| Code | Name | Resource type |
|------|------|---------------|
| `andy-policies:policy:read` | Read policies | `policy` |
| `andy-policies:policy:author` | Author drafts | `policy` |
| `andy-policies:policy:publish` | Publish a draft | `policy` |
| `andy-policies:policy:transition` | Wind-down / retire | `policy` |
| `andy-policies:binding:read` | Read bindings | `binding` |
| `andy-policies:binding:manage` | Create / delete bindings | `binding` |
| `andy-policies:scope:read` | Read scope tree | `scope` |
| `andy-policies:scope:manage` | Create / delete scopes | `scope` |
| `andy-policies:override:read` | Read overrides | `override` |
| `andy-policies:override:propose` | Propose an override | `override` |
| `andy-policies:override:approve` | Approve an override | `override` |
| `andy-policies:override:revoke` | Revoke an override | `override` |
| `andy-policies:bundle:read` | Read bundles | `bundle` |
| `andy-policies:bundle:create` | Create a bundle | `bundle` |
| `andy-policies:bundle:delete` | Soft-delete a bundle | `bundle` |
| `andy-policies:audit:read` | Read audit events | `audit` |
| `andy-policies:audit:export` | Export audit chain | `audit` |
| `andy-policies:audit:verify` | Verify audit chain | `audit` |

## Roles

| Code | Name | Description | Permissions |
|------|------|-------------|-------------|
| `admin` | Administrator | Full access: author, publish, transition, edit bindings, manage overrides. | `*` (all permissions) |
| `author` | Policy Author | May author drafts and propose publish; cannot approve own publishes. | `andy-policies:policy:read`, `andy-policies:policy:author`, `andy-policies:binding:read`, `andy-policies:scope:read`, `andy-policies:override:read`, `andy-policies:override:propose`, `andy-policies:bundle:read`, `andy-policies:audit:read` |
| `approver` | Publish Approver | Approves transitions of drafts to active versions. | `andy-policies:policy:read`, `andy-policies:policy:publish`, `andy-policies:policy:transition`, `andy-policies:binding:read`, `andy-policies:binding:manage`, `andy-policies:scope:read`, `andy-policies:override:read`, `andy-policies:override:approve`, `andy-policies:override:revoke`, `andy-policies:bundle:read`, `andy-policies:bundle:create`, `andy-policies:audit:read` |
| `risk` | Risk / Compliance | Read-all plus audit-export. | `andy-policies:policy:read`, `andy-policies:binding:read`, `andy-policies:scope:read`, `andy-policies:override:read`, `andy-policies:bundle:read`, `andy-policies:audit:read`, `andy-policies:audit:export`, `andy-policies:audit:verify` |
| `viewer` | Viewer | Read-only access to active policies and public audit trail. | `andy-policies:policy:read`, `andy-policies:binding:read`, `andy-policies:scope:read`, `andy-policies:override:read`, `andy-policies:bundle:read` |

## Counts

- Resource types: 7
- Permissions: 18
- Roles: 5
