# Security

## Authentication

### Andy Auth (OAuth2/OIDC)

This service integrates with [Andy Auth](https://github.com/rivoli-ai/andy-auth) for authentication:

- **Protocol**: OAuth 2.0 Authorization Code with PKCE
- **Token format**: JWT Bearer tokens
- **Authority**: Configured via `AndyAuth:Authority`
- **Audience**: `urn:andy-policies-api`

### OAuth Client Registration

Two OAuth clients are registered in Andy Auth:

1. **`andy-policies-api`** (Confidential) - For service-to-service communication
2. **`andy-policies-web`** (Public) - For the Angular SPA

See `config/auth-seed.sql` for the seed data.

### Test User

- **Email**: `test@andy.local`
- **Password**: `Test123!`
- **Role**: User (with super-admin in RBAC)

## Authorization

### Andy RBAC

Role-based access control is provided by [Andy RBAC](https://github.com/rivoli-ai/andy-rbac):

- **Application code**: `andy-policies`
- **Roles**: admin, user, viewer
- **Actions**: read, write, delete, admin

See `config/rbac-seed.json` for the RBAC configuration.

## Transport Security

- **HTTPS everywhere**: TLS is enforced from development to production
- **Self-signed certs**: Generated automatically in Docker for development
- **Corporate CAs**: Supported via the `certs/` directory
- **Certificate injection**: At build time and runtime in Docker

## API Security

- **Swagger**: Bearer authentication scheme configured
- **MCP**: Requires authorization
- **gRPC**: Uses the same Bearer authentication
- **Health endpoint**: Unauthenticated (for load balancer probes)

## Best Practices

- Never commit secrets to the repository
- Use environment variables for sensitive configuration
- Rotate tokens and passwords regularly
- Review RBAC permissions periodically
