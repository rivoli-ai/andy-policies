---
title: Architecture
order: 4
tags: [architecture, design, layers]
---

# Architecture

## Overview

This service follows Clean Architecture with strict dependency rules.

```
┌─────────────────────────────────────────────┐
│                Angular SPA                   │
│           (client/ directory)                │
├─────────────────────────────────────────────┤
│              API Layer                       │
│    REST Controllers │ MCP Tools │ gRPC       │
├─────────────────────────────────────────────┤
│           Application Layer                  │
│       Interfaces │ DTOs │ Contracts          │
├─────────────────────────────────────────────┤
│         Infrastructure Layer                 │
│   EF Core │ Services │ External Integrations │
├─────────────────────────────────────────────┤
│            Domain Layer                      │
│         Entities │ Enums │ Value Objects      │
└─────────────────────────────────────────────┘
```

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Backend | .NET 8 |
| Frontend | Angular 18 |
| Database | PostgreSQL (default) / SQLite (embedded) |
| Auth | Andy Auth (OAuth2/OIDC) |
| Authorization | Andy RBAC |
| Settings | Andy Settings |
| Telemetry | OpenTelemetry |
| Containerization | Docker |

## External Dependencies

| Service | Port | Purpose |
|---------|------|---------|
| Andy Auth | 5001 | Identity provider |
| Andy RBAC | 5003 | Access control |
| Andy Settings | 5300 | Configuration |

## Database Strategy

- **PostgreSQL**: Default for standalone deployment
- **SQLite**: Used when embedded in Conductor or for lightweight deployments
- Switch via `Database:Provider` configuration (`PostgreSql` or `Sqlite`)
