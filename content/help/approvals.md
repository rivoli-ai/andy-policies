---
title: Approvals
order: 9
tags: [approvals, governance]
---

# Approvals

## Overview

The approvals workflow ensures that policy changes are reviewed by the right people before taking effect. Approval requirements can be configured per policy or applied globally through governance rules.

## Approval Policies

| Policy Type | Description | Typical Use Case |
|-------------|-------------|------------------|
| **Single Approver** | One designated reviewer must approve | Low-risk operational guidelines |
| **Multi-Approver** | Multiple reviewers must approve | Security or compliance policies |
| **Quorum** | Majority of a defined group must approve | High-impact organizational standards |
| **Automatic** | No approval required; auto-approved on submit | Templates or draft iterations |

## Requesting Approval

1. Ensure the policy is in **Draft** state
2. Click **Submit for Review**
3. Select approvers or let the system assign based on governance rules
4. Add a summary of changes for reviewers
5. Submit — the policy moves to **Review** state

## Reviewing Requests

Approvers receive notifications via:

- In-app alerts
- Email (if configured)
- Slack / Teams integration (if enabled)

### Actions

- **Approve** — Policy moves to **Active** (or next required reviewer)
- **Reject** — Policy returns to **Draft** with feedback
- **Request Changes** — Policy stays in **Review** with comments

## Delegation

Approvers can delegate their review to another user when:

- They are out of office
- A conflict of interest exists
- Domain expertise is better suited to a colleague

Delegation is time-bound and logged for audit.

## Audit Trail

Every approval action creates an immutable record containing:

- Approver identity
- Timestamp
- Decision (Approved / Rejected)
- Comments
- Policy version at time of approval

Access the full history from a policy’s **Approvals** tab or via the `/api/policies/{id}/approvals` endpoint.
