#!/usr/bin/env python3
"""Runner protocol references for spec 06 (1/spec/06-runners.md): plan-to-runner matching ([PLAN-7]), the
verification of a finished event stream ([STRM-3]), and the check that the runs a stream names keep to its plan
([STRM-4]).

Usage:
  python aef_stream.py --check
      Runs every vector of conformance/protocol/ (the matching, stream and plan-conformance vectors) and exits 1 on
      any difference.
  python aef_stream.py --conform EVENTS PLAN RUNS [--policy POLICY]
      [STRM-4] for one job: the event stream, its plan, and the folder of the runs it produced; POLICY is a trust
      policy file, which decides which redactions are authorized when the runs are verified (OVL-10). Prints
      {"problems": [[where, problem], ...]}; exits 0 when it ran, 2 on a usage or input error.

The vectors of conformance/protocol/ (spec 09 §9.2.1 defers to this list). Every expected.json has `kind` and
`rules` (the rule ids the vector concerns):
  plans/<name>/, runners/<name>/   kind plan: document.json; `schema` (run-plan or runner), `writer` and `reader`
                                   (valid or invalid), optional `reads` (field path: the value a reader reads it as,
                                   spec 07 §7.3, as for a reader-only vector), `why`
  matching/<name>/                 kind matching: plan.json and runner.json; `matches` (true or false), `readerOnly`
                                   when a document holds a value a later minor may add (only a reader accepts it),
                                   `why`
  streams/<name>/                  kind stream: events.ndjson (read as STRM-2 says: a last line without LF is still
                                   being written, and not read); `plan` (the plan file, relative to the vector: the
                                   plans are shared, in streams/), `problems` ([where, problem] pairs, in
                                   order), and `readerOnly` when a writer could not write the stream (an unknown kind)
  plan-conformance/<name>/         kind plan-conformance: `events`, `plan` and `runs` (the stream, its plan and the
                                   folder of the runs it produced, beside expected.json), optional `policy` (a trust
                                   policy file beside it), `problems` ([where, problem] pairs, in order), `why`
A stream vector's plan digest is the SHA-256 of its plan file's bytes.
"""
import calendar
import datetime
import hashlib
import json
import math
import os
import re
import sys
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))
from aef_decide import parse_duration  # noqa: E402  (ENC-9: the one duration grammar)

TERMINAL = {"job.sealed", "job.failed", "job.cancelled", "job.refused"}
TIME = re.compile(r"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z$")
PROVENANCE = ("planId", "planDigest", "jobId", "runnerId")


def parse_time(text):
    """(seconds since the epoch, nanoseconds): exact. An impossible date (February 31) is refused, not rolled over."""
    m = TIME.fullmatch(text)
    if not m:
        raise ValueError(f"{text!r} is not an RFC 3339 UTC time")
    y, mo, d, h, mi, s, frac = (int(g) if g and i < 6 else g for i, g in enumerate(m.groups()))
    moment = datetime.datetime(y, mo, d, h, mi, s)  # raises on an impossible date
    return calendar.timegm(moment.timetuple()), int((frac or "").ljust(9, "0"))


def _plan_knowledge():
    """The provider, isolation, content capture, target modes, credential schemes and purposes this version knows: what
    the writer schemas accept there (VER-8)."""
    writer = Path(__file__).resolve().parents[1] / "1" / "schemas" / "writer"
    plan = json.loads((writer / "run-plan.schema.json").read_bytes())["properties"]
    provider = json.loads((writer / "common.schema.json").read_bytes())["$defs"]["provider"]["anyOf"]
    creds = plan["credentialRefs"]["items"]["properties"]
    return (set(provider[0]["enum"]), re.compile(provider[1]["pattern"]), set(plan["isolation"]["enum"]),
            set(plan["contentCapture"]["enum"]), set(plan["targetMode"]["enum"]), set(creds["scheme"]["enum"]),
            set(creds["purpose"]["enum"]))


def target_mode(plan):
    """The target mode a plan asks for: its targetMode, live when it has none (spec 06 §6.1)."""
    return plan.get("targetMode", "live")


def target_modes(runner):
    """The target modes a runner gives: its manifest's targetModes, live only when it has none (PLAN-6)."""
    modes = runner.get("targetModes")
    return modes if isinstance(modes, list) else ["live"]


def knows(plan):
    """PLAN-7: whether a runner of this version knows every value of the plan it must know, or must refuse it."""
    providers, provider_pattern, isolations, captures, modes, schemes, purposes = _plan_knowledge()
    p = plan.get("provider")
    return ((p in providers or (isinstance(p, str) and provider_pattern.fullmatch(p) is not None))
            and plan.get("isolation") in isolations
            and plan.get("contentCapture", "off") in captures
            and target_mode(plan) in modes
            and all(c.get("scheme") in schemes and c.get("purpose") in purposes for c in plan.get("credentialRefs", [])))


def matches(plan, runner):
    """PLAN-7: a runner takes a plan when it can take it (it carries every selector tag, supports the plan's provider,
    gives the plan's target mode, and, for a remote-zone plan, is in the plan's zone) and knows its values (knows), as
    far as its manifest tells. A plan without a targetMode asks for live; a manifest without targetModes gives live
    only."""
    return (knows(plan)
            and all(tag in runner.get("tags", []) for tag in plan.get("runnerSelector", []))
            and plan["provider"] in runner["providers"]
            and target_mode(plan) in target_modes(runner)
            and (plan["isolation"] != "remote-zone" or runner.get("networkZone") == plan.get("zone")))


def verify(events, plan=None, plan_digest=None):
    """(where, problem) for every problem, in event order, then by problem name; 'stream' last."""
    problems = []
    previous_seq, previous_at, spent, terminal, accepted, cases, over_time = 0, None, None, False, False, 0, False
    announced = {}
    valid = [e for e in events if e is not None]  # None: an event-invalid line, which takes no other part
    first = valid[0] if valid else None
    start = parse_time(first["at"]) if first else None
    limit = None
    if plan is not None and "timeout" in plan["limits"]:
        limit = parse_duration(plan["limits"]["timeout"])
    seen, after_invalid = False, False
    for i, e in enumerate(events, start=1):
        if e is None:
            problems.append((f"event:{i}", "event-invalid"))
            after_invalid = True
            continue
        found, kind = [], e["kind"]
        if not seen and kind not in ("job.accepted", "job.refused"):
            found.append("first")
        seen = True
        seq = int(e["seq"])  # an integral number, also when written 2.0
        if seq != previous_seq + 1 and not after_invalid:
            found.append("seq")
        previous_seq, after_invalid = seq, False
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


# ---------------------------------------------------------------------------- STRM-4: the runs a stream names

def run_folders(root):
    """[(folder, run.json)] of every run under root, found by its run.json (RUN-1), in path order. A folder that holds
    a run holds no other; a run.json that is not a JSON object with a string runId names no run."""
    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        if "run.json" in filenames:
            dirnames[:] = []
            try:
                doc = json.loads(Path(dirpath, "run.json").read_bytes().decode("utf-8"))
            except ValueError:
                continue
            if isinstance(doc, dict) and isinstance(doc.get("runId"), str):
                found.append((Path(dirpath), doc))
    return sorted(found, key=lambda f: f[0].as_posix().encode("utf-8"))


def examine(folder, policy=None):
    """(a run's run hash, whether it is intact), by the reference run verifier: SEAL-4's run hash (its seal's
    predicate.runHash when seal.json is valid against the reader seal schema, else recomputed from the files) and the
    outcome of §4.5 under the caller's trust policy, which decides which redactions are authorized (OVL-10). Imported
    here: aef_verify imports this module."""
    if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
        sys.path.insert(0, str(TOOLS))
    import aef_verify  # noqa: E402
    run = aef_verify.Run(folder, policy)
    return run.claimed_run_hash(), run.intact


def ndjson(path):
    """The objects of an NDJSON file, one per LF-terminated line."""
    return [json.loads(line) for line in Path(path).read_bytes().decode("utf-8").split("\n") if line]


def departures(doc, plan, accepted, terminal):
    """The codes of STRM-4 an intact, announced run's run.json breaks (the job's limits apart). accepted and terminal
    are the stream's first job.accepted and first terminal event, or None."""
    found = []
    provenance = doc.get("provenance")
    if accepted is None or not isinstance(provenance, dict) or any(provenance.get(k) != accepted.get(k) for k in PROVENANCE):
        found.append("provenance")
    subject = doc.get("subject") or {}
    if subject.get("ref") != plan["subject"]["ref"] or subject.get("version") != plan["subject"]["version"]:
        found.append("subject")
    deployment = doc.get("deployment") or {}
    if any(key in plan["subject"] and deployment.get(field) != plan["subject"][key]
           for key, field in (("deployment", "ref"), ("endpoint", "endpoint"))):
        found.append("deployment")
    # Within the job, at full precision (ENC-8): a run that started before the job was accepted was adopted, not produced.
    if ((accepted is not None and "startedAt" in doc and parse_time(doc["startedAt"]) < parse_time(accepted["at"]))
            or (terminal is not None and "endedAt" in doc and parse_time(doc["endedAt"]) > parse_time(terminal["at"]))):
        found.append("time")
    suite = doc.get("suite") or {}
    if not any(suite.get("ref") == s["ref"] and suite.get("version") == s["version"]
               and ("digest" not in s or suite.get("digest") == s["digest"]) for s in plan["suites"]):
        found.append("suite")
    # The run's judges graded it (RUN-9): the plan's with some left out, a judge being its model, provider and
    # rubricDigest; a plan that names none allows none.
    fields = lambda judges: [(j.get("model"), j.get("provider"), j.get("rubricDigest")) for j in judges]
    if not sub_list(fields(doc.get("judges") or []), fields(plan.get("judges") or [])):
        found.append("judges")
    capture = "off" if doc.get("contentCapture") == "off" else "on"  # absent or unknown reads as on
    if capture != plan["contentCapture"]:
        found.append("content-capture")
    if (doc.get("execution") or {}).get("targetMode") != target_mode(plan):  # as written; a plan without one asks for live
        found.append("target-mode")
    return found


def sub_list(items, of):
    """STRM-4 judges: items are `of` with some entries left out, in its order (each item matched to a later entry, so
    an entry `of` names twice may be matched twice, and never more)."""
    rest = iter(of)
    return all(any(item == entry for entry in rest) for item in items)


def conform(events, plan, runs, policy=None, examine=examine):
    """[STRM-4]: (where, problem) for the runs a job.sealed or job.failed names, at 'run:<runId>', and for the job's
    limits over those runs, at 'job'; ordered by path (UTF-8 bytes) and then by code. runs is the folder of the runs the
    job produced; policy the caller's trust policy (or None); examine(folder, policy) gives (run hash, intact)."""
    accepted = next((e for e in events if e.get("kind") == "job.accepted"), None)
    terminal = next((e for e in events if e.get("kind") in TERMINAL), None)
    announced, named = {}, []
    for e in events:
        if e.get("kind") == "evidence.produced":
            announced.setdefault(e["runId"], e["runHash"])  # the first announcement (STRM-3 reports a change)
        elif e.get("kind") in ("job.sealed", "job.failed"):
            named.extend(r for r in e.get("runs", []) if r not in named)  # a run named twice is checked once
    folders = run_folders(runs)
    problems, chosen = [], []
    for run_id in named:
        candidates = [(f, doc) for f, doc in folders if doc["runId"] == run_id]
        run = next(((f, doc) for f, doc in candidates if run_id in announced and examine(f, policy) == (announced[run_id], True)), None)
        if not candidates:
            found = ["run-missing"]
        elif run is None:
            found = ["run-hash"]
        else:
            found = departures(run[1], plan, accepted, terminal)
            summary_file = run[0] / "summary.json"
            summary = json.loads(summary_file.read_bytes()) if summary_file.is_file() else {}
            if not isinstance((summary.get("cost") or {}).get("totalUsd"), (int, float)):
                found.append("no-cost")  # a budget cannot be checked without it
            chosen.append(run[0])
        problems.extend((f"run:{run_id}", p) for p in found)

    # The job's limits, over the runs found, each once: a runner cannot pass by splitting its work across runs. A case
    # is its run's suite (ref and version) with its caseId: one case id in two suites counts twice (PLAN-8).
    limits, costs, cases = plan["limits"], [], set()
    for folder in chosen:
        summary = json.loads((folder / "summary.json").read_bytes()) if (folder / "summary.json").is_file() else {}
        costs.append((summary.get("cost") or {}).get("totalUsd", 0))
        suite = json.loads((folder / "run.json").read_bytes()).get("suite") or {}
        lines = ndjson(folder / "results.ndjson") if (folder / "results.ndjson").is_file() else []
        cases |= {(suite.get("ref"), suite.get("version"), line["caseId"])
                  for line in lines if line.get("parentResultId") is None}
    if math.fsum(costs) > limits["maxUsd"]:  # STRM-4 (W4-2): summed exactly, rounded once, then compared
        problems.append(("job", "over-budget"))
    if "cases" in limits and len(cases) > limits["cases"]:
        problems.append(("job", "over-cases"))
    return sorted(problems, key=lambda p: (p[0].encode("utf-8"), p[1].encode("utf-8")))


def read_stream_lines(path):
    """STRM-2 and STRM-3: the finished lines of a stream (a last line without LF is still being written), each the
    event it holds or None when it is not an I-JSON object valid against the reader schema; or None for the whole when
    its framing breaks ENC-5 or ENC-7. Read through the reference verifier's I-JSON reader and schemas."""
    if str(TOOLS) not in sys.path:
        sys.path.insert(0, str(TOOLS))
    import aef_verify  # noqa: E402
    data = Path(path).read_bytes()
    complete = data[:data.rfind(b"\n") + 1]
    if aef_verify.ndjson_framing(complete):
        return None
    return [aef_verify._stream_event(raw) for _, _, raw in aef_verify.ndjson_lines(complete)]


def read_stream(path):
    """The events STRM-4 reads: the valid ones of read_stream_lines."""
    return [e for e in read_stream_lines(path) or [] if e is not None]


def conform_files(events_path, plan_path, runs_path, policy_path=None):
    plan = json.loads(Path(plan_path).read_bytes().decode("utf-8"))
    policy = json.loads(Path(policy_path).read_bytes().decode("utf-8")) if policy_path else None
    return conform(read_stream(events_path), plan, Path(runs_path), policy)


# ---------------------------------------------------------------------------- the vectors

def check():
    root = TOOLS.parent / "1" / "conformance" / "protocol"
    failed, count = 0, 0
    for d in sorted(p for p in (root / "streams").iterdir() if p.is_dir()):
        count += 1
        expected_doc = json.loads((d / "expected.json").read_text(encoding="utf-8"))
        plan_path = (d / expected_doc["plan"]).resolve()
        plan = json.loads(plan_path.read_text(encoding="utf-8"))
        events = read_stream_lines(d / "events.ndjson")
        expected = [(p[0], p[1]) for p in expected_doc["problems"]]
        actual = ([("stream", "encoding")] if events is None
                  else verify(events, plan, hashlib.sha256(plan_path.read_bytes()).hexdigest()))
        if actual != expected:
            failed += 1
            print(f"FAIL streams/{d.name}\n  expected {expected}\n  actual   {actual}")
    for d in sorted(p for p in (root / "matching").iterdir() if p.is_dir()):
        count += 1
        load = lambda n: json.loads((d / n).read_text(encoding="utf-8"))
        if matches(load("plan.json"), load("runner.json")) != load("expected.json")["matches"]:
            failed += 1
            print(f"FAIL matching/{d.name}")
    for d in sorted(p for p in (root / "plan-conformance").iterdir() if p.is_dir()):
        count += 1
        e = json.loads((d / "expected.json").read_text(encoding="utf-8"))
        expected = [tuple(p) for p in e["problems"]]
        actual = conform_files(d / e["events"], d / e["plan"], d / e["runs"], d / e["policy"] if "policy" in e else None)
        if actual != expected:
            failed += 1
            print(f"FAIL plan-conformance/{d.name}\n  expected {expected}\n  actual   {actual}")
    for bad in ("2026-02-31T00:00:00Z", "2026-10-08T12:00:00Z\n"):
        try:
            parse_time(bad)
            failed += 1
            print(f"FAIL parse_time accepted {bad!r}")
        except ValueError:
            pass
    print(f"{count - failed} of {count} protocol vectors pass")
    return 1 if failed or not count else 0


def main(argv):
    if "--check" in argv:
        return check()
    if "--conform" in argv:
        args = argv[argv.index("--conform") + 1:]
        policy = None
        if len(args) == 5 and args[3] == "--policy":
            args, policy = args[:3], args[4]
        if len(args) != 3:
            print("usage: aef_stream.py --conform EVENTS PLAN RUNS [--policy POLICY]", file=sys.stderr)
            return 2
        try:
            problems = conform_files(*args, policy)
        except (OSError, ValueError, KeyError, TypeError) as error:
            print(f"aef_stream.py: {error}", file=sys.stderr)
            return 2
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", newline="\n")
        sys.stdout.write(json.dumps({"problems": [list(p) for p in problems]}, ensure_ascii=False) + "\n")
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
