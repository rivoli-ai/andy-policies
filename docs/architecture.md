# Architecture

## Overview

Andy Policies follows Clean Architecture with the following layers:

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

## Project Structure

| Layer | Project | Purpose |
|-------|---------|---------|
| Domain | `Andy.Policies.Domain` | Entities, enums, value objects |
| Application | `Andy.Policies.Application` | Interfaces, DTOs, contracts |
| Infrastructure | `Andy.Policies.Infrastructure` | EF Core, service implementations |
| API | `Andy.Policies.Api` | REST, MCP, gRPC endpoints |
| Shared | `Andy.Policies.Shared` | Shared types across projects |
| CLI | `Andy.Policies.Cli` | Command-line interface |

## API Protocols

### REST (Swagger)
Standard HTTP API with OpenAPI documentation available at `/swagger`.

### MCP (Model Context Protocol)
AI assistant integration endpoint at `/mcp`. MCP tools share the same service layer as REST controllers.

### gRPC
High-performance RPC defined in `Protos/items.proto`. Uses the same service layer.

## Database Strategy

- **PostgreSQL** (default): Used in standalone deployment
- **SQLite** (embedded): Used when bundled with Conductor

Configured via `Database:Provider` in appsettings or environment variable.

## Authentication Flow

```
User → Angular SPA → Andy Auth (OIDC) → JWT Token → API (Bearer Auth)
```

## External Dependencies

- **Andy Auth** (port 5001) - OAuth2/OIDC identity provider
- **Andy RBAC** (port 5003) - Role-based access control
- **Andy Settings** (port 5300) - Centralized configuration (optional)
