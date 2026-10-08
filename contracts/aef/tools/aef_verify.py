#!/usr/bin/env python3
"""The AEF 1.0 reference verifier, written from the specification (1/spec/) alone. Standard library only.

It is a library (one function per operation, each returning the JSON value it prints) and a command line with one
command per operation. This command line is the cross-language contract of spec 09 §9.3: a conformance runner
(tools/aef_conformance.py) drives any implementation that offers the same commands, takes input paths as arguments,
prints its result as one JSON value on standard output and exits 0 when it could perform the operation (whatever the
verdict). A usage error exits 2 with a message on standard error. JSON is printed UTF-8, sorted keys, no ASCII escaping.

Problems are always [path, code] pairs, ordered by path and then by code as spec 03 §3.9 orders them (paths by their
UTF-8 bytes, except that the `<file>:<line>` paths of one file go by line number as a number), except the manifest
problems of CKP-7, which are codes alone in code order.

Commands (the JSON each prints):

  run DIR [--policy POLICY] [--anchors ANCHORS]
      The run verifier (spec 03 §3.9, spec 02, spec 04 §4.1 and §4.5).
      {"outcome": "invalid" | "unsealed" | "intact", "problems": [[path, code], ...]}
      With --policy (a trust policy file), also "signedBy": the identities attestation.dsse.json (over seal.json)
      verifies for, in policy order; empty unless the run is intact. With --anchors (a file holding a JSON list of
      trusted run hashes), also "anchored": whether the run is intact and its run hash is in the list.
  seal DIR
      §4.1 alone. {"manifest": "<manifest text of the files present>", "runHash": hex, "problems": [...]}
  chain DIR
      §4.2 (OVL-5) alone. {"problems": [...]}
  view DIR --at TIME
      The effective view of §4.3, in the shape of spec 09 §9.2.1:
      {"results": [...], "reviews": [...], "waivers": [...], "withheld": [...], "unsealedEvents": n}
  checkpoint FILE
      Schema verdicts and [CKP-7]. {"writer": "valid"|"invalid", "reader": "valid"|"invalid",
      "problems": [code, ...]}; "problems" is null when the reader refuses the manifest.
  lanes CHECKPOINT --runs DIR [--at TIME]
      §5.3 and [CKP-8]. {"lanes": [{"lane": name, "result": {...} | null}, ...], "problems": [...]}
      --at is the time of verification, used as oldestClosedAt for an undecided checkpoint whose runs have no endedAt
      (LANE-9); it defaults to the current time.
  signature ENVELOPE FILE POLICY [--payload-type TYPE]
      §4.4. {"envelopeResult": null | "malformed" | "payload-mismatch",
      "signatures": [{"keyid", "result", "identity"?}, ...], "verifiesFor": [identity, ...]}
      Without --payload-type, the type SIG-1 gives the file is inferred from its content (an in-toto Statement or a
      checkpoint manifest).
  paths JSONFILE
      [RUN-3] over a list of paths. The file holds a JSON array of strings: {"problems": [...]}. It may also hold the
      corpus form (a list of {"name", "paths", ...}): then [{"name": name, "problems": [...]}, ...].
  result-id RUNID CASEID PATH [TRIAL]
      [RES-4]. {"resultId": "r_..."}. TRIAL is a JSON number (an integral one: 3 and 3.0 are trial 3).
  document SCHEMA FILE
      Schema verdicts for one document (NDJSON: every line) and the §7.3 readings of the values it holds:
      {"writer": ..., "reader": ..., "reads": {"field.path[i]": value as read, ...}}
  decide FILE
      The decision function (§5.4, through aef_decide.py) on a decision input, or on a decision vector's "input":
      {"output": {...}} or {"error": message}.
  match PLAN RUNNER
      [PLAN-7] (through aef_stream.py). {"matches": true|false}
  stream EVENTS PLAN
      [STRM-3] (through aef_stream.py) against the plan's bytes. {"problems": [[where, problem], ...]}
  conform EVENTS PLAN RUNS [--policy POLICY]
      [STRM-4], written here from spec 06 §6.4 (not through aef_stream.py): the runs the stream's job.sealed and
      job.failed events name, found in the folder RUNS, against the plan. {"problems": [[path, code], ...]}, at
      'run:<runId>' and 'job'. The policy authorizes redactions (OVL-10).

Times are RFC 3339 UTC strings (ENC-8). Exit status: 0 when the operation ran, 2 on a usage or input error.
"""
from __future__ import annotations

import argparse
import base64
import datetime
import functools
import hashlib
import json
import math
import os
import re
import sys
from collections import Counter, defaultdict
from fractions import Fraction
from math import comb
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_crypto  # noqa: E402
import aef_decide  # noqa: E402
import aef_schema  # noqa: E402
import aef_stream  # noqa: E402

AEF_ROOT = TOOLS.parent / "1"

# ---------------------------------------------------------------------------- mutations (for --self-check)
# Each name switches off one check, so a conformance runner can show that the corpus notices.
MUTATIONS: set[str] = set()
KNOWN_MUTATIONS = {
    "seal-digest": "seal verification does not compare file digests",
    "manifest-order": "the manifest orders paths segment by segment instead of by UTF-8 bytes",
    "ijson-duplicates": "a member named twice is accepted (the last wins) instead of refused",
    "pattern-dollar": "a pattern's final $ also matches before a final newline (Python's default)",
    "summary": "summary.json is not recomputed from results.ndjson",
    "overlay-run-hash": "an overlay batch's runHash is not compared with the run hash",
    "line-order": "problems are ordered by the bytes of their path only, so results.ndjson:10 comes before :9",
    "anchors": "a run is reported anchored when intact, whatever the list of trusted run hashes holds",
    "untrusted-key": "a signature by a key the policy does not list counts as verified (for its key id)",
    "subject-binding": "eligibility ignores the run's subject, deployment and suite (LANE-1's binding)",
    "severity-evidence": "a severity lane passes without evidence: undecided lines and minimumN are ignored",
    "redaction-authorization": "any redact event in a verified batch withholds its blob, signed or not",
}
_ORIGINAL_COMPILE = aef_schema.compile_pattern


@functools.lru_cache(maxsize=None)
def _python_dollar(pattern):
    return re.compile(aef_schema.translate_pattern(pattern).replace("\\Z", "$"))


def set_mutations(names):
    """Switches the named checks off (an empty set restores every check)."""
    unknown = set(names) - set(KNOWN_MUTATIONS)
    if unknown:
        raise ValueError(f"unknown mutation(s): {', '.join(sorted(unknown))}")
    MUTATIONS.clear()
    MUTATIONS.update(names)
    aef_schema.compile_pattern = _python_dollar if "pattern-dollar" in MUTATIONS else _ORIGINAL_COMPILE


# ---------------------------------------------------------------------------- constants

MAX_JSON = 4 * 1024 * 1024  # ENC-17: a JSON file or one NDJSON line
MAX_DEPTH = 64
MAX_LINES = 1_000_000
MAX_FILES = 100_000
MAX_BLOB = 1 << 30
MAX_PATH = 255

STATES = {"passed", "failed", "warn", "inconclusive", "scored", "not_measured", "not_applicable", "skipped", "error",
          "pending"}  # closed (VER-9)
ABSENT_STATES = {"not_measured", "skipped", "error", "pending"}  # SUM-4: not measured (not_applicable is left out)
SEVERITY_ORDER = {"none": 0, "low": 1, "medium": 2, "high": 3, "critical": 4}
METRIC_KINDS = {"score", "rate", "count", "duration", "cost", "verdict"}
DIRECTIONS = {"higher_better", "lower_better", "none"}
RUN_STATUSES = {"running", "completed", "aborted"}
TARGET_MODES = {"live", "replayed", "scripted", "mocked"}
GATE_OUTCOMES = {"ship", "no_ship", "inconclusive"}
COMPARABILITY = {"comparable", "incomparable", "not_applicable"}
OVERLAY_KINDS = {"approve", "reject", "override", "adjudicate", "waive", "acknowledge", "accept_baseline",
                 "annotate", "redact"}
CHECKPOINT_STATES = {"draft", "planned", "approved_to_spend", "running", "evidence_complete", "decided"}
CHECKPOINT_OUTCOMES = {"approved", "approved_with_exceptions", "blocked", "inconclusive", "expired", "aborted"}
DECISION_OUTCOMES = {"approved", "approved_with_exceptions", "blocked", "inconclusive", "expired"}
DECISION_LANE_STATUSES = {"passed", "failed", "waived", "missing", "not_measured", "incomparable", "stale"}
INPUT_STATUSES = {"passed", "failed", "not_measured", "incomparable"}
RULE_KINDS = {"threshold", "severity", "comparison", "evidence-present"}
AXES = ("subject", "suite", "suite-content", "judges", "rubrics", "target-mode", "deployment", "producer")
IN_TOTO_TYPE = "application/vnd.in-toto+json"
CHECKPOINT_TYPE = "application/vnd.agenteval.aef.checkpoint+json"

RUN_JSON = {"run.json": "run", "metrics.json": "metrics", "summary.json": "summary"}
RUN_NDJSON = {"results.ndjson": "result", "evidence.ndjson": "evidence", "gates.ndjson": "gate-decision",
              "traces.otlp.jsonl": None, "logs.otlp.jsonl": None}
CONTENT_KINDS = {"judge_reasoning", "tool_call", "document", "input", "expected", "output", "transcript"}  # RUN-11
MAX_SAFE_INTEGER = 2 ** 53 - 1  # ENC-4
EVENTS = "overlays/events.ndjson"
BATCH_SEAL = re.compile(r"overlays/seal-([0-9]{4})\.json")
BATCH_SIGNATURE = re.compile(r"overlays/seal-([0-9]{4})\.dsse\.json")
BLOB_PATH = re.compile(r"blobs/sha256/([0-9a-f]{2})/([0-9a-f]{64})")
_MISSING = object()


class InputError(Exception):
    """An input the operation cannot work on at all (exit status 2)."""


# ---------------------------------------------------------------------------- JSON (spec 02 §2.1, §2.2)

class EncodingProblem(ValueError):
    """Bytes that are not an I-JSON text (ENC-1 to ENC-3)."""


_LONE_SURROGATE = re.compile("[\ud800-\udfff]")
_DEPTH_TOKENS = re.compile(rb'"[^"\\]*(?:\\.[^"\\]*)*"|[\[\]{}]', re.S)


def nesting_depth(data: bytes) -> int:
    """The deepest nesting of objects and arrays, counted on the bytes (strings skipped) so a hostile document cannot
    exhaust a recursive parser before the limit is checked."""
    depth = deepest = 0
    for m in _DEPTH_TOKENS.finditer(data):
        token = m.group()
        if token in (b"[", b"{"):
            depth += 1
            deepest = max(deepest, depth)
        elif token in (b"]", b"}"):
            depth -= 1
    return deepest


def load_json_text(text: str):
    """One strict I-JSON value: no member named twice, no unpaired surrogate, finite numbers only."""
    def members(pairs):
        obj = {}
        for key, value in pairs:
            if key in obj and "ijson-duplicates" not in MUTATIONS:
                raise EncodingProblem(f"the member {key!r} appears twice")
            obj[key] = value
        return obj

    def constant(name):
        raise EncodingProblem(f"{name} is not a JSON number")

    def number(text):
        value = float(text)
        if not math.isfinite(value):
            raise EncodingProblem(f"{text} is not a finite binary64 number")
        return value

    def integer(text):
        """Plain digits, read as binary64 (ENC-4): exact up to 2^53, rounded beyond (where the schemas bound every
        integer field, so the value is a schema problem), and an encoding problem when it overflows (ENC-3). Up to
        15 digits the value is exact, so int() is the binary64 value."""
        if len(text.lstrip("-")) <= 15:
            return int(text)
        value = float(text)  # float() has no digit limit, unlike int()
        if not math.isfinite(value):
            raise EncodingProblem(f"{text[:20]}... overflows binary64")
        return int(value)

    try:
        value = json.loads(text, object_pairs_hook=members, parse_constant=constant, parse_float=number,
                           parse_int=integer)
    except EncodingProblem:
        raise
    except (ValueError, RecursionError) as error:
        raise EncodingProblem(str(error)) from None
    stack = [value]
    while stack:
        x = stack.pop()
        if isinstance(x, str):
            if _LONE_SURROGATE.search(x):
                raise EncodingProblem("a string holds an unpaired surrogate")
        elif isinstance(x, dict):
            for k, v in x.items():
                if _LONE_SURROGATE.search(k):
                    raise EncodingProblem("a member name holds an unpaired surrogate")
                stack.append(v)
        elif isinstance(x, list):
            stack.extend(x)
    return value


def load_json_bytes(data: bytes, object_only=True):
    """A JSON document from its bytes (ENC-1): UTF-8, no byte-order mark, one I-JSON value, an object."""
    if data.startswith(b"\xef\xbb\xbf"):
        raise EncodingProblem("a byte-order mark")
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise EncodingProblem(f"not UTF-8: {error}") from None
    value = load_json_text(text)
    if object_only and not isinstance(value, dict):
        raise EncodingProblem("the top-level value is not an object")
    return value


def load_json_file(path) -> object:
    """A JSON input file of a command (a policy, a checkpoint, a list of paths); InputError when it cannot be read."""
    try:
        return load_json_bytes(Path(path).read_bytes(), object_only=False)
    except OSError as error:
        raise InputError(f"{path}: {error}") from None
    except EncodingProblem as error:
        raise InputError(f"{path}: not an I-JSON document: {error}") from None


def ndjson_framing(data: bytes):
    """None when the bytes are framed as ENC-5 and ENC-7 require, else why not."""
    if not data:
        return None
    if data.startswith(b"\xef\xbb\xbf"):
        return "a byte-order mark"
    if b"\r" in data:
        return "a CR"
    if not data.endswith(b"\n"):
        return "the last line does not end in LF"
    if data.startswith(b"\n") or b"\n\n" in data:
        return "a blank line"
    return None


def ndjson_lines(data: bytes):
    """(1-based number, start offset, bytes without the LF) of every line ended by LF."""
    lines, start, number = [], 0, 0
    while True:
        end = data.find(b"\n", start)
        if end < 0:
            return lines
        number += 1
        lines.append((number, start, data[start:end]))
        start = end + 1


# ---------------------------------------------------------------------------- values

def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def parse_time(text):
    """(seconds, nanoseconds) of an RFC 3339 UTC time (ENC-8), exact; ValueError for anything else."""
    if not isinstance(text, str):
        raise ValueError("not a string")
    return aef_decide.parse_time(text)


def time_key(text):
    try:
        return parse_time(text)
    except ValueError:
        return None


def utc_now():
    now = datetime.datetime.now(datetime.timezone.utc)
    return now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond:06d}Z"


def utf8_key(text: str) -> bytes:
    return text.encode("utf-8", "surrogatepass")


_LINE_PATH = re.compile(r"([^:]*\.(?:ndjson|jsonl)):([0-9]+)")


def path_key(path: str):
    """§3.9's order of paths: by their UTF-8 bytes, except that the '<file>:<line>' paths of one file go by line
    number as a number (results.ndjson:9 before results.ndjson:10). A run's paths hold no ':' (RUN-3), so a line path
    sorts exactly where its bytes would put it among every other path."""
    m = _LINE_PATH.fullmatch(path)
    if m and "line-order" not in MUTATIONS:
        return utf8_key(m.group(1) + ":"), int(m.group(2))
    return utf8_key(path), -1


def sort_problems(problems):
    """[path, code] pairs, ordered by path (path_key) and then by code; each pair once."""
    return [list(p) for p in sorted(set(problems), key=lambda p: (path_key(p[0]), utf8_key(p[1])))]


def result_id(run_id: str, case_id: str, path: str, trial) -> str:
    """RES-4: 'r_' + the first 32 hex of SHA-256 over runId, caseId, path and trial joined by U+001F."""
    if trial is None:
        trial_text = ""
    else:
        if isinstance(trial, bool) or not isinstance(trial, (int, float)) or not float(trial).is_integer():
            raise ValueError(f"trial {trial!r} is not an integer")
        trial_text = str(int(trial))
    joined = "\u001f".join([run_id, case_id, path, trial_text])
    return "r_" + hashlib.sha256(joined.encode("utf-8")).hexdigest()[:32]


def get(obj, *keys, default=None):
    """obj[k1][k2]... or default when any step is missing or not an object."""
    for k in keys:
        if not isinstance(obj, dict) or k not in obj:
            return default
        obj = obj[k]
    return obj


def as_number(x):
    return x if isinstance(x, (int, float)) and not isinstance(x, bool) else None


def close_enough(written, recomputed) -> bool:
    """§3.6: equal when they differ by at most 1e-9 x max(1, |recomputed|)."""
    w = as_number(written)
    return w is not None and abs(w - recomputed) <= 1e-9 * max(1.0, abs(recomputed))


# ---------------------------------------------------------------------------- §7.3: reading unknown values

def read_status(value):
    return value if value in RUN_STATUSES else "running"


def read_state(value):
    return value if value in STATES else "inconclusive"


def read_severity(value):
    return value if value in SEVERITY_ORDER else "critical"


def read_target_mode(value):
    return value if value in TARGET_MODES else "mocked"


def read_content_capture(value):
    return value if value in ("on", "off") else "on"


def read_direction(value):
    return value if value in DIRECTIONS else "none"


def read_overlay_kind(value):
    return value if value in OVERLAY_KINDS else "annotate"


def read_comparability(value):
    return value if value in COMPARABILITY else "incomparable"


def _known(values, fallback):
    return lambda v: v if v in values else fallback


def _as_written(v):
    return v


_RUN_SUBJECT_KINDS = {"agent", "workflow", "model", "endpoint", "mcp-server", "other"}
_JUDGE_MODES = {"single", "panel", "primary", "shadow", "other"}
_EVIDENCE_KINDS = {"span", "tool_call", "judge_reasoning", "compliance_artifact", "document", "input", "expected",
                   "output", "transcript", "other"}
_ANNOTATOR_KINDS = {"CODE", "LLM", "HUMAN", "HYBRID", "OTHER"}
_USAGE_ROLES = {"agent", "judge", "attacker", "other"}
_TAXONOMY_SCHEMES = {"owasp-llm", "owasp-agentic", "mitre-atlas", "nist-ai-rmf", "other"}
_SUMMARY_VERDICTS = {"passed", "failed", "warn", "inconclusive", "not_measured"}

# Per schema: (field path with [*] for every item of an array and {*} for every member of an object, how a reader
# reads the value). Spec 07 §7.3 (VER-8).
READINGS = {
    "result": [("severity", read_severity),
               ("annotator.kind", _known(_ANNOTATOR_KINDS, "OTHER")),
               ("usage[*].role", _known(_USAGE_ROLES, "other")),
               ("attack.taxonomy[*].scheme", _known(_TAXONOMY_SCHEMES, "other")),
               ("trials.aggregation", _as_written),
               ("aggregation.strategy", _as_written), ("aggregation.rulePath", _as_written)],
    "run": [("execution.targetMode", read_target_mode),
            ("execution.stimulus", _known({"suite", "generated", "imported", "other"}, "other")),
            ("contentCapture", read_content_capture), ("subject.kind", _known(_RUN_SUBJECT_KINDS, "other")),
            ("judges[*].mode", _known(_JUDGE_MODES, "other")),
            ("suite.executionPolicy.aggregation", _as_written), ("config.thresholds{*}.op", _as_written)],
    "summary": [("lanes[*].metrics[*].verdict", _known(_SUMMARY_VERDICTS, "inconclusive"))],
    "evidence": [("kind", _known(_EVIDENCE_KINDS, "other"))],
    "metrics": [("metrics[*].direction", read_direction)],
    "gate-decision": [("outcome", _known(GATE_OUTCOMES, "inconclusive")), ("comparability", read_comparability),
                      ("rule.strategy", _as_written)],
    # OVL-3: an assurance is shown only as far as it was verified; a document alone verifies nothing.
    "overlay-event": [("kind", read_overlay_kind), ("by.assurance", lambda v: "self-attested")],
    "seal": [("predicate.sealedBy", _known({"producer", "ingest"}, "ingest"))],
    "checkpoint": [("state", _known(CHECKPOINT_STATES, "unverifiable")),
                   ("outcome", lambda v: v if v is None or v in CHECKPOINT_OUTCOMES else "unverifiable"),
                   ("lanes[*].rule.kind", lambda v: v if v in RULE_KINDS else "not_measured"),
                   ("lanes[*].rule.max", lambda v: v if v in ("none", "low", "medium", "high") else "not_measured"),
                   ("lanes[*].rule.axes[*]", lambda v: v if v in AXES else "incomparable")],
    "decision": [("lanes[*].status", _known(DECISION_LANE_STATUSES, "not_measured"))],
    "run-plan": [("provider", lambda v: v if v in ("local", "docker", "k8s") or
                  (isinstance(v, str) and re.fullmatch(r"ci:[a-z0-9-]{1,64}", v)) else "refused"),
                 ("isolation", _known({"process", "container", "remote-zone"}, "refused")),
                 ("credentialRefs[*].scheme", _known({"env", "keychain", "vault"}, "refused")),
                 ("credentialRefs[*].purpose", _known({"subject", "judge", "attacker", "evaluator", "other"},
                                                      "refused"))],
    "runner-event": [("kind", _known({"job.accepted", "job.refused", "plan.estimated", "spend.updated",
                                      "case.completed", "lane.completed", "evidence.produced", "job.cancelled",
                                      "job.failed", "job.sealed"}, "skipped")),
                     ("status", _known(INPUT_STATUSES, "not_measured"))],  # lane.completed's status
    "runner": [("kind", _as_written), ("os", _as_written)],
}


def readings(schema: str, document) -> dict:
    """{concrete field path: the value a reader takes} for every §7.3 field the document holds."""
    out = {}
    for pattern, read in READINGS.get(schema.split("#")[0], ()):
        for path, value in _expand(document, pattern.split("."), ""):
            out[path] = read(value)
    return out


def _expand(obj, steps, prefix):
    if not steps:
        yield prefix, obj
        return
    step, rest = steps[0], steps[1:]
    many, members = step.endswith("[*]"), step.endswith("{*}")
    name = step[:-3] if many or members else step
    if not isinstance(obj, dict) or name not in obj:
        return
    here = f"{prefix}.{name}" if prefix else name
    if members:
        if isinstance(obj[name], dict):
            for key, item in obj[name].items():
                yield from _expand(item, rest, f"{here}.{key}") if rest else iter([(f"{here}.{key}", item)])
    elif many:
        if isinstance(obj[name], list):
            for i, item in enumerate(obj[name]):
                yield from _expand(item, rest, f"{here}[{i}]") if rest else iter([(f"{here}[{i}]", item)])
    else:
        yield from _expand(obj[name], rest, here)


# ---------------------------------------------------------------------------- schemas

_SETS = {}


def schema_set(side):
    if side not in _SETS:
        _SETS[side] = aef_schema.load_schemas(AEF_ROOT / "schemas" / side)
    return _SETS[side]


def schema_valid(side, schema, instance) -> bool:
    return not schema_set(side).validate(schema, instance)


_TIME_SHAPE = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T")


def impossible_times(side, schema, instance):
    """The strings the schema places at a timestamp whose date does not exist (ENC-8: a pattern cannot refuse
    2026-02-31). Walks the schema beside the instance; every applicator is followed, so nothing is missed."""
    ss = schema_set(side)
    stamp = ss._target("common#/$defs/timestamp")
    found, stack, seen = [], [(ss._target(schema), instance)], set()
    while stack:
        s, x = stack.pop()
        if not isinstance(s, dict) or (id(s), id(x)) in seen:
            continue
        seen.add((id(s), id(x)))
        if s is stamp and isinstance(x, str):
            m = _TIME_SHAPE.match(x)
            if m:
                try:
                    datetime.date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
                except ValueError:
                    found.append(x)
        if "$ref" in s:
            stack.append((ss._refs[id(s)], x))
        for key in ("allOf", "anyOf", "oneOf"):
            stack.extend((sub, x) for sub in s.get(key, ()))
        for key in ("then", "else"):
            if key in s:
                stack.append((s[key], x))
        if isinstance(x, dict):
            declared = s.get("properties", {})
            for name, sub in declared.items():
                if name in x:
                    stack.append((sub, x[name]))
            for pattern, sub in s.get("patternProperties", {}).items():
                stack.extend((sub, x[n]) for n in x if aef_schema.compile_pattern(pattern).search(n))
            extra = s.get("additionalProperties")
            if isinstance(extra, dict):
                stack.extend((extra, x[n]) for n in x if n not in declared)
        if isinstance(x, list):
            prefix = s.get("prefixItems", [])
            for i, item in enumerate(x):
                sub = prefix[i] if i < len(prefix) else s.get("items")
                if isinstance(sub, dict):
                    stack.append((sub, item))
    return found


def document_ok(side, schema, instance) -> bool:
    """Valid against the schema, and no impossible time."""
    return schema_valid(side, schema, instance) and not impossible_times(side, schema, instance)


# ---------------------------------------------------------------------------- RUN-3 paths

_SEGMENT = re.compile(r"[A-Za-z0-9._-]+")
_RESERVED = re.compile(r"(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])", re.I)


def path_problems(paths):
    """[RUN-3] over a list of paths: each path that breaks it (a segment that is empty, holds another character,
    starts or ends with '.', or is a reserved Windows name), and for paths that differ only in letter case (in any
    folder of them), every one but the first in byte order. [(path, 'path')]."""
    bad = set()
    for p in paths:
        if not isinstance(p, str):
            continue
        if len(utf8_key(p)) > MAX_PATH:
            bad.add(p)
            continue
        for segment in p.split("/"):
            if (not _SEGMENT.fullmatch(segment) or segment.startswith(".") or segment.endswith(".")
                    or _RESERVED.fullmatch(segment.split(".")[0])):
                bad.add(p)
                break
    spellings = defaultdict(set)  # lower-case prefix -> the spellings of it
    for p in paths:
        if not isinstance(p, str):
            continue
        parts = p.split("/")
        for i in range(1, len(parts) + 1):
            prefix = "/".join(parts[:i])
            spellings[prefix.lower()].add(prefix)
    for variants in spellings.values():
        if len(variants) > 1:
            later = sorted(variants, key=utf8_key)[1:]
            for p in paths:
                if isinstance(p, str) and any(p == v or p.startswith(v + "/") for v in later):
                    bad.add(p)
    return {(p, "path") for p in bad}


# ---------------------------------------------------------------------------- the run folder

class Folder:
    """The files of a run folder, by their path relative to it ('/' separators), with their bytes read once."""

    def __init__(self, root):
        self.root = Path(root)
        if not self.root.is_dir():
            raise InputError(f"{root}: not a folder")
        paths = []
        for dirpath, dirnames, filenames in os.walk(self.root):
            dirnames.sort()
            rel = os.path.relpath(dirpath, self.root)
            for name in filenames:
                paths.append(name if rel == "." else f"{rel.replace(os.sep, '/')}/{name}")
        self.paths = sorted(paths, key=utf8_key)
        self.present = set(paths)
        self._bytes, self._digests = {}, {}

    def has(self, rel):
        return rel in self.present

    def file(self, rel) -> Path:
        return self.root.joinpath(*rel.split("/"))

    def size(self, rel):
        return self.file(rel).stat().st_size

    def read(self, rel) -> bytes:
        if rel not in self._bytes:
            self._bytes[rel] = self.file(rel).read_bytes()
        return self._bytes[rel]

    def digest(self, rel) -> str:
        if rel not in self._digests:
            if rel in self._bytes:
                self._digests[rel] = sha256_hex(self._bytes[rel])
            else:
                h = hashlib.sha256()
                with open(self.file(rel), "rb") as f:
                    for chunk in iter(lambda: f.read(1 << 20), b""):
                        h.update(chunk)
                self._digests[rel] = h.hexdigest()
        return self._digests[rel]

    def sealed_files(self):
        """SEAL-1: every file but seal.json, attestation.dsse.json and everything under overlays/."""
        return [p for p in self.paths
                if p not in ("seal.json", "attestation.dsse.json") and not p.startswith("overlays/")]

    def manifest(self):
        """SEAL-3: '<sha256>  <size>  <path>\\n' per sealed file, by the UTF-8 bytes of its path."""
        files = self.sealed_files()
        if "manifest-order" in MUTATIONS:
            files = sorted(files, key=lambda p: [utf8_key(s) for s in p.split("/")])
        return "".join(f"{self.digest(p)}  {self.size(p)}  {p}\n" for p in files)

    def run_hash(self):
        """SEAL-4: the SHA-256 of the manifest's bytes."""
        return sha256_hex(self.manifest().encode("utf-8"))


# ---------------------------------------------------------------------------- the run verifier

class Run:
    """One run folder, verified lazily: documents (spec 02, §3.9 reading), the seal (§4.1), the overlay chain (§4.2),
    the rules across files (§3.9), the outcome (§4.5)."""

    def __init__(self, root, policy=None):
        """policy: the caller's trust policy (a parsed JSON object), or None. It decides which redactions are
        authorized (OVL-10)."""
        self.folder = Folder(root)
        self.policy = policy
        self._read = None
        self._seal = None
        self._chain = None
        self._verdict = None
        self._batch_signers = {}

    # ---- reading (spec 02, §3.9 first paragraph)

    def _json_file(self, rel, schema, problems):
        """The document (or None) of a JSON file of the run, adding its encoding, limit and schema problems."""
        f = self.folder
        if f.size(rel) > MAX_JSON:
            problems.add((rel, "limit"))
            return None
        data = f.read(rel)
        if nesting_depth(data) > MAX_DEPTH:
            problems.add((rel, "limit"))
            return None
        try:
            doc = load_json_bytes(data)
        except EncodingProblem:
            problems.add((rel, "encoding"))
            return None
        if schema and not document_ok("reader", schema, doc):
            problems.add((rel, "schema"))
        return doc

    def _ndjson_file(self, rel, schema, problems):
        """[(line number, start offset, object or None)] of an NDJSON file of the run, adding its problems. A framing
        problem is reported once at the file and no line is read."""
        data = self.folder.read(rel)
        if ndjson_framing(data):
            problems.add((rel, "encoding"))
            return []
        lines = ndjson_lines(data)
        if len(lines) > MAX_LINES or any(len(raw) > MAX_JSON or nesting_depth(raw) > MAX_DEPTH for _, _, raw in lines):
            problems.add((rel, "limit"))
            return []
        out = []
        for number, start, raw in lines:
            where = f"{rel}:{number}"
            try:
                obj = load_json_bytes(raw)
            except EncodingProblem:
                problems.add((where, "encoding"))
                out.append((number, start, None))
                continue
            if schema and not document_ok("reader", schema, obj):
                problems.add((where, "schema"))
            out.append((number, start, obj))
        return out

    def read(self):
        """(documents, lines, problems): the run's JSON documents and NDJSON lines as read, and the problems of
        reading them (encoding, limit, schema). seal.json, attestation.dsse.json and overlays/ are read by §4.1,
        §4.4 and §4.2."""
        if self._read is None:
            f, problems, docs, lines = self.folder, set(), {}, {}
            if len(f.paths) > MAX_FILES:
                problems.add((".", "limit"))
            for rel, schema in RUN_JSON.items():
                if f.has(rel):
                    docs[rel] = self._json_file(rel, schema, problems)
            for rel, schema in RUN_NDJSON.items():
                if f.has(rel):
                    lines[rel] = self._ndjson_file(rel, schema, problems)
            run = docs.get("run.json")
            closed = isinstance(run, dict) and read_status(run.get("status")) != "running"
            required = ["run.json", "results.ndjson", "metrics.json"] + (["summary.json"] if closed else [])
            for rel in required:  # RUN-2: files a run always has; their absence leaves nothing to check against
                if not f.has(rel):
                    problems.add((rel, "schema"))
            for p in f.paths:
                if p.startswith("blobs/") and f.size(p) > MAX_BLOB:
                    problems.add((p, "limit"))
            self._read = (docs, lines, problems)
        return self._read

    @property
    def run_doc(self):
        doc = self.read()[0].get("run.json")
        return doc if isinstance(doc, dict) else None

    def objects(self, rel):
        """The parsed objects of an NDJSON file's lines: [(line number, object)], lines that did not parse left out."""
        return [(n, o) for n, _, o in self.read()[1].get(rel, []) if isinstance(o, dict)]

    # ---- the seal document

    def seal_doc(self):
        """(seal.json as read, or None, whether it is an I-JSON document valid against the reader seal schema)."""
        if not self.folder.has("seal.json"):
            return None, False
        try:
            doc = load_json_bytes(self.folder.read("seal.json"))
        except EncodingProblem:
            return None, False
        return doc, document_ok("reader", "seal", doc)

    def claimed_run_hash(self):
        """SEAL-4's 'a run's run hash': the seal's predicate.runHash when seal.json is valid against the reader seal
        schema (a withheld blob leaves the manifest impossible to recompute), else the hash recomputed from the
        files."""
        doc, valid = self.seal_doc()
        return doc["predicate"]["runHash"] if valid else self.folder.run_hash()

    # ---- §4.2 the overlay chain

    def chain(self):
        """{'problems': set, 'verified_end': byte offset, 'lines': [(number, start, end, obj|None, ok)]}."""
        if self._chain is not None:
            return self._chain
        f, problems = self.folder, set()
        seals = {}
        for p in f.paths:
            if not p.startswith("overlays/") or p == EVENTS:
                continue
            m = BATCH_SEAL.fullmatch(p)
            if m:
                seals[int(m.group(1))] = p
            elif not BATCH_SIGNATURE.fullmatch(p):
                problems.add((p, "unexpected-file"))
        events = f.read(EVENTS) if f.has(EVENTS) else b""
        if f.has(EVENTS) and ndjson_framing(events):
            # OVL-5: reported once, and the chain is not checked further: no batch verifies, no event has effect.
            problems.add((EVENTS, "encoding"))
            lines = [(n, s, s + len(raw) + 1, None, False) for n, s, raw in ndjson_lines(events)]
            self._chain = {"problems": problems, "verified_end": 0, "lines": lines, "batches": []}
            return self._chain
        run = self.run_doc
        run_id = run.get("runId") if run else None
        run_hash = self.claimed_run_hash()
        if 0 in seals:  # OVL-5: batches are 1-based; seal-0000.json is batch-number and not checked further
            problems.add((seals[0], "batch-number"))
        covered = []  # claimed ranges, clipped to the file
        batches = []  # (number, offset, end) of the verified batches (§4.3)
        verified, verified_end, previous_end, previous_ok = True, 0, None, False
        for k in range(1, max(seals, default=0) + 1):
            path = f"overlays/seal-{k:04d}.json"
            if k not in seals:
                problems.add((path, "missing"))
                verified, previous_ok = False, False
                continue
            try:
                doc = load_json_bytes(f.read(path))
                valid = document_ok("reader", "overlay-seal", doc)
            except EncodingProblem:
                valid = False
            if not valid:
                problems.add((path, "batch-invalid"))
                verified, previous_ok = False, False
                continue
            pred, own = doc["predicate"], set()
            if pred["batch"] != k:
                own.add("batch-number")
            if run_id is not None and pred["runId"] != run_id:
                own.add("run-id")
            if pred["runHash"] != run_hash and "overlay-run-hash" not in MUTATIONS:
                own.add("run-hash")
            offset, length = int(pred["offset"]), int(pred["length"])
            end = offset + length
            expected_offset = 0 if k == 1 else (previous_end if previous_ok else None)
            if expected_offset is not None and offset != expected_offset:
                own.add("offset")
            if not (end <= len(events) and (offset == 0 or events[offset - 1] == 0x0A) and events[end - 1] == 0x0A):
                own.add("line-boundary")
            if end > len(events) or sha256_hex(events[offset:end]) != doc["subject"][0]["digest"]["sha256"]:
                own.add("batch-digest")
            before = f"overlays/seal-{k - 1:04d}.json"
            prev = pred.get("previous")
            if k == 1:
                if prev is not None:
                    own.add("previous")
            elif not (isinstance(prev, dict) and prev.get("path") == before and (k - 1) in seals
                      and prev.get("sha256") == f.digest(before)):
                own.add("previous")
            covered.append((min(offset, len(events)), min(end, len(events))))
            problems.update((path, c) for c in own)
            if verified and not own:
                verified_end = end
                batches.append((k, offset, end))
            else:
                verified = False
            previous_end, previous_ok = end, True
        if _uncovered(covered, len(events)):
            problems.add((EVENTS, "uncovered"))
        lines = self._events(events, run_id, run_hash, problems) if f.has(EVENTS) else []
        self._chain = {"problems": problems, "verified_end": verified_end, "lines": lines, "batches": batches}
        return self._chain

    def _events(self, data, run_id, run_hash, problems):
        """The lines of overlays/events.ndjson: (number, start, end, object or None, usable), with the event-invalid,
        event-id and target problems. A file whose NDJSON framing is broken (ENC-5, ENC-7) is reported once as
        encoding at its path (spec 02: OVL-5 names no code for it), and none of its events is usable."""
        if ndjson_framing(data):
            problems.add((EVENTS, "encoding"))
            return [(n, s, s + len(raw) + 1, None, False) for n, s, raw in ndjson_lines(data)]
        result_ids = {o.get("resultId") for _, o in self.objects("results.ndjson")}
        results_read = "results.ndjson" in self.read()[1] and not any(
            p[0] == "results.ndjson" or p[0].startswith("results.ndjson:") for p in self.read()[2])
        seen, out = set(), []
        for number, start, raw in ndjson_lines(data):
            where = f"{EVENTS}:{number}"
            end = start + len(raw) + 1
            try:
                event = load_json_bytes(raw)
            except EncodingProblem:
                problems.add((where, "event-invalid"))
                out.append((number, start, end, None, False))
                continue
            usable = True
            if not document_ok("reader", "overlay-event", event):
                problems.add((where, "event-invalid"))
                out.append((number, start, end, event, False))
                continue
            if event["eventId"] in seen:
                problems.add((where, "event-id"))
                usable = False
            seen.add(event["eventId"])
            target = event["target"]
            wrong = (run_id is not None and target["run"] != run_id) or (
                "runHash" in target and target["runHash"] != run_hash and "overlay-run-hash" not in MUTATIONS) or (
                "result" in target and results_read and target["result"] not in result_ids)
            if wrong:
                problems.add((where, "target"))
                usable = False
            out.append((number, start, end, event, usable))
        return out

    def verified_events(self, with_batch=False):
        """The usable events of the verified batches, in file order (§4.3); with_batch: (event, batch number)."""
        chain = self.chain()
        out = []
        for _, start, end, e, ok in chain["lines"]:
            if ok and end <= chain["verified_end"]:
                batch = next((k for k, s, t in chain["batches"] if s <= start < t), None)
                out.append((e, batch) if with_batch else e)
        return out

    def batch_signers(self, k):
        """The identities overlays/seal-<k>.dsse.json verifies for under the caller's policy (§4.4), in policy
        order; empty without a policy or a signature."""
        if k not in self._batch_signers:
            sig, seal = f"overlays/seal-{k:04d}.dsse.json", f"overlays/seal-{k:04d}.json"
            signers = []
            if self.policy is not None and self.folder.has(sig) and self.folder.has(seal):
                signers = verify_signature(self.folder.read(sig), self.folder.read(seal), IN_TOTO_TYPE,
                                           self.policy)["verifiesFor"]
            self._batch_signers[k] = signers
        return self._batch_signers[k]

    def authorized(self, event, batch):
        """OVL-10: a redact event is authorized when its batch's signature verifies for the event's by.identity and
        the caller's policy lets that identity redact."""
        if "redaction-authorization" in MUTATIONS:
            return True
        identity = get(event, "by", "identity")
        return (batch is not None and identity in self.batch_signers(batch)
                and policy_allows(self.policy, identity, "redact"))

    def withheld_blobs(self):
        """The hex names of the blobs authorized redactions withhold, in file order (OVL-10)."""
        out = []
        for e, batch in self.verified_events(with_batch=True):
            if (read_overlay_kind(e.get("kind")) == "redact" and "blob" in e["target"]
                    and e["target"]["blob"] not in out and self.authorized(e, batch)):
                out.append(e["target"]["blob"])
        return out

    # ---- §4.1 the seal

    def seal(self):
        """The §4.1 problems (a set); empty for an unsealed run."""
        if self._seal is not None:
            return self._seal
        f = self.folder
        problems = set()
        if not f.has("seal.json"):
            self._seal = problems
            return problems
        doc, valid = self.seal_doc()
        if not valid:
            self._seal = {("seal.json", "seal-invalid")}
            return self._seal
        names = Counter(s["name"] for s in doc["subject"])
        subjects = {}
        for s in doc["subject"]:
            name = s["name"]
            if names[name] > 1:
                problems.add((name, "duplicate-subject"))
            elif name in ("seal.json", "attestation.dsse.json") or name.startswith("overlays/"):
                problems.add((name, "subject-path"))
            else:
                subjects[name] = s["digest"]["sha256"]
        for p in f.sealed_files():
            if names.get(p, 0) > 1:
                continue  # a duplicated subject's digests are not compared
            if p not in subjects:
                problems.add((p, "not-sealed"))
            elif "seal-digest" not in MUTATIONS and f.digest(p) != subjects[p]:
                problems.add((p, "digest"))
        withheld = None
        for name in subjects:
            if f.has(name):
                continue
            if withheld is None:
                withheld = set(self.withheld_blobs())
            m = BLOB_PATH.fullmatch(name)
            problems.add((name, "withheld" if m and m.group(2) in withheld else "missing"))
        matching = not any(c in ("duplicate-subject", "digest", "not-sealed", "missing", "withheld")
                           for _, c in problems)
        pred = doc["predicate"]
        if matching and f.run_hash() != pred["runHash"]:
            problems.add(("seal.json", "run-hash"))
        run = self.run_doc
        if run is not None:
            if pred["runId"] != run.get("runId"):
                problems.add(("seal.json", "run-id"))
            if _predicate_differs(pred, run):
                problems.add(("seal.json", "predicate"))
            if read_status(run.get("status")) == "running":
                problems.add(("run.json", "run-open"))
        self._seal = problems
        return problems

    # ---- §3.9 the rules across files

    def cross_file(self):
        """The problems of §3.9 from result-id down (run only when reading found no encoding, limit or schema
        problem)."""
        f = self.folder
        docs, _, _ = self.read()
        run = docs.get("run.json") or {}
        metrics_doc = docs.get("metrics.json") or {}
        summary = docs.get("summary.json")
        results = self.objects("results.ndjson")
        evidence = self.objects("evidence.ndjson")
        gates = self.objects("gates.ndjson")
        P = set()
        run_id = run.get("runId")
        closed = read_status(run.get("status")) != "running"
        off = read_content_capture(run.get("contentCapture")) == "off"
        withheld = set(self.withheld_blobs()) if f.has("seal.json") else set()

        declared = Counter(m.get("id") for m in metrics_doc.get("metrics", []))
        metric = {}
        for m in metrics_doc.get("metrics", []):
            metric.setdefault(m.get("id"), m)
        for m in metrics_doc.get("metrics", []):
            scale = m.get("scale")
            if declared[m.get("id")] > 1 or (isinstance(scale, dict) and scale["min"] > scale["max"]):
                P.add(("metrics.json", "metric"))

        spans = None
        if f.has("traces.otlp.jsonl"):
            spans = set()
            for n, request in self.objects("traces.otlp.jsonl"):
                for span in _otlp_spans(request):
                    spans.add((str(span.get("traceId", "")).lower(), str(span.get("spanId", "")).lower()))
                    if off and _carries_content(span):
                        P.add((f"traces.otlp.jsonl:{n}", "content-capture"))  # SEC-6

        def span_known(trace_id, span_id):
            if spans is None:
                return True
            if span_id is None:
                return any(t == trace_id.lower() for t, _ in spans)
            return (trace_id.lower(), span_id.lower()) in spans

        def blob_state(digest_uri):
            """'present', 'withheld' or 'absent' for a sha256:<hex> blob reference."""
            hexname = digest_uri.split(":", 1)[1]
            if f.has(f"blobs/sha256/{hexname[:2]}/{hexname}"):
                return "present"
            return "withheld" if hexname in withheld else "absent"

        # evidence.ndjson
        evidence_ids = set()
        for n, e in evidence:
            where = f"evidence.ndjson:{n}"
            if e["evidenceId"] in evidence_ids:
                P.add((where, "evidence-id"))
            evidence_ids.add(e["evidenceId"])
            link = e["link"]
            if "blob" in link:
                if e.get("digest") != link["blob"]:
                    P.add((where, "evidence-digest"))
                if blob_state(link["blob"]) == "absent":
                    P.add((where, "blob"))
            if "traceId" in link and not span_known(link["traceId"], link.get("spanId")):
                P.add((where, "trace-link"))
            if off and e.get("kind") in CONTENT_KINDS:
                P.add((where, "content-capture"))

        # results.ndjson
        all_ids = {o["resultId"] for _, o in results}
        children = defaultdict(set)
        for _, o in results:
            if o.get("parentResultId") is not None:
                children[o["parentResultId"]].add(o["resultId"])
        seen = set()
        for n, o in results:
            where = f"results.ndjson:{n}"
            try:
                expected = result_id(run_id, o["caseId"], o["path"], o.get("trial")) if isinstance(run_id, str) else None
            except ValueError:
                expected = None
            if o["resultId"] != expected or o["resultId"] in seen:
                P.add((where, "result-id"))
            seen.add(o["resultId"])
            parent = o.get("parentResultId")
            if parent is not None and parent not in all_ids:
                P.add((where, "parent"))
            agg = o.get("aggregation")
            if isinstance(agg, dict):
                measured, total = agg["measured"], agg["total"]
                unmeasured = agg.get("unmeasured")
                counts_wrong = measured > total or (isinstance(unmeasured, dict) and sum(
                    v for v in unmeasured.values() if as_number(v) is not None) != total - measured)
                if counts_wrong or any(d not in children[o["resultId"]] for d in agg.get("decisive", [])):
                    P.add((where, "aggregation"))
            trials = o.get("trials")
            if isinstance(trials, dict) and trials["passed"] > trials["n"]:
                P.add((where, "trials"))
            if closed and o["state"] == "pending":
                P.add((where, "pending"))
            if any(e not in evidence_ids for e in o.get("evidence", [])):
                P.add((where, "evidence"))
            reasoning = o.get("reasoning")
            if isinstance(reasoning, dict):
                state = blob_state(reasoning["blob"])
                if state == "absent":
                    P.add((where, "blob"))
                elif state == "present":
                    hexname = reasoning["blob"].split(":", 1)[1]
                    if f.size(f"blobs/sha256/{hexname[:2]}/{hexname}") != reasoning["bytes"]:
                        P.add((where, "reasoning-size"))
                if off:
                    P.add((where, "content-capture"))
            annotator = o.get("annotator")
            if isinstance(annotator, dict):
                panel = annotator.get("panel")
                if isinstance(panel, dict) and panel["agree"] > panel["of"]:
                    P.add((where, "annotator"))  # RES-10
                if off and "promptHash" in annotator:
                    P.add((where, "content-capture"))  # RUN-11: no digest of content either
            scored = Counter(s.get("metric") for s in o.get("scores", []))
            if any(m not in metric for m in scored) or any(count > 1 for count in scored.values()):
                P.add((where, "metric"))  # undeclared, or one metric scored twice
            started, ended = time_key(o.get("startedAt")), time_key(o.get("endedAt"))
            roles = [u["role"] for u in o.get("usage") or [] if isinstance(u, dict) and "role" in u]
            if (started is not None and ended is not None and ended < started) or len(roles) != len(set(roles)):
                P.add((where, "result-times"))
            if _inverted(get(o, "uncertainty", "ci")):
                P.add((where, "interval"))
            link = o.get("traceLink")
            if isinstance(link, dict) and not span_known(link["traceId"], link.get("spanId")):
                P.add((where, "trace-link"))

        # blobs
        for p in f.paths:
            if p.startswith("blobs/"):
                m = BLOB_PATH.fullmatch(p)
                if not m or m.group(2)[:2] != m.group(1) or f.digest(p) != m.group(2):
                    P.add((p, "blob-digest"))

        # summary.json
        if isinstance(summary, dict):
            if summary.get("runId") != run_id:
                P.add(("summary.json", "summary-run-id"))
            if any(e.get("metric") not in metric for lane in summary.get("lanes", []) for e in lane.get("metrics", [])):
                P.add(("summary.json", "metric"))
            if "summary" not in MUTATIONS and _summary_wrong(summary, results, metric):
                P.add(("summary.json", "summary"))
            if any(_inverted(e.get("ci")) for lane in summary.get("lanes", []) for e in lane.get("metrics", [])):
                P.add(("summary.json", "interval"))

        # gates.ndjson
        for n, g in gates:
            named = list(get(g, "inputs", "results", default=[]) or []) + list(g.get("decisive", []))
            ship_incomparable = g.get("outcome") == "ship" and read_comparability(g.get("comparability")) == "incomparable"
            if any(r not in all_ids for r in named) or ship_incomparable:
                P.add((f"gates.ndjson:{n}", "gate"))

        # run.json
        start, end = time_key(run.get("startedAt")), time_key(run.get("endedAt"))
        if start is not None and end is not None and end < start:
            P.add(("run.json", "run-times"))
        for judge in run.get("judges") or []:
            cal = judge.get("calibration") if isinstance(judge, dict) else None
            if isinstance(cal, dict):
                measured_at = time_key(cal.get("measuredAt"))
                if (as_number(cal.get("dangerousErrors")) is not None and cal["dangerousErrors"] > cal["n"]) or (
                        measured_at is not None and start is not None and measured_at > start):
                    P.add(("run.json", "calibration"))  # RUN-9: measured before the run, on n cases
        policy = get(run, "suite", "executionPolicy")
        if isinstance(policy, dict) and as_number(policy.get("requirePasses")) is not None and \
                policy["requirePasses"] > policy["trialsPerCase"]:
            P.add(("run.json", "execution-policy"))
        return P

    # ---- §4.5 the outcome

    def verify(self):
        """(outcome, problems): every problem found (reading, RUN-3, §4.1, and §3.9 when the data could be read)."""
        if self._verdict is None:
            self._verdict = self._verify()
        return self._verdict

    def _verify(self):
        _, _, reading = self.read()
        problems = set(reading) | path_problems(self.folder.paths) | self.seal()
        if not reading:
            problems |= self.cross_file()
        if any(code != "withheld" for _, code in problems):
            outcome = "invalid"
        elif not self.folder.has("seal.json"):
            outcome = "unsealed"
        else:
            outcome = "intact"
        return outcome, problems

    @property
    def intact(self):
        return self.verify()[0] == "intact"


def _inverted(interval):
    """An interval whose low exceeds its high (§3.9 interval)."""
    return (isinstance(interval, dict) and as_number(interval.get("low")) is not None
            and as_number(interval.get("high")) is not None and interval["low"] > interval["high"])


def _uncovered(ranges, size):
    position = 0
    for start, end in sorted(ranges):
        if start > position:
            return True
        position = max(position, end)
    return position < size


# SEC-6: the OpenTelemetry GenAI attributes that carry content (and the deprecated gen_ai.prompt / gen_ai.completion).
CONTENT_ATTRIBUTES = {"gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions",
                      "gen_ai.tool.call.arguments", "gen_ai.tool.call.result", "gen_ai.prompt", "gen_ai.completion"}


def _carries_content(span):
    """Whether a span, or one of its events, carries a content attribute (SEC-6)."""
    holders = [span] + [e for e in span.get("events") or [] if isinstance(e, dict)]
    return any(isinstance(a, dict) and a.get("key") in CONTENT_ATTRIBUTES
               for h in holders for a in (h.get("attributes") or []) if isinstance(h.get("attributes"), list))


def _otlp_spans(request):
    for resource in request.get("resourceSpans", []) if isinstance(request, dict) else []:
        if not isinstance(resource, dict):
            continue
        for scope in (resource.get("scopeSpans") or []) + (resource.get("instrumentationLibrarySpans") or []):
            for span in scope.get("spans", []) if isinstance(scope, dict) else []:
                if isinstance(span, dict):
                    yield span


def _predicate_differs(pred, run):
    """SEAL-6 predicate: the seal's copy of run.json's producer, subject, deployment, suite, judges and endedAt."""
    def value(obj, *keys):
        x = get(obj, *keys, default=_MISSING)
        return _MISSING if x is None else x

    pairs = [
        (value(pred, "producer", "name"), value(run, "producer", "name")),
        (value(pred, "producer", "version"), value(run, "producer", "version")),
        (value(pred, "subject", "ref"), value(run, "subject", "ref")),
        (value(pred, "subject", "version"), value(run, "subject", "version")),
        (value(pred, "deployment", "ref"), value(run, "deployment", "ref")),
        (value(pred, "suite", "ref"), value(run, "suite", "ref")),
        (value(pred, "suite", "version"), value(run, "suite", "version")),
        (value(pred, "suite", "digest"), value(run, "suite", "digest")),
    ]
    if any(a != b for a, b in pairs):
        return True

    def judges(obj):
        return [(value(j, "model"), value(j, "rubricDigest")) for j in (obj.get("judges") or []) if isinstance(j, dict)]

    if judges(pred) != judges(run):
        return True
    closed, ended = pred.get("closedAt"), run.get("endedAt")
    a, b = time_key(closed), time_key(ended)
    return (a != b) if a is not None and b is not None else closed != ended


def _belongs(line, lane_name, single_lane):
    """SUM-3: a line belongs to the lane its lane names; with a single summary lane, a line without lane too."""
    return line.get("lane") == lane_name or ("lane" not in line and single_lane)


def _line_value(line, metric_id, kind):
    """SUM-4: the line's value for the metric, or None (not measured). A line that scores the metric twice is not
    measured for it (§3.9 metric)."""
    state = line.get("state")
    if state in ABSENT_STATES:
        return None
    values = [s.get("value") for s in line.get("scores", []) if s.get("metric") == metric_id]
    if len(values) > 1:
        return None
    if kind in ("rate", "verdict"):
        return None if state == "scored" else (1 if state == "passed" else 0)  # scored: no verdict
    return values[0] if values else None


def _summary_wrong(summary, results, metric):
    """SUM-3 to SUM-5: whether any entry's N, n, notMeasured, sum or value is not what results.ndjson gives."""
    lanes = summary.get("lanes", [])
    single = len(lanes) == 1
    for lane in lanes:
        for e in lane.get("metrics", []):
            declaration = metric.get(e.get("metric"))
            if declaration is None or declaration.get("kind") not in METRIC_KINDS:
                continue  # undeclared: reported as metric; an unknown kind takes no part in summaries (§7.3)
            kind = declaration["kind"]
            N, values = 0, []
            for _, o in results:
                if "trial" in o or o.get("path") != e.get("path") or not _belongs(o, lane.get("lane"), single):
                    continue
                if o.get("state") == "not_applicable":
                    continue
                N += 1
                v = _line_value(o, e.get("metric"), kind)
                if v is not None:
                    values.append(v)
            n = len(values)
            total = math.fsum(values)
            value = None if n == 0 else (total if kind == "count" else total / n)
            if e.get("N") != N or e.get("n") != n or e.get("notMeasured") != N - n:
                return True
            if "sum" in e and not close_enough(e["sum"], total):
                return True
            if "aggregate" in e:  # SUM-5: the producer's value (pass@k, F1, a median); only null when n is 0
                if n == 0 and e.get("value") is not None:
                    return True
            elif (e.get("value") is None) != (value is None) or (value is not None and not close_enough(e["value"], value)):
                return True
    return False


# ---------------------------------------------------------------------------- operations: run, seal, chain, view

def op_run(directory, policy=None, anchors=None):
    """§4.5: the outcome and every problem; with a trust policy, signedBy; with a list of trusted run hashes,
    anchored. Both stronger levels hold only for an intact run."""
    run = Run(directory, policy)
    outcome, problems = run.verify()
    out = {"outcome": outcome, "problems": sort_problems(problems)}
    withheld = sum(1 for _, code in problems if code == "withheld")
    if withheld:
        out["withheld"] = withheld  # §4.5: "intact, n withheld"
    if policy is not None:
        out["signedBy"] = []
        if outcome == "intact" and run.folder.has("attestation.dsse.json"):
            result = verify_signature(run.folder.read("attestation.dsse.json"), run.folder.read("seal.json"),
                                      IN_TOTO_TYPE, policy)
            out["signedBy"] = result["verifiesFor"]
    if anchors is not None:
        if not isinstance(anchors, list) or not all(isinstance(h, str) for h in anchors):
            raise InputError("anchors are a JSON list of run hashes")
        # The run hash of an intact run is its seal's (equal to the recomputed one; a withheld blob leaves the
        # manifest impossible to recompute).
        out["anchored"] = outcome == "intact" and (
            "anchors" in MUTATIONS or run.seal_doc()[0]["predicate"]["runHash"] in anchors)
    return out


def op_seal(directory, policy=None):
    run = Run(directory, policy)
    manifest = run.folder.manifest()
    return {"manifest": manifest, "runHash": sha256_hex(manifest.encode("utf-8")), "problems": sort_problems(run.seal())}


def op_chain(directory):
    return {"problems": sort_problems(Run(directory).chain()["problems"])}


def op_view(directory, at, policy=None):
    """§4.3 and the shape of spec 09 §9.2.1."""
    now = parse_time(at)
    run = Run(directory, policy)
    results = run.objects("results.ndjson")
    order = {o["resultId"]: i for i, (_, o) in reversed(list(enumerate(results)))}
    sealed = {o["resultId"]: o.get("state") for _, o in reversed(results)}
    effective, review, waivers, withheld = {}, {}, [], []
    for e in run.verified_events():
        kind, target = read_overlay_kind(e.get("kind")), e["target"]
        if kind in ("override", "adjudicate") and target.get("result") in order:
            effective[target["result"]] = e
        elif kind in ("approve", "reject"):
            if "result" in target:
                if target["result"] in order:
                    review[target["result"]] = e
            elif not any(k in target for k in ("requirement", "blob")):
                review["run"] = e
        elif kind == "waive":
            active = parse_time(e["at"]) <= now < parse_time(e["expires"])
            waivers.append({"target": {k: v for k, v in target.items() if k not in ("run", "runHash")},
                            "expires": e["expires"], "active": active, "event": e["eventId"]})
    withheld = run.withheld_blobs()  # authorized redactions only (OVL-10)
    chain = run.chain()
    return {
        "results": [{"resultId": r, "sealedState": sealed[r], "effectiveState": e["state"], "event": e["eventId"]}
                    for r, e in sorted(effective.items(), key=lambda item: order[item[0]])],
        "reviews": [{"target": t, "status": e["kind"], "event": e["eventId"]}
                    for t, e in sorted(review.items(), key=lambda item: -1 if item[0] == "run" else order[item[0]])],
        "waivers": waivers,
        "withheld": withheld,
        "unsealedEvents": sum(1 for _, start, _, _, _ in chain["lines"] if start >= chain["verified_end"]),
    }


# ---------------------------------------------------------------------------- operations: checkpoint (CKP-7)

def document_verdicts(schema, path):
    """{'writer', 'reader', 'document'}: the verdicts for one JSON document or every line of an NDJSON file."""
    data = Path(path).read_bytes()
    try:
        if str(path).endswith((".ndjson", ".jsonl")):
            if ndjson_framing(data):
                raise EncodingProblem("framing")
            docs = [load_json_bytes(raw, object_only=False) for _, _, raw in ndjson_lines(data)]
        else:
            docs = [load_json_bytes(data, object_only=False)]
    except EncodingProblem:
        return {"writer": "invalid", "reader": "invalid", "documents": None}
    verdict = {side: "valid" if all(document_ok(side, schema, d) for d in docs) else "invalid"
               for side in ("writer", "reader")}
    verdict["documents"] = docs
    return verdict


def manifest_problems(m):
    """[CKP-7] codes for a manifest the reader accepts, in code order."""
    state, outcome = m.get("state"), m.get("outcome")
    if state not in CHECKPOINT_STATES or (outcome is not None and outcome not in CHECKPOINT_OUTCOMES):
        return ["unverifiable"]
    if state != "decided" or outcome == "aborted":
        return []
    decision, given = m.get("decision"), m.get("decisionInput")
    if not isinstance(decision, dict) or not isinstance(given, dict):
        return ["unverifiable"]
    if decision.get("outcome") not in DECISION_OUTCOMES or any(
            l.get("status") not in DECISION_LANE_STATUSES for l in decision.get("lanes", [])) or any(
            isinstance(l.get("result"), dict) and l["result"].get("status") not in INPUT_STATUSES
            for l in given.get("lanes", [])):
        return ["unverifiable"]
    problems = set()
    try:
        recomputed = aef_decide.decide(given)
    except Exception:  # the input cannot be decided
        recomputed = None
    if recomputed is None or _decision_key(recomputed) != _decision_key(decision):
        problems.add("decision")
    lane_key = lambda l: (l.get("lane"), l.get("blocking"), l.get("freshness"))
    if [lane_key(l) for l in m["lanes"]] != [lane_key(l) for l in given["lanes"]]:
        problems.add("lanes")
    given_lanes = {}
    for l in given["lanes"]:
        given_lanes.setdefault(l["lane"], l)
    for lane in m["lanes"]:
        has_result = get(given_lanes.get(lane["lane"]), "result") is not None
        if bool(lane["runs"]) != has_result:
            problems.add("evidence")
    if outcome != decision.get("outcome"):
        problems.add("outcome")
    if given.get("subjectVersion") != get(m, "subject", "version"):
        problems.add("version")
    return sorted(problems)


def _decision_key(d):
    """The fields of a decision this version defines (CKP-7: only those are compared)."""
    return (d.get("outcome"),
            [(l.get("lane"), l.get("status"), l.get("blocking"), list(l.get("axes") or [])) for l in d.get("lanes", [])],
            list(d.get("reasons", [])))


def op_checkpoint(path):
    verdict = document_verdicts("checkpoint", path)
    docs = verdict.pop("documents")
    verdict["problems"] = manifest_problems(docs[0]) if verdict["reader"] == "valid" and isinstance(docs[0], dict) else None
    return verdict


# ---------------------------------------------------------------------------- operations: lanes (§5.3, CKP-8)

def run_folders(root):
    """The run folders under root, found by their run.json (RUN-1), in path order; a run folder holds no other run."""
    root = Path(root)
    if not root.is_dir():
        raise InputError(f"{root}: not a folder")
    folders = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        if "run.json" in filenames:
            folders.append(dirpath)
            dirnames[:] = []
    return sorted(folders, key=lambda d: utf8_key(Path(d).relative_to(root).as_posix()))


class Store:
    """The runs under a folder, found by their run.json (RUN-1), by runId and the run's run hash (CKP-8, SEAL-4: its
    seal's when seal.json is valid, else recomputed from the files)."""

    def __init__(self, root, policy=None):
        self.index = {}
        for d in run_folders(root):
            run = Run(d, policy)
            doc = run.run_doc
            if doc is not None and isinstance(doc.get("runId"), str):
                self.index.setdefault((doc["runId"], run.claimed_run_hash()), run)

    def find(self, ref):
        if not isinstance(ref, dict):
            return None
        return self.index.get((ref.get("runId"), ref.get("runHash")))


def _eligible(run, binding, version_needed=True):
    """LANE-1: intact, closed completed, live, bound to the checkpoint's subject (and deployment, when it names one)
    and to the rule's suite, and with a subject.version (not asked of a comparison's baseline)."""
    doc = run.run_doc or {}
    return (run.intact and doc.get("status") == "completed"
            and read_target_mode(get(doc, "execution", "targetMode")) == "live"
            and (not version_needed or get(doc, "subject", "version") is not None)
            and _bound(doc, binding))


def _bound(doc, binding):
    """LANE-1's binding: (checkpoint subject.ref, checkpoint subject.deployment or None, rule suite or None)."""
    if "subject-binding" in MUTATIONS:
        return True
    subject_ref, deployment, suite = binding
    if get(doc, "subject", "ref") != subject_ref:
        return False
    if deployment is not None and get(doc, "deployment", "ref") != deployment:
        return False
    if isinstance(suite, dict):
        if get(doc, "suite", "ref") != suite.get("ref"):
            return False
        if any(k in suite and get(doc, "suite", k) != suite[k] for k in ("version", "digest")):
            return False
    return True


def _closed_at(run):
    """LANE-9: run.json endedAt (for an intact run, its seal's closedAt, the same instant)."""
    if run.intact:
        doc, valid = run.seal_doc()
        if valid:
            return doc["predicate"]["closedAt"]
    ended = (run.run_doc or {}).get("endedAt")
    return ended if time_key(ended) is not None else None


def _summary_entry(run, lane, metric_id, path):
    summary = run.read()[0].get("summary.json")
    for l in (summary or {}).get("lanes", []) if isinstance(summary, dict) else []:
        if l.get("lane") == lane:
            for e in l.get("metrics", []):
                if e.get("metric") == metric_id and e.get("path") == path:
                    return e
    return None


_OPS = {">=": lambda a, b: a >= b, ">": lambda a, b: a > b, "<=": lambda a, b: a <= b, "<": lambda a, b: a < b}


def _threshold(rule, runs):
    """LANE-2."""
    statuses = []
    op = _OPS.get(rule.get("op"))
    minimum = as_number(rule.get("minimumN"))
    for run in runs:
        e = _summary_entry(run, rule.get("lane"), rule.get("metric"), rule.get("path"))
        if (e is None or e.get("n") == 0 or as_number(e.get("value")) is None or op is None
                or as_number(rule.get("value")) is None or (minimum is not None and e.get("n") < minimum)):
            statuses.append("not_measured")
        else:
            statuses.append("passed" if op(float(e["value"]), float(rule["value"])) else "failed")
    return "failed" if "failed" in statuses else "not_measured" if "not_measured" in statuses else "passed"


def _severity(rule, runs):
    """LANE-3: failed on a failure worse than max; else not_measured on an undecided line or fewer decided lines
    than minimumN (at least 1); else passed. not_applicable and scored lines take no part."""
    limit = {"none": 0, "low": 1, "medium": 2, "high": 3}.get(rule.get("max"))
    if limit is None:
        return "not_measured"  # a max this version does not know (§7.3)
    undecided, decided = False, 0
    for run in runs:
        for _, o in run.objects("results.ndjson"):
            if "trial" in o:
                continue
            state = o.get("state")
            if state in ("failed", "warn"):
                decided += 1
                if SEVERITY_ORDER[read_severity(o.get("severity"))] > limit:
                    return "failed"
            elif state == "passed":
                decided += 1
            elif state in ("inconclusive", "not_measured", "skipped", "error", "pending"):
                undecided = True
    if "severity-evidence" in MUTATIONS:
        return "passed"
    minimum = rule.get("minimumN") if as_number(rule.get("minimumN")) is not None else 1
    return "not_measured" if undecided or decided < max(minimum, 1) else "passed"


def _axis_value(doc, axis):
    def v(*keys):
        x = get(doc, *keys, default=_MISSING)
        return x
    if axis == "subject":
        return v("subject", "ref")
    if axis == "suite":
        return v("suite", "ref"), v("suite", "version")
    if axis == "suite-content":
        return v("suite", "digest")
    if axis in ("judges", "rubrics"):
        judges = doc.get("judges", _MISSING)
        if not isinstance(judges, list):
            return judges
        field = "model" if axis == "judges" else "rubricDigest"
        return tuple(get(j, field, default=_MISSING) for j in judges)
    if axis == "target-mode":
        return v("execution", "targetMode")
    if axis == "deployment":
        return v("deployment", "ref")
    if axis == "producer":
        return v("producer", "name"), v("producer", "version")
    raise KeyError(axis)


def _measured_values(run, lane, path, metric_id, kind):
    """LANE-7: {caseId: value} of the run's measured lines at path in lane (SUM-3, SUM-4), trial lines excluded."""
    summary = run.read()[0].get("summary.json")
    single = isinstance(summary, dict) and len(summary.get("lanes", [])) == 1
    out = {}
    for _, o in run.objects("results.ndjson"):
        if "trial" in o or o.get("path") != path or not _belongs(o, lane, single):
            continue
        if o.get("state") == "not_applicable":
            continue
        value = _line_value(o, metric_id, kind)
        if as_number(value) is not None:
            out.setdefault(o.get("caseId"), value)
    return out


def _comparison(rule, runs, baseline, binding):
    """LANE-5 to LANE-8: (status, axes or None). The baseline is checked as the candidate is, but for its version."""
    if len(runs) != 1 or runs[0] is None or not _eligible(runs[0], binding):
        return "not_measured", None
    candidate = runs[0]
    if baseline is None or not _eligible(baseline, binding, version_needed=False):
        return "not_measured", None
    bdoc = baseline.run_doc
    cdoc = candidate.run_doc
    differing = [a for a in rule.get("axes", [])
                 if a not in AXES or _axis_value(cdoc, a) != _axis_value(bdoc, a)]
    if differing:
        return "incomparable", differing
    declared = {}
    for m in (candidate.read()[0].get("metrics.json") or {}).get("metrics", []):
        declared.setdefault(m.get("id"), m)
    metric = declared.get(rule.get("metric"))
    direction = read_direction(metric.get("direction")) if metric else "none"
    if direction == "none" or metric.get("kind") not in METRIC_KINDS:
        return "not_measured", None
    kind = metric["kind"]
    c = _measured_values(candidate, rule.get("lane"), rule.get("path"), rule.get("metric"), kind)
    b = _measured_values(baseline, rule.get("lane"), rule.get("path"), rule.get("metric"), kind)
    regressed = improved = 0
    for case in c.keys() & b.keys():
        cv, bv = float(c[case]), float(b[case])
        if cv == bv:
            continue
        worse = cv < bv if direction == "higher_better" else cv > bv
        regressed += worse
        improved += not worse
    m = regressed + improved
    minimum = rule.get("minimumPairs")
    if as_number(minimum) is None or m < minimum:
        return "not_measured", None
    significance = as_number(rule.get("significance"))
    if significance is None:
        return "not_measured", None
    p = Fraction(binomial_tail(m, regressed), 2 ** m)
    return ("failed" if p <= Fraction(float(significance)) else "passed"), None


def binomial_tail(m, r):
    """Σ_{k=r}^{m} C(m, k), exactly (LANE-8). The terms follow from one another (C(m,k+1) = C(m,k)·(m−k)/(k+1)), so
    the sum costs O(m) big-integer steps; the shorter side is summed, the other taken from 2^m."""
    if r <= 0:
        return 2 ** m
    if r > m:
        return 0
    if r > m - r:                        # few terms on the upper side: sum them
        term, total = comb(m, r), 0
        for k in range(r, m + 1):
            total += term
            term = term * (m - k) // (k + 1)
        return total
    term, below = 1, 0                    # few terms below r: sum those and subtract
    for k in range(0, r):
        below += term
        term = term * (m - k) // (k + 1)
    return 2 ** m - below


def lane_result(rule, runs, baseline, version, fallback_time, binding):
    """LaneResult (§5.3): the result the decision function takes for one lane, or None.
    runs: the lane's runs in lane order, each a Run or None (not found); binding: see _bound."""
    found = [r for r in runs if r is not None]
    if not found:
        return None
    subject_version = version
    for r in found:
        v = get(r.run_doc, "subject", "version")
        if v is not None and v != version:
            subject_version = v
            break
    closings = [(time_key(t), i, t) for i, t in enumerate(_closed_at(r) for r in found) if t is not None]
    oldest = min(closings)[2] if closings else fallback_time
    kind = rule.get("kind") if isinstance(rule, dict) else None
    axes = None
    if kind not in RULE_KINDS:
        status = "not_measured"
    elif kind == "comparison":
        status, axes = _comparison(rule, runs, baseline, binding)
    elif not all(r is not None and _eligible(r, binding) for r in runs):
        status = "not_measured"  # LANE-1: fails closed (LANE-4: every run eligible)
    elif kind == "threshold":
        status = _threshold(rule, found)
    elif kind == "severity":
        status = _severity(rule, found)
    else:
        needed = rule.get("runs")
        status = "passed" if as_number(needed) is not None and len(found) >= needed else "not_measured"
    result = {"status": status, "subjectVersion": subject_version, "oldestClosedAt": oldest}
    if status == "incomparable":
        result["axes"] = axes
    return result


def op_lanes(checkpoint_path, runs_dir, at=None, policy=None):
    """§5.3 and CKP-8. at: the evaluation time of an undecided checkpoint (LANE-9); policy: the caller's trust policy
    (authorized redactions, CKP-8)."""
    m = load_json_file(checkpoint_path)
    if not isinstance(m, dict) or not isinstance(m.get("lanes"), list):
        raise InputError(f"{checkpoint_path}: not a checkpoint manifest")
    store = Store(runs_dir, policy)
    given = m.get("decisionInput") if m.get("state") == "decided" else None
    given = given if isinstance(given, dict) else None
    fallback = given["evaluatedAt"] if given and "evaluatedAt" in given else (at or utc_now())
    version = get(m, "subject", "version")
    recorded = {}
    for l in (given or {}).get("lanes", []):
        recorded.setdefault(l.get("lane"), l)
    lanes, problems = [], set()
    for lane in m["lanes"]:
        name, rule = lane.get("lane"), lane.get("rule") or {}

        def look(ref):
            run = store.find(ref)
            where = f"lanes/{name}/runs/{get(ref, 'runId')}"
            if run is None:
                problems.add((where, "run-missing"))
            elif not run.intact:
                problems.add((where, "run-unverified"))
            return run

        runs = [look(ref) for ref in lane.get("runs", [])]
        baseline = look(rule["baseline"]) if rule.get("kind") == "comparison" and isinstance(rule.get("baseline"), dict) else None
        binding = (get(m, "subject", "ref"), get(m, "subject", "deployment"),
                   rule.get("suite") if isinstance(rule.get("suite"), dict) else None)
        result = lane_result(rule, runs, baseline, version, fallback, binding)
        lanes.append({"lane": name, "result": result})
        if given is not None and name in recorded:
            before = recorded[name].get("result")
            where = f"lanes/{name}"
            if (before is None) != (result is None):
                problems.add((where, "lane-result"))
            elif result is not None:
                if before.get("status") != result["status"] or list(before.get("axes") or []) != list(result.get("axes") or []):
                    problems.add((where, "lane-result"))
                if before.get("subjectVersion") != result["subjectVersion"]:
                    problems.add((where, "lane-version"))
                a, b = time_key(before.get("oldestClosedAt")), time_key(result["oldestClosedAt"])
                if (a != b) if a is not None and b is not None else before.get("oldestClosedAt") != result["oldestClosedAt"]:
                    problems.add((where, "oldest-closed"))
    return {"lanes": lanes, "problems": sort_problems(problems)}


# ---------------------------------------------------------------------------- operations: signatures (§4.4)

def _der_element(data, pos):
    """(tag, content, end) of a DER element (lengths of up to four bytes); ValueError when it does not fit."""
    if pos + 2 > len(data):
        raise ValueError("truncated DER")
    tag, first, pos = data[pos], data[pos + 1], pos + 2
    if first < 0x80:
        length = first
    elif 0x81 <= first <= 0x84:
        count = first & 0x7F
        length = int.from_bytes(data[pos:pos + count], "big")
        pos += count
    else:
        raise ValueError("unsupported DER length")
    if pos + length > len(data):
        raise ValueError("truncated DER")
    return tag, data[pos:pos + length], pos + length


def _pem_der(pem):
    lines = [line.strip() for line in str(pem).strip().splitlines()]
    if len(lines) < 3 or lines[0] != "-----BEGIN PUBLIC KEY-----" or lines[-1] != "-----END PUBLIC KEY-----":
        raise ValueError("not a PEM PUBLIC KEY block")
    return base64.b64decode("".join(lines[1:-1]), validate=True)


def _spki_algorithm(der):
    """The AlgorithmIdentifier's OID bytes of a SubjectPublicKeyInfo, or ValueError when it is not one."""
    tag, body, end = _der_element(der, 0)
    if tag != 0x30 or end != len(der):
        raise ValueError("not a SubjectPublicKeyInfo")
    tag, algorithm, pos = _der_element(body, 0)
    bits_tag, _, end = _der_element(body, pos)
    if tag != 0x30 or bits_tag != 0x03 or end != len(body):
        raise ValueError("not a SubjectPublicKeyInfo")
    oid_tag, oid, _ = _der_element(algorithm, 0)
    if oid_tag != 0x06:
        raise ValueError("not an AlgorithmIdentifier")
    return oid


def policy_allows(policy, identity, action):
    """SIG-4: whether the trust policy lets this identity do this beyond signing ("may": ["redact"])."""
    keys = policy.get("keys") if isinstance(policy, dict) else None
    return any(isinstance(k, dict) and k.get("identity") == identity and action in (k.get("may") or [])
               for k in keys or [])


def load_policy(policy):
    """[(identity, keyid, public key or None when its algorithm is not supported)], in policy order (SIG-4). The key
    id is computed from the key (SIG-3), never read from the policy."""
    if not isinstance(policy, dict) or not isinstance(policy.get("keys"), list):
        raise InputError("a trust policy is {\"keys\": [{\"identity\": ..., \"publicKey\": <SPKI PEM>}]}")
    keys = []
    for entry in policy["keys"]:
        if not isinstance(entry, dict) or not isinstance(entry.get("identity"), str):
            raise InputError("a trust policy key has an identity and a publicKey")
        try:
            der = _pem_der(entry.get("publicKey"))
            _spki_algorithm(der)
        except (ValueError, TypeError) as error:
            raise InputError(f"the policy key for {entry['identity']} is not a SubjectPublicKeyInfo: {error}") from None
        try:
            key = aef_crypto.load_spki_der(der)
            kid = aef_crypto.keyid(key)
        except ValueError:
            key, kid = None, "sha256:" + sha256_hex(der)  # another algorithm (RSA, another curve): unsupported
        keys.append((entry["identity"], kid, key))
    return keys


def _infer_payload_type(data):
    try:
        doc = load_json_bytes(data)
    except EncodingProblem:
        return IN_TOTO_TYPE
    return IN_TOTO_TYPE if doc.get("_type") == "https://in-toto.io/Statement/v1" else CHECKPOINT_TYPE


def verify_signature(envelope_bytes, file_bytes, payload_type, policy):
    """§4.4: {'envelopeResult', 'signatures', 'verifiesFor'}."""
    keys = load_policy(policy)
    try:
        envelope = load_json_bytes(envelope_bytes)
        if not isinstance(envelope.get("signatures"), list) or not envelope["signatures"]:
            raise aef_crypto.EnvelopeError("an envelope has at least one signature")
        parsed = aef_crypto.parse_envelope(envelope)
    except (EncodingProblem, aef_crypto.EnvelopeError, ValueError):
        return {"envelopeResult": "malformed", "signatures": [], "verifiesFor": []}
    mismatch = parsed.payload != file_bytes or parsed.payload_type != payload_type
    message = aef_crypto.pae(parsed.payload_type, parsed.payload)
    signatures, verified_ids, untrusted_ids = [], set(), []
    for kid, sig in parsed.signatures:
        if kid:
            candidates = [k for k in keys if k[1] == kid]
            if not candidates:
                if "untrusted-key" in MUTATIONS:  # the mutation: a key the policy does not list is believed
                    signatures.append({"keyid": kid, "result": "verified", "identity": kid})
                    untrusted_ids.append(kid)
                else:
                    signatures.append({"keyid": kid, "result": "untrusted-key"})
                continue
            identity, _, key = candidates[0]
            if key is None:
                signatures.append({"keyid": kid, "result": "unsupported-algorithm"})
            elif key.verify(message, sig):
                signatures.append({"keyid": kid, "result": "verified", "identity": identity})
                verified_ids.add(kid)
            else:
                signatures.append({"keyid": kid, "result": "invalid"})
        else:
            hit = next((k for k in keys if k[2] is not None and k[2].verify(message, sig)), None)
            if hit is None:
                signatures.append({"keyid": "", "result": "untrusted-key"})
            else:
                signatures.append({"keyid": hit[1], "result": "verified", "identity": hit[0]})
                verified_ids.add(hit[1])
    verifies_for = []
    if not mismatch:
        for identity, kid, _ in keys:
            if kid in verified_ids and identity not in verifies_for:
                verifies_for.append(identity)
        verifies_for += [kid for kid in untrusted_ids if kid not in verifies_for]
    return {"envelopeResult": "payload-mismatch" if mismatch else None, "signatures": signatures,
            "verifiesFor": verifies_for}


def op_signature(envelope_path, file_path, policy_path, payload_type=None):
    try:
        envelope_bytes, file_bytes = Path(envelope_path).read_bytes(), Path(file_path).read_bytes()
    except OSError as error:
        raise InputError(str(error)) from None
    return verify_signature(envelope_bytes, file_bytes, payload_type or _infer_payload_type(file_bytes),
                            load_json_file(policy_path))


# ---------------------------------------------------------------------------- operations: small ones

def op_paths(path):
    data = load_json_file(path)
    if isinstance(data, list) and all(isinstance(p, str) for p in data):
        return {"problems": sort_problems(path_problems(data))}
    if isinstance(data, list) and all(isinstance(v, dict) and isinstance(v.get("paths"), list) for v in data):
        return [{"name": v.get("name"), "problems": sort_problems(path_problems(v["paths"]))} for v in data]
    raise InputError(f"{path}: not a list of paths")


def op_result_id(run_id, case_id, path, trial=None):
    if trial is not None:
        try:
            trial = load_json_text(trial)
        except EncodingProblem:
            raise InputError(f"trial {trial!r} is not a JSON number") from None
    try:
        return {"resultId": result_id(run_id, case_id, path, trial)}
    except ValueError as error:
        raise InputError(str(error)) from None


def op_document(schema, path):
    verdict = document_verdicts(schema, path)
    docs = verdict.pop("documents")
    reads = {}
    if docs is not None and len(docs) == 1:
        reads = readings(schema, docs[0])
    verdict["reads"] = reads
    return verdict


def op_decide(path):
    value = load_json_file(path)
    given = value["input"] if isinstance(value, dict) and "input" in value and "subjectVersion" not in value else value
    try:
        return {"output": aef_decide.decide(given)}
    except (ValueError, KeyError, TypeError) as error:
        return {"error": str(error) or type(error).__name__}


def op_match(plan_path, runner_path):
    return {"matches": bool(aef_stream.matches(load_json_file(plan_path), load_json_file(runner_path)))}


def op_stream(events_path, plan_path):
    data = Path(events_path).read_bytes()
    try:
        plan_bytes = Path(plan_path).read_bytes()
        plan = load_json_bytes(plan_bytes)
    except (OSError, EncodingProblem) as error:
        raise InputError(f"{plan_path}: {error}") from None
    complete = data[:data.rfind(b"\n") + 1]  # STRM-2: a last line without LF is still being written
    try:
        events = [load_json_bytes(raw) for _, _, raw in ndjson_lines(complete)]
    except EncodingProblem as error:
        raise InputError(f"{events_path}: {error}") from None
    problems = aef_stream.verify(events, plan, sha256_hex(plan_bytes))
    return {"problems": [list(p) for p in problems]}


# ---------------------------------------------------------------------------- operations: plan conformance (STRM-4)

def op_conform(events_path, plan_path, runs_dir, policy=None):
    """[STRM-4]: the runs a job's stream names, checked against its plan. Each named run is found once: the folder
    whose run.json has its runId, whose run hash (SEAL-4) is the one the first evidence.produced for that runId
    announced, and that is intact (a blob withheld by a redaction the policy authorizes leaves it intact). A run not
    found has one problem, run-missing or run-hash; the runs found are checked at 'run:<runId>', and together against
    the plan's limits at 'job'."""
    try:
        data = Path(events_path).read_bytes()
    except OSError as error:
        raise InputError(str(error)) from None
    plan = load_json_file(plan_path)
    if not isinstance(plan, dict):
        raise InputError(f"{plan_path}: not a run plan")
    events = []
    for number, _, raw in ndjson_lines(data[:data.rfind(b"\n") + 1]):  # STRM-2: an unfinished last line is not read
        try:
            events.append(load_json_bytes(raw))
        except EncodingProblem as error:
            raise InputError(f"{events_path}:{number}: not an I-JSON object: {error}") from None

    named, announced, accepted = [], {}, None
    for e in events:
        kind = e.get("kind")
        if kind == "job.accepted" and accepted is None:
            accepted = e
        elif kind == "evidence.produced" and isinstance(e.get("runId"), str):
            announced.setdefault(e["runId"], e.get("runHash"))  # the first announcement counts
        if kind in ("job.sealed", "job.failed"):
            for run_id in e.get("runs") or []:
                if isinstance(run_id, str) and run_id not in named:
                    named.append(run_id)  # each run is checked once, however often it is named

    by_id = defaultdict(list)
    for d in run_folders(runs_dir):
        run = Run(d, policy)
        doc = run.run_doc
        if doc is not None and isinstance(doc.get("runId"), str):
            by_id[doc["runId"]].append(run)

    problems, found = set(), []
    for run_id in named:
        where, candidates = f"run:{run_id}", by_id.get(run_id, [])
        if not candidates:
            problems.add((where, "run-missing"))
            continue
        run = next((r for r in candidates if run_id in announced and r.claimed_run_hash() == announced[run_id]
                    and r.intact), None)
        if run is None:
            problems.add((where, "run-hash"))  # the run's only problem
            continue
        found.append(run)
        problems.update((where, code) for code in _plan_problems(run.run_doc, plan, accepted))
        if as_number(get(run.read()[0].get("summary.json"), "cost", "totalUsd")) is None:
            problems.add((where, "no-cost"))  # STRM-4: the budget cannot be checked without it

    limits = plan.get("limits") if isinstance(plan.get("limits"), dict) else {}
    costs = [get(r.read()[0].get("summary.json"), "cost", "totalUsd") for r in found]
    max_usd = as_number(limits.get("maxUsd"))
    if max_usd is not None and math.fsum(c for c in costs if as_number(c) is not None) > max_usd:
        problems.add(("job", "over-budget"))
    max_cases = as_number(limits.get("cases"))
    if max_cases is not None:
        cases = {o.get("caseId") for r in found for _, o in r.objects("results.ndjson")
                 if o.get("parentResultId") is None}
        if len(cases) > max_cases:
            problems.add(("job", "over-cases"))
    return {"problems": sort_problems(problems)}


def _plan_problems(doc, plan, accepted):
    """STRM-4's codes for one run found."""
    codes = set()
    if read_content_capture(doc.get("contentCapture")) != plan.get("contentCapture"):
        codes.add("content-capture")

    def judges(items):
        return [(get(j, "model", default=_MISSING), get(j, "rubricDigest", default=_MISSING))
                for j in items or [] if isinstance(j, dict)]

    if plan.get("judges") and judges(doc.get("judges")) != judges(plan["judges"]):
        codes.add("judges")
    provenance = doc.get("provenance")
    if accepted is None or not isinstance(provenance, dict) or any(
            provenance.get(k) != accepted.get(k) for k in ("planId", "planDigest", "jobId", "runnerId")):
        codes.add("provenance")
    if (get(doc, "subject", "ref") != get(plan, "subject", "ref")
            or get(doc, "subject", "version") != get(plan, "subject", "version")):
        codes.add("subject")
    suite = doc.get("suite")
    if not isinstance(suite, dict) or not any(
            isinstance(s, dict) and s.get("ref") == suite.get("ref") and s.get("version") == suite.get("version")
            and ("digest" not in s or s["digest"] == suite.get("digest")) for s in plan.get("suites") or []):
        codes.add("suite")
    if get(doc, "execution", "targetMode") != "live":
        codes.add("target-mode")
    return codes


# ---------------------------------------------------------------------------- command line

def dispatch(argv):
    """Performs one command (argv without the program name) and returns its JSON value. InputError or SystemExit
    on a usage error."""
    parser = argparse.ArgumentParser(prog="aef_verify.py", description=__doc__.split("\n\n")[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("run"); p.add_argument("dir"); p.add_argument("--policy"); p.add_argument("--anchors")
    p = sub.add_parser("seal"); p.add_argument("dir"); p.add_argument("--policy")
    p = sub.add_parser("chain"); p.add_argument("dir")
    p = sub.add_parser("view"); p.add_argument("dir"); p.add_argument("--at", required=True); p.add_argument("--policy")
    p = sub.add_parser("checkpoint"); p.add_argument("file")
    p = sub.add_parser("lanes"); p.add_argument("checkpoint"); p.add_argument("--runs", required=True); p.add_argument("--at")
    p.add_argument("--policy")
    p = sub.add_parser("signature"); p.add_argument("envelope"); p.add_argument("file"); p.add_argument("policy")
    p.add_argument("--payload-type")
    p = sub.add_parser("paths"); p.add_argument("file")
    p = sub.add_parser("result-id"); p.add_argument("run_id"); p.add_argument("case_id"); p.add_argument("path")
    p.add_argument("trial", nargs="?")
    p = sub.add_parser("document"); p.add_argument("schema"); p.add_argument("file")
    p = sub.add_parser("decide"); p.add_argument("file")
    p = sub.add_parser("match"); p.add_argument("plan"); p.add_argument("runner")
    p = sub.add_parser("stream"); p.add_argument("events"); p.add_argument("plan")
    p = sub.add_parser("conform"); p.add_argument("events"); p.add_argument("plan"); p.add_argument("runs")
    p.add_argument("--policy")
    a = parser.parse_args(argv)
    if a.command == "run":
        return op_run(a.dir, load_json_file(a.policy) if a.policy else None,
                      load_json_file(a.anchors) if a.anchors else None)
    policy = load_json_file(a.policy) if getattr(a, "policy", None) else None
    if a.command == "seal":
        return op_seal(a.dir, policy)
    if a.command == "chain":
        return op_chain(a.dir)
    if a.command == "view":
        try:
            parse_time(a.at)
        except ValueError:
            raise InputError(f"--at {a.at!r} is not an RFC 3339 UTC time") from None
        return op_view(a.dir, a.at, policy)
    if a.command == "checkpoint":
        return op_checkpoint(a.file)
    if a.command == "lanes":
        return op_lanes(a.checkpoint, a.runs, a.at, policy)
    if a.command == "signature":
        return op_signature(a.envelope, a.file, a.policy, a.payload_type)
    if a.command == "paths":
        return op_paths(a.file)
    if a.command == "result-id":
        return op_result_id(a.run_id, a.case_id, a.path, a.trial)
    if a.command == "document":
        return op_document(a.schema, a.file)
    if a.command == "decide":
        return op_decide(a.file)
    if a.command == "match":
        return op_match(a.plan, a.runner)
    if a.command == "conform":
        return op_conform(a.events, a.plan, a.runs, policy)
    return op_stream(a.events, a.plan)


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    try:
        value = dispatch(argv[1:])
    except InputError as error:
        print(f"aef_verify.py: {error}", file=sys.stderr)
        return 2
    sys.stdout.write(json.dumps(value, ensure_ascii=False, sort_keys=True) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
