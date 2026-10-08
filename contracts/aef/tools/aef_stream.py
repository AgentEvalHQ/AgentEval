#!/usr/bin/env python3
"""Verifies a finished runner event stream: a reference implementation of v2/README.md, 'The event stream'.

`python aef_stream.py --check` runs it against conformance/protocol/streams/ and exits 1 on any difference.
"""
import json
import sys
from datetime import datetime
from pathlib import Path

TERMINAL = {"job.sealed", "job.failed", "job.cancelled"}


def parse_time(text):
    base, _, frac = text.rstrip("Z").partition(".")
    return datetime.strptime(base, "%Y-%m-%dT%H:%M:%S"), int((frac + "000000000")[:9]) if frac else 0


def verify(events, plan=None):
    """(where, problem) for every problem, in event order, then by problem name; 'stream' last."""
    problems = []
    previous_seq, previous_at, spent, terminal = 0, None, None, False
    announced = set()
    job = events[0]["jobId"] if events else None
    for i, e in enumerate(events, start=1):
        where, found = f"event:{i}", []
        if i == 1 and e["kind"] != "job.accepted":
            found.append("first")
        if e["seq"] != previous_seq + 1:
            found.append("seq")
        previous_seq = e["seq"]
        if e["jobId"] != job:
            found.append("job-id")
        at = parse_time(e["at"])
        if previous_at is not None and at < previous_at:
            found.append("time")
        previous_at = at
        if terminal:
            found.append("after-terminal")
        kind = e["kind"]
        if kind == "job.accepted" and plan is not None and e["planId"] != plan["planId"]:
            found.append("plan-id")
        elif kind == "spend.updated":
            if spent is not None and e["spentUsd"] < spent:
                found.append("spend-decreased")
            if plan is not None and e["spentUsd"] > plan["limits"]["maxUsd"]:
                found.append("over-budget")
            spent = e["spentUsd"]
        elif kind == "evidence.produced":
            announced.add(e["runId"])
        elif kind == "job.sealed" and any(r not in announced for r in e["runs"]):
            found.append("unannounced-run")
        if kind in TERMINAL:
            terminal = True
        problems.extend((where, p) for p in sorted(found))
    if not terminal:
        problems.append(("stream", "no-terminal"))
    return problems


def check():
    root = Path(__file__).resolve().parents[1] / "v2" / "conformance" / "protocol" / "streams"
    plan = json.loads((root / "plan.json").read_text(encoding="utf-8"))
    failed, names = 0, sorted(p.name for p in root.iterdir() if p.is_dir())
    for name in names:
        events = [json.loads(line) for line in (root / name / "events.ndjson").read_bytes().decode("utf-8").split("\n") if line]
        expected = [(p["where"], p["problem"]) for p in json.loads((root / name / "expected.json").read_text(encoding="utf-8"))["problems"]]
        actual = verify(events, plan)
        if actual != expected:
            failed += 1
            print(f"FAIL {name}\n  expected {expected}\n  actual   {actual}")
    print(f"{len(names) - failed} of {len(names)} event-stream vectors pass")
    return 1 if failed or not names else 0


if __name__ == "__main__":
    sys.exit(check() if "--check" in sys.argv else 0)
