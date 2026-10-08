#!/usr/bin/env python3
"""The AEF v2 checkpoint decision function: a reference implementation of v2/README.md, 'The decision function'.

Pure: no I/O and no clock (the evaluation time is an input). `python aef_decide.py --check` runs it against
conformance/decision-vectors/ and exits 1 on any difference.
"""
import json
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

DURATION = re.compile(r"^P(?=\d|T\d)(?:(\d+)D)?(?:T(\d+)H)?$")
REASON = {"failed": "failed", "missing": "missing", "not_measured": "not-measured", "incomparable": "incomparable", "stale": "stale"}


def parse_time(text):
    return datetime.strptime(text.replace("Z", "+0000"), "%Y-%m-%dT%H:%M:%S%z") if "." not in text \
        else datetime.strptime(text.replace("Z", "+0000"), "%Y-%m-%dT%H:%M:%S.%f%z")


def parse_duration(text):
    days, hours = DURATION.match(text).groups()
    return timedelta(days=int(days or 0), hours=int(hours or 0))


def decide(inp):
    evaluated_at = parse_time(inp["evaluatedAt"])
    lanes, reasons = [], []
    for lane in inp["lanes"]:
        result, code = lane["result"], None
        if result is None:
            status = "missing"
        elif result["subjectVersion"] != inp["subjectVersion"]:
            status, code = "missing", "wrong-version"
        elif "freshness" in lane and parse_time(result["closedAt"]) + parse_duration(lane["freshness"]) < evaluated_at:
            status = "stale"
        else:
            status = result["status"]

        item = {"lane": lane["lane"], "status": status, "blocking": lane["blocking"]}
        if status == "incomparable" and result.get("axes"):
            item["axes"] = result["axes"]
        lanes.append(item)

        if status != "passed":
            if code is None:
                code = "advisory-failed" if status == "failed" and not lane["blocking"] else REASON[status]
            reasons.append(f"{code}:{lane['lane']}")

    superseded = inp.get("supersededBy") not in (None, inp["subjectVersion"])
    if superseded:
        reasons.append(f"superseded:{inp['supersededBy']}")

    statuses = [(l["status"], l["blocking"]) for l in lanes]
    if superseded or any(s == "stale" for s, _ in statuses):
        outcome = "expired"
    elif any(s == "failed" and b for s, b in statuses):
        outcome = "blocked"
    elif any(s in ("missing", "not_measured", "incomparable") for s, _ in statuses):
        outcome = "inconclusive"
    else:
        outcome = "approved"
    reasons.append(f"outcome:{outcome}")
    return {"outcome": outcome, "lanes": lanes, "reasons": reasons}


def check():
    vectors = sorted((Path(__file__).resolve().parents[1] / "v2" / "conformance" / "decision-vectors").glob("*.json"))
    failed = 0
    for path in vectors:
        vector = json.loads(path.read_text(encoding="utf-8"))
        actual = decide(vector["input"])
        if actual != vector["expected"]:
            failed += 1
            print(f"FAIL {path.name}\n  expected {vector['expected']}\n  actual   {actual}")
    print(f"{len(vectors) - failed} of {len(vectors)} decision vectors pass")
    return 1 if failed or not vectors else 0


if __name__ == "__main__":
    sys.exit(check() if "--check" in sys.argv else 0)
