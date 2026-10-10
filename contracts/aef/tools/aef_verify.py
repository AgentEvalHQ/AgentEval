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
      The decision function (§5.4, written here from DEC-1 to DEC-5) on a decision input, or on a decision vector's
      "input": {"output": {...}} or {"error": message} when the function refuses the input.
  match PLAN RUNNER
      [PLAN-7] (through aef_stream.py): tags, provider, zone, the plan's values, and the target mode (a plan without
      one asks for live; a manifest without targetModes gives live only). {"matches": true|false}
  stream EVENTS PLAN
      [STRM-3] (through aef_stream.py) against the plan's bytes. {"problems": [[where, problem], ...]}
  conform EVENTS PLAN RUNS [--policy POLICY]
      [STRM-4], written here from spec 06 §6.4 (not through aef_stream.py): the runs the stream's job.sealed and
      job.failed events name, found in the folder RUNS, against the plan. {"problems": [[path, code], ...]}, at
      'run:<runId>' and 'job'. The policy authorizes redactions (OVL-10).
      Plan durations and freshness follow ENC-9's one grammar (parse_duration). A PLAN the reader schema refuses (a
      timeout that is no duration among them) is an input error for match, stream and conform (spec 09 §9.3).

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
import stat
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
    "exception-evidence": "an exception in force waives a failed lane whatever evidence it names (DEC-2 step 6)",
    "run-copies": "CKP-8 takes the first folder holding a run, not an intact copy among several",
    "plan-where-when": "STRM-4 does not compare the plan's deployment and endpoint, or the job's time window",
    "logs-content": "SEC-6 does not look at logs.otlp.jsonl",
    "summary-duplicates": "SUM-9 duplicate entries, lane names and usage entries are accepted",
    "limits": "nothing beyond ENC-17's limits is refused",
    "trial-rollups": "a rollup is not compared with its trial lines, and a trial line needs no rollup (RES-8)",
    "rule-unknown": "a lane whose rule or runs hold a value this version does not know is compared like any other (CKP-8)",
    # The rulings made where two implementations disagreed (R4-4), and round 4's rules: each pinned by a vector.
    "rollup-later": "a second rollup for one case and path makes every rollup of it `trials`, the first included (W3-17)",
    "target-on-schema": "any results.ndjson problem, a schema one included, skips the overlay target check (W3-18)",
    "stream-limits": "a stream line beyond ENC-17's limits is read as an event (W4-1)",
    "budget-in-order": "a job's costs are added in binary64 in the order its runs are named (W4-2)",
    "input-only-lane": "a decision input's result for a lane the manifest does not have is not `evidence` (W4-3)",
    "run-named-twice": "a run a lane names twice counts twice (W4-5)",
    "judges-absent": "a run without judges is not the same as one with an empty list (W4-6)",
    "line-path-any": "any path ending in an NDJSON file name and :<n> orders as a line path (W4-10)",
    "trial-undecided": "an undecided trial line makes a severity lane not_measured in LANE-3 step 2 (R4-8a)",
    "rollup-tree": "a composite case's rollups need not form its tree (RES-8, R4-8b)",
    "events-whole-file": "the events file's framing is judged as a file, not line by line (OVL-5, R4-2, R4N-9)",
    "policy-loose": "a trust policy is not checked against its schema, and `may` is matched as a substring (SIG-4, R4-3)",
    "overlay-files-stop": "too many files under overlays/ stop the chain, so junk files void a redaction (OVL-5, R5-1)",
    "declared-version": "a document that declares 1.0 can make a lane or a manifest unverifiable (CKP-7, CKP-8, R5-4, R6-2)",
    "tree-across-cases": "a line may have a parent of another case (RES-5, R6-3)",
    "trial-under-plain": "a trial line may hang under a line that carries no trial (RES-8, R6-3)",
    "otlp-names": "spans under the pre-1.0 name instrumentationLibrarySpans are read too",
    "target-mode-live": "STRM-4 compares a run's target mode with live, not with the plan's targetMode",
    "cases-by-id": "STRM-4 counts a job's cases by caseId alone, so one case id in two suites counts once",
    "overlays-boundary": "a file named overlays, and a first segment Overlays in another case, are not path problems "
                         "(RUN-3)",
    "judges-same-list": "STRM-4 asks a run for the plan's whole list of judges, not some of them (R8-2)",
    "judges-guard": "a plan that names no judges leaves a run's judges unchecked (R9-3)",
    "judges-provider": "STRM-4 compares judges by model and rubricDigest, not by provider (R9-4)",
    "judges-once": "a run may name a judge once at most, even one the plan names twice (R9-4)",
    "summary-tolerance": "a summary's sum and value are compared within 1e-9, not exactly, so an exact mean passes "
                         "for SUM-5's division (R11-2)",
    "summary-overflow-mean": "a sum beyond binary64 is read as giving the exact mean as the value, not null (R10-3)",
    "judges-provider-strict": "a plan judge without a provider matches only a run judge without one (R10-6)",
    "judges-rubric-strict": "a plan judge without a rubricDigest matches only a run judge without one (R10-6)",
    "names-raw": "a file name that is not a Unicode string is reported as read, not with U+FFFD (F1)",
    "minor-per-document": "a value a lane reads counts as a later minor's by its own line or file, not its run's "
                          "run.json (CKP-8, VER-6, F4)",
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
MAX_SEAL = 40 * 1024 * 1024  # ENC-17: seal.json and a batch seal (they list every sealed file)
MAX_ENVELOPE = 56 * 1024 * 1024  # ENC-17: a DSSE envelope, the base64 of a seal
MAX_DEPTH = 64
MAX_LINES = 1_000_000
MAX_FILES = 100_000
MAX_OVERLAY_FILES = 1 + 2 * 9999  # ENC-17: the events file, and a seal and a signature for each of 9,999 batches
MAX_BLOB = 1 << 30
MAX_NDJSON = 1 << 30  # ENC-17: one NDJSON file
MAX_PATH = 255

def _writer_enum(ref):
    """The values the writer schema lists at ref ("file#/json/pointer" to an enum): what this version knows (§7.3,
    [VER-8]). Read from the schema, so the sets a reader checks against can never drift from it."""
    file, pointer = ref.split("#")
    node = json.loads((AEF_ROOT / "schemas" / "writer" / file).read_bytes())
    for step in pointer.strip("/").split("/"):
        node = node[int(step)] if isinstance(node, list) else node[step.replace("~1", "/").replace("~0", "~")]
    return frozenset(node)


STATES = _writer_enum("common.schema.json#/$defs/state/enum")  # closed (VER-9)
ABSENT_STATES = {"not_measured", "skipped", "error", "pending"}  # SUM-4: not measured (not_applicable is left out)
SEVERITY_ORDER = {"none": 0, "low": 1, "medium": 2, "high": 3, "critical": 4}
METRIC_KINDS = _writer_enum("metrics.schema.json#/properties/metrics/items/properties/kind/enum")
DIRECTIONS = _writer_enum("metrics.schema.json#/properties/metrics/items/properties/direction/enum")
RUN_STATUSES = _writer_enum("run.schema.json#/properties/status/enum")
TARGET_MODES = _writer_enum("run.schema.json#/properties/execution/properties/targetMode/enum")
GATE_OUTCOMES = _writer_enum("gate-decision.schema.json#/properties/outcome/enum")
COMPARABILITY = _writer_enum("gate-decision.schema.json#/properties/comparability/enum")
OVERLAY_KINDS = _writer_enum("overlay-event.schema.json#/properties/kind/enum")
CHECKPOINT_STATES = _writer_enum("checkpoint.schema.json#/properties/state/enum")
CHECKPOINT_OUTCOMES = _writer_enum("checkpoint.schema.json#/properties/outcome/anyOf/1/enum")
DECISION_OUTCOMES = _writer_enum("decision.schema.json#/properties/outcome/enum")
DECISION_LANE_STATUSES = _writer_enum("decision.schema.json#/$defs/laneStatus/enum")
INPUT_STATUSES = _writer_enum("decision.schema.json#/$defs/input/properties/lanes/items/properties/result/anyOf/1/properties/status/enum")
RULE_KINDS = {"threshold", "severity", "comparison", "evidence-present"}
AXES = tuple(json.loads((AEF_ROOT / "schemas" / "writer" / "common.schema.json").read_bytes())["$defs"]["axis"]["enum"])  # in order
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


def _max_json(rel):
    """ENC-17: the size limit of a JSON file of the run."""
    if rel.endswith(".dsse.json"):
        return MAX_ENVELOPE
    if rel == "seal.json" or re.fullmatch(r"overlays/seal-[0-9]{4}\.json", rel):
        return MAX_SEAL
    return MAX_JSON


def _beyond_limits(f, rel):
    """ENC-17: whether a JSON file of the run is beyond its size or the depth limit (checked on the bytes)."""
    if "limits" in MUTATIONS:
        return False
    return f.size(rel) > _max_json(rel) or nesting_depth(f.read(rel)) > MAX_DEPTH


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
        data = Path(path).read_bytes()
    except OSError as error:
        raise InputError(f"{path}: {error}") from None
    if len(data) > MAX_JSON or nesting_depth(data) > MAX_DEPTH:  # ENC-17: a JSON file (a trust policy, SIG-4)
        raise InputError(f"{path}: beyond ENC-17's limits for a JSON file")
    try:
        return load_json_bytes(data, object_only=False)
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


_TIME = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z")


def parse_time(text):
    """(seconds since the epoch, nanoseconds) of an RFC 3339 UTC time (ENC-8): exact, never rounded; a date that does
    not exist is refused, never rolled over. ValueError for anything else."""
    m = _TIME.fullmatch(text) if isinstance(text, str) else None
    if not m:
        raise ValueError(f"{text!r} is not an RFC 3339 UTC time")
    y, mo, d, h, mi, sec = (int(g) for g in m.groups()[:6])
    moment = datetime.datetime(y, mo, d, h, mi, sec, tzinfo=datetime.timezone.utc)  # raises on 2026-02-31
    return int(moment.timestamp()), int((m.group(7) or "").ljust(9, "0"))


# ENC-9: P, an optional <n>D, then an optional T with <n>H, <n>M or both in that order; n is 1-5 digits; at least one
# part, and no T without a part after it. Freshness and plan timeouts share it.
DURATION = re.compile(r"P(?:[0-9]{1,5}D(?:T(?:[0-9]{1,5}H(?:[0-9]{1,5}M)?|[0-9]{1,5}M))?"
                      r"|T(?:[0-9]{1,5}H(?:[0-9]{1,5}M)?|[0-9]{1,5}M))")
_DURATION_PART = re.compile(r"([0-9]{1,5})([DHM])")


def parse_duration(text):
    """The seconds of an ENC-9 duration; ValueError for anything else. (M follows T in the grammar: minutes.)"""
    if not isinstance(text, str) or not DURATION.fullmatch(text):
        raise ValueError(f"{text!r} is not a duration")
    return sum(int(n) * {"D": 86400, "H": 3600, "M": 60}[unit] for n, unit in _DURATION_PART.findall(text))


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


def reported(name: str) -> str:
    """§3.9: a file's name as a report and a manifest give it. A name that is not a Unicode string, which Python reads
    with surrogates (bytes that are not UTF-8 as surrogate escapes; an unpaired surrogate where names are UTF-16), has
    each ill-formed part replaced by U+FFFD, as a decoder replaces it."""
    if "names-raw" in MUTATIONS or not any("\ud800" <= c <= "\udfff" for c in name):
        return name
    if os.name == "nt":
        return name.encode("utf-16-le", "surrogatepass").decode("utf-16-le", "replace")
    return os.fsencode(name).decode("utf-8", "replace")


_LINE_PATH = re.compile(r"(results\.ndjson|evidence\.ndjson|gates\.ndjson|traces\.otlp\.jsonl|logs\.otlp\.jsonl"
                        r"|overlays/events\.ndjson):([0-9]+)")  # §3.9: the run's own NDJSON files, nothing else


_ANY_LINE_PATH = re.compile(r"(.*\.(?:ndjson|jsonl)):([0-9]+)")  # the "line-path-any" mutation (W4-10)


def path_key(path: str):
    """§3.9's order of paths: by their UTF-8 bytes, except that the '<file>:<line>' paths of one file go by line
    number as a number (results.ndjson:9 before results.ndjson:10). A run's paths hold no ':' (RUN-3), so a line path
    sorts exactly where its bytes would put it among every other path."""
    m = (_ANY_LINE_PATH if "line-path-any" in MUTATIONS else _LINE_PATH).fullmatch(path)
    if m and "line-order" not in MUTATIONS:
        return utf8_key(m.group(1) + ":"), int(m.group(2))
    return utf8_key(path), -1


def sort_problems(problems):
    """[path, code] pairs, ordered by path (path_key) and then by code; each pair once. A path is reported as §3.9
    says, and ordered so."""
    problems = {(reported(path), code) for path, code in problems}
    return [list(p) for p in sorted(problems, key=lambda p: (path_key(p[0]), utf8_key(p[1])))]


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


def defined_equal(written, recomputed) -> bool:
    """§3.6: a summary number SUM-5 or SUM-8 defines to one binary64 value (sum, value, a median, min or max) equals
    the recomputed one exactly, both read as binary64 (CONF-2)."""
    if "summary-tolerance" in MUTATIONS:
        return close_enough(written, recomputed)
    w = as_number(written)
    return w is not None and float(w) == float(recomputed)


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


_RUN_SUBJECT_KINDS = _writer_enum("run.schema.json#/properties/subject/properties/kind/enum")
_JUDGE_MODES = _writer_enum("run.schema.json#/properties/judges/items/properties/mode/enum")
_STIMULI = _writer_enum("run.schema.json#/properties/execution/properties/stimulus/enum")
_EVIDENCE_KINDS = _writer_enum("evidence.schema.json#/properties/kind/enum")
_ANNOTATOR_KINDS = _writer_enum("result.schema.json#/properties/annotator/properties/kind/enum")
_USAGE_ROLES = _writer_enum("result.schema.json#/properties/usage/items/properties/role/enum")
_SUMMARY_USAGE_ROLES = _writer_enum("summary.schema.json#/properties/usage/items/properties/role/enum")
_TAXONOMY_SCHEMES = _writer_enum("result.schema.json#/properties/attack/properties/taxonomy/items/properties/scheme/enum")
_SUMMARY_VERDICTS = _writer_enum("summary.schema.json#/properties/lanes/items/properties/metrics/items/properties/verdict/enum")
_THRESHOLD_OPS = _writer_enum("checkpoint.schema.json#/$defs/laneRule/oneOf/0/properties/op/enum")
_PLAN_CONTENT_CAPTURE = _writer_enum("run-plan.schema.json#/properties/contentCapture/enum")
_PLAN_TARGET_MODES = _writer_enum("run-plan.schema.json#/properties/targetMode/enum")
_SEVERITY_MAX = _writer_enum("checkpoint.schema.json#/$defs/laneRule/oneOf/1/properties/max/enum")
_SEALED_BY = _writer_enum("seal.schema.json#/properties/predicate/properties/sealedBy/enum")
_ISOLATIONS = _writer_enum("run-plan.schema.json#/properties/isolation/enum")
_CREDENTIAL_SCHEMES = _writer_enum("run-plan.schema.json#/properties/credentialRefs/items/properties/scheme/enum")
_CREDENTIAL_PURPOSES = _writer_enum("run-plan.schema.json#/properties/credentialRefs/items/properties/purpose/enum")

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
            ("execution.stimulus", _known(_STIMULI, "other")),
            ("contentCapture", read_content_capture), ("subject.kind", _known(_RUN_SUBJECT_KINDS, "other")),
            ("judges[*].mode", _known(_JUDGE_MODES, "other")),
            ("suite.executionPolicy.aggregation", _as_written), ("config.thresholds{*}.op", _as_written)],
    "summary": [("lanes[*].metrics[*].verdict", _known(_SUMMARY_VERDICTS, "inconclusive")),
                ("usage[*].role", _known(_SUMMARY_USAGE_ROLES, "other"))],
    "evidence": [("kind", _known(_EVIDENCE_KINDS, "other"))],
    "metrics": [("metrics[*].direction", read_direction)],
    "gate-decision": [("outcome", _known(GATE_OUTCOMES, "inconclusive")), ("comparability", read_comparability),
                      ("rule.strategy", _as_written)],
    # OVL-3: an assurance is shown only as far as it was verified; a document alone verifies nothing.
    "overlay-event": [("kind", read_overlay_kind), ("by.assurance", lambda v: "self-attested")],
    "seal": [("predicate.sealedBy", _known(_SEALED_BY, "ingest"))],
    "checkpoint": [("state", _known(CHECKPOINT_STATES, "unverifiable")),
                   ("outcome", lambda v: v if v is None or v in CHECKPOINT_OUTCOMES else "unverifiable"),
                   ("lanes[*].rule.kind", lambda v: v if v in RULE_KINDS else "not_measured"),
                   ("lanes[*].rule.max", lambda v: v if v in _SEVERITY_MAX else "not_measured"),
                   ("lanes[*].rule.op", lambda v: v if v in _THRESHOLD_OPS else "not_measured"),
                   ("budget.approvedBy.assurance", lambda v: "self-attested"),
                   ("decisionInput.exceptions[*].by.assurance", lambda v: "self-attested"),
                   ("lanes[*].rule.axes[*]", lambda v: v if v in AXES else "incomparable")],
    # A decision document: its outcome and lane statuses ([CKP-7]); a decision input's results ([DEC-2]).
    "decision": [("outcome", _known(DECISION_OUTCOMES, "unverifiable")),
                 ("lanes[*].status", _known(DECISION_LANE_STATUSES, "unverifiable")),
                 ("lanes[*].result.status", _known(INPUT_STATUSES, "not_measured")),
                 ("exceptions[*].by.assurance", lambda v: "self-attested")],
    "run-plan": [("provider", lambda v: v if v in ("local", "docker", "k8s") or
                  (isinstance(v, str) and re.fullmatch(r"ci:[a-z0-9-]{1,64}", v)) else "refused"),
                 ("isolation", _known(_ISOLATIONS, "refused")),
                 ("contentCapture", _known(_PLAN_CONTENT_CAPTURE, "refused")),
                 ("targetMode", _known(_PLAN_TARGET_MODES, "refused")),
                 ("credentialRefs[*].scheme", _known(_CREDENTIAL_SCHEMES, "refused")),
                 ("credentialRefs[*].purpose", _known(_CREDENTIAL_PURPOSES, "refused"))],
    "runner-event": [("kind", _known({"job.accepted", "job.refused", "plan.estimated", "spend.updated",
                                      "case.completed", "lane.completed", "evidence.produced", "job.cancelled",
                                      "job.failed", "job.sealed"}, "skipped")),
                     ("status", _known(INPUT_STATUSES, "not_measured")),  # lane.completed's status
                     ("limit", _as_written)],  # job.failed's limit
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
        paths, special = [], []
        for dirpath, dirnames, filenames in os.walk(self.root):  # followlinks=False: a linked folder is not entered
            rel = os.path.relpath(dirpath, self.root)
            prefix = "" if rel == "." else rel.replace(os.sep, "/") + "/"
            for name in list(dirnames):
                if os.path.islink(os.path.join(dirpath, name)):
                    dirnames.remove(name)
                    special.append(prefix + name)
            dirnames.sort()
            for name in filenames:
                # RUN-3: a link, pipe, socket or device is a path problem and is never followed or read.
                (paths if stat.S_ISREG(os.lstat(os.path.join(dirpath, name)).st_mode) else special).append(prefix + name)
        self.paths = sorted(paths, key=utf8_key)
        self.special = sorted(special, key=utf8_key)
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
        return "".join(f"{self.digest(p)}  {self.size(p)}  {reported(p)}\n" for p in files)  # §3.9: a name as reported

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
        if f.size(rel) > _max_json(rel):
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
        if len(lines) > MAX_LINES or len(data) > MAX_NDJSON:
            problems.add((rel, "limit"))
            return []
        out = []
        for number, start, raw in lines:
            where = f"{rel}:{number}"
            if "limits" not in MUTATIONS and (len(raw) > MAX_JSON or nesting_depth(raw) > MAX_DEPTH):  # ENC-18: at the line
                problems.add((where, "limit"))
                out.append((number, start, None))
                continue
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
            if sum(1 for p in f.paths if p not in ("seal.json", "attestation.dsse.json")
                   and not p.startswith("overlays/")) > MAX_FILES:
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
        if not self.folder.has("seal.json") or _beyond_limits(self.folder, "seal.json"):
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
        seals, others = {}, set()
        for p in f.paths:
            if not p.startswith("overlays/") or p == EVENTS:
                continue
            m = BATCH_SEAL.fullmatch(p)
            if m:
                seals[int(m.group(1))] = p
            elif not BATCH_SIGNATURE.fullmatch(p):
                others.add((p, "unexpected-file"))
        others |= {(p, "unexpected-file") for p in f.special if p.startswith("overlays/")}  # links, pipes: never read
        count = sum(1 for p in f.paths if p.startswith("overlays/")) + sum(1 for p in f.special if p.startswith("overlays/"))
        if "limits" not in MUTATIONS and count > MAX_OVERLAY_FILES and "overlay-files-stop" not in MUTATIONS:
            # ENC-17, OVL-5 (R5-1): limit once, the other files not listed, and the chain checked as usual.
            problems.add(("overlays", "limit"))
        elif "overlay-files-stop" in MUTATIONS and count > MAX_OVERLAY_FILES:
            self._chain = {"problems": {("overlays", "limit")}, "verified_end": 0, "lines": [], "batches": []}
            return self._chain
        else:
            problems |= others
        events = f.read(EVENTS) if f.has(EVENTS) else b""
        # OVL-5: a reader reads the events file as far as ENC-17 allows; a longer file is `limit`, once, and the rest
        # is not read. A batch reaching past what was read is refused like a batch seal beyond the limits.
        cut = events if "limits" in MUTATIONS else _within_limits(events)
        # More than ENC-17 allows: over 1 GiB, or more than 1,000,000 lines (an unfinished last line is no line).
        over = len(cut) < len(events) and (len(events) > MAX_NDJSON or b"\n" in events[len(cut):])
        readable = cut if over else events
        if over:
            problems.add((EVENTS, "limit"))
        before_chain = set(problems)
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
            if _beyond_limits(f, path):  # ENC-17: refused; it ends the verified prefix
                problems.add((path, "limit"))
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
            if len(readable) < end <= len(events):  # OVL-5: beyond what a reader reads; it ends the verified prefix
                problems.add((path, "limit"))
                verified, previous_ok = False, False
                continue
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
        if f.has(EVENTS) and "events-whole-file" in MUTATIONS and ndjson_framing(readable):
            # The mutation: the whole file's framing is judged, as before R4-2 and R4N-9 (OVL-5 now judges lines).
            lines = [(n, s, s + len(raw) + 1, None, False) for n, s, raw in ndjson_lines(readable)]
            self._chain = {"problems": before_chain | {(EVENTS, "encoding")}, "verified_end": 0, "lines": lines,
                           "batches": []}
            return self._chain
        # OVL-5: every line is judged by itself, inside the batches and after them; a last line without LF is still
        # being written. Over its limits, only the verified batches are read: nothing after them is.
        lines = self._events(readable[:verified_end] if over else readable, verified_end,
                             run_id, run_hash, problems) if f.has(EVENTS) else []
        self._chain = {"problems": problems, "verified_end": verified_end, "lines": lines, "batches": batches}
        return self._chain

    def _events(self, data, verified_end, run_id, run_hash, problems):
        """The LF-terminated lines of overlays/events.ndjson: (number, start, end, object or None, usable), with the
        event-invalid, event-id and target problems. A blank line, a CR or a leading byte-order mark makes that line
        event-invalid, wherever it is (OVL-5)."""
        result_ids = {o.get("resultId") for _, o in self.objects("results.ndjson")}
        results_read = "results.ndjson" in self.read()[1] and not any(  # OVL-2: "does not read"
            (p[0] == "results.ndjson" or p[0].startswith("results.ndjson:"))
            and (p[1] in ("encoding", "limit") or "target-on-schema" in MUTATIONS)
            for p in self.read()[2])
        seen, out = set(), []
        for number, start, raw in ndjson_lines(data):
            where = f"{EVENTS}:{number}"
            end = start + len(raw) + 1
            if "limits" not in MUTATIONS and (len(raw) > MAX_JSON or nesting_depth(raw) > MAX_DEPTH):  # ENC-18
                problems.add((where, "limit"))
                out.append((number, start, end, None, False))
                continue
            try:
                if b"\r" in raw or raw.startswith(b"\xef\xbb\xbf"):
                    raise EncodingProblem("OVL-5: the line breaks ENC-5")
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
        if _beyond_limits(f, "seal.json"):  # ENC-17: refused, not checked further
            self._seal = {("seal.json", "limit")}
            return self._seal
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
            if name in ("seal.json", "attestation.dsse.json") or name.startswith("overlays/"):
                problems.add((name, "subject-path"))
            elif names[name] == 1:
                subjects[name] = s["digest"]["sha256"]
        duplicated = {n for n, c in names.items() if c > 1 and n not in ("seal.json", "attestation.dsse.json")
                      and not n.startswith("overlays/")}
        for p in f.sealed_files():
            if names.get(p, 0) > 1:
                continue  # a duplicated subject's digests are not compared
            if p not in subjects:
                problems.add((p, "not-sealed"))
            elif "seal-digest" not in MUTATIONS and f.digest(p) != subjects[p]:
                problems.add((p, "digest"))
        withheld = None
        for name in sorted(set(subjects) | duplicated, key=utf8_key):
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
                if off and _container_carries_content(request, "resourceSpans", "scopeSpans"):
                    P.add((f"traces.otlp.jsonl:{n}", "content-capture"))  # SEC-6: resource and scope attributes

        if off and f.has("logs.otlp.jsonl") and "logs-content" not in MUTATIONS:
            for n, request in self.objects("logs.otlp.jsonl"):
                if any(_log_carries_content(record) for record in _otlp_log_records(request)) or \
                        _container_carries_content(request, "resourceLogs", "scopeLogs"):
                    P.add((f"logs.otlp.jsonl:{n}", "content-capture"))  # SEC-6

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
        rollups, trial_lines = Counter(), defaultdict(list)  # RES-8, per (caseId, path)
        by_id = {o["resultId"]: o for _, o in results}
        for _, o in results:
            if o.get("parentResultId") is not None:
                children[o["parentResultId"]].add(o["resultId"])
            if "trials" in o:
                rollups[(o.get("caseId"), o.get("path"))] += 1
            if "trial" in o:
                trial_lines[(o.get("caseId"), o.get("path"))].append(o)
        rollup_ids = {}  # RES-8: the resultId of the first rollup of each (caseId, path)
        for _, o in results:
            if "trials" in o:
                rollup_ids.setdefault((o.get("caseId"), o.get("path")), o["resultId"])
        seen = set()
        rollup_first = {}  # RES-8: the first rollup line of each (caseId, path)
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
            elif (parent is not None and parent in by_id and by_id[parent].get("caseId") != o.get("caseId")
                  and "tree-across-cases" not in MUTATIONS):
                P.add((where, "parent"))  # RES-5 (R6-3): a tree belongs to one case
            agg = o.get("aggregation")
            if isinstance(agg, dict):
                measured, total = agg["measured"], agg["total"]
                unmeasured = agg.get("unmeasured")
                counts = unmeasured if isinstance(unmeasured, dict) else {}  # RES-6: absent counts are 0
                counts_wrong = measured > total or total != len(children[o["resultId"]]) or sum(
                    v for v in counts.values() if as_number(v) is not None) != total - measured
                if counts_wrong or any(d not in children[o["resultId"]] for d in agg.get("decisive", [])):
                    P.add((where, "aggregation"))
            if o.get("parentResultId") is not None and "component" not in o:
                P.add((where, "component"))  # RES-5
            if children[o["resultId"]] and not isinstance(o.get("aggregation"), dict):
                P.add((where, "aggregation"))  # RES-5: a node with children has aggregation
            parent = by_id.get(o.get("parentResultId"))
            if parent is not None and "trial" in parent and o.get("trial") != parent["trial"]:
                P.add((where, "trials"))  # RES-8: a trial's tree carries its trial
            elif parent is not None and "trial" in o and "trial" not in parent and "trial-under-plain" not in MUTATIONS:
                P.add((where, "trials"))  # RES-8 (R6-3): a trial line's parent carries trial: trials are whole trees
            trials = o.get("trials")
            path = o.get("path")
            if isinstance(trials, dict) and not MUTATIONS & {"trial-rollups", "rollup-tree"}:
                # RES-8 (R5-5): the rollups form the case's tree as its trial lines do, whatever the paths' spelling.
                parent_paths = set()
                for t in trial_lines.get((o.get("caseId"), path), []):
                    up = by_id.get(t.get("parentResultId")) if t.get("parentResultId") is not None else None
                    parent_paths.add(up.get("path") if up is not None else None)
                if len(parent_paths) > 1:
                    P.add((where, "trials"))  # trial lines whose parents are at several paths, or roots and not
                elif parent_paths:
                    (up_path,) = parent_paths
                    above = rollup_ids.get((o.get("caseId"), up_path)) if up_path is not None else None
                    # R5N-3: checked when the case has a rollup there (a running case may not have it yet)
                    if (up_path is None or above is not None) and o.get("parentResultId") != above:
                        P.add((where, "trials"))
            if isinstance(trials, dict):
                own = trial_lines.get((o.get("caseId"), o.get("path")), [])
                if "trial-rollups" in MUTATIONS:
                    own = []
                second = rollup_first.setdefault((o.get("caseId"), o.get("path")), n) != n
                if "rollup-later" in MUTATIONS:
                    second = rollups[(o.get("caseId"), o.get("path"))] > 1
                if trials["passed"] > trials["n"] or (second and "trial-rollups" not in MUTATIONS) or (own and (
                        trials["n"] != len(own) or trials["passed"] != sum(1 for t in own if t.get("state") == "passed")
                        or trials.get("agree") != (len({t.get("state") for t in own}) == 1))):
                    P.add((where, "trials"))  # RES-8
            if closed and "trial" in o and rollups[(o.get("caseId"), o.get("path"))] == 0 and "trial-rollups" not in MUTATIONS:
                P.add((where, "trials"))  # RES-8: a trial line whose case and path have no rollup
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
            roles = [(u["role"], u.get("model", _MISSING)) for u in o.get("usage") or [] if isinstance(u, dict) and "role" in u]
            if (started is not None and ended is not None and ended < started) or len(roles) != len(set(roles)):
                P.add((where, "result-times"))
            if _inverted(get(o, "uncertainty", "ci")):
                P.add((where, "interval"))
            if get(o, "attack", "success") is True and o.get("state") == "passed":
                P.add((where, "attack"))  # an attack that succeeded is not a pass for the subject
            link = o.get("traceLink")
            if isinstance(link, dict) and not span_known(link["traceId"], link.get("spanId")):
                P.add((where, "trace-link"))

        # blobs, and files RUN-2 does not list
        for p in f.paths:
            if _is_blob_path(p) and f.digest(p) != p.rsplit("/", 1)[1]:
                P.add((p, "blob-digest"))
        seal_doc, seal_valid = self.seal_doc()
        sealed = {s["name"] for s in seal_doc["subject"]} if seal_valid else set()
        for p in f.paths:
            if p in sealed and not _run2_lists(p):
                P.add((p, "unexpected-file"))  # a producer seals only RUN-2's files

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
            if _summary_duplicates(summary) and "summary-duplicates" not in MUTATIONS:
                P.add(("summary.json", "summary-duplicate"))

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
        # RUN-3, §4.5 (R5-1, R5-2): overlays/ is the chain's to report, and an envelope beyond its limit is only a
        # malformed signature (SIG-1): neither is a problem of the run.
        own = [p for p in self.folder.paths if not p.startswith("overlays/")]
        # RUN-3: `overlays` is a folder (a file of that name is a path problem); a path whose first segment is
        # `overlays` in another case is a path problem whatever else the run holds: on a case-insensitive file system
        # it is the overlays folder.
        clash = {(p, "path") for p in own if p == "overlays" or (
            p.split("/", 1)[0] != "overlays" and p.split("/", 1)[0].lower() == "overlays")}
        if "overlays-boundary" in MUTATIONS:
            clash = set()
        problems = (set(reading) | path_problems(own) | clash
                    | {(p, "path") for p in self.folder.special if not p.startswith("overlays/")}
                    | self.seal())
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


def _within_limits(events):
    """OVL-5, ENC-17: what a reader reads of the events file: up to its last LF within the first 1 GiB and the first
    1,000,000 lines."""
    part = events[:MAX_NDJSON]
    part = part[:part.rfind(b"\n") + 1] if len(part) < len(events) else part
    lf, count = -1, 0
    while count < MAX_LINES:
        lf = part.find(b"\n", lf + 1)
        if lf < 0:
            return part
        count += 1
    return part[:lf + 1]


def _uncovered(ranges, size):
    position = 0
    for start, end in sorted(ranges):
        if start > position:
            return True
        position = max(position, end)
    return position < size


# SEC-6: the OpenTelemetry GenAI attributes that carry content (and the deprecated gen_ai.prompt / gen_ai.completion).
CONTENT_ATTRIBUTES = {"gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions",
                      "gen_ai.tool.call.arguments", "gen_ai.tool.call.result", "gen_ai.evaluation.explanation",
                      "gen_ai.prompt", "gen_ai.completion"}
_RUN2_FILES = {"run.json", "results.ndjson", "metrics.json", "summary.json", "evidence.ndjson", "gates.ndjson",
               "traces.otlp.jsonl", "logs.otlp.jsonl", "seal.json", "attestation.dsse.json"}


def _is_blob_path(p):
    """blobs/sha256/<first two hex characters>/<64 hex characters> (EVD-3)."""
    m = BLOB_PATH.fullmatch(p)
    return bool(m) and m.group(2)[:2] == m.group(1)


def _run2_lists(p):
    """Whether RUN-2 lists this path (ext/ and overlays/ have their own rules)."""
    return p in _RUN2_FILES or p.startswith(("ext/", "overlays/")) or _is_blob_path(p)


def _otlp_log_records(request):
    """The log records of an OTLP/JSON LogsData object."""
    for resource in request.get("resourceLogs", []) if isinstance(request, dict) else []:
        for scope in (resource.get("scopeLogs") or []) if isinstance(resource, dict) else []:
            for record in scope.get("logRecords", []) if isinstance(scope, dict) else []:
                if isinstance(record, dict):
                    yield record


def _container_carries_content(request, resources, scopes):
    """SEC-6: a content attribute on a resource or a scope of an OTLP/JSON TracesData or LogsData object."""
    def holds(attributes):
        return isinstance(attributes, list) and any(isinstance(a, dict) and a.get("key") in CONTENT_ATTRIBUTES
                                                     for a in attributes)
    for resource in request.get(resources, []) if isinstance(request, dict) else []:
        if not isinstance(resource, dict):
            continue
        if holds(get(resource, "resource", "attributes", default=None)):
            return True
        for scope in resource.get(scopes) or []:
            if isinstance(scope, dict) and holds(get(scope, "scope", "attributes", default=None)):
                return True
    return False


def _log_carries_content(record):
    """SEC-6: a log record with a content attribute, or with a body."""
    attributes = record.get("attributes") if isinstance(record.get("attributes"), list) else []
    return "body" in record or any(isinstance(a, dict) and a.get("key") in CONTENT_ATTRIBUTES for a in attributes)


def _carries_content(span):
    """Whether a span, or one of its events, carries a content attribute (SEC-6)."""
    holders = [span] + [e for e in span.get("events") or [] if isinstance(e, dict)]
    return any(isinstance(a, dict) and a.get("key") in CONTENT_ATTRIBUTES
               for h in holders for a in (h.get("attributes") or []) if isinstance(h.get("attributes"), list))


def _otlp_spans(request):
    for resource in request.get("resourceSpans", []) if isinstance(request, dict) else []:
        if not isinstance(resource, dict):
            continue
        scopes = (resource.get("scopeSpans") or []) + (
            resource.get("instrumentationLibrarySpans") or [] if "otlp-names" in MUTATIONS else [])
        for scope in scopes:  # OTLP/JSON 1.x only (RUN-14)
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
    sealed = time_key(pred.get("sealedAt"))
    if a is not None and sealed is not None and sealed < a:
        return True  # SEAL-1: only a closed run is sealed
    return (a != b) if a is not None and b is not None else closed != ended


def _belongs(line, lane_name, summary_lanes):
    """SUM-3: a line belongs to the lane its lane names; a line without lane belongs to the summary's lane when the
    summary has a single one. summary_lanes: the summary's lane names, in order."""
    return line.get("lane") == lane_name or ("lane" not in line and set(summary_lanes) == {lane_name})


def _summary_lanes(run):
    summary = run.read()[0].get("summary.json")
    return [l.get("lane") for l in summary.get("lanes", [])] if isinstance(summary, dict) else []


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


def rounded_sum(values):
    """SUM-5: the exact sum of binary64 values, rounded once; None when it is beyond binary64 (never an exception:
    math.fsum raises on an intermediate overflow even when the exact sum is finite, so the exact sum decides then)."""
    try:
        total = math.fsum(values)
    except OverflowError:
        total = None
    if total is None or not math.isfinite(total):
        try:
            total = float(sum((Fraction(v) for v in values), Fraction(0)))
        except OverflowError:
            return None
    return total if math.isfinite(total) else None


def _median(values):
    """SUM-8: the middle value; of an even count, the mean of the two middle values computed exactly and rounded once."""
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[middle]
    return float((Fraction(ordered[middle - 1]) + Fraction(ordered[middle])) / 2)


AGGREGATES = {"median": _median, "min": min, "max": max}  # SUM-8: the methods AEF defines


def _summary_duplicates(summary):
    """SUM-9: two entries with one lane, metric and path, or two usage entries with one role and model (an absent
    model a value of its own)."""
    keys = [(lane.get("lane"), e.get("metric"), e.get("path"))
            for lane in summary.get("lanes", []) for e in lane.get("metrics", [])]
    usage = [(get(u, "role", default=_MISSING), get(u, "model", default=_MISSING))
             for u in summary.get("usage") or [] if isinstance(u, dict)]
    names = [lane.get("lane") for lane in summary.get("lanes", [])]
    return len(keys) != len(set(keys)) or len(usage) != len(set(usage)) or len(names) != len(set(names))


def _summary_wrong(summary, results, metric):
    """SUM-3 to SUM-5: whether any entry's N, n, notMeasured, sum or value is not what results.ndjson gives."""
    lanes = summary.get("lanes", [])
    names = [l.get("lane") for l in lanes]
    for lane in lanes:
        for e in lane.get("metrics", []):
            declaration = metric.get(e.get("metric"))
            if declaration is None or declaration.get("kind") not in METRIC_KINDS:
                continue  # undeclared: reported as metric; an unknown kind takes no part in summaries (§7.3)
            kind = declaration["kind"]
            N, values = 0, []
            for _, o in results:
                if "trial" in o or o.get("path") != e.get("path") or not _belongs(o, lane.get("lane"), names):
                    continue
                if o.get("state") == "not_applicable":
                    continue
                N += 1
                v = _line_value(o, e.get("metric"), kind)
                if v is not None:
                    values.append(v)
            n = len(values)
            total = rounded_sum(values)  # None beyond binary64
            beyond = n > 0 and total is None
            if n == 0 or beyond:
                value = None  # SUM-5: a mean whose sum is beyond binary64 is no binary64 value, as when n is 0
                if beyond and "summary-overflow-mean" in MUTATIONS:
                    value = float(sum((Fraction(v) for v in values), Fraction(0)) / n)
            else:
                value = total if kind == "count" else total / n
            if e.get("N") != N or e.get("n") != n or e.get("notMeasured") != N - n:
                return True
            if "sum" in e and (total is None or not defined_equal(e["sum"], total)):  # SUM-5: exact, or omitted
                return True
            if beyond and "aggregate" not in e and e.get("verdict") != "not_measured" and value is None:
                return True  # SUM-6: as when n is 0
            if "sumSq" in e:  # §3.6: binary64, compared within 1e-9; a sum of squares that overflows matches nothing
                squares = 0.0
                for x in values:
                    squares += x * x
                if not math.isfinite(squares) or not close_enough(e["sumSq"], squares):
                    return True
            if "aggregate" in e:  # SUM-8
                method = get(e, "aggregate", "method")
                if (n == 0) != (e.get("value") is None):  # null exactly when n is 0
                    return True
                if n == 0:
                    pass
                elif method in AGGREGATES and not defined_equal(e.get("value"), AGGREGATES[method](values)):
                    return True  # median, min and max are recomputed; any other method is the producer's
            elif (e.get("value") is None) != (value is None) or (value is not None and not defined_equal(e["value"], value)):
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
    # OVL-3: signed only when the event's batch has a signature that verifies, under the policy, for its identity; a
    # file reader never received an event from a host, so it never shows authenticated.
    assurance = [{"event": ev["eventId"],
                  "shown": "signed" if batch is not None and get(ev, "by", "identity") in run.batch_signers(batch)
                  else "self-attested"}
                 for ev, batch in run.verified_events(with_batch=True)]
    return {
        "results": [{"resultId": r, "sealedState": sealed[r], "effectiveState": e["state"], "event": e["eventId"]}
                    for r, e in sorted(effective.items(), key=lambda item: order[item[0]])],
        "reviews": [{"target": t, "status": e["kind"], "event": e["eventId"]}
                    for t, e in sorted(review.items(), key=lambda item: -1 if item[0] == "run" else order[item[0]])],
        "waivers": waivers,
        "withheld": withheld,
        "unsealedEvents": sum(1 for _, start, _, _, _ in chain["lines"] if start >= chain["verified_end"]),
        "assurance": assurance,
    }


# ---------------------------------------------------------------------------- operations: checkpoint (CKP-7)

def document_verdicts(schema, path):
    """{'writer', 'reader', 'document'}: the verdicts for one JSON document or every line of an NDJSON file."""
    data = Path(path).read_bytes()
    try:
        if str(path).endswith((".ndjson", ".jsonl")):
            if ndjson_framing(data):
                raise EncodingProblem("framing")
            lines = ndjson_lines(data)
            if len(data) > MAX_NDJSON or len(lines) > MAX_LINES or any(
                    len(raw) > MAX_JSON or nesting_depth(raw) > MAX_DEPTH for _, _, raw in lines):
                raise EncodingProblem("limit")  # ENC-17: refused
            docs = [load_json_bytes(raw, object_only=False) for _, _, raw in lines]
        else:
            limit = MAX_SEAL if schema.split("#")[0] in ("seal", "overlay-seal") else MAX_JSON
            if len(data) > limit or nesting_depth(data) > MAX_DEPTH:
                raise EncodingProblem("limit")  # ENC-17: refused, at the limit of that file
            docs = [load_json_bytes(data, object_only=False)]
    except EncodingProblem:
        return {"writer": "invalid", "reader": "invalid", "documents": None}
    verdict = {side: "valid" if all(document_ok(side, schema, d) for d in docs) else "invalid"
               for side in ("writer", "reader")}
    verdict["documents"] = docs
    return verdict


def checked_as_decided(m):
    """CKP-7 (R6N-1..3): state decided, or, in a manifest that declares this version or an earlier one, a state this
    version does not know with an outcome or a decision recorded."""
    state = m.get("state")
    return state == "decided" or (state not in CHECKPOINT_STATES and not _later_minor(m)
                                  and (m.get("outcome") is not None or m.get("decision") is not None))


def manifest_problems(m):
    """[CKP-7] codes for a manifest the reader accepts, in code order."""
    state, outcome = m.get("state"), m.get("outcome")
    later = _later_minor(m)  # R6-2: only a later minor's values are unverifiable; a 1.0 manifest is checked as usual
    if later and (state not in CHECKPOINT_STATES or (outcome is not None and outcome not in CHECKPOINT_OUTCOMES)):
        return ["unverifiable"]
    if not checked_as_decided(m) or outcome == "aborted":
        return []
    decision, given = m.get("decision"), m.get("decisionInput")
    if not isinstance(decision, dict) or not isinstance(given, dict):
        # R7-4: nothing to recompute; a later minor's is unverifiable, this version's cannot be decided
        return ["unverifiable"] if later else ["decision"]
    if later and (decision.get("outcome") not in DECISION_OUTCOMES or any(
            l.get("status") not in DECISION_LANE_STATUSES for l in decision.get("lanes", [])) or any(
            isinstance(l.get("result"), dict) and l["result"].get("status") not in INPUT_STATUSES
            for l in given.get("lanes", []))):
        return ["unverifiable"]
    problems = set()
    try:
        recomputed = decide(given)
    except (ValueError, KeyError, TypeError):  # the input cannot be decided
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
    manifest_names = {lane["lane"] for lane in m["lanes"]}
    if any(l["lane"] not in manifest_names and l.get("result") is not None for l in given["lanes"]) and (
            "input-only-lane" not in MUTATIONS):
        problems.add("evidence")  # a result for a lane the manifest does not have: a result but no runs
    runs_of = {}  # manifest lane -> the run hashes of its runs
    for lane in m["lanes"]:
        runs_of.setdefault(lane["lane"], {r.get("runHash") for r in lane.get("runs", []) if isinstance(r, dict)})
    for l in given["lanes"]:
        if l["lane"] in runs_of and set(l.get("evidence") or []) != runs_of[l["lane"]]:
            problems.add("lane-evidence")  # none is the empty set
    for x in given.get("exceptions") or []:
        if any(h not in runs_of.get(x.get("lane"), set()) for h in x.get("evidence") or []):
            problems.add("exception-evidence")
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
        self.index = defaultdict(list)
        for d in run_folders(root):
            run = Run(d, policy)
            doc = run.run_doc
            if doc is not None and isinstance(doc.get("runId"), str):
                self.index[(doc["runId"], run.claimed_run_hash())].append(run)

    def find(self, ref):
        """The run with this runId and run hash: an intact one when any folder holding it is intact (CKP-8: the
        order folders are listed in never matters), else any."""
        if not isinstance(ref, dict):
            return None
        runs = self.index.get((ref.get("runId"), ref.get("runHash")), [])
        if "run-copies" in MUTATIONS:
            return runs[0] if runs else None
        return next((r for r in runs if r.intact), runs[0] if runs else None)


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
    """LANE-9: run.json endedAt, as written."""
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
                or as_number(rule.get("value")) is None or (minimum is not None and e.get("n") < minimum)
                or ("aggregate" in e and get(e, "aggregate", "method") not in AGGREGATES)):
            statuses.append("not_measured")
        else:
            statuses.append("passed" if op(float(e["value"]), float(rule["value"])) else "failed")
    return "failed" if "failed" in statuses else "not_measured" if "not_measured" in statuses else "passed"


def _later_minor(document):
    """VER-6 (R5-4): whether a document declares a later minor of major 1 than this version (1.0)."""
    version = document.get("schemaVersion") if isinstance(document, dict) else None
    if "declared-version" in MUTATIONS:
        return True
    return isinstance(version, str) and re.fullmatch(r"1[.][0-9]+", version) is not None and version != "1.0"


def _rule_unknown(rule, checkpoint=None):
    """CKP-8: whether a lane rule holds anything this version does not know (VER-8): it is not valid against this
    version's writer schema (a kind, a member or a value a later minor added: a severity max, a threshold op, a
    comparison axis, a new rule member), in a checkpoint that declares a later minor (R5-4)."""
    if "rule-unknown" in MUTATIONS or not _later_minor(checkpoint):
        return False
    return not schema_valid("writer", "checkpoint#/$defs/laneRule", rule)


def _reads_unknown(rule, runs):
    """CKP-8 (R4-1): whether the lane's recomputation reads a value this version does not know in the runs it reads
    (found ones: its runs and its baseline): an execution.targetMode; for a severity lane, a severity on one of its
    lines (its lane and path, trial lines included); for a comparison lane, the compared metric's direction."""
    if "rule-unknown" in MUTATIONS:
        return False
    kind, lane, path = rule.get("kind"), rule.get("lane"), rule.get("path")
    for run in runs:
        if run is None:
            continue
        mode = get(run.run_doc or {}, "execution", "targetMode")
        if mode is not None and mode not in TARGET_MODES and _later_minor(run.run_doc):
            return True
        if kind == "severity":
            names = _summary_lanes(run)
            for _, o in run.objects("results.ndjson"):
                if lane is not None and not _belongs(o, lane, names):
                    continue
                if path is not None and not (o.get("path") == path or str(o.get("path", "")).startswith(path + "/")):
                    continue
                # CKP-8, VER-6: a run's minor is its run.json's, whatever the line declares
                if "severity" in o and o["severity"] not in SEVERITY_ORDER and _later_minor(
                        o if "minor-per-document" in MUTATIONS else run.run_doc):
                    return True
        if kind == "comparison" and run is not runs[-1]:  # the candidates', never the baseline's (LANE-7)
            metrics = run.read()[0].get("metrics.json") or {}
            for m in metrics.get("metrics", []):
                if (isinstance(m, dict) and m.get("id") == rule.get("metric") and "direction" in m
                        and m["direction"] not in DIRECTIONS
                        and _later_minor(metrics if "minor-per-document" in MUTATIONS else run.run_doc)):
                    return True
    return False


def _severity(rule, runs):
    """LANE-3: failed on a failure worse than max; else not_measured on an undecided line or fewer decided lines
    than minimumN (at least 1); else passed. not_applicable and scored lines take no part."""
    limit = {"none": 0, "low": 1, "medium": 2, "high": 3}.get(rule.get("max"))
    if limit is None:
        return "not_measured"  # a max this version does not know (§7.3)
    undecided, decided = False, 0
    lane, path = rule.get("lane"), rule.get("path")
    for run in runs:
        names = _summary_lanes(run)
        for _, o in run.objects("results.ndjson"):
            if lane is not None and not _belongs(o, lane, names):
                continue  # only lines of that summary lane (SUM-3)
            if path is not None and not (o.get("path") == path or str(o.get("path", "")).startswith(path + "/")):
                continue  # only lines at that path or below it
            state = o.get("state")
            if "trial" in o:  # LANE-3: a failing trial counts for step 1, and takes no part in the counts
                if state in ("failed", "warn") and SEVERITY_ORDER[read_severity(o.get("severity"))] > limit:
                    return "failed"
                if "trial-undecided" in MUTATIONS and state in ("inconclusive", "not_measured", "skipped", "error", "pending"):
                    undecided = True
                continue
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
        judges = doc.get("judges", [])  # LANE-6: no judges is the empty list
        if "judges-absent" in MUTATIONS and "judges" not in doc:
            judges = None
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
    names = _summary_lanes(run)
    out = {}
    for _, o in run.objects("results.ndjson"):
        if "trial" in o or o.get("path") != path or not _belongs(o, lane, names):
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
    counted = [r for r in found if r.intact and _bound(r.run_doc or {}, binding)]  # LANE-9
    for r in counted:
        v = get(r.run_doc, "subject", "version")
        if v is not None and v != version:
            subject_version = v
            break
    closings = [(time_key(t), i, t) for i, t in enumerate(_closed_at(r) for r in counted) if t is not None]
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


def op_lanes(checkpoint_path, runs_dir, at=None, policy=None, envelope=None):
    """§5.3 and CKP-8. at: the evaluation time of an undecided checkpoint (LANE-9); policy: the caller's trust policy
    (authorized redactions, CKP-8; the checkpoint's signature, CKP-9); envelope: the checkpoint's signature."""
    m = load_json_file(checkpoint_path)
    if not isinstance(m, dict) or not isinstance(m.get("lanes"), list):
        raise InputError(f"{checkpoint_path}: not a checkpoint manifest")
    store = Store(runs_dir, policy)
    given = m.get("decisionInput") if checked_as_decided(m) else None  # CKP-8: as CKP-7 checks it
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

        distinct, refs = set(), []  # LANE-4: a run named twice counts once
        for ref in lane.get("runs", []):
            key = (get(ref, "runId"), get(ref, "runHash"))
            if key not in distinct or "run-named-twice" in MUTATIONS:
                distinct.add(key)
                refs.append(ref)
        runs = [look(ref) for ref in refs]
        baseline = look(rule["baseline"]) if rule.get("kind") == "comparison" and isinstance(rule.get("baseline"), dict) else None
        binding = (get(m, "subject", "ref"), get(m, "subject", "deployment"),
                   rule.get("suite") if isinstance(rule.get("suite"), dict) else None)
        result = lane_result(rule, runs, baseline, version, fallback, binding)
        lanes.append({"lane": name, "result": result})
        if given is not None and name in recorded and (_rule_unknown(rule, m) or _reads_unknown(rule, runs + [baseline])):
            problems.add((f"lanes/{name}", "unverifiable"))  # CKP-8: a later minor's rule is not compared
        elif given is not None and name in recorded:
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
    # CKP-9, SIG-8: a checkpoint with no problem of CKP-7 or CKP-8 and a signature verified for a trusted identity
    # anchors the runs its lanes name, their comparison baselines included.
    anchors = []
    if envelope is not None and policy is not None and not problems and op_checkpoint(checkpoint_path)["problems"] == []:
        signed = verify_signature(Path(envelope).read_bytes(), Path(checkpoint_path).read_bytes(), CHECKPOINT_TYPE,
                                  policy)["verifiesFor"]
        if signed:
            named = {get(ref, "runHash") for lane in m["lanes"] for ref in lane.get("runs", [])}
            named |= {get(lane.get("rule"), "baseline", "runHash") for lane in m["lanes"]
                      if get(lane.get("rule"), "kind") == "comparison"}
            anchors = sorted((h for h in named if isinstance(h, str)), key=utf8_key)
    return {"lanes": lanes, "problems": sort_problems(problems), "anchors": anchors}


# ---------------------------------------------------------------------------- operations: signatures (§4.4)

def _der_element(data, pos):
    """(tag, content, end) of a DER element (lengths of up to four bytes); ValueError when it does not fit or its
    length is not DER's minimal one (SIG-3: a SubjectPublicKeyInfo is DER, whatever its algorithm)."""
    if pos + 2 > len(data):
        raise ValueError("truncated DER")
    tag, first, pos = data[pos], data[pos + 1], pos + 2
    if first < 0x80:
        length = first
    elif 0x81 <= first <= 0x84:
        count = first & 0x7F
        if pos + count > len(data):
            raise ValueError("truncated DER")
        length = int.from_bytes(data[pos:pos + count], "big")
        if length < 0x80 or data[pos] == 0:
            raise ValueError("a DER length that is not minimal (BER)")
        pos += count
    else:
        raise ValueError("unsupported DER length")
    if pos + length > len(data):
        raise ValueError("truncated DER")
    return tag, data[pos:pos + length], pos + length


def _pem_der(pem):
    """SIG-3: RFC 7468's strict form. The BEGIN line, lines of base64 and nothing else, the END line; LF or CRLF; no
    text around the block, no blank line, no whitespace in a line."""
    if not isinstance(pem, str):
        raise ValueError("not a PEM string")
    text = pem[:-1] if pem.endswith("\n") else pem
    lines = [line[:-1] if line.endswith("\r") else line for line in text.split("\n")]
    if len(lines) < 3 or lines[0] != "-----BEGIN PUBLIC KEY-----" or lines[-1] != "-----END PUBLIC KEY-----":
        raise ValueError("not a PEM PUBLIC KEY block, or text around it")
    if any(not re.fullmatch(r"[A-Za-z0-9+/]+={0,2}", line) for line in lines[1:-1]):
        raise ValueError("a PEM line that is not base64 alone (blank, or with whitespace)")
    if any(len(line) != 64 for line in lines[1:-2]) or not 1 <= len(lines[-2]) <= 64:
        raise ValueError("PEM lines of 64 characters but the last (RFC 7468's strict form)")
    return base64.b64decode("".join(lines[1:-1]), validate=True)


def _spki_algorithm(der):
    """The AlgorithmIdentifier's OID bytes of a SubjectPublicKeyInfo, or ValueError when it is not one."""
    tag, body, end = _der_element(der, 0)
    if tag != 0x30 or end != len(der):
        raise ValueError("not a SubjectPublicKeyInfo")
    tag, algorithm, pos = _der_element(body, 0)
    bits_tag, bits, end = _der_element(body, pos)
    if tag != 0x30 or bits_tag != 0x03 or end != len(body):
        raise ValueError("not a SubjectPublicKeyInfo")
    if not bits or bits[0] != 0:
        raise ValueError("the key BIT STRING has unused bits")
    oid_tag, oid, _ = _der_element(algorithm, 0)
    if oid_tag != 0x06:
        raise ValueError("not an AlgorithmIdentifier")
    return oid


def _spki_algorithm_body(der):
    """The content of a SubjectPublicKeyInfo's AlgorithmIdentifier (OID and parameters)."""
    _, body, _ = _der_element(der, 0)
    _, algorithm, _ = _der_element(body, 0)
    return algorithm


def policy_allows(policy, identity, action):
    """SIG-4: whether the trust policy lets this identity do this beyond signing ("may": ["redact"]): an exact value
    of a key's `may` array; values this version does not know grant nothing."""
    keys = policy.get("keys") if isinstance(policy, dict) else None
    if "policy-loose" in MUTATIONS:
        return any(isinstance(k, dict) and k.get("identity") == identity and action in (k.get("may") or [])
                   for k in keys if isinstance(keys, list))
    return any(isinstance(k, dict) and k.get("identity") == identity and isinstance(k.get("may"), list)
               and action in k["may"] for k in keys if isinstance(keys, list))


def load_policy(policy):
    """[(identity, keyid, public key or None when its algorithm is not supported)], in policy order (SIG-4). The key
    id is computed from the key (SIG-3), never read from the policy."""
    version = policy.get("schemaVersion") if isinstance(policy, dict) else None
    if isinstance(version, str) and not version.startswith("1."):  # VER-4: another major is not read at all
        raise InputError(f"a trust policy of AEF version {version}: a 1.x verifier reads major version 1 only (VER-4)")
    if "policy-loose" not in MUTATIONS and not document_ok("reader", "trust-policy", policy):  # SIG-4: refused whole
        raise InputError("a trust policy is {\"keys\": [{\"identity\": ..., \"publicKey\": <SPKI PEM>, \"may\": [...]?}]}, "
                         "valid against trust-policy.schema.json")
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
            if key.algorithm == aef_crypto.ED25519 and aef_crypto._ed_equal(
                    aef_crypto._ed_mul(8, aef_crypto._ed_decode(key.key)), aef_crypto._ED_ZERO):
                raise ValueError("an Ed25519 key of small order verifies forgeries")
        except ValueError as error:
            if _spki_algorithm_body(der) in (aef_crypto.ID_EC_PUBLIC_KEY + aef_crypto.PRIME256V1, aef_crypto.ID_ED25519):
                # SIG-2, SIG-3: a P-256 or Ed25519 key that cannot be used (compressed, off the curve, not DER)
                # refuses the whole policy; a verifier never verifies against part of one.
                raise InputError(f"the policy key for {entry['identity']} cannot be used: {error}") from None
            key, kid = None, "sha256:" + sha256_hex(der)  # another algorithm (RSA, another curve): unsupported
        if any(kid == k[1] for k in keys):
            raise InputError(f"the policy lists the key {kid} twice: a verifier does not choose between them (SIG-3)")
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
        if len(envelope_bytes) > MAX_ENVELOPE or nesting_depth(envelope_bytes) > MAX_DEPTH:
            raise ValueError("an envelope beyond ENC-17's limits")  # SIG-1: malformed
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
    reads = {}  # §9.3: only for one document the reader accepts
    if docs is not None and len(docs) == 1 and verdict["reader"] == "valid":
        reads = readings(schema, docs[0])
    verdict["reads"] = reads
    return verdict


class Refused(ValueError):
    """The decision function refuses an input rather than decide it (DEC-1)."""


def decide(inp):
    """Decide(input) -> output (§5.4, DEC-1 to DEC-5): pure, no clock. Refused for an input DEC-1 does not allow."""
    if not isinstance(inp, dict) or not isinstance(inp.get("lanes"), list):
        raise Refused("not a decision input")
    names = [lane.get("lane") for lane in inp["lanes"]]
    if not names:
        raise Refused("no lanes")
    if len(set(names)) != len(names):
        raise Refused("a lane twice")
    exceptions = inp.get("exceptions") or []
    for x in exceptions:
        if x.get("lane") not in names:
            raise Refused("an exception for a lane the input does not have")
        if not x.get("evidence"):
            raise Refused("an exception with no run hash")
        if not parse_time(x["expires"]) > parse_time(x["at"]):
            raise Refused("an exception that expires before it is granted")
    now, version = parse_time(inp["evaluatedAt"]), inp["subjectVersion"]

    lanes, reasons = [], []
    for lane in inp["lanes"]:
        name, blocking, result = lane["lane"], lane["blocking"], lane.get("result")
        after = []
        if result is None:
            status, code = "missing", "missing"
        elif result["subjectVersion"] != version:
            status, code = "missing", "wrong-version"
        elif parse_time(result["oldestClosedAt"]) > now:
            status, code = "missing", "future-evidence"
        elif "freshness" in lane and _later(parse_time(result["oldestClosedAt"]), parse_duration(lane["freshness"])) < now:
            status, code = "stale", "stale"
        else:
            status = result.get("status") if result.get("status") in INPUT_STATUSES else "not_measured"
            code = {"passed": None, "failed": "failed" if blocking else "advisory-failed",
                    "not_measured": "not-measured", "incomparable": "incomparable"}[status]
            if status == "failed":
                evidence = set(lane.get("evidence") or [])
                own = [x for x in exceptions if x["lane"] == name]
                same = [x for x in own if set(x["evidence"]) == evidence or "exception-evidence" in MUTATIONS]
                applying = [x for x in same if parse_time(x["at"]) <= now < parse_time(x["expires"])]
                if applying:
                    status, code = "waived", "waived"
                else:
                    if same:
                        after.append("exception-expired")
                    if any(set(x["evidence"]) != evidence for x in own):
                        after.append("exception-other-evidence")
        item = {"lane": name, "status": status, "blocking": blocking}
        if status == "incomparable" and result.get("axes"):
            item["axes"] = list(result["axes"])
        lanes.append(item)
        reasons += ([f"{code}:{name}"] if code else []) + [f"{c}:{name}" for c in after]

    superseded = inp.get("supersededBy") is not None and inp["supersededBy"] != version
    if superseded:
        reasons.append(f"superseded:{inp['supersededBy']}")
    statuses = [(l["status"], l["blocking"]) for l in lanes]
    if superseded or any(s == "stale" for s, _ in statuses):
        outcome = "expired"
    elif any(s == "failed" and b for s, b in statuses):
        outcome = "blocked"
    elif any(s in ("missing", "not_measured", "incomparable") for s, _ in statuses):
        outcome = "inconclusive"
    elif any(s == "waived" for s, _ in statuses):
        outcome = "approved_with_exceptions"
    else:
        outcome = "approved"
    reasons.append(f"outcome:{outcome}")
    return {"outcome": outcome, "lanes": lanes, "reasons": reasons}


def _later(moment, seconds):
    return moment[0] + seconds, moment[1]


def op_decide(path):
    value = load_json_file(path)
    given = value["input"] if isinstance(value, dict) and "input" in value and "subjectVersion" not in value else value
    try:
        return {"output": decide(given)}
    except (ValueError, KeyError, TypeError) as error:
        return {"error": str(error) or type(error).__name__}


def load_plan(path):
    """A run plan given to match, stream or conform: (its bytes, the plan). InputError when it does not read or the
    reader schema refuses it (spec 09 §9.3): PLAN-7, STRM-3 and STRM-4 are defined against a plan, and a timeout that
    is no duration ([ENC-9]) cannot be checked against."""
    plan = load_json_file(path)
    if not isinstance(plan, dict) or not schema_valid("reader", "run-plan", plan):
        raise InputError(f"{path}: not a run plan the reader schema accepts")
    return Path(path).read_bytes(), plan


def op_match(plan_path, runner_path):
    return {"matches": bool(aef_stream.matches(load_plan(plan_path)[1], load_json_file(runner_path)))}


def _stream_event(raw):
    """A stream line's event, or None when it is not an I-JSON object valid against the reader schema (STRM-3
    event-invalid)."""
    if (len(raw) > MAX_JSON or nesting_depth(raw) > MAX_DEPTH) and "stream-limits" not in MUTATIONS:
        return None  # beyond ENC-17's limits: not read
    try:
        event = load_json_bytes(raw)
    except EncodingProblem:
        return None
    return event if isinstance(event, dict) and schema_valid("reader", "runner-event", event) else None


def op_stream(events_path, plan_path):
    try:
        data = Path(events_path).read_bytes()
    except OSError as error:
        raise InputError(str(error)) from None
    plan_bytes, plan = load_plan(plan_path)
    complete = data[:data.rfind(b"\n") + 1]  # STRM-2: a last line without LF is still being written
    if ndjson_framing(complete):  # STRM-3: one problem, and the stream is not checked further
        return {"problems": [["stream", "encoding"]]}
    if len(ndjson_lines(complete)) > MAX_LINES:  # ENC-17: one limit at stream, not checked further
        return {"problems": [["stream", "limit"]]}
    events = [_stream_event(raw) for _, _, raw in ndjson_lines(complete)]
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
    plan = load_plan(plan_path)[1]
    events = []
    complete = data[:data.rfind(b"\n") + 1]  # STRM-2: an unfinished last line is not read
    if not ndjson_framing(complete):  # STRM-4 reads the events STRM-3 can read; the others take no part
        events = [e for e in (_stream_event(raw) for _, _, raw in ndjson_lines(complete)) if e is not None]

    named, announced, accepted, terminal = [], {}, None, None
    for e in events:
        kind = e.get("kind")
        if kind == "job.accepted" and accepted is None:
            accepted = e
        if kind in ("job.sealed", "job.failed", "job.cancelled", "job.refused") and terminal is None:
            terminal = e
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
        problems.update((where, code) for code in _plan_problems(run.run_doc, plan, accepted, terminal))
        if as_number(get(run.read()[0].get("summary.json"), "cost", "totalUsd")) is None:
            problems.add((where, "no-cost"))  # STRM-4: the budget cannot be checked without it

    limits = plan.get("limits") if isinstance(plan.get("limits"), dict) else {}
    costs = [get(r.read()[0].get("summary.json"), "cost", "totalUsd") for r in found]
    max_usd = as_number(limits.get("maxUsd"))
    known = [c for c in costs if as_number(c) is not None]
    total = functools.reduce(lambda a, b: a + b, known, 0.0) if "budget-in-order" in MUTATIONS else math.fsum(known)
    if max_usd is not None and total > max_usd:
        problems.add(("job", "over-budget"))
    max_cases = as_number(limits.get("cases"))
    if max_cases is not None:
        # A case is its run's suite (ref and version) with its caseId: one case id in two suites counts twice, in two
        # runs of one suite once (PLAN-8).
        suite = lambda r: () if "cases-by-id" in MUTATIONS else (get(r.run_doc, "suite", "ref"),
                                                                 get(r.run_doc, "suite", "version"))
        cases = {suite(r) + (o.get("caseId"),) for r in found for _, o in r.objects("results.ndjson")
                 if o.get("parentResultId") is None}
        if len(cases) > max_cases:
            problems.add(("job", "over-cases"))
    return {"problems": sort_problems(problems)}


def _plan_problems(doc, plan, accepted, terminal):
    """STRM-4's codes for one run found (no-cost aside)."""
    codes = set()
    if read_content_capture(doc.get("contentCapture")) != plan.get("contentCapture"):
        codes.add("content-capture")
    deployment, endpoint = get(plan, "subject", "deployment"), get(plan, "subject", "endpoint")
    if "plan-where-when" not in MUTATIONS and ((deployment is not None and get(doc, "deployment", "ref") != deployment) or (
            endpoint is not None and get(doc, "deployment", "endpoint") != endpoint)):
        codes.add("deployment")  # an absent value is not it
    started, ended = time_key(doc.get("startedAt")), time_key(doc.get("endedAt"))
    accepted_at = time_key(accepted.get("at")) if accepted else None
    terminal_at = time_key(terminal.get("at")) if terminal else None
    if "plan-where-when" not in MUTATIONS and ((started is not None and accepted_at is not None and started < accepted_at) or (
            ended is not None and terminal_at is not None and ended > terminal_at)):
        codes.add("time")  # made by this job, between its acceptance and its end

    def judges(items):
        """A judge is the fields a plan's judge and a run's judge both define (R9-4): model, provider, rubricDigest."""
        return [tuple(get(j, f, default=_MISSING) for f in ("model", "provider", "rubricDigest"))
                for j in items or [] if isinstance(j, dict)]

    if plan.get("judges") or "judges-guard" not in MUTATIONS:
        # STRM-4: a run names the judges that graded it (RUN-9): the plan's list with some left out, in its order; a
        # plan that names none allows none (R9-3). A plan judge without a provider leaves it to the runner (R10-6).
        named, planned = judges(doc.get("judges")), judges(plan.get("judges"))
        def is_the_plans(run_judge, plan_judge):
            """Its model always; its provider (1) and rubricDigest (2) wherever the plan judge names them (R10-6)."""
            for k, (named_value, planned_value) in enumerate(zip(run_judge, plan_judge)):
                if k == 1 and "judges-provider" in MUTATIONS:
                    continue  # the mutation: provider never compared
                strict = (k == 1 and "judges-provider-strict" in MUTATIONS) or (
                    k == 2 and "judges-rubric-strict" in MUTATIONS)
                if k > 0 and planned_value is _MISSING and not strict:
                    continue  # left to the runner: the run names the one that served or graded
                if named_value != planned_value:
                    return False
            return True

        if "judges-same-list" in MUTATIONS:
            within = named == planned
        else:
            position, within = 0, "judges-once" not in MUTATIONS or len(set(named)) == len(named)
            for judge in named:
                while position < len(planned) and not is_the_plans(judge, planned[position]):
                    position += 1
                within = within and position < len(planned)
                position += 1
        if not within:
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
    asked = "live" if "target-mode-live" in MUTATIONS else plan.get("targetMode", "live")  # none asks for live
    if get(doc, "execution", "targetMode") != asked:  # compared as written
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
    p.add_argument("--policy"); p.add_argument("--envelope")
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
        if a.policy:
            load_policy(load_json_file(a.policy))  # refused as a whole, as an input error (SIG-4)
        return op_run(a.dir, load_json_file(a.policy) if a.policy else None,
                      load_json_file(a.anchors) if a.anchors else None)
    policy = load_json_file(a.policy) if getattr(a, "policy", None) else None
    if policy is not None:
        load_policy(policy)  # SIG-3, SIG-4, VER-4: refused as a whole, as an input error, wherever it is given
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
        return op_lanes(a.checkpoint, a.runs, a.at, policy, a.envelope)
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
