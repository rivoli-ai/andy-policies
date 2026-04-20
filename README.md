# Andy Policies

Governance policy catalog — structured, versioned policy documents with lifecycle and audit trail, consumed by Conductor for story admission, verification, and compliance reporting (content only; enforcement lives in consumers)

## Overview

Andy Policies is a microservice in the [Andy ecosystem](https://github.com/rivoli-ai) providing Governance policy catalog — structured, versioned policy documents with lifecycle and audit trail, consumed by Conductor for story admission, verification, and compliance reporting (content only; enforcement lives in consumers).

### Features

- **REST API** - Full CRUD API with Swagger documentation
- **MCP Tools** - AI-assisted management via Model Context Protocol
- **gRPC** - High-performance RPC for service-to-service communication
- **Angular SPA** - Web-based management interface
- **CLI Tool** - Command-line resource management
- **OAuth2/OIDC** - Authentication via Andy Auth
- **RBAC** - Role-based access control via Andy RBAC
- **OpenTelemetry** - Distributed tracing, metrics, and logging

## Quick Start

```bash
# Start infrastructure
docker compose up -d postgres

# Run the API
cd src/Andy.Policies.Api
dotnet run

# Run the client (in a separate terminal)
cd client
npm install && npm start
```

## Architecture

| Layer | Project | Purpose |
|-------|---------|---------|
| Domain | `Andy.Policies.Domain` | Entities, enums |
| Application | `Andy.Policies.Application` | Interfaces, DTOs |
| Infrastructure | `Andy.Policies.Infrastructure` | EF Core, services |
| API | `Andy.Policies.Api` | REST, MCP, gRPC, auth |
| Shared | `Andy.Policies.Shared` | Shared types |
| CLI | `Andy.Policies.Cli` | Command-line tool |

## Documentation

Full documentation available at [rivoli-ai.github.io/andy-policies](https://rivoli-ai.github.io/andy-policies/).

## Ports

| Service | Port |
|---------|------|
| API HTTPS | 5112 |
| API HTTP | 5113 |
| PostgreSQL | 5439 |
| Client (Angular) | 4206 |

## Docker

```bash
# Full stack (PostgreSQL + API)
docker compose up -d

# Embedded mode (SQLite, for Conductor)
docker compose -f docker-compose.embedded.yml up -d
```

## Testing

```bash
# Backend tests
dotnet test

# Frontend tests
cd client && npm test
```

## License

Apache 2.0 - See [LICENSE](LICENSE) for details.

Copyright (c) Rivoli AI 2026
