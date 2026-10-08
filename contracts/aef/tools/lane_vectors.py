#!/usr/bin/env python3
"""Builds conformance/lane-vectors/: checkpoints with small sealed runs, and the lane results (spec 05, §5.3) and
[CKP-8] problems each gives.

The runs are made with build_conformance's helpers (result ids, seals). Every expected lane result below is written by
hand from the spec: nothing here evaluates a lane. The decision recorded in each manifest is input data, computed with
aef_decide so that the manifest itself is consistent ([CKP-7]); the lane vectors test [CKP-8].

Usage: python contracts/aef/tools/lane_vectors.py   (after build_conformance.py)
"""
import hashlib
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import aef_decide  # noqa: E402
from build_conformance import V, h, result_id, run_hash, seal, write_json, write_ndjson  # noqa: E402

OUT = Path(__file__).resolve().parents[1] / "1" / "conformance" / "lane-vectors"
VERSION = "v7"
SUBJECT = "agent:shop/assistant"
JUDGES = [{"model": "gpt-5.1", "provider": "azure.ai.openai", "mode": "single", "rubricDigest": "sha256:" + h("rubric-a")}]
SUITE = {"ref": "suite:shop/memory", "version": "3", "digest": "sha256:" + h("memory@3")}
EVALUATED_AT = "2026-10-08T00:00:00Z"
TYPED_ABSENCE = ("not_measured", "not_applicable", "skipped", "error", "pending")


# ---------------------------------------------------------------------------- runs

def summarize(lines, lane, entries, kinds):
    """SUM-3..5 for the runs this script makes (input data, so that each run is intact)."""
    metrics = []
    for metric, path in entries:
        values, total = [], 0
        for l in lines:
            if l["path"] != path or l.get("lane", lane) != lane or "trial" in l or l["state"] == "not_applicable":
                continue
            total += 1
            if l["state"] in TYPED_ABSENCE:
                continue
            if kinds[metric] in ("rate", "verdict"):
                values.append(1 if l["state"] == "passed" else 0)
            else:
                v = next((s["value"] for s in l.get("scores", []) if s["metric"] == metric), None)
                if v is not None:
                    values.append(v)
        n = len(values)
        s = sum(values)
        value = None if n == 0 else s if kinds[metric] == "count" else s / n
        metrics.append({"metric": metric, "path": path, "n": n, "N": total, "notMeasured": total - n, "value": value,
                        "verdict": "not_measured" if n == 0 else "passed", "rule": "recorded by the producer",
                        "sum": s, "sumSq": sum(v * v for v in values)})
    return {"lanes": [{"lane": lane, "metrics": metrics}]}


def make_run(folder, run_id, lines, *, lane="quality", metrics=(("m", "score", "higher_better"),), entries=(("m", "p"),),
             version=VERSION, mode="live", status="completed", ended="2026-10-01T12:00:00Z", judges=JUDGES,
             sealed=True, tamper=False, break_summary=False, subject_ref=SUBJECT, deployment=None, suite=SUITE,
             aggregate=None, more_lanes=()):
    """A small run. lines: dicts with case, path, state and optionally scores, severity, trial, trials, reason.
    Returns (runId, runHash)."""
    run_dir = folder / run_id
    subject = {"ref": subject_ref, "kind": "agent"}
    if version is not None:
        subject["version"] = version
    run = {"schemaVersion": V, "runId": run_id, "status": status, "producer": {"name": "agenteval-cli", "version": "1.0.0"},
           "subject": subject, "execution": {"targetMode": mode}, "suite": suite, "judges": judges,
           "startedAt": "2026-09-01T00:00:00Z", "endedAt": ended}
    if deployment is not None:
        run["deployment"] = {"ref": deployment}
    if status == "aborted":
        run["abortReason"] = "Stopped by the operator."
    full = []
    for l in lines:
        line = {"schemaVersion": V, "resultId": result_id(run_id, l["case"], l["path"], l.get("trial")), "caseId": l["case"],
                "path": l["path"], "evaluator": {"id": "code:check"}, "state": l["state"]}
        for k in ("lane", "trial", "trials", "severity", "scores", "reason"):
            if k in l:
                line[k] = l[k]
        if l["state"] in TYPED_ABSENCE and "reason" not in line:
            line["reason"] = "Nothing to measure."
        full.append(line)
    kinds = {m: k for m, k, _ in metrics}
    summary = summarize(full, lane, entries, kinds)
    for other, other_entries in more_lanes:
        summary["lanes"] += summarize(full, other, other_entries, kinds)["lanes"]
    if break_summary:
        summary["lanes"][0]["metrics"][0]["value"] = 0.123
    if aggregate is not None:  # (aggregate object, the producer's value)
        summary["lanes"][0]["metrics"][0].update(aggregate=aggregate[0], value=aggregate[1])
    write_json(run_dir / "run.json", run)
    write_ndjson(run_dir / "results.ndjson", full)
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": [
        {"id": m, "kind": k, "direction": d, "scale": "unbounded" if k in ("duration", "count") else {"min": 0, "max": 1},
         **({"unit": "ms"} if k == "duration" else {})} for m, k, d in metrics]})
    write_json(run_dir / "summary.json", {"schemaVersion": V, "runId": run_id, **summary})
    the_hash = run_hash(run_dir)
    if sealed:
        assert seal(run_dir, run, "producer", sealed_at="2026-10-07T00:00:00Z") == the_hash
    if tamper:
        p = run_dir / "results.ndjson"
        p.write_bytes(p.read_bytes().replace(b'"state":"failed"', b'"state":"passed"', 1))
        assert run_hash(run_dir) != the_hash
    return run_id, the_hash


def scores(**kv):
    return [{"metric": k, "value": v} for k, v in kv.items()]


# ---------------------------------------------------------------------------- checkpoints

def checkpoint(folder, lanes, recorded, *, state="decided", deployment=None):
    """lanes: (name, rule, [(runId, runHash)], blocking). recorded: lane -> the result the manifest's decisionInput
    records for it (None for no evidence)."""
    manifest_lanes = [{"lane": name, "rule": rule, "runs": [{"runId": r, "runHash": hh, "origin": "launched"} for r, hh in runs],
                       "blocking": blocking} for name, rule, runs, blocking in lanes]
    cp = {"schemaVersion": V, "checkpointId": "cp_" + hashlib.sha256(str(folder.name).encode()).hexdigest()[:12],
          "subject": {"ref": SUBJECT, "version": VERSION, **({"deployment": deployment} if deployment else {})},
          "lanes": manifest_lanes, "state": state, "outcome": None}
    if state == "decided":
        inp = {"subjectVersion": VERSION, "evaluatedAt": EVALUATED_AT, "supersededBy": None,
               "lanes": [{"lane": name, "blocking": blocking, "result": recorded[name],
                          **({"evidence": sorted({hh for _, hh in runs})} if recorded[name] is not None else {})}
                         for name, _, runs, blocking in lanes]}
        out = aef_decide.decide(inp)
        cp.update(outcome=out["outcome"], decisionInput=inp, decision=out)
    write_json(folder / "checkpoint.json", cp)


def expect(folder, lanes, results, problems, rules):
    write_json(folder / "expected.json", {
        "kind": "lane", "checkpoint": "checkpoint.json", "runs": "runs",
        "lanes": [{"lane": name, "result": results[name]} for name, _, _, _ in lanes],
        "problems": sorted(problems, key=lambda p: (p[0].encode("utf-8"), p[1])), "rules": rules + ["LANE-10"]})


def res(status, oldest, version=VERSION, axes=None):
    r = {"status": status, "subjectVersion": version, "oldestClosedAt": oldest}
    if axes is not None:
        r["axes"] = axes
    return r


def vector(name, build, deployment=None):
    folder = OUT / name
    runs = folder / "runs"
    lanes, results, problems, rules, recorded = build(runs)
    checkpoint(folder, lanes, recorded if recorded is not None else results, deployment=deployment)
    expect(folder, lanes, results, problems, rules)


def threshold(lane, op, value, path="p", metric="m"):
    return {"kind": "threshold", "lane": lane, "metric": metric, "path": path, "op": op, "value": value}


# ---------------------------------------------------------------------------- the vectors

def v_threshold(runs):
    q1 = make_run(runs, "Q1", [dict(case="c1", path="p", state="passed", scores=scores(m=1.0)),
                               dict(case="c2", path="p", state="failed", scores=scores(m=0.5)),
                               dict(case="c3", path="p", state="passed", scores=scores(m=0.75))])  # value 2.25 / 3 = 0.75
    q2 = make_run(runs, "Q2", [dict(case="c1", path="p", state="not_measured"), dict(case="c2", path="p", state="skipped")],
                  ended="2026-10-02T12:00:00Z")  # n = 0
    q3 = make_run(runs, "Q3", [dict(case="c1", path="p", state="failed", scores=scores(m=0.25)),
                               dict(case="c2", path="p", state="failed", scores=scores(m=0.25))],
                  ended="2026-09-28T08:30:00.5Z")  # value 0.25
    T1, T3 = "2026-10-01T12:00:00Z", "2026-09-28T08:30:00.5Z"
    lanes = [
        ("pass", threshold("quality", ">=", 0.5), [q1], True),
        ("ge-boundary", threshold("quality", ">=", 0.75), [q1], True),
        ("gt-boundary", threshold("quality", ">", 0.75), [q1], True),
        ("le", threshold("quality", "<=", 0.5), [q1], True),
        ("lt", threshold("quality", "<", 0.8), [q1], True),
        ("no-entry", threshold("quality", ">=", 0.5, path="nope"), [q1], True),
        ("other-summary-lane", threshold("security", ">=", 0.5), [q1], True),
        ("n-zero", threshold("quality", ">=", 0.5), [q2], True),
        ("one-fails", threshold("quality", ">=", 0.5), [q1, q3], True),
        ("failed-beats-unmeasured", threshold("quality", ">=", 0.5), [q2, q3], True),
        ("unmeasured-beats-passed", threshold("quality", ">=", 0.5), [q1, q2], True),
        ("no-runs", threshold("quality", ">=", 0.5), [], False),
    ]
    results = {
        "pass": res("passed", T1), "ge-boundary": res("passed", T1), "gt-boundary": res("failed", T1),
        "le": res("failed", T1), "lt": res("passed", T1), "no-entry": res("not_measured", T1),
        "other-summary-lane": res("not_measured", T1), "n-zero": res("not_measured", "2026-10-02T12:00:00Z"),
        "one-fails": res("failed", T3), "failed-beats-unmeasured": res("failed", T3),
        "unmeasured-beats-passed": res("not_measured", T1), "no-runs": None,
    }
    return lanes, results, [], ["LANE-2", "LANE-9", "ENC-8"], None


def v_severity(runs):
    s1 = make_run(runs, "S1", [dict(case="c1", path="a", state="passed"),
                               dict(case="c2", path="a", state="failed", severity="low"),
                               dict(case="c3", path="a", state="warn", severity="none"),
                               # A failing trial counts for the severity (LANE-3): low here. MajorityVote breaks the
                               # 1-to-1 tie toward the more severe verdict (RES-6), so the rollup fails too.
                               dict(case="c4", path="a", state="failed", severity="low", trial=0),
                               dict(case="c4", path="a", state="passed", trial=1),
                               dict(case="c4", path="a", state="failed", severity="low",
                                    trials={"n": 2, "passed": 1, "aggregation": "MajorityVote", "agree": False})],
                  metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "a"),), lane="security", ended="2026-10-03T00:00:00Z")
    s2 = make_run(runs, "S2", [dict(case="c1", path="a", state="failed")],
                  metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "a"),), lane="security", ended="2026-10-03T00:00:00Z")
    s3 = make_run(runs, "S3", [dict(case="c1", path="a", state="passed"), dict(case="c2", path="a", state="not_measured")],
                  metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "a"),), lane="security", ended="2026-10-03T00:00:00Z")
    s4 = make_run(runs, "S4", [dict(case="c1", path="a", state="failed", severity="medium"), dict(case="c2", path="a", state="error")],
                  metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "a"),), lane="security", ended="2026-10-03T00:00:00Z")
    s5 = make_run(runs, "S5", [dict(case="c1", path="a", state="warn", severity="high")],
                  metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "a"),), lane="security", ended="2026-10-03T00:00:00Z")
    ok_rate = dict(metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "a"),), lane="security", ended="2026-10-03T00:00:00Z")
    s_inc = make_run(runs, "S-inconclusive", [dict(case=f"c{i}", path="a", state="inconclusive") for i in (1, 2, 3)], **ok_rate)
    s_empty = make_run(runs, "S-empty", [], **ok_rate)
    s_na = make_run(runs, "S-not-applicable", [dict(case="c1", path="a", state="passed"), dict(case="c2", path="a", state="not_applicable")], **ok_rate)
    s_two = make_run(runs, "S-two-passed", [dict(case="c1", path="a", state="passed"), dict(case="c2", path="a", state="passed")], **ok_rate)
    # A critical failure in one trial, behind a rollup that passes (AnyPass): the lane still fails (LANE-3).
    s_trial = make_run(runs, "S-failing-trial", [dict(case="c1", path="a", state="failed", severity="critical", trial=0),
                                                dict(case="c1", path="a", state="passed", trial=1),
                                                dict(case="c1", path="a", state="passed",
                                                     trials={"n": 2, "passed": 1, "aggregation": "AnyPass", "agree": False})],
                       **ok_rate)
    T = "2026-10-03T00:00:00Z"
    sev = lambda m, n=None: {"kind": "severity", "max": m, **({"minimumN": n} if n else {})}
    lanes = [
        ("low-allows-low", sev("low"), [s1], True),            # worst counted failure: low (the critical one is a trial)
        ("none-refuses-low", sev("none"), [s1], True),
        ("missing-is-critical", sev("high"), [s2], True),
        ("unmeasured", sev("high"), [s3], True),
        ("failure-beats-unmeasured", sev("low"), [s4], True),
        ("two-runs", sev("medium"), [s1, s3], True),
        ("warn-counts", sev("medium"), [s5], True),
        ("all-inconclusive", sev("none"), [s_inc], True),         # no decision anywhere: never a pass
        ("no-results", sev("none"), [s_empty], True),              # no evidence at all: never a pass
        ("not-applicable-ignored", sev("none"), [s_na], True),     # one passed line; not_applicable takes no part
        ("minimum-not-met", sev("none", 3), [s_two], True),        # two decided lines, three needed
        ("minimum-met", sev("none", 2), [s_two], True),
        ("failing-trial-counts", sev("high"), [s_trial], True),
    ]
    results = {"low-allows-low": res("passed", T), "none-refuses-low": res("failed", T), "missing-is-critical": res("failed", T),
               "unmeasured": res("not_measured", T), "failure-beats-unmeasured": res("failed", T),
               "two-runs": res("not_measured", T), "warn-counts": res("failed", T),
               "all-inconclusive": res("not_measured", T), "no-results": res("not_measured", T),
               "not-applicable-ignored": res("passed", T), "minimum-not-met": res("not_measured", T),
               "minimum-met": res("passed", T), "failing-trial-counts": res("failed", T)}
    return lanes, results, [], ["LANE-3", "RES-9", "RES-2", "RES-8"], None


def v_severity_max_unknown(runs):
    """VER-8, §7.3: a severity level a later minor added (reader-valid only) gives not_measured, never a pass."""
    s = make_run(runs, "SM", [dict(case="c1", path="a", state="passed")], metrics=(("ok", "rate", "higher_better"),),
                 entries=(("ok", "a"),), lane="security", ended="2026-10-03T00:00:00Z")
    lanes = [("max-unknown", {"kind": "severity", "max": "catastrophic"}, [s], True),
             ("kind-unknown", {"kind": "percentile", "lane": "security", "metric": "ok", "path": "a", "value": 0.9}, [s], True)]
    T = "2026-10-03T00:00:00Z"
    # What a 1.1 verifier recorded: passed. A 1.0 verifier cannot recompute it, so it does not call it wrong (CKP-8).
    recorded = {"max-unknown": res("passed", T), "kind-unknown": res("passed", T)}
    return (lanes, {"max-unknown": res("not_measured", T), "kind-unknown": res("not_measured", T)},
            [["lanes/kind-unknown", "unverifiable"], ["lanes/max-unknown", "unverifiable"]], ["LANE-3", "VER-8", "CKP-8"], recorded)


def v_evidence_present(runs):
    line = [dict(case="c1", path="p", state="passed", scores=scores(m=1.0))]
    e1 = make_run(runs, "E1", line, ended="2026-10-03T00:00:00Z")
    e2 = make_run(runs, "E2", line, ended="2026-10-04T00:00:00Z")
    e3 = make_run(runs, "E3", line, mode="scripted", ended="2026-10-05T00:00:00Z")
    ep = lambda n: {"kind": "evidence-present", "runs": n}
    lanes = [("two-of-two", ep(2), [e1, e2], True), ("three-needed", ep(3), [e1, e2], True),
             ("ineligible-run", ep(1), [e1, e3], True)]
    T = "2026-10-03T00:00:00Z"
    results = {"two-of-two": res("passed", T), "three-needed": res("not_measured", T), "ineligible-run": res("not_measured", T)}
    return lanes, results, [], ["LANE-1", "LANE-4"], None


def v_eligibility(runs):
    good = [dict(case="c1", path="p", state="passed", scores=scores(m=1.0)),
            dict(case="c2", path="p", state="failed", scores=scores(m=0.5))]  # value 0.75
    T = "2026-10-05T00:00:00Z"
    live = make_run(runs, "G-live", good, ended=T)
    scripted = make_run(runs, "G-scripted", good, mode="scripted", ended=T)
    replayed = make_run(runs, "G-replayed", good, mode="replayed", ended=T)
    mocked = make_run(runs, "G-mocked", good, mode="mocked", ended=T)
    aborted = make_run(runs, "G-aborted", good, status="aborted", ended=T)
    v6 = make_run(runs, "G-v6", good, version="v6", ended=T)
    noversion = make_run(runs, "G-noversion", good, version=None, ended=T)
    unsealed = make_run(runs, "G-unsealed", good, ended=T, sealed=False)
    tampered = make_run(runs, "G-tampered", good, ended=T, tamper=True)
    bad_summary = make_run(runs, "G-bad-summary", good, ended=T, break_summary=True)
    shutil.copytree(runs / "G-live", runs / "A-tampered-copy-of-G-live")  # sorts before the original
    copy_results = runs / "A-tampered-copy-of-G-live" / "results.ndjson"
    copy_results.write_bytes(copy_results.read_bytes().replace(b'"state":"failed"', b'"state":"passed"', 1))
    rule = threshold("quality", ">=", 0.5)
    lanes = [
        ("live", rule, [live], True),
        ("scripted", rule, [scripted], True),
        ("replayed", rule, [replayed], True),
        ("mocked", rule, [mocked], True),
        ("aborted", rule, [aborted], True),
        ("other-version", rule, [v6], True),
        ("mixed-versions", rule, [live, v6], True),
        ("no-version", rule, [noversion], True),
        ("unsealed", rule, [unsealed], True),
        ("tampered", rule, [tampered], True),
        ("not-intact", rule, [bad_summary], True),
        ("missing", rule, [("R-404", h("never written"))], True),
        ("wrong-hash", rule, [("G-live", "0" * 64)], True),
        ("one-missing", rule, [live, ("R-404", h("never written"))], True),
        ("intact-copy-beside-a-tampered-one", rule, [live], True),
    ]
    results = {
        "live": res("passed", T), "scripted": res("not_measured", T), "replayed": res("not_measured", T),
        "mocked": res("not_measured", T), "aborted": res("not_measured", T), "other-version": res("passed", T, "v6"),
        "mixed-versions": res("passed", T, "v6"), "no-version": res("not_measured", T), "unsealed": res("not_measured", EVALUATED_AT),
        # A run changed after sealing is found by its seal's run hash (SEAL-4), and is not intact.
        "tampered": res("not_measured", EVALUATED_AT), "not-intact": res("not_measured", EVALUATED_AT), "missing": None, "wrong-hash": None,
        "one-missing": res("not_measured", T),
        "intact-copy-beside-a-tampered-one": res("passed", T),
    }
    problems = [
        ["lanes/unsealed/runs/G-unsealed", "run-unverified"],
        ["lanes/tampered/runs/G-tampered", "run-unverified"],
        ["lanes/not-intact/runs/G-bad-summary", "run-unverified"],
        ["lanes/missing/runs/R-404", "run-missing"],
        ["lanes/wrong-hash/runs/G-live", "run-missing"],
        ["lanes/one-missing/runs/R-404", "run-missing"],
    ]
    return lanes, results, problems, ["LANE-1", "LANE-9", "CKP-8", "RUN-7"], None


def comparison_runs(runs):
    """A baseline and candidates over 23 cases at path mem. Baseline: recall 0.5, latency 100, tokens 10 each.
    A candidate's case regresses (recall 0.4, latency 120), improves (0.6, 80) or ties (0.5, 100)."""
    M = (("recall", "score", "higher_better"), ("latency", "duration", "lower_better"), ("tokens", "count", "none"))
    E = (("recall", "mem"), ("latency", "mem"), ("tokens", "mem"))

    def lines(pattern, extra=()):
        out = []
        for i, kind in enumerate(pattern, start=1):
            r, l = {"r": (0.4, 120), "i": (0.6, 80), "t": (0.5, 100)}[kind]
            out.append(dict(case=f"b{i:02d}", path="mem", state="passed", scores=scores(recall=r, latency=l, tokens=10)))
        return out + list(extra)

    def run(run_id, pattern, **kw):
        kw.setdefault("ended", "2026-10-06T00:00:00Z")
        return make_run(runs, run_id, lines(pattern, kw.pop("extra", ())), lane="memory", metrics=M, entries=E, **kw)

    other_judges = [{"model": "gpt-6", "provider": "azure.ai.openai", "mode": "single", "rubricDigest": "sha256:" + h("rubric-b")}]
    out = {
        "B": run("B", "t" * 23, version="v6", ended="2026-09-20T00:00:00Z"),
        "BS": run("BS", "t" * 23, version="v6", mode="scripted", ended="2026-09-20T00:00:00Z"),
        # r 15, i 5, 3 ties, and a case the baseline does not have.
        "C15": run("C15", "r" * 15 + "i" * 5 + "t" * 3, extra=[dict(case="b24", path="mem", state="passed", scores=scores(recall=0.1, latency=500, tokens=10))]),
        # r 13, i 7; b23 not measured, so 2 ties.
        "C13": run("C13", "r" * 13 + "i" * 7 + "t" * 2, extra=[dict(case="b23", path="mem", state="not_measured")]),
        "C14": run("C14", "r" * 14 + "i" * 6 + "t" * 3),
        "C9": run("C9", "r" * 9 + "i" + "t" * 13),
        "CFEW": run("CFEW", "r" * 8 + "i" * 2 + "t" * 13),
        "CJ": run("CJ", "r" * 13 + "i" * 7 + "t" * 3, judges=other_judges),
        "CS": run("CS", "r" * 13 + "i" * 7 + "t" * 3, mode="scripted"),
    }
    return out


def cmp_rule(baseline, *, metric="recall", significance=0.05, pairs=20, axes=("subject", "suite", "judges", "rubrics", "target-mode")):
    return {"kind": "comparison", "lane": "memory", "metric": metric, "path": "mem",
            "baseline": {"runId": baseline[0], "runHash": baseline[1]}, "significance": significance, "minimumPairs": pairs,
            "axes": list(axes)}


def v_comparison(runs):
    r = comparison_runs(runs)
    B = r["B"]
    T = "2026-10-06T00:00:00Z"
    lanes = [
        # p = sum C(20, k), k = 15..20, / 2^20 = 21700 / 1048576 = 0.0207 <= 0.05
        ("regressed", cmp_rule(B), [r["C15"]], True),
        # p = 137980 / 1048576 = 0.1316 > 0.05
        ("not-significant", cmp_rule(B), [r["C13"]], True),
        # p = 60460 / 1048576 = 0.0577 > 0.05 (a normal approximation without continuity correction gives 0.037)
        ("exact-not-normal", cmp_rule(B), [r["C14"]], True),
        # one-sided p = 0.0207 <= 0.03 (a two-sided test gives 0.0414 > 0.03)
        ("one-sided", cmp_rule(B, significance=0.03), [r["C15"]], True),
        # m = 10, r = 9: p = (10 + 1) / 1024 = 0.0107421875 exactly, and significance is exactly that: failed
        ("boundary-equal", cmp_rule(B, significance=0.0107421875, pairs=10), [r["C9"]], True),
        # ... and a significance just below p: passed
        ("boundary-below", cmp_rule(B, significance=0.0107421874, pairs=10), [r["C9"]], True),
        # m = 10 < 20
        ("too-few-pairs", cmp_rule(B), [r["CFEW"]], True),
        # 23 pairs, 13 of them ties: m = 10 < 11
        ("ties-dropped", cmp_rule(B, pairs=11), [r["C9"]], True),
        # latency is lower_better: 15 cases went 100 -> 120 (regressions), 5 went 100 -> 80
        ("lower-better", cmp_rule(B, metric="latency"), [r["C15"]], True),
        ("direction-none", cmp_rule(B, metric="tokens"), [r["C15"]], True),
        ("judges-differ", cmp_rule(B, axes=("suite", "judges")), [r["CJ"]], True),
        ("axes-in-rule-order", cmp_rule(B, axes=("rubrics", "target-mode", "judges")), [r["CJ"]], True),
        ("candidate-scripted", cmp_rule(B), [r["CS"]], True),
        ("baseline-scripted", cmp_rule(r["BS"]), [r["C13"]], True),
        ("baseline-missing", cmp_rule(("B", "0" * 64)), [r["C13"]], True),
        ("two-candidates", cmp_rule(B), [r["C13"], r["C15"]], True),
    ]
    results = {
        "regressed": res("failed", T), "not-significant": res("passed", T), "exact-not-normal": res("passed", T),
        "one-sided": res("failed", T), "boundary-equal": res("failed", T), "boundary-below": res("passed", T),
        "too-few-pairs": res("not_measured", T), "ties-dropped": res("not_measured", T), "lower-better": res("failed", T),
        "direction-none": res("not_measured", T), "judges-differ": res("incomparable", T, axes=["judges"]),
        "axes-in-rule-order": res("incomparable", T, axes=["rubrics", "judges"]),
        "candidate-scripted": res("not_measured", T), "baseline-scripted": res("not_measured", T),
        "baseline-missing": res("not_measured", T), "two-candidates": res("not_measured", T),
    }
    problems = [["lanes/baseline-missing/runs/B", "run-missing"]]
    return lanes, results, problems, ["LANE-5", "LANE-6", "LANE-7", "LANE-8", "LANE-9"], None


def v_binding(runs):
    """Evidence about another subject, deployment or suite never counts (LANE-1); minimumN (LANE-2). The checkpoint names
    the deployment deployment:shop/assistant@prod."""
    good = [dict(case="c1", path="p", state="passed", scores=scores(m=1.0)),
            dict(case="c2", path="p", state="failed", scores=scores(m=0.5))]  # n 2, value 0.75
    T = "2026-10-05T00:00:00Z"
    D = "deployment:shop/assistant@prod"
    right = make_run(runs, "K-right", good, deployment=D, ended=T)
    other_subject = make_run(runs, "K-other-subject", good, deployment=D, subject_ref="agent:someone-else/other-agent", ended=T)
    other_deployment = make_run(runs, "K-other-deployment", good, deployment="deployment:shop/assistant@staging", ended=T)
    no_deployment = make_run(runs, "K-no-deployment", good, ended=T)
    other_suite_version = make_run(runs, "K-suite-v4", good, deployment=D, suite=dict(SUITE, version="4"), ended=T)
    other_suite_digest = make_run(runs, "K-suite-other-digest", good, deployment=D,
                                  suite=dict(SUITE, digest="sha256:" + h("memory@3 edited")), ended=T)
    rule = threshold("quality", ">=", 0.5)
    with_suite = dict(rule, suite={"ref": SUITE["ref"], "version": SUITE["version"], "digest": SUITE["digest"]})
    by_ref_only = dict(rule, suite={"ref": SUITE["ref"]})
    lanes = [
        ("right", rule, [right], True),
        ("other-subject", rule, [other_subject], True),
        ("other-deployment", rule, [other_deployment], True),
        ("no-deployment", rule, [no_deployment], True),
        ("one-of-two-other-subject", rule, [right, other_subject], True),
        ("suite-matches", with_suite, [right], True),
        ("suite-version-differs", with_suite, [other_suite_version], True),
        ("suite-digest-differs", with_suite, [other_suite_digest], True),
        ("suite-by-ref-only", by_ref_only, [other_suite_version], True),   # only the ref is named: v4 is that suite
        ("minimum-n-not-met", dict(rule, minimumN=3), [right], True),      # n is 2
        ("minimum-n-met", dict(rule, minimumN=2), [right], True),
        ("evidence-present-other-subject", {"kind": "evidence-present", "runs": 1}, [other_subject], True),
    ]
    results = {"right": res("passed", T), "other-subject": res("not_measured", EVALUATED_AT), "other-deployment": res("not_measured", EVALUATED_AT),
               "no-deployment": res("not_measured", EVALUATED_AT), "one-of-two-other-subject": res("not_measured", T),
               "suite-matches": res("passed", T), "suite-version-differs": res("not_measured", EVALUATED_AT),
               "suite-digest-differs": res("not_measured", EVALUATED_AT), "suite-by-ref-only": res("passed", T),
               "minimum-n-not-met": res("not_measured", T), "minimum-n-met": res("passed", T),
               "evidence-present-other-subject": res("not_measured", EVALUATED_AT)}
    return lanes, results, [], ["LANE-1", "LANE-2", "LANE-4", "LANE-9"], None


def v_comparison_large(runs):
    """m = 1,200 pairs (LANE-8, LANE-11). 630 regressed, 570 improved: p = 0.044246, failed at 0.05, passed at 0.04
    (these two a careful floating-point implementation also gets right). The lanes one binary64 step either side of the
    exact p for 629 regressions are the ones only exact arithmetic gets right."""
    M = (("recall", "score", "higher_better"),)
    E = (("recall", "mem"),)
    base = [dict(case=f"b{i:04d}", path="mem", state="passed", scores=scores(recall=0.5)) for i in range(1, 1201)]
    cand = [dict(case=f"b{i:04d}", path="mem", state="passed", scores=scores(recall=0.4 if i <= 630 else 0.6)) for i in range(1, 1201)]
    B = make_run(runs, "BL", base, lane="memory", metrics=M, entries=E, version="v6", ended="2026-09-20T00:00:00Z")
    C = make_run(runs, "CL", cand, lane="memory", metrics=M, entries=E, ended="2026-10-06T00:00:00Z")
    # 629 regressed, 571 improved: p = 0.049918595773026... lies just above the binary64 value 0.04991859577302666 and
    # nearer it than the next one, 0.04991859577302667 (both computed once, exactly, by hand). With the lower value as
    # the significance, p > it: passed; but p in binary64, correctly rounded, equals it: a floating-point verifier says
    # failed. With the upper value, p <= it: failed. A normal approximation is wrong by far more than one step.
    cand629 = [dict(case=f"b{i:04d}", path="mem", state="passed", scores=scores(recall=0.4 if i <= 629 else 0.6)) for i in range(1, 1201)]
    C629 = make_run(runs, "CL629", cand629, lane="memory", metrics=M, entries=E, ended="2026-10-06T00:00:00Z")
    T = "2026-10-06T00:00:00Z"
    lanes = [("large-at-005", cmp_rule(B, pairs=1000), [C], True),
             ("large-at-004", cmp_rule(B, significance=0.04, pairs=1000), [C], True),
             ("one-step-below-p", cmp_rule(B, significance=0.04991859577302666, pairs=1000), [C629], True),
             ("one-step-above-p", cmp_rule(B, significance=0.04991859577302667, pairs=1000), [C629], True)]
    results = {"large-at-005": res("failed", T), "large-at-004": res("passed", T),
               "one-step-below-p": res("passed", T), "one-step-above-p": res("failed", T)}
    return lanes, results, [], ["LANE-8", "LANE-11"], None


def v_aggregates(runs):
    """SUM-8 and LANE-2: a median (defined, recomputed) can decide a lane; pass@k (the producer's, unchecked) cannot."""
    lines = [dict(case=f"c{i}", path="p", state="passed", scores=scores(m=v)) for i, v in ((1, 0.4), (2, 0.9), (3, 0.95))]
    T = "2026-10-05T00:00:00Z"
    med = make_run(runs, "AG-median", lines, ended=T, aggregate=({"method": "median"}, 0.9))
    passk = make_run(runs, "AG-pass-at-k", lines, ended=T, aggregate=({"method": "pass@k", "k": 3}, 0.99))
    rule = threshold("quality", ">=", 0.85)
    lanes = [("median-decides", rule, [med], True), ("pass-at-k-cannot", rule, [passk], True)]
    results = {"median-decides": res("passed", T), "pass-at-k-cannot": res("not_measured", T)}
    return lanes, results, [], ["SUM-8", "LANE-2"], None


def v_severity_scope(runs):
    """LANE-3 with lane and path: only the lines of that summary lane, at that path or below it, count. "toolsets" is not
    below "tools" (a prefix of the text, not of the path)."""
    sec = lambda case, path, state, **kw: dict(case=case, path=path, state=state, lane="security", **kw)
    run = make_run(runs, "SS", [sec("c1", "tools", "passed"), sec("c2", "tools/fetch", "failed", severity="high"),
                                sec("c3", "chat", "passed"), sec("c5", "toolsets", "failed", severity="critical"),
                                dict(case="c4", path="chat", state="failed", severity="critical", lane="quality")],
                   metrics=(("ok", "rate", "higher_better"),), entries=(("ok", "tools"), ("ok", "chat")), lane="security",
                   more_lanes=(("quality", (("ok", "chat"),)),), ended="2026-10-03T00:00:00Z")
    T = "2026-10-03T00:00:00Z"
    sev = lambda m, **scope: {"kind": "severity", "max": m, **scope}
    lanes = [("lane-and-path", sev("low", lane="security", path="chat"), [run], True),    # c3 only
             ("lane-only", sev("low", lane="security"), [run], True),                     # c2 high, c5 critical
             ("path-below", sev("high", path="tools"), [run], True),                      # c1, c2; not toolsets
             ("other-lane", sev("low", lane="quality"), [run], True),                     # c4 critical
             ("path-with-no-lines", sev("none", path="nowhere"), [run], True),            # nothing decides
             ("unscoped", sev("high"), [run], True)]                                      # c4, c5 critical
    results = {"lane-and-path": res("passed", T), "lane-only": res("failed", T), "path-below": res("passed", T),
               "other-lane": res("failed", T), "path-with-no-lines": res("not_measured", T), "unscoped": res("failed", T)}
    return lanes, results, [], ["LANE-3"], None


def v_comparison_unknown_axis(runs):
    r = comparison_runs(runs)
    lanes = [("unknown-axis", cmp_rule(r["B"], axes=("suite", "weather")), [r["C13"]], True)]
    results = {"unknown-axis": res("incomparable", "2026-10-06T00:00:00Z", axes=["weather"])}
    return lanes, results, [["lanes/unknown-axis", "unverifiable"]], ["LANE-6", "VER-3", "CKP-8"], None


def v_recorded_differs(runs):
    """[CKP-8]: the lane results recomputed from the runs, against what the manifest recorded."""
    q1 = make_run(runs, "Q1", [dict(case="c1", path="p", state="passed", scores=scores(m=1.0))])  # 1.0
    q3 = make_run(runs, "Q3", [dict(case="c1", path="p", state="failed", scores=scores(m=0.25))])  # 0.25
    c = comparison_runs(runs)
    T, TC = "2026-10-01T12:00:00Z", "2026-10-06T00:00:00Z"
    rule = threshold("quality", ">=", 0.5)
    lanes = [
        ("status-differs", rule, [q3], True),
        ("version-differs", rule, [q1], True),
        ("oldest-differs", rule, [q1], True),
        ("axes-differ", cmp_rule(c["B"], axes=("suite", "judges")), [c["CJ"]], True),
        ("recorded-null", rule, [q1], True),
        ("all-equal", rule, [q1], True),
    ]
    results = {"status-differs": res("failed", T), "version-differs": res("passed", T), "oldest-differs": res("passed", T),
               "axes-differ": res("incomparable", TC, axes=["judges"]), "recorded-null": res("passed", T),
               "all-equal": res("passed", T)}
    recorded = dict(results)
    recorded.update({"status-differs": res("passed", T), "version-differs": res("passed", T, "v6"),
                     "oldest-differs": res("passed", "2026-10-01T12:00:01Z"),
                     "axes-differ": res("incomparable", TC, axes=["suite"]), "recorded-null": None})
    problems = [["lanes/status-differs", "lane-result"], ["lanes/version-differs", "lane-version"],
                ["lanes/oldest-differs", "oldest-closed"], ["lanes/axes-differ", "lane-result"],
                ["lanes/recorded-null", "lane-result"]]
    return lanes, results, problems, ["CKP-8"], recorded


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    vector("threshold", v_threshold)
    vector("severity", v_severity)
    vector("evidence-present", v_evidence_present)
    vector("eligibility", v_eligibility)
    vector("comparison", v_comparison)
    vector("comparison-unknown-axis", v_comparison_unknown_axis)
    vector("aggregates", v_aggregates)
    vector("severity-scope", v_severity_scope)
    vector("severity-max-unknown", v_severity_max_unknown)
    vector("comparison-large", v_comparison_large)
    vector("binding", v_binding, deployment="deployment:shop/assistant@prod")
    vector("recorded-differs", v_recorded_differs)
    print("lane vectors written:", sorted(p.name for p in OUT.iterdir()))


if __name__ == "__main__":
    main()
