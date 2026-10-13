#!/usr/bin/env python3
"""Offline decision CLI cross-check against the independent AEF Python reader.

Requires Python 3, OpenSSL and a built AgentEval.Cli.dll. Example:
python scripts/verify-aef-decisions.py --cli src/AgentEval.Cli/bin/Debug/net10.0/AgentEval.Cli.dll
"""
import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def run(args):
    return subprocess.check_output([str(a) for a in args], text=True, encoding="utf-8")


def snapshot(root):
    return {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in root.rglob("*") if p.is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    cli = ["dotnet", args.cli.resolve()]
    reader = [sys.executable, repo / "contracts/aef/tools/aef_verify.py"]
    comparisons = 0
    with tempfile.TemporaryDirectory(prefix="aef-decisions-") as temporary:
        root = Path(temporary)
        folder = root / "run"
        fixture = repo / "samples/interop/assert-ai/example-run/results/billing-safety/run-1"
        json.loads(run(cli + ["aef", "import", "assert-ai", fixture, folder, "--content-capture", "off", "--json"]))
        original = snapshot(folder)
        rows = [json.loads(line) for line in (folder / "results.ndjson").read_text().splitlines()]
        child = next(r for r in rows if "parentResultId" in r)
        result_id = child["resultId"]
        key = root / "key.pem"
        run(["openssl", "genpkey", "-algorithm", "EC", "-pkeyopt", "ec_paramgen_curve:P-256", "-out", key])
        public = run(["openssl", "pkey", "-in", key, "-pubout"])
        policies = []
        for identity in ("reviewer-1", "someone-else"):
            path = root / (identity + ".json")
            path.write_text(json.dumps({"keys": [{"identity": identity, "publicKey": public}]}))
            policies.append(path)
        expiry = (datetime.now(timezone.utc) + timedelta(days=1)).replace(microsecond=0)
        expires = expiry.strftime("%Y-%m-%dT%H:%M:%SZ")
        decisions = [
            ("approve", [], False),
            ("reject", ["--result", result_id], False),
            ("waive", ["--result", result_id, "--expires", expires], True),
            ("waive", ["--requirement", "REQ-42", "--expires", expires], False),
            ("override", ["--result", result_id, "--state", "passed"], True),
            ("adjudicate", ["--result", result_id, "--state", "failed"], True),
            ("approve", ["--result", result_id], True),
        ]
        for number, (kind, options, signed) in enumerate(decisions, 1):
            report = json.loads(run(cli + ["decide", folder, "--kind", kind, "--by", "reviewer-1",
                                          "--reason", "Offline cross-check", "--json"] + options
                                    + (["--key", key] if signed else [])))
            assert report["batch"] == number and report["identityVerified"] is False
        assert all(snapshot(folder)[path] == digest for path, digest in original.items())
        assert json.loads(run(reader + ["run", folder]))["outcome"] == "intact"
        assert json.loads(run(reader + ["chain", folder]))["problems"] == []
        before = snapshot(folder)
        invalid = subprocess.run([str(a) for a in cli + ["decide", folder, "--kind", "approve", "--by", "reviewer-1",
                                 "--reason", "Invalid target", "--result", "missing-result", "--json"]], capture_output=True)
        assert invalid.returncode == 2 and snapshot(folder) == before

        def compare(at, policy):
            nonlocal comparisons
            options = ["--at", at] + (["--policy", policy] if policy else [])
            actual = json.loads(run(cli + ["aef", "view", folder, "--json"] + options))
            expected = json.loads(run(reader + ["view", folder] + options))
            assert actual == expected, (actual, expected)
            comparisons += 1
            return actual

        active_time = (expiry - timedelta(seconds=1)).strftime("%Y-%m-%dT%H:%M:%SZ")
        for at, active in [("2000-01-01T00:00:00Z", False), (active_time, True), (expires, False)]:
            for policy in [None, *policies]:
                view = compare(at, policy)
                assert len(view["waivers"]) == 2 and all(w["active"] == active for w in view["waivers"])
                assert len(view["results"]) == 1
                assert view["results"][0]["sealedState"] == child["state"]
                assert view["results"][0]["effectiveState"] == "failed"
                assert len(view["reviews"]) == 2 and all(r["status"] == "approve" for r in view["reviews"])
                assert sum(a["shown"] == "signed" for a in view["assurance"]) == (4 if policy == policies[0] else 0)

        # A corrupted signature must lose verified assurance in both readers without altering review history.
        signature = folder / "overlays/seal-0007.dsse.json"
        envelope = json.loads(signature.read_text())
        encoded = envelope["signatures"][0]["sig"]
        envelope["signatures"][0]["sig"] = ("A" if encoded[0] != "A" else "B") + encoded[1:]
        signature.write_text(json.dumps(envelope))
        view = compare(active_time, policies[0])
        assert view["assurance"][-1]["shown"] == "self-attested"
        assert sum(a["shown"] == "signed" for a in view["assurance"]) == 3
        assert all(snapshot(folder)[path] == digest for path, digest in original.items())
    print(json.dumps({"decisions": len(decisions), "matching_views": comparisons,
                      "original_evidence_preserved": True, "tampered_signature_rejected": True}))


if __name__ == "__main__":
    main()
