#!/usr/bin/env bash
# Mission Control install-and-open smoke (S0): install the agenteval tool the way a user does, serve a workspace with
# stored runs, and open every page in a headless browser (smoke.mjs). It fails on a page that is blank, errors, or does
# not show the data it should. It caught all three S0 page bugs on the code before they were fixed; the server tests,
# which write their own queries and fixture shapes, caught none.
#
# Usage:
#   scripts/mc-browser-smoke/run.sh                          # pack the CLI from this tree (build the UI first)
#   scripts/mc-browser-smoke/run.sh --feed DIR --version V   # install an already packed tool (the release workflow)
# Needs .NET 10, Node 20+ and network access for npm and the Playwright browser. MC_SMOKE_WITH_DEPS=1 also installs
# the browser's system libraries (CI). MC_SMOKE_PORT picks the port (default 5055).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
feed=""
version=""
while [ $# -gt 0 ]; do
  case "$1" in
    --feed) feed=$2; shift 2 ;;
    --version) version=$2; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

work=$(mktemp -d)
server=""
cleanup() { [ -n "$server" ] && kill "$server" 2>/dev/null; rm -rf "$work"; }
trap cleanup EXIT

if [ -z "$feed" ]; then
  if [ ! -f "$root/src/AgentEval.MissionControl/wwwroot/index.html" ]; then
    echo "The Mission Control UI is not built: (cd src/AgentEval.MissionControl.Spa && npm ci && npm run build)" >&2
    exit 2
  fi
  feed="$work/feed"
  version="0.0.0-smoke"
  dotnet pack "$root/src/AgentEval.Cli/AgentEval.Cli.csproj" -c Release -o "$feed" -p:Version="$version" -nologo -v q
elif [ -z "$version" ]; then
  echo "--feed needs --version" >&2
  exit 2
fi

echo "== install the tool ($version)"
dotnet tool install AgentEval.Cli --tool-path "$work/tool" --add-source "$feed" --version "$version"

echo "== write a workspace with stored runs (the Mission Control test fixture)"
ids=$(dotnet run -c Release --project "$here/fixture" -- "$work/ws" | tail -1)
echo "$ids"

port=${MC_SMOKE_PORT:-5055}
if curl -fs "http://localhost:$port/" > /dev/null 2>&1; then
  echo "Something already answers on port $port: the pages would come from it, not from this install." >&2
  exit 2
fi
echo "== agenteval mc serve on port $port"
"$work/tool/agenteval" mc serve --port "$port" --workspace "$work/ws" > "$work/serve.log" 2>&1 &
server=$!
for _ in $(seq 1 60); do
  curl -fs "http://localhost:$port/" > /dev/null 2>&1 && break
  sleep 0.5
done
if ! curl -fs "http://localhost:$port/" > /dev/null 2>&1; then
  echo "mc serve did not answer on port $port:" >&2
  cat "$work/serve.log" >&2
  exit 1
fi

echo "== open every page"
(cd "$here" && npm ci --silent)
if [ "${MC_SMOKE_WITH_DEPS:-}" = "1" ]; then
  (cd "$here" && npx playwright install --with-deps chromium)
else
  (cd "$here" && npx playwright install chromium)
fi
node "$here/smoke.mjs" "http://localhost:$port" "$ids"

# A service manager, `kill` or CI stops the tool with SIGTERM, which reaches the launcher only: the server child
# must not outlive it and keep the port.
echo "== SIGTERM stops the server"
kill -TERM "$server"
for _ in $(seq 1 20); do
  curl -fs "http://localhost:$port/" > /dev/null 2>&1 || break
  sleep 0.5
done
if curl -fs "http://localhost:$port/" > /dev/null 2>&1; then
  echo "FAIL: mc serve was terminated, but the server still answers on port $port" >&2
  exit 1
fi
server=""
echo "ok   the server stopped with the tool"
