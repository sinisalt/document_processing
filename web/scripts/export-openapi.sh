#!/usr/bin/env bash
# Starts the API without a database, downloads the OpenAPI document into the api-client package and regenerates
# the TypeScript schema. Run after changing any endpoint. Requires the .NET SDK (see global.json) and Node (pnpm).
#
# OPENAPI_EXPORT_PORT (default 5199) moves the temporary API to another port, OPENAPI_EXPORT_LOG keeps its log at a
# fixed path (default: a fresh temporary file, printed on failure). The document's `servers` entry, which the API
# fills from the request's host and port, is removed before the file is written: the committed contract must not
# depend on where it was exported, or CI's drift check fails after an export on another port.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/web/packages/api-client/openapi/v1.json"
PORT="${OPENAPI_EXPORT_PORT:-5199}"
LOG="${OPENAPI_EXPORT_LOG:-$(mktemp "${TMPDIR:-/tmp}/docproc-openapi-export.XXXXXX")}"
command -v dotnet >/dev/null || { echo "dotnet is not on PATH (see global.json)" >&2; exit 1; }
command -v node >/dev/null || { echo "node is not on PATH (see web/package.json engines)" >&2; exit 1; }
DOWNLOAD="$(mktemp "${TMPDIR:-/tmp}/docproc-openapi.XXXXXX")"
rm -f "$OUT"

export ASPNETCORE_URLS="http://127.0.0.1:${PORT}" ASPNETCORE_ENVIRONMENT=Testing Persistence__MigrateOnStartup=false ConnectionStrings__DocProc="" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project "$ROOT/src/DocProc.Api" -c Release --no-launch-profile >"$LOG" 2>&1 &
API_PID=$!
trap 'kill $API_PID 2>/dev/null || true; rm -f "$DOWNLOAD"' EXIT

for _ in $(seq 1 60); do
  if curl -sf "http://127.0.0.1:${PORT}/openapi/v1.json" -o "$DOWNLOAD"; then
    break
  fi
  sleep 1
done
if ! test -s "$DOWNLOAD"; then
  echo "OpenAPI export failed; the API log ($LOG) follows:" >&2
  cat "$LOG" >&2 || true
  exit 1
fi
node "$ROOT/web/scripts/strip-openapi-servers.mjs" "$DOWNLOAD" "$OUT"
echo "wrote $OUT"
if [ -z "${OPENAPI_EXPORT_LOG:-}" ]; then rm -f "$LOG"; fi
(cd "$ROOT/web" && pnpm --filter @docproc/api-client generate)
