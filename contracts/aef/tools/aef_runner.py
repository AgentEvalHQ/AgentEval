#!/usr/bin/env python3
"""A minimal reference runner for AEF 1.0 (1/spec/06-runners.md), written from spec 06 and spec 09 §9.2.1 alone,
against a scripted target: a test fixture (TARGET) that stands for the subject with fixed answers, costs and
durations. It gives the target mode `scripted` only ([PLAN-7]), and every run it writes says so ([RUN-7]). Standard
library only; it writes runs, result ids, summaries and seals through aef_produce.py, and checks what it reads and
writes with aef_schema.py.

Usage:
  python aef_runner.py job PLAN RUNNER TARGET OUT [--at TIME]

The `job` operation of spec 09 §9.3, so `aef_conformance.py --command "python aef_runner.py" --kind job` runs the
corpus's job vectors through it as through any other implementation. PLAN is a run plan (schema run-plan); RUNNER the
capability manifest of the runner this process acts as (schema runner); TARGET the scripted target (§9.2.1); OUT a
folder that does not exist yet, or an empty one. TIME, an RFC 3339 UTC time ([ENC-8]), fixes the clock: the job
starts at TIME, and the clock moves only by a case's `seconds` and a run's `closeSeconds`, so the same inputs and TIME
give byte-identical output. Without --at the clock is the system's (UTC), never read backwards.

What it does:
  1. Reads PLAN, RUNNER and TARGET as a reader does: UTF-8 I-JSON objects within [ENC-17]'s limits (4 MiB, depth 64);
     PLAN and RUNNER against the reader schemas ([VER-3]), TARGET against the shape §9.2.1 gives it. planDigest is the
     SHA-256 of PLAN's exact bytes ([PLAN-5]).
  2. [PLAN-7] It takes the plan when it can take it (it carries every runnerSelector tag, supports the provider, gives
     the plan's target mode by its manifest's targetModes, live only when the manifest has none, and for a
     remote-zone plan has the plan's zone as its networkZone), knows the plan's provider, isolation, contentCapture,
     targetMode and every credential's scheme and purpose (known: what the writer schema accepts at that field,
     [VER-8]), and can run the job as the plan asks: its target is scripted and runs in this process, so a plan
     asking for another mode, or for the isolation container or remote-zone, is refused whatever the manifest says
     (spec 09 §9.2.1). Otherwise it writes one event, job.refused, with the reason, and stops. A
     plan the reader schema refuses is refused the same way, when it names a planId a job.refused can carry. A reason
     names where a plan is wrong, never a value from a credential or from a plan the schema refuses: such a plan may
     hold a secret ([PLAN-4]), and no event may carry one ([SEC-1]).
  3. [PLAN-3] Before job.accepted it resolves every credential, though the scripted target needs none: one of scheme
     env resolves when the variable its `path` names is set in this process's environment and not empty (its value
     is never written or printed, nor its path); keychain and vault it cannot resolve. A credential it cannot
     resolve is a refusal.
  4. [PLAN-8] Before job.accepted it resolves each suite by its ref and version to the target's suite of that ref and
     version; a suite the target does not have, a plan naming one suite twice, and a suite whose digest the plan
     gives and whose content does not have it (sha256: and the hex SHA-256 of the content's UTF-8 bytes) are each a
     refusal. Case ids are the target's, never rewritten.
  5. Otherwise it writes job.accepted, then plan.estimated (the suites' cases, at most the plan's cases limit; usdLow
     and usdHigh the sums of the `usd` and of the `usdBound` of the cases it counts, the first ones in run order), and
     runs the plan's suites in order, one run per suite (run.json names one suite, [RUN-8]), each suite's cases in the
     target's order: per case one result line, the case's root at path `check` with its state, the case's severity
     when it has one ([RES-9]), the lane the plan gives its suite ([PLAN-8]) and a reason on a typed absence
     ([RES-2]), then case.completed (naming its run) and spend.updated. When a suite's last case is run, it closes
     the run completed (its endedAt the end of that case), seals it ([SEAL-5], sealedBy producer, closeSeconds
     later) and announces it (evidence.produced); at the end, job.sealed naming every run. A run's metrics.json
     declares pass-rate, and its summary.json has the suite's lane, with pass-rate at check, or no lane.
  6. [PLAN-9] Before each case it checks the plan's limits in the order PLAN-2 lists them, against what the case could
     take: maxUsd (the spend so far plus the case's usdBound is above it, in either sum the verifiers will compute:
     the job's case costs added exactly and rounded once, as STRM-3 reads spentUsd, and the runs' cost.totalUsd, each
     rounded once, added exactly and rounded once, as STRM-4 adds them; the bound counts in the case's own run),
     cases (the plan's number of cases are complete), timeout (the time since job.accepted, plus the case's secondsBound, plus closeSeconds for closing
     and sealing its run, is above it). At the first limit it could pass it stops: it closes the run in progress as
     aborted (with the cases it completed, and no line for a case it did not run), seals and announces it, and ends
     with job.failed naming the limit and every run it sealed ([STRM-1]); a suite it stops before opens no run. Spend
     equal to maxUsd is within it.
  7. [PLAN-10] Every run carries the plan's subject (kind from the ref's kind: agent, workflow, model, endpoint or
     mcp-server, otherwise other), deployment and endpoint (a plan that names only an endpoint gives the deployment
     ref endpoint:<the endpoint, encoded as ENC-13 encodes a name>), suite (with the plan's digest, or none), judges and
     contentCapture, no judges (no model grades a scripted case, [RUN-9]), `targetMode` scripted, costPolicy (the plan's maxUsd, the target's priceTable), a summary whose
     cost.totalUsd is the sum of its cases' `usd`, and `provenance`: the planId, planDigest, jobId and runnerId of the
     job.accepted ([RUN-12]).
Sums of costs are computed exactly and written as the nearest binary64 ([SUM-5]). The target calls no model. Lane
results (lane.completed) are not reported: a lane's status comes from its checkpoint's rule (spec 05), which a plan
does not carry.

It writes OUT/events.ndjson (the event stream, NDJSON, one event per line, each flushed as it happens) and one
folder per sealed run, OUT/runs/<runId>/ (run.json, results.ndjson, metrics.json, summary.json, seal.json), and
nothing else. It prints {"events", "jobId", "limit", "runs", "terminal"}. Exit status: 0 when it ran the job
(whatever its end: accepted or refused, sealed or failed), 2 with a message on standard error for a usage or input
error (nothing written): an unreadable PLAN, a plan that names no planId, a RUNNER the reader schema refuses, a TARGET
not of §9.2.1's shape (a member it does not name among them), an OUT that is not empty, a TIME that is no time, a TIME
from which the clock, moved by every case's secondsBound and closeSeconds, would leave the years 0001 to 9999.

Not for production: the target is a fixture, and the runs say so.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import os
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
RUNNER_VERSION = "0.2.0"
SCHEMAS = TOOLS.parent / "1" / "schemas"

PATH = "check"  # spec 09 §9.2.1: a case's one result line is at this path
EVALUATOR = {"id": "scripted:check", "version": RUNNER_VERSION}
METRICS = {"schemaVersion": AEF_VERSION,
           "metrics": [{"id": "pass-rate", "kind": "rate", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]}
SUBJECT_KINDS = ("agent", "workflow", "model", "endpoint", "mcp-server")  # run.json subject.kind, besides other
TARGET_MODE = "scripted"  # the one target mode this runner gives (RUN-7)
SEVERE = ("failed", "warn")  # RES-9: a case in these states has a severity, and no other
TARGET_MEMBERS = {"suites", "closeSeconds", "priceTable"}  # spec 09 §9.2.1: a target names these and no other
SUITE_MEMBERS = {"ref", "version", "content", "cases"}
CASE_MEMBERS = {"caseId", "state", "severity", "usd", "usdBound", "seconds", "secondsBound"}
METRIC = "pass-rate"  # §9.2.1: the metric a run declares, and its lane's summary entry at PATH
TYPED_ABSENCES = ("not_measured", "not_applicable", "skipped", "error")  # RES-2: a line in these states says why

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

# ---------------------------------------------------------------------------- mutations (for --self-check)
# Each name breaks the runner the way a plausible implementation would, so aef_conformance.py --self-check can show
# that the job vectors notice.
MUTATIONS: set[str] = set()
KNOWN_MUTATIONS = {
    "cost-bound": "maxUsd is checked with the case's cost, not its cost bound (PLAN-9, R7-2)",
    "time-bound": "the timeout is checked with the case's duration, not its time bound (PLAN-9, R7-2)",
    "case-prefix": "case ids are prefixed with their suite's ref and version (PLAN-8, R7-3)",
    "credentials-unresolved": "credentials are not resolved before job.accepted (PLAN-3, R7-2)",
    "suite-twice": "a plan that names one suite twice is run, one run per entry, not refused (PLAN-8)",
    "budget-exact": "maxUsd is checked against the exact spend plus the bound, not the sums the verifiers round "
                    "(PLAN-9, R7R-3)",
    "budget-job-only": "maxUsd is checked against the job's spend only, not the sum of the runs' costs (PLAN-9, R7R-3)",
    "severity-medium": "every failed or warn line carries severity medium, whatever the case's (R7R-5)",
    "lane-unnamed": "a run's lines do not name the lane its suite serves (PLAN-8, R7R-6)",
    "digest-resolved": "a run whose plan suite has no digest carries the digest of the content resolved (PLAN-8, R7R-4)",
    "env-empty-set": "an env credential whose variable is empty is resolved (PLAN-3, R7R-7)",
    "isolation-any": "a plan asking for container or remote-zone isolation is taken (R7R-8)",
    "judges-copied": "each run names the plan's judges, though no model graded a scripted case (R8-2)",
    "clock-unchecked": "the clock's range is not checked before the job: it fails mid-job with files written (R8-5)",
    "members-ignored": "a target member spec 09 §9.2.1 does not name is ignored, not an input error (R7R-9)",
}


def set_mutations(names):
    """Breaks the named behaviours (an empty set restores them)."""
    unknown = set(names) - set(KNOWN_MUTATIONS)
    if unknown:
        raise ValueError(f"unknown mutation(s): {', '.join(sorted(unknown))}")
    MUTATIONS.clear()
    MUTATIONS.update(names)


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


# ---------------------------------------------------------------------------- the scripted target (spec 09 §9.2.1)

def _whole(value):
    """A whole number of seconds, 0 or more (an integral JSON number), or None."""
    if isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0 or value != int(value):
        return None
    return int(value)


def _amount(value):
    """A cost in US dollars, 0 or more, or None."""
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and value >= 0 else None


def target_problem(target):
    """Why TARGET is not a scripted target of §9.2.1's shape, or None."""
    writer = schemas("writer")
    if set(target) - TARGET_MEMBERS and "members-ignored" not in MUTATIONS:
        return f"a member it does not name: {sorted(set(target) - TARGET_MEMBERS)[0]}"
    suites = target.get("suites")
    if not isinstance(suites, list):
        return "suites is not a list"
    if _whole(target.get("closeSeconds")) is None:
        return "closeSeconds is not a whole number of seconds"
    price = target.get("priceTable")
    if not isinstance(price, str) or not 1 <= len(price) <= 64:
        return "priceTable is not a name of 1 to 64 characters"
    named = set()
    for i, suite in enumerate(suites):
        where = f"suites[{i}]"
        if not isinstance(suite, dict) or not all(isinstance(suite.get(k), str) for k in ("ref", "version", "content")):
            return f"{where} has no ref, version and content, each a string"
        if set(suite) - SUITE_MEMBERS and "members-ignored" not in MUTATIONS:
            return f"{where} has a member a suite does not name: {sorted(set(suite) - SUITE_MEMBERS)[0]}"
        if (suite["ref"], suite["version"]) in named:
            return f"{where} has the ref and version of an earlier suite"
        named.add((suite["ref"], suite["version"]))
        cases = suite.get("cases")
        if not isinstance(cases, list) or not cases:
            return f"{where}.cases is not a list of one or more cases"
        ids = set()
        for k, case in enumerate(cases):
            at = f"{where}.cases[{k}]"
            if not isinstance(case, dict):
                return f"{at} is not an object"
            if set(case) - CASE_MEMBERS and "members-ignored" not in MUTATIONS:
                return f"{at} has a member a case does not name: {sorted(set(case) - CASE_MEMBERS)[0]}"
            case_id = case.get("caseId")
            if not (isinstance(case_id, str) and 1 <= len(case_id) <= 256
                    and writer.is_valid("common#/$defs/text", case_id)):
                return f"{at}.caseId is not a case id (1 to 256 characters, no control character)"
            if case_id in ids:
                return f"{at}.caseId is an earlier case's"
            ids.add(case_id)
            # RES-1, RES-3: a state of a closed run's line
            if case.get("state") == "pending" or not writer.is_valid("common#/$defs/state", case.get("state")):
                return f"{at}.state is not a result state of a closed run"
            if (case["state"] in SEVERE) != ("severity" in case):
                return f"{at}: a case has a severity exactly when its state is failed or warn ([RES-9])"
            if "severity" in case and not writer.is_valid("common#/$defs/severity", case["severity"]):
                return f"{at}.severity is not a severity"
            usd, bound = _amount(case.get("usd")), _amount(case.get("usdBound"))
            if usd is None or bound is None or Fraction(bound) < Fraction(usd):
                return f"{at}: usd and usdBound are not costs with usdBound not below usd"
            seconds, limit = _whole(case.get("seconds")), _whole(case.get("secondsBound"))
            if seconds is None or limit is None or limit < seconds:
                return f"{at}: seconds and secondsBound are not whole seconds with secondsBound not below seconds"
    return None


def read_target(path):
    _, target = read_document(path, "the scripted target")
    problem = target_problem(target)
    if problem is not None:
        raise InputError(f"the scripted target: {problem} (spec 09 §9.2.1)")
    return target


def suite_digest(content):
    """PLAN-8: the digest of a scripted suite: sha256: and the hex SHA-256 of its content's UTF-8 bytes."""
    return "sha256:" + hashlib.sha256(content.encode("utf-8")).hexdigest()


def target_suite(target, suite):
    """PLAN-8: the target's suite of the plan suite's ref and version, or None."""
    return next((s for s in target["suites"] if (s["ref"], s["version"]) == (suite["ref"], suite["version"])), None)


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
        """The target's work takes `seconds` (spec 09 §9.2.1); on the system clock it takes what it takes."""
        if self.fixed:
            self._now = (self._now[0] + seconds, self._now[1])


def _plus(instant, seconds):
    return instant[0] + seconds, instant[1]


# ---------------------------------------------------------------------------- matching and refusal (spec 06 §6.3)

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
    """PLAN-7: why the runner cannot take the plan, or cannot drive the target as it asks (empty when it can)."""
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
    if plan["isolation"] != "process" and "isolation-any" not in MUTATIONS:
        found.append(f"the plan asks for the isolation {plan['isolation']}, and this runner's target runs in its own "
                     f"process: it gives the isolation process only")
    mode = plan.get("targetMode", "live")  # a plan without one asks for live
    if schemas("writer").is_valid(KNOWN_AT["targetMode"], mode):
        modes = runner.get("targetModes") or ["live"]  # a manifest without them gives live only (PLAN-6)
        if mode not in modes:
            found.append(f"the plan asks for targetMode {mode}, which the runner's manifest does not give "
                         f"(it gives {', '.join(modes)})")
        if mode != TARGET_MODE:
            found.append(f"the plan asks for targetMode {mode}, and this runner's target is {TARGET_MODE}: it cannot "
                         f"drive the target as asked")
    return found


def unresolved_credentials(plan):
    """PLAN-3: the credentials this runner cannot resolve where it runs, each named by its place in the plan only:
    neither its value nor its path is written ([PLAN-4], [SEC-1])."""
    if "credentials-unresolved" in MUTATIONS:
        return []
    found = []
    for i, ref in enumerate(plan.get("credentialRefs") or []):
        if ref["scheme"] == "env":
            if not os.environ.get(ref["path"]) and not ("env-empty-set" in MUTATIONS and ref["path"] in os.environ):
                found.append(f"credentialRefs[{i}] (scheme env) is not set in this runner's environment, or is empty")
        else:
            found.append(f"credentialRefs[{i}] (scheme {ref['scheme']}) is of a scheme this runner cannot resolve")
    return found


def suite_problems(plan, target):
    """PLAN-8: the plan's suites this runner cannot resolve to the target's content, or names twice."""
    found, seen = [], {}
    for i, suite in enumerate(plan["suites"]):
        key = (suite["ref"], suite["version"])
        if key in seen and "suite-twice" not in MUTATIONS:
            found.append(f"suites[{i}] names the suite of suites[{seen[key]}] again: a job runs a suite's cases once")
        seen.setdefault(key, i)
        resolved = target_suite(target, suite)
        if resolved is None:
            found.append(f"suites[{i}] ({suite['ref']} version {suite['version']}) is not a suite this runner's "
                         f"target has")
        elif "digest" in suite and suite["digest"] != suite_digest(resolved["content"]):
            found.append(f"suites[{i}] resolves to content of digest {suite_digest(resolved['content'])}, not the "
                         f"plan's {suite['digest']}")
    return found


def refusal(plan, runner, target):
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
    found = unresolved_credentials(plan)
    if found:
        return "a credential cannot be resolved ([PLAN-3]): " + "; ".join(found)
    found = suite_problems(plan, target)
    if found:
        return "a suite cannot be run as the plan names it ([PLAN-8]): " + "; ".join(found)
    return None


# ---------------------------------------------------------------------------- the job (spec 06 §6.4, §6.5)

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


def _usd(amount):
    """An exact amount as the binary64 number written for it ([ENC-4]): computed exactly, rounded once."""
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
    def __init__(self, out, plan, plan_digest, runner, target, clock, job_id):
        self.out, self.plan, self.plan_digest, self.runner, self.clock = out, plan, plan_digest, runner, clock
        self.target, self.job_id = target, job_id
        self.close_seconds = _whole(target["closeSeconds"])
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
        doc["suite"] = {k: run.suite[k] for k in ("ref", "version", "digest") if k in run.suite}  # the plan's digest, or none
        if "digest-resolved" in MUTATIONS and "digest" not in doc["suite"]:
            doc["suite"]["digest"] = suite_digest(target_suite(self.target, run.suite)["content"])
        # RUN-9: a run's judges are the models that graded it; the scripted target grades with none (spec 09 §9.2.1)
        judges = [{k: j[k] for k in ("model", "provider", "rubricDigest") if k in j} for j in plan.get("judges") or []]
        if judges and "judges-copied" in MUTATIONS:
            doc["judges"] = judges
        doc.update(startedAt=format_time(run.started), endedAt=format_time(ended),
                   contentCapture=plan["contentCapture"],
                   costPolicy={"maxUsd": plan["limits"]["maxUsd"], "priceTable": self.target["priceTable"]},
                   execution={"targetMode": TARGET_MODE, "stimulus": "suite"},
                   provenance={"planId": plan["planId"], "planDigest": self.plan_digest, "jobId": self.job_id,
                               "runnerId": self.runner["runnerId"]})
        return doc

    def open(self, index, suite):
        return Run(f"{self.job_id}-run-{len(self.sealed) + 1}", index, suite, self.clock.now())

    def run_case(self, run, case):
        """The target answers one case: one result line, the case's root at path `check` (spec 09 §9.2.1)."""
        case_id, state = case["caseId"], case["state"]
        if "case-prefix" in MUTATIONS:
            case_id = f"{run.suite['ref']}@{run.suite['version']}/{case_id}"
        started = self.clock.now()
        self.clock.advance(_whole(case["seconds"]))
        line = {"schemaVersion": AEF_VERSION, "resultId": aef_produce.result_id(run.run_id, case_id, PATH),
                "caseId": case_id, "path": PATH, "evaluator": dict(EVALUATOR), "state": state}
        if "severity" in case:
            line["severity"] = "medium" if "severity-medium" in MUTATIONS else case["severity"]  # RES-9: the target's
        if "lane" in run.suite and "lane-unnamed" not in MUTATIONS:
            line["lane"] = run.suite["lane"]  # PLAN-8: the run serves its suite's lane
        if state in TYPED_ABSENCES:
            line["reason"] = f"the scripted target answers {state} for this case"  # RES-2
        line.update(startedAt=format_time(started), endedAt=format_time(self.clock.now()))
        _written("result", line)
        run.lines.append(line)
        run.cost += Fraction(case["usd"])
        self.spent += Fraction(case["usd"])
        self.done += 1
        self.stream.emit("case.completed", caseId=case_id, state=state, runId=run.run_id)
        self.stream.emit("spend.updated", spentUsd=_usd(self.spent))

    def close(self, run, abort_reason=None):
        """Closes the run (RUN-4: its final files), seals it (SEAL-5) and announces it (evidence.produced)."""
        doc = self._run_json(run, self.clock.now(), abort_reason)
        lane = run.suite.get("lane")
        request = {"lanes": [{"lane": lane, "metrics": [{"metric": METRIC, "path": PATH}]}] if lane else []}
        summary = aef_produce.summary_of(doc, METRICS, run.lines, request)
        summary["cost"] = {"totalUsd": _usd(run.cost), "source": f"{self.target['priceTable']}: the scripted target's "
                                                                  f"cost per case"[:256]}
        for schema, value in (("run", doc), ("metrics", METRICS), ("summary", summary)):
            _written(schema, value)
        folder = self.out / "runs" / run.run_id
        folder.mkdir(parents=True)
        _document(folder / "run.json", doc)
        with open(folder / "results.ndjson", "w", encoding="utf-8", newline="\n") as f:
            f.write("".join(json.dumps(line, ensure_ascii=False, separators=(",", ":")) + "\n" for line in run.lines))
        _document(folder / "metrics.json", METRICS)
        _document(folder / "summary.json", summary)
        self.clock.advance(self.close_seconds)
        sealed = aef_produce.seal_write(folder, "producer", format_time(self.clock.now()))
        self.stream.emit("evidence.produced", runId=run.run_id, runHash=sealed["runHash"])
        self.sealed.append(run.run_id)
        self.run_totals.append(_usd(run.cost))

    def limit_before(self, run, deadline, case, remaining):
        """PLAN-9: (the limit, why) when the next case could pass one of the plan's limits, checked in PLAN-2's order
        against its bounds, or None. The timeout includes closing and sealing the case's run."""
        limits = self.plan["limits"]
        max_usd = limits["maxUsd"]
        bound = Fraction(case["usd"] if "cost-bound" in MUTATIONS else case["usdBound"])
        # PLAN-9: the spend as STRM-3 and STRM-4 will compute it, the bound counted in the case's own run: the job's
        # case costs added exactly and rounded once (spentUsd), and the runs' costs, each rounded once, added exactly
        # and rounded once (cost.totalUsd). Either above maxUsd stops the job; equal is within it.
        spend = _usd(self.spent + bound)
        runs = _usd(sum((Fraction(t) for t in self.run_totals + [_usd((run.cost if run else 0) + bound)]), Fraction(0)))
        if "budget-exact" in MUTATIONS:
            over = self.spent + bound > Fraction(max_usd)
        elif "budget-job-only" in MUTATIONS:
            over = spend > max_usd
        else:
            over = spend > max_usd or runs > max_usd
        if over:
            return "maxUsd", (f"stopped before a case whose cost bound would take the spend to ${spend} (the runs' "
                              f"costs to ${runs}), above the plan's maxUsd of ${max_usd}; {remaining} case(s) not run")
        if "cases" in limits and self.done >= limits["cases"]:
            return "cases", (f"stopped after {self.done} case(s), the plan's cases limit; {remaining} case(s) not run")
        seconds = _whole(case["seconds"] if "time-bound" in MUTATIONS else case["secondsBound"])
        if deadline is not None and _plus(self.clock.now(), seconds + self.close_seconds) > deadline:
            return "timeout", (f"stopped before a case whose time bound, with closing and sealing its run, would end "
                               f"after the plan's timeout of {limits['timeout']} from job.accepted; {remaining} "
                               f"case(s) not run")
        return None

    def execute(self, accepted_at):
        """Runs the plan's suites against the target; returns (the terminal kind, the limit or None)."""
        limits = self.plan["limits"]
        script = [(i, suite, case) for i, suite in enumerate(self.plan["suites"])
                  for case in target_suite(self.target, suite)["cases"]]
        # plan.estimated: the suites' cases, at most the plan's cases limit; the cost range of the cases it counts
        counted = script if "cases" not in limits else script[:limits["cases"]]
        self.stream.emit("plan.estimated", cases=len(counted),
                         usdLow=_usd(sum((Fraction(c["usd"]) for _, _, c in counted), Fraction(0))),
                         usdHigh=_usd(sum((Fraction(c["usdBound"]) for _, _, c in counted), Fraction(0))),
                         priceTable=self.target["priceTable"])
        deadline = None
        if "timeout" in limits:
            deadline = _plus(accepted_at, duration_seconds(limits["timeout"]))
        run = None
        for k, (index, suite, case) in enumerate(script):
            if run is not None and run.index != index:  # the suite's last case is run: its run is complete
                self.close(run)
                run = None
            stop = self.limit_before(run, deadline, case, len(script) - k)
            if stop is not None:
                limit, reason = stop
                if run is not None:
                    self.close(run, f"the job stopped at the plan's {limit} limit: {reason}")
                self.stream.emit("job.failed", reason=reason, limit=limit, runs=list(self.sealed))
                return "job.failed", limit
            if run is None:
                run = self.open(index, suite)
            self.run_case(run, case)
        self.close(run)
        self.stream.emit("job.sealed", runs=list(self.sealed))
        return "job.sealed", None


def job(plan_path, runner_path, target_path, out_dir, at=None):
    """Runs one job; returns {"events", "jobId", "limit", "runs", "terminal"}."""
    clock = Clock(at)
    plan_bytes, plan = read_document(plan_path, "the plan")
    plan_digest = hashlib.sha256(plan_bytes).hexdigest()  # PLAN-5: the plan file's exact bytes
    _, runner = read_document(runner_path, "the runner manifest")
    errors = schemas("reader").validate("runner", runner)
    if errors:
        raise InputError(f"the runner manifest is not valid against the runner schema: {errors[0]}")
    target = read_target(target_path)
    if clock.fixed and "clock-unchecked" not in MUTATIONS:  # spec 09 §9.3: the clock, moved as far as the target could move it, stays within ENC-8's years
        furthest = sum(_whole(c["secondsBound"]) for s in target["suites"] for c in s["cases"])
        furthest += _whole(target["closeSeconds"]) * len(target["suites"])
        try:
            format_time(_plus(clock.now(), furthest))  # beyond 9999-12-31: InputError
        except InputError:
            raise InputError(f"--at {at}: the job's clock, moved by every case's secondsBound and closeSeconds, would "
                             f"leave the years 0001 to 9999 ([ENC-8])") from None
    if not schemas("writer").is_valid("common#/$defs/id", plan.get("planId")):
        raise InputError("the plan names no planId a job.refused could carry: it is not a plan")
    out = Path(out_dir)
    if out.exists() and not (out.is_dir() and not any(out.iterdir())):
        raise InputError(f"{out_dir}: OUT is a folder that does not exist yet, or an empty one")

    started = clock.now()
    job_id = "job-" + hashlib.sha256(f"{plan_digest}\x1f{runner['runnerId']}\x1f{format_time(started)}"
                                     .encode("utf-8")).hexdigest()[:16]
    out.mkdir(parents=True, exist_ok=True)
    work = Job(out, plan, plan_digest, runner, target, clock, job_id)
    try:
        reason = refusal(plan, runner, target)
        who = {"planId": plan["planId"], "planDigest": plan_digest, "runnerId": runner["runnerId"]}
        if reason is not None:
            work.stream.emit("job.refused", **who, reason=reason[:2048])
            terminal, limit = "job.refused", None
        else:
            work.stream.emit("job.accepted", **who)
            terminal, limit = work.execute(started)
    finally:
        work.stream.close()
    return {"events": work.stream.seq, "jobId": job_id, "limit": limit, "runs": work.sealed, "terminal": terminal}


# ---------------------------------------------------------------------------- command line

OPERATIONS = ("job",)  # spec 09 §9.3


def dispatch(argv):
    parser = argparse.ArgumentParser(prog="aef_runner.py", description=__doc__.split("\n\n")[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("job", help="run a plan against a scripted target (spec 09 §9.3)")
    p.add_argument("plan")
    p.add_argument("runner")
    p.add_argument("target")
    p.add_argument("out")
    p.add_argument("--at", help="a fixed clock: the job starts at this RFC 3339 UTC time")
    a = parser.parse_args(argv)
    return job(a.plan, a.runner, a.target, a.out, a.at)


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
