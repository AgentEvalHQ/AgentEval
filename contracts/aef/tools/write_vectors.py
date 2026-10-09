#!/usr/bin/env python3
"""Builds conformance/write-vectors/: the write-side vectors of spec 09 (§9.2), in which an implementation computes or
writes something and the conformance runner judges the result:

- summarize/<name>/: a run without its summary.json (run/), the entries to compute (request.json) and the expected
  summary.json ([SUM-2]-[SUM-9]); or a request the Producer refuses;
- produce/<name>/: a scenario (scenario.json: a closed run's run.json and metrics.json, each case's result tree and
  trial trees as facts, a summary request), the expected lines of results.ndjson (expected-results.ndjson, compared as
  a set: [RES-4]-[RES-8]) and the expected summary.json; or a scenario that contradicts itself, which the Producer
  refuses;
- seal-write/<name>/: an unsealed closed run, sealedBy and sealedAt, the expected manifest and predicate
  ([SEAL-1]-[SEAL-5]); or a run the sealer refuses (an open run, a seal dated before the run closed, a run sealed on
  custody whose paths or files are not valid);
- sign/<name>/: a file, its payload type, a test private key (signature-vectors/keys/) and a trust policy holding its
  public key ([SIG-1]-[SIG-3]), marked with the algorithm it needs (ecdsa-p256 or ed25519); for Ed25519, whose
  signatures are deterministic, also the expected signature.

Every expected value is written by hand below. For a summary, the generator also derives each entry from the lines
with its own reading of [SUM-3] and [SUM-4] and stops unless it agrees with the hand-written N and measured values;
it then computes sum, sumSq and value exactly (fractions.Fraction over the binary64 values, rounded once) and stops
unless they agree with the hand-written decimals. For a produce scenario, it builds the expected lines with its own
reading of [RES-4]-[RES-8] and stops unless each line's derived fields (its parent, a rollup's n, passed and agree, a
composite's measured, total and unmeasured) are the hand-written ones. For a seal, it stops unless the byte order of
the paths is the hand-written order. It implements only what it writes: result ids (build_conformance.result_id),
manifests and run hashes, key ids and Ed25519 signatures (aef_crypto, checked against RFC 8032 by its self-test). It
never calls tools/aef_produce.py, the reference implementation of the four operations.

Usage: python contracts/aef/tools/write_vectors.py   (after build_conformance.py and signature_vectors.py, whose
files the sign vectors copy; then build_index.py)
"""
import hashlib
import json
import shutil
import sys
from fractions import Fraction
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import aef_crypto as C  # noqa: E402
from build_conformance import overlay_batches, result_id  # noqa: E402
from signature_vectors import ED, ID, PEM, WHO  # noqa: E402

CONF = Path(__file__).resolve().parents[1] / "1" / "conformance"
OUT = CONF / "write-vectors"
V = "1.0"
INTOTO = "application/vnd.in-toto+json"
CHECKPOINT = "application/vnd.agenteval.aef.checkpoint+json"
KEYS = "../../../signature-vectors/keys"  # the test keys, from a sign vector's folder
ABSENT = {"not_measured", "skipped", "error", "pending"}


def write_bytes(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def write_json(path, obj):
    write_bytes(path, (json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


def write_ndjson(path, objs):
    write_bytes(path, "".join(json.dumps(o, ensure_ascii=False, separators=(",", ":")) + "\n" for o in objs).encode("utf-8"))


def h(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------- summarize

def metric(mid, kind, direction="higher_better", scale=None, unit=None):
    m = {"id": mid, "kind": kind, "direction": direction, "scale": scale if scale is not None else {"min": 0, "max": 1}}
    return m | ({"unit": unit} if unit else {})


def L(case, state, scores=None, path="q", **extra):
    """A result line, before its run id is known: case, state, {metric: value}, path, and other fields."""
    return {"case": case, "state": state, "scores": scores or {}, "path": path, "extra": extra}


def E(metric_id, path, *, N, measured, sum, sumSq, value, rule=None, verdict=None, aggregate=None, given=None):
    """A hand-written entry: N and the measured values in file order (the hand reading of SUM-3 and SUM-4), and
    sum, sumSq and value as decimals worked out by hand (value None when nothing was measured). rule and verdict are
    the producer's; given is the producer's value for an aggregate method AEF does not define."""
    return {"metric": metric_id, "path": path, "N": N, "measured": measured, "sum": sum, "sumSq": sumSq,
            "value": value, "rule": rule, "verdict": verdict, "aggregate": aggregate, "given": given}


def lines_of(run_id, specs):
    out = []
    for s in specs:
        extra = dict(s["extra"])
        o = {"schemaVersion": V, "resultId": result_id(run_id, s["case"], s["path"], extra.get("trial")),
             "caseId": s["case"], "path": s["path"], "evaluator": {"id": "e:" + s["path"]}, "state": s["state"]}
        if s["state"] in ABSENT | {"not_applicable"}:
            o["reason"] = extra.pop("reason", "The input the evaluator needs was not there.")
        if s["scores"]:
            o["scores"] = [{"metric": m, "value": v} for m, v in s["scores"].items()]
        o.update(extra)
        out.append(o)
    return out


def derive(lines, lane, lane_names, metric_id, kind, path):
    """SUM-3 and SUM-4, read independently of any implementation: (N, the measured values in file order)."""
    N, measured = 0, []
    for line in lines:
        if "trial" in line or line["path"] != path:
            continue
        if ("lane" in line and line["lane"] != lane) or ("lane" not in line and len(lane_names) != 1):
            continue
        if line["state"] == "not_applicable":
            continue
        N += 1
        if line["state"] in ABSENT:
            continue
        if kind in ("rate", "verdict"):
            if line["state"] != "scored":
                measured.append(1 if line["state"] == "passed" else 0)
        else:
            scores = [s["value"] for s in line.get("scores", []) if s["metric"] == metric_id]
            if len(scores) == 1:
                measured.append(scores[0])
    return N, measured


def num(exact):
    """An exact value rounded once to binary64; an integer in plain digits."""
    return exact.numerator if exact.denominator == 1 and abs(exact.numerator) <= 2 ** 53 else float(exact)


def agrees(exact, hand, where):
    """The exact value and the hand-written decimal agree to 1e-12 (relative)."""
    assert abs(exact - Fraction(hand)) <= Fraction(1, 10 ** 12) * max(1, abs(exact)), f"{where}: {float(exact)} != {hand}"


def summarize_run(d, run_id, metrics, specs, status="completed"):
    """Writes a run without its summary.json; returns its result lines."""
    run = {"schemaVersion": V, "runId": run_id, "status": status, "producer": {"name": "p", "version": "1"},
           "subject": {"ref": "agent:a/b", "kind": "agent", "version": "v1"}, "execution": {"targetMode": "live"},
           "startedAt": "2026-10-01T00:00:00Z", "contentCapture": "on"}
    if status != "running":
        run["endedAt"] = "2026-10-01T00:01:00Z"
    lines = lines_of(run_id, specs)
    write_json(d / "run" / "run.json", run)
    write_ndjson(d / "run" / "results.ndjson", lines)
    write_json(d / "run" / "metrics.json", {"schemaVersion": V, "metrics": metrics})
    return lines


def summarize_refused(name, why, rules, metrics, specs, request, results=None):
    """A request the Producer refuses (exit status 2): an input error of §9.3. results, when given, rewrites the bytes
    of results.ndjson (a file that does not read)."""
    d = OUT / "summarize" / name
    summarize_run(d, "summarize-" + name, metrics, specs)
    if results is not None:
        path = d / "run" / "results.ndjson"
        path.write_bytes(results(path.read_bytes()))
    write_json(d / "request.json", request)
    write_json(d / "expected.json", {"kind": "summarize", "rules": rules, "run": "run", "request": "request.json",
                                     "refused": True, "why": why})


def summarize_vector(name, why, rules, metrics, specs, lanes, status="completed"):
    run_id = "summarize-" + name
    d = OUT / "summarize" / name
    lines = summarize_run(d, run_id, metrics, specs, status)
    request, summary = expected_summary(name, run_id, metrics, lines, lanes)
    write_json(d / "request.json", request)
    write_json(d / "expected.json", {"kind": "summarize", "rules": rules, "run": "run", "request": "request.json",
                                     "summary": summary, "why": why})


def expected_summary(name, run_id, metrics, lines, lanes):
    """(the request, the expected summary.json) for the hand-written entries of each lane, checked against the
    lines: N and the measured values by derive(), then sum, sumSq and value exactly."""
    kinds = {m["id"]: m["kind"] for m in metrics}
    names = [lane for lane, _ in lanes]
    request, expected = [], []
    for lane, entries in lanes:
        asked, want = [], []
        for e in entries:
            where = f"{name}: {lane}/{e['metric']}@{e['path']}"
            N, measured = derive(lines, lane, names, e["metric"], kinds[e["metric"]], e["path"])
            assert (N, measured) == (e["N"], e["measured"]), f"{where}: derived N={N}, measured={measured}"
            values = [Fraction(v) for v in measured]  # the exact binary64 values the lines hold
            n, total, squares = len(values), sum(values, Fraction(0)), sum((v * v for v in values), Fraction(0))
            agrees(total, e["sum"], where + " sum")
            agrees(squares, e["sumSq"], where + " sumSq")
            method = (e["aggregate"] or {}).get("method")
            if n == 0:
                value = None
            elif method is None:
                value = total if kinds[e["metric"]] == "count" else total / n
            elif method == "min":
                value = min(values)
            elif method == "max":
                value = max(values)
            elif method == "median":
                ordered = sorted(values)
                value = ordered[n // 2] if n % 2 else (ordered[n // 2 - 1] + ordered[n // 2]) / 2
            else:
                value = Fraction(e["given"])
            assert (value is None) == (e["value"] is None), f"{where}: value {value} against {e['value']}"
            if value is not None:
                agrees(value, e["value"], where + " value")
            ask = {"metric": e["metric"], "path": e["path"]}
            entry = {"metric": e["metric"], "path": e["path"], "N": N, "n": n, "notMeasured": N - n, "sum": num(total),
                     "sumSq": num(squares), "value": None if value is None else num(value),
                     "verdict": "not_measured" if n == 0 else e["verdict"] or "scored"}
            for field in ("rule", "verdict", "aggregate"):
                if e[field] is not None:
                    ask[field] = e[field]
            for field in ("rule", "aggregate"):
                if e[field] is not None:
                    entry[field] = e[field]
            if e["given"] is not None:
                ask["value"] = e["given"]
            asked.append(ask)
            want.append(entry)
        request.append({"lane": lane, "metrics": asked})
        expected.append({"lane": lane, "metrics": want})
    return {"lanes": request}, {"schemaVersion": V, "runId": run_id, "lanes": expected}


def summarize_vectors():
    quality, pass_rate = metric("quality", "score"), metric("pass_rate", "rate")
    latency = metric("latency", "duration", "lower_better", {"min": 0, "max": 60000}, "ms")

    summarize_vector(
        "mean-of-scores",
        "Every measured state with a score for the metric is measured (passed, failed, warn, inconclusive, scored); "
        "a line without one is counted in N but not measured; a line at another path is not counted. The entry with "
        "no rule has the verdict scored.",
        ["SUM-2", "SUM-3", "SUM-4", "SUM-5", "SUM-6"],
        [quality, latency],
        [L("k1", "passed", {"quality": 0.9}), L("k2", "failed", {"quality": 0.4}), L("k3", "warn", {"quality": 0.65}),
         L("k4", "inconclusive", {"quality": 0.5}), L("k5", "scored", {"quality": 0.7}), L("k6", "inconclusive"),
         L("k7", "passed", {"latency": 120}), L("k8", "passed", {"quality": 0.1}, path="q/sub")],
        [("quality", [
            # 0.9 + 0.4 + 0.65 + 0.5 + 0.7 = 3.15; squares 0.81 + 0.16 + 0.4225 + 0.25 + 0.49 = 2.1325; 3.15 / 5
            E("quality", "q", N=7, measured=[0.9, 0.4, 0.65, 0.5, 0.7], sum="3.15", sumSq="2.1325", value="0.63",
              rule="quality >= 0.6", verdict="passed"),
            E("latency", "q", N=7, measured=[120], sum="120", sumSq="14400", value="120")])])

    summarize_vector(
        "count-is-summed",
        "A metric of kind count has the sum as its value, not the mean; a skipped line is counted in N, not measured.",
        ["SUM-4", "SUM-5"],
        [metric("tool_calls", "count", "lower_better", {"min": 0, "max": 100})],
        [L("k1", "passed", {"tool_calls": 3}), L("k2", "failed", {"tool_calls": 0}), L("k3", "passed", {"tool_calls": 5}),
         L("k4", "warn", {"tool_calls": 2}), L("k5", "skipped", reason="Skipped by --max-cases 4.")],
        [("quality", [E("tool_calls", "q", N=5, measured=[3, 0, 5, 2], sum="10", sumSq="38", value="10",
                        rule="tool_calls <= 12", verdict="passed")])])

    summarize_vector(
        "rate-and-verdict",
        "For a rate or a verdict metric a line counts 1 when passed and 0 otherwise (warn and inconclusive are not a "
        "pass); a scored line has no verdict and is not measured; not_measured is counted, not measured; "
        "not_applicable is left out.",
        ["SUM-1", "SUM-4", "SUM-5", "RES-1"],
        [pass_rate, metric("ok", "verdict"), latency],
        [L("k1", "passed"), L("k2", "failed"), L("k3", "warn"), L("k4", "inconclusive"), L("k5", "passed"),
         L("k6", "scored", {"latency": 850}), L("k7", "not_measured"), L("k8", "not_applicable")],
        [("quality", [
            E("pass_rate", "q", N=7, measured=[1, 0, 0, 0, 1], sum="2", sumSq="2", value="0.4",
              rule="pass_rate >= 0.8", verdict="failed"),
            E("ok", "q", N=7, measured=[1, 0, 0, 0, 1], sum="2", sumSq="2", value="0.4")])])

    summarize_vector(
        "typed-absences",
        "not_measured, skipped and error lines are counted in N and are not measured: never a fail with 0.",
        ["SUM-4", "SUM-5", "RES-1", "RES-2"],
        [quality, pass_rate],
        [L("k1", "passed", {"quality": 0.8}), L("k2", "not_measured"), L("k3", "skipped", reason="Not run."),
         L("k4", "error", reason="The judge returned HTTP 429 three times."), L("k5", "failed", {"quality": 0.2})],
        [("quality", [
            E("quality", "q", N=5, measured=[0.8, 0.2], sum="1", sumSq="0.68", value="0.5"),
            E("pass_rate", "q", N=5, measured=[1, 0], sum="1", sumSq="1", value="0.5")])])

    summarize_vector(
        "not-applicable-left-out",
        "not_applicable lines are left out entirely. At path g every line is not_applicable: N and n are 0, the value "
        "is null and the verdict not_measured, whatever the producer's rule would say.",
        ["SUM-4", "SUM-5", "SUM-6"],
        [quality],
        [L("k1", "passed", {"quality": 1.0}), L("k2", "not_applicable"), L("k3", "not_applicable"),
         L("k4", "failed", {"quality": 0.5}), L("k1", "not_applicable", path="g"), L("k2", "not_applicable", path="g")],
        [("quality", [
            E("quality", "q", N=2, measured=[1.0, 0.5], sum="1.5", sumSq="1.25", value="0.75"),
            E("quality", "g", N=0, measured=[], sum="0", sumSq="0", value=None, rule="quality >= 0.5", verdict="failed")])])

    summarize_vector(
        "scored-lines",
        "A scored line is measured for a score metric when it has a score for it, and never for a rate.",
        ["SUM-4", "RES-1"],
        [quality, pass_rate, latency],
        [L("k1", "scored", {"quality": 0.3}), L("k2", "scored", {"quality": 0.6}), L("k3", "passed", {"quality": 0.9}),
         L("k4", "scored", {"latency": 300})],
        [("quality", [
            E("quality", "q", N=4, measured=[0.3, 0.6, 0.9], sum="1.8", sumSq="1.26", value="0.6"),
            E("pass_rate", "q", N=4, measured=[1], sum="1", sumSq="1", value="1")])])

    trials3 = {"n": 3, "passed": 2, "aggregation": "Mean", "agree": False}
    trials2 = {"n": 2, "passed": 1, "aggregation": "AnyPass", "agree": False}
    summarize_vector(
        "trial-lines",
        "Trial lines are not counted; each case's rollup line is (case k1: three trials and a rollup; k2: one line; "
        "k3: two trials, one not measured, and a rollup).",
        ["SUM-3", "RES-8"],
        [quality, pass_rate],
        [L("k1", "passed", {"quality": 0.9}, trial=0), L("k1", "failed", {"quality": 0.3}, trial=1),
         L("k1", "passed", {"quality": 0.9}, trial=2), L("k1", "passed", {"quality": 0.7}, trials=trials3),
         L("k2", "failed", {"quality": 0.5}),
         L("k3", "not_measured", trial=0), L("k3", "passed", {"quality": 0.8}, trial=1),
         L("k3", "passed", {"quality": 0.8}, trials=trials2)],
        [("quality", [
            E("quality", "q", N=3, measured=[0.7, 0.5, 0.8], sum="2", sumSq="1.38", value="0.666666666667"),
            E("pass_rate", "q", N=3, measured=[1, 0, 1], sum="2", sumSq="2", value="0.666666666667")])])

    summarize_vector(
        "median-even-count",
        "The median of an even count is the mean of the two middle values: (200 + 260) / 2, not the mean 370. sum "
        "and sumSq are still those of every measured value.",
        ["SUM-8", "SUM-5"],
        [latency],
        [L("k1", "passed", {"latency": 120}), L("k2", "passed", {"latency": 200}), L("k3", "failed", {"latency": 900}),
         L("k4", "passed", {"latency": 260}), L("k5", "error", reason="The agent timed out.")],
        [("performance", [E("latency", "q", N=5, measured=[120, 200, 900, 260], sum="1480", sumSq="932000", value="230",
                            aggregate={"method": "median"}, rule="median latency <= 300 ms", verdict="passed")])])

    summarize_vector(
        "min-max-and-a-median-count",
        "min and max over the measured values; the median of an odd count; and a count with an aggregate takes the "
        "aggregate's value (2), not the sum.",
        ["SUM-8", "SUM-5"],
        [quality, metric("cost", "cost", "lower_better", "unbounded", "USD"),
         metric("turns", "count", "lower_better", {"min": 1, "max": 50})],
        [L("k1", "passed", {"quality": 0.7, "cost": 0.012, "turns": 3}),
         L("k2", "failed", {"quality": 0.35, "cost": 0.03, "turns": 1}),
         L("k3", "passed", {"quality": 0.9, "cost": 0.004, "turns": 2}), L("k4", "skipped", reason="Not run.")],
        [("quality", [
            E("quality", "q", N=4, measured=[0.7, 0.35, 0.9], sum="1.95", sumSq="1.4225", value="0.35",
              aggregate={"method": "min"}, rule="min quality >= 0.3", verdict="passed"),
            E("cost", "q", N=4, measured=[0.012, 0.03, 0.004], sum="0.046", sumSq="0.00106", value="0.03",
              aggregate={"method": "max"}),
            E("turns", "q", N=4, measured=[3, 1, 2], sum="6", sumSq="14", value="2", aggregate={"method": "median"})])])

    summarize_vector(
        "aggregate-of-the-producer",
        "An aggregate method AEF does not define (pass@k, f1): the value is the producer's figure, as written, but N, "
        "n, notMeasured, sum and sumSq are computed; with n at 0 the value is null all the same.",
        ["SUM-8", "SUM-6"],
        [quality],
        [L("k1", "passed", {"quality": 0.9}), L("k2", "failed", {"quality": 0.2}), L("k3", "not_measured"),
         L("k1", "skipped", path="z", reason="Not run.")],
        [("quality", [
            E("quality", "q", N=3, measured=[0.9, 0.2], sum="1.1", sumSq="0.85", value="0.75",
              aggregate={"method": "pass@k", "k": 3}, given=0.75),
            E("quality", "z", N=1, measured=[], sum="0", sumSq="0", value=None, aggregate={"method": "f1"}, given=0.5)])])

    summarize_vector(
        "nothing-measured",
        "n = 0: sum and sumSq are 0, the value is null (with an aggregate too) and the verdict not_measured, whatever "
        "the producer's rule would say; a path with no line at all has N = 0.",
        ["SUM-5", "SUM-6", "SUM-8"],
        [quality, latency],
        [L("k1", "not_measured"), L("k2", "skipped", reason="Not run."), L("k3", "error", reason="Crashed.")],
        [("quality", [
            E("quality", "q", N=3, measured=[], sum="0", sumSq="0", value=None, rule="quality >= 0.5", verdict="failed"),
            E("latency", "q", N=3, measured=[], sum="0", sumSq="0", value=None, aggregate={"method": "median"}),
            E("quality", "q/absent", N=0, measured=[], sum="0", sumSq="0", value=None)])])

    summarize_vector(
        "two-lanes",
        "With two lanes a line belongs to the lane its lane names: a line without lane belongs to neither, and a line "
        "of a lane the summary does not have to no entry.",
        ["SUM-3", "SUM-9"],
        [pass_rate, metric("helpfulness", "score")],
        [L("k1", "passed", {"helpfulness": 0.8}, lane="quality"), L("k2", "failed", {"helpfulness": 0.4}, lane="quality"),
         L("k3", "passed", lane="security"), L("k4", "failed", lane="security"), L("k5", "failed", lane="security"),
         L("k6", "passed", {"helpfulness": 0.9}), L("k7", "passed", lane="memory")],
        [("quality", [
            E("pass_rate", "q", N=2, measured=[1, 0], sum="1", sumSq="1", value="0.5"),
            E("helpfulness", "q", N=2, measured=[0.8, 0.4], sum="1.2", sumSq="0.8", value="0.6")]),
         ("security", [
            E("pass_rate", "q", N=3, measured=[1, 0, 0], sum="1", sumSq="1", value="0.333333333333",
              rule="pass_rate == 1", verdict="failed")])])

    summarize_vector(
        "one-lane-takes-unlaned-lines",
        "With a single lane, a line without lane belongs to it; a line naming another lane does not.",
        ["SUM-3"],
        [quality],
        [L("k1", "passed", {"quality": 0.6}), L("k2", "failed", {"quality": 0.2}, lane="main"),
         L("k3", "passed", {"quality": 1.0}, lane="aux")],
        [("main", [E("quality", "q", N=2, measured=[0.6, 0.2], sum="0.8", sumSq="0.4", value="0.4")])])

    summarize_vector(
        "exact-sum-cancellation",
        "sum is computed exactly and rounded once: 1e20 + 1 - 1e20 is 1 (adding in order in binary64 gives 0); "
        "sumSq is 2e40 + 1 rounded once; the value is 1 / 3.",
        ["SUM-5"],
        [metric("delta", "score", "none", "unbounded")],
        [L("k1", "scored", {"delta": 1e20}), L("k2", "scored", {"delta": 1}), L("k3", "scored", {"delta": -1e20})],
        [("quality", [E("delta", "q", N=3, measured=[1e20, 1, -1e20], sum="1", sumSq="2e40",
                        value="0.333333333333")])])

    summarize_vector(
        "open-run-pending",
        "A pending line (in a run still running) is counted in N and not measured.",
        ["SUM-4", "RES-3"],
        [quality],
        [L("k1", "passed", {"quality": 0.6}), L("k2", "pending", reason="Still running."),
         L("k3", "failed", {"quality": 0.2})],
        [("quality", [E("quality", "q", N=3, measured=[0.6, 0.2], sum="0.8", sumSq="0.4", value="0.4")])],
        status="running")

    summarize_refused(
        "value-for-a-defined-method-refused",
        "The request gives a value (300) for a median, which AEF defines and the Producer computes (230 here): an "
        "input that contradicts what the Producer must compute is an input error (exit status 2), never written.",
        ["SUM-8"],
        [latency],
        [L("k1", "passed", {"latency": 120}), L("k2", "passed", {"latency": 200}), L("k3", "failed", {"latency": 900}),
         L("k4", "passed", {"latency": 260})],
        {"lanes": [{"lane": "performance", "metrics": [{"metric": "latency", "path": "q",
                                                         "aggregate": {"method": "median"}, "value": 300}]}]})

    # The other input errors §9.3 lists for summarize (R4-6): each a request the Producer refuses, never a summary.
    two = [L("k1", "passed", {"quality": 0.9}), L("k2", "failed", {"quality": 0.4})]
    summarize_refused(
        "undeclared-metric-refused",
        "The request asks for helpfulness, which metrics.json does not declare: a summary entry names a declared "
        "metric ([SUM-1]), so the request is an input error (exit status 2).",
        ["SUM-1"], [quality], two,
        {"lanes": [{"lane": "quality", "metrics": [{"metric": "helpfulness", "path": "q"}]}]})
    summarize_refused(
        "lane-named-twice-refused",
        "The request names the lane quality twice (once per entry): a summary names each lane once ([SUM-9]), so the "
        "request is an input error (exit status 2), not two lanes of one name.",
        ["SUM-9"], [quality, pass_rate], two,
        {"lanes": [{"lane": "quality", "metrics": [{"metric": "quality", "path": "q"}]},
                   {"lane": "quality", "metrics": [{"metric": "pass_rate", "path": "q"}]}]})
    summarize_refused(
        "entry-twice-refused",
        "The request asks twice for lane quality, metric quality and path q (the second time with a rule): one lane "
        "has one entry per metric and path ([SUM-9]), so the request is an input error (exit status 2).",
        ["SUM-9"], [quality], two,
        {"lanes": [{"lane": "quality", "metrics": [{"metric": "quality", "path": "q"},
                                                   {"metric": "quality", "path": "q", "rule": "quality >= 0.5",
                                                    "verdict": "passed"}]}]})
    summarize_refused(
        "producer-method-without-value-refused",
        "The request gives the aggregate method pass@k, which AEF does not define, and no value: the value of such an "
        "entry is the producer's figure ([SUM-8]), and with n = 2 it is a number, so there is nothing to write: an "
        "input error (exit status 2), never a null value.",
        ["SUM-8"], [quality], two,
        {"lanes": [{"lane": "quality", "metrics": [{"metric": "quality", "path": "q",
                                                    "aggregate": {"method": "pass@k", "k": 2}}]}]})
    summarize_refused(
        "results-do-not-read-refused",
        "results.ndjson does not read: its second line names the member state twice, which I-JSON forbids ([ENC-2]). "
        "A reader that keeps the last value would count a passed line; the Producer refuses the run instead (exit "
        "status 2).",
        ["ENC-2"], [quality], two,
        {"lanes": [{"lane": "quality", "metrics": [{"metric": "quality", "path": "q"}]}]},
        results=lambda b: b.replace(b'"state":"failed"', b'"state":"failed","state":"passed"', 1))


# ---------------------------------------------------------------------------- produce

MEASURED = {"passed", "failed", "warn", "inconclusive", "scored"}  # RES-1


def node(path, state, evaluator=None, quality=None, **facts):
    """A node of a scenario: the facts of one line (the evaluator code:<path> unless given; a quality score)."""
    out = {"path": path, "evaluator": {"id": evaluator or "code:" + path}, "state": state}
    if quality is not None:
        out["scores"] = [{"metric": "quality", "value": quality}]
    return out | facts


def child(path, state, weight=1, required=True, **facts):
    return node(path, state, component={"weight": weight, "required": required}, **facts)


def H(case, path, trial=None, parent=None, trials=None, counts=None):
    """A hand-written line of the expected results.ndjson, by what is derived: its parent ((path, trial) of the parent's
    line, or None), a rollup's (n, passed, agree), a composite's (measured, total, {state: count, the non-zero ones})."""
    return (case, path, trial), {"parent": parent, "trials": trials, "counts": counts}


def scenario_run(run_id, status="completed", **extra):
    run = {"schemaVersion": V, "runId": run_id, "status": status, "producer": {"name": "p", "version": "1"},
           "subject": {"ref": "agent:a/b", "kind": "agent", "version": "v1"}, "execution": {"targetMode": "live"},
           "startedAt": "2026-10-01T00:00:00Z", "contentCapture": "on"}
    if status != "running":
        run["endedAt"] = "2026-10-01T00:01:00Z"
    return run | extra


def tree_lines(run_id, case_id, n, trial, parent, out, rollup=None):
    """The generator's own reading of RES-4 to RES-8, independent of tools/aef_produce.py: one line per node, the
    node's first; parent is the (path, trial) of its parent's line; rollup the trial lines of the case, when the
    lines are its rollups."""
    line = {"schemaVersion": V, "resultId": result_id(run_id, case_id, n["path"], trial), "caseId": case_id,
            "path": n["path"]}
    if parent is not None:
        line["parentResultId"] = result_id(run_id, case_id, *parent)
        line["component"] = n["component"]
    if trial is not None:
        line["trial"] = trial
    line |= {"evaluator": n["evaluator"], "state": n["state"]}
    line |= {f: n[f] for f in ("scores", "severity", "reason", "lane") if f in n}
    if rollup is not None:
        states = [t["state"] for t in rollup["lines"] if t["path"] == n["path"]]
        line["trials"] = {"n": len(states), "passed": states.count("passed"), "aggregation": rollup["aggregation"],
                          "agree": len(set(states)) == 1} | ({"k": rollup["k"]} if "k" in rollup else {})
    out.append(line)
    kids = n.get("children", [])
    for c in kids:
        tree_lines(run_id, case_id, c, trial, (n["path"], trial), out, rollup)
    if "aggregation" in n:
        facts, states = n["aggregation"], [c["state"] for c in kids]
        line["aggregation"] = {"strategy": facts["strategy"]} | {f: facts[f] for f in ("threshold", "score") if f in facts} | {
            "rulePath": facts["rulePath"], "measured": sum(s in MEASURED for s in states), "total": len(states),
            "unmeasured": {s: states.count(s) for s in ("not_measured", "not_applicable", "skipped", "error")}}
        if "decisive" in facts:
            line["aggregation"]["decisive"] = [result_id(run_id, case_id, p, trial) for p in facts["decisive"]]


def case_lines(run_id, case):
    out = []
    if "trials" not in case:
        tree_lines(run_id, case["caseId"], case, None, None, out)
        return out
    for t, tree in enumerate(case["trials"]["trees"]):
        tree_lines(run_id, case["caseId"], tree, t, None, out)
    rollup = {"lines": list(out)} | {k: v for k, v in case["trials"].items() if k != "trees"}
    tree_lines(run_id, case["caseId"], case, None, None, out, rollup)
    return out


def checked_against_hand(name, lines, hand):
    """Stops unless the lines' derived fields are the hand-written ones, line for line."""
    by_id = {l["resultId"]: l for l in lines}
    got = {}
    for l in lines:
        parent = by_id[l["parentResultId"]] if "parentResultId" in l else None
        agg = l.get("aggregation")
        got[(l["caseId"], l["path"], l.get("trial"))] = {
            "parent": None if parent is None else (parent["path"], parent.get("trial")),
            "trials": None if "trials" not in l else (l["trials"]["n"], l["trials"]["passed"], l["trials"]["agree"]),
            "counts": None if agg is None else (agg["measured"], agg["total"],
                                                {s: c for s, c in agg["unmeasured"].items() if c})}
    assert len(got) == len(lines), f"{name}: two lines of one case, path and trial"
    want = dict(hand)
    assert set(got) == set(want), f"{name}: lines {sorted(map(str, set(got) ^ set(want)))} differ from the hand list"
    for key in want:
        assert got[key] == want[key], f"{name} {key}: derived {got[key]}, by hand {want[key]}"


def produce_vector(name, why, rules, cases, hand, lanes, metrics=None, run=None):
    """A scenario the Producer writes, the expected lines (in the generator's order: the judge compares them as a
    set) and the expected summary."""
    d = OUT / "produce" / name
    run_id = "produce-" + name
    run = run or scenario_run(run_id)
    metrics = metrics or [metric("quality", "score"), metric("pass_rate", "rate")]
    lines = [l for case in cases for l in case_lines(run_id, case)]
    checked_against_hand(name, lines, hand)
    request, summary = expected_summary(name, run_id, metrics, lines, lanes)
    write_json(d / "scenario.json", {"run": run, "metrics": {"schemaVersion": V, "metrics": metrics}, "cases": cases,
                                     "summary": request})
    write_ndjson(d / "expected-results.ndjson", lines)
    write_json(d / "expected.json", {"kind": "produce", "rules": rules, "scenario": "scenario.json",
                                     "results": "expected-results.ndjson", "summary": summary, "why": why})


def produce_refused(name, why, rules, cases, run=None):
    """A scenario that contradicts itself: the Producer refuses it (exit status 2) and writes nothing."""
    d = OUT / "produce" / name
    run_id = "produce-" + name
    write_json(d / "scenario.json", {
        "run": run or scenario_run(run_id), "metrics": {"schemaVersion": V, "metrics": [metric("pass_rate", "rate")]},
        "cases": cases, "summary": {"lanes": [{"lane": "quality", "metrics": [{"metric": "pass_rate", "path": "q"}]}]}})
    write_json(d / "expected.json", {"kind": "produce", "rules": rules, "scenario": "scenario.json", "refused": True,
                                     "why": why})


def produce_vectors():
    case = lambda case_id, n, **trials: {"caseId": case_id} | n | ({"trials": trials} if trials else {})
    produce_vector(
        "flat-run-typed-absences",
        "A flat run: one line per case, a root with its RES-4 id and the facts as given (a typed absence keeps its "
        "reason and carries no scores). The summary leaves the not_applicable case out and counts the other absences "
        "as not measured.",
        ["RES-1", "RES-2", "RES-4", "SUM-3", "SUM-4", "SUM-5"],
        [case("k1", node("q", "passed", quality=0.9)),
         case("k2", node("q", "failed", quality=0.3, severity="high")),
         case("k3", node("q", "not_measured", reason="The reference answer was missing.")),
         case("k4", node("q", "error", reason="The judge returned HTTP 429 three times.")),
         case("k5", node("q", "not_applicable", reason="The case asks for no answer."))],
        [H("k1", "q"), H("k2", "q"), H("k3", "q"), H("k4", "q"), H("k5", "q")],
        # 0.9 + 0.3 = 1.2; squares 0.81 + 0.09 = 0.9; 1.2 / 2 = 0.6. pass_rate: 1 + 0, over 2.
        [("quality", [E("quality", "q", N=4, measured=[0.9, 0.3], sum="1.2", sumSq="0.9", value="0.6"),
                      E("pass_rate", "q", N=4, measured=[1, 0], sum="1", sumSq="1", value="0.5",
                        rule="pass_rate >= 0.8", verdict="failed")])])

    answer = node("answer", "passed", "composite:answer", quality=0.85, aggregation={
        "strategy": "WeightedSum", "threshold": 0.7, "score": 0.85, "rulePath": "threshold",
        "decisive": ["answer/grounded"]}, children=[
        child("answer/grounded", "passed", weight=2, quality=0.85),
        child("answer/tone", "not_measured", required=False, reason="No tone rubric for this locale."),
        child("answer/citations", "not_applicable", required=False, reason="The answer cites nothing.")])
    scenario = node("scenario", "failed", "agenteval:scenario", quality=0.6, severity="medium", aggregation={
        "strategy": "Own", "threshold": 0.8, "score": 0.6, "rulePath": "threshold"}, children=[
        child("scenario/assertion-1", "passed", weight=0, required=False),
        child("scenario/assertion-2", "failed", weight=0, required=False, severity="low")])
    produce_vector(
        "composite-cases",
        "Two composite cases. answer (WeightedSum) has three children: total 3 and measured 1, the not_measured and "
        "the not_applicable child counted in unmeasured (a composite counts every child, unlike SUM-4's N), decisive "
        "the grounded child's id. scenario (Own) has two assertions of weight 0, each with its component all the "
        "same. Every child has its parent's id and its component; the summary reads each path's lines.",
        ["RES-4", "RES-5", "RES-6", "SUM-3", "SUM-4"],
        [case("k1", answer), case("k2", scenario)],
        [H("k1", "answer", counts=(1, 3, {"not_measured": 1, "not_applicable": 1})),
         H("k1", "answer/grounded", parent=("answer", None)), H("k1", "answer/tone", parent=("answer", None)),
         H("k1", "answer/citations", parent=("answer", None)),
         H("k2", "scenario", counts=(2, 2, {})), H("k2", "scenario/assertion-1", parent=("scenario", None)),
         H("k2", "scenario/assertion-2", parent=("scenario", None))],
        [("quality", [E("pass_rate", "answer", N=1, measured=[1], sum="1", sumSq="1", value="1"),
                      E("quality", "answer", N=1, measured=[0.85], sum="0.85", sumSq="0.7225", value="0.85"),
                      E("pass_rate", "answer/tone", N=1, measured=[], sum="0", sumSq="0", value=None),
                      E("pass_rate", "answer/citations", N=0, measured=[], sum="0", sumSq="0", value=None),
                      E("pass_rate", "scenario", N=1, measured=[0], sum="0", sumSq="0", value="0")])])

    produce_vector(
        "case-in-three-trials",
        "A case run three times, one trial failing: each trial's line carries its trial (0, 1, 2) and an id hashed "
        "with it; one rollup line at the case's path has n 3, passed 2 and agree false, and the given aggregation; no "
        "line is another's parent. The summary counts the rollup, never a trial.",
        ["RES-4", "RES-8", "SUM-3"],
        [case("k1", node("q", "passed", quality=0.8), aggregation="MajorityVote",
              trees=[node("q", "passed", quality=0.9), node("q", "failed", quality=0.2, severity="high"),
                     node("q", "passed", quality=0.8)]),
         case("k2", node("q", "passed", quality=0.7))],
        [H("k1", "q", 0), H("k1", "q", 1), H("k1", "q", 2), H("k1", "q", trials=(3, 2, False)), H("k2", "q")],
        # the rollup's 0.8 and k2's 0.7: 1.5; squares 0.64 + 0.49 = 1.13; 0.75.
        [("quality", [E("quality", "q", N=2, measured=[0.8, 0.7], sum="1.5", sumSq="1.13", value="0.75"),
                      E("pass_rate", "q", N=2, measured=[1, 1], sum="2", sumSq="2", value="1")])])

    produce_vector(
        "trials-agreement",
        "agree is true exactly when a path's trial lines are all in one state (RES-8): two warn trials agree though "
        "neither passed; a failed and an error trial do not, though neither passed; a single trial agrees with itself. "
        "passed counts the passed trials only. PassAtK's k is carried beside the aggregation.",
        ["RES-8", "SUM-3", "SUM-4"],
        [case("k1", node("q", "warn", severity="low"), aggregation="AllPass",
              trees=[node("q", "warn", severity="low"), node("q", "warn", severity="low")]),
         case("k2", node("q", "failed", severity="medium"), aggregation="MajorityVote",
              trees=[node("q", "failed", severity="medium"), node("q", "error", reason="The judge timed out.")]),
         case("k3", node("q", "passed"), aggregation="PassAtK", k=3,
              trees=[node("q", "failed", severity="low"), node("q", "passed"), node("q", "failed", severity="low")]),
         case("k4", node("q", "passed"), aggregation="AllPass", trees=[node("q", "passed")])],
        [H("k1", "q", 0), H("k1", "q", 1), H("k1", "q", trials=(2, 0, True)),
         H("k2", "q", 0), H("k2", "q", 1), H("k2", "q", trials=(2, 0, False)),
         H("k3", "q", 0), H("k3", "q", 1), H("k3", "q", 2), H("k3", "q", trials=(3, 1, False)),
         H("k4", "q", 0), H("k4", "q", trials=(1, 1, True))],
        [("quality", [E("pass_rate", "q", N=4, measured=[0, 0, 1, 1], sum="2", sumSq="2", value="0.5")])])

    minimum = lambda score, decisive: {"strategy": "Min", "threshold": 0.5, "score": score, "rulePath": "threshold",
                                       "decisive": decisive}
    produce_vector(
        "composite-in-two-trials",
        "A composite case in two trials; the second stopped before its tools step. Each trial's tree is its own: its "
        "lines carry its trial, its children's parent is that trial's root, its decisive ids are that trial's. The "
        "rollups form the case's tree: one at each path the trials have, the children's under the root's, with their "
        "component; n and passed are counted per path (plan/tools has one trial line: n 1, agree true). The summary "
        "counts the rollup at each path.",
        ["RES-4", "RES-5", "RES-6", "RES-8", "SUM-3"],
        [case("k1", node("plan", "failed", "composite:plan", severity="high", aggregation=minimum(0.1, ["plan/steps"]),
                         children=[child("plan/steps", "failed", severity="high"), child("plan/tools", "passed")]),
              aggregation="AllPass", trees=[
                  node("plan", "passed", "composite:plan", aggregation=minimum(0.9, []),
                       children=[child("plan/steps", "passed"), child("plan/tools", "passed")]),
                  node("plan", "failed", "composite:plan", severity="high", aggregation=minimum(0.1, ["plan/steps"]),
                       children=[child("plan/steps", "failed", severity="high")])])],
        [H("k1", "plan", 0, counts=(2, 2, {})), H("k1", "plan/steps", 0, parent=("plan", 0)),
         H("k1", "plan/tools", 0, parent=("plan", 0)),
         H("k1", "plan", 1, counts=(1, 1, {})), H("k1", "plan/steps", 1, parent=("plan", 1)),
         H("k1", "plan", trials=(2, 1, False), counts=(2, 2, {})),
         H("k1", "plan/steps", parent=("plan", None), trials=(2, 1, False)),
         H("k1", "plan/tools", parent=("plan", None), trials=(1, 1, True))],
        [("quality", [E("pass_rate", "plan", N=1, measured=[0], sum="0", sumSq="0", value="0"),
                      E("pass_rate", "plan/steps", N=1, measured=[0], sum="0", sumSq="0", value="0"),
                      E("pass_rate", "plan/tools", N=1, measured=[1], sum="1", sumSq="1", value="1")])])

    produce_vector(
        "aborted-run",
        "An aborted run is closed: run.json as given, with its abortReason; the case it was running is error and the "
        "one it never started skipped, each with its reason (RES-3).",
        ["RUN-5", "RES-2", "RES-3", "SUM-4"],
        [case("k1", node("q", "passed", quality=0.8)),
         case("k2", node("q", "error", reason="The run was aborted while the case ran.")),
         case("k3", node("q", "skipped", reason="Not run: the run was aborted."))],
        [H("k1", "q"), H("k2", "q"), H("k3", "q")],
        [("quality", [E("quality", "q", N=3, measured=[0.8], sum="0.8", sumSq="0.64", value="0.8"),
                      E("pass_rate", "q", N=3, measured=[1], sum="1", sumSq="1", value="1")])],
        run=scenario_run("produce-aborted-run", "aborted",
                         abortReason="The endpoint refused every request after k2 (HTTP 401)."))

    # n2-c: a run without contentCapture (a reader takes it as on, RUN-11); n2-d: times to the nanosecond (ENC-8).
    for name, run, why in (
            ("run-without-content-capture",
             {k: v for k, v in scenario_run("produce-run-without-content-capture").items() if k != "contentCapture"},
             "run.json has no contentCapture, which a reader takes as on (RUN-11): a Producer writes it as given, and "
             "writing contentCapture on instead means the same (spec 09 §9.3)."),
            ("times-to-the-nanosecond",
             scenario_run("produce-times-to-the-nanosecond", startedAt="2026-10-01T00:00:00.000000001Z",
                          endedAt="2026-10-01T00:01:00.123456789Z"),
             "run.json's times carry nine fraction digits, which ENC-8 allows: a Producer writes them as given, to the "
             "nanosecond, never rounded.")):
        produce_vector(
            name, why, ["RUN-11", "ENC-8"],
            [case("k1", node("q", "passed", quality=0.9))], [H("k1", "q")],
            [("quality", [E("quality", "q", N=1, measured=[0.9], sum="0.9", sumSq="0.81", value="0.9"),
                          E("pass_rate", "q", N=1, measured=[1], sum="1", sumSq="1", value="1")])],
            run=run)

    produce_vector(
        "aborted-before-any-result",
        "A run aborted before its first result: results.ndjson is written all the same, empty (RUN-2), and the "
        "summary's entry has N 0, a null value and the verdict not_measured.",
        ["RUN-2", "RUN-5", "SUM-5", "SUM-6"],
        [], [],
        [("quality", [E("pass_rate", "q", N=0, measured=[], sum="0", sumSq="0", value=None)])],
        run=scenario_run("produce-aborted-before-any-result", "aborted",
                         abortReason="The endpoint refused the first request (HTTP 401)."))

    # The scenarios that contradict themselves (spec 09 §9.3): refused, nothing written.
    root = lambda *children, **facts: node("answer", "passed", "composite:answer", aggregation={
        "strategy": "Min", "threshold": 0.5, "score": 1, "rulePath": "threshold"} | facts, children=list(children))
    produce_refused(
        "child-path-not-under-parent-refused",
        "The child tone is not one level under its parent answer (answer/tone would be): a child's path is its "
        "parent's, / and one more level, as RES-8's rollup tree reads it. The Producer refuses the scenario.",
        ["RES-5", "RES-8"], [case("k1", root(child("tone", "passed")))])
    produce_refused(
        "decisive-not-a-child-refused",
        "decisive names answer/style, which is no child of answer: a decisive id is a child's (RES-6). The Producer "
        "refuses the scenario.",
        ["RES-5", "RES-6"], [case("k1", root(child("answer/grounded", "passed"), decisive=["answer/style"]))])
    produce_refused(
        "trial-root-elsewhere-refused",
        "The second trial's tree is rooted at q2, not at the case's path q: a trial's lines are the case's at its own "
        "paths, and its rollup is at the same path (RES-8). The Producer refuses the scenario.",
        ["RES-8"], [case("k1", node("q", "passed"), aggregation="AnyPass",
                         trees=[node("q", "passed"), node("q2", "failed", severity="low")])])
    produce_refused(
        "trial-path-without-rollup-refused",
        "A trial's tree has the child q/x, but the case's tree (its rollups) has no node at q/x: a case run in trials "
        "has a rollup at each path its trials have (RES-8). The Producer refuses the scenario.",
        ["RES-8"], [case("k1", node("q", "passed"), aggregation="AllPass", trees=[
            node("q", "passed", aggregation={"strategy": "Min", "rulePath": "threshold"},
                 children=[child("q/x", "passed")])])])
    produce_refused(
        "pending-in-closed-run-refused",
        "The run is completed, and case k2 is still pending: a closed run has no pending line; it becomes skipped or "
        "error, with a reason (RES-3), which only the producer can say. The Producer refuses the scenario.",
        ["RES-3"], [case("k1", node("q", "passed")), case("k2", node("q", "pending", reason="Still running."))])
    produce_refused(
        "open-run-refused",
        "run.json says running: produce writes a closed run, with its summary (RUN-2, RUN-5). The Producer refuses "
        "the scenario.",
        ["RUN-2", "RUN-5"], [case("k1", node("q", "passed"))], run=scenario_run("produce-open-run-refused", "running"))
    produce_refused(
        "case-and-path-twice-refused",
        "Two trees of case k1 are rooted at q: their lines would have one resultId (RES-4). The Producer refuses the "
        "scenario.",
        ["RES-4"], [case("k1", node("q", "passed")), case("k1", node("q", "failed", severity="low"))])
    produce_refused(
        "child-without-component-refused",
        "The child answer/grounded has no component facts: every child carries its weight and whether it is required "
        "(RES-5), which only the producer knows. The Producer refuses the scenario.",
        ["RES-5"], [case("k1", root(node("answer/grounded", "passed")))])
    produce_refused(
        "children-without-aggregation-refused",
        "answer has a child and no aggregation facts: a node with children carries aggregation (RES-5), whose strategy "
        "and rulePath only the producer knows. The Producer refuses the scenario.",
        ["RES-5"], [case("k1", node("answer", "passed", children=[child("answer/grounded", "passed")]))])


# ---------------------------------------------------------------------------- seal-write

TRACE, SPAN ="4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"


def seal_write_vector(name, why, rules, build, order, sealed_by, sealed_at, predicate):
    """build(run_dir) writes the run; order is the hand-written byte order of its sealed paths; predicate the
    hand-written predicate without runHash, which is the SHA-256 of the manifest (SEAL-4)."""
    d = OUT / "seal-write" / name
    build(d / "run")
    files = [p.relative_to(d / "run").as_posix() for p in (d / "run").rglob("*") if p.is_file()]
    sealed = [p for p in files if not p.startswith("overlays/")]
    assert sorted(sealed) == sorted(order), f"{name}: the hand-written list is not the run's files: {sorted(sealed)}"
    assert sorted(sealed, key=lambda p: p.encode("utf-8")) == order, f"{name}: the hand-written order is not byte order"
    manifest = ""
    for rel in order:
        data = (d / "run" / rel).read_bytes()
        manifest += f"{hashlib.sha256(data).hexdigest()}  {len(data)}  {rel}\n"
    write_bytes(d / "expected-manifest.txt", manifest.encode("utf-8"))
    predicate = {"schemaVersion": V, "runId": predicate["runId"], "runHash": h(manifest)} | predicate
    write_json(d / "expected.json", {"kind": "seal-write", "rules": rules, "run": "run", "sealedBy": sealed_by,
                                     "sealedAt": sealed_at, "manifest": "expected-manifest.txt", "predicate": predicate,
                                     "why": why})


def minimal_run(d, run_id, ended="2026-10-01T00:01:00Z", status="completed"):
    run = {"schemaVersion": V, "runId": run_id, "status": status, "producer": {"name": "p", "version": "1"},
           "subject": {"ref": "agent:a/b", "kind": "agent", "version": "v1"}, "execution": {"targetMode": "live"},
           "startedAt": "2026-10-01T00:00:00Z"}
    if ended is not None:
        run["endedAt"] = ended
    write_json(d / "run.json", run)
    write_ndjson(d / "results.ndjson", [{"schemaVersion": V, "resultId": result_id(run_id, "k1", "q"), "caseId": "k1",
                                         "path": "q", "evaluator": {"id": "code:q"}, "state": "passed",
                                         "scores": [{"metric": "m", "value": 1}]}])
    write_json(d / "metrics.json", {"schemaVersion": V, "metrics": [metric("m", "score")]})
    if status != "running":
        write_json(d / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": [{"lane": "main", "metrics": [
            {"metric": "m", "path": "q", "N": 1, "n": 1, "notMeasured": 0, "sum": 1, "sumSq": 1, "value": 1,
             "verdict": "passed", "rule": "m >= 0.5"}]}]})
    return run


def full_run(d):
    """A run with everything a producer writes: a blob (non-ASCII text and a CRLF, sealed byte for byte), evidence,
    a gate decision, a trace, a producer's ext file, and run.json fields the predicate leaves out."""
    run_id = "seal-write-full"
    reasoning = "The reply escalated the refund — as policy §4 requires · « correct » ✓\r\nSecond line (CRLF kept).\n"
    data = reasoning.encode("utf-8")
    blob = hashlib.sha256(data).hexdigest()
    write_bytes(d / "blobs" / "sha256" / blob[:2] / blob, data)
    rubric = "sha256:" + h("rubric-v3")
    write_json(d / "run.json", {
        "schemaVersion": V, "runId": run_id, "status": "completed",
        "producer": {"name": "agenteval-cli", "version": "1.0.0", "runtime": {"name": "dotnet", "version": "10.0.4"}},
        "subject": {"ref": "agent:support/triage", "kind": "agent", "version": "git:3f2a1c", "environment": "dev"},
        "deployment": {"ref": "deployment:support/triage@dev", "environment": "dev", "endpoint": "http://localhost:5080"},
        "execution": {"targetMode": "live", "stimulus": "suite"},
        "suite": {"ref": "suite:support/triage", "version": "4", "digest": "sha256:" + h("triage@4"), "frozen": True,
                  "executionPolicy": {"trialsPerCase": 1}},
        "judges": [{"model": "gpt-5.1", "provider": "azure.ai.openai", "mode": "single", "rubricDigest": rubric,
                    "calibration": {"labelSet": "labels:support/triage-golden@2", "n": 120, "accuracy": 0.925,
                                    "kappa": 0.81, "dangerousErrors": 1, "measuredAt": "2026-09-20T10:00:00Z"}},
                   {"model": "judge-b", "mode": "single"}],
        "startedAt": "2026-10-02T14:02:11.120Z", "endedAt": "2026-10-02T14:06:23.004Z", "contentCapture": "on"})
    k1, k2 = result_id(run_id, "k1", "q"), result_id(run_id, "k2", "q")
    write_ndjson(d / "results.ndjson", [
        {"schemaVersion": V, "resultId": k1, "caseId": "k1", "path": "q", "evaluator": {"id": "llm:q", "version": "3"},
         "state": "passed", "scores": [{"metric": "quality", "value": 0.9}],
         "reasoning": {"blob": "sha256:" + blob, "bytes": len(data)}, "evidence": ["E-1"],
         "traceLink": {"traceId": TRACE, "spanId": SPAN}},
        {"schemaVersion": V, "resultId": k2, "caseId": "k2", "path": "q", "evaluator": {"id": "llm:q", "version": "3"},
         "state": "failed", "severity": "medium", "scores": [{"metric": "quality", "value": 0.5}]}])
    write_json(d / "metrics.json", {"schemaVersion": V, "metrics": [metric("quality", "score")]})
    # SUM-5 by hand: 0.9 + 0.5 = 1.4; squares 0.81 + 0.25 = 1.06; 1.4 / 2 = 0.7.
    write_json(d / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": [{"lane": "quality", "metrics": [
        {"metric": "quality", "path": "q", "N": 2, "n": 2, "notMeasured": 0, "sum": 1.4, "sumSq": 1.06, "value": 0.7,
         "verdict": "passed", "rule": "quality >= 0.6"}]}]})
    write_ndjson(d / "evidence.ndjson", [{"schemaVersion": V, "evidenceId": "E-1", "kind": "judge_reasoning",
                                          "digest": "sha256:" + blob, "link": {"blob": "sha256:" + blob},
                                          "description": "The judge's reasoning for k1"}])
    write_ndjson(d / "gates.ndjson", [{"schemaVersion": V, "gateId": "gate:support/pr", "decisionId": "D-1",
                                       "rule": {"strategy": "threshold"}, "inputs": {"results": [k1, k2]},
                                       "comparability": "not_applicable", "outcome": "ship", "exitCode": 0,
                                       "decidedAt": "2026-10-02T14:06:23.004Z"}])
    write_ndjson(d / "traces.otlp.jsonl", [{"resourceSpans": [{"resource": {"attributes": []}, "scopeSpans": [
        {"scope": {"name": "agenteval"}, "spans": [
            {"traceId": TRACE, "spanId": SPAN, "name": "invoke_agent triage", "kind": 3,
             "startTimeUnixNano": "1790949731120000000", "endTimeUnixNano": "1790949733400000000"}]}]}]}])
    write_json(d / "ext" / "agenteval.cli" / "settings.json", {"maxCases": 20, "note": "a producer's own file: sealed"})
    return blob


def seal_write_vectors():
    blob = hashlib.sha256(("The reply escalated the refund — as policy §4 requires · « correct » ✓\r\n"
                           "Second line (CRLF kept).\n").encode("utf-8")).hexdigest()
    seal_write_vector(
        "full-run",
        "Every file but seal.json is sealed, the blob byte for byte; the predicate holds only what SEAL-5 names of "
        "run.json (no runtime, no subject kind, no deployment endpoint, no executionPolicy, no judge provider, mode "
        "or calibration), the judges in order, and closedAt as run.json's endedAt.",
        ["SEAL-1", "SEAL-2", "SEAL-3", "SEAL-4", "SEAL-5", "EVD-3"],
        full_run,
        [f"blobs/sha256/{blob[:2]}/{blob}", "evidence.ndjson", "ext/agenteval.cli/settings.json", "gates.ndjson",
         "metrics.json", "results.ndjson", "run.json", "summary.json", "traces.otlp.jsonl"],
        "producer", "2026-10-02T14:06:24Z",
        {"runId": "seal-write-full", "producer": {"name": "agenteval-cli", "version": "1.0.0"},
         "subject": {"ref": "agent:support/triage", "version": "git:3f2a1c"},
         "deployment": {"ref": "deployment:support/triage@dev"},
         "suite": {"ref": "suite:support/triage", "version": "4", "digest": "sha256:" + h("triage@4")},
         "judges": [{"model": "gpt-5.1", "rubricDigest": "sha256:" + h("rubric-v3")}, {"model": "judge-b"}],
         "closedAt": "2026-10-02T14:06:23.004Z", "sealedAt": "2026-10-02T14:06:24Z", "sealedBy": "producer"})

    def byte_order(d):
        minimal_run(d, "seal-write-byte-order")
        for rel, text in (("ext/Z", "upper case before lower case\n"), ("ext/a-b", ""), ("ext/a.b", "dot\n"),
                          ("ext/a/b", "slash\n"), ("ext/a_b", "underscore\n")):
            write_bytes(d / rel, text.encode("utf-8"))

    seal_write_vector(
        "byte-order-of-paths",
        "Subjects and manifest lines are ordered by the UTF-8 bytes of the path: Z (0x5A) before a, and - (0x2D) "
        "before . (0x2E) before / (0x2F) before _ (0x5F), so ext/a/b comes after ext/a.b, where a sort by path "
        "segments would put it first. An empty file is sealed too.",
        ["SEAL-3", "SEAL-4", "SEAL-5"],
        byte_order,
        ["ext/Z", "ext/a-b", "ext/a.b", "ext/a/b", "ext/a_b", "metrics.json", "results.ndjson", "run.json",
         "summary.json"],
        "ingest", "2026-10-08T09:00:00Z",
        {"runId": "seal-write-byte-order", "producer": {"name": "p", "version": "1"},
         "subject": {"ref": "agent:a/b", "version": "v1"}, "deployment": None, "suite": None, "judges": [],
         "closedAt": "2026-10-01T00:01:00Z", "sealedAt": "2026-10-08T09:00:00Z", "sealedBy": "ingest"})

    def aborted(d):
        run_id = "seal-write-aborted"
        write_json(d / "run.json", {"schemaVersion": V, "runId": run_id, "status": "aborted",
                                    "abortReason": "The endpoint refused every request (HTTP 401).",
                                    "producer": {"name": "agenteval-cli", "version": "1.0.0"},
                                    "subject": {"ref": "agent:people/hr-bot", "kind": "agent"},
                                    "execution": {"targetMode": "live"},
                                    "startedAt": "2026-10-03T09:00:00Z", "endedAt": "2026-10-03T09:00:02Z"})
        write_bytes(d / "results.ndjson", b"")
        write_json(d / "metrics.json", {"schemaVersion": V, "metrics": []})
        write_json(d / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": []})

    seal_write_vector(
        "aborted-without-optional-fields",
        "An aborted run is closed and sealed. With no deployment, suite or judges in run.json the predicate has "
        "deployment null, suite null and judges []; a subject without version has none in the predicate; the empty "
        "results.ndjson is sealed with size 0.",
        ["SEAL-1", "SEAL-2", "SEAL-5"],
        aborted,
        ["metrics.json", "results.ndjson", "run.json", "summary.json"],
        "ingest", "2026-10-03T09:05:00Z",
        {"runId": "seal-write-aborted", "producer": {"name": "agenteval-cli", "version": "1.0.0"},
         "subject": {"ref": "agent:people/hr-bot"}, "deployment": None, "suite": None, "judges": [],
         "closedAt": "2026-10-03T09:00:02Z", "sealedAt": "2026-10-03T09:05:00Z", "sealedBy": "ingest"})

    def with_overlays(d):
        run_id = "seal-write-overlays"
        minimal_run(d, run_id)
        manifest = ""
        for p in sorted((q for q in d.rglob("*") if q.is_file()), key=lambda q: q.relative_to(d).as_posix().encode()):
            data = p.read_bytes()
            manifest += f"{hashlib.sha256(data).hexdigest()}  {len(data)}  {p.relative_to(d).as_posix()}\n"
        overlay_batches(d, run_id, [[{"schemaVersion": V, "eventId": "ov_0001", "kind": "annotate",
                                      "target": {"run": run_id}, "reason": "Looked at before it was sealed.",
                                      "by": {"identity": "git:alice@example.com", "assurance": "self-attested"},
                                      "at": "2026-10-01T00:05:00Z"}]], h(manifest))

    seal_write_vector(
        "overlays-not-sealed",
        "Everything under overlays/ is left out of the seal (an events file and its batch seal, appended after "
        "close): the subjects are the run's own files only.",
        ["SEAL-1"],
        with_overlays,
        ["metrics.json", "results.ndjson", "run.json", "summary.json"],
        "ingest", "2026-10-08T09:00:00Z",
        {"runId": "seal-write-overlays", "producer": {"name": "p", "version": "1"},
         "subject": {"ref": "agent:a/b", "version": "v1"}, "deployment": None, "suite": None, "judges": [],
         "closedAt": "2026-10-01T00:01:00Z", "sealedAt": "2026-10-08T09:00:00Z", "sealedBy": "ingest"})

    seal_write_vector(
        "times-at-full-precision",
        "closedAt is run.json's endedAt and sealedAt the time given, both at the nine fraction digits written: a "
        "writer that rounds them (to 100 ns, say) writes another time.",
        ["SEAL-5", "ENC-8"],
        lambda d: minimal_run(d, "seal-write-times", ended="2026-10-01T00:01:00.123456789Z"),
        ["metrics.json", "results.ndjson", "run.json", "summary.json"],
        "producer", "2026-10-08T09:00:00.000000001Z",
        {"runId": "seal-write-times", "producer": {"name": "p", "version": "1"},
         "subject": {"ref": "agent:a/b", "version": "v1"}, "deployment": None, "suite": None, "judges": [],
         "closedAt": "2026-10-01T00:01:00.123456789Z", "sealedAt": "2026-10-08T09:00:00.000000001Z",
         "sealedBy": "producer"})

    d = OUT / "seal-write" / "open-run-refused"
    minimal_run(d / "run", "seal-write-open", ended=None, status="running")
    write_json(d / "expected.json", {"kind": "seal-write", "rules": ["SEAL-1"], "run": "run", "sealedBy": "producer",
                                     "sealedAt": "2026-10-08T09:00:00Z", "refused": True,
                                     "why": "Only a closed run is sealed: run.json says running. The sealer refuses "
                                            "(exit status 2) and writes nothing."})

    d = OUT / "seal-write" / "sealed-before-close-refused"
    minimal_run(d / "run", "seal-write-early", ended="2026-10-01T00:01:00Z")
    write_json(d / "expected.json", {"kind": "seal-write", "rules": ["SEAL-1", "SEAL-6", "ENC-8"], "run": "run",
                                     "sealedBy": "producer", "sealedAt": "2026-10-01T00:00:59.999999999Z",
                                     "refused": True,
                                     "why": "A seal dated before its run closed is a predicate problem, and a sealer "
                                            "never writes one: --sealed-at is 1 ns before run.json's endedAt (times "
                                            "compare at the precision written, never rounded). The sealer refuses "
                                            "(exit status 2) and writes nothing."})

    # SEAL-1: a host that seals a run on taking custody of it (ingest) seals only a run whose paths keep RUN-3 and
    # whose files the reader schemas accept, as received.
    d = OUT / "seal-write" / "ingest-schema-invalid-refused"
    run = minimal_run(d / "run", "seal-write-ingest-invalid")
    run["deployment"] = {"ref": "deployment:a/b@prod", "endpoint": "https://agent.example.com/v1?api-key=sk-test-0000"}
    write_json(d / "run" / "run.json", run)
    write_json(d / "expected.json", {"kind": "seal-write", "rules": ["SEAL-1", "RUN-10", "ENC-16"], "run": "run",
                                     "sealedBy": "ingest", "sealedAt": "2026-10-08T09:00:00Z", "refused": True,
                                     "why": "Sealed on custody (ingest), a run is sealed only when its files are valid "
                                            "against the reader schemas; the reader run schema refuses this "
                                            "run.json (a deployment endpoint with a query string, RUN-10). The "
                                            "sealer refuses (exit status 2) and writes nothing."})

    d = OUT / "seal-write" / "ingest-path-problem-refused"
    minimal_run(d / "run", "seal-write-ingest-path")
    write_bytes(d / "run" / "ext" / "notes v2.txt", b"A file name with a space breaks RUN-3.\n")
    write_json(d / "expected.json", {"kind": "seal-write", "rules": ["SEAL-1", "RUN-3"], "run": "run",
                                     "sealedBy": "ingest", "sealedAt": "2026-10-08T09:00:00Z", "refused": True,
                                     "why": "Sealed on custody (ingest), a run is sealed only when its paths keep "
                                            "RUN-3; ext/notes v2.txt holds a space. The sealer refuses (exit status "
                                            "2) and writes nothing."})


# ---------------------------------------------------------------------------- sign

def sign_vector(name, why, file_name, data, payload_type, key, trusted, rules):
    """trusted: the keys of the trust policy, in order; the key signing is one of them."""
    d = OUT / "sign" / name
    write_bytes(d / file_name, data)
    write_json(d / "policy.json", {"keys": [{"identity": WHO[k], "publicKey": PEM[k]} for k in trusted]})
    # The algorithm a Sealer needs for this vector: a signer uses one of SIG-2's two, so a Sealer passes the sign
    # vectors of the algorithms it claims (spec 09 §9.1).
    algorithm = "ed25519" if key.startswith("ed25519") else "ecdsa-p256"
    expected = {"kind": "sign", "rules": rules, "algorithm": algorithm, "file": file_name, "payloadType": payload_type,
                "key": f"{KEYS}/{key}.key.pem", "policy": "policy.json", "keyid": ID[key], "identity": WHO[key],
                "why": why}
    if key.startswith("ed25519"):  # RFC 8032 signatures are deterministic: these bytes and no others
        expected["sig"] = C.b64encode(ED.sign(C.pae(payload_type, data)))
    write_json(d / "expected.json", expected)


def sign_vectors():
    run = CONF / "valid" / "completed-eval" / "run"
    seal_bytes, batch = (run / "seal.json").read_bytes(), (run / "overlays" / "seal-0001.json").read_bytes()
    checkpoint = (CONF / "checkpoints" / "valid-decided" / "document.json").read_bytes()
    noted = json.loads(checkpoint.decode("utf-8"))
    noted["ext"] = dict(noted.get("ext") or {}, **{"example.note": "Größe — geprüft ✓ 検証済み"})
    non_ascii = (json.dumps(noted, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    assert len(non_ascii) != len(non_ascii.decode("utf-8")), "the checkpoint must hold multi-byte characters"
    S = ["SIG-1", "SIG-2", "SIG-3"]
    sign_vector("ecdsa-p256-seal", "A run's seal.json signed with a P-256 key: the envelope verifies for its identity "
                "(an ECDSA signature is not compared: it need not be deterministic).",
                "seal.json", seal_bytes, INTOTO, "ecdsa-a", ["ecdsa-b", "ecdsa-a"], S)
    sign_vector("ed25519-seal", "A run's seal.json signed with an Ed25519 key: the envelope verifies, and the signature "
                "is the one RFC 8032 gives.", "seal.json", seal_bytes, INTOTO, "ed25519-a", ["ed25519-a"], S)
    sign_vector("ecdsa-p256-checkpoint", "A checkpoint manifest, with the checkpoint payload type, signed with a P-256 key.",
                "checkpoint.json", checkpoint, CHECKPOINT, "ecdsa-b", ["ecdsa-a", "ecdsa-b"], S + ["CKP-5"])
    sign_vector("ed25519-checkpoint-non-ascii", "A checkpoint manifest holding multi-byte UTF-8 characters: PAE counts "
                "the payload's bytes, not its characters.", "checkpoint.json", non_ascii, CHECKPOINT, "ed25519-a",
                ["ecdsa-a", "ed25519-a"], S + ["CKP-5"])
    sign_vector("ed25519-overlay-batch", "An overlay batch seal, signed with an Ed25519 key.", "seal-0001.json", batch,
                INTOTO, "ed25519-a", ["ed25519-a"], S)


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    summarize_vectors()
    produce_vectors()
    seal_write_vectors()
    sign_vectors()
    counts = {k: len([p for p in (OUT / k).iterdir() if p.is_dir()]) for k in ("summarize", "produce", "seal-write", "sign")}
    print("write vectors:", ", ".join(f"{k} {n}" for k, n in counts.items()))


if __name__ == "__main__":
    main()
