#!/usr/bin/env python3
"""Builds conformance/rulings/: one vector per ruling made where two implementations read the specification apart
(the reference tools/aef_verify.py and AgentEval's .NET implementation: findings W3-17 to W3-19 and W4-1 to W4-10 of
Q4-39, and the critic's R4-8a), and the pre-release ruling that SUM-5's sum and value are compared exactly (R11-2).
With a ruling undone in a copy of a verifier, its vector here fails: each vector pins the reading the specification
chose against the one it did not.

Each vector is a folder rulings/<name>/ whose expected.json uses an existing kind and that kind's format (run, chain,
document, stream, plan-conformance, checkpoint, lane: spec 09 §9.2.1), names its rules, and gives the finding it pins
in `why`. As in every generator, each expected result is written by hand from the specification; the runs, seals and
decisions are made with the other generators' helpers, as data (it reads none of their output).

Usage: python contracts/aef/tools/pin_vectors.py   (in the order of tools/README.md: after write_vectors.py; then
build_index.py)
"""
import hashlib
import json
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import aef_decide  # noqa: E402
import aef_verify  # noqa: E402  (only nesting_depth and MAX_JSON: what a vector's crafted bytes must reach)
import lane_vectors as LV  # noqa: E402
import protocol_vectors as PV  # noqa: E402
from build_conformance import (COMPLETED_ID, SMALL_ID, V, by, checkpoints, completed_eval, deep_object,  # noqa: E402
                               ndjson_bytes, overlay_batches, read_json, result_id, run_hash, seal, small_run, write_bytes,
                               write_json, write_ndjson)

OUT = Path(__file__).resolve().parents[1] / "1" / "conformance" / "rulings"


def expect(folder, expected):
    write_json(folder / "expected.json", expected)


# ---------------------------------------------------------------------------- run and chain (WP3)

def w3_17_second_rollup():
    """W3-17: a second rollup for one case and path is `trials` at the later rollup only, not at both."""
    folder = OUT / "trials-second-rollup-at-the-later-one"
    TL = lambda trial, state, **kw: dict({"schemaVersion": V, "resultId": result_id(SMALL_ID, "k3", "t", trial), "caseId": "k3",
                                          "path": "t", "trial": trial, "evaluator": {"id": "code:t"}, "state": state}, **kw)
    rollup = {"schemaVersion": V, "resultId": result_id(SMALL_ID, "k3", "t"), "caseId": "k3", "path": "t",
              "evaluator": {"id": "code:t"}, "state": "passed",
              "trials": {"n": 2, "passed": 1, "aggregation": "AnyPass", "agree": False}}
    run, _ = small_run(folder / "run", lines=lambda ls: ls + [TL(0, "failed", severity="low"), TL(1, "passed"), rollup,
                                                              dict(rollup)])
    seal(folder / "run", run, "producer")
    expect(folder, {"kind": "run", "run": "run", "outcome": "invalid",
                    "problems": [["results.ndjson:8", "result-id"], ["results.ndjson:8", "trials"]],
                    "rules": ["RES-8", "RES-4"],
                    "why": "line 8 repeats line 7, the rollup of case k3 at path t (and so its result id). The "
                           "second rollup for one case and path is reported at the later one (line 8), never at the "
                           "first, which matches its two trial lines."})


def w3_18_target_despite_schema_problem():
    """W3-18: a schema problem in results.ndjson does not stop the overlay `target` check; only a file that does not
    read (encoding, limit) does."""
    folder = OUT / "overlay-target-checked-despite-a-results-schema-problem"
    run_dir = folder / "run"
    completed_eval(run_dir)
    results = run_dir / "results.ndjson"
    lines = [json.loads(l) for l in results.read_bytes().split(b"\n")[:-1]]  # by LF only: a reason holds a U+2028
    assert lines[1]["path"] == "triage/policy"
    lines[1]["state"] = "a-later-state"  # VER-9 closes state: the reader result schema refuses it (a schema problem only)
    write_ndjson(results, lines)
    events = [json.loads(l) for l in (run_dir / "overlays" / "events.ndjson").read_bytes().split(b"\n")[:-1]]
    shutil.rmtree(run_dir / "overlays")
    the_hash = seal(run_dir, read_json(run_dir / "run.json"), "producer", sealed_at="2026-10-02T14:06:24Z")
    nowhere = "r_" + "0" * 32
    assert nowhere not in {l["resultId"] for l in lines}
    stray = {"schemaVersion": V, "eventId": "ov_0003", "kind": "annotate", "target": {"run": COMPLETED_ID, "result": nowhere},
             "reason": "A note on a result the run does not have.", "by": by(), "at": "2026-10-02T15:20:00Z"}
    overlay_batches(run_dir, COMPLETED_ID, [events[:1], events[1:] + [stray]], the_hash)
    expect(folder, {"kind": "chain", "run": "run", "problems": [["overlays/events.ndjson:3", "target"]],
                    "rules": ["OVL-2", "OVL-5"],
                    "why": "results.ndjson line 2 has a state 1.0 does not define, a schema problem; the file "
                           "still reads (no encoding or limit problem), so OVL-2's target check is made, and the third "
                           "event, which names a result the run does not have, is target."})


# ---------------------------------------------------------------------------- the document operation (W3-19)

def w3_19_document_limit_of_its_role():
    """W3-19: `document SCHEMA FILE` applies the limit of the file the schema names (ENC-17): 4 MiB for run.json, 40
    MiB for a seal. One size, just over 4 MiB, both ways. The padding is JSON whitespace after the value."""
    size = aef_verify.MAX_JSON + 1  # 4 MiB + 1 byte
    run_dir = OUT / "document-run-over-4-mib"
    scratch = run_dir / "scratch-run"
    small_run(scratch)
    run_doc = read_json(scratch / "run.json")
    the_hash = seal(scratch, run_doc, "producer")
    seal_bytes = (scratch / "seal.json").read_bytes()
    run_bytes = (scratch / "run.json").read_bytes()
    shutil.rmtree(scratch)
    assert json.loads(seal_bytes)["predicate"]["runHash"] == the_hash
    for name, schema, data, verdict, why in (
            ("document-run-over-4-mib", "run", run_bytes, "invalid",
             "a valid run.json padded with spaces to 4 MiB + 1 byte. The document operation applies the limit of "
             "the file its schema names (ENC-17): a run.json is a JSON file of at most 4 MiB, so both schemas refuse "
             "it, however valid its content. Its twin, document-seal-over-4-mib-valid, is the same size and valid."),
            ("document-seal-over-4-mib-valid", "seal", seal_bytes, "valid",
             "a valid seal.json padded with spaces to 4 MiB + 1 byte. A seal's limit is 40 MiB (ENC-17), so "
             "the document operation accepts it: the limit is the schema's file's, not one size for every "
             "document (its twin document-run-over-4-mib is refused at the same size).")):
        padded = data[:-1] + b" " * (size - len(data)) + b"\n"
        assert len(padded) == size and json.loads(padded) == json.loads(data)
        # Generated by the conformance runner (spec 09 §9.2), so the corpus does not hold 8 MiB of spaces.
        parts = [[data[:-1].decode("utf-8"), 1], [" ", size - len(data)], ["\n", 1]]
        assert b"".join(text.encode("utf-8") * n for text, n in parts) == padded
        expect(OUT / name, {"kind": "document", "schema": schema, "document": "document.json", "writer": verdict,
                            "reader": verdict, "generate": [{"write": ["document.json", parts]}],
                            "rules": ["ENC-17", "ENC-18"], "why": why})


# ---------------------------------------------------------------------------- streams and plans (WP4)

def w4_1_stream_line_depth_65():
    """W4-1: a stream line nested beyond ENC-17's depth is event-invalid; it is never read as an event."""
    folder = OUT / "stream-line-depth-65"
    digest = hashlib.sha256(PV.dumps(PV.PLAN)).hexdigest()
    streams = {name: events for name, _, events, _, _ in PV.streams(digest, digest, digest)}
    sealed = streams["complete-sealed"]
    deep = dict(sealed[1], ext={"agenteval.deep": deep_object(62)})  # event 2 again, 65 levels deep
    line = ndjson_bytes([deep])
    assert aef_verify.nesting_depth(line) == 65
    write_json(folder / "plan.json", PV.PLAN)
    write_bytes(folder / "events.ndjson", ndjson_bytes(sealed[:1]) + line + ndjson_bytes(sealed[1:]))
    expect(folder, {"kind": "stream", "plan": "plan.json", "problems": [["event:2", "event-invalid"]],
                    "rules": ["STRM-3", "ENC-17"],
                    "why": "the stream complete-sealed with line 2 inserted: event 2 again (seq 2), with an ext "
                           "nested 65 levels deep. A line beyond ENC-17's depth is event-invalid, and the line after it "
                           "is not checked for seq. Read as an event, it would make line 3's seq 2 a seq problem."})


def w4_2_budget_summed_exactly():
    """W4-2: the job's cost is summed exactly and rounded once, so the order the runs are named in never matters."""
    plan = dict(PV.CPLAN, planId="plan-47", limits={"maxUsd": 0.6, "cases": 3, "timeout": "PT2H"})
    digest = hashlib.sha256(PV.dumps(plan)).hexdigest()
    for name, order, why in (
            ("budget-summed-exactly-ascending", ["R-1", "R-2", "R-3"],
             "three runs cost $0.10, $0.20 and $0.30, named in that order, under maxUsd 0.6. Summed exactly "
             "(0.6000000000000000055...) and rounded once, the cost is the binary64 0.6, which equals maxUsd: within it. "
             "Added in binary64 in this order it is 0.6000000000000001, over it; compared exactly without rounding, it "
             "is over too."),
            ("budget-summed-exactly-descending", ["R-3", "R-2", "R-1"],
             "the same three runs named in the other order: within the budget all the same (in binary64, in "
             "this order, 0.3 + 0.2 + 0.1 is 0.6).")):
        folder = OUT / name
        runs = folder / "runs"
        hashes = {run_id: PV.make_run(runs, run_id, run_id, plan, digest, cases=PV.ONE_CASE, cost=cost)
                  for run_id, cost in (("R-1", 0.1), ("R-2", 0.2), ("R-3", 0.3))}
        write_json(folder / "plan.json", plan)
        write_ndjson(folder / "events.ndjson", PV.job(plan, digest, [(r, hashes[r]) for r in order], order))
        expect(folder, {"kind": "plan-conformance", "events": "events.ndjson", "plan": "plan.json", "runs": "runs",
                        "problems": [], "rules": ["STRM-4", "PLAN-2", "SUM-5"], "why": why})


# ---------------------------------------------------------------------------- checkpoints and lanes (WP4, R4-8a)

def w4_3_input_only_lane():
    """W4-3: CKP-7's evidence covers a lane on one side only: a result in the input for a lane the manifest does not
    have is a result with no runs."""
    folder = OUT / "checkpoint-input-only-lane"
    decided = next(doc for name, doc, *_ in checkpoints() if name == "valid-decided")
    doc = json.loads(json.dumps(decided))
    given = doc["decisionInput"]
    given["lanes"].append(dict(given["lanes"][0], lane="phantom"))  # the quality lane's input again, with its result
    doc["decision"] = aef_decide.decide(given)  # the decision is what the input gives: only the lanes are wrong
    assert doc["decision"]["outcome"] == doc["outcome"]
    write_json(folder / "document.json", doc)
    expect(folder, {"kind": "checkpoint", "schema": "checkpoint", "writer": "valid", "reader": "valid",
                    "problems": ["evidence", "lanes"], "rules": ["CKP-7"],
                    "why": "valid-decided, its input given a lane phantom (a copy of the quality lane, with its "
                           "result) that the manifest does not have, and the decision recomputed from that input. "
                           "phantom has a result but no runs: evidence, as for a lane on both sides; and lanes."})


def lane_vector(name, build, why):
    """A lane vector (the format of lane-vectors/) in rulings/<name>/: build(runs) -> (lanes, results, problems,
    rules), the manifest recording the results it should."""
    folder = OUT / name
    lanes, results, problems, rules = build(folder / "runs")
    LV.checkpoint(folder, lanes, results)
    expect(folder, {"kind": "lane", "checkpoint": "checkpoint.json", "runs": "runs",
                    "lanes": [{"lane": lane, "result": results[lane]} for lane, _, _, _ in lanes],
                    "problems": problems, "rules": rules, "why": why})


OK_RATE = dict(metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "a"),), lane="security",
               ended="2026-10-03T00:00:00Z")
SEVERITY_ENDED = "2026-10-03T00:00:00Z"


def w4_5_evidence_present(runs):
    e1 = LV.make_run(runs, "E1", [dict(case="c1", path="p", state="passed", scores=LV.scores(m=1.0))], ended="2026-10-03T00:00:00Z")
    lanes = [("two-runs-one-named-twice", {"kind": "evidence-present", "runs": 2}, [e1, e1], True),
             ("one-run-named-twice", {"kind": "evidence-present", "runs": 1}, [e1, e1], True)]
    results = {"two-runs-one-named-twice": LV.res("not_measured", "2026-10-03T00:00:00Z"),
               "one-run-named-twice": LV.res("passed", "2026-10-03T00:00:00Z")}
    return lanes, results, [], ["LANE-4"]


def w4_5_severity(runs):
    s2 = LV.make_run(runs, "S-two-passed", [dict(case="c1", path="a", state="passed"),
                                            dict(case="c2", path="a", state="passed")], **OK_RATE)
    sev = lambda n: {"kind": "severity", "max": "none", "minimumN": n}
    lanes = [("minimum-4-one-run-named-twice", sev(4), [s2, s2], True),
             ("minimum-2-one-run-named-twice", sev(2), [s2, s2], True)]
    results = {"minimum-4-one-run-named-twice": LV.res("not_measured", SEVERITY_ENDED),
               "minimum-2-one-run-named-twice": LV.res("passed", SEVERITY_ENDED)}
    return lanes, results, [], ["LANE-3", "LANE-4"]


def w4_6_judges_absent(runs):
    """A baseline with judges: [] and a candidate without judges, as comparison_runs makes B and C15 otherwise."""
    M = (("recall", "score", "higher_better"), ("latency", "duration", "lower_better"), ("tokens", "count", "none"))
    E = (("recall", "mem"), ("latency", "mem"), ("tokens", "mem"))
    values = {"r": (0.4, 120), "i": (0.6, 80), "t": (0.5, 100)}
    lines = lambda pattern: [dict(case=f"b{i:02d}", path="mem", state="passed",
                                  scores=LV.scores(recall=values[k][0], latency=values[k][1], tokens=10))
                             for i, k in enumerate(pattern, start=1)]
    baseline = LV.make_run(runs, "B-judges-empty", lines("t" * 23), lane="memory", metrics=M, entries=E, version="v6",
                           ended="2026-09-20T00:00:00Z", judges=[])
    run_id = "C15-no-judges"
    LV.make_run(runs, run_id, lines("r" * 15 + "i" * 5 + "t" * 3), lane="memory", metrics=M, entries=E,
                ended="2026-10-06T00:00:00Z", sealed=False)
    run_dir = runs / run_id
    run = read_json(run_dir / "run.json")
    del run["judges"]
    write_json(run_dir / "run.json", run)
    candidate = (run_id, seal(run_dir, run, "producer", sealed_at="2026-10-07T00:00:00Z"))
    assert candidate[1] == run_hash(run_dir)
    lanes = [("judges-absent-against-empty", LV.cmp_rule(baseline), [candidate], True)]
    # p = sum C(20, k), k = 15..20, / 2^20 = 0.0207 <= 0.05 (lane-vectors/comparison, lane regressed)
    return lanes, {"judges-absent-against-empty": LV.res("failed", "2026-10-06T00:00:00Z")}, [], ["LANE-6", "LANE-8"]


def w4_10_missing_runs_order(runs):
    q1 = LV.make_run(runs, "Q1", [dict(case="c1", path="p", state="passed", scores=LV.scores(m=1.0))])
    missing = [("x.ndjson:9", LV.h("never written 9")), ("x.ndjson:10", LV.h("never written 10"))]
    lanes = [("found", LV.threshold("quality", ">=", 0.5), [q1], True),
             ("missing-runs", LV.threshold("quality", ">=", 0.5), missing, False)]
    results = {"found": LV.res("passed", "2026-10-01T12:00:00Z"), "missing-runs": None}
    problems = [["lanes/missing-runs/runs/x.ndjson:10", "run-missing"], ["lanes/missing-runs/runs/x.ndjson:9", "run-missing"]]
    return lanes, results, problems, ["CKP-8", "CONF-2"]


def r4_8a_undecided_trial(runs):
    """A case run in two trials, one inconclusive, one passed; its rollup passed (AnyPass). A plain case passed."""
    run = LV.make_run(runs, "S-inconclusive-trial", [
        dict(case="c1", path="a", state="inconclusive", trial=0),
        dict(case="c1", path="a", state="passed", trial=1),
        dict(case="c1", path="a", state="passed", trials={"n": 2, "passed": 1, "aggregation": "AnyPass", "agree": False}),
        dict(case="c2", path="a", state="passed")], **OK_RATE)
    sev = lambda **kw: {"kind": "severity", "max": "none", **kw}
    lanes = [("undecided-trial-decided-rollup", sev(), [run], True),
             ("minimum-counts-rollups-not-trials", sev(minimumN=3), [run], True)]
    results = {"undecided-trial-decided-rollup": LV.res("passed", SEVERITY_ENDED),
               "minimum-counts-rollups-not-trials": LV.res("not_measured", SEVERITY_ENDED)}
    return lanes, results, [], ["LANE-3", "RES-8"]


# ---------------------------------------------------------------------------- SUM-5 defined exactly (R11-2)

# Scores whose binary64 sum is 2.1: SUM-5's value is 2.1 / 5 = 0.42000000000000004, the exact mean 0.42.
SUM5_LINES = [dict(case=f"c{i}", path="p", state="passed", scores=LV.scores(m=v))
              for i, v in enumerate((0.3, 0.4, 0.5, 0.4, 0.5), start=1)]


def r11_2_exact_mean():
    """A sealed run whose summary writes the exact mean, 0.42, where SUM-5 gives 0.42000000000000004."""
    folder = OUT / "summary-exact-mean-is-a-summary-problem"
    run_id = "R-exact-mean"
    LV.make_run(folder, run_id, SUM5_LINES, sealed=False)
    run_dir = folder / run_id
    summary = read_json(run_dir / "summary.json")
    entry = summary["lanes"][0]["metrics"][0]
    assert (entry["sum"], entry["value"]) == (2.1, 0.42000000000000004), entry
    entry["value"] = 0.42
    write_json(run_dir / "summary.json", summary)
    seal(run_dir, read_json(run_dir / "run.json"), "producer", sealed_at="2026-10-07T00:00:00Z")
    run_dir.rename(folder / "run")
    expect(folder, {"kind": "run", "run": "run", "outcome": "invalid", "problems": [["summary.json", "summary"]],
                    "rules": ["SUM-5"],
                    "why": "the lines score 0.3, 0.4, 0.5, 0.4 and 0.5. SUM-5's value is their binary64 sum, 2.1, "
                           "divided by 5: 0.42000000000000004. This summary writes the exact mean, 0.42, another "
                           "binary64 value: SUM-5 defines value to one binary64 value, so it is compared exactly, and "
                           "this is a summary problem, though it lies within 1e-9 of the right one."})


def r11_2_threshold(runs):
    run = LV.make_run(runs, "R-sum-divided-by-n", SUM5_LINES)
    lanes = [("above-0.42", LV.threshold("quality", ">", 0.42), [run], True)]
    return lanes, {"above-0.42": LV.res("passed", "2026-10-01T12:00:00Z")}, [], ["SUM-5", "LANE-2"]


# ---------------------------------------------------------------------------- a sum beyond binary64 (R10-3)

# Two scores of 1.5e308: their exact sum, 3e308, is beyond binary64.
BEYOND_LINES = [dict(case=f"c{i}", path="p", state="passed", scores=LV.scores(m=1.5e308)) for i in (1, 2)]


def r10_3_sum_beyond_binary64():
    """As a producer writes it (no sum or sumSq, value null, not_measured), and with the exact mean written instead."""
    for name, mean, outcome, problems, why in (
            ("summary-sum-beyond-binary64", None, "intact", [],
             "two scores of 1.5e308: their exact sum, 3e308, is beyond binary64, so the mean is no binary64 value. The "
             "summary omits sum and sumSq, and its value is null and its verdict not_measured, as when nothing was "
             "measured: the run is intact, and a verifier recomputing it does not fail on the overflow."),
            ("summary-sum-beyond-binary64-mean-written", 1.5e308, "invalid", [["summary.json", "summary"]],
             "the same lines, and a summary that writes the exact mean, 1.5e308, a finite number: SUM-5 gives no "
             "binary64 value for a mean whose sum is beyond binary64, so its value is null, and this is a summary "
             "problem.")):
        folder = OUT / name
        run_id = "R-" + name
        LV.make_run(folder, run_id, BEYOND_LINES, sealed=False)
        run_dir = folder / run_id
        summary = read_json(run_dir / "summary.json")
        entry = summary["lanes"][0]["metrics"][0]
        assert "sum" not in entry and "sumSq" not in entry and entry["value"] is None, entry
        assert entry["verdict"] == "not_measured", entry
        if mean is not None:
            entry.update(value=mean, verdict="passed")
            write_json(run_dir / "summary.json", summary)
        seal(run_dir, read_json(run_dir / "run.json"), "producer", sealed_at="2026-10-07T00:00:00Z")
        run_dir.rename(folder / "run")
        expect(folder, {"kind": "run", "run": "run", "outcome": outcome, "problems": problems,
                        "rules": ["SUM-5", "SUM-6"], "why": why})


def r10_3_lane(runs):
    run = LV.make_run(runs, "R-sum-beyond-binary64", BEYOND_LINES)
    lanes = [("at-least-zero", LV.threshold("quality", ">=", 0), [run], True)]
    return lanes, {"at-least-zero": LV.res("not_measured", "2026-10-01T12:00:00Z")}, [], ["SUM-5", "LANE-2"]


# ---------------------------------------------------------------------------- names that are not Unicode strings (F1)

REPORTED = "n\ufffdo.txt"  # the name n, one ill-formed unit, o.txt, as a report gives it (§3.9)


def f1_ill_formed_names():
    """Generated (spec 09 §9.2.1): a file name that is not a Unicode string, at the run's root and under overlays/."""
    copy = {"copy": ["valid/completed-eval/run", "run"]}
    for name, kind, where, outcome, problems, rules, why in (
            ("name-not-unicode-at-the-root", "run", "run", "invalid",
             [[REPORTED, "not-sealed"], [REPORTED, "path"]], ["RUN-3", "SEAL-3"],
             "a sealed run with a file added at its root whose name is n, one ill-formed unit (the byte 0xFF, or an "
             "unpaired surrogate where names are UTF-16), then o.txt: not a Unicode string, so a path problem, and a "
             "file the seal does not list. The report names it with U+FFFD in place of the ill-formed unit, and the "
             "verifier does not stop on it."),
            ("name-not-unicode-under-overlays", "run", "run/overlays", "intact", [], ["RUN-3", "OVL-5"],
             "the same name under overlays/: nothing under overlays/ is a problem of the run (§4.5), so the run is "
             "intact."),
            ("name-not-unicode-under-overlays-chain", "chain", "run/overlays", None,
             [["overlays/" + REPORTED, "unexpected-file"]], ["OVL-5", "RUN-3"],
             "the same name under overlays/, as the chain reports it: an unexpected file, named with U+FFFD in place "
             "of the ill-formed unit.")):
        expected = {"kind": kind, "run": "run", "generate": [copy, {"ill-formed-name": [where, "n", "o.txt"]}]}
        if outcome is not None:
            expected["outcome"] = outcome
        expected.update(problems=problems, rules=rules, why=why)
        expect(OUT / name, expected)


# ---------------------------------------------------------------------------- a run's minor is its run.json's (F4)

def f4_later_line_in_a_1_0_run():
    """A 1.0 run with one result line that declares 1.1 and holds a severity 1.0 does not know: the lane is read at
    the run's minor, 1.0, so it is compared (lane-result), not unverifiable."""
    folder = OUT / "lane-later-line-in-a-1-0-run"
    runs = folder / "runs"
    run_id = "S-1-0-with-a-1-1-line"
    LV.make_run(runs, run_id, [dict(case="c1", path="a", state="passed"),
                               dict(case="c2", path="a", state="failed", severity="catastrophic"),
                               dict(case="c3", path="a", state="passed"),
                               dict(case="c4", path="a", state="failed", severity="low")], sealed=False, **OK_RATE)
    run_dir = runs / run_id
    lines = [json.loads(line) for line in (run_dir / "results.ndjson").read_text(encoding="utf-8").splitlines()]
    lines[1]["schemaVersion"] = "1.1"  # one line declares a later minor; run.json declares 1.0
    write_ndjson(run_dir / "results.ndjson", lines)
    the_hash = seal(run_dir, read_json(run_dir / "run.json"), "producer", sealed_at="2026-10-07T00:00:00Z")
    lanes = [("severity-1-0-run", {"kind": "severity", "max": "none"}, [(run_id, the_hash)], True)]
    LV.checkpoint(folder, lanes, {"severity-1-0-run": LV.res("passed", SEVERITY_ENDED)})
    expect(folder, {"kind": "lane", "checkpoint": "checkpoint.json", "runs": "runs",
                    "lanes": [{"lane": "severity-1-0-run", "result": LV.res("failed", SEVERITY_ENDED)}],
                    "problems": [["lanes/severity-1-0-run", "lane-result"]], "rules": ["CKP-8", "VER-6", "LANE-3"],
                    "why": "the run's run.json declares 1.0, and one result line declares 1.1 with a severity 1.0 does "
                           "not know (catastrophic). A run's minor is its run.json's (a writer writes one minor "
                           "throughout a run), so the lane is read at 1.0, the unknown severity as critical (§7.3), "
                           "and compared: c4's known low failure already fails max none, and the manifest's recorded "
                           "passed is a lane-result problem, not unverifiable."})


# ---------------------------------------------------------------------------- main

def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    w3_17_second_rollup()
    w3_18_target_despite_schema_problem()
    w3_19_document_limit_of_its_role()
    w4_1_stream_line_depth_65()
    w4_2_budget_summed_exactly()
    w4_3_input_only_lane()
    lane_vector("lane-run-named-twice-evidence-present", w4_5_evidence_present,
                "a lane's runs are its distinct pairs of runId and runHash (LANE-4): E1 named twice is one run, "
                "so evidence-present with runs 2 is not_measured (and with runs 1 passed: once, not never).")
    lane_vector("lane-run-named-twice-severity", w4_5_severity,
                "S-two-passed (two passed lines) named twice is one run: two decided lines, so minimumN 4 is "
                "not_measured, and minimumN 2 passed (its lines count once, not never).")
    lane_vector("comparison-judges-absent-equals-empty", w4_6_judges_absent,
                "the candidate's run.json has no judges and the baseline's has judges: []. A run without judges "
                "has the empty list (LANE-6), so the judges and rubrics axes agree, the comparison is made, and the 15 "
                "regressions against 5 improvements fail it (p = 0.0207 <= 0.05), not incomparable.")
    lane_vector("lane-missing-runs-ordered-by-bytes", w4_10_missing_runs_order,
                "missing runs x.ndjson:9 and x.ndjson:10. Only the line paths of a run's own NDJSON and JSONL "
                "files order by line number (§3.9); lanes/missing-runs/runs/x.ndjson:10 is not one, so it orders by "
                "its bytes, before :9.")
    lane_vector("severity-undecided-trial-under-decided-rollup", r4_8a_undecided_trial,
                "LANE-3 reads trial lines in step 1 only (a failing trial fails the lane). In step 2 a case is "
                "its rollup: c1's inconclusive trial does not make the lane not_measured, since c1's rollup passed; "
                "and minimumN counts the two decided lines (c1's rollup, c2), not the passed trial: 3 is not met.")
    r11_2_exact_mean()
    r10_3_sum_beyond_binary64()
    f1_ill_formed_names()
    f4_later_line_in_a_1_0_run()
    lane_vector("lane-sum-beyond-binary64-not-measured", r10_3_lane,
                "the run's two scores of 1.5e308 have a sum beyond binary64, so its summary's value is null: the lane "
                "reads the run as not measured, not as passed, however low its threshold.")
    lane_vector("lane-threshold-at-sum-divided-by-n", r11_2_threshold,
                "the run's lines score 0.3, 0.4, 0.5, 0.4 and 0.5, and its summary's value is SUM-5's: their binary64 "
                "sum, 2.1, divided by 5, 0.42000000000000004. That is above the rule's 0.42, so the lane passes. The "
                "exact mean, 0.42, is not above it, and a summary that writes it is a summary problem "
                "(summary-exact-mean-is-a-summary-problem): every conforming producer's run decides this lane alike.")
    print("ruling vectors written:", ", ".join(sorted(p.name for p in OUT.iterdir())))


if __name__ == "__main__":
    main()
