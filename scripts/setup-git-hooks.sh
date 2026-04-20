#!/usr/bin/env bash
# Copyright (c) Rivoli AI 2026. All rights reserved.
# Set up git hooks for this repository.

set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"

echo "Setting up git hooks..."
git config core.hooksPath "$REPO_DIR/.githooks"
chmod +x "$REPO_DIR/.githooks/"*
echo "Done. Git hooks are now active from .githooks/"
echo ""
echo "Hooks installed:"
ls -1 "$REPO_DIR/.githooks/" | grep -v '\.sample$' | sed 's/^/  - /'
