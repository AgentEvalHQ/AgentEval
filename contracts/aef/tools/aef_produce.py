#!/usr/bin/env python3
"""The AEF 1.0 reference writer: the four operations of the write-side conformance vectors (spec 09 §9.3), in which
an implementation computes or writes something and the conformance runner judges it. Written from the specification
(1/spec/) alone; standard library, plus aef_crypto.py for signing and aef_schema.py (the JSON Schema validator) for
the ingest check of [SEAL-1].

It is a library (one function per operation, each returning the JSON value it prints) and a command line that follows
the same contract as aef_verify.py: input paths as arguments, one JSON value on standard output (UTF-8, sorted keys,
no ASCII escaping), exit status 0 when the operation ran, and 2 with a message on standard error for a usage or input
error.

Commands (the JSON each prints):

  summarize DIR REQUEST
      [SUM-2]-[SUM-9]: the summary.json of the run in DIR, computed from its run.json (the runId), metrics.json (the
      metric kinds) and results.ndjson, for the entries the REQUEST file lists:
        {"lanes": [{"lane": name, "metrics": [{"metric", "path", "aggregate"?, "rule"?, "verdict"?, "value"?}]}]}
      `verdict` is the producer's verdict under `rule` when something was measured; `value` is the producer's figure
      for an `aggregate` method AEF does not define. Prints the summary document: {"schemaVersion", "runId",
      "lanes": [{"lane", "metrics": [{"metric", "path", "N", "n", "notMeasured", "sum", "sumSq", "value", "verdict",
      "rule"?, "aggregate"?}]}]}, lanes and entries in request order. `sum` and `sumSq` are computed exactly and
      rounded once to binary64 ([SUM-5]); so are `value` (the exact sum over n) and the median of an even count.
      Input errors: an undeclared metric; a lane twice, or a lane, metric and path twice; a method AEF does not
      define without `value`; a `value` for an entry AEF computes (no aggregate, or median, min or max); results
      or metrics that do not read.
  produce SCENARIO OUT
      [RES-4]-[RES-8], [SUM-2]-[SUM-9]: writes the closed, unsealed run the SCENARIO file describes (spec 09
      §9.2.1: the run.json and metrics.json to write, each case's result tree as facts, and a summarize request)
      in the folder OUT, which must not exist yet or be empty: run.json and metrics.json as given, results.ndjson
      (one line per node, with what the producer derives: result ids, parents, trial numbers, the rollups' trials,
      the aggregation counts and decisive ids) and summary.json (as summarize computes it from those lines).
      Prints {"results": the number of lines}. Input errors, with nothing written: a scenario not of that shape; a
      run that is not closed; a pending node; a child whose path is not its parent's and one more level; a decisive
      path that is no child's; a node with children and no aggregation, or a child without component; a trial tree
      whose paths are not the case's; two lines with one case, path and trial; and the input errors of summarize.
  seal-write DIR --sealed-by producer|ingest --sealed-at TIME
      [SEAL-1]-[SEAL-5]: writes DIR/seal.json for the closed run in DIR and changes nothing else. Prints
      {"runHash": hex}. Input errors, with nothing written: an open run; a TIME before run.json's endedAt; a run that
      already has a seal.json; and with ingest, a run whose paths break [RUN-3] or whose files the reader schemas
      refuse ([SEAL-1], [ENC-16]; through aef_schema.py).
  sign FILE KEY --payload-type TYPE
      [SIG-1]-[SIG-3]: signs FILE's exact bytes with KEY, an unencrypted PKCS#8 PEM private key (RFC 5208 / RFC 5958:
      P-256 with an RFC 5915 ECPrivateKey inside, or Ed25519 as RFC 8410 gives it); another key, curve or format is
      an input error. Prints the DSSE envelope:
      {"payloadType": TYPE, "payload": base64, "signatures": [{"keyid": "sha256:...", "sig": base64}]}, base64 in
      the standard alphabet with padding. ECDSA uses the RFC 6979 nonce (aef_crypto.py), so the reference's output is
      reproducible; a conforming signer need not be.

Not for production key handling: see aef_crypto.py.
"""
from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import json
import math
import os
import re
import sys
from fractions import Fraction
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_crypto  # noqa: E402
import aef_schema  # noqa: E402  (the JSON Schema validator, standard library only: for the ingest check of SEAL-1)

AEF_VERSION = "1.0"
OPERATIONS = ("summarize", "produce", "seal-write", "sign")

# ---------------------------------------------------------------------------- mutations (for --self-check)
# Each name breaks one writer the way a plausible implementation would, so the conformance runner can show that the
# write-side vectors notice (aef_conformance.py --self-check).
MUTATIONS: set[str] = set()
KNOWN_MUTATIONS = {
    "summary-absences": "the summary leaves typed absences out of N, as it leaves not_applicable lines out",
    "summary-binary64": "sum and sumSq are added up in binary64 in file order, not exactly",
    "summary-trials": "trial lines are counted in the summary beside their rollup",
    "summary-value-ignored": "a value the request gives for median, min or max is ignored instead of refused",
    "produce-ids": "result ids are hashed without the trial, so a trial's lines take its rollup's ids",
    "produce-parent": "a parent is looked up by case and parent path, finding the first line written there (in a case "
                      "run in trials, trial 0's), not the line of the node's own tree",
    "produce-trial-children": "only a trial tree's root carries the trial; the lines under it carry none",
    "produce-rollup-n": "a rollup's n is the case's number of trials at every path, though a trial has no line there",
    "produce-rollup-passed": "a rollup's passed counts every trial not failed (warn, inconclusive and absences pass)",
    "produce-agree": "a rollup's agree is computed from passed (0 or n), not from the trials' states",
    "produce-aggregation-counts": "a not_applicable child is left out of total and unmeasured, as SUM-4 leaves it out",
    "produce-component": "a child's component is left out when its weight is 0 (an Own composite's children)",
    "produce-unchecked": "a scenario that contradicts itself is written instead of refused",
    "seal-omits-file": "the seal leaves the last sealed file out of its subjects and its manifest",
    "seal-ingest-unchecked": "a host seals on custody (ingest) without checking paths and reader schemas",
    "seal-path-order": "the subjects are ordered segment by segment (as a path sort does), not by UTF-8 bytes",
    "sign-other-key": "the envelope is signed with another key, under the given key's id",
    "sign-unpadded": "base64 is written without its padding",
}


def set_mutations(names):
    """Breaks the named writers (an empty set restores them)."""
    unknown = set(names) - set(KNOWN_MUTATIONS)
    if unknown:
        raise ValueError(f"unknown mutation(s): {', '.join(sorted(unknown))}")
    MUTATIONS.clear()
    MUTATIONS.update(names)


class InputError(Exception):
    """A usage or input error: the operation cannot be performed (exit status 2)."""


# ---------------------------------------------------------------------------- reading (spec 02)

def _ijson(text, where):
    """One I-JSON value ([ENC-2], [ENC-3]): no member named twice, finite numbers, no unpaired surrogate."""
    def members(pairs):
        obj = {}
        for key, value in pairs:
            if key in obj:
                raise InputError(f"{where}: the member {key!r} appears twice ([ENC-2])")
            obj[key] = value
        return obj

    def constant(name):
        raise InputError(f"{where}: {name} is not a JSON number ([ENC-3])")

    def number(literal):
        value = float(literal)
        if not math.isfinite(value):
            raise InputError(f"{where}: {literal} overflows binary64 ([ENC-3])")
        return value

    try:
        value = json.loads(text, object_pairs_hook=members, parse_constant=constant, parse_float=number)
    except ValueError as error:
        raise InputError(f"{where}: not a JSON text: {error}") from None
    if re.search("[\ud800-\udfff]", json.dumps(value, ensure_ascii=False)):
        raise InputError(f"{where}: an unpaired surrogate ([ENC-2])")
    return value


def _decode(data, where):
    if data.startswith(b"\xef\xbb\xbf"):
        raise InputError(f"{where}: a byte-order mark ([ENC-1])")
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise InputError(f"{where}: not UTF-8 ([ENC-1]): {error}") from None


def read_json(path, where=None):
    """A JSON document whose top-level value is an object ([ENC-1])."""
    where = where or Path(path).name
    try:
        data = Path(path).read_bytes()
    except OSError as error:
        raise InputError(f"{where}: {error}") from None
    value = _ijson(_decode(data, where), where)
    if not isinstance(value, dict):
        raise InputError(f"{where}: the top-level value is not an object ([ENC-1])")
    return value


def read_ndjson(path, where=None):
    """The objects of an NDJSON file ([ENC-5]-[ENC-7]): LF only, every line ended, no blank line."""
    where = where or Path(path).name
    try:
        data = Path(path).read_bytes()
    except OSError as error:
        raise InputError(f"{where}: {error}") from None
    text = _decode(data, where)
    if not text:
        return []
    if "\r" in text or not text.endswith("\n") or text.startswith("\n") or "\n\n" in text:
        raise InputError(f"{where}: not NDJSON (a CR, a blank line or an unended last line, [ENC-5])")
    lines = []
    for number, line in enumerate(text[:-1].split("\n"), start=1):
        value = _ijson(line, f"{where}:{number}")
        if not isinstance(value, dict):
            raise InputError(f"{where}:{number}: a line is a JSON object ([ENC-5])")
        lines.append(value)
    return lines


# ---------------------------------------------------------------------------- summarize (spec 03 §3.6)

MEASURED_STATES = {"passed", "failed", "warn", "inconclusive", "scored"}  # RES-1: measured
NOT_MEASURED_STATES = {"not_measured", "skipped", "error", "pending"}  # SUM-4: counted in N, not measured
LEFT_OUT_STATES = {"not_applicable"}  # SUM-4: left out entirely
METRIC_KINDS = {"score", "rate", "count", "duration", "cost", "verdict"}  # SUM-1
DEFINED_AGGREGATES = {"median", "min", "max"}  # SUM-8: AEF defines these; any other method is the producer's
VERDICTS = {"passed", "failed", "warn", "inconclusive", "not_measured", "scored"}  # SUM-6


def _belongs(line, lane, lane_names):
    """SUM-3: a line belongs to the lane its `lane` names; when the summary has a single lane, a line without `lane`
    belongs to it."""
    if "lane" in line:
        return line["lane"] == lane
    return len(lane_names) == 1


def _measurement(line, metric, kind):
    """SUM-4 for a line that is not left out: (counted in N, its value or None when not measured)."""
    state = line.get("state")
    if state in NOT_MEASURED_STATES:
        return ("summary-absences" not in MUTATIONS), None
    if state not in MEASURED_STATES:
        raise InputError(f"result {line.get('resultId')!r}: the state {state!r} is not one AEF 1.0 defines ([RES-1])")
    if kind in ("rate", "verdict"):
        if state == "scored":
            return True, None  # a scored line has no verdict: not measured
        return True, 1 if state == "passed" else 0  # warn and inconclusive are not a pass
    scores = [s for s in line.get("scores") or [] if isinstance(s, dict) and s.get("metric") == metric]
    if len(scores) != 1:
        return True, None  # no score for the metric; or two, which §3.9 reports, and which count as not measured
    value = scores[0].get("value")
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise InputError(f"result {line.get('resultId')!r}: the {metric} score is not a number")
    return True, value


def _number(exact):
    """An exact value rounded once to binary64 (Python's int / int division and float(Fraction) round correctly);
    an integer within 2^53 is written in plain digits ([ENC-4])."""
    if exact.denominator == 1 and abs(exact.numerator) <= 2 ** 53:
        return exact.numerator
    return float(exact)


def _entry(want, lane, lane_names, kinds, lines):
    for field in ("metric", "path"):
        if not isinstance(want.get(field), str):
            raise InputError(f"lane {lane!r}: an entry has no {field}")
    metric, path = want["metric"], want["path"]
    if metric not in kinds:
        raise InputError(f"metric {metric!r} is not declared in metrics.json ([SUM-1])")
    kind = kinds[metric]
    N, values = 0, []
    for line in lines:
        if "trial" in line and "summary-trials" not in MUTATIONS:
            continue  # SUM-3: trial lines are not counted; their rollup line is
        if line.get("path") != path or not _belongs(line, lane, lane_names):
            continue
        if line.get("state") in LEFT_OUT_STATES:
            continue
        counted, value = _measurement(line, metric, kind)
        N += counted
        if value is not None:
            values.append(value)
    n = len(values)
    if "summary-binary64" in MUTATIONS:
        total = total_sq = 0.0
        for v in values:
            total += v
            total_sq += v * v
        exact_sum, exact_sq = Fraction(total), Fraction(total_sq)
    else:
        exact_sum = sum((Fraction(v) for v in values), Fraction(0))
        exact_sq = sum((Fraction(v) ** 2 for v in values), Fraction(0))

    aggregate = want.get("aggregate")
    if aggregate is not None and not (isinstance(aggregate, dict) and isinstance(aggregate.get("method"), str)):
        raise InputError(f"{lane}/{metric}/{path}: aggregate is an object with a method ([SUM-8])")
    defined = aggregate is None or aggregate["method"] in DEFINED_AGGREGATES
    if defined and "value" in want and "summary-value-ignored" not in MUTATIONS:
        # SUM-5, SUM-8: AEF computes the mean, median, min and max; a given value would contradict what is computed
        raise InputError(f"{lane}/{metric}/{path}: a value is given only for an aggregate method AEF does not define")
    if n == 0:
        value = None  # SUM-5, SUM-8: null when nothing was measured
    elif aggregate is None:
        # SUM-5: the sum for a count; otherwise the binary64 sum divided by n, in one binary64 division (2.1 / 5 is
        # 0.42000000000000004, not the exact mean 0.42)
        value = exact_sum if kind == "count" else Fraction(float(_number(exact_sum)) / n)
    elif aggregate["method"] in DEFINED_AGGREGATES:  # SUM-8, over the measured values
        ordered = sorted(Fraction(v) for v in values)
        if aggregate["method"] == "min":
            value = ordered[0]
        elif aggregate["method"] == "max":
            value = ordered[-1]
        elif n % 2:
            value = ordered[n // 2]
        else:
            value = (ordered[n // 2 - 1] + ordered[n // 2]) / 2  # the mean of the two middle values
    else:  # SUM-8: another method is the producer's figure, written as given
        given = want.get("value")
        if isinstance(given, bool) or not isinstance(given, (int, float)):
            raise InputError(f"{lane}/{metric}/{path}: the method {aggregate['method']!r} is not one AEF defines; "
                             "the request gives the producer's value")
        value = Fraction(given)

    verdict = want.get("verdict")
    if verdict is not None and verdict not in VERDICTS:
        raise InputError(f"{lane}/{metric}/{path}: {verdict!r} is not a summary verdict ([SUM-6])")
    entry = {"metric": metric, "path": path, "N": N, "n": n, "notMeasured": N - n,
             "sum": _number(exact_sum), "sumSq": _number(exact_sq), "value": None if value is None else _number(value),
             # SUM-6: not_measured when n is 0; the producer's verdict under its rule; scored when it applied none
             "verdict": "not_measured" if n == 0 else verdict if verdict is not None else "scored"}
    if "rule" in want:
        entry["rule"] = want["rule"]
    if aggregate is not None:
        entry["aggregate"] = aggregate
    return entry


def summarize(run_dir, request):
    """The summary.json document of the run in run_dir for the requested entries ([SUM-2]-[SUM-9])."""
    run_dir = Path(run_dir)
    run = read_json(run_dir / "run.json", "run.json")
    metrics = read_json(run_dir / "metrics.json", "metrics.json")
    lines = read_ndjson(run_dir / "results.ndjson", "results.ndjson")
    return summary_of(run, metrics, lines, request)


def summary_of(run, metrics, lines, request):
    """The summary.json document of a run read into memory: its run.json, metrics.json and result lines."""
    if not isinstance(run.get("runId"), str):
        raise InputError("run.json has no runId")
    kinds = {}
    for m in metrics.get("metrics") or []:
        if not isinstance(m, dict) or m.get("kind") not in METRIC_KINDS or m.get("id") in kinds:
            raise InputError(f"metrics.json: {m!r} is not a metric declared once with a kind AEF 1.0 defines ([SUM-1])")
        kinds[m["id"]] = m["kind"]
    lanes = request.get("lanes") if isinstance(request, dict) else None
    if not isinstance(lanes, list) or not all(isinstance(l, dict) and isinstance(l.get("lane"), str)
                                              and isinstance(l.get("metrics"), list) for l in lanes):
        raise InputError('the request is {"lanes": [{"lane": name, "metrics": [entry, ...]}, ...]}')
    names = [l["lane"] for l in lanes]
    keys = [(l["lane"], e.get("metric"), e.get("path")) for l in lanes for e in l["metrics"] if isinstance(e, dict)]
    if len(set(names)) != len(names) or len(set(keys)) != len(keys):
        raise InputError("the request names a lane twice, or one lane, metric and path twice ([SUM-9])")
    return {"schemaVersion": AEF_VERSION, "runId": run["runId"],  # SUM-2
            "lanes": [{"lane": l["lane"], "metrics": [_entry(e, l["lane"], names, kinds, lines) for e in l["metrics"]]}
                      for l in lanes]}


# ---------------------------------------------------------------------------- produce (spec 03 §3.4, spec 09 §9.2.1)
# A scenario holds the facts a producer has when it writes a run, and nothing it must derive:
#   {"run": run.json, "metrics": metrics.json, "cases": [CASE, ...], "summary": a summarize request}
#   NODE: {"path", "evaluator", "state", "scores"?, "severity"?, "reason"?, "lane"?, "component"? (on a child),
#          "aggregation"?: {"strategy", "rulePath", "threshold"?, "score"?, "decisive"?: [child paths]},
#          "children"?: [NODE, ...]}
#   CASE: a NODE with "caseId", and for a case run in trials "trials": {"aggregation", "k"?, "trees": [NODE, ...]}:
#         one tree per trial; the case's own node and its children are then the rollups.

CLOSED_STATUSES = ("completed", "aborted")  # RUN-5
TYPED_ABSENCES = ("not_measured", "not_applicable", "skipped", "error", "pending")  # RES-1
LINE_FACTS = ("scores", "severity", "reason", "lane")  # copied to the line as given
NODE_MEMBERS = {"path", "evaluator", "state", "component", "aggregation", "children", *LINE_FACTS}
CASE_MEMBERS = {"caseId", "trials"}  # beside a node's, on a case
AGGREGATION_FACTS = {"strategy", "rulePath", "threshold", "score", "decisive"}  # RES-5: the producer's own
TRIALS_FACTS = {"aggregation", "k", "trees"}


def result_id(run_id, case_id, path, trial=None):
    """RES-4: "r_" and the first 32 hex characters of the SHA-256 of runId, caseId, path and trial, joined by U+001F;
    the trial in plain integer digits, or empty on a line without one."""
    text = "\x1f".join((run_id, case_id, path, "" if trial is None else str(trial)))
    return "r_" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:32]


def _checked():
    return "produce-unchecked" not in MUTATIONS


def _shape(ok, what):
    if not ok:
        raise InputError(f"the scenario: {what} (spec 09 §9.2.1)")


def _node_lines(run_id, case_id, node, trial, parent, where, own=frozenset(), rollup=None):
    """The lines of one node and the nodes under it, the node's own line first. trial: the trial of the tree the node
    is in (None in a case's own tree); parent: the line of its parent (None at a root); rollup: for a case run in
    trials, {"states": the states of the trial lines by path, "trials": the scenario's trials}: the lines are then
    the case's rollups."""
    _shape(isinstance(node, dict), f"{where} is not an object")
    unknown = set(node) - NODE_MEMBERS - own
    _shape(not unknown, f"{where} has members a node does not: {', '.join(sorted(unknown))}")
    path, state = node.get("path"), node.get("state")
    _shape(isinstance(path, str) and isinstance(state, str) and isinstance(node.get("evaluator"), dict),
           f"{where} has no path, state or evaluator")
    _shape(isinstance(node.get("scores", []), list) and all(isinstance(node.get(f, ""), str) for f in LINE_FACTS[1:]),
           f"{where}: scores is a list, and severity, reason and lane are strings")
    if state == "pending" and _checked():
        raise InputError(f"{where} is pending in a closed run ([RES-3]): it is skipped or error, with a reason")
    line = {"schemaVersion": AEF_VERSION, "resultId": result_id(run_id, case_id, path, trial),  # RES-4
            "caseId": case_id, "path": path}
    if parent is not None:
        name = path[len(parent["path"]) + 1:] if path.startswith(parent["path"] + "/") else ""
        if (not name or "/" in name) and _checked():
            raise InputError(f"{where}: the path {path!r} is not its parent's ({parent['path']!r}) and one more level")
        line["parentResultId"] = parent["resultId"]  # the parent's line, in the same tree
        component = node.get("component")
        if component is None and _checked():
            raise InputError(f"{where}: a child without component ([RES-5])")
        if component is not None:
            _shape(isinstance(component, dict) and isinstance(component.get("required"), bool)
                   and isinstance(component.get("weight"), (int, float))
                   and not isinstance(component.get("weight"), bool), f"{where}.component is not {{weight, required}}")
            if not ("produce-component" in MUTATIONS and component.get("weight") == 0):
                line["component"] = component
    elif "component" in node and _checked():
        raise InputError(f"{where}: component on a node that is no child ([RES-5])")
    if trial is not None:
        line["trial"] = trial  # RES-8: every line of a trial's tree carries its trial
    line["evaluator"], line["state"] = node["evaluator"], state
    for fact in LINE_FACTS:
        if fact in node:
            line[fact] = node[fact]
    if rollup is not None:  # RES-8: n and passed over the trial lines at this path; agree when they are in one state
        states, given = rollup["states"].get(path, []), rollup["trials"]
        n, passed = len(states), sum(1 for s in states if s == "passed")
        if "produce-rollup-n" in MUTATIONS:
            n = len(given["trees"])
        if "produce-rollup-passed" in MUTATIONS:
            passed = sum(1 for s in states if s != "failed")
        agree = passed in (0, n) if "produce-agree" in MUTATIONS else len(set(states)) == 1
        line["trials"] = {"n": n, "passed": passed, "aggregation": given["aggregation"], "agree": agree}
        if "k" in given:
            line["trials"]["k"] = int(given["k"])  # ENC-4: an integer is written in plain digits

    children = node.get("children", [])
    _shape(isinstance(children, list), f"{where}.children is not a list")
    lines, kids = [line], []
    for i, child in enumerate(children):
        below = _node_lines(run_id, case_id, child, trial, line, f"{where}.children[{i}]", rollup=rollup)
        kids.append(below[0])
        lines += below
    facts = node.get("aggregation")
    if kids and facts is None and _checked():
        raise InputError(f"{where}: a node with children and no aggregation ([RES-5])")
    if facts is not None:
        _shape(isinstance(facts, dict) and not set(facts) - AGGREGATION_FACTS and isinstance(facts.get("strategy"), str)
               and isinstance(facts.get("rulePath"), str), f"{where}.aggregation is not the producer's facts "
               "(strategy, rulePath, and optionally threshold, score and decisive)")
        counted = kids
        if "produce-aggregation-counts" in MUTATIONS:
            counted = [k for k in kids if k["state"] != "not_applicable"]
        aggregation = {"strategy": facts["strategy"]}
        aggregation.update({f: facts[f] for f in ("threshold", "score") if f in facts})
        # RES-5, RES-6: total is the children; measured those in a measured state (RES-1); unmeasured the others, by
        # state (pending only when there is one: an absent count is 0).
        aggregation.update(rulePath=facts["rulePath"], measured=sum(1 for k in counted if k["state"] in MEASURED_STATES),
                           total=len(counted), unmeasured={s: sum(1 for k in counted if k["state"] == s)
                                                           for s in TYPED_ABSENCES if s != "pending"})
        pending = sum(1 for k in counted if k["state"] == "pending")
        if pending:
            aggregation["unmeasured"]["pending"] = pending
        if "decisive" in facts:
            _shape(isinstance(facts["decisive"], list) and all(isinstance(p, str) for p in facts["decisive"]),
                   f"{where}.aggregation.decisive is not a list of paths")
            ids = {k["path"]: k["resultId"] for k in reversed(kids)}
            for p in facts["decisive"]:
                if p not in ids and _checked():
                    raise InputError(f"{where}: the decisive path {p!r} is no child's ([RES-6])")
            aggregation["decisive"] = [ids[p] for p in facts["decisive"] if p in ids]
        line["aggregation"] = aggregation
    return lines


def _case_lines(run_id, case, where):
    """The lines of one case: its tree's, or, for a case run in trials, each trial's tree and then the rollups."""
    _shape(isinstance(case, dict) and isinstance(case.get("caseId"), str), f"{where} has no caseId")
    case_id, trials = case["caseId"], case.get("trials")
    if trials is None:
        return _node_lines(run_id, case_id, case, None, None, where, own=CASE_MEMBERS)
    _shape(isinstance(trials, dict) and not set(trials) - TRIALS_FACTS and isinstance(trials.get("aggregation"), str)
           and isinstance(trials.get("trees"), list) and trials["trees"]
           and (type(trials.get("k", 1)) is int or isinstance(trials.get("k"), float) and trials["k"].is_integer()),
           f"{where}.trials is not {{aggregation, k?, trees}} with a tree per trial")
    lines, states = [], {}
    for t, tree in enumerate(trials["trees"]):
        if isinstance(tree, dict) and tree.get("path") != case.get("path") and _checked():
            raise InputError(f"{where}.trials.trees[{t}]: its root is at {tree.get('path')!r}, not at the case's "
                             f"path {case.get('path')!r} ([RES-8])")
        for line in _node_lines(run_id, case_id, tree, t, None, f"{where}.trials.trees[{t}]"):
            lines.append(line)
            states.setdefault(line["path"], []).append(line["state"])
    rollups = _node_lines(run_id, case_id, case, None, None, where, own=CASE_MEMBERS,
                          rollup={"states": states, "trials": trials})
    if {r["path"] for r in rollups} != set(states) and _checked():
        raise InputError(f"{where}: the case's tree and its trial trees do not have the same paths: a rollup at each "
                         "path its trials have, and at no other ([RES-8])")
    return lines + rollups


def _renamed(lines, new_id):
    """The lines with every result id replaced by new_id(line), and the parents and decisive ids that cite them."""
    ids = {line["resultId"]: new_id(line) for line in lines}
    for line in lines:
        line["resultId"] = ids[line["resultId"]]
        if line.get("parentResultId") in ids:
            line["parentResultId"] = ids[line["parentResultId"]]
        if "decisive" in line.get("aggregation", {}):
            line["aggregation"]["decisive"] = [ids.get(d, d) for d in line["aggregation"]["decisive"]]
    return lines


def _mutated(run_id, lines):
    """The writers --self-check breaks after the lines are derived (aef_conformance.py)."""
    if "produce-ids" in MUTATIONS:
        _renamed(lines, lambda l: result_id(run_id, l["caseId"], l["path"]))
    if "produce-trial-children" in MUTATIONS:
        for line in lines:
            if "parentResultId" in line:
                line.pop("trial", None)
        _renamed(lines, lambda l: result_id(run_id, l["caseId"], l["path"], l.get("trial")))
    if "produce-parent" in MUTATIONS:
        first = {}
        for line in lines:
            first.setdefault((line["caseId"], line["path"]), line["resultId"])
        for line in lines:
            if "parentResultId" in line:
                line["parentResultId"] = first[(line["caseId"], line["path"].rsplit("/", 1)[0])]
    return lines


def _document_bytes(value):
    return (json.dumps(value, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def produce(scenario, out_dir):
    """Writes the closed, unsealed run the scenario describes in out_dir: run.json and metrics.json as given,
    results.ndjson with what [RES-4]-[RES-8] derive, and summary.json ([SUM-2]-[SUM-9]). Nothing is written when the
    scenario is an input error. Returns {"results": the number of lines}."""
    out = Path(out_dir)
    if out.exists() and not (out.is_dir() and not any(out.iterdir())):
        raise InputError(f"{out_dir}: OUT is a folder that does not exist yet, or an empty one")
    _shape(isinstance(scenario, dict) and not set(scenario) ^ {"run", "metrics", "cases", "summary"},
           "a scenario is {run, metrics, cases, summary}")
    run, metrics, cases = scenario["run"], scenario["metrics"], scenario["cases"]
    _shape(isinstance(run, dict) and isinstance(run.get("runId"), str), "run is no run.json with a runId")
    _shape(isinstance(metrics, dict), "metrics is no metrics.json")
    _shape(isinstance(cases, list), "cases is not a list")
    if run.get("status") not in CLOSED_STATUSES and _checked():
        raise InputError(f"the run is not closed (status {run.get('status')!r}): produce writes a closed run")
    lines = []
    for i, case in enumerate(cases):
        lines += _case_lines(run["runId"], case, f"cases[{i}]")
    seen = set()
    for line in lines:
        key = (line["caseId"], line["path"], line.get("trial"))
        if key in seen and _checked():
            raise InputError(f"two lines of case {key[0]!r} at {key[1]!r}" + ("" if key[2] is None else f", trial {key[2]}")
                             + ": they would have one resultId ([RES-4])")
        seen.add(key)
    lines = _mutated(run["runId"], lines)
    summary = summary_of(run, metrics, lines, scenario["summary"])
    out.mkdir(parents=True, exist_ok=True)
    (out / "run.json").write_bytes(_document_bytes(run))
    (out / "results.ndjson").write_bytes("".join(json.dumps(line, ensure_ascii=False, separators=(",", ":")) + "\n"
                                                 for line in lines).encode("utf-8"))
    (out / "metrics.json").write_bytes(_document_bytes(metrics))
    (out / "summary.json").write_bytes(_document_bytes(summary))
    return {"results": len(lines)}


# ---------------------------------------------------------------------------- seal-write (spec 04 §4.1)

STATEMENT_TYPE = "https://in-toto.io/Statement/v1"
EVIDENCE_PREDICATE = "https://agenteval.dev/aef/1/evidence"
NOT_SEALED = ("seal.json", "attestation.dsse.json")  # SEAL-1, with everything under overlays/


_TIME = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z")


def _instant(text, what):
    """ENC-8: (the second, the nanosecond) of an RFC 3339 UTC time with zero to nine fraction digits, on a date that
    exists; compared at the full precision written, never rounded."""
    m = _TIME.fullmatch(text) if isinstance(text, str) else None
    try:
        if m:
            moment = datetime.datetime(*(int(g) for g in m.groups()[:6]), tzinfo=datetime.timezone.utc)
            return int(moment.timestamp()), int((m.group(7) or "").ljust(9, "0"))
    except ValueError:
        pass
    raise InputError(f"{what} {text!r} is not an RFC 3339 UTC time ([ENC-8])")


def run_files(run_dir):
    """The path of every file of the run folder. A symbolic link or another entry that is not a regular file or a
    folder is an input error ([RUN-3]: never followed or read)."""
    files = []
    for root, folders, names in os.walk(run_dir):
        for name in folders:
            if (Path(root) / name).is_symlink():
                raise InputError(f"{(Path(root) / name).relative_to(run_dir).as_posix()} is a link ([RUN-3]): "
                                 "the run cannot be sealed")
        for name in names:
            entry = Path(root) / name
            rel = entry.relative_to(run_dir).as_posix()
            if entry.is_symlink() or not entry.is_file():
                raise InputError(f"{rel} is not a regular file ([RUN-3]): the run cannot be sealed")
            files.append(rel)
    return files


def sealed_files(run_dir):
    """SEAL-1: the paths of every file of the run except seal.json, attestation.dsse.json and overlays/, in the
    order of SEAL-3: by the UTF-8 bytes of the path."""
    files = [p for p in run_files(run_dir) if p not in NOT_SEALED and not p.startswith("overlays/")]
    if "seal-path-order" in MUTATIONS:
        return sorted(files, key=lambda p: p.split("/"))
    files.sort(key=lambda p: p.encode("utf-8"))
    return files[:-1] if "seal-omits-file" in MUTATIONS else files


# What a host checks before it seals a run on taking custody of it (SEAL-1, sealedBy ingest): the paths keep RUN-3,
# and the files are valid against the reader schemas (ENC-16: the schema accepts them, and every timestamp exists).

_SEGMENT = re.compile(r"[A-Za-z0-9._-]+")
_RESERVED = {"CON", "PRN", "AUX", "NUL"} | {f"COM{i}" for i in range(1, 10)} | {f"LPT{i}" for i in range(1, 10)}
SCHEMA_FILES = {"run.json": "run", "metrics.json": "metrics", "summary.json": "summary"}  # RUN-2
SCHEMA_LINES = {"results.ndjson": "result", "evidence.ndjson": "evidence", "gates.ndjson": "gate-decision"}
REQUIRED_FILES = ("run.json", "results.ndjson", "metrics.json", "summary.json")  # RUN-2, in a closed run
_READER = None


def path_problems(paths):
    """RUN-3 over the paths of a run's files: the paths that break it, with why."""
    problems = []
    for p in paths:
        segments = p.split("/")
        if len(p.encode("utf-8")) > 255 or not all(_SEGMENT.fullmatch(s) for s in segments):
            problems.append(f"{p}: not segments of ASCII letters, digits, '.', '_' and '-', at most 255 bytes")
        elif any(s.startswith(".") or s.endswith(".") for s in segments):
            problems.append(f"{p}: a segment starts or ends with '.'")
        elif any(s.split(".", 1)[0].upper() in _RESERVED for s in segments):
            problems.append(f"{p}: a segment is a name Windows reserves")
    names = {}  # every path and every folder of one, by its lower case
    for p in paths:
        segments = p.split("/")
        for i in range(1, len(segments) + 1):
            prefix = "/".join(segments[:i])
            names.setdefault(prefix.lower(), set()).add(prefix)
    problems += [f"{', '.join(sorted(same))}: differ only in letter case" for same in names.values() if len(same) > 1]
    return problems


def _reader_schemas():
    global _READER
    if _READER is None:
        _READER = aef_schema.load_schemas(TOOLS.parent / "1" / "schemas" / "reader")
    return _READER


def _times_exist(schemas, schema, instance):
    """ENC-8, ENC-16: whether every string the schema places at a timestamp names a date that exists (a pattern
    cannot refuse 2026-02-31). Walks the schema beside the instance through every applicator."""
    stamp = schemas._target("common#/$defs/timestamp")
    stack, seen = [(schemas._target(schema), instance)], set()
    while stack:
        s, x = stack.pop()
        if not isinstance(s, dict) or (id(s), id(x)) in seen:
            continue
        seen.add((id(s), id(x)))
        if s is stamp and isinstance(x, str) and _TIME.fullmatch(x):
            try:
                datetime.date(int(x[0:4]), int(x[5:7]), int(x[8:10]))
            except ValueError:
                return False
        if "$ref" in s:
            stack.append((schemas._refs[id(s)], x))
        for key in ("allOf", "anyOf", "oneOf"):
            stack.extend((sub, x) for sub in s.get(key, ()))
        stack.extend((s[key], x) for key in ("then", "else") if key in s)
        if isinstance(x, dict):
            declared = s.get("properties", {})
            stack.extend((sub, x[name]) for name, sub in declared.items() if name in x)
            if isinstance(s.get("additionalProperties"), dict):
                stack.extend((s["additionalProperties"], x[n]) for n in x if n not in declared)
            for pattern, sub in s.get("patternProperties", {}).items():
                stack.extend((sub, x[n]) for n in x if aef_schema.compile_pattern(pattern).search(n))
        if isinstance(x, list):
            prefix = s.get("prefixItems", [])
            stack.extend((prefix[i] if i < len(prefix) else s.get("items"), item) for i, item in enumerate(x))
    return True


def schema_problems(run_dir, paths):
    """The run's files the reader schemas refuse (ENC-16), or that are missing (RUN-2) or do not read (§2.1, §2.2)."""
    schemas, problems = _reader_schemas(), []
    for name in REQUIRED_FILES:
        if name not in paths:
            problems.append(f"{name}: missing ([RUN-2])")
    for name, schema in list(SCHEMA_FILES.items()) + list(SCHEMA_LINES.items()):
        if name not in paths:
            continue
        try:
            documents = read_ndjson(run_dir / name, name) if name in SCHEMA_LINES else [read_json(run_dir / name, name)]
        except InputError as error:
            problems.append(f"{error} (it does not read)")
            continue
        for i, doc in enumerate(documents, start=1):
            if not schemas.is_valid(schema, doc) or not _times_exist(schemas, schema, doc):
                problems.append(f"{name}{f':{i}' if name in SCHEMA_LINES else ''}: refused by the reader {schema} "
                                "schema ([ENC-16])")
    return problems


def _projection(run, *fields):
    return {k: run[k] for k in fields if k in run}


def seal_write(run_dir, sealed_by, sealed_at):
    """Writes run_dir/seal.json ([SEAL-5]) and returns {"runHash": hex}."""
    run_dir = Path(run_dir)
    if sealed_by not in ("producer", "ingest"):
        raise InputError(f"--sealed-by is producer or ingest, not {sealed_by!r} ([SEAL-5])")
    sealed = _instant(sealed_at, "--sealed-at")
    if (run_dir / "seal.json").exists() or (run_dir / "seal.json").is_symlink():
        raise InputError("the run already has a seal.json: it is not resealed")
    run = read_json(run_dir / "run.json", "run.json")
    if run.get("status") not in ("completed", "aborted"):
        raise InputError(f"only a closed run is sealed ([SEAL-1]): run.json says status {run.get('status')!r}")
    for field in ("runId", "producer", "subject", "endedAt"):
        if field not in run:
            raise InputError(f"run.json has no {field}: the predicate cannot be written ([SEAL-5])")
    if sealed < _instant(run["endedAt"], "run.json's endedAt"):
        raise InputError("--sealed-at is before the run closed: only a closed run is sealed ([SEAL-1], [SEAL-5])")
    if sealed_by == "ingest" and "seal-ingest-unchecked" not in MUTATIONS:
        # SEAL-1: a host seals on custody only a run whose paths keep RUN-3 and whose files the reader schemas accept,
        # as received. (A producer SHOULD NOT seal a run with problems; it is not refused here.)
        paths = run_files(run_dir)
        problems = path_problems(paths) + schema_problems(run_dir, paths)
        if problems:
            raise InputError("a host seals on taking custody only a valid run ([SEAL-1]); this one is not: "
                             + "; ".join(problems))
    subjects, manifest = [], []
    for rel in sealed_files(run_dir):
        data = (run_dir / rel).read_bytes()
        digest = hashlib.sha256(data).hexdigest()  # SEAL-2: the exact bytes
        subjects.append({"name": rel, "digest": {"sha256": digest}})
        manifest.append(f"{digest}  {len(data)}  {rel}\n")  # SEAL-3
    run_hash = hashlib.sha256("".join(manifest).encode("utf-8")).hexdigest()  # SEAL-4
    deployment, suite = run.get("deployment"), run.get("suite")
    statement = {
        "_type": STATEMENT_TYPE,
        "subject": subjects,
        "predicateType": EVIDENCE_PREDICATE,
        "predicate": {  # SEAL-5: the run's identity and what run.json says, as it says it
            "schemaVersion": AEF_VERSION,
            "runId": run["runId"],
            "runHash": run_hash,
            "producer": _projection(run["producer"], "name", "version"),
            "subject": _projection(run["subject"], "ref", "version"),
            "deployment": None if deployment is None else _projection(deployment, "ref"),
            "suite": None if suite is None else _projection(suite, "ref", "version", "digest"),
            "judges": [_projection(j, "model", "rubricDigest") for j in run.get("judges") or []],
            "closedAt": run["endedAt"],
            "sealedAt": sealed_at,
            "sealedBy": sealed_by,
        },
    }
    (run_dir / "seal.json").write_bytes((json.dumps(statement, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))
    return {"runHash": run_hash}


# ---------------------------------------------------------------------------- sign (spec 04 §4.4)

def _der_any(data, pos, what):
    """(tag, value, position after it) of the DER element at pos."""
    try:
        return aef_crypto._der_read(data, pos)
    except ValueError as error:
        raise InputError(f"the private key: {what} is not DER: {error}") from None


def _der(data, pos, tag, what):
    """The value of the DER element at pos, which must have this tag, and the position after it."""
    found, value, end = _der_any(data, pos, what)
    if found != tag:
        raise InputError(f"the private key: {what} has tag {found:#04x}, not {tag:#04x}")
    return value, end


def _uncompressed(public_key):
    return b"\x04" + public_key.x.to_bytes(32, "big") + public_key.y.to_bytes(32, "big")


def load_private_key(pem: bytes):
    """A private key from an unencrypted PKCS#8 PEM block (`-----BEGIN PRIVATE KEY-----`): RFC 5208's PrivateKeyInfo
    or RFC 5958's OneAsymmetricKey, holding a P-256 key (id-ecPublicKey with prime256v1, an RFC 5915 ECPrivateKey) or
    an Ed25519 key (id-Ed25519, RFC 8410: the 32-byte seed in an OCTET STRING). A public key the file also carries
    must be the private key's."""
    try:
        text = pem.decode("ascii")
    except UnicodeDecodeError:
        raise InputError("the private key is not a PEM file (ASCII)") from None
    lines = [line.rstrip("\r") for line in text.split("\n")]
    try:
        begin = lines.index("-----BEGIN PRIVATE KEY-----")
        end = lines.index("-----END PRIVATE KEY-----", begin)
    except ValueError:
        raise InputError("the private key is not a PKCS#8 PEM block (-----BEGIN PRIVATE KEY-----)") from None
    try:
        der = base64.b64decode("".join(lines[begin + 1:end]), validate=True)
    except ValueError:
        raise InputError("the private key's PEM body is not base64") from None

    body, after = _der(der, 0, 0x30, "the PrivateKeyInfo")
    if after != len(der):
        raise InputError("the private key: bytes after the PrivateKeyInfo")
    version, pos = _der(body, 0, 0x02, "the version")
    if version not in (b"\x00", b"\x01"):
        raise InputError("the private key: PKCS#8 version 0 or 1")
    algorithm, pos = _der(body, pos, 0x30, "the AlgorithmIdentifier")
    private, pos = _der(body, pos, 0x04, "the privateKey OCTET STRING")
    public = None
    while pos < len(body):
        found, value, pos = _der_any(body, pos, "an optional element")
        if found == 0x81:  # [1] IMPLICIT BIT STRING: the public key (RFC 5958)
            public = value
        elif found != 0xA0:  # [0] attributes: ignored
            raise InputError(f"the private key: an unexpected element {found:#04x}")

    if algorithm == aef_crypto.ID_EC_PUBLIC_KEY + aef_crypto.PRIME256V1:
        ec, after = _der(private, 0, 0x30, "the ECPrivateKey")
        if after != len(private):
            raise InputError("the private key: bytes after the ECPrivateKey")
        ec_version, p = _der(ec, 0, 0x02, "the ECPrivateKey version")
        scalar, p = _der(ec, p, 0x04, "the ECPrivateKey privateKey")
        if ec_version != b"\x01" or len(scalar) != 32:
            raise InputError("the private key: an ECPrivateKey is version 1 with a 32-byte P-256 scalar (RFC 5915)")
        try:
            key = aef_crypto.P256PrivateKey(scalar)
        except ValueError as error:
            raise InputError(f"the private key: {error}") from None
        while p < len(ec):
            found, value, p = _der_any(ec, p, "an ECPrivateKey element")
            if found == 0xA0 and value != aef_crypto.PRIME256V1:
                raise InputError("the private key: ECPrivateKey parameters other than prime256v1")
            if found == 0xA1:
                bits, _ = _der(value, 0, 0x03, "the ECPrivateKey publicKey")
                if bits != b"\x00" + _uncompressed(key.public_key):
                    raise InputError("the private key: its public key is not the private key's")
        if public is not None and public != b"\x00" + _uncompressed(key.public_key):
            raise InputError("the private key: its public key is not the private key's")
        return key
    if algorithm == aef_crypto.ID_ED25519:
        seed, after = _der(private, 0, 0x04, "the Ed25519 CurvePrivateKey")
        if after != len(private) or len(seed) != 32:
            raise InputError("the private key: an Ed25519 private key is a 32-byte seed (RFC 8410)")
        key = aef_crypto.Ed25519PrivateKey(seed)
        if public is not None and public != b"\x00" + key.public_key.key:
            raise InputError("the private key: its public key is not the private key's")
        return key
    raise InputError("the private key is neither a P-256 key (id-ecPublicKey, prime256v1) nor an Ed25519 key ([SIG-2])")


def _b64(data):
    """SIG-1: base64 is written in the standard alphabet with padding."""
    text = base64.b64encode(data).decode("ascii")
    return text.rstrip("=") if "sign-unpadded" in MUTATIONS else text


def sign(file_path, key_path, payload_type):
    """The DSSE envelope over the file's exact bytes ([SIG-1]), signed by the key ([SIG-2]), under its key id
    ([SIG-3])."""
    if not isinstance(payload_type, str) or not payload_type:
        raise InputError("--payload-type is the type [SIG-1] gives the file")
    try:
        payload = Path(file_path).read_bytes()
        pem = Path(key_path).read_bytes()
    except OSError as error:
        raise InputError(str(error)) from None
    key = load_private_key(pem)
    signer = key
    if "sign-other-key" in MUTATIONS:
        other = hashlib.sha256(b"AEF 1.0 another key: never trust").digest()
        signer = aef_crypto.P256PrivateKey(other) if isinstance(key, aef_crypto.P256PrivateKey) \
            else aef_crypto.Ed25519PrivateKey(other)
    signature = signer.sign(aef_crypto.pae(payload_type, payload))  # the PAE of the type and the payload
    return {"payloadType": payload_type, "payload": _b64(payload),
            "signatures": [{"keyid": aef_crypto.keyid(key.public_key), "sig": _b64(signature)}]}


# ---------------------------------------------------------------------------- command line

def dispatch(argv):
    """Performs one command (argv without the program name) and returns its JSON value. InputError or SystemExit
    on a usage or input error."""
    parser = argparse.ArgumentParser(prog="aef_produce.py", description=__doc__.split("\n\n")[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("summarize")
    p.add_argument("dir")
    p.add_argument("request")
    p = sub.add_parser("produce")
    p.add_argument("scenario")
    p.add_argument("out")
    p = sub.add_parser("seal-write")
    p.add_argument("dir")
    p.add_argument("--sealed-by", required=True)
    p.add_argument("--sealed-at", required=True)
    p = sub.add_parser("sign")
    p.add_argument("file")
    p.add_argument("key")
    p.add_argument("--payload-type", required=True)
    a = parser.parse_args(argv)
    if a.command == "summarize":
        return summarize(a.dir, read_json(a.request, "the request"))
    if a.command == "produce":
        return produce(read_json(a.scenario, "the scenario"), a.out)
    if a.command == "seal-write":
        if not Path(a.dir).is_dir():
            raise InputError(f"{a.dir}: not a folder")
        return seal_write(a.dir, a.sealed_by, a.sealed_at)
    return sign(a.file, a.key, a.payload_type)


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    try:
        result = dispatch(argv[1:])
    except InputError as error:
        print(f"aef_produce.py: {error}", file=sys.stderr)
        return 2
    sys.stdout.write(json.dumps(result, ensure_ascii=False, sort_keys=True) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
