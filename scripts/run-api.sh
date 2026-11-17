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
export ASPNETCORE_ENVIRONMENT="Development"
export ASPNETCORE_URLS="http://localhost:5000"

cd "${ROOT_DIR}"
echo "==> Starting Relay ASP.NET Core API on http://localhost:5000..."
dotnet run --project src/Relay.Api
