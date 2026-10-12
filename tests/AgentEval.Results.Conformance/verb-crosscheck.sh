#!/usr/bin/env bash
# Exercise native CLI producers against loopback HTTP targets and independently verify every sealed artifact.
# Usage: bash verb-crosscheck.sh net10.0 (also net8.0 / net9.0). No external model credentials are needed.
set -euo pipefail
tfm="${1:?usage: verb-crosscheck.sh <tfm>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
fixtures="$(mktemp -d)"
trap 'rm -rf "$fixtures"' EXIT

AEF_VERB_FIXTURES="$fixtures" dotnet test "$root/tests/AgentEval.Tests/AgentEval.Tests.csproj" \
  -f "$tfm" --no-restore -p:UseSharedCompilation=false -m:1 \
  --filter 'FullyQualifiedName~AgentEval.Tests.Cli.Aef.NativeVerbIntegrationTests' -v quiet

python3 -X utf8 -I - "$root/contracts/aef/tools/aef_verify.py" "$fixtures" <<'PY'
import json, pathlib, subprocess, sys

verifier, directory = sys.argv[1:]
runs = sorted(pathlib.Path(directory).glob('*/run.json'))
if not runs:
    raise SystemExit('FAIL: the CLI integration tests retained no sealed runs')
failures = 0
for header in runs:
    checked = subprocess.run([sys.executable, '-X', 'utf8', '-I', verifier, 'run', str(header.parent)],
                             capture_output=True, text=True, encoding='utf-8')
    try:
        result = json.loads(checked.stdout)
    except json.JSONDecodeError:
        result = {}
    intact = checked.returncode == 0 and result.get('outcome') == 'intact' and not result.get('problems')
    if not intact:
        failures += 1
    print(f"{'ok' if intact else 'FAIL'} {header.parent.name}: {result.get('outcome', checked.stderr.strip())}")
print(f'{len(runs) - failures} of {len(runs)} native runs independently verified')
sys.exit(1 if failures else 0)
PY
