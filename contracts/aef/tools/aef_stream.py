#!/usr/bin/env python3
"""Runner protocol references for 1/README.md, 'Run plans and runners' and 'The event stream': plan-to-runner
matching, and the verification of a finished event stream.

`python aef_stream.py --check` runs both against conformance/protocol/ and exits 1 on any difference.
"""
import calendar
import datetime
import hashlib
import json
import re
import sys
from pathlib import Path

TERMINAL = {"job.sealed", "job.failed", "job.cancelled", "job.refused"}
TIME = re.compile(r"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z$")
TIMEOUT = re.compile(r"^PT(?=[0-9])(?:([0-9]{1,5})H)?(?:([0-9]{1,5})M)?$")


def parse_time(text):
    """(seconds since the epoch, nanoseconds): exact. An impossible date (February 31) is refused, not rolled over."""
    m = TIME.fullmatch(text)
    if not m:
        raise ValueError(f"{text!r} is not an RFC 3339 UTC time")
    y, mo, d, h, mi, s, frac = (int(g) if g and i < 6 else g for i, g in enumerate(m.groups()))
    moment = datetime.datetime(y, mo, d, h, mi, s)  # raises on an impossible date
    return calendar.timegm(moment.timetuple()), int((frac or "").ljust(9, "0"))


def matches(plan, runner):
    """A runner can take a plan when it carries every selector tag, supports the plan's provider, and, for a
    remote-zone plan, is in the plan's zone."""
    return (all(tag in runner.get("tags", []) for tag in plan.get("runnerSelector", []))
            and plan["provider"] in runner["providers"]
            and (plan["isolation"] != "remote-zone" or runner.get("networkZone") == plan.get("zone")))


def verify(events, plan=None, plan_digest=None):
    """(where, problem) for every problem, in event order, then by problem name; 'stream' last."""
    problems = []
    previous_seq, previous_at, spent, terminal, accepted, cases, over_time = 0, None, None, False, False, 0, False
    announced = {}
    first = events[0] if events else None
    start = parse_time(first["at"]) if first else None
    limit = None
    if plan is not None and "timeout" in plan["limits"]:
        h, m = TIMEOUT.fullmatch(plan["limits"]["timeout"]).groups()
        limit = int(h or 0) * 3600 + int(m or 0) * 60
    for i, e in enumerate(events, start=1):
        found, kind = [], e["kind"]
        if i == 1 and kind not in ("job.accepted", "job.refused"):
            found.append("first")
        seq = int(e["seq"])  # an integral number, also when written 2.0
        if seq != previous_seq + 1:
            found.append("seq")
        previous_seq = seq
        if e["jobId"] != first["jobId"]:
            found.append("job-id")
        at = parse_time(e["at"])
        if previous_at is not None and at < previous_at:
            found.append("time")
        previous_at = at
        if limit is not None and not over_time and (at[0] - start[0], at[1] - start[1]) > (limit, 0):
            found.append("over-time")
            over_time = True
        if terminal:
            found.append("after-terminal")
        if kind in ("job.accepted", "job.refused"):
            if kind == "job.accepted" and accepted:
                found.append("accepted-twice")
            accepted = accepted or kind == "job.accepted"
            if plan is not None and e["planId"] != plan["planId"]:
                found.append("plan-id")
            if plan_digest is not None and e["planDigest"] != plan_digest:
                found.append("plan-digest")
        elif kind == "plan.estimated" and e["usdLow"] > e["usdHigh"]:
            found.append("estimate")
        elif kind == "spend.updated":
            if spent is not None and e["spentUsd"] < spent:
                found.append("spend-decreased")
            if plan is not None and e["spentUsd"] > plan["limits"]["maxUsd"]:
                found.append("over-budget")
            spent = e["spentUsd"]
        elif kind == "case.completed":
            cases += 1
            if plan is not None and "cases" in plan["limits"] and cases == plan["limits"]["cases"] + 1:
                found.append("over-cases")
        elif kind == "evidence.produced":
            if e["runId"] in announced and announced[e["runId"]] != e["runHash"]:
                found.append("run-hash-changed")
            announced.setdefault(e["runId"], e["runHash"])
        if kind in ("job.sealed", "job.failed"):
            named = e.get("runs", [])
            if any(r not in announced for r in named):
                found.append("unannounced-run")
            if any(r not in named for r in announced):
                found.append("unsealed-run")
        if kind in TERMINAL:
            terminal = True
        problems.extend((f"event:{i}", p) for p in sorted(found))
    if not terminal:
        problems.append(("stream", "no-terminal"))
    return problems


def check():
    root = Path(__file__).resolve().parents[1] / "1" / "conformance" / "protocol"
    failed, count = 0, 0
    for d in sorted(p for p in (root / "streams").iterdir() if p.is_dir()):
        count += 1
        expected_doc = json.loads((d / "expected.json").read_text(encoding="utf-8"))
        plan_path = (d / expected_doc["plan"]).resolve()
        plan = json.loads(plan_path.read_text(encoding="utf-8"))
        events = [json.loads(line) for line in (d / "events.ndjson").read_bytes().decode("utf-8").split("\n") if line]
        expected = [(p["where"], p["problem"]) for p in expected_doc["problems"]]
        actual = verify(events, plan, hashlib.sha256(plan_path.read_bytes()).hexdigest())
        if actual != expected:
            failed += 1
            print(f"FAIL streams/{d.name}\n  expected {expected}\n  actual   {actual}")
    for d in sorted(p for p in (root / "matching").iterdir() if p.is_dir()):
        count += 1
        load = lambda n: json.loads((d / n).read_text(encoding="utf-8"))
        if matches(load("plan.json"), load("runner.json")) != load("expected.json")["matches"]:
            failed += 1
            print(f"FAIL matching/{d.name}")
    for bad in ("2026-02-31T00:00:00Z", "2026-10-08T12:00:00Z\n"):
        try:
            parse_time(bad)
            failed += 1
            print(f"FAIL parse_time accepted {bad!r}")
        except ValueError:
            pass
    print(f"{count - failed} of {count} protocol vectors pass")
    return 1 if failed or not count else 0


if __name__ == "__main__":
    sys.exit(check() if "--check" in sys.argv else 0)
