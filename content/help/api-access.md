---
title: API Access
order: 2
tags: [api, swagger, mcp, grpc, cli]
---

# API Access

## REST / Swagger

Interactive API documentation is available at `/swagger` when running in Development mode.

All endpoints require a Bearer token from Andy Auth unless running without auth configured.

### Authentication

```
Authorization: Bearer <your-jwt-token>
```

### Base URL

- Development: `https://localhost:5112`
- Docker: `https://localhost:5112`

## MCP (Model Context Protocol)

Connect AI assistants to this service via the `/mcp` endpoint.

### Supported Clients

- Claude Desktop
- ChatGPT
- VS Code extensions (Cline, Roo, Continue)

### Available Tools

- **ListItems** — List all items
- **GetItem** — Get details of a specific item
- **CreateItem** — Create a new item
- **DeleteItem** — Delete an item

## gRPC

For service-to-service communication. Proto definitions are in `src/Api/Protos/`.

## CLI

```bash
# List items
dotnet run --project tools/*.Cli -- items list --api-url https://localhost:5112

# Create item
dotnet run --project tools/*.Cli -- items create --name "My Item" --description "Details"

# With authentication
dotnet run --project tools/*.Cli -- items list --token <bearer-token>
```
