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

mkdir -p "$OUTPUT_DIR"

# Resolve the live Swagger provider through the integration-test host. This
# uses the same Program.cs/controller graph as production while keeping hosted
# dependency refreshers and seed/migration ownership under the tested factory.
UPDATE_OPENAPI=1 dotnet test \
    tests/Andy.Policies.Tests.Integration/Andy.Policies.Tests.Integration.csproj \
    --filter 'FullyQualifiedName~OpenApiSnapshotExportTests.ExportYaml_WhenExplicitlyRequested' \
    --no-restore --nologo --verbosity minimal

echo "Wrote $OUTPUT_FILE"
