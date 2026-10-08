#!/usr/bin/env python3
"""The AEF 1.0 checkpoint decision function: a reference implementation of 1/README.md, 'The decision function'.

Pure: no I/O and no clock (the evaluation time is an input). Times compare at the full precision written (up to nine
fraction digits), never rounded. `python aef_decide.py --check` runs it against conformance/decision-vectors/ and
exits 1 on any difference.
"""
import calendar
import datetime
import json
import re
import sys
from pathlib import Path

TIME = re.compile(r"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z$")
DURATION = re.compile(r"^P(?=[0-9]|T[0-9])(?:([0-9]{1,5})D)?(?:T([0-9]{1,5})H)?$")
KNOWN = {"passed": "passed", "failed": "failed", "not_measured": "not_measured", "incomparable": "incomparable"}
REASON = {"failed": "failed", "missing": "missing", "not_measured": "not-measured", "incomparable": "incomparable", "stale": "stale",
          "waived": "waived"}


def parse_time(text):
    """(seconds since the epoch, nanoseconds): exact, so 12:00:00.000000001Z is later than 12:00:00Z."""
    m = TIME.fullmatch(text)
    if not m:
        raise ValueError(f"{text!r} is not an RFC 3339 UTC time")
    y, mo, d, h, mi, s, frac = m.groups()
    moment = datetime.datetime(int(y), int(mo), int(d), int(h), int(mi), int(s))  # raises on an impossible date
    seconds = calendar.timegm(moment.timetuple())
    return seconds, int((frac or "").ljust(9, "0"))


def parse_duration(text):
    m = DURATION.fullmatch(text)
    if not m:
        raise ValueError(f"{text!r} is not a duration in days and hours")
    days, hours = m.groups()
    return int(days or 0) * 86400 + int(hours or 0) * 3600


def decide(inp):
    names = [lane["lane"] for lane in inp["lanes"]]
    if not names:
        raise ValueError("a checkpoint has at least one lane")
    if len(set(names)) != len(names):
        raise ValueError("a lane is listed twice")
    evaluated_at = parse_time(inp["evaluatedAt"])
    exceptions = {}  # lane -> [(at, expires)]: DEC-1 refuses an exception for no lane, or one never in force
    for exception in inp.get("exceptions") or []:
        if exception["lane"] not in names:
            raise ValueError(f"an exception names {exception['lane']!r}, which is not a lane of the input")
        at, expires = parse_time(exception["at"]), parse_time(exception["expires"])
        if expires <= at:
            raise ValueError(f"an exception for {exception['lane']!r} expires at or before it is granted: it is never in force")
        exceptions.setdefault(exception["lane"], []).append((at, expires))
    lanes, reasons = [], []
    for lane in inp["lanes"]:
        result, code = lane["result"], None
        if result is None:
            status = "missing"
        elif result["subjectVersion"] != inp["subjectVersion"]:
            status, code = "missing", "wrong-version"
        elif parse_time(result["oldestClosedAt"]) > evaluated_at:
            status, code = "missing", "future-evidence"
        elif "freshness" in lane and (lambda c: (c[0] + parse_duration(lane["freshness"]), c[1]))(parse_time(result["oldestClosedAt"])) < evaluated_at:
            status = "stale"
        else:
            status = KNOWN.get(result["status"], "not_measured")  # an unknown status fails closed

        # DEC-2 step 6: only a failure is ever waived, by an exception in force (at <= evaluatedAt < expires).
        lapsed = False
        if status == "failed" and lane["lane"] in exceptions:
            if any(at <= evaluated_at < expires for at, expires in exceptions[lane["lane"]]):
                status = "waived"
            else:
                lapsed = True

        item = {"lane": lane["lane"], "status": status, "blocking": lane["blocking"]}
        if status == "incomparable" and result.get("axes"):
            item["axes"] = result["axes"]
        lanes.append(item)

        if status != "passed":
            if code is None:
                code = "advisory-failed" if status == "failed" and not lane["blocking"] else REASON[status]
            reasons.append(f"{code}:{lane['lane']}")
        if lapsed:
            reasons.append(f"exception-expired:{lane['lane']}")

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
    elif any(s == "waived" for s, _ in statuses):
        outcome = "approved_with_exceptions"
    else:
        outcome = "approved"
    reasons.append(f"outcome:{outcome}")
    return {"outcome": outcome, "lanes": lanes, "reasons": reasons}


def check():
    vectors = sorted((Path(__file__).resolve().parents[1] / "1" / "conformance" / "decision-vectors").glob("*.json"))
    failed = 0
    for path in vectors:
        vector = json.loads(path.read_text(encoding="utf-8"))
        if "expectedError" in vector:
            try:
                decide(vector["input"])
                failed += 1
                print(f"FAIL {path.name}: decided what it must refuse")
            except ValueError:
                pass
            continue
        actual = decide(vector["input"])
        if actual != vector["expected"]:
            failed += 1
            print(f"FAIL {path.name}\n  expected {vector['expected']}\n  actual   {actual}")
    print(f"{len(vectors) - failed} of {len(vectors)} decision vectors pass")
    return 1 if failed or not vectors else 0


if __name__ == "__main__":
    sys.exit(check() if "--check" in sys.argv else 0)
