"""Writes contracts/aef/v2/conformance/decision-vectors/*.json (python contracts/aef/tools/decision_vectors.py). Every expected output below is written by hand from
the rules in v2/README.md ('The decision function'), never computed by an implementation."""
import json
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "v2" / "conformance" / "decision-vectors"
V = "git:3f2a1c"
AT = "2026-10-08T12:00:00Z"


def lane(name, blocking, status=None, version=V, closed="2026-10-07T10:00:00Z", freshness=None, axes=None):
    item = {"lane": name, "blocking": blocking}
    if freshness:
        item["freshness"] = freshness
    if status is None:
        item["result"] = None
    else:
        result = {"status": status, "subjectVersion": version, "oldestClosedAt": closed}
        if axes:
            result["axes"] = axes
        item["result"] = result
    return item


def out(status, blocking, name, axes=None):
    item = {"lane": name, "status": status, "blocking": blocking}
    if axes:
        item["axes"] = axes
    return item


VECTORS = [
    ("01-all-passed", "Every lane passed on fresh evidence for this version.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "passed")]},
     {"outcome": "approved", "lanes": [out("passed", True, "quality"), out("passed", True, "security")],
      "reasons": ["outcome:approved"]}),
    ("02-blocking-failed", "A blocking lane failed: blocked.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "failed")]},
     {"outcome": "blocked", "lanes": [out("passed", True, "quality"), out("failed", True, "security")],
      "reasons": ["failed:security", "outcome:blocked"]}),
    ("03-advisory-failed", "An advisory lane failed: approved, and the failure is reported.",
     {"lanes": [lane("quality", True, "passed"), lane("performance", False, "failed")]},
     {"outcome": "approved", "lanes": [out("passed", True, "quality"), out("failed", False, "performance")],
      "reasons": ["advisory-failed:performance", "outcome:approved"]}),
    ("04-missing", "A lane with no evidence is missing: at best inconclusive, never a pass.",
     {"lanes": [lane("quality", True, "passed"), lane("memory", True)]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("missing", True, "memory")],
      "reasons": ["missing:memory", "outcome:inconclusive"]}),
    ("05-wrong-version", "Evidence for another version is no evidence for this one.",
     {"lanes": [lane("quality", True, "passed"), lane("compliance", True, "passed", version="git:000000")]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("missing", True, "compliance")],
      "reasons": ["wrong-version:compliance", "outcome:inconclusive"]}),
    ("06-not-measured", "Evidence that measured nothing the rule reads.",
     {"lanes": [lane("quality", True, "not_measured"), lane("security", True, "passed")]},
     {"outcome": "inconclusive", "lanes": [out("not_measured", True, "quality"), out("passed", True, "security")],
      "reasons": ["not-measured:quality", "outcome:inconclusive"]}),
    ("07-incomparable", "A comparison whose runs differ on a required axis decides nothing.",
     {"lanes": [lane("quality", True, "passed"), lane("memory", True, "incomparable", axes=["judge.modelId"])]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("incomparable", True, "memory", ["judge.modelId"])],
      "reasons": ["incomparable:memory", "outcome:inconclusive"]}),
    ("08-blocked-beats-missing", "A failed blocking lane blocks even when another lane is missing.",
     {"lanes": [lane("security", True, "failed"), lane("memory", True)]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security"), out("missing", True, "memory")],
      "reasons": ["failed:security", "missing:memory", "outcome:blocked"]}),
    ("09-stale", "Evidence older than the lane's freshness: the checkpoint expired.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "passed", closed="2026-09-20T12:00:00Z", freshness="P14D")]},
     {"outcome": "expired", "lanes": [out("passed", True, "quality"), out("stale", True, "security")],
      "reasons": ["stale:security", "outcome:expired"]}),
    ("10-stale-beats-blocked", "Stale evidence cannot decide, even a failure: expired before blocked.",
     {"lanes": [lane("quality", True, "failed"), lane("security", True, "passed", closed="2026-09-20T12:00:00Z", freshness="P14D")]},
     {"outcome": "expired", "lanes": [out("failed", True, "quality"), out("stale", True, "security")],
      "reasons": ["failed:quality", "stale:security", "outcome:expired"]}),
    ("11-superseded", "A newer version superseded this one: expired, whatever the lanes say.",
     {"supersededBy": "git:9e8d7c", "lanes": [lane("quality", True, "passed")]},
     {"outcome": "expired", "lanes": [out("passed", True, "quality")],
      "reasons": ["superseded:git:9e8d7c", "outcome:expired"]}),
    ("12-fresh-at-the-boundary", "Evidence exactly as old as the freshness is still fresh.",
     {"lanes": [lane("security", True, "passed", closed="2026-09-24T12:00:00Z", freshness="P14D")]},
     {"outcome": "approved", "lanes": [out("passed", True, "security")], "reasons": ["outcome:approved"]}),
    ("13-advisory-missing", "Any lane without evidence, advisory too, keeps the outcome from approved.",
     {"lanes": [lane("quality", True, "passed"), lane("performance", False)]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("missing", False, "performance")],
      "reasons": ["missing:performance", "outcome:inconclusive"]}),
    ("14-hours", "Freshness in hours: closed 37 h before, allowed 36 h.",
     {"lanes": [lane("security", True, "passed", closed="2026-10-06T23:00:00Z", freshness="PT36H")]},
     {"outcome": "expired", "lanes": [out("stale", True, "security")], "reasons": ["stale:security", "outcome:expired"]}),
    ("15-days-and-hours", "P1DT12H is 36 hours: closed exactly 36 h before is fresh.",
     {"lanes": [lane("security", True, "passed", closed="2026-10-07T00:00:00Z", freshness="P1DT12H")]},
     {"outcome": "approved", "lanes": [out("passed", True, "security")], "reasons": ["outcome:approved"]}),
    ("16-superseded-by-itself", "supersededBy equal to the checkpoint's own version is not a supersession.",
     {"supersededBy": V, "lanes": [lane("quality", True, "passed")]},
     {"outcome": "approved", "lanes": [out("passed", True, "quality")], "reasons": ["outcome:approved"]}),
    ("17-version-before-freshness", "Old evidence for another version is missing (wrong-version), not stale.",
     {"lanes": [lane("security", True, "passed", version="git:000000", closed="2026-01-01T00:00:00Z", freshness="P14D")]},
     {"outcome": "inconclusive", "lanes": [out("missing", True, "security")],
      "reasons": ["wrong-version:security", "outcome:inconclusive"]}),
    ("18-advisory-stale", "A stale advisory lane expires the checkpoint too: stale evidence decides nothing.",
     {"lanes": [lane("quality", True, "passed"),
                lane("performance", False, "passed", closed="2026-09-01T00:00:00Z", freshness="P7D")]},
     {"outcome": "expired", "lanes": [out("passed", True, "quality"), out("stale", False, "performance")],
      "reasons": ["stale:performance", "outcome:expired"]}),
    ("19-superseded-after-lane-reasons", "Lane reasons come first, then the supersession, then the outcome.",
     {"supersededBy": "git:9e8d7c", "lanes": [lane("quality", True, "failed")]},
     {"outcome": "expired", "lanes": [out("failed", True, "quality")],
      "reasons": ["failed:quality", "superseded:git:9e8d7c", "outcome:expired"]}),
    ("20-advisory-not-measured", "An advisory lane that measured nothing keeps the outcome from approved.",
     {"lanes": [lane("quality", True, "passed"), lane("performance", False, "not_measured")]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("not_measured", False, "performance")],
      "reasons": ["not-measured:performance", "outcome:inconclusive"]}),
    ("21-advisory-incomparable", "An advisory comparison that is incomparable keeps the outcome from approved.",
     {"lanes": [lane("quality", True, "passed"), lane("memory", False, "incomparable", axes=["stimulus"])]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("incomparable", False, "memory", ["stimulus"])],
      "reasons": ["incomparable:memory", "outcome:inconclusive"]}),
    ("22-empty-axes-are-omitted", "An incomparable lane with no axes named has no axes in the output.",
     {"lanes": [{"lane": "memory", "blocking": True,
                 "result": {"status": "incomparable", "subjectVersion": V, "oldestClosedAt": "2026-10-07T10:00:00Z", "axes": []}}]},
     {"outcome": "inconclusive", "lanes": [out("incomparable", True, "memory")],
      "reasons": ["incomparable:memory", "outcome:inconclusive"]}),
    ("23-future-evidence", "Evidence that closed after the evaluation time did not exist then: missing.",
     {"lanes": [lane("quality", True, "passed", closed="2026-10-09T00:00:00Z")]},
     {"outcome": "inconclusive", "lanes": [out("missing", True, "quality")],
      "reasons": ["future-evidence:quality", "outcome:inconclusive"]}),
    ("24-one-nanosecond-stale", "Times compare at full precision: one nanosecond past the freshness is stale.",
     {"evaluatedAt": "2026-10-08T12:00:00.000000001Z",
      "lanes": [lane("security", True, "passed", closed="2026-09-24T12:00:00Z", freshness="P14D")]},
     {"outcome": "expired", "lanes": [out("stale", True, "security")], "reasons": ["stale:security", "outcome:expired"]}),
    ("25-unknown-status-fails-closed", "A status this version does not know (a later minor's) reads as not_measured.",
     {"lanes": [lane("quality", True, "deferred")]},
     {"outcome": "inconclusive", "lanes": [out("not_measured", True, "quality")],
      "reasons": ["not-measured:quality", "outcome:inconclusive"]}),
]

# Vectors whose input only a reader accepts (a value a later minor may add).
READER_ONLY = {"25-unknown-status-fails-closed"}


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.json"):
        old.unlink()
    for name, description, partial, expected in VECTORS:
        doc = {"description": description,
               "input": {"subjectVersion": V, "evaluatedAt": AT, **partial},
               "expected": expected}
        if name in READER_ONLY:
            doc["readerOnly"] = True
        (OUT / f"{name}.json").write_bytes((json.dumps(doc, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
