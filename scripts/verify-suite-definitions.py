#!/usr/bin/env python3
"""Exercise real dataset-runner dry-run identity without a listening endpoint or model calls."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    args = parser.parse_args()
    cli = ["dotnet", str(args.cli.resolve()), "run"]
    with tempfile.TemporaryDirectory(prefix="ae-suite-definition-") as temporary:
        root = Path(temporary)
        dataset = root / "cases.json"
        dataset.write_text('[{"id":"one","input":"private-input","passingScore":80}]', encoding="utf-8")
        plan = {"schemaVersion": "1.0", "planId": "definition-check",
                "subject": {"ref": "model:gpt-4o-mini", "version": "1", "endpoint": "http://127.0.0.1:1/v1"},
                "suites": [{"ref": "dataset:cases.json", "version": "1", "lane": "quality"}],
                "limits": {"maxUsd": 1}, "contentCapture": "off", "targetMode": "live",
                "isolation": "process", "provider": "local",
                "ext": {"agenteval.run": {"inputCap": 8192, "outputCap": 100, "caseSeconds": 30}}}
        path = root / "plan.json"

        def dry_run(expected_code):
            path.write_text(json.dumps(plan), encoding="utf-8")
            before = {p: p.read_bytes() for p in root.rglob("*") if p.is_file()}
            result = subprocess.run(cli + ["--plan", str(path), "--root", str(root), "--runner-id", "definition-check", "--dry-run", "--json"],
                                    capture_output=True, text=True, timeout=60)
            assert result.returncode == expected_code, (result.returncode, result.stdout, result.stderr)
            assert before == {p: p.read_bytes() for p in root.rglob("*") if p.is_file()}
            assert not (root / ".agenteval").exists()
            assert "private-input" not in result.stdout
            return json.loads(result.stdout)

        first = dry_run(0)
        suite = first["suites"][0]
        assert suite["identityScope"] == "definition" and suite["plannedCases"] == 1
        assert suite["digest"].startswith("sha256:") and len(suite["digest"]) == 71
        plan["suites"][0]["digest"] = suite["digest"]
        assert dry_run(0)["suites"][0]["digest"] == suite["digest"]
        dataset.write_text(dataset.read_text().replace('80', '90'), encoding="utf-8")
        changed = dry_run(3)
        assert changed["accepted"] is False and "digest" in changed["reason"]
        assert changed["suites"][0]["digest"] != suite["digest"]
    print("3 suite identity dry-runs passed: discovery, exact pin, changed definition refusal; no evidence writes.")


if __name__ == "__main__":
    main()
