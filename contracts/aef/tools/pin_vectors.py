#!/usr/bin/env python3
"""Builds conformance/rulings/: one vector per ruling made where two implementations read the specification apart
(the reference tools/aef_verify.py and AgentEval's .NET implementation: findings W3-17 to W3-19 and W4-1 to W4-10 of
Q4-39, and the critic's R4-8a). With a ruling undone in a copy of a verifier, its vector here fails: each vector pins
the reading the specification chose against the one it did not.

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
                    "why": "W3-17: line 8 repeats line 7, the rollup of case k3 at path t (and so its result id). The "
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
                    "why": "W3-18: results.ndjson line 2 has a state 1.0 does not define, a schema problem; the file "
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
             "W3-19: a valid run.json padded with spaces to 4 MiB + 1 byte. The document operation applies the limit of "
             "the file its schema names (ENC-17): a run.json is a JSON file of at most 4 MiB, so both schemas refuse "
             "it, however valid its content. Its twin, document-seal-over-4-mib-valid, is the same size and valid."),
            ("document-seal-over-4-mib-valid", "seal", seal_bytes, "valid",
             "W3-19: a valid seal.json padded with spaces to 4 MiB + 1 byte. A seal's limit is 40 MiB (ENC-17), so "
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
                    "why": "W4-1: the stream complete-sealed with line 2 inserted: event 2 again (seq 2), with an ext "
                           "nested 65 levels deep. A line beyond ENC-17's depth is event-invalid, and the line after it "
                           "is not checked for seq. Read as an event, it would make line 3's seq 2 a seq problem."})


def w4_2_budget_summed_exactly():
    """W4-2: the job's cost is summed exactly and rounded once, so the order the runs are named in never matters."""
    plan = dict(PV.CPLAN, planId="plan-47", limits={"maxUsd": 0.6, "cases": 3, "timeout": "PT2H"})
    digest = hashlib.sha256(PV.dumps(plan)).hexdigest()
    for name, order, why in (
            ("budget-summed-exactly-ascending", ["R-1", "R-2", "R-3"],
             "W4-2: three runs cost $0.10, $0.20 and $0.30, named in that order, under maxUsd 0.6. Summed exactly "
             "(0.6000000000000000055...) and rounded once, the cost is the binary64 0.6, which equals maxUsd: within it. "
             "Added in binary64 in this order it is 0.6000000000000001, over it; compared exactly without rounding, it "
             "is over too."),
            ("budget-summed-exactly-descending", ["R-3", "R-2", "R-1"],
             "W4-2: the same three runs named in the other order: within the budget all the same (in binary64, in "
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
                    "why": "W4-3: valid-decided, its input given a lane phantom (a copy of the quality lane, with its "
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
                "W4-5: a lane's runs are its distinct pairs of runId and runHash (LANE-4): E1 named twice is one run, "
                "so evidence-present with runs 2 is not_measured (and with runs 1 passed: once, not never).")
    lane_vector("lane-run-named-twice-severity", w4_5_severity,
                "W4-5: S-two-passed (two passed lines) named twice is one run: two decided lines, so minimumN 4 is "
                "not_measured, and minimumN 2 passed (its lines count once, not never).")
    lane_vector("comparison-judges-absent-equals-empty", w4_6_judges_absent,
                "W4-6: the candidate's run.json has no judges and the baseline's has judges: []. A run without judges "
                "has the empty list (LANE-6), so the judges and rubrics axes agree, the comparison is made, and the 15 "
                "regressions against 5 improvements fail it (p = 0.0207 <= 0.05), not incomparable.")
    lane_vector("lane-missing-runs-ordered-by-bytes", w4_10_missing_runs_order,
                "W4-10: missing runs x.ndjson:9 and x.ndjson:10. Only the line paths of a run's own NDJSON and JSONL "
                "files order by line number (§3.9); lanes/missing-runs/runs/x.ndjson:10 is not one, so it orders by "
                "its bytes, before :9.")
    lane_vector("severity-undecided-trial-under-decided-rollup", r4_8a_undecided_trial,
                "R4-8a: LANE-3 reads trial lines in step 1 only (a failing trial fails the lane). In step 2 a case is "
                "its rollup: c1's inconclusive trial does not make the lane not_measured, since c1's rollup passed; "
                "and minimumN counts the two decided lines (c1's rollup, c2), not the passed trial: 3 is not met.")
    print("ruling vectors written:", ", ".join(sorted(p.name for p in OUT.iterdir())))


if __name__ == "__main__":
    main()
