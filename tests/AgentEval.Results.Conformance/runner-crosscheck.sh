#!/usr/bin/env bash
# Exercise bounded runner jobs offline and compare .NET and Python evidence/protocol verification.
# Usage: bash runner-crosscheck.sh net10.0 (or net8.0). Restore the solution first.
set -euo pipefail
tfm="${1:?usage: runner-crosscheck.sh <net8.0|net10.0>}"
case "$tfm" in net8.0|net10.0) ;; *) echo "Unsupported CLI target framework" >&2; exit 2 ;; esac
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
fixtures="$(mktemp -d)"
trap 'rm -rf "$fixtures"' EXIT

AEF_RUNNER_FIXTURES="$fixtures" dotnet test "$root/tests/AgentEval.Tests/AgentEval.Tests.csproj" \
  -f "$tfm" --no-restore -p:UseSharedCompilation=false -m:1 \
  --filter 'FullyQualifiedName~AefDatasetJobTargetTests|FullyQualifiedName~RunnerCommandTests' -v quiet
dotnet build "$here/AgentEval.Results.Conformance.csproj" -f "$tfm" --no-restore \
  -p:UseSharedCompilation=false -m:1 -v quiet
python3 -X utf8 -I "$here/runner-crosscheck.py" "$fixtures" --tfm "$tfm" --exercise-cli
