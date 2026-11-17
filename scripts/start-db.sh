#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

echo "==> Starting local SQL Server container for Relay..."

cd "${ROOT_DIR}"

# Check for Docker or Podman
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    docker compose up -d sqlserver
    echo "==> SQL Server started via Docker Compose on port 1433."
elif command -v podman >/dev/null 2>&1; then
    echo "==> Using Podman to run SQL Server 2022 container..."
    podman run -d \
        --name relay-sqlserver \
        -p 1433:1433 \
        -e "ACCEPT_EULA=Y" \
        -e "MSSQL_SA_PASSWORD=Your_Strong_Password123!" \
        -e "MSSQL_PID=Developer" \
        -v relay_mssql_data:/var/opt/mssql:Z \
        --replace \
        mcr.microsoft.com/mssql/server:2022-latest
    echo "==> SQL Server started via Podman on port 1433."
else
    echo "ERROR: Neither running Docker nor Podman was found on the host."
    echo "Please install Docker/Podman or start a local SQL Server instance on localhost:1433."
    exit 1
fi

echo "==> Waiting for SQL Server to be ready on localhost:1433..."
for i in {1..30}; do
    if nc -z localhost 1433 2>/dev/null || (echo > /dev/tcp/localhost/1433) 2>/dev/null; then
        echo "==> SQL Server port 1433 is open and listening!"
        exit 0
    fi
    echo -n "."
    sleep 2
done

echo "==> Ready! You can now run migrations using: ./scripts/migrate.sh"
