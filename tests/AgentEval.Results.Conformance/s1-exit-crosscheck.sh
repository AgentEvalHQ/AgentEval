#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 AgentEval Contributors
# Signed native eval/redteam/bench checkpoint; loopback HTTP only, no external credentials.
set -euo pipefail
tfm="${1:?usage: s1-exit-crosscheck.sh <net8.0|net10.0>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
fixtures="$(mktemp -d)"
trap 'rm -rf "$fixtures"' EXIT
AEF_EXIT_FIXTURES="$fixtures" dotnet test "$root/tests/AgentEval.Tests/AgentEval.Tests.csproj" \
 -f "$tfm" --no-restore -p:UseSharedCompilation=false -m:1 \
 --filter 'FullyQualifiedName~S1ExitCriterionTests' -v quiet
python3 - "$root" "$fixtures" "$tfm" <<'PY'
import json, pathlib, subprocess, sys
root, artifacts = map(pathlib.Path, sys.argv[1:3])
cli = ['dotnet', str(root / f'src/AgentEval.Cli/bin/Debug/{sys.argv[3]}/AgentEval.Cli.dll')]
ref = [sys.executable, str(root / 'contracts/aef/tools/aef_verify.py')]
def invoke(command):
    result = subprocess.run(list(map(str, command)), capture_output=True, text=True, timeout=60)
    assert result.returncode == 0, (command, result.stdout, result.stderr)
    return json.loads(result.stdout)
policy = artifacts / 'policy.json'
checkpoint = artifacts / 'checkpoint.json'
envelope = artifacts / 'checkpoint.dsse.json'
runs = artifacts / 'evidence'
headers = list(runs.rglob('run.json'))
assert len(headers) == 3
hashes = set()
for header in headers:
    for command in (cli + ['aef', 'verify', header.parent, '--policy', policy, '--json'],
                    ref + ['run', header.parent, '--policy', policy]):
        result = invoke(command)
        assert result['outcome'] == 'intact' and not result['problems'], result
        assert 'test:s1-local' in result['signedBy'], result
    hashes.add(json.loads((header.parent / 'seal.json').read_text())['predicate']['runHash'])
manifest = invoke(ref + ['checkpoint', checkpoint])
assert manifest['reader'] == 'valid' and manifest['writer'] == 'valid' and not manifest['problems'], manifest
lanes = invoke(ref + ['lanes', checkpoint, '--runs', runs, '--policy', policy, '--envelope', envelope])
assert not lanes['problems'] and set(lanes['anchors']) == hashes, lanes
assert len(lanes['lanes']) == 3 and all(l['result']['status'] == 'passed' for l in lanes['lanes']), lanes
signature = invoke(ref + ['signature', envelope, checkpoint, policy])
assert signature['envelopeResult'] is None and signature['verifiesFor'] == ['test:s1-local'], signature
invoke(cli + ['aef', 'checkpoint', checkpoint, '--runs', runs, '--policy', policy, '--envelope', envelope, '--json'])
assert all('PRIVATE KEY' not in f.read_text() for f in artifacts.rglob('*') if f.is_file())
print(f'{sys.argv[3]}: three signed native runs, three threshold lanes and checkpoint signature independently verified')
PY
