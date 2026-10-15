#!/usr/bin/env python3
"""Offline requirement CLI integration against a sealed AEF conformance fixture.

Usage: python scripts/verify-requirements.py --cli path/to/AgentEval.Cli.dll
Requires a built CLI and Python 3; no provider or network calls.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    folder = repo / "contracts/aef/1/conformance/overlay-views/waiver-boundaries/run"
    before = {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.rglob("*") if p.is_file()}
    verified = subprocess.run([sys.executable, str(repo / "contracts/aef/tools/aef_verify.py"), "run", str(folder)],
                              capture_output=True, text=True, check=True)
    assert json.loads(verified.stdout)["outcome"] == "intact"
    run_hash = json.loads((folder / "seal.json").read_text())["predicate"]["runHash"]
    cli = ["dotnet", str(args.cli.resolve()), "requirements"]
    catalogue = {"schemaVersion": "1.0", "requirements": [{
        "id": "example:quality", "selector": {"lane": "main", "metric": "m", "path": "q"},
        "acceptance": {"kind": "threshold", "operator": ">=", "value": 0.8},
        "aggregation": {"kind": "all", "missing": "not_measured"}, "freshnessSeconds": 86400}]}
    with tempfile.TemporaryDirectory(prefix="ae-requirements-") as temporary:
        source = Path(temporary) / "catalogue.json"
        def status(expected, code, version="v1", at="2026-10-01T01:00:00Z", selected=True, hash_value=run_hash):
            source.write_text(json.dumps(catalogue), encoding="utf-8")
            invocation = cli + ["status", str(source), "--subject", "agent:a/b", "--version", version, "--at", at, "--json"]
            if selected:
                invocation += ["--evidence", f"{hash_value}={folder}"]
            result = subprocess.run(invocation, text=True, capture_output=True)
            assert result.returncode == code, (result.returncode, result.stdout, result.stderr)
            report = json.loads(result.stdout)
            if expected == "error":
                assert report["error"] is True
            else:
                assert report["requirements"][0]["status"] == expected, report
                assert report["catalogueDigest"] == hashlib.sha256(source.read_bytes()).hexdigest()
                if selected:
                    assert report["selectedRunHashes"] == [run_hash]
                    assert len(report["evidence"][0]["overlayDigest"]) == 64
        status("not_met", 1)
        status("not_measured", 1, version="other")
        status("not_measured", 1, selected=False)
        status("stale", 1, at="2026-10-03T01:00:00Z")
        status("error", 2, hash_value="a" * 64)
        catalogue["requirements"][0]["aggregation"]["kind"] = "any"
        status("met", 0)
        catalogue["requirements"][0]["aggregation"] = {"kind": "fraction", "minimum": 0.5, "missing": "not_measured"}
        status("met", 0)
    assert before == {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.rglob("*") if p.is_file()}
    print("7 requirement CLI cases passed; independent AEF verification intact; original evidence bytes unchanged.")


if __name__ == "__main__":
    main()
