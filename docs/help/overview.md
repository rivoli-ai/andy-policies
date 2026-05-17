---
title: Andy Policies Overview
slug: andy-policies-overview
order: 1
tags: [policies, governance, compliance]
---

# Andy Policies Overview

Andy Policies is the governance policy catalog for the Andy ecosystem. It stores structured, versioned policy documents with a lifecycle and audit trail. Conductor consumes these policies for story admission, agent-run verification, and compliance reporting.

## What it does

- Stores policies with structured fields (id, version, status, applies-to, content, effective-from).
- Tracks policy lifecycle: `draft → review → active → deprecated → archived`.
- Records every read and every status transition in an audit log retained per the org's retention setting.
- Serves policies to consumers (Conductor, agents) over a versioned HTTP API; consumers cache by version.
- Surfaces a diff view between policy versions for review.

## Key concepts

- **Policy document** — the unit. Has metadata, content, and an immutable version.
- **Applies-to** — the scope the policy covers (`agent-run`, `repo-write`, `data-export`, …). Consumers query by this.
- **Enforcement vs content** — Policies stores the *content*; enforcement (the actual gate) lives in the consuming service. This service is the source of truth, not the bouncer.

## Where it fits

Conductor's Tasks/Agents/Docs paths read policies at the right pre-action moments. Depends on Auth, RBAC, and Settings. Most policy data is read-mostly; writes happen rarely (admin only).

## Configuration

Policy retention, audit log retention, and review-required toggles live under `andy.policies.*` in `andy-settings`. The catalog seed (default policies for a fresh installation) ships in `config/registration.json`. Conductor surfaces the live catalog in **Policies** (top-level tab).

## Troubleshooting

- **A policy isn't being applied** — verify the consuming service queries the right `applies-to` scope and check its cached version is current.
- **Edit blocked with "must go through review"** — the policy is in `active`. Either move it to `draft` (revoking the active version) or publish a new version.
- **Audit gaps** — Policies logs every access; gaps usually mean the audit subscriber lost its NATS connection. Restart the consumer.
