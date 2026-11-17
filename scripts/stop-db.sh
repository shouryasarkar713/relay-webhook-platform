#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

cd "${ROOT_DIR}"

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    docker compose down
    echo "==> SQL Server stopped via Docker Compose."
elif command -v podman >/dev/null 2>&1; then
    podman stop relay-sqlserver || true
    echo "==> SQL Server stopped via Podman."
else
    echo "No container runtime detected."
fi
