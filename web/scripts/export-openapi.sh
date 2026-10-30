#!/usr/bin/env bash
# Starts the API without a database, downloads the OpenAPI document into the api-client package and regenerates
# the TypeScript schema. Run after changing any endpoint. Requires the .NET SDK (see global.json).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/web/packages/api-client/openapi/v1.json"
PORT="${OPENAPI_EXPORT_PORT:-5199}"

export ASPNETCORE_URLS="http://127.0.0.1:${PORT}" ASPNETCORE_ENVIRONMENT=Testing Persistence__MigrateOnStartup=false ConnectionStrings__DocProc="" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project "$ROOT/src/DocProc.Api" -c Release --no-launch-profile >/tmp/docproc-openapi-export.log 2>&1 &
API_PID=$!
trap 'kill $API_PID 2>/dev/null || true' EXIT

for _ in $(seq 1 60); do
  if curl -sf "http://127.0.0.1:${PORT}/openapi/v1.json" -o "$OUT"; then
    break
  fi
  sleep 1
done
test -s "$OUT" || { echo "OpenAPI export failed; see /tmp/docproc-openapi-export.log" >&2; exit 1; }
echo "wrote $OUT"
(cd "$ROOT/web" && pnpm --filter @docproc/api-client generate)
