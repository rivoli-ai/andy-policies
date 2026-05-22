---
title: Lifecycle
order: 7
tags: [lifecycle, transitions]
---

# Lifecycle

## Overview

Policies move through a defined lifecycle that ensures proper review and approval before becoming enforceable. Each status change is logged and can require authorization depending on your governance rules.

## States

```
┌─────────┐    submit    ┌─────────┐   activate   ┌─────────┐
│  Draft  │ ───────────► │ Review  │ ───────────► │ Active  │
└─────────┘              └─────────┘              └─────────┘
     ▲                                               │
     │                                               │
     └────────────  archive  ────────────────────────┘
```

| Status | Description |
|--------|-------------|
| **Draft** | Initial state; editable by authors |
| **Review** | Under evaluation; read-only pending approval |
| **Active** | Approved and enforceable |
| **Archived** | Retired; kept for audit history |

## Transitions

### Draft → Review

- Triggered by the **Submit** action
- Requires all mandatory fields to be populated
- Notifies assigned reviewers

### Review → Active

- Triggered by an **Approve** decision
- May require one or more approvers based on policy criticality
- Once active, the policy version is locked

### Active → Draft

- Triggered by the **Revise** action
- Creates a new minor version copy in Draft state
- Previous active version remains enforceable until the new version is activated

### Any → Archived

- Triggered by the **Archive** action
- Removes the policy from active enforcement
- Preserves full history for compliance audits

## Automation

Lifecycle transitions can be automated via:

- **Webhooks** — Call external systems on state change
- **Scheduled Rules** — Auto-archive policies past expiration date
- **API** — Programmatic transitions via `/api/policies/{id}/transitions`
