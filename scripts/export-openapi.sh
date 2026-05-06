#!/usr/bin/env bash
# Copyright (c) Rivoli AI 2026. All rights reserved.
#
# Regenerates docs/openapi/andy-policies-v1.yaml from the running Swashbuckle
# document. CI runs this and fails on `git diff` so the committed schema can
# never drift from controller attributes / DTO shapes (P1.9, #79).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

OUTPUT_DIR="docs/openapi"
OUTPUT_FILE="$OUTPUT_DIR/andy-policies-v1.yaml"
API_PROJECT="src/Andy.Policies.Api"
API_DLL="$API_PROJECT/bin/Release/net8.0/Andy.Policies.Api.dll"

mkdir -p "$OUTPUT_DIR"

# Restore the locally-pinned Swashbuckle.AspNetCore.Cli tool. Idempotent.
dotnet tool restore >/dev/null

# Build Release once so the resolved DLL has the production Swagger config.
dotnet build "$API_PROJECT" -c Release --nologo --verbosity minimal

# AndyAuth__Authority, AndySettings__ApiBaseUrl, and AndyRbac__BaseUrl are
# required at startup (#103, #108, #51 — no silent dev bypass). Provide
# placeholders for the document generator; nothing reaches over the network
# because Swashbuckle only walks the controller graph in-process.
# Database:Provider=Sqlite + an in-memory data source keeps the boot-time
# stock-policy seeder (#73) happy without needing a live Postgres. The temp
# file path is wiped on each run so the seed never carries over.
TMP_DB="$(mktemp -t andy-policies-export-openapi-XXXXXX.db)"
trap 'rm -f "$TMP_DB"' EXIT
# P10.1 (#31): use Development env so Program.cs runs MigrateAsync
# against the empty temp SQLite — the boot-time seeder needs a
# schema. The "Testing" env is reserved for integration tests
# whose own factories own schema initialisation.
ASPNETCORE_ENVIRONMENT="Development" \
AndyAuth__Authority="https://export.invalid" \
AndyAuth__Audience="urn:andy-policies-api" \
AndySettings__ApiBaseUrl="https://export.invalid" \
AndyRbac__BaseUrl="https://export.invalid" \
Database__Provider="Sqlite" \
ConnectionStrings__DefaultConnection="Data Source=$TMP_DB" \
    dotnet tool run swagger tofile --yaml --output "$OUTPUT_FILE" "$API_DLL" v1

echo "Wrote $OUTPUT_FILE"
