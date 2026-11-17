#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

export PATH="${HOME}/.dotnet:${PATH}"
export XDG_RUNTIME_DIR="/tmp"
export DOTNET_CLI_DO_NOT_USE_GLOBAL_MUTEX=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_HOME="/tmp"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "${ROOT_DIR}"
echo "==> Applying EF Core migrations to SQL Server..."
dotnet ef database update --project src/Relay.Infrastructure --startup-project src/Relay.Infrastructure
echo "==> Database schema is up to date!"
