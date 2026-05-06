# Deployment

## Docker Compose (Development)

```bash
# Full stack with PostgreSQL
docker compose up -d

# Embedded mode with SQLite (for Conductor)
docker compose -f docker-compose.embedded.yml up -d
```

## Docker Build

```bash
docker build -t andy-policies:latest .
```

## Kubernetes

### Prerequisites
- Kubernetes cluster
- `kubectl` configured
- Container registry access

### Deployment Steps

1. Build and push image:
```bash
docker build -t registry.example.com/andy-policies:latest .
docker push registry.example.com/andy-policies:latest
```

2. Create namespace and secrets:
```bash
kubectl create namespace andy-policies
kubectl create secret generic andy-policies-db \
  --from-literal=connection-string="Host=postgres;Port=5432;Database=andy_policies;Username=andy_policies;Password=CHANGE_ME"
```

3. Apply manifests (create your own or use Helm).

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | Runtime environment | `Production` |
| `ASPNETCORE_URLS` | Listen URLs | `https://+:8443;http://+:8080` |
| `ConnectionStrings__DefaultConnection` | Database connection string | (see appsettings) |
| `Database__Provider` | `PostgreSql` or `Sqlite` | `PostgreSql` |
| `AndyAuth__Authority` | Andy Auth server URL | `https://localhost:5001` |
| `AndyAuth__Audience` | JWT audience | `urn:andy-policies-api` |
| `Rbac__ApiBaseUrl` | Andy RBAC server URL | `https://localhost:5003` |
| `Rbac__ApplicationCode` | RBAC application code | `andy-policies` |
| `OpenTelemetry__OtlpEndpoint` | OTLP collector endpoint | (empty) |

## Ports

| Service | Port |
|---------|------|
| API HTTPS | 5112 |
| API HTTP | 5113 |
| PostgreSQL | 5439 |
| Client (Angular) | 4206 |

## Conductor Integration

To embed this service in Conductor, use the SQLite configuration:

```bash
docker compose -f docker-compose.embedded.yml up -d
```

Or configure the API directly:
```bash
export Database__Provider=Sqlite
export ConnectionStrings__DefaultConnection="Data Source=andy_policies.db"
dotnet run --project src/Andy.Policies.Api
```

### Embedded mode (Mode 3) operator docs

Mode 3 is the bundled-with-Conductor deployment shape. The operator-facing runbooks live under `embedded/`:

- [Operations](embedded/operations.md) — boot, seed, backup, upgrade, full env var matrix.
- [Troubleshooting](embedded/troubleshooting.md) — 404 under the prefix, manifest fail-loud, SQLite locks, OIDC redirect mismatches.
- [Cross-service smoke](embedded/cross-service-smoke.md) — local recipe + Conductor harness contract.
- [ADR 0010 — Embedded mode](adr/0010-embedded-mode.md) — architectural decisions (SQLite trade-offs, in-process pathbase, manifest-driven registration, fail-loud rationale).
