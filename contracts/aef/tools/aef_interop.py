#!/usr/bin/env python3
"""AEF 1.0 interop: reference converters for two of the informative mappings, written from the pages alone
(1/interop/opentelemetry.md and 1/interop/inspect.md). Standard library, plus aef_produce.py (reading, result ids,
the ingest seal) and aef_schema.py (the writer schemas a converted run is checked against before it is written).

The pages are informative, and so is this tool: nothing in the specification depends on it. Beyond their tables, the
pages state rules and refusals of their own (OT-1 to OT-10 and IN-1 to IN-13, settled 10-09 and 10-10); the converter follows
them, and a refusal exits with status 2 naming its rule. It never fills a gap the pages leave. The checked examples
under 1/interop/examples/ run it (tools/check_interop.py).

The command line follows aef_produce.py's contract: input paths as arguments, one JSON value on standard output
(UTF-8, sorted keys, no ASCII escaping), exit status 0 when the conversion ran, and 2 with a message on standard
error for a usage or input error, with nothing written.

Commands (the JSON each prints):

  to-otel RUN OUT
      opentelemetry.md, "AEF -> OpenTelemetry": writes OUT, OTLP/JSON logs as OpenTelemetry's file exporter writes
      them (one LogsData object per line): one `gen_ai.evaluation.result` event per score of each result line of the
      run folder RUN, and one without a score value for a line without scores; one LogsData line per result line
      (OT-2), from the sealed lines, overlays not applied (OT-7). Prints {"events": n, "lines": m}. Refused, with
      nothing written: a run that does not verify (OT-8); a line without scores whose name the summary does not give
      (OT-1); a reasoning blob that is not UTF-8 or is over 4 MiB, a time timeUnixNano cannot hold (OT-9).
  from-otel LOGS OUT --run-id ID --from SOURCE --subject REF --subject-kind KIND --target-mode MODE
            [--content-capture on|off] [--at TIME]
      opentelemetry.md, "OpenTelemetry -> AEF": writes the run folder OUT (which must not exist yet, or be empty)
      from the OTLP/JSON logs file LOGS: one result line per `gen_ai.evaluation.result` event, the file itself as
      logs.otlp.jsonl, metrics.json with a declaration per scored metric, run.json with `imported` (OT-4), a
      summary.json without lanes, and the seal (`sealedBy: ingest`, README "A converted run is a new run"), sealed at
      TIME (the conversion time; default now), and verified (OT-8). Prints {"results": n, "runHash": hex}.
      Refused: the events of OT-4 and OT-6, and a run that would not verify (with --content-capture off, logs that
      carry content, [SEC-6]).
  to-inspect RUN OUT [--ignore-overlays]
      inspect.md, "AEF -> Inspect": writes OUT, the run as one Inspect EvalLog in its .json form (log format 2).
      An unscored value is the bare token NaN, as Inspect writes it (not JSON). Refused: a run that does not verify
      (IN-11); the cases of IN-1, IN-3, IN-4, IN-5 and IN-12; a run with overlay events unless --ignore-overlays leaves
      them out (IN-4).
  from-inspect LOG OUT --target-mode MODE [--content-capture on|off] [--at TIME]
      inspect.md, "Inspect -> AEF": writes the run folder OUT (which must not exist yet, or be empty) from the Inspect
      eval log LOG in its .json form (Inspect's NaN token read as an unscored value): run.json with `imported`
      (IN-6), a line per sample score and a rollup per reduced score (IN-7, IN-8), metrics.json, summary.json
      recomputed from the lines and compared with Inspect's results (IN-9), the case content as blobs and evidence
      when content is kept (default on), and the seal (`sealedBy: ingest`) at TIME (default now), unless the log is
      still `started`: a running run is not sealed. Prints {"results": n, "runHash": hex or null}. Refused: the cases
      of IN-6 to IN-10.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import re
import shutil
import sys
from decimal import Decimal
from fractions import Fraction
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_produce  # noqa: E402
import aef_schema  # noqa: E402
import aef_verify  # noqa: E402  (the reference verifier: a converter reads and writes only runs that verify)
from aef_produce import InputError, read_json, read_ndjson, result_id  # noqa: E402

AEF_VERSION = "1.0"
PRODUCER = {"name": "aef_interop.py", "version": AEF_VERSION}  # RUN-15: the converter is the converted run's producer
STATES = ("passed", "failed", "warn", "inconclusive", "scored",
          "not_measured", "not_applicable", "skipped", "error", "pending")  # RES-1
TYPED_ABSENCES = ("not_measured", "not_applicable", "skipped", "error", "pending")  # RES-1, RES-2
EVENT_NAME = "gen_ai.evaluation.result"
SCOPE = {"name": "agenteval"}  # OT-2: the worked example's instrumentation scope
REASON_MAX = 4096  # the writer schema's maxLength of a result's reason
LABEL_MAX = 64  # ... and of a score's label
_WRITER = None


def _writer_schemas():
    global _WRITER
    if _WRITER is None:
        _WRITER = aef_schema.load_schemas(TOOLS.parent / "1" / "schemas" / "writer")
    return _WRITER


def _check_schema(schema, document, where, page=None):
    """A converted run is a new run (interop/README.md): its documents are valid against the writer schemas."""
    errors = _writer_schemas().validate(schema, document)
    if errors:
        named = f": refused ({page})" if page else ""
        raise InputError(f"{where} would not be valid against the writer {schema} schema: {errors[0]}{named}")


def _verified(run_dir, what, page):
    """The outcome of `aef_verify.py run` on the folder, which must be intact or unsealed: no problem but an
    authorized withhold (OT-8, IN-11). Raises InputError naming the first problems otherwise."""
    try:
        result = aef_verify.op_run(str(run_dir))
    except (aef_verify.InputError, OSError) as error:
        raise InputError(f"{what} cannot be verified: {error} ({page})") from None
    if result["outcome"] not in ("intact", "unsealed"):
        shown = ", ".join(f"{path} {code}" for path, code in result["problems"][:4])
        raise InputError(f"{what} does not verify ({result['outcome']}: {shown}): refused ({page})")
    return result["outcome"]


def _compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _write_text(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


# ---------------------------------------------------------------------------- times (ENC-8 and OTLP's nanoseconds)

def _nanos(timestamp, what):
    """An RFC 3339 UTC time ([ENC-8]) as nanoseconds since the Unix epoch."""
    seconds, nanos = aef_produce._instant(timestamp, what)
    return seconds * 10 ** 9 + nanos


def _timestamp(nanos, where, page=None):
    """Nanoseconds since the Unix epoch as an RFC 3339 UTC time ([ENC-8]): the fraction with no trailing zero, and
    none when it is zero (OTLP's 1790949735000000000 is 2026-10-02T14:02:15Z)."""
    seconds, fraction = divmod(nanos, 10 ** 9)
    try:
        moment = datetime.datetime.fromtimestamp(seconds, tz=datetime.timezone.utc)
    except (OverflowError, OSError, ValueError):
        raise InputError(f"{where}: {nanos} ns is not a time AEF can write ([ENC-8])"
                         + (f": refused ({page})" if page else "")) from None
    if not 1 <= moment.year <= 9999:
        raise InputError(f"{where}: {nanos} ns is outside the years 0001 to 9999 ([ENC-8])"
                         + (f": refused ({page})" if page else ""))
    text = moment.strftime("%Y-%m-%dT%H:%M:%S")
    if fraction:
        text += "." + f"{fraction:09d}".rstrip("0")
    return text + "Z"


# ---------------------------------------------------------------------------- reading an AEF run

def _read_run(run_dir):
    """run.json, the result lines, and summary.json, metrics.json, evidence.ndjson and the overlay events when present."""
    run_dir = Path(run_dir)
    if not run_dir.is_dir():
        raise InputError(f"{run_dir}: not a folder")
    run = read_json(run_dir / "run.json", "run.json")
    if not isinstance(run.get("runId"), str):
        raise InputError("run.json has no runId ([RUN-1])")
    lines = read_ndjson(run_dir / "results.ndjson", "results.ndjson")
    optional = {}
    for name in ("summary.json", "metrics.json"):
        if (run_dir / name).is_file():
            optional[name] = read_json(run_dir / name, name)
    for name in ("evidence.ndjson", "overlays/events.ndjson"):
        optional[name] = read_ndjson(run_dir / name, name) if (run_dir / name).is_file() else []
    for n, line in enumerate(lines, start=1):
        for field in ("caseId", "path", "state"):
            if not isinstance(line.get(field), str):
                raise InputError(f"results.ndjson:{n} has no {field}")
        if line["state"] not in STATES:
            raise InputError(f"results.ndjson:{n}: the state {line['state']!r} is not one AEF 1.0 defines ([RES-1])")
    return run, lines, optional


def _blob_text(run_dir, digest, where, limit=None, rule=None):
    """The text of the blob a `sha256:<hex>` reference names ([EVD-3]), or None when the run does not hold it (a
    redaction can withhold it, [OVL-10]). A blob that is not UTF-8, or longer than limit bytes, is refused."""
    hexname = digest.split(":", 1)[1] if isinstance(digest, str) and digest.startswith("sha256:") else ""
    path = Path(run_dir) / "blobs" / "sha256" / hexname[:2] / hexname
    if not re.fullmatch("[0-9a-f]{64}", hexname) or not path.is_file():
        return None
    data = path.read_bytes()
    named = f" ({rule})" if rule else ""
    if limit is not None and len(data) > limit:
        raise InputError(f"{where}: the blob {digest} is {len(data)} bytes, over {limit}: refused{named}")
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        raise InputError(f"{where}: the blob {digest} is not UTF-8 text: refused{named}") from None


def _explanation(run_dir, run, line, where, limit=None, rule=None):
    """`reason`, or the `reasoning` blob's text when the line has no reason and the run keeps content."""
    if isinstance(line.get("reason"), str):
        return line["reason"]
    if "reasoning" in line and run.get("contentCapture") != "off":  # RUN-11: a reader takes an absent value as on
        return _blob_text(run_dir, line["reasoning"].get("blob"), where, limit, rule)
    return None


# ---------------------------------------------------------------------------- AEF -> OpenTelemetry

def _attribute(key, kind, value):
    return {"key": key, "value": {kind: value}}


def _double(value, where):
    """A score as an OTLP double (the convention types gen_ai.evaluation.score.value as a double), written as the
    run writes it: 820 stays 820, a JSON number with the same value as 820.0."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise InputError(f"{where}: a score value is not a number")
    if isinstance(value, int) and abs(value) > 2 ** 53:
        raise InputError(f"{where}: the score {value} has no exact double")
    return value


def _unscored_name(line, summary, where):
    """OT-1: the metric of the summary entries at the line's path, in the lanes the line belongs to
    ([SUM-3]), when they name exactly one."""
    lanes = (summary or {}).get("lanes") or []
    names = [lane.get("lane") for lane in lanes if isinstance(lane, dict)]
    metrics = []
    for lane in lanes:
        if not isinstance(lane, dict) or not aef_produce._belongs(line, lane.get("lane"), names):
            continue
        for entry in lane.get("metrics") or []:
            if isinstance(entry, dict) and entry.get("path") == line["path"] and entry.get("metric") not in metrics:
                metrics.append(entry.get("metric"))
    if len(metrics) != 1 or not isinstance(metrics[0], str):
        found = "no summary.json" if summary is None else f"{len(metrics)} metric(s) in the summary at that path"
        raise InputError(f"{where}: the event of a line without scores is named after the one metric the summary "
                         f"gives at its path (opentelemetry.md, OT-1), and the run has {found}")
    return metrics[0]


def otel_events(run_dir, run, line, summary, where):
    """The `gen_ai.evaluation.result` log records of one result line."""
    record = {}
    time = line.get("endedAt", line.get("startedAt"))  # `endedAt` (or `startedAt`) -> timeUnixNano
    if time is not None:
        nanos = _nanos(time, f"{where} time")
        if not 1 <= nanos <= 2 ** 64 - 1:  # OT-9: an unsigned 64-bit count of nanoseconds, 0 meaning unknown
            raise InputError(f"{where}: the time {time} is outside what timeUnixNano holds (1970-01-01T00:00:00."
                             "000000001Z to 2554-07-21T23:34:33.709551615Z): refused (opentelemetry.md, OT-9)")
        record["timeUnixNano"] = str(nanos)
    record["eventName"] = EVENT_NAME
    link = line.get("traceLink")
    if isinstance(link, dict):  # the evaluated operation: the event's parent (a traceId alone names its trace)
        record["traceId"] = link["traceId"]
        if "spanId" in link:
            record["spanId"] = link["spanId"]
    # OT-3: SEC-6 forbids the explanation in the logs of a run that keeps no content; none is written for such a run.
    explanation = None if run.get("contentCapture") == "off" else \
        _explanation(run_dir, run, line, where, 4 * 1024 * 1024, "opentelemetry.md, OT-9")
    scores = line.get("scores") or []
    if not all(isinstance(s, dict) and isinstance(s.get("metric"), str) for s in scores):
        raise InputError(f"{where}: a score without a metric")
    targets = [(s["metric"], _double(s.get("value"), where)) for s in scores] or \
              [(_unscored_name(line, summary, where), None)]
    records = []
    for name, value in targets:
        attributes = [_attribute("gen_ai.evaluation.name", "stringValue", name)]
        if value is not None:
            attributes.append(_attribute("gen_ai.evaluation.score.value", "doubleValue", value))
        attributes.append(_attribute("gen_ai.evaluation.score.label", "stringValue", line["state"]))  # RUN-14
        if line["state"] == "error":
            attributes.append(_attribute("error.type", "stringValue", "_OTHER"))  # AEF records no error class
        if explanation is not None:
            attributes.append(_attribute("gen_ai.evaluation.explanation", "stringValue", explanation))
        attributes.append(_attribute("test.case.name", "stringValue", line["caseId"]))
        records.append(dict(record, attributes=attributes))
    return records


def to_otel(run_dir, out):
    """Writes OUT: one OTLP/JSON LogsData line per result line of the run (OT-2): its sealed lines, overlays not
    applied (OT-7), of a run that verifies (OT-8). A refused line refuses the whole export (OT-1, OT-9): nothing is
    written. Returns the counts."""
    _verified(run_dir, "the run", "opentelemetry.md, OT-8")
    run, lines, optional = _read_run(run_dir)
    summary = optional.get("summary.json")
    service = ((run.get("subject") or {}).get("telemetry") or {}).get("serviceName")
    output, events = [], 0
    for n, line in enumerate(lines, start=1):
        records = otel_events(run_dir, run, line, summary, f"results.ndjson:{n}")
        events += len(records)
        resource_logs = {}
        if isinstance(service, str):  # run.json subject.telemetry.serviceName -> resource service.name
            resource_logs["resource"] = {"attributes": [_attribute("service.name", "stringValue", service)]}
        resource_logs["scopeLogs"] = [{"scope": dict(SCOPE), "logRecords": records}]
        output.append(_compact({"resourceLogs": [resource_logs]}) + "\n")
    _write_text(out, "".join(output))
    return {"events": events, "lines": len(output)}


# ---------------------------------------------------------------------------- OpenTelemetry -> AEF

def _attributes(container, where):
    """An OTLP attribute list as {key: AnyValue}; a key twice is refused (OTLP: keys are unique)."""
    attributes = {}
    raw = container.get("attributes", []) if isinstance(container, dict) else []
    if not isinstance(raw, list):
        raise InputError(f"{where}: attributes is not a list")
    for a in raw:
        if not isinstance(a, dict) or not isinstance(a.get("key"), str):
            raise InputError(f"{where}: an attribute is not {{key, value}}")
        if a["key"] in attributes:
            raise InputError(f"{where}: the attribute {a['key']!r} appears twice")
        attributes[a["key"]] = a.get("value", {})
    return attributes


def _string(attributes, key, where):
    value = attributes.get(key)
    if value is None:
        return None
    if not (isinstance(value, dict) and set(value) == {"stringValue"} and isinstance(value["stringValue"], str)):
        raise InputError(f"{where}: {key} is not a string attribute")
    return value["stringValue"]


def _score_value(attributes, where):
    """gen_ai.evaluation.score.value: a double (a JSON number) or an int (OTLP/JSON writes a 64-bit int as a decimal
    string). OTLP/JSON's NaN and Infinity strings are refused: AEF numbers are finite ([ENC-3])."""
    value = attributes.get("gen_ai.evaluation.score.value")
    if value is None:
        return None
    if isinstance(value, dict) and set(value) == {"doubleValue"}:
        number = value["doubleValue"]
        if not isinstance(number, bool) and isinstance(number, (int, float)) and math.isfinite(number):
            return number
    if isinstance(value, dict) and set(value) == {"intValue"}:
        number = value["intValue"]
        if isinstance(number, str) and re.fullmatch("-?[0-9]+", number):
            return int(number)
        if isinstance(number, int) and not isinstance(number, bool):
            return number
    raise InputError(f"{where}: gen_ai.evaluation.score.value is not a finite number ([ENC-3])")


def _unix_nanos(record, where):
    """timeUnixNano as an integer; None when absent or 0 (OTLP: unknown)."""
    value = record.get("timeUnixNano")
    if value is None:
        return None
    if isinstance(value, str) and re.fullmatch("[0-9]+", value):
        value = int(value)
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise InputError(f"{where}: timeUnixNano is not a count of nanoseconds")
    return value or None


def _hex_id(record, key, size, where):
    """A trace or span id: hex ([RUN-14]: written in lower case); an empty or all-zero id is no id (OTLP)."""
    value = record.get(key)
    if value in (None, "") or (isinstance(value, str) and set(value) == {"0"}):
        return None
    if not isinstance(value, str) or not re.fullmatch(f"[0-9a-fA-F]{{{size}}}", value):
        raise InputError(f"{where}: {key} is not {size} hex characters")
    return value.lower()


def _text(value, field, maximum, where, page=None):
    """caseId, a path, an evaluator id, a metric: 1 to maximum characters, no control character ([RES-4])."""
    if not 1 <= len(value) <= maximum or re.search("[\x00-\x1f\x7f-\x9f]", value):
        raise InputError(f"{where}: {value!r} cannot be a {field} (1 to {maximum} characters, no control character)"
                         + (f": refused ({page})" if page else ""))
    return value


def _refuse(where, what, item="OT-6"):
    raise InputError(f"{where}: {what}: refused (opentelemetry.md, OpenTelemetry -> AEF, {item})")


def imported_line(run_id, record, where):
    """The result line of one `gen_ai.evaluation.result` event, and its time in nanoseconds (or None)."""
    a = _attributes(record, where)
    name = _string(a, "gen_ai.evaluation.name", where)
    if name is None:
        _refuse(where, "an event without gen_ai.evaluation.name, which the convention requires")
    value = _score_value(a, where)
    label = _string(a, "gen_ai.evaluation.score.label", where)
    explanation = _string(a, "gen_ai.evaluation.explanation", where)
    error_type = _string(a, "error.type", where)
    case = _string(a, "test.case.name", where)
    if case is None:
        case = _string(a, "gen_ai.response.id", where)  # lossy: the completion stands for the case
    if case is None:
        _refuse(where, "an event with neither test.case.name nor gen_ai.response.id (a result needs a caseId)")
    score_label = None
    if label in STATES:  # a label that is an AEF state name gives the state (RUN-14)
        if label == "pending":
            _refuse(where, "the label pending (a converted run is closed, [RES-3])")
        if error_type is not None and label != "error":
            _refuse(where, f"the label {label!r} beside error.type {error_type!r}")
        if label in TYPED_ABSENCES and value is not None:
            _refuse(where, f"the typed absence {label!r} with a score value ([RES-2]: it carries no scores)")
        state = label
    elif label is not None:  # any other label: scored, the label kept beside the value
        if value is None:
            _refuse(where, f"the label {label!r}, which is not a state name, without a score value "
                           "(scores[].value is required)")
        if error_type is not None:
            _refuse(where, f"the label {label!r} beside error.type {error_type!r}")
        if len(label) > LABEL_MAX:
            _refuse(where, f"a label longer than {LABEL_MAX} characters, the most scores[].label holds")
        state, score_label = "scored", label
    elif error_type is not None:  # error.type -> state: error
        if value is not None:
            _refuse(where, f"error.type {error_type!r} beside a score value ([RES-2])")
        state = "error"
    elif value is not None:  # a value and no label
        state = "scored"
    else:
        _refuse(where, "an event with no label, no score value and no error.type")
    # explanation -> reason (up to 4096 characters); error.type -> reason when there is no explanation (OT-5)
    reason = explanation[:REASON_MAX] if explanation else error_type if state == "error" else None
    if state in TYPED_ABSENCES and not reason:
        _refuse(where, f"the typed absence {state!r} without an explanation ([RES-2]: it needs a reason)")
    _text(case, "caseId", 256, where)
    _text(name, "path, evaluator id or metric", 256, where)
    line = {"schemaVersion": AEF_VERSION, "resultId": result_id(run_id, case, name),  # RES-4, no trial
            "caseId": case, "path": name, "evaluator": {"id": name}, "state": state}
    if reason:
        line["reason"] = reason
    if value is not None:
        score = {"metric": name, "value": value}
        if score_label is not None:
            score["label"] = score_label
        line["scores"] = [score]
    nanos = _unix_nanos(record, where)
    if nanos is not None:
        line["endedAt"] = _timestamp(nanos, where)  # timeUnixNano -> endedAt
    trace_id, span_id = _hex_id(record, "traceId", 32, where), _hex_id(record, "spanId", 16, where)
    if span_id is not None and trace_id is None:
        raise InputError(f"{where}: a spanId without a traceId")
    if trace_id is not None:  # the event's parent -> traceLink
        line["traceLink"] = {"traceId": trace_id, **({"spanId": span_id} if span_id else {})}
    return line, nanos


def _within_limits(data):
    """[ENC-17] on the logs file, which the converted run keeps as logs.otlp.jsonl: at most 1,000,000 lines, each at
    most 4 MiB and at most 64 deep (objects and arrays, counted outside strings)."""
    lines = data.split(b"\n")
    if len(lines) - 1 > 1_000_000:
        raise InputError("the logs file has more than 1,000,000 lines ([ENC-17])")
    for n, line in enumerate(lines, start=1):
        if len(line) > 4 * 1024 * 1024:
            raise InputError(f"logs line {n} is longer than 4 MiB ([ENC-17])")
        depth = deepest = 0
        in_string = escaped = False
        for byte in line:
            if in_string:
                escaped, in_string = (False, True) if escaped else (byte == 0x5C, byte != 0x22)
            elif byte == 0x22:
                in_string = True
            elif byte in (0x7B, 0x5B):
                depth += 1
                deepest = max(deepest, depth)
            elif byte in (0x7D, 0x5D):
                depth -= 1
        if deepest > 64:
            raise InputError(f"logs line {n} nests deeper than 64 ([ENC-17])")


def _schema_url(container, where, urls):
    url = container.get("schemaUrl") if isinstance(container, dict) else None
    if url in (None, ""):
        return
    if not isinstance(url, str) or not re.fullmatch("[a-z][a-z0-9+.-]*:[!-~]+", url) or len(url) > 2048:
        raise InputError(f"{where}: schemaUrl {url!r} is not a URI otel.schemaUrls can hold")
    if url not in urls:
        urls.append(url)


def from_otel(logs_path, out, run_id, source, subject_ref, subject_kind, target_mode, at, capture="on"):
    """Writes the run folder OUT from the OTLP/JSON logs file (OT-4, OT-5, OT-6). Returns the counts."""
    out = Path(out)
    if out.exists() and not (out.is_dir() and not any(out.iterdir())):
        raise InputError(f"{out}: OUT is a folder that does not exist yet, or an empty one")
    if capture not in ("on", "off"):
        raise InputError("--content-capture is on or off ([RUN-11])")
    sealed_at = _nanos(at, "--at")
    data = Path(logs_path).read_bytes() if Path(logs_path).is_file() else None
    if data is None:
        raise InputError(f"{logs_path}: not a file")
    _within_limits(data)
    requests = read_ndjson(logs_path, "the logs file")  # one LogsData object per line ([ENC-5] for logs.otlp.jsonl)
    lines, times, services, urls, seen = [], [], [], [], {}
    for n, request in enumerate(requests, start=1):
        resource_logs = request.get("resourceLogs", [])
        if not isinstance(resource_logs, list):
            raise InputError(f"logs line {n}: resourceLogs is not a list")
        for i, resource in enumerate(resource_logs):
            where = f"logs line {n}, resourceLogs[{i}]"
            if not isinstance(resource, dict):
                raise InputError(f"{where} is not an object")
            if "instrumentationLibraryLogs" in resource:
                raise InputError(f"{where}: records under instrumentationLibraryLogs, the names before OTLP 1.0, "
                                 "which a reader does not read ([RUN-14])")
            _schema_url(resource, where, urls)
            service = _string(_attributes(resource.get("resource", {}), f"{where}.resource"), "service.name", where)
            for j, scope in enumerate(resource.get("scopeLogs") or []):
                if not isinstance(scope, dict):
                    raise InputError(f"{where}.scopeLogs[{j}] is not an object")
                _schema_url(scope, f"{where}.scopeLogs[{j}]", urls)
                for k, record in enumerate(scope.get("logRecords") or []):
                    at_record = f"{where}.scopeLogs[{j}].logRecords[{k}]"
                    if not isinstance(record, dict):
                        raise InputError(f"{at_record} is not an object")
                    if record.get("eventName") != EVENT_NAME:
                        continue  # another record: kept in logs.otlp.jsonl, no result
                    line, nanos = imported_line(run_id, record, at_record)
                    key = (line["caseId"], line["path"])
                    if key in seen:
                        _refuse(at_record, f"a second event of case {key[0]!r} named {key[1]!r} (the first is at "
                                           f"{seen[key]}): the importer takes the path from the name, so their lines "
                                           "would have one resultId ([RES-4])")
                    seen[key] = at_record
                    lines.append(line)
                    if nanos is not None:
                        times.append(nanos)
                    if service is not None and service not in services:
                        services.append(service)
    if len(services) > 1:
        raise InputError(f"the events name {len(services)} services ({', '.join(services)}), and a run has one subject "
                         "(opentelemetry.md, OpenTelemetry -> AEF, OT-4)")
    # OT-4: the run header. Everything the converter supplies is listed in imported.asserted (RUN-15).
    started, ended = (min(times), max(times)) if times else (sealed_at, sealed_at)
    if sealed_at < ended:
        raise InputError("--at is before the last event: the run is sealed after it closes ([SEAL-1])")
    subject = {"ref": subject_ref, "kind": subject_kind}
    if services:
        subject["telemetry"] = {"serviceName": services[0]}  # resource service.name
    run = {"schemaVersion": AEF_VERSION, "runId": run_id, "status": "completed", "producer": dict(PRODUCER),
           "subject": subject, "execution": {"targetMode": target_mode},
           "startedAt": _timestamp(started, "the first event"), "endedAt": _timestamp(ended, "the last event")}
    if urls:
        run["otel"] = {"schemaUrls": urls}  # the source's schema URL
    run["contentCapture"] = capture  # OT-4: the converter's choice, on unless asked otherwise (RUN-11, RUN-15)
    run["imported"] = {"from": source, "asserted": ["runId", "status", "subject.ref", "subject.kind",
                                                    "execution.targetMode", "startedAt", "endedAt",
                                                    "contentCapture"]}
    metrics = {"schemaVersion": AEF_VERSION, "metrics": []}
    for line in lines:  # no metric declaration: kind score, direction none, scale unbounded
        for score in line.get("scores", []):
            if all(m["id"] != score["metric"] for m in metrics["metrics"]):
                metrics["metrics"].append({"id": score["metric"], "kind": "score", "direction": "none",
                                           "scale": "unbounded"})
    summary = {"schemaVersion": AEF_VERSION, "runId": run_id, "lanes": []}  # OT-4: the events carry no summary
    _check_schema("run", run, "run.json")
    _check_schema("metrics", metrics, "metrics.json")
    _check_schema("summary", summary, "summary.json")
    for n, line in enumerate(lines, start=1):
        _check_schema("result", line, f"results.ndjson:{n}")
    created = not out.exists()
    out.mkdir(parents=True, exist_ok=True)
    try:
        _write_text(out / "run.json", json.dumps(run, indent=2, ensure_ascii=False) + "\n")
        _write_text(out / "results.ndjson", "".join(_compact(line) + "\n" for line in lines))
        _write_text(out / "metrics.json", json.dumps(metrics, indent=2, ensure_ascii=False) + "\n")
        _write_text(out / "summary.json", json.dumps(summary, indent=2, ensure_ascii=False) + "\n")
        (out / "logs.otlp.jsonl").write_bytes(data)  # the log records themselves, as they came
        sealed = aef_produce.seal_write(out, "ingest", _timestamp(sealed_at, "--at"))  # README: sealed as ingest
        _verified(out, "the converted run", "opentelemetry.md, OpenTelemetry -> AEF, OT-8")
    except BaseException:
        if created:
            shutil.rmtree(out, ignore_errors=True)
        else:
            for child in list(out.iterdir()):
                shutil.rmtree(child) if child.is_dir() else child.unlink()
        raise
    return {"results": len(lines), "runHash": sealed["runHash"]}


# ---------------------------------------------------------------------------- AEF -> Inspect

REDUCERS = {"MajorityVote": "majority", "Mean": "mean", "Median": "median", "Max": "max"}
USAGE_FIELDS = (("gen_ai.usage.input_tokens", "input_tokens"), ("gen_ai.usage.output_tokens", "output_tokens"),
                ("gen_ai.usage.cache_write.input_tokens", "input_tokens_cache_write"),
                ("gen_ai.usage.cache_read.input_tokens", "input_tokens_cache_read"),
                ("gen_ai.usage.reasoning.output_tokens", "reasoning_tokens"))
SCORE_METADATA = ("parentResultId", "severity", "verdictRule", "annotator", "aggregation", "component")


def _reducer(aggregation, k, n, where):
    """An AEF trial aggregation as Inspect's epochs reducer (inspect.md, `suite.executionPolicy.aggregation`)."""
    if aggregation in REDUCERS:
        return REDUCERS[aggregation]
    if aggregation == "AnyPass":
        return "at_least_1"
    if aggregation == "AllPass" and isinstance(n, int):
        return f"at_least_{n}"
    if aggregation == "PassAtK" and isinstance(k, int):
        return f"pass_at_{k}"
    raise InputError(f"{where}: the aggregation {aggregation!r} has no Inspect reducer without its k or n")


def _in_refusal(where, what, item):
    raise InputError(f"{where}: {what}: refused (inspect.md, AEF -> Inspect, {item})")


class _Usage:
    """Inspect's ModelUsage totals of AEF usage entries: tokens summed exactly, costUsd as total_cost."""

    def __init__(self):
        self.tokens, self.cost = {}, None

    def add(self, entry):
        for aef, inspect in USAGE_FIELDS:
            if isinstance(entry.get(aef), int):
                self.tokens[inspect] = self.tokens.get(inspect, 0) + entry[aef]
        if isinstance(entry.get("costUsd"), (int, float)) and not isinstance(entry.get("costUsd"), bool):
            self.cost = (self.cost or Fraction(0)) + Fraction(entry["costUsd"])

    def value(self):
        out = {k: self.tokens[k] for k in ("input_tokens", "output_tokens") if k in self.tokens}
        if out:
            out["total_tokens"] = sum(out.values())  # Inspect's total: input and output
        out.update({k: self.tokens[k] for _, k in USAGE_FIELDS[2:] if k in self.tokens})
        if self.cost is not None:
            out["total_cost"] = float(self.cost)
        return out


def _usage_maps(entries):
    """(model_usage, role_usage) of (usage entry, the model it is counted under or None) pairs."""
    by_model, by_role = {}, {}
    for entry, model in entries:
        by_role.setdefault(entry.get("role"), _Usage()).add(entry)
        if model is not None:
            by_model.setdefault(model, _Usage()).add(entry)
    return {m: u.value() for m, u in by_model.items()}, {r: u.value() for r, u in by_role.items()}


def inspect_score(run_dir, run, line, where):
    """The Inspect Score of one result line."""
    scores = line.get("scores") or []
    score = {}
    if line["state"] in TYPED_ABSENCES or not scores:
        # A typed absence: NaN, the state name in reason. IN-2: so is a measured line with no score.
        score["value"] = math.nan
        score["reason"] = line["state"]
    else:
        values = {}
        for s in scores:
            if s.get("metric") in values:
                raise InputError(f"{where}: the metric {s.get('metric')!r} is scored twice")
            values[s.get("metric")] = s["label"] if "label" in s else s.get("value")  # a label is the value
        score["value"] = next(iter(values.values())) if len(values) == 1 else values  # two or more: a map
    # IN-13: a run that keeps no content gives no explanation, as OT-3. IN-12: a reasoning blob that
    # is not UTF-8 refuses the export; one an authorized redaction withholds is left out.
    explanation = None if run.get("contentCapture") == "off" else \
        _explanation(run_dir, run, line, where, rule="inspect.md, AEF -> Inspect, IN-12")
    if explanation is not None:
        score["explanation"] = explanation
    aef = {"resultId": line.get("resultId"), "state": line["state"], "evaluator": line.get("evaluator")}
    aef.update({k: line[k] for k in SCORE_METADATA if line.get(k) is not None})
    score["metadata"] = {"aef": aef}
    return score


def _case_content(run_dir, lines, evidence, where):
    """samples[].input and target: the text of the input and expected evidence the sample's lines cite (IN-5)."""
    records = {e.get("evidenceId"): e for e in evidence if isinstance(e, dict)}
    found = {}
    for line in lines:
        for evidence_id in line.get("evidence") or []:
            record = records.get(evidence_id)
            kind = record.get("kind") if record else None
            if kind in ("output", "transcript"):
                _in_refusal(where, f"{kind} evidence: samples[].{'output' if kind == 'output' else 'messages'} is "
                                   "structured, and the page does not say how a blob's text becomes it", "IN-5")
            if kind not in ("input", "expected"):
                continue
            blob = (record.get("link") or {}).get("blob")
            if not blob:
                _in_refusal(where, f"{kind} evidence {evidence_id} that is not a blob of the run", "IN-5")
            text = _blob_text(run_dir, blob, where, rule="inspect.md, AEF -> Inspect, IN-12")
            if text is None:  # IN-12: withheld by an authorized redaction (the run verified): left out
                continue
            if found.get(kind, text) != text:
                _in_refusal(where, f"two {kind} evidence records with different text", "IN-5")
            found[kind] = text
    return found.get("input", ""), found.get("expected", "")


def _root_fact(roots, field, where):
    """A fact of "a case's root line" (startedAt, endedAt, durationMs): roots that carry the same value (times as
    instants, [ENC-8]) count as one; two different values are refused (IN-5)."""
    carried = [root[field] for root in roots if field in root]
    same = (lambda v: _nanos(v, f"{where} {field}")) if field in ("startedAt", "endedAt") else (lambda v: v)
    if len({same(v) for v in carried}) > 1:
        _in_refusal(where, f"root lines carry {len(set(map(same, carried)))} different {field} values, and the table "
                           "takes it from the case's root line", "IN-5")
    return carried[0] if carried else None


def inspect_sample(run_dir, run, case_id, trial, lines, evidence, where):
    """One EvalSample: a case's lines of one trial (or of a case run once)."""
    sample = {"id": case_id, "epoch": 1 if trial is None else trial + 1}  # epoch = trial + 1
    sample["input"], sample["target"] = _case_content(run_dir, lines, evidence, where)  # required: empty if not kept
    sample["scores"] = {line["path"]: inspect_score(run_dir, run, line, f"{where}, path {line['path']!r}")
                        for line in lines}
    entries = []
    for line in lines:
        for entry in line.get("usage") or []:
            model = entry.get("model")
            if model is None and entry.get("role") == "judge":
                model = (line.get("annotator") or {}).get("model")  # a judge's entry without a model
            entries.append((entry, model))
    model_usage, role_usage = _usage_maps(entries)
    if model_usage:
        sample["model_usage"] = model_usage
    if role_usage:
        sample["role_usage"] = role_usage
    roots = [line for line in lines if line.get("parentResultId") is None]
    started, ended = _root_fact(roots, "startedAt", where), _root_fact(roots, "endedAt", where)
    duration = _root_fact(roots, "durationMs", where)
    if started is not None:
        sample["started_at"] = started
    if ended is not None:
        sample["completed_at"] = ended
    if duration is not None:
        sample["total_time"] = float(Fraction(duration) / 1000)  # seconds
    return sample


def inspect_results(summary, metrics, samples):
    """IN-3: `results` from summary.json, one EvalScore per entry."""
    kinds = {m.get("id"): m.get("kind") for m in (metrics or {}).get("metrics") or [] if isinstance(m, dict)}
    scores, paths = [], set()
    for lane in summary.get("lanes") or []:
        for entry in lane.get("metrics") or []:
            where = f"summary.json, lane {lane.get('lane')!r}, {entry.get('metric')!r} at {entry.get('path')!r}"
            if entry.get("path") in paths:
                _in_refusal(where, "a second entry at one path: Inspect has one EvalScore per scorer", "IN-3")
            paths.add(entry.get("path"))
            if kinds.get(entry.get("metric")) == "count":
                _in_refusal(where, "an entry of kind count, whose value is a sum", "IN-3")
            aggregate = entry.get("aggregate") or {}
            name = aggregate.get("method", "mean")
            metric = {"name": name, "value": math.nan if entry.get("value") is None else entry["value"]}
            params = {k: v for k, v in aggregate.items() if k != "method"}
            if params:
                metric["params"] = params
            measured = {name: metric}
            if entry.get("stderr") is not None:
                measured["stderr"] = {"name": "stderr", "value": entry["stderr"]}
            aef = {"lane": lane.get("lane"), "metric": entry.get("metric")}
            aef.update({k: entry[k] for k in ("N", "notMeasured", "verdict", "rule", "ci") if entry.get(k) is not None})
            scores.append({"name": entry.get("path"), "scorer": entry.get("path"), "scored_samples": entry.get("n"),
                           "unscored_samples": entry.get("notMeasured"), "metrics": measured,
                           "metadata": {"aef": aef}})
    return {"total_samples": len(samples), "completed_samples": len(samples), "scores": scores}


def to_inspect(run_dir, out, ignore_overlays=False):
    """Writes OUT: the run as one Inspect EvalLog in .json form, from a run that verifies (IN-11). Returns the
    counts."""
    _verified(run_dir, "the run", "inspect.md, AEF -> Inspect, IN-11")
    run, lines, optional = _read_run(run_dir)
    if optional["overlays/events.ndjson"] and not ignore_overlays:
        _in_refusal("overlays/events.ndjson", f"{len(optional['overlays/events.ndjson'])} overlay event(s): the table "
                                              "names Score.history and log_updates, not the shape of their entries "
                                              "(--ignore-overlays leaves the overlays out)", "IN-4")
    suite, subject = run.get("suite"), run.get("subject") or {}
    if not isinstance(suite, dict):
        _in_refusal("run.json", "no suite: eval.task is required", "IN-1")
    if not str(suite.get("ref")).startswith("suite:"):
        _in_refusal("run.json", f"the suite ref {suite.get('ref')!r} is not suite:<task>", "IN-1")
    judges = run.get("judges") or []
    if len(judges) > 1:
        _in_refusal("run.json", f"{len(judges)} judges: eval.model_roles holds one model for the role judge", "IN-1")
    status = {"completed": "success", "aborted": "error", "running": "started"}.get(run.get("status"))
    if status is None:
        raise InputError(f"run.json: the status {run.get('status')!r} is not one AEF 1.0 defines ([RUN-5])")
    ref = subject.get("ref", "")
    kind_part, _, name_part = ref.partition(":")
    name_part = _unref(name_part)  # the name decoded, so the import's ENC-13 encoding gives the ref back
    model = name_part if subject.get("kind") == "model" and kind_part == "model" else f"{kind_part}:{name_part}"
    case_ids = list(dict.fromkeys(line["caseId"] for line in lines))
    spec = {"eval_id": run["runId"], "run_id": run["runId"], "created": run.get("startedAt"),
            "task": suite["ref"][len("suite:"):], "task_version": suite.get("version"),
            "dataset": {"samples": len(case_ids), "sample_ids": case_ids}, "model": model}  # IN-1: dataset, model
    if judges:
        spec["model_roles"] = {"judge": {"model": judges[0].get("model")}}
    policy = suite.get("executionPolicy") or {}
    config = {}
    if "trialsPerCase" in policy:
        config["epochs"] = policy["trialsPerCase"]
    if "aggregation" in policy:
        config["epochs_reducer"] = [_reducer(policy["aggregation"], policy.get("k"), policy.get("trialsPerCase"),
                                             "run.json suite.executionPolicy")]
    spec["config"] = config
    producer = run.get("producer") or {}
    spec["packages"] = {producer.get("name"): producer.get("version")}  # lossy: Inspect has no producing tool
    facts = {}
    if "digest" in suite:
        facts["suite"] = {"digest": suite["digest"]}
    judged = [{"model": j.get("model"), **{k: j[k] for k in ("rubricDigest", "calibration") if k in j}}
              for j in judges]
    if any(len(j) > 1 for j in judged):
        facts["judges"] = judged
    facts.update({k: run[k] for k in ("execution", "contentCapture", "deployment", "imported") if k in run})
    if facts:
        spec["metadata"] = {"aef": facts}
    log = {"version": 2, "status": status, "eval": spec}

    groups, rollups = {}, {}
    for line in lines:
        if "trials" in line:  # a rollup: reductions[], with the matching reducer
            t = line["trials"]
            key = (line["path"], _reducer(t.get("aggregation"), t.get("k"), t.get("n"), f"rollup {line['resultId']}"))
            rollups.setdefault(key, []).append(line)
        else:
            groups.setdefault((line["caseId"], line.get("trial")), []).append(line)
    evidence = optional["evidence.ndjson"]
    samples = [inspect_sample(run_dir, run, case_id, trial, group, evidence,
                              f"case {case_id!r}" + ("" if trial is None else f", trial {trial}"))
               for (case_id, trial), group in groups.items()]
    if "summary.json" in optional:
        log["results"] = inspect_results(optional["summary.json"], optional.get("metrics.json"), samples)
    stats = {"started_at": run.get("startedAt")}
    if "endedAt" in run:
        stats["completed_at"] = run["endedAt"]
    usage = (optional.get("summary.json") or {}).get("usage") or []
    model_usage, role_usage = _usage_maps((u, u.get("model")) for u in usage if isinstance(u, dict))
    if model_usage:
        stats["model_usage"] = model_usage
    if role_usage:
        stats["role_usage"] = role_usage
    log["stats"] = stats
    if status == "error":
        log["error"] = {"message": run.get("abortReason", ""), "traceback": "", "traceback_ansi": ""}
    log["samples"] = samples
    if rollups:
        log["reductions"] = [
            {"scorer": path, "reducer": reducer,
             "samples": [dict(inspect_score(run_dir, run, line, f"rollup {line['resultId']}"), sample_id=line["caseId"])
                         for line in group]}
            for (path, reducer), group in rollups.items()]
    _write_text(out, json.dumps(log, indent=2, ensure_ascii=False) + "\n")  # NaN as Inspect writes it
    return {"samples": len(samples), "reductions": len(rollups)}


# ---------------------------------------------------------------------------- Inspect -> AEF

LETTERS = {"C": 1, "I": 0, "P": 0.5, "N": 0}  # Inspect's letters, as its metrics read them
LETTER_STATES = {"C": "passed", "I": "failed", "P": "warn", "N": "failed"}
MODEL_BLAMED = ("refusal", "no_response", "invalid_response_format")  # state failed, whatever the value
INSTRUMENT_BLAMED = ("grader_failed", "scoring_failed")  # with NaN: state error (IN-7)
FROM_REDUCER = {"majority": "MajorityVote", "mode": "MajorityVote", "mean": "Mean", "median": "Median", "max": "Max"}
MEAN_METRICS = ("accuracy", "mean")  # a summary entry's mean (SUM-5)
NO_VALUE_REASON = "Inspect recorded no value (NaN) and no reason"  # IN-7
ERROR_REASON = "Inspect recorded an error without a message"  # IN-8
_INSPECT_TIME = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?"
                           r"(Z|[+-][0-9]{2}:[0-9]{2})")
_AEF_ID = re.compile(r"[A-Za-z0-9._:-]{1,128}")
_METHOD = re.compile(r"[a-z][a-z0-9@._-]{0,63}")


SAFE_INTEGER = 2 ** 53 - 1  # [ENC-4]: an integral value every binary64 reader reads exactly


def _nan_allowed(path):
    """Where an Inspect log may hold its bare NaN token (IN-6): a score's value or a map member of it, a reduced
    score's, a metric's value, a limit's number (which IN-8 then refuses) and case content (which JCS refuses when it
    is kept). Anywhere else, a NaN refuses the log."""
    p = path
    member = len(p) == 5 or len(p) == 6 and isinstance(p[5], str)  # the value, or a member of a map one, not a list's
    if member and p[0] == "samples" and p[2] == "scores" and p[4] == "value":
        return True
    if member and p[0] == "reductions" and p[2] == "samples" and p[4] == "value":
        return True
    if len(p) == 6 and p[:2] == ("results", "scores") and p[3] == "metrics" and p[5] == "value":
        return True
    if len(p) == 4 and p[0] == "samples" and p[2:] == ("limit", "limit"):
        return True
    return len(p) >= 3 and p[0] == "samples" and p[2] in ("input", "target", "output", "messages")


def _read_inspect_log(path):
    """An Inspect log in its .json form, read as I-JSON (RFC 7493), as AEF reads its own files ([ENC-1]-[ENC-3]),
    within [ENC-17]'s nesting depth, with one exception Inspect needs: its bare NaN token, where _nan_allowed says.
    Everything else is refused as the log is read, wherever it is (IN-6): a byte-order mark or bytes that are
    not UTF-8; a member named twice; a string with an unpaired surrogate; Infinity; a number that overflows binary64
    (1e400); nesting deeper than 64. Every number is read as binary64 ([ENC-4]): an integer beyond 2^53 reads as the
    binary64 value a JSON parser gives it."""
    rule = "(inspect.md, Inspect -> AEF, IN-6)"
    try:
        data = Path(path).read_bytes()
    except OSError as error:
        raise InputError(f"{path}: {error}") from None
    try:
        text = aef_produce._decode(data, "the Inspect log")
    except InputError as error:
        raise InputError(f"{error}: refused {rule}") from None
    if aef_verify.nesting_depth(data) > aef_verify.MAX_DEPTH:
        raise InputError(f"the Inspect log nests deeper than {aef_verify.MAX_DEPTH} ([ENC-17]): refused {rule}")

    def members(pairs):
        obj = {}
        for key, value in pairs:
            if key in obj:
                raise InputError(f"the Inspect log: the member {key!r} appears twice ([ENC-2]): refused {rule}")
            obj[key] = value
        return obj

    def constant(name):
        if name == "NaN":
            return math.nan
        raise InputError(f"the Inspect log holds {name}, which no AEF number holds ([ENC-3]): refused {rule}")

    def number(literal):
        value = float(literal)
        if not math.isfinite(value):
            raise InputError(f"the Inspect log holds {literal}, which overflows binary64 ([ENC-3]): refused {rule}")
        return value

    def integer(literal):
        value = int(literal)
        if abs(value) <= 2 ** 53:
            return value
        return number(literal)  # read as binary64, as every JSON parser can ([ENC-4])

    try:
        log = json.loads(text, object_pairs_hook=members, parse_constant=constant, parse_float=number,
                         parse_int=integer)
    except ValueError as error:
        raise InputError(f"the Inspect log is not JSON: {error}: refused {rule}") from None
    stack = [((), log)]
    while stack:  # unpaired surrogates and NaN, anywhere
        where, value = stack.pop()
        if isinstance(value, str):
            if any("\ud800" <= c <= "\udfff" for c in value):
                raise InputError(f"the Inspect log: a string at {where} holds an unpaired surrogate ([ENC-2]): "
                                 f"refused {rule}")
        elif isinstance(value, float) and math.isnan(value) and not _nan_allowed(where):
            raise InputError(f"the Inspect log: NaN at {'/'.join(map(str, where))}, where no score, metric, limit "
                             f"or content is: refused {rule}")
        elif isinstance(value, dict):
            for key, item in value.items():
                stack.append((where + (key,), key))
                stack.append((where + (key,), item))
        elif isinstance(value, list):
            stack.extend((where + (i,), item) for i, item in enumerate(value))
    if not isinstance(log, dict) or not isinstance(log.get("eval"), dict):
        raise InputError("the Inspect log is not an EvalLog (an object with eval): refused (inspect.md, Inspect -> AEF, IN-6)")
    return log


def _whole(value, field, where, item, low=-SAFE_INTEGER, high=SAFE_INTEGER):
    """An integer field of the log (an id, a version, an epoch, epochs, a token count, a metric's k), read by its
    binary64 value alone ([ENC-4]): 1, 1.0 and 1e0 are the integer 1. A value that is no integer of at most 2^53 - 1
    in magnitude, or outside [low, high], is refused, naming the field's rule (item)."""
    if isinstance(value, float) and value.is_integer() and abs(value) <= SAFE_INTEGER:
        value = int(value)
    if isinstance(value, bool) or not isinstance(value, int) or abs(value) > SAFE_INTEGER or not low <= value <= high:
        bounds = "of at most 2^53 - 1 in magnitude" if (low, high) == (-SAFE_INTEGER, SAFE_INTEGER) else \
            f"from {low} to {high}" if high < SAFE_INTEGER else f"of at least {low}"
        _in(where, f"{field} {value!r} is not an integer {bounds}", item)
    return value


def _integral_text(value, field, where, item):
    """A sample's id or eval.task_version that is a number: an integer, by value (_whole), in decimal digits."""
    return str(_whole(value, field, where, item))


def _inspect_nanos(text, where):
    """An Inspect time (ISO 8601 with an offset) as nanoseconds since the Unix epoch, in UTC (the instant is kept)."""
    m = _INSPECT_TIME.fullmatch(text) if isinstance(text, str) else None
    if m is None:
        raise InputError(f"{where}: {text!r} is not a time with an offset (inspect.md, Inspect -> AEF, IN-6)")
    try:
        moment = datetime.datetime(*(int(g) for g in m.groups()[:6]), tzinfo=datetime.timezone.utc)
    except ValueError:
        raise InputError(f"{where}: {text!r} names a date that does not exist: refused (inspect.md, Inspect -> AEF, IN-6)") from None
    offset = 0 if m.group(8) == "Z" else (int(m.group(8)[1:3]) * 60 + int(m.group(8)[4:6])) * \
        (1 if m.group(8)[0] == "+" else -1)
    seconds = int(moment.timestamp()) - offset * 60
    return seconds * 10 ** 9 + int((m.group(7) or "").ljust(9, "0"))


def _ref(kind, name):
    """A typed reference whose name comes from free text, encoded as [ENC-13] says."""
    data = name.encode("utf-8")
    text = "".join(chr(b) if 0x21 <= b <= 0x7E and b != 0x25 else f"%{b:02X}" for b in data)
    text = "-" if text == "" else "%2D" if text == "-" else text
    if len(text) > 256:
        text = text[:239] + "~" + hashlib.sha256(data).hexdigest()[:16]
    return f"{kind}:{text}"


def _unref(name):
    """The free text a ref's name encodes ([ENC-13]): every %XX is the byte XX, `-` is the empty name. A name that
    does not decode to UTF-8 is kept as written."""
    if name == "-":
        return ""
    if "%" not in name:
        return name
    try:
        return re.sub(rb"%([0-9A-Fa-f]{2})", lambda m: bytes([int(m.group(1), 16)]),
                      name.encode("ascii")).decode("utf-8")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return name


def _finite(value):
    """A JSON number (not a boolean) whose binary64 value is finite ([ENC-3], [ENC-4])."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return False
    try:
        return math.isfinite(float(value))
    except OverflowError:  # an integer literal beyond binary64
        return False


def _shortest(value):
    """A finite number written from its binary64 value alone: the shortest decimal that reads back as the same
    binary64 value, the form [ENC-4] recommends, spelled as ECMAScript's Number::toString spells it. 1000 and 1000.0
    give 1000, 1e16 gives 10000000000000000, 1e21 gives 1e+21, 1e-7 gives 1e-7, 0.5 gives 0.5."""
    x = float(value)
    if x == 0:
        return "0"  # -0 too
    sign = "-" if x < 0 else ""
    digits_tuple = Decimal(repr(abs(x))).normalize().as_tuple()  # repr: the shortest round-trip digits
    digits = "".join(map(str, digits_tuple.digits))
    k = len(digits)
    n = digits_tuple.exponent + k  # x = 0.d1d2...dk x 10^n
    if k <= n <= 21:
        text = digits + "0" * (n - k)
    elif 0 < n <= 21:
        text = digits[:n] + "." + digits[n:]
    elif -6 < n <= 0:
        text = "0." + "0" * -n + digits
    else:
        e = n - 1
        text = (digits if k == 1 else digits[0] + "." + digits[1:]) + "e" + ("+" if e >= 0 else "-") + str(abs(e))
    return sign + text


def _in(where, what, item):
    raise InputError(f"{where}: {what}: refused (inspect.md, Inspect -> AEF, {item})")


def _from_reducer(reducer, epochs, where):
    """Inspect's epochs reducer as AEF's trial aggregation and k (inspect.md, `eval.config.epochs_reducer`), or None
    for a reducer without an AEF value."""
    if reducer in FROM_REDUCER:
        return FROM_REDUCER[reducer], None
    m = re.fullmatch(r"(pass_at|at_least)_([1-9][0-9]*)", reducer) if isinstance(reducer, str) else None
    if m and m.group(1) == "pass_at":
        return "PassAtK", int(m.group(2))
    if m and m.group(2) == "1":
        return "AnyPass", None
    if m and int(m.group(2)) == epochs:
        return "AllPass", None
    return None


def _aef_role(name, model_roles):
    """IN-8: an Inspect role name as an AEF usage role: agent, judge and attacker as they are, a role of
    eval.model_roles (a judge of judges[]) as judge, any other as other."""
    if name in ("agent", "judge", "attacker"):
        return name
    return "judge" if name in model_roles else "other"


def _usage_entry(role, model, usage, where, item):
    """A usage entry from an Inspect ModelUsage: tokens under OpenTelemetry's names, each an integer by value
    (_whole), total_cost as costUsd; Inspect's total_tokens is left out (it is input and output)."""
    if not isinstance(usage, dict):
        _in(where, "a usage that is not an object", item)
    entry = {"role": role}
    if model is not None:
        entry["model"] = model
    for aef, inspect in USAGE_FIELDS:
        value = usage.get(inspect)
        if value is None:
            continue
        entry[aef] = _whole(value, inspect, where, item, low=0)
    if usage.get("total_tokens") is not None:  # read and checked like every token count; AEF has no total to write
        _whole(usage["total_tokens"], "total_tokens", where, item, low=0)
    cost = usage.get("total_cost")
    if cost is not None:
        if isinstance(cost, bool) or not isinstance(cost, (int, float)) or not cost >= 0:
            _in(where, "a total_cost that is not a cost (a finite number of at least 0)", item)
        entry["costUsd"] = cost
    return entry


def _merged(entries):
    """Usage entries with one entry per role and model ([RES-10], [SUM-9]): two that meet are added together."""
    out = {}
    for entry in entries:
        key = (entry["role"], entry.get("model"))
        if key not in out:
            out[key] = dict(entry)
            continue
        kept = out[key]
        for field, value in entry.items():
            if field in ("role", "model"):
                continue
            if field == "costUsd":
                kept[field] = float(Fraction(kept.get(field, 0)) + Fraction(value))
            else:
                kept[field] = kept.get(field, 0) + value
    return list(out.values())


_JCS_ESCAPES = {'"': '\\"', "\\": "\\\\", "\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t"}


def _jcs(value, where):
    """RFC 8785, the JSON Canonicalization Scheme (IN-8, settled 10-10): no whitespace; numbers as ECMAScript writes
    them (_shortest); strings with only `"`, `\\` and the control characters escaped (\\b \\f \\n \\r \\t, else
    \\u00xx); object members sorted by the UTF-16 code units of their names. JCS has no NaN or infinity, and no
    unpaired surrogate: such content is refused (IN-8)."""
    if value is None or isinstance(value, bool):
        return {None: "null", True: "true", False: "false"}[value]
    if isinstance(value, (int, float)):
        if not _finite(value):
            _in(where, "content that is not text holds a number JCS cannot write (NaN)", "IN-8")
        return _shortest(value)
    if isinstance(value, str):
        try:
            value.encode("utf-8")
        except UnicodeEncodeError:
            _in(where, "content that is not text holds an unpaired surrogate, which JCS cannot write", "IN-8")
        return '"' + "".join(_JCS_ESCAPES.get(c) or (f"\\u{ord(c):04x}" if c < " " else c) for c in value) + '"'
    if isinstance(value, list):
        return "[" + ",".join(_jcs(item, where) for item in value) + "]"
    if isinstance(value, dict):
        members = sorted(value.items(), key=lambda kv: kv[0].encode("utf-16-be"))
        return "{" + ",".join(_jcs(k, where) + ":" + _jcs(v, where) for k, v in members) + "}"
    _in(where, "content that is not JSON", "IN-8")


def _text_bytes(value, where):
    """The bytes of a case's content (IN-8): a string as its UTF-8 text; anything else serialized by JCS (RFC 8785),
    so two converters give the same bytes, and the same blob name, for the same values."""
    return value.encode("utf-8") if isinstance(value, str) else _jcs(value, where).encode("utf-8")


class _Blobs:
    """The blobs and evidence records a converted run writes ([EVD-1]-[EVD-3]): one record per kind and content."""

    def __init__(self):
        self.blobs, self.records, self._ids = {}, [], {}

    def blob(self, data):
        digest = hashlib.sha256(data).hexdigest()
        self.blobs[digest] = data
        return digest

    def evidence(self, kind, data, description):
        digest = self.blob(data)
        if (kind, digest) not in self._ids:
            evidence_id = f"E-{len(self.records) + 1}"
            self._ids[(kind, digest)] = evidence_id
            self.records.append({"schemaVersion": AEF_VERSION, "evidenceId": evidence_id, "kind": kind,
                                 "digest": f"sha256:{digest}", "link": {"blob": f"sha256:{digest}"},
                                 "description": description})
        return self._ids[(kind, digest)]


def inspect_line(run_id, case_id, path, trial, score, evaluator_id, capture, blobs, where):
    """The result line of one Inspect Score (IN-7)."""
    if not isinstance(score, dict):
        _in(where, "a score that is not an object", "IN-7")
    if score.get("history"):
        _in(where, "a score with history edits: the reference converter writes no overlays", "IN-10")
    value, reason_name, explanation = score.get("value"), score.get("reason"), score.get("explanation")
    if reason_name is not None and (not isinstance(reason_name, str) or not reason_name):
        _in(where, "a Score.reason that is not a name", "IN-7")
    scores, extra = [], {}

    def measured(member, metric):
        if isinstance(member, bool):
            _in(where, f"the boolean value {member}: no row gives it a state or a number", "IN-7")
        if isinstance(member, (int, float)) and not (isinstance(member, float) and math.isnan(member)):
            return {"metric": metric, "value": member}
        if isinstance(member, str) and member in LETTERS:
            return {"metric": metric, "value": LETTERS[member], "label": member}
        _in(where, f"the value {member!r}, which is no number and no letter C, I, P or N", "IN-7")

    if isinstance(value, float) and math.isnan(value):  # unscored
        state = "error" if reason_name in INSTRUMENT_BLAMED else "failed" if reason_name in MODEL_BLAMED \
            else "not_measured"
    elif isinstance(value, dict):  # a map: one score per member, scored; a NaN member is unscored
        scores = [measured(v, k) for k, v in value.items() if not (isinstance(v, float) and math.isnan(v))]
        state = "scored"
    elif isinstance(value, list):  # a list: kept in ext, no score
        extra["value"], state = value, "scored"
    else:
        scores = [measured(value, path)]
        state = LETTER_STATES.get(value, "scored") if isinstance(value, str) else "scored"
    if reason_name in MODEL_BLAMED:  # the model under test is blamed: failed, the reason in reason
        state = "failed"
    if state in TYPED_ABSENCES:
        scores = []  # RES-2
    reason = reason_name
    if reason is None and state in TYPED_ABSENCES:
        reason = NO_VALUE_REASON
    line = {"schemaVersion": AEF_VERSION, "resultId": result_id(run_id, case_id, path, trial), "caseId": case_id,
            "path": path}
    if trial is not None:
        line["trial"] = trial
    line["evaluator"], line["state"] = {"id": evaluator_id}, state
    if explanation is not None and not isinstance(explanation, str):
        _in(where, "a Score.explanation that is not text", "IN-7")
    if reason is not None:
        line["reason"] = reason
    if scores:
        line["scores"] = scores
    # IN-7: the explanation is a reasoning blob when contentCapture is on. A run that keeps no content keeps no judge
    # reasoning and no response, in reason or ext either (RUN-11): with off, the explanation and the answer are left out.
    if explanation and capture == "on":
        data = explanation.encode("utf-8")
        line["reasoning"] = {"blob": "sha256:" + blobs.blob(data), "bytes": len(data)}
    for field in ("answer", "metadata") if capture == "on" else ("metadata",):
        if score.get(field) is not None:
            extra[field] = score[field]
    if extra:
        line["ext"] = {"inspect_ai": extra}
    return line


def _sample_content(sample, capture, blobs, where):
    """The evidence ids of a sample's input, target, output and messages, kept only with contentCapture on (IN-8)."""
    if capture != "on":
        return []
    ids = []
    output = sample.get("output")
    for field, kind, value in (("input", "input", sample.get("input")), ("target", "expected", sample.get("target")),
                               ("output", "output", output if isinstance(output, dict) and output.get("choices")
                                else None),
                               ("messages", "transcript", sample.get("messages"))):
        if value in (None, "", [], {}):
            continue
        ids.append(blobs.evidence(kind, _text_bytes(value, f"{where}.{field}"), f"Inspect samples[].{field}"))
    return list(dict.fromkeys(ids))


def _json_type(value):
    return "null" if value is None else "a boolean" if isinstance(value, bool) else \
        "a number" if isinstance(value, (int, float)) else "a string" if isinstance(value, str) else \
        "a list" if isinstance(value, list) else "an object"


def _need(value, kind, where, item):
    """IN-6 to IN-10, settled 10-10: a member the converter reads has the JSON type Inspect writes for it (kind:
    object, list or string); null counts as absent; any other type refuses the log, naming the member's rule."""
    if value is None:
        return
    if not {"object": isinstance(value, dict), "list": isinstance(value, list), "string": isinstance(value, str)}[kind]:
        article = "an" if kind == "object" else "a"
        _in(where, f"{_json_type(value)} where Inspect writes {article} {kind}", item)


def _check_types(log):
    """The types of every member the import reads (the table under "Member types" in inspect.md). Members kept as
    data (eval.run_id, eval.eval_set_id, eval.dataset, results.headline, Score.answer, Score.metadata) may hold any
    JSON value; members the converter does not read are not checked. Values (integers, times, costs) are checked
    where they are read."""
    _need(log.get("status"), "string", "status", "IN-6")
    spec = log["eval"]
    for key in ("eval_id", "run_id", "created", "task", "model"):
        if key != "run_id":
            _need(spec.get(key), "string", f"eval.{key}", "IN-6")
    for key in ("config", "packages", "model_roles"):
        _need(spec.get(key), "object", f"eval.{key}", "IN-6")
    for role, config in (spec.get("model_roles") or {}).items():
        _need(config, "object", f"eval.model_roles[{role!r}]", "IN-6")
        _need((config or {}).get("model"), "string", f"eval.model_roles[{role!r}].model", "IN-6")
    _need((spec.get("packages") or {}).get("inspect_ai"), "string", "eval.packages.inspect_ai", "IN-6")
    reducers = (spec.get("config") or {}).get("epochs_reducer")
    _need(reducers, "list", "eval.config.epochs_reducer", "IN-6")
    for i, reducer in enumerate(reducers or []):
        _need(reducer, "string", f"eval.config.epochs_reducer[{i}]", "IN-6")
    _need(spec.get("scorers"), "list", "eval.scorers", "IN-8")
    for i, scorer in enumerate(spec.get("scorers") or []):
        _need(scorer, "object", f"eval.scorers[{i}]", "IN-8")
        _need((scorer or {}).get("name"), "string", f"eval.scorers[{i}].name", "IN-8")
    _need(log.get("error"), "object", "error", "IN-6")
    stats = log.get("stats")
    _need(stats, "object", "stats", "IN-6")
    for key in ("model_usage", "role_usage"):
        _need((stats or {}).get(key), "object", f"stats.{key}", "IN-9")
    results = log.get("results")
    _need(results, "object", "results", "IN-9")
    _need((results or {}).get("scores"), "list", "results.scores", "IN-9")
    for i, entry in enumerate((results or {}).get("scores") or []):
        where = f"results.scores[{i}]"
        _need(entry, "object", where, "IN-9")
        _need(entry.get("name"), "string", f"{where}.name", "IN-9")
        _need(entry.get("scorer"), "string", f"{where}.scorer", "IN-9")
        _need(entry.get("metrics"), "object", f"{where}.metrics", "IN-9")
        for name, metric in (entry.get("metrics") or {}).items():
            _need(metric, "object", f"{where}.metrics[{name!r}]", "IN-9")  # the value and params: where read
    _need(log.get("samples"), "list", "samples", "IN-8")
    for n, sample in enumerate(log.get("samples") or []):
        where = f"samples[{n}]"
        _need(sample, "object", where, "IN-8")
        for key in ("scores", "role_usage", "model_usage"):
            _need(sample.get(key), "object", f"{where}.{key}", "IN-8")
    _need(log.get("reductions"), "list", "reductions", "IN-8")
    for i, reduction in enumerate(log.get("reductions") or []):
        where = f"reductions[{i}]"
        _need(reduction, "object", where, "IN-8")
        for key in ("scorer", "reducer"):
            _need(reduction.get(key), "string", f"{where}.{key}", "IN-8")
        _need(reduction.get("samples"), "list", f"{where}.samples", "IN-8")
        for j, reduced in enumerate(reduction.get("samples") or []):
            _need(reduced, "object", f"{where}.samples[{j}]", "IN-8")


def from_inspect(log_path, out, target_mode, capture, at):
    """Writes the run folder OUT from an Inspect eval log (inspect.md, Inspect -> AEF, and IN-6 to IN-10)."""
    out = Path(out)
    if out.exists() and not (out.is_dir() and not any(out.iterdir())):
        raise InputError(f"{out}: OUT is a folder that does not exist yet, or an empty one")
    if capture not in ("on", "off"):
        raise InputError("--content-capture is on or off ([RUN-11])")
    log = _read_inspect_log(log_path)
    _check_types(log)  # IN-6 to IN-10, settled 10-10: every member read has Inspect's type
    spec, stats, results = log["eval"], log.get("stats") or {}, log.get("results") or {}
    if log.get("log_updates"):
        _in("log_updates", "post-run edits: the reference converter writes no overlays", "IN-10")

    # IN-6: the run header
    run_id = spec.get("eval_id") or spec.get("run_id")
    if not isinstance(run_id, str) or not _AEF_ID.fullmatch(run_id):
        _in("eval.eval_id", f"{run_id!r} is not an AEF id (1 to 128 letters, digits, '.', '_', ':', '-')", "IN-6")
    status = {"success": "completed", "error": "aborted", "cancelled": "aborted", "started": "running"}.get(
        log.get("status"))
    if status is None:
        _in("status", f"{log.get('status')!r}, not a status Inspect writes", "IN-6")
    closed = status != "running"
    run = {"schemaVersion": AEF_VERSION, "runId": run_id, "status": status}
    if log.get("status") == "error":
        message = (log.get("error") or {}).get("message")
        if not isinstance(message, str) or not message:
            _in("error.message", "an error log without a message: an aborted run has an abortReason", "IN-6")
        run["abortReason"] = message[:2048]
    elif log.get("status") == "cancelled":
        run["abortReason"] = "cancelled"
    run["producer"] = dict(PRODUCER)
    model, task = spec.get("model"), spec.get("task")
    if not isinstance(model, str) or not isinstance(task, str):
        _in("eval", "eval.model and eval.task that are not both strings", "IN-6")
    run["subject"] = {"ref": _ref("model", model), "kind": "model"}
    run["execution"] = {"targetMode": target_mode}
    version = spec.get("task_version", 0)
    if not isinstance(version, str):
        version = _integral_text(version, "task_version", "eval.task_version", "IN-6")
    if not re.fullmatch("[!-~]{1,128}", version) or version.lower() == "latest":
        _in("eval.task_version", f"{version!r} is not an exact version ([ENC-10])", "IN-6")
    suite = {"ref": _ref("suite", task), "version": version}
    config = spec.get("config") or {}
    epochs = 1 if config.get("epochs") is None else \
        _whole(config["epochs"], "epochs", "eval.config", "IN-6", low=1, high=1000)  # by value: 1.0 is 1
    reducers = config.get("epochs_reducer") or []
    if len(reducers) > 1:
        _in("eval.config.epochs_reducer", f"{len(reducers)} reducers: a trial aggregation is one", "IN-8")
    mapped = _from_reducer(reducers[0], epochs, "eval.config.epochs_reducer") if reducers else None
    if config.get("epochs") is not None:  # null counts as absent
        policy = {"trialsPerCase": epochs}
        if mapped:
            policy["aggregation"] = mapped[0]
            if mapped[1] is not None:
                policy["k"] = mapped[1]
        suite["executionPolicy"] = policy
    run["suite"] = suite
    model_roles = spec.get("model_roles") or {}
    judged = [c.get("model") for r, c in model_roles.items()
              if isinstance(c, dict) and _aef_role(r, model_roles) == "judge"]
    if judged:  # eval.model_roles -> judges: a model that grades, each once
        run["judges"] = [{"model": m} for m in dict.fromkeys(judged)]
    asserted = ["subject.ref", "subject.kind", "execution.targetMode", "contentCapture"]
    if stats.get("started_at"):
        started = _inspect_nanos(stats["started_at"], "stats.started_at")
    else:
        started = _inspect_nanos(spec.get("created"), "eval.created")  # the creation time stands for the start
        asserted.append("startedAt")
    run["startedAt"] = _timestamp(started, "the start", "inspect.md, Inspect -> AEF, IN-6")
    ended = None
    if closed:
        if not stats.get("completed_at"):
            _in("stats.completed_at", "a closed log without its end time: a closed run has endedAt", "IN-6")
        ended = _inspect_nanos(stats["completed_at"], "stats.completed_at")
        if ended < started:
            _in("stats.completed_at", "an end before the start ([RUN-5])", "IN-6")
        run["endedAt"] = _timestamp(ended, "the end", "inspect.md, Inspect -> AEF, IN-6")
    run["contentCapture"] = capture
    packages = spec.get("packages") or {}
    source = f"inspect_ai {packages['inspect_ai']}" if isinstance(packages.get("inspect_ai"), str) else "inspect_ai"
    run["imported"] = {"from": source, "asserted": asserted}
    ext = {k: spec[k] for k in ("run_id", "eval_set_id", "dataset") if spec.get(k) is not None}
    if reducers and not mapped:
        ext["epochs_reducer"] = reducers  # a reducer without an AEF value, kept as data
    if results.get("headline") is not None:
        ext["headline"] = results["headline"]
    if ext:
        run["ext"] = {"inspect_ai": ext}

    # IN-7, IN-8: the samples' lines, then the rollups
    scorer_of = {s.get("name"): s.get("scorer") for s in results.get("scores") or [] if isinstance(s, dict)}
    blobs, lines, seen, trial_lines = _Blobs(), [], set(), {}
    for n, sample in enumerate(log.get("samples") or []):
        where = f"samples[{n}]"
        if not isinstance(sample, dict) or not isinstance(sample.get("id"), (int, float, str)):
            _in(where, "a sample whose id is no string and no number", "IN-8")
        sample_id = sample["id"]
        case_id = _text(sample_id if isinstance(sample_id, str) else
                        _integral_text(sample_id, "id", where, "IN-8"), "caseId", 256, where, "inspect.md, Inspect -> AEF, IN-8")
        epoch = sample.get("epoch", 1)
        epoch = 1 if epoch is None else _whole(epoch, "epoch", where, "IN-8", low=1, high=epochs)  # by value
        trial = epoch - 1 if epochs > 1 else None  # trial = epoch - 1, when there is more than one epoch
        if sample.get("invalidation"):
            _in(where, "an invalidated sample: the reference converter writes no overlays", "IN-10")
        scores = sample.get("scores") or {}
        failure, limit = sample.get("error"), sample.get("limit")
        if failure is not None and not isinstance(failure, dict):  # Inspect writes an error as an object
            _in(where, f"an error that is not an object ({type(failure).__name__})", "IN-8")
        if limit is not None and not (isinstance(limit, dict) and isinstance(limit.get("type"), str) and limit["type"]
                                      and _finite(limit.get("limit"))):
            _in(where, "a limit that is not {type, limit} with a type and a finite number", "IN-8")
        stopped = failure is not None or limit is not None  # an error {} is an error
        if not scores and stopped:  # stopped before it was scored: a line per scorer the log names
            scores = {s.get("name"): {"value": math.nan} for s in spec.get("scorers") or []
                      if isinstance(s, dict) and isinstance(s.get("name"), str)}
        if not isinstance(scores, dict) or not scores:
            _in(where, "a sample without scores (and, for one that stopped, a log naming no scorer): its case would "
                       "have no line", "IN-8")
        evidence = _sample_content(sample, capture, blobs, where)
        sample_lines = []
        for path, score in scores.items():
            _text(path, "path", 1024, f"{where}.scores", "inspect.md, Inspect -> AEF, IN-8")
            evaluator = scorer_of.get(path) if isinstance(scorer_of.get(path), str) else path
            line = inspect_line(run_id, case_id, path, trial, score, evaluator, capture, blobs,
                                f"{where}.scores[{path!r}]")
            key = (case_id, path, trial)
            if key in seen:
                _in(where, f"a second score of case {case_id!r} at {path!r}, epoch {epoch}", "IN-8")
            seen.add(key)
            sample_lines.append(line)
        if stopped:  # samples[].error, limit -> error (not_measured for a limit), the message in reason
            if failure is not None:
                message = failure.get("message")
                if message is not None and not isinstance(message, str):
                    _in(where, "an error whose message is not text", "IN-8")
                if not message:
                    message = ERROR_REASON  # an error without a message, {} included
            else:
                message = f"{limit['type']} limit {_shortest(limit['limit'])}"  # "token limit 1000"
            for line in sample_lines:
                line["state"] = "error" if failure is not None else "not_measured"
                line["reason"] = str(message)[:REASON_MAX] or NO_VALUE_REASON
                line.pop("scores", None)
        for field, source_field in (("startedAt", "started_at"), ("endedAt", "completed_at")):
            if sample.get(source_field):  # on the case's lines
                stamp = _timestamp(_inspect_nanos(sample[source_field], f"{where}.{source_field}"), where,
                                   "inspect.md, Inspect -> AEF, IN-6")
                for line in sample_lines:
                    line[field] = stamp
        first = sample_lines[0]
        if sample.get("total_time") is not None:  # durationMs, on the sample's first line
            seconds = sample["total_time"]
            if isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or not seconds >= 0:
                _in(where, "a total_time that is not a duration (a finite number of at least 0)", "IN-8")
            ms = float(Fraction(seconds) * 1000)  # rounded once
            first["durationMs"] = int(ms) if ms.is_integer() else ms
        model_usage = sample.get("model_usage") or {}
        entries = []
        for role, usage in (sample.get("role_usage") or {}).items():  # one entry per role
            aef_role = _aef_role(role, model_roles)
            role_model = (model_roles.get(role) or {}).get("model") if role in model_roles else \
                model if aef_role == "agent" else None
            entries.append(_usage_entry(aef_role, role_model if role_model in model_usage else None, usage,
                                        f"{where}.role_usage[{role!r}]", "IN-8"))
        if model in model_usage and not any(e["role"] == "agent" for e in entries):
            entries.append(_usage_entry("agent", model, model_usage[model], f"{where}.model_usage", "IN-8"))
        if entries:
            first["usage"] = _merged(entries)
        for line in sample_lines:
            if evidence:
                line["evidence"] = evidence
            lines.append(line)
            if trial is not None:
                trial_lines.setdefault((case_id, line["path"]), []).append(line)
    if epochs > 1:
        rolled = set()
        for i, reduction in enumerate(log.get("reductions") or []):
            where = f"reductions[{i}]"
            scorer = reduction.get("scorer") if isinstance(reduction, dict) else None
            mapped = _from_reducer(reduction.get("reducer"), epochs, where) if isinstance(scorer, str) else None
            if not mapped:
                _in(where, f"the reducer {reduction.get('reducer') if isinstance(reduction, dict) else None!r}, "
                           "which has no AEF trial aggregation", "IN-8")
            for j, reduced in enumerate(reduction.get("samples") or []):
                sample_id = reduced.get("sample_id") if isinstance(reduced, dict) else None
                if not isinstance(sample_id, (int, float, str)):
                    _in(f"{where}.samples[{j}]", "a reduction without a sample_id", "IN-8")
                case_id = sample_id if isinstance(sample_id, str) else \
                    _integral_text(sample_id, "sample_id", f"{where}.samples[{j}]", "IN-8")
                trials = trial_lines.get((case_id, scorer))
                if not trials or (case_id, scorer) in rolled:
                    _in(f"{where}.samples[{j}]", f"a reduction of case {case_id!r} at {scorer!r} with no epochs, "
                                                 "or a second one", "IN-8")
                rolled.add((case_id, scorer))
                line = inspect_line(run_id, case_id, scorer, None, reduced, trials[0]["evaluator"]["id"], capture,
                                    blobs, f"{where}.samples[{j}]")
                states = [t["state"] for t in trials]
                rollup = {"n": len(trials), "passed": states.count("passed"), "aggregation": mapped[0],
                          "agree": len(set(states)) == 1}
                if mapped[1] is not None:
                    rollup["k"] = mapped[1]
                line = {**{k: v for k, v in line.items() if k in ("schemaVersion", "resultId", "caseId", "path")},
                        "trials": rollup, **{k: v for k, v in line.items()
                                             if k not in ("schemaVersion", "resultId", "caseId", "path")}}
                lines.append(line)
        if closed:
            missing = sorted(set(trial_lines) - rolled)
            if missing:
                _in("reductions", f"no reduction for case {missing[0][0]!r} at {missing[0][1]!r}: every case's path "
                                  "has a rollup in a closed run ([RES-8])", "IN-8")

    # IN-9: metrics.json and summary.json
    request, inspect_values, summary_ext = [], {}, {}
    for i, es in enumerate(results.get("scores") or [] if closed else []):
        where = f"results.scores[{i}]"
        name, measures = es.get("name"), es.get("metrics") or {}
        if not isinstance(name, str) or not isinstance(measures, dict):
            _in(where, "an EvalScore without a name or metrics", "IN-9")
        means = [m for m in measures if m in MEAN_METRICS]
        others = [m for m in measures if m not in MEAN_METRICS and m != "stderr"]
        if len(means) > 1:
            _in(where, "both accuracy and mean: one summary entry per lane, metric and path ([SUM-9])", "IN-9")
        entry = {"metric": name, "path": name}
        if means:
            chosen = means[0]
            if others:
                summary_ext[name] = {m: (measures[m] or {}).get("value") for m in others}
        elif len(others) == 1:
            chosen = others[0]
            if not _METHOD.fullmatch(chosen):
                _in(where, f"the metric {chosen!r} cannot be an aggregate method", "IN-9")
            aggregate = {"method": chosen}
            _need((measures[chosen] or {}).get("params"), "object", f"{where}.metrics[{chosen!r}].params", "IN-9")
            k = ((measures[chosen] or {}).get("params") or {}).get("k")
            if k is not None:  # an integer by value, like every integer field
                aggregate["k"] = _whole(k, "params.k", where, "IN-9", low=1)
            entry["aggregate"] = aggregate
            given = (measures[chosen] or {}).get("value")
            if chosen not in aef_produce.DEFINED_AGGREGATES and isinstance(given, (int, float)) and \
                    not isinstance(given, bool) and math.isfinite(given):
                entry["value"] = given  # the producer's figure (SUM-8)
        elif others:
            _in(where, "no mean and more than one other metric", "IN-9")
        else:
            chosen = None  # no metric: a plain mean entry, with nothing of Inspect's to compare
        for read in ([chosen] if chosen else []) + (["stderr"] if "stderr" in measures else []):
            value = (measures[read] or {}).get("value")  # read: compared with the lines, or written as stderr
            if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float))):
                _in(f"{where}.metrics[{read!r}].value", f"{_json_type(value)} where Inspect writes a number", "IN-9")
        inspect_values[name] = ((measures[chosen] or {}).get("value") if chosen else None,
                                (measures.get("stderr") or {}).get("value"), chosen is not None)
        request.append(entry)
    names = []
    for line in lines:
        names += [s["metric"] for s in line.get("scores", [])]
    names += [e["metric"] for e in request]
    metrics = {"schemaVersion": AEF_VERSION, "metrics": [{"id": m, "kind": "score", "direction": "none",
                                                          "scale": "unbounded"} for m in dict.fromkeys(names)]}
    summary = None
    if closed:
        try:
            summary = aef_produce.summary_of(run, metrics, lines, {"lanes": [{"lane": "main", "metrics": request}]})
        except InputError as error:
            raise InputError(f"{error}: refused (inspect.md, Inspect -> AEF, IN-9)") from None
        for entry in summary["lanes"][0]["metrics"]:
            given, stderr, compared = inspect_values[entry["path"]]
            given = None if isinstance(given, float) and math.isnan(given) else given
            mine = entry["value"]
            if compared and ((given is None) != (mine is None) or (mine is not None and (  # within §3.6
                    not isinstance(given, (int, float)) or abs(mine - given) > 1e-9 * max(1, abs(mine))))):
                _in(f"results.scores {entry['path']!r}", f"Inspect gives {given!r}, the lines give {mine!r}: the "
                                                         "summary is recomputed from the lines ([SUM-5])", "IN-9")
            if isinstance(stderr, (int, float)) and not isinstance(stderr, bool) and math.isfinite(stderr) \
                    and stderr >= 0:
                entry["stderr"] = stderr
        if not summary["lanes"][0]["metrics"]:
            summary["lanes"] = []
        usage = []
        for used, totals in (stats.get("model_usage") or {}).items():  # one entry per role and model
            role = next((_aef_role(r, model_roles) for r, c in model_roles.items() if c.get("model") == used),
                        "agent" if used == model else "other")
            usage.append(_usage_entry(role, used, totals, f"stats.model_usage[{used!r}]", "IN-9"))
        if not stats.get("model_usage"):  # without model_usage, one entry per role_usage role, no model
            for role, totals in (stats.get("role_usage") or {}).items():
                usage.append(_usage_entry(_aef_role(role, model_roles), None, totals, f"stats.role_usage[{role!r}]",
                                          "IN-9"))
        if usage:
            summary["usage"] = _merged(usage)
        costs = [u["costUsd"] for u in usage if "costUsd" in u]
        if costs:
            summary["cost"] = {"totalUsd": float(sum((Fraction(c) for c in costs), Fraction(0)))}
        if summary_ext:
            summary["ext"] = {"inspect_ai": {"metrics": summary_ext}}

    _check_schema("run", run, "run.json", "inspect.md, Inspect -> AEF, IN-11")
    _check_schema("metrics", metrics, "metrics.json", "inspect.md, Inspect -> AEF, IN-11")
    if summary is not None:
        _check_schema("summary", summary, "summary.json", "inspect.md, Inspect -> AEF, IN-11")
    for n, line in enumerate(lines, start=1):
        _check_schema("result", line, f"results.ndjson:{n}", "inspect.md, Inspect -> AEF, IN-11")
    for n, record in enumerate(blobs.records, start=1):
        _check_schema("evidence", record, f"evidence.ndjson:{n}", "inspect.md, Inspect -> AEF, IN-11")
    sealed_at = _nanos(at, "--at")
    if closed and sealed_at < ended:
        raise InputError("--at is before the run's end: the run is sealed after it closes ([SEAL-1])")

    created = not out.exists()
    out.mkdir(parents=True, exist_ok=True)
    try:
        _write_text(out / "run.json", json.dumps(run, indent=2, ensure_ascii=False) + "\n")
        _write_text(out / "results.ndjson", "".join(_compact(line) + "\n" for line in lines))
        _write_text(out / "metrics.json", json.dumps(metrics, indent=2, ensure_ascii=False) + "\n")
        if summary is not None:
            _write_text(out / "summary.json", json.dumps(summary, indent=2, ensure_ascii=False) + "\n")
        if blobs.records:
            _write_text(out / "evidence.ndjson", "".join(_compact(r) + "\n" for r in blobs.records))
        for digest, data in blobs.blobs.items():
            (out / "blobs" / "sha256" / digest[:2]).mkdir(parents=True, exist_ok=True)
            (out / "blobs" / "sha256" / digest[:2] / digest).write_bytes(data)
        sealed = aef_produce.seal_write(out, "ingest", _timestamp(sealed_at, "--at"))["runHash"] if closed else None
        _verified(out, "the converted run", "inspect.md, Inspect -> AEF, IN-11")
    except BaseException:
        if created:
            shutil.rmtree(out, ignore_errors=True)
        else:
            for child in list(out.iterdir()):
                shutil.rmtree(child) if child.is_dir() else child.unlink()
        raise
    return {"results": len(lines), "runHash": sealed}


# ---------------------------------------------------------------------------- command line

def dispatch(argv):
    """Performs one command (argv without the program name) and returns its JSON value. InputError or SystemExit
    on a usage or input error."""
    parser = argparse.ArgumentParser(prog="aef_interop.py", description=__doc__.split("\n\n")[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("to-otel")
    p.add_argument("run")
    p.add_argument("out")
    p = sub.add_parser("from-otel")
    p.add_argument("logs")
    p.add_argument("out")
    p.add_argument("--run-id", required=True, help="the new run's id: the events carry none")
    p.add_argument("--from", dest="source", required=True, help="imported.from: the tool that wrote the events")
    p.add_argument("--subject", required=True, help="subject.ref, which the events do not give")
    p.add_argument("--subject-kind", required=True)
    p.add_argument("--target-mode", required=True, help="execution.targetMode, which the events do not give")
    p.add_argument("--content-capture", default="on", help="contentCapture: on (default) or off (OT-4)")
    p.add_argument("--at", help="the time of the conversion (RFC 3339 UTC; default now): sealedAt, and the run's "
                                "times when no event has one")
    p = sub.add_parser("to-inspect")
    p.add_argument("run")
    p.add_argument("out")
    p.add_argument("--ignore-overlays", action="store_true", help="leave the overlays out (IN-4)")
    p = sub.add_parser("from-inspect")
    p.add_argument("log")
    p.add_argument("out")
    p.add_argument("--target-mode", required=True, help="execution.targetMode, which the log does not give")
    p.add_argument("--content-capture", default="on", help="contentCapture: on (default) or off (IN-6)")
    p.add_argument("--at", help="the time of the conversion (RFC 3339 UTC; default now): sealedAt")
    a = parser.parse_args(argv)
    at = getattr(a, "at", None) or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    if a.command == "to-otel":
        return to_otel(a.run, a.out)
    if a.command == "from-otel":
        return from_otel(a.logs, a.out, a.run_id, a.source, a.subject, a.subject_kind, a.target_mode, at,
                         a.content_capture)
    if a.command == "from-inspect":
        return from_inspect(a.log, a.out, a.target_mode, a.content_capture, at)
    return to_inspect(a.run, a.out, a.ignore_overlays)


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    try:
        result = dispatch(argv[1:])
    except (InputError, OSError) as error:  # OSError: OUT cannot be written (a folder where a file goes, ...)
        print(f"aef_interop.py: {error}", file=sys.stderr)
        return 2
    sys.stdout.write(json.dumps(result, ensure_ascii=False, sort_keys=True) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
