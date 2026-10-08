"""Writes contracts/aef/1/conformance/decision-vectors/*.json (python contracts/aef/tools/decision_vectors.py). Every expected output below is written by hand from
the rules in 1/README.md ('The decision function'), never computed by an implementation."""
import json
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "1" / "conformance" / "decision-vectors"
V = "git:3f2a1c"
AT = "2026-10-08T12:00:00Z"


# The run hash of each lane's one run, written by hand. A lane with a result names its evidence: the sorted set of its
# runs' run hashes (DEC-1). OTHER is another run of a lane (an earlier run, or a re-run): another run hash.
RUN = {"quality": "a" * 64, "security": "b" * 64, "memory": "c" * 64, "performance": "d" * 64, "compliance": "e" * 64}
OTHER = "f" * 64


def lane(name, blocking, status=None, version=V, closed="2026-10-07T10:00:00Z", freshness=None, axes=None, evidence=None):
    item = {"lane": name, "blocking": blocking}
    if freshness:
        item["freshness"] = freshness
    if status is None:
        item["result"] = None
    else:
        item["evidence"] = evidence or [RUN[name]]
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


def exception(name, at="2026-10-07T15:00:00Z", expires="2026-10-21T00:00:00Z", requirement=None, evidence=None):
    """An exception for lane `name`. The defaults accept the lane's one run (RUN[name]) and are in force at AT: granted
    five hours after that run closed (2026-10-07T10:00:00Z), expiring on 2026-10-21."""
    item = {"lane": name, "evidence": [RUN[name]] if evidence is None else evidence}
    if requirement:
        item["requirement"] = requirement
    item.update(reason=f"The {name} failure is accepted until the fix ships; tracked in the release review.",
                by={"identity": "oidc:https://login.example.com/u-7f3a", "assurance": "authenticated"},
                at=at, expires=expires)
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
     {"lanes": [lane("quality", True, "passed"), lane("memory", True, "incomparable", axes=["judges"])]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("incomparable", True, "memory", ["judges"])],
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
     {"lanes": [lane("quality", True, "passed"), lane("memory", False, "incomparable", axes=["suite-content"])]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("incomparable", False, "memory", ["suite-content"])],
      "reasons": ["incomparable:memory", "outcome:inconclusive"]}),
    ("22-empty-axes-are-omitted", "An incomparable lane with no axes named has no axes in the output.",
     {"lanes": [{"lane": "memory", "blocking": True,
                 "evidence": [RUN["memory"]],
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
    # Exceptions (DEC-1, DEC-2 step 6, DEC-3 rule 4, DEC-4, CKP-10). AT is 2026-10-08T12:00:00Z; each lane's run closed
    # at 2026-10-07T10:00:00Z unless the vector says otherwise, and every exception is granted after its evidence closed.
    ("28-waived-blocking-failure", "A blocking failure with an exception for its exact evidence, in force: waived, approved_with_exceptions.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "failed")],
      "exceptions": [exception("security", requirement="REQ-15")]},
     {"outcome": "approved_with_exceptions", "lanes": [out("passed", True, "quality"), out("waived", True, "security")],
      "reasons": ["waived:security", "outcome:approved_with_exceptions"]}),
    ("29-waived-advisory-failure", "An advisory failure with an exception for its evidence, in force, is waived too: approved_with_exceptions, not approved.",
     {"lanes": [lane("quality", True, "passed"), lane("performance", False, "failed")],
      "exceptions": [exception("performance")]},
     {"outcome": "approved_with_exceptions", "lanes": [out("passed", True, "quality"), out("waived", False, "performance")],
      "reasons": ["waived:performance", "outcome:approved_with_exceptions"]}),
    ("30-exception-at-its-expiry", "An exception is not in force at its expires time (evaluatedAt < expires): the failure blocks.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "failed")],
      "exceptions": [exception("security", at="2026-10-07T12:00:00Z", expires=AT)]},
     {"outcome": "blocked", "lanes": [out("passed", True, "quality"), out("failed", True, "security")],
      "reasons": ["failed:security", "exception-expired:security", "outcome:blocked"]}),
    ("31-exception-at-its-grant", "An exception is in force from its at time (at <= evaluatedAt): waived.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "failed")],
      "exceptions": [exception("security", at=AT, expires="2026-10-15T12:00:00Z")]},
     {"outcome": "approved_with_exceptions", "lanes": [out("passed", True, "quality"), out("waived", True, "security")],
      "reasons": ["waived:security", "outcome:approved_with_exceptions"]}),
    ("32-exception-one-nanosecond-left", "Times compare at full precision: an exception expiring one nanosecond after evaluatedAt is in force.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", at="2026-10-07T12:00:00Z", expires="2026-10-08T12:00:00.000000001Z")]},
     {"outcome": "approved_with_exceptions", "lanes": [out("waived", True, "security")],
      "reasons": ["waived:security", "outcome:approved_with_exceptions"]}),
    ("33-exception-on-missing", "Missing evidence is never waived: the exception has no effect and adds no reason.",
     {"lanes": [lane("quality", True, "passed"), lane("memory", True)],
      "exceptions": [exception("memory")]},
     {"outcome": "inconclusive", "lanes": [out("passed", True, "quality"), out("missing", True, "memory")],
      "reasons": ["missing:memory", "outcome:inconclusive"]}),
    ("34-exception-on-stale", "Stale evidence is never waived, even of a failure and by an exception for it: the checkpoint expired.",
     {"lanes": [lane("quality", True, "passed"),
                lane("security", True, "failed", closed="2026-09-20T12:00:00Z", freshness="P14D")],
      "exceptions": [exception("security")]},
     {"outcome": "expired", "lanes": [out("passed", True, "quality"), out("stale", True, "security")],
      "reasons": ["stale:security", "outcome:expired"]}),
    ("35-exception-on-not-measured", "A lane that measured nothing is never waived.",
     {"lanes": [lane("quality", True, "not_measured")], "exceptions": [exception("quality")]},
     {"outcome": "inconclusive", "lanes": [out("not_measured", True, "quality")],
      "reasons": ["not-measured:quality", "outcome:inconclusive"]}),
    ("36-exception-on-incomparable", "An incomparable comparison is never waived.",
     {"lanes": [lane("memory", False, "incomparable", axes=["judges"])], "exceptions": [exception("memory")]},
     {"outcome": "inconclusive", "lanes": [out("incomparable", False, "memory", ["judges"])],
      "reasons": ["incomparable:memory", "outcome:inconclusive"]}),
    ("37-exception-on-passed-lane", "An exception for a lane that passed changes nothing: approved, not approved_with_exceptions.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "passed")],
      "exceptions": [exception("security")]},
     {"outcome": "approved", "lanes": [out("passed", True, "quality"), out("passed", True, "security")],
      "reasons": ["outcome:approved"]}),
    ("38-two-exceptions-one-in-force", "Two exceptions for the lane's evidence, one expired (a short first grant) and one in force: waived, and no exception-expired.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", at="2026-10-07T11:00:00Z", expires="2026-10-08T00:00:00Z"), exception("security")]},
     {"outcome": "approved_with_exceptions", "lanes": [out("waived", True, "security")],
      "reasons": ["waived:security", "outcome:approved_with_exceptions"]}),
    ("39-two-exceptions-none-in-force", "Two expired exceptions for the lane's evidence: blocked, with one exception-expired for the lane.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", at="2026-10-07T11:00:00Z", expires="2026-10-07T18:00:00Z"),
                     exception("security", at="2026-10-07T18:00:00Z", expires="2026-10-08T06:00:00Z")]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security")],
      "reasons": ["failed:security", "exception-expired:security", "outcome:blocked"]}),
    ("40-exception-granted-later", "An exception granted after evaluatedAt is not in force then: blocked.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", at="2026-10-09T00:00:00Z", expires="2026-10-20T00:00:00Z")]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security")],
      "reasons": ["failed:security", "exception-expired:security", "outcome:blocked"]}),
    ("41-waived-and-another-blocking-failure", "A waived lane does not block, but another blocking failure still does.",
     {"lanes": [lane("quality", True, "failed"), lane("security", True, "failed")],
      "exceptions": [exception("security")]},
     {"outcome": "blocked", "lanes": [out("failed", True, "quality"), out("waived", True, "security")],
      "reasons": ["failed:quality", "waived:security", "outcome:blocked"]}),
    ("42-waived-and-missing", "A waived lane beside a missing one: inconclusive before approved_with_exceptions.",
     {"lanes": [lane("quality", True), lane("security", True, "failed")],
      "exceptions": [exception("security")]},
     {"outcome": "inconclusive", "lanes": [out("missing", True, "quality"), out("waived", True, "security")],
      "reasons": ["missing:quality", "waived:security", "outcome:inconclusive"]}),
    ("43-waived-but-superseded", "A superseded version expires the checkpoint whatever its exceptions.",
     {"supersededBy": "git:9e8d7c", "lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security")]},
     {"outcome": "expired", "lanes": [out("waived", True, "security")],
      "reasons": ["waived:security", "superseded:git:9e8d7c", "outcome:expired"]}),
    ("44-advisory-exception-expired", "An advisory failure whose exception expired: approved, and both the failure and the lapse are reported.",
     {"lanes": [lane("quality", True, "passed"), lane("performance", False, "failed")],
      "exceptions": [exception("performance", at="2026-10-07T11:00:00Z", expires="2026-10-08T00:00:00Z")]},
     {"outcome": "approved", "lanes": [out("passed", True, "quality"), out("failed", False, "performance")],
      "reasons": ["advisory-failed:performance", "exception-expired:performance", "outcome:approved"]}),
    ("45-reasons-in-lane-order", "exception-expired follows its own lane's reason, before the next lane's.",
     {"lanes": [lane("security", True, "failed"), lane("memory", True)],
      "exceptions": [exception("security", at="2026-10-07T11:00:00Z", expires="2026-10-08T00:00:00Z")]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security"), out("missing", True, "memory")],
      "reasons": ["failed:security", "exception-expired:security", "missing:memory", "outcome:blocked"]}),
    ("46-advisory-failed-and-waived", "An advisory failure and a waived blocking failure: approved_with_exceptions, reasons in lane order.",
     {"lanes": [lane("performance", False, "failed"), lane("security", True, "failed")],
      "exceptions": [exception("security")]},
     {"outcome": "approved_with_exceptions", "lanes": [out("failed", False, "performance"), out("waived", True, "security")],
      "reasons": ["advisory-failed:performance", "waived:security", "outcome:approved_with_exceptions"]}),
    ("47-re-evaluated-after-expiry", "CKP-10: vector 28's input read on 2026-10-21 at noon, after its exception expired at midnight: approved_with_exceptions now reads as blocked.",
     {"evaluatedAt": "2026-10-21T12:00:00Z",
      "lanes": [lane("quality", True, "passed"), lane("security", True, "failed")],
      "exceptions": [exception("security", requirement="REQ-15")]},
     {"outcome": "blocked", "lanes": [out("passed", True, "quality"), out("failed", True, "security")],
      "reasons": ["failed:security", "exception-expired:security", "outcome:blocked"]}),
    # Exceptions bind to evidence: the same set of run hashes, no more and no fewer (DEC-2 step 6).
    ("51-exception-for-other-evidence", "An exception accepted another run's failure (an earlier run, or before a re-run): no effect on this run's failure.",
     {"lanes": [lane("quality", True, "passed"), lane("security", True, "failed")],
      "exceptions": [exception("security", evidence=[OTHER])]},
     {"outcome": "blocked", "lanes": [out("passed", True, "quality"), out("failed", True, "security")],
      "reasons": ["failed:security", "exception-other-evidence:security", "outcome:blocked"]}),
    ("52-exception-for-some-of-the-runs", "A lane of two runs; the exception names only one of them: not the same set, no effect.",
     {"lanes": [lane("security", True, "failed", evidence=[RUN["security"], OTHER])],
      "exceptions": [exception("security", evidence=[RUN["security"]])]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security")],
      "reasons": ["failed:security", "exception-other-evidence:security", "outcome:blocked"]}),
    ("53-exception-for-more-than-the-runs", "The exception names the lane's run and another: not the same set, no effect.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", evidence=[RUN["security"], OTHER])]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security")],
      "reasons": ["failed:security", "exception-other-evidence:security", "outcome:blocked"]}),
    ("54-exception-for-every-run", "A lane of two runs; the exception names both: the same set, waived.",
     {"lanes": [lane("security", True, "failed", evidence=[RUN["security"], OTHER])],
      "exceptions": [exception("security", evidence=[RUN["security"], OTHER])]},
     {"outcome": "approved_with_exceptions", "lanes": [out("waived", True, "security")],
      "reasons": ["waived:security", "outcome:approved_with_exceptions"]}),
    ("55-expired-and-other-evidence", "One exception for other evidence (listed first) and one for this evidence that expired: both reasons, exception-expired first.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", evidence=[OTHER]),
                     exception("security", at="2026-10-07T11:00:00Z", expires="2026-10-08T00:00:00Z")]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security")],
      "reasons": ["failed:security", "exception-expired:security", "exception-other-evidence:security", "outcome:blocked"]}),
    ("56-other-evidence-and-expired", "An expired exception for other evidence is for other evidence: exception-other-evidence only.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", evidence=[OTHER], at="2026-10-07T11:00:00Z", expires="2026-10-08T00:00:00Z")]},
     {"outcome": "blocked", "lanes": [out("failed", True, "security")],
      "reasons": ["failed:security", "exception-other-evidence:security", "outcome:blocked"]}),
    ("57-waiver-beside-other-evidence", "One exception applies and another is for other evidence: waived, and a waived lane adds no other reason.",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", evidence=[OTHER]), exception("security")]},
     {"outcome": "approved_with_exceptions", "lanes": [out("waived", True, "security")],
      "reasons": ["waived:security", "outcome:approved_with_exceptions"]}),
    ("59-minutes-at-the-boundary", "A freshness in minutes (ENC-9): evidence exactly 90 minutes old is fresh; evidence one "
     "nanosecond older than PT1H30M, the same 90 minutes written otherwise, is stale.",
     {"lanes": [lane("quality", True, "passed", closed="2026-10-08T10:30:00Z", freshness="PT90M"),
                lane("security", True, "passed", closed="2026-10-08T10:29:59.999999999Z", freshness="PT1H30M")]},
     {"outcome": "expired", "lanes": [out("passed", True, "quality"), out("stale", True, "security")],
      "reasons": ["stale:security", "outcome:expired"]}),
]

# Vectors whose input only a reader accepts (a value a later minor may add).
READER_ONLY = {"25-unknown-status-fails-closed"}

# The rules each vector tests. Every decided vector reads an input (DEC-1) and checks each lane's status (DEC-2), the
# outcome (DEC-3) and the reason list (DEC-4); a refused one tests DEC-1. These add the rules a vector turns on besides.
DECIDED_RULES = ["DEC-1", "DEC-2", "DEC-3", "DEC-4"]
EXTRA_RULES = {
    # DEC-5: versions, lane names and run hashes byte for byte, times at full precision, at a boundary.
    **{name: ["DEC-5"] for name in (
        "05-wrong-version", "11-superseded", "12-fresh-at-the-boundary", "15-days-and-hours", "16-superseded-by-itself",
        "17-version-before-freshness", "23-future-evidence", "24-one-nanosecond-stale", "30-exception-at-its-expiry",
        "31-exception-at-its-grant", "32-exception-one-nanosecond-left", "48-exception-for-an-unknown-lane",
        "49-exception-expires-at-its-grant", "51-exception-for-other-evidence", "52-exception-for-some-of-the-runs",
        "53-exception-for-more-than-the-runs", "54-exception-for-every-run")},
    # ENC-9: a duration in minutes, two spellings of one duration.
    "59-minutes-at-the-boundary": ["DEC-5", "ENC-9"],
    # CKP-10: a decided checkpoint's input evaluated again at the time of reading.
    "47-re-evaluated-after-expiry": ["CKP-10"],
}

# Inputs the function refuses rather than decide: (name, description, input, error). Schema-invalid inputs say so.
REFUSED = [
    ("26-no-lanes", "No lane: deciding nothing would approve nothing. (The schema refuses it too.)",
     {"lanes": []}, "no-lanes", True),
    ("27-a-lane-twice", "A lane listed twice is ambiguous: refused.",
     {"lanes": [lane("quality", True, "passed"), lane("quality", True, "failed")]}, "duplicate-lane", False),
    ("48-exception-for-an-unknown-lane", "An exception for a lane the input does not have: refused, never ignored (DEC-1).",
     {"lanes": [lane("security", True, "failed")], "exceptions": [exception("memory")]}, "exception-unknown-lane", False),
    ("49-exception-expires-at-its-grant", "An exception whose expires equals its at is never in force: refused (DEC-1).",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", at="2026-10-07T15:00:00Z", expires="2026-10-07T15:00:00Z")]},
     "exception-never-in-force", False),
    ("50-exception-expires-before-its-grant", "An exception that expires before it is granted: refused (DEC-1).",
     {"lanes": [lane("security", True, "failed")],
      "exceptions": [exception("security", at="2026-10-07T15:00:00Z", expires="2026-10-07T12:00:00Z")]},
     "exception-never-in-force", False),
    ("58-exception-for-no-evidence", "An exception that names no run hash would accept any failure, a policy rather than an exception: refused (DEC-1). (The schema refuses it too.)",
     {"lanes": [lane("security", True, "failed")], "exceptions": [exception("security", evidence=[])]},
     "exception-no-evidence", True),
]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.json"):
        old.unlink()
    for name, description, partial, expected in VECTORS:
        doc = {"description": description, "rules": DECIDED_RULES + EXTRA_RULES.get(name, []),
               "input": {"subjectVersion": V, "evaluatedAt": AT, **partial},
               "expected": expected}
        if name in READER_ONLY:
            doc["readerOnly"] = True
        (OUT / f"{name}.json").write_bytes((json.dumps(doc, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))
    for name, description, partial, error, schema_invalid in REFUSED:
        doc = {"description": description, "rules": ["DEC-1"] + EXTRA_RULES.get(name, []),
               "input": {"subjectVersion": V, "evaluatedAt": AT, **partial}, "expectedError": error}
        if schema_invalid:
            doc["schemaInvalid"] = True
        (OUT / f"{name}.json").write_bytes((json.dumps(doc, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
