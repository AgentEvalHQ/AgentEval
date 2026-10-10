#!/usr/bin/env bash
# Runs the AEF 1.0 conformance corpus through aef-dotnet with the Python runner (contracts/aef/tools/aef_conformance.py,
# spec 09 §9.3): publishes this driver for one target framework, then drives it through the command-line contract.
# Only this result counts for a conformance class claim; the xunit corpus theories are fast feedback.
#
# Usage: run-corpus.sh <tfm> [aef_conformance.py arguments...]
#   run-corpus.sh net10.0 --quiet --kind decision
#   run-corpus.sh net8.0 --quiet --class "Decision engine"     (--class combines classes with OR: check one at a time)
# Environment: AEF_DRIVER_OUT (default /tmp/aefc) is where the driver is published, per framework; AEF_RID (default
# linux-x64) its runtime identifier. Needs the .NET SDK and python3 (the Linux test container has both).
set -euo pipefail
tfm="${1:?usage: run-corpus.sh <tfm> [aef_conformance.py arguments...]}"
shift
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="${AEF_DRIVER_OUT:-/tmp/aefc}/$tfm"
rid="${AEF_RID:-linux-x64}"

# ReadyToRun: every vector starts the driver once, so start-up time is most of the run.
dotnet publish "$here/AgentEval.Results.Conformance.csproj" -c Release -f "$tfm" -o "$out" -r "$rid" \
  --self-contained false -p:PublishReadyToRun=true -nologo -v q

exec python3 -X utf8 -I "$root/contracts/aef/tools/aef_conformance.py" --command "dotnet $out/aef-dotnet.dll" "$@"
