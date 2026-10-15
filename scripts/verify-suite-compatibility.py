#!/usr/bin/env python3
"""Exercise strict suite compatibility through the actual CLI using explicit offline mock runs."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    args = parser.parse_args()
    cli = ["dotnet", str(args.cli.resolve())]
    with tempfile.TemporaryDirectory(prefix="ae-suite-compatibility-") as temporary:
        root = Path(temporary)

        def invoke(arguments, expected):
            result = subprocess.run(cli + arguments, cwd=root, capture_output=True, text=True, timeout=60)
            assert result.returncode == expected, (result.returncode, result.stdout, result.stderr)
            return result

        (root / "fixture.slnx").write_text("<Solution />", encoding="utf-8")
        invoke(["init-workspace", "--name", "suite-compatibility-fixture"], 0)

        def produce(name, prompt):
            destination = root / name
            invoke(["bench", "eu-ai-act", "--sut", "mock", "--subject", "compatibility-fixture",
                    "--input", prompt, "--root", str(root), "--aef", str(destination)], 11)
            return next(destination.rglob("run.json")).parent

        baseline = produce("baseline", "fixture input")
        candidate = produce("candidate", "fixture input")
        changed = produce("changed", "different fixture input")
        before = {p: p.read_bytes() for p in root.rglob("*") if p.is_file()}

        def compare(left, right, expected, *options):
            result = invoke(["aef", "compare-suites", str(left), str(right), "--json", *options], expected)
            if expected == 2:
                assert not result.stdout.strip() and result.stderr.strip()
                return None
            return json.loads(result.stdout)

        matched = compare(baseline, candidate, 0)
        assert matched["reason"] == "same-definition" and matched["compatible"]
        assert matched["baseline"]["runHash"] != matched["candidate"]["runHash"]
        compare(baseline, candidate, 0, "--baseline-hash", matched["baseline"]["runHash"],
                "--candidate-hash", matched["candidate"]["runHash"])
        assert compare(baseline, changed, 13)["reason"] == "definition-mismatch"
        compare(baseline, candidate, 2, "--candidate-hash", "0" * 64)
        compare(baseline, root / "absent", 2)
        assert before == {p: p.read_bytes() for p in root.rglob("*") if p.is_file()}
    print("5 suite comparisons passed: match, pinned match, changed workload, wrong pin, missing evidence; no run mutations.")


if __name__ == "__main__":
    main()
