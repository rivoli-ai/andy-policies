# Andy Policies — Development Guide

## Project Overview

Andy Policies is an Andy ecosystem microservice: Governance policy catalog — structured, versioned policy documents with lifecycle and audit trail, consumed by Conductor for story admission, verification, and compliance reporting (content only; enforcement lives in consumers)

## Tech Stack

- **Runtime**: .NET 8.0
- **Frontend**: Angular 18 (standalone components, OIDC auth)
- **Database**: PostgreSQL (default), SQLite (embedded/Conductor mode)
- **ORM**: Entity Framework Core 8
- **Auth**: OAuth2/OIDC via Andy Auth (OpenIddict, JWT Bearer)
- **Authorization**: Andy RBAC (role-based access control)
- **Settings**: Andy Settings (centralized configuration)
- **API**: REST/Swagger + MCP (Model Context Protocol) + gRPC
- **Telemetry**: OpenTelemetry (traces, metrics, OTLP export)
- **Testing**: xUnit, WebApplicationFactory, Karma/Jasmine
- **Containerization**: Docker multi-stage, docker-compose

## Architecture

Clean Architecture with five layers:

| Layer | Project | Purpose |
|-------|---------|---------|
| Domain | `Andy.Policies.Domain` | Entities, enums, value objects — no dependencies |
| Application | `Andy.Policies.Application` | Interfaces, DTOs, contracts |
| Infrastructure | `Andy.Policies.Infrastructure` | EF Core, service implementations, external integrations |
| API | `Andy.Policies.Api` | REST controllers, MCP tools, gRPC services, auth, middleware |
| Shared | `Andy.Policies.Shared` | Types shared across projects |
| CLI | `Andy.Policies.Cli` | Command-line interface (System.CommandLine) |

**Dependency rule**: outer layers depend on inner layers, never the reverse. API → Infrastructure → Application → Domain.

## Repository Structure

```
andy-policies/
├── CLAUDE.md                    # This file
├── README.md
├── LICENSE                      # Apache 2.0
├── Andy.Policies.sln
├── Directory.Build.props        # Shared build properties (Rivoli AI metadata)
├── nuget.config                 # NuGet sources (nuget.org + local-packages/)
├── Dockerfile                   # Multi-stage: Node + .NET + Runtime (with cert injection)
├── docker-compose.yml           # PostgreSQL + API
├── docker-compose.embedded.yml  # SQLite mode (Conductor integration)
├── mkdocs.yml                   # Documentation site config
├── certs/                       # Corporate CA certificates (not committed)
├── client/                      # Angular 18 SPA
│   ├── src/app/
│   │   ├── core/auth/           # OIDC callback
│   │   ├── core/guards/         # Route guards (auth)
│   │   ├── core/interceptors/   # HTTP auth interceptor (Bearer token)
│   │   ├── features/            # Feature modules (dashboard, items, ...)
│   │   └── shared/services/     # API service layer
│   ├── src/environments/        # Environment configs (dev, docker, prod)
│   ├── angular.json
│   └── package.json
├── config/
│   ├── auth-seed.sql            # Andy Auth OAuth client registration
│   └── rbac-seed.json           # Andy RBAC application/role/permission seed
├── docs/                        # MkDocs documentation
├── examples/                    # Multi-language API usage examples
├── local-packages/              # Local NuGet packages
├── src/                         # .NET source projects
├── tests/                       # Unit + integration tests
└── tools/                       # CLI tool
```

## Coding Conventions

### C# Style
- .NET 8 with `<LangVersion>latest</LangVersion>`, nullable enabled, warnings as errors
- Use file-scoped namespaces (`namespace X;`)
- Use primary constructors where appropriate
- Prefer records for DTOs and value objects
- Async all the way — return `Task<T>`, use `CancellationToken`
- No `#region` blocks

### Naming
- Entities: plain names (`Item`, `Project`)
- DTOs: `{Name}Dto` (e.g., `ItemDto`)
- Request models: `Create{Name}Request`, `Update{Name}Request`
- Services: `I{Name}Service` (interface), `{Name}Service` (implementation)
- Controllers: `{Name}Controller` (plural resource name, e.g., `ItemsController`)
- MCP tools: static class `ServiceTools` with `[McpServerTool]` methods
- gRPC services: `{Name}GrpcService`
- Test classes: `{ClassUnderTest}Tests`

### API Design
- REST controllers and MCP tools share the same service layer — never duplicate business logic
- gRPC services also use the same service layer
- Controllers use `[Authorize]` by default; use `[AllowAnonymous]` only for health/public endpoints
- Return DTOs from controllers, never domain entities

### Frontend (Angular)
- Standalone components only (no NgModules)
- Use `angular-auth-oidc-client` for OIDC — never roll custom auth
- All API calls go through `ApiService` (typed HTTP methods)
- Route guards via `authGuard` (functional guard)
- HTTP interceptor attaches Bearer tokens to `/api` requests

## Common Commands

```bash
# Build
dotnet build

# Run tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults

# Run the API (Development mode)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Andy.Policies.Api

# Run the API with SQLite
Database__Provider=Sqlite dotnet run --project src/Andy.Policies.Api

# Frontend
cd client && npm install && npm start

# Frontend tests
cd client && npm test -- --watch=false --browsers=ChromeHeadless

# Docker (full stack)
docker compose up -d

# Docker (embedded/SQLite)
docker compose -f docker-compose.embedded.yml up -d

# CLI
dotnet run --project tools/Andy.Policies.Cli -- items list

# EF migrations (create)
dotnet ef migrations add MigrationName --project src/Andy.Policies.Infrastructure --startup-project src/Andy.Policies.Api

# EF migrations (apply)
dotnet ef database update --project src/Andy.Policies.Infrastructure --startup-project src/Andy.Policies.Api
```

## Ports

| Service | Port |
|---------|------|
| API HTTPS | 5112 |
| API HTTP | 5113 |
| PostgreSQL | 5439 |
| Client (Angular dev) | 4200 |
| Client (Docker) | 4206 |

## External Dependencies

| Service | Port | Purpose |
|---------|------|---------|
| Andy Auth | 5001 | OAuth2/OIDC identity provider |
| Andy RBAC | 5003 | Role-based access control |
| Andy Settings | 5300 | Centralized configuration |

## Database

- **PostgreSQL** (default): Set `Database:Provider` to `PostgreSql` in appsettings
- **SQLite** (embedded): Set `Database:Provider` to `Sqlite` — used for Conductor bundling
- Auto-migration runs in Development mode
- Design-time factory in `Infrastructure/Data/DesignTimeDbContextFactory.cs`

## Authentication & Authorization

- **Auth bypass**: When `AndyAuth:Authority` is empty, all endpoints are open (dev convenience)
- **JWT Bearer**: When configured, API requires valid tokens from Andy Auth
- **Test user**: `test@andy.local` / `Test123!` (seeded in Andy Auth for non-production)
- **RBAC**: Application code `andy-policies` registered in Andy RBAC with admin/user/viewer roles
- **Swagger**: Bearer security scheme configured — use "Authorize" button in Swagger UI

## Testing Requirements

- **Always write tests** for new code in `tests/` assemblies
- **Run `dotnet test` before claiming completion**
- Unit tests use EF Core InMemory provider
- Integration tests use `WebApplicationFactory<Program>`
- Frontend tests use Karma/Jasmine with ChromeHeadless

## Code Quality

- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` — zero warnings policy
- Run `dotnet format` before committing
- All tests must pass before push
- Copyright header: `// Copyright (c) Rivoli AI 2026. All rights reserved.`

## Secret Scanning

Three layers of protection against committing secrets:

1. **Pre-commit hook** (local): Scans staged files for passwords, API keys, tokens, private keys
   - Install: `./scripts/setup-git-hooks.sh`
   - Bypass for known dev defaults: `git commit --no-verify`
2. **Gitleaks** (CI): Runs on every push/PR as the first CI job — blocks build if secrets found
   - Config: `.gitleaks.toml` (allowlists for dev defaults)
3. **GitHub secret scanning** (if enabled on the repo)

**Never commit**: real API keys, production passwords, private keys, personal tokens.
**Allowed**: dev-only defaults like `_dev_password`, `Test123!`, `devcert` (allowlisted in `.gitleaks.toml`).

## CI/CD

- **ci.yml**: Build + test (.NET and Angular) on push/PR
- **docs.yml**: Deploy MkDocs to GitHub Pages on push to main
- **docker.yml**: Build and push Docker image on version tags

## Template Origin

This project was scaffolded from [andy-service-template](https://github.com/rivoli-ai/andy-service-template).
Run `check-compliance.sh` from the template repo to verify template compliance.
