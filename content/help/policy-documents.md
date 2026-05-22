---
title: Policy Documents
order: 6
tags: [policies, authoring]
---

# Policy Documents

## Overview

Policy documents are the core artifacts managed by the Andy Policies service. Each document represents a governance rule, standard, or guideline that can be versioned, reviewed, and enforced across your organization.

## Document Structure

Every policy document contains:

- **Title** — Human-readable name
- **Slug** — Unique URL-friendly identifier
- **Content** — Markdown body with the policy text
- **Version** — Semantic version string
- **Status** — Draft, Active, or Archived
- **Owner** — Team or individual responsible

## Creating a Policy

1. Navigate to **Policies** → **New Policy**
2. Fill in the title and slug
3. Write the content using Markdown
4. Assign an owner and set initial status to **Draft**
5. Save to create the first version (`1.0.0`)

## Versioning

Policies follow semantic versioning:

| Change Type | Version Bump | Example |
|-------------|--------------|---------|
| Minor edit | Patch | `1.0.0` → `1.0.1` |
| Substantial change | Minor | `1.0.0` → `1.1.0` |
| Complete rewrite | Major | `1.0.0` → `2.0.0` |

## Templates

Use built-in templates to standardize policy structure:

- **Security Policy** — Covers access controls and data handling
- **Compliance Standard** — Maps to regulatory requirements
- **Operational Guide** — Runbooks and procedural rules
- **Custom** — Start from a blank document

## Search and Discovery

Policies are indexed by title, content, and tags. Use the catalog search or filter by status, owner, or binding scope to find relevant documents quickly.
