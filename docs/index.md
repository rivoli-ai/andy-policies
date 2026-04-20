# Andy Policies

Governance policy catalog — structured, versioned policy documents with lifecycle and audit trail, consumed by Conductor for story admission, verification, and compliance reporting (content only; enforcement lives in consumers)

## Quick Start

```bash
# Start infrastructure
docker compose up -d postgres

# Run the API
cd src/Andy.Policies.Api
dotnet run

# Run the client
cd client
npm install && npm start
```

## Documentation

- [Features](features.md) - Service capabilities
- [Architecture](architecture.md) - System design and layers
- [Implementation](implementation.md) - Technical details
- [Testing](testing.md) - Test strategy and execution
- [Deployment](deployment.md) - Deployment guide
- [Security](security.md) - Security configuration
