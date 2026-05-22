---
title: Bindings
order: 8
tags: [bindings, scope]
---

# Bindings

## Overview

Bindings connect policies to the resources they govern. A binding defines *where* a policy applies, allowing fine-grained control over enforcement scope.

## Scope Types

| Scope | Description | Example |
|-------|-------------|---------|
| **Global** | Applies to all resources | Organization-wide security baseline |
| **Environment** | Applies to a specific environment | Production change-management rules |
| **Resource Group** | Applies to a tagged collection | PCI-DSS scope resources |
| **Resource** | Applies to a single resource | Exception policy for a legacy database |

## Binding Rules

- A policy can have multiple bindings
- Multiple policies can bind to the same scope
- Bindings are evaluated at enforcement time
- More specific scopes override broader ones (Resource > Group > Environment > Global)

## Managing Bindings

### Via UI

1. Open a policy and select the **Bindings** tab
2. Click **Add Binding**
3. Choose scope type and target
4. Save to apply immediately

### Via API

```bash
POST /api/policies/{policyId}/bindings
{
  "scope": "Environment",
  "target": "production",
  "priority": 100
}
```

## Inheritance

Child resources inherit bindings from their parents unless explicitly overridden:

- A resource in the `production` environment inherits environment-level bindings
- Adding a direct resource binding takes precedence over inherited ones

## Audit

All binding changes are recorded with:

- Timestamp
- Actor identity
- Previous and new scope
- Reason (optional but recommended)
