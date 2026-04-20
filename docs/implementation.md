# Implementation

## Backend (.NET 8)

### Program.cs

The API entry point configures:
1. Database (PostgreSQL or SQLite)
2. Authentication (Andy Auth JWT Bearer)
3. Authorization (Andy RBAC)
4. OpenTelemetry (traces, metrics)
5. Swagger/OpenAPI
6. CORS (Angular + MCP clients)
7. MCP Server
8. gRPC services
9. Health checks
10. Static files (Angular SPA)

### Service Layer

Services implement interfaces from the Application layer and are used by:
- REST controllers
- MCP tools
- gRPC services

This ensures consistent behavior across all API protocols.

### Database

Entity Framework Core with support for:
- PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL`)
- SQLite (`Microsoft.EntityFrameworkCore.Sqlite`)

Switch via `Database:Provider` configuration.

Auto-migration runs in Development mode.

## Frontend (Angular 18)

### Authentication

Uses `angular-auth-oidc-client` for OIDC integration with Andy Auth:
- `auth.interceptor.ts` - Attaches Bearer tokens to API requests
- `auth.guard.ts` - Protects routes, redirects to login
- `callback.component.ts` - Handles OAuth callback

### API Communication

`ApiService` provides typed HTTP methods for all API endpoints.

## CLI

Uses `System.CommandLine` for argument parsing. Supports:
- `--api-url` - API base URL
- `--token` - Bearer token for authentication
- Resource-specific subcommands (list, get, create, delete)
