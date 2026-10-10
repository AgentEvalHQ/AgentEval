#!/usr/bin/env bash
# Checks runs written by AgentEval.Results' writer (Writing/: AefRunWriter, AefSealer, AefOverlayWriter) with the
# REFERENCE verifier (contracts/aef/tools/aef_verify.py), not only with the .NET one: publishes the driver, has it write
# the sample runs (write-samples: rich, content-off, aborted, unsealed, overlays; see Ops/WriterOps.cs) with a trust
# policy for the fresh test keys that signed them, then runs aef_verify.py's run, seal, chain and view on each and
# requires (a) the output the writer meant (intact or unsealed, no problems, signedBy, withheld) and (b) the same output
# as the .NET verifier. Exit 0 when every check agrees.
#
# Usage: writer-crosscheck.sh <tfm>
# Environment: AEF_DRIVER_OUT (default /tmp/aefc) and AEF_RID (default linux-x64), as run-corpus.sh. Needs the .NET
# SDK and python3 (the Linux test container has both).
set -euo pipefail
tfm="${1:?usage: writer-crosscheck.sh <tfm>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="${AEF_DRIVER_OUT:-/tmp/aefc}/$tfm"
rid="${AEF_RID:-linux-x64}"

dotnet publish "$here/AgentEval.Results.Conformance.csproj" -c Release -f "$tfm" -o "$out" -r "$rid" \
  --self-contained false -p:PublishReadyToRun=true -nologo -v q

samples="$(mktemp -d)"
trap 'rm -rf "$samples"' EXIT
dotnet "$out/aef-dotnet.dll" write-samples "$samples/runs" > "$samples/manifest.json"

exec python3 -X utf8 -I - "$root/contracts/aef/tools/aef_verify.py" "$samples/manifest.json" <<'PY'
import json, subprocess, sys

verifier, manifest = sys.argv[1], sys.argv[2]
checks = json.load(open(manifest, encoding="utf-8"))["checks"]
failures = 0
for check in checks:
    args = check["args"]
    done = subprocess.run([sys.executable, "-X", "utf8", "-I", verifier, *args], capture_output=True, text=True, encoding="utf-8")
    name = " ".join(a if "/" not in a else a.rsplit("/", 1)[-1] for a in args)
    if done.returncode != 0:
        print(f"FAIL {name}: aef_verify exited {done.returncode}: {done.stderr.strip()}")
        failures += 1
        continue
    reference = json.loads(done.stdout)
    wrong = {k: v for k, v in check["want"].items() if reference.get(k) != v}
    if wrong:
        print(f"FAIL {name}: aef_verify does not give what the writer meant: want {wrong}, got {reference}")
        failures += 1
    elif reference != check["dotnet"]:
        print(f"FAIL {name}: aef_verify and aef-dotnet disagree:\n  aef_verify {reference}\n  aef-dotnet {check['dotnet']}")
        failures += 1
    else:
        print(f"ok   {name}: {json.dumps({k: reference[k] for k in check['want']}, ensure_ascii=False)}")
print(f"{len(checks) - failures} of {len(checks)} checks agree")
sys.exit(1 if failures else 0)
PY
