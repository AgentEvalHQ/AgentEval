#!/usr/bin/env python3
"""A minimal reference runner for AEF 1.0 (1/spec/06-runners.md), written from spec 06 alone, against a built-in
scripted target: a deterministic stand-in for the subject. It takes only a plan that asks for `targetMode` `scripted`
([PLAN-7]: a plan without one asks for `live`, which it cannot give), and every run it writes says `scripted`
([RUN-7]). Standard library only; it writes runs, result ids, summaries and seals through aef_produce.py, and checks
what it reads and writes with aef_schema.py.

Usage:
  python aef_runner.py run PLAN RUNNER OUT --target scripted [--at TIME]

PLAN is a run plan (schema run-plan); RUNNER the capability manifest of the runner this process acts as (schema
runner); OUT a folder that does not exist yet, or an empty one. TIME, an RFC 3339 UTC time ([ENC-8]), fixes the
clock: the job starts at TIME, and its clock moves only by the scripted target's fixed durations, so the same PLAN,
RUNNER and TIME give byte-identical output. Without --at the clock is the system's (UTC), never read backwards.

What it does:
  1. Reads PLAN and RUNNER as a reader does: UTF-8 I-JSON objects within [ENC-17]'s limits (4 MiB, depth 64), valid
     against the reader schemas ([VER-3]). planDigest is the SHA-256 of PLAN's exact bytes ([PLAN-5]).
  2. [PLAN-7] It takes the plan when it can take it (it carries every runnerSelector tag, supports the provider, and
     for a remote-zone plan has the plan's zone as its networkZone), knows the plan's provider, isolation,
     contentCapture, targetMode and every credential's scheme and purpose (known: what the writer schema accepts at
     that field, [VER-8]), and can drive the target as the plan asks: its targetMode is scripted. Otherwise it writes
     one event, job.refused, with the reason, and stops. A plan the reader schema refuses is refused the same way,
     when it names a planId a job.refused can carry. A reason names where a plan is wrong, never a value from a
     credential or from a plan the schema refuses: such a plan may hold a secret ([PLAN-4]), and no event may carry
     one ([SEC-1]).
  3. [PLAN-8] Before job.accepted it resolves each suite by its ref and version to the scripted target's cases; a
     suite whose digest the plan gives, and whose content does not have it, is refused (job.refused). [PLAN-3] It
     would resolve credentials before job.accepted too, but the scripted target has no process to give one to, so it
     resolves none: a value it does not need is a value it could leak.
  4. Otherwise it writes job.accepted, then plan.estimated (the suites' cases, at most the plan's cases limit), and
     runs the plan's suites in order against the scripted target, one run per suite (run.json names one suite,
     [RUN-8]): after each case, case.completed and spend.updated; when a suite's cases are done, it closes the run,
     seals it ([SEAL-5], sealedBy producer) and announces it (evidence.produced); at the end, job.sealed naming every
     run.
  5. [PLAN-9] Before each case it checks the plan's limits, in the order PLAN-2 lists them: maxUsd (the case's cost
     would take the spend above it, as STRM-3 reads spentUsd and as STRM-4 adds the runs' costs), cases (the plan's
     number of cases are complete), timeout (the case, and closing and sealing its run, would end after
     job.accepted's time plus the timeout). At the first limit it would pass it stops: it closes the run in progress
     as aborted (with the cases it completed, and no line for a case it did not run), seals and announces it, and
     ends with job.failed naming the limit and every run it sealed ([STRM-1]). Spend equal to maxUsd is within it.
  6. [PLAN-10] Every run carries the plan's subject (kind from the ref's kind: agent, workflow, model, endpoint or
     mcp-server, otherwise other), deployment and endpoint (a plan that names only an endpoint gives the deployment
     ref endpoint:<the endpoint, encoded as ENC-13 encodes a name>), suite (with its digest), judges and
     contentCapture, `targetMode` scripted, and `provenance`: the planId, planDigest, jobId and runnerId of the
     job.accepted ([RUN-12]).

The scripted target (SCRIPT below): every suite resolves to three cases, case-1 to case-3, each one result line at
path `check` (passed, passed, failed, in that order), costing $0.25 and taking 20 seconds; closing and sealing a run
takes one second. Every suite has the same three case names, so a case's id is prefixed with its suite, to be unique
within the job ([PLAN-8]): `<suite ref>@<version>/case-<n>`, and `<suite ref>@<version>#<position>/case-<n>`
(position 1-based in the plan's suites) for a suite the plan names more than once. A suite's content is its case ids
`<suite ref>@<version>/case-1` to `case-3`, each followed by LF; its digest is the SHA-256 of those bytes. The target
calls no model. Lane results (lane.completed) are not reported: a lane's status comes from its checkpoint's rule
(spec 05), which a plan does not carry.

It writes OUT/events.ndjson (the event stream, NDJSON, one event per line, each flushed as it happens) and one
folder per sealed run, OUT/runs/<runId>/ (run.json, results.ndjson, metrics.json, summary.json, seal.json), and
nothing else. It prints {"events", "jobId", "limit", "runs", "terminal"}. Exit status: 0 when it ran the job
(whatever its end: accepted or refused, sealed or failed), 2 with a message on standard error for a usage or input
error (nothing written): an unreadable PLAN, a RUNNER the reader schema refuses, an OUT that is not empty, a plan that
names no planId.

Not for production: the target is a script, and the runs say so.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import re
import sys
import time
from fractions import Fraction
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_produce  # noqa: E402  (result ids, the summary, the seal)
import aef_schema  # noqa: E402  (the reader and writer schemas)

AEF_VERSION = "1.0"
RUNNER_NAME = "aef-runner"
RUNNER_VERSION = "0.1.0"
SCHEMAS = TOOLS.parent / "1" / "schemas"

# ---------------------------------------------------------------------------- the scripted target
CASES_PER_SUITE = 3
SCRIPT = ("passed", "passed", "failed")  # the state of case n is SCRIPT[(n - 1) % 3]
CASE_USD = Fraction(1, 4)  # each case's cost, exact in binary64
CASE_SECONDS = 20  # each case's duration on the job's clock
CLOSE_SECONDS = 1  # closing and sealing a run
PRICE_TABLE = "aef-runner-scripted"
PATH = "check"
EVALUATOR = {"id": "scripted:check", "version": RUNNER_VERSION}
METRICS = {"schemaVersion": AEF_VERSION,
           "metrics": [{"id": "pass-rate", "kind": "rate", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]}
SUBJECT_KINDS = ("agent", "workflow", "model", "endpoint", "mcp-server")  # run.json subject.kind, besides other
TARGET_MODE = "scripted"  # the one target mode this runner gives (RUN-7)

# ENC-17
MAX_DOCUMENT = 4 * 1024 * 1024
MAX_DEPTH = 64

# VER-8: a plan value is known when the writer schema accepts it at its field.
KNOWN_AT = {
    "provider": "common#/$defs/provider",
    "isolation": "run-plan#/properties/isolation",
    "contentCapture": "run-plan#/properties/contentCapture",
    "targetMode": "run-plan#/properties/targetMode",
    "scheme": "run-plan#/properties/credentialRefs/items/properties/scheme",
    "purpose": "run-plan#/properties/credentialRefs/items/properties/purpose",
}


class InputError(Exception):
    """A usage or input error: no job is run (exit status 2)."""


_SCHEMAS = {}


def schemas(side):
    if side not in _SCHEMAS:
        _SCHEMAS[side] = aef_schema.load_schemas(SCHEMAS / side)
    return _SCHEMAS[side]


def _written(schema, document):
    """VER-2: what the runner writes is valid against the writer schema; anything else is a bug in this runner."""
    errors = schemas("writer").validate(schema, document)
    if errors:
        raise RuntimeError(f"aef_runner.py would write a {schema} document its writer schema refuses: {errors[0]}")


# ---------------------------------------------------------------------------- reading (spec 02)

def _depth(data):
    """ENC-17: the nesting depth of a JSON text, counted on its bytes: { and [ outside strings."""
    depth = deepest = 0
    in_string = escaped = False
    for byte in data:
        if in_string:
            if escaped:
                escaped = False
            elif byte == 0x5C:
                escaped = True
            elif byte == 0x22:
                in_string = False
        elif byte == 0x22:
            in_string = True
        elif byte in (0x7B, 0x5B):
            depth += 1
            deepest = max(deepest, depth)
        elif byte in (0x7D, 0x5D):
            depth -= 1
    return deepest


def _ijson(text, what):
    """ENC-2, ENC-3: no member twice, finite numbers, no unpaired surrogate."""
    def members(pairs):
        obj = {}
        for key, value in pairs:
            if key in obj:
                raise InputError(f"{what}: the member {key!r} appears twice ([ENC-2])")
            obj[key] = value
        return obj

    def constant(name):
        raise InputError(f"{what}: {name} is not a JSON number ([ENC-3])")

    def number(literal):
        value = float(literal)
        if not math.isfinite(value):
            raise InputError(f"{what}: {literal} overflows binary64 ([ENC-3])")
        return value

    def integer(literal):
        value = int(literal)
        try:
            float(value)
        except OverflowError:
            raise InputError(f"{what}: {literal[:20]}... overflows binary64 ([ENC-3])") from None
        return value

    try:
        value = json.loads(text, object_pairs_hook=members, parse_constant=constant, parse_float=number,
                           parse_int=integer)
    except ValueError as error:
        raise InputError(f"{what}: not a JSON text: {error}") from None
    if re.search("[\ud800-\udfff]", json.dumps(value, ensure_ascii=False)):
        raise InputError(f"{what}: an unpaired surrogate ([ENC-2])")
    return value


def read_document(path, what):
    """(the file's bytes, its top-level object), read as §2.1 and ENC-17 say."""
    try:
        data = Path(path).read_bytes()
    except OSError as error:
        raise InputError(f"{what}: {error}") from None
    if len(data) > MAX_DOCUMENT:
        raise InputError(f"{what}: larger than 4 MiB ([ENC-17])")
    if _depth(data) > MAX_DEPTH:
        raise InputError(f"{what}: nested deeper than 64 ([ENC-17])")
    if data.startswith(b"\xef\xbb\xbf"):
        raise InputError(f"{what}: a byte-order mark ([ENC-1])")
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise InputError(f"{what}: not UTF-8 ([ENC-1]): {error}") from None
    value = _ijson(text, what)
    if not isinstance(value, dict):
        raise InputError(f"{what}: the top-level value is not an object ([ENC-1])")
    return data, value


# ---------------------------------------------------------------------------- times and durations (ENC-8, ENC-9)

_TIME = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z")
_DURATION = re.compile(r"P(?:([0-9]{1,5})D)?(?:T(?:([0-9]{1,5})H)?(?:([0-9]{1,5})M)?)?")
EPOCH = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)


def parse_time(text):
    """ENC-8: (seconds since the epoch, nanoseconds) of an RFC 3339 UTC time in the years 0001 to 9999."""
    m = _TIME.fullmatch(text) if isinstance(text, str) else None
    if m and m.group(1) != "0000":
        try:
            moment = datetime.datetime(*(int(g) for g in m.groups()[:6]), tzinfo=datetime.timezone.utc)
        except ValueError:
            moment = None
        if moment is not None:
            delta = moment - EPOCH
            return delta.days * 86400 + delta.seconds, int((m.group(7) or "").ljust(9, "0"))
    raise InputError(f"{text!r} is not an RFC 3339 UTC time ([ENC-8])")


def format_time(instant):
    seconds, nanos = instant
    try:
        moment = EPOCH + datetime.timedelta(seconds=seconds)
    except OverflowError:
        raise InputError("the job's clock leaves the years 0001 to 9999 ([ENC-8])") from None
    text = (f"{moment.year:04d}-{moment.month:02d}-{moment.day:02d}"
            f"T{moment.hour:02d}:{moment.minute:02d}:{moment.second:02d}")
    if nanos:
        text += "." + f"{nanos:09d}".rstrip("0")
    return text + "Z"


def duration_seconds(text):
    """ENC-9: P[nD][T[nH][nM]], at least one part and no T without one."""
    m = _DURATION.fullmatch(text) if isinstance(text, str) else None
    if not m or not any(m.groups()) or ("T" in text and m.group(2) is None and m.group(3) is None):
        raise InputError(f"{text!r} is not a duration ([ENC-9])")
    days, hours, minutes = (int(g or 0) for g in m.groups())
    return days * 86400 + hours * 3600 + minutes * 60


class Clock:
    """The job's clock: fixed (it starts at --at and moves only when advanced) or the system's."""

    def __init__(self, at=None):
        self.fixed = at is not None
        self._now = parse_time(at) if self.fixed else self._system()

    @staticmethod
    def _system():
        return divmod(time.time_ns(), 1_000_000_000)

    def now(self):
        if not self.fixed:
            self._now = max(self._now, self._system())  # never earlier than a time already written
        return self._now

    def advance(self, seconds):
        """The scripted target's work takes `seconds`; on the system clock it takes what it takes."""
        if self.fixed:
            self._now = (self._now[0] + seconds, self._now[1])


def _plus(instant, seconds):
    return instant[0] + seconds, instant[1]


# ---------------------------------------------------------------------------- matching (spec 06 §6.3)

def unknown_values(plan):
    """PLAN-7, VER-8: the plan's values a runner must know and this version does not."""
    writer, found = schemas("writer"), []
    for field in ("provider", "isolation", "contentCapture", "targetMode"):
        value = plan.get(field, "live") if field == "targetMode" else plan.get(field)  # no targetMode asks for live
        if not writer.is_valid(KNOWN_AT[field], value):
            found.append(f"its {field} {json.dumps(value)} is not one this runner knows")
    for i, ref in enumerate(plan.get("credentialRefs") or []):
        for field in ("scheme", "purpose"):
            if not writer.is_valid(KNOWN_AT[field], ref.get(field)):  # named, not repeated: it is part of a credential
                found.append(f"credentialRefs[{i}].{field} is not one this runner knows")
    return found


def cannot_take(plan, runner):
    """PLAN-7: why the runner cannot take the plan (empty when it can)."""
    found = []
    missing = [tag for tag in plan.get("runnerSelector") or [] if tag not in runner["tags"]]
    if missing:
        found.append(f"the runner does not carry the runnerSelector tag(s) {', '.join(missing)}")
    if plan["provider"] not in runner["providers"]:
        found.append(f"the runner does not support the provider {plan['provider']} "
                     f"(it supports {', '.join(runner['providers'])})")
    if plan["isolation"] == "remote-zone" and runner.get("networkZone") != plan.get("zone"):
        found.append(f"the plan runs in the zone {plan.get('zone')}, and the runner's networkZone is "
                     f"{runner.get('networkZone', 'not set')}")
    mode = plan.get("targetMode", "live")  # a plan without one asks for live
    if mode != TARGET_MODE and schemas("writer").is_valid(KNOWN_AT["targetMode"], mode):
        found.append(f"the plan asks for targetMode {mode}, and this runner's target is {TARGET_MODE}: it cannot "
                     f"drive the target as asked")
    return found


def digest_mismatches(plan):
    """PLAN-8: the plan's suites whose digest is not that of the content they resolve to."""
    return [f"suites[{i}] resolves to content of digest {suite_digest(suite)}, not the plan's {suite['digest']}"
            for i, suite in enumerate(plan["suites"]) if "digest" in suite and suite["digest"] != suite_digest(suite)]


def refusal(plan, runner):
    """The reason the runner refuses the plan, or None when it takes it ([PLAN-7])."""
    version = plan.get("schemaVersion")
    if isinstance(version, str) and not version.startswith("1."):
        return f"the plan is AEF {version[:16]}: this runner reads major version 1 only ([VER-4])"
    errors = schemas("reader").validate("run-plan", plan)
    if errors:
        # where and which keyword, never the value: a plan the schema refuses may hold a secret (PLAN-4), and no
        # event may carry one (SEC-1)
        return (f"the plan is not valid against the run-plan schema ([VER-3]): at {errors[0].path or 'the plan'}, "
                f"{errors[0].keyword}")
    found = unknown_values(plan) + cannot_take(plan, runner)
    if found:
        return "the runner does not take this plan ([PLAN-7]): " + "; ".join(found)
    mismatches = digest_mismatches(plan)
    if mismatches:
        return "a suite is not the content the plan names ([PLAN-8]): " + "; ".join(mismatches)
    return None


# ---------------------------------------------------------------------------- the job (spec 06 §6.4)

def ref_name(text):
    """ENC-13: a name derived from free text, encoded so that two writers write the same bytes."""
    name = "".join(chr(b) if 0x21 <= b <= 0x7E and b != 0x25 else f"%{b:02X}" for b in text.encode("utf-8"))
    if not name:
        return "-"
    if name == "-":
        return "%2D"
    if len(name) > 256:
        return name[:239] + "~" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]
    return name


def case_id(suite, n, position=None):
    """PLAN-8: the id of the scripted target's case n of a suite, unique within the job: prefixed with the suite, and
    with its 1-based position among the plan's suites when given (a suite the plan names more than once)."""
    suite_name = f"{suite['ref']}@{suite['version']}" + (f"#{position}" if position is not None else "")
    text = f"{suite_name}/case-{n}"
    if len(text) > 256:  # a result's caseId is at most 256 characters
        key = f"{suite['ref']}\x1f{suite['version']}" + (f"\x1f{position}" if position is not None else "")
        text = "case-" + hashlib.sha256(key.encode("utf-8")).hexdigest()[:32] + f"-{n}"
    return text


def suite_digest(suite):
    """PLAN-8: the digest of a scripted suite's content: its case ids, each followed by LF."""
    content = "".join(case_id(suite, n) + "\n" for n in range(1, CASES_PER_SUITE + 1))
    return "sha256:" + hashlib.sha256(content.encode("utf-8")).hexdigest()


def job_case_ids(suites):
    """Per suite of the plan, its case ids: unique within the job, across suites (PLAN-8)."""
    named = {}
    for suite in suites:
        named[(suite["ref"], suite["version"])] = named.get((suite["ref"], suite["version"]), 0) + 1
    return [[case_id(suite, n, i if named[(suite["ref"], suite["version"])] > 1 else None)
             for n in range(1, CASES_PER_SUITE + 1)] for i, suite in enumerate(suites, start=1)]


def _usd(amount):
    """An exact amount as the binary64 number written for it ([ENC-4])."""
    return float(amount)


def _document(path, value):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(value, indent=2, ensure_ascii=False) + "\n")


class Stream:
    """The event stream: NDJSON (§2.2), one event per line, seq from 1, every line ended and flushed."""

    def __init__(self, path, job_id, clock):
        self._file = open(path, "w", encoding="utf-8", newline="\n")
        self.job_id, self.clock, self.seq = job_id, clock, 0

    def emit(self, kind, **fields):
        self.seq += 1
        event = {"schemaVersion": AEF_VERSION, "seq": self.seq, "kind": kind, "jobId": self.job_id,
                 "at": format_time(self.clock.now()), **fields}
        _written("runner-event", event)
        self._file.write(json.dumps(event, ensure_ascii=False, separators=(",", ":")) + "\n")
        self._file.flush()
        return event

    def close(self):
        self._file.close()


class Run:
    """A run in progress: one suite's cases."""

    def __init__(self, run_id, index, suite, started):
        self.run_id, self.index, self.suite, self.started = run_id, index, suite, started
        self.lines, self.cost = [], Fraction(0)


class Job:
    def __init__(self, out, plan, plan_digest, runner, clock, job_id):
        self.out, self.plan, self.plan_digest, self.runner, self.clock = out, plan, plan_digest, runner, clock
        self.job_id = job_id
        self.stream = Stream(out / "events.ndjson", job_id, clock)
        self.spent, self.done = Fraction(0), 0
        self.sealed, self.run_totals = [], []  # the runs sealed, and the cost each one's summary states

    # -- what a run says about the plan
    def _run_json(self, run, ended, abort_reason):
        plan, subject = self.plan, self.plan["subject"]
        kind = subject["ref"].split(":", 1)[0]
        doc = {"schemaVersion": AEF_VERSION, "runId": run.run_id, "status": "aborted" if abort_reason else "completed"}
        if abort_reason:
            doc["abortReason"] = abort_reason[:2048]
        doc["producer"] = {"name": RUNNER_NAME, "version": RUNNER_VERSION}
        doc["subject"] = {"ref": subject["ref"], "kind": kind if kind in SUBJECT_KINDS else "other",
                          "version": subject["version"]}
        if "deployment" in subject or "endpoint" in subject:
            # run.json's deployment needs a ref; a plan that names only an endpoint gets one derived from it
            doc["deployment"] = {"ref": subject.get("deployment") or "endpoint:" + ref_name(subject["endpoint"])}
            if "endpoint" in subject:
                doc["deployment"]["endpoint"] = subject["endpoint"]
        doc["suite"] = {k: run.suite[k] for k in ("ref", "version", "digest") if k in run.suite}
        judges = [{k: j[k] for k in ("model", "provider", "rubricDigest") if k in j} for j in plan.get("judges") or []]
        if judges:
            doc["judges"] = judges
        doc.update(startedAt=format_time(run.started), endedAt=format_time(ended),
                   contentCapture=plan["contentCapture"],
                   costPolicy={"maxUsd": plan["limits"]["maxUsd"], "priceTable": PRICE_TABLE},
                   execution={"targetMode": TARGET_MODE, "stimulus": "suite"},
                   provenance={"planId": plan["planId"], "planDigest": self.plan_digest, "jobId": self.job_id,
                               "runnerId": self.runner["runnerId"]})
        return doc

    def open(self, index, suite):
        return Run(f"{self.job_id}-run-{len(self.sealed) + 1}", index, suite, self.clock.now())

    def run_case(self, run, case, state):
        """The scripted target answers one case."""
        started = self.clock.now()
        self.clock.advance(CASE_SECONDS)
        line = {"schemaVersion": AEF_VERSION, "resultId": aef_produce.result_id(run.run_id, case, PATH),
                "caseId": case, "path": PATH, "evaluator": dict(EVALUATOR), "state": state}
        if state == "failed":
            line["severity"] = "medium"  # RES-9
        line.update(startedAt=format_time(started), endedAt=format_time(self.clock.now()))
        _written("result", line)
        run.lines.append(line)
        run.cost += CASE_USD
        self.spent += CASE_USD
        self.done += 1
        self.stream.emit("case.completed", caseId=case, state=state)
        self.stream.emit("spend.updated", spentUsd=_usd(self.spent))

    def close(self, run, abort_reason=None):
        """Closes the run (RUN-4: its final files), seals it (SEAL-5) and announces it (evidence.produced)."""
        doc = self._run_json(run, self.clock.now(), abort_reason)
        lane = run.suite.get("lane")
        request = {"lanes": [{"lane": lane, "metrics": [{"metric": "pass-rate", "path": PATH}]}] if lane else []}
        summary = aef_produce.summary_of(doc, METRICS, run.lines, request)
        summary["cost"] = {"totalUsd": _usd(run.cost), "source": f"{PRICE_TABLE}: $0.25 per case"}
        for schema, value in (("run", doc), ("metrics", METRICS), ("summary", summary)):
            _written(schema, value)
        folder = self.out / "runs" / run.run_id
        folder.mkdir(parents=True)
        _document(folder / "run.json", doc)
        with open(folder / "results.ndjson", "w", encoding="utf-8", newline="\n") as f:
            f.write("".join(json.dumps(line, ensure_ascii=False, separators=(",", ":")) + "\n" for line in run.lines))
        _document(folder / "metrics.json", METRICS)
        _document(folder / "summary.json", summary)
        self.clock.advance(CLOSE_SECONDS)
        sealed = aef_produce.seal_write(folder, "producer", format_time(self.clock.now()))
        self.stream.emit("evidence.produced", runId=run.run_id, runHash=sealed["runHash"])
        self.sealed.append(run.run_id)
        self.run_totals.append(_usd(run.cost))

    def limit_before(self, run, deadline, remaining):
        """PLAN-9: (the limit, why) when the next case would pass one of the plan's limits, checked in PLAN-2's order,
        or None. The timeout includes closing and sealing the case's run."""
        limits = self.plan["limits"]
        max_usd = limits["maxUsd"]
        spend = self.spent + CASE_USD
        totals = self.run_totals + [_usd((run.cost if run else 0) + CASE_USD)]
        # within the budget as the real spend, as STRM-3 reads spentUsd (binary64) and as STRM-4 adds the runs'
        # summary costs (exactly, then rounded once): spend equal to maxUsd is within it
        if not (spend <= Fraction(max_usd) and _usd(spend) <= max_usd
                and sum((Fraction(t) for t in totals), Fraction(0)) <= Fraction(max_usd)):
            return "maxUsd", (f"stopped before a case whose cost would take the spend from ${_usd(self.spent)} to "
                              f"${_usd(spend)}, above the plan's maxUsd of ${max_usd}; {remaining} case(s) not run")
        if "cases" in limits and self.done >= limits["cases"]:
            return "cases", (f"stopped after {self.done} case(s), the plan's cases limit; {remaining} case(s) not run")
        if deadline is not None and _plus(self.clock.now(), CASE_SECONDS + CLOSE_SECONDS) > deadline:
            return "timeout", (f"stopped before a case that would end after the plan's timeout of "
                               f"{limits['timeout']} from job.accepted; {remaining} case(s) not run")
        return None

    def execute(self, accepted_at):
        """Runs the plan's suites against the scripted target; returns (the terminal kind, the limit or None)."""
        limits = self.plan["limits"]
        ids = job_case_ids(self.plan["suites"])
        script = [(i, suite, n) for i, suite in enumerate(self.plan["suites"]) for n in range(1, CASES_PER_SUITE + 1)]
        # plan.estimated: the suites' cases, at most the plan's cases limit
        expected = len(script) if "cases" not in limits else min(len(script), limits["cases"])
        self.stream.emit("plan.estimated", cases=expected, usdLow=_usd(expected * CASE_USD),
                         usdHigh=_usd(expected * CASE_USD), priceTable=PRICE_TABLE)
        deadline = None
        if "timeout" in limits:
            deadline = _plus(accepted_at, duration_seconds(limits["timeout"]))
        run = None
        for k, (index, suite, n) in enumerate(script):
            if run is not None and run.index != index:
                self.close(run)
                run = None
            stop = self.limit_before(run, deadline, len(script) - k)
            if stop is not None:
                limit, reason = stop
                if run is not None:
                    self.close(run, f"the job stopped at the plan's {limit} limit: {reason}")
                self.stream.emit("job.failed", reason=reason, limit=limit, runs=list(self.sealed))
                return "job.failed", limit
            if run is None:
                run = self.open(index, suite)
            self.run_case(run, ids[index][n - 1], SCRIPT[(n - 1) % len(SCRIPT)])
        self.close(run)
        self.stream.emit("job.sealed", runs=list(self.sealed))
        return "job.sealed", None


def run(plan_path, runner_path, out_dir, target, at=None):
    """Runs one job; returns {"events", "jobId", "limit", "runs", "terminal"}."""
    if target != "scripted":
        raise InputError(f"--target {target}: this runner has one target, scripted")
    clock = Clock(at)
    plan_bytes, plan = read_document(plan_path, "the plan")
    plan_digest = hashlib.sha256(plan_bytes).hexdigest()  # PLAN-5: the plan file's exact bytes
    _, runner = read_document(runner_path, "the runner manifest")
    errors = schemas("reader").validate("runner", runner)
    if errors:
        raise InputError(f"the runner manifest is not valid against the runner schema: {errors[0]}")
    if not schemas("writer").is_valid("common#/$defs/id", plan.get("planId")):
        raise InputError("the plan names no planId a job.refused could carry: it is not a plan")
    out = Path(out_dir)
    if out.exists() and not (out.is_dir() and not any(out.iterdir())):
        raise InputError(f"{out_dir}: OUT is a folder that does not exist yet, or an empty one")

    started = clock.now()
    job_id = "job-" + hashlib.sha256(f"{plan_digest}\x1f{runner['runnerId']}\x1f{format_time(started)}"
                                     .encode("utf-8")).hexdigest()[:16]
    out.mkdir(parents=True, exist_ok=True)
    job = Job(out, plan, plan_digest, runner, clock, job_id)
    try:
        reason = refusal(plan, runner)
        who = {"planId": plan["planId"], "planDigest": plan_digest, "runnerId": runner["runnerId"]}
        if reason is not None:
            job.stream.emit("job.refused", **who, reason=reason[:2048])
            terminal, limit = "job.refused", None
        else:
            job.stream.emit("job.accepted", **who)
            terminal, limit = job.execute(started)
    finally:
        job.stream.close()
    return {"events": job.stream.seq, "jobId": job_id, "limit": limit, "runs": job.sealed, "terminal": terminal}


# ---------------------------------------------------------------------------- command line

def dispatch(argv):
    parser = argparse.ArgumentParser(prog="aef_runner.py", description=__doc__.split("\n\n")[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("run", help="run a plan against the scripted target")
    p.add_argument("plan")
    p.add_argument("runner")
    p.add_argument("out")
    p.add_argument("--target", required=True, choices=["scripted"])
    p.add_argument("--at", help="a fixed clock: the job starts at this RFC 3339 UTC time")
    a = parser.parse_args(argv)
    return run(a.plan, a.runner, a.out, a.target, a.at)


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    try:
        result = dispatch(argv[1:])
    except InputError as error:
        print(f"aef_runner.py: {error}", file=sys.stderr)
        return 2
    sys.stdout.write(json.dumps(result, ensure_ascii=False, sort_keys=True) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
