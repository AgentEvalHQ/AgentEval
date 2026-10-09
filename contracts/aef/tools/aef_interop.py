#!/usr/bin/env python3
"""AEF 1.0 interop: reference converters for two of the informative mappings, written from the pages alone
(1/interop/opentelemetry.md and 1/interop/inspect.md). Standard library, plus aef_produce.py (reading, result ids,
the ingest seal) and aef_schema.py (the writer schemas a converted run is checked against before it is written).

The pages are informative, and so is this tool: nothing in the specification depends on it. Beyond their tables, the
pages state rules and refusals of their own (OT-1 to OT-6 and IN-1 to IN-5, settled 10-09); the converter follows
them, and a refusal exits with status 2 naming its rule. It never fills a gap the pages leave. The checked examples under
1/interop/examples/ run it (tools/check_interop.py).

The command line follows aef_produce.py's contract: input paths as arguments, one JSON value on standard output
(UTF-8, sorted keys, no ASCII escaping), exit status 0 when the conversion ran, and 2 with a message on standard
error for a usage or input error, with nothing written.

Commands (the JSON each prints):

  to-otel RUN OUT
      opentelemetry.md, "AEF -> OpenTelemetry": writes OUT, OTLP/JSON logs as OpenTelemetry's file exporter writes
      them (one LogsData object per line): one `gen_ai.evaluation.result` event per score of each result line of the
      run folder RUN, and one without a score value for a line without scores; one LogsData line per result line
      (OT-2). Prints {"events": n, "lines": m}. Refused: a line without scores whose name the summary does not give
      (OT-1).
  from-otel LOGS OUT --run-id ID --from SOURCE --subject REF --subject-kind KIND --target-mode MODE [--at TIME]
      opentelemetry.md, "OpenTelemetry -> AEF": writes the run folder OUT (which must not exist yet, or be empty)
      from the OTLP/JSON logs file LOGS: one result line per `gen_ai.evaluation.result` event, the file itself as
      logs.otlp.jsonl, metrics.json with a declaration per scored metric, run.json with `imported` (OT-4), a
      summary.json without lanes, and the seal (`sealedBy: ingest`, README "A converted run is a new run"), sealed at
      TIME (the conversion time; default now). Prints {"results": n, "runHash": hex}. Refused: the events of OT-6.
  to-inspect RUN OUT [--ignore-overlays]
      inspect.md, "AEF -> Inspect": writes OUT, the run as one Inspect EvalLog in its .json form (log format 2).
      An unscored value is the bare token NaN, as Inspect writes it (not JSON). Refused: the cases of IN-1, IN-3,
      IN-4 and IN-5; a run with overlay events unless --ignore-overlays leaves them out (IN-4).
"""
from __future__ import annotations

import argparse
import datetime
import json
import math
import re
import shutil
import sys
from fractions import Fraction
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_produce  # noqa: E402
import aef_schema  # noqa: E402
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


def _check_schema(schema, document, where):
    """A converted run is a new run (interop/README.md): its documents are valid against the writer schemas."""
    errors = _writer_schemas().validate(schema, document)
    if errors:
        raise InputError(f"{where} would not be valid against the writer {schema} schema: {errors[0]}")


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


def _timestamp(nanos, where):
    """Nanoseconds since the Unix epoch as an RFC 3339 UTC time ([ENC-8]): the fraction with no trailing zero, and
    none when it is zero (OTLP's 1790949735000000000 is 2026-10-02T14:02:15Z)."""
    seconds, fraction = divmod(nanos, 10 ** 9)
    try:
        moment = datetime.datetime.fromtimestamp(seconds, tz=datetime.timezone.utc)
    except (OverflowError, OSError, ValueError):
        raise InputError(f"{where}: timeUnixNano {nanos} is not a time AEF can write ([ENC-8])") from None
    if not 1 <= moment.year <= 9999:
        raise InputError(f"{where}: timeUnixNano {nanos} is outside the years 0001 to 9999 ([ENC-8])")
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


def _blob_text(run_dir, digest, where):
    """The text of the blob a `sha256:<hex>` reference names ([EVD-3]), or None when the run does not hold it (a
    redaction can withhold it, [OVL-10])."""
    hexname = digest.split(":", 1)[1] if isinstance(digest, str) and digest.startswith("sha256:") else ""
    path = Path(run_dir) / "blobs" / "sha256" / hexname[:2] / hexname
    if not re.fullmatch("[0-9a-f]{64}", hexname) or not path.is_file():
        return None
    try:
        return path.read_bytes().decode("utf-8")
    except UnicodeDecodeError:
        raise InputError(f"{where}: the blob {digest} is not UTF-8 text") from None


def _explanation(run_dir, run, line, where):
    """`reason`, or the `reasoning` blob's text when the line has no reason and the run keeps content."""
    if isinstance(line.get("reason"), str):
        return line["reason"]
    if "reasoning" in line and run.get("contentCapture") != "off":  # RUN-11: a reader takes an absent value as on
        return _blob_text(run_dir, line["reasoning"].get("blob"), where)
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
        record["timeUnixNano"] = str(_nanos(time, f"{where} time"))
    record["eventName"] = EVENT_NAME
    link = line.get("traceLink")
    if isinstance(link, dict):  # the evaluated operation: the event's parent (a traceId alone names its trace)
        record["traceId"] = link["traceId"]
        if "spanId" in link:
            record["spanId"] = link["spanId"]
    # OT-3: SEC-6 forbids the explanation in the logs of a run that keeps no content; none is written for such a run.
    explanation = None if run.get("contentCapture") == "off" else _explanation(run_dir, run, line, where)
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
    """Writes OUT: one OTLP/JSON LogsData line per result line of the run (OT-2). Returns the counts."""
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


def _text(value, field, maximum, where):
    """caseId, a path, an evaluator id, a metric: 1 to maximum characters, no control character ([RES-4])."""
    if not 1 <= len(value) <= maximum or re.search("[\x00-\x1f\x7f-\x9f]", value):
        raise InputError(f"{where}: {value!r} cannot be a {field} (1 to {maximum} characters, no control character)")
    return value


def _refuse(where, what, item="OT-6"):
    raise InputError(f"{where}: {what}: refused (opentelemetry.md, OpenTelemetry -> AEF, {item})")


def imported_line(run_id, record, where):
    """The result line of one `gen_ai.evaluation.result` event, and its time in nanoseconds (or None)."""
    a = _attributes(record, where)
    name = _string(a, "gen_ai.evaluation.name", where)
    if name is None:
        raise InputError(f"{where}: an evaluation event without gen_ai.evaluation.name (the convention requires it)")
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
            raise InputError(f"{where}: the label is longer than {LABEL_MAX} characters, the most scores[].label holds")
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


def from_otel(logs_path, out, run_id, source, subject_ref, subject_kind, target_mode, at):
    """Writes the run folder OUT from the OTLP/JSON logs file (OT-4, OT-5, OT-6). Returns the counts."""
    out = Path(out)
    if out.exists() and not (out.is_dir() and not any(out.iterdir())):
        raise InputError(f"{out}: OUT is a folder that does not exist yet, or an empty one")
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
    run["imported"] = {"from": source, "asserted": ["runId", "status", "subject.ref", "subject.kind",
                                                    "execution.targetMode", "startedAt", "endedAt"]}
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
    except BaseException:
        if created:
            shutil.rmtree(out, ignore_errors=True)
        else:
            for child in out.iterdir():
                child.unlink()
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
    explanation = _explanation(run_dir, run, line, where)
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
            text = _blob_text(run_dir, blob, where) if blob else None
            if text is None:
                _in_refusal(where, f"{kind} evidence {evidence_id} that is not a blob of the run", "IN-5")
            if found.get(kind, text) != text:
                _in_refusal(where, f"two {kind} evidence records with different text", "IN-5")
            found[kind] = text
    return found.get("input", ""), found.get("expected", "")


def _root_fact(roots, field, where):
    """A fact of "a case's root line" (startedAt, endedAt, durationMs): refused when two roots carry it (IN-5)."""
    carried = [root[field] for root in roots if field in root]
    if len(carried) > 1:
        _in_refusal(where, f"{len(carried)} root lines carry {field}, and the table takes it from the case's root "
                           "line", "IN-5")
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
    """Writes OUT: the run as one Inspect EvalLog in .json form. Returns the counts."""
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
    model = ref[len("model:"):] if subject.get("kind") == "model" and ref.startswith("model:") else ref  # IN-1
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
        log["error"] = {"message": run.get("abortReason", ""), "traceback": ""}
    log["samples"] = samples
    if rollups:
        log["reductions"] = [
            {"scorer": path, "reducer": reducer,
             "samples": [dict(inspect_score(run_dir, run, line, f"rollup {line['resultId']}"), sample_id=line["caseId"])
                         for line in group]}
            for (path, reducer), group in rollups.items()]
    _write_text(out, json.dumps(log, indent=2, ensure_ascii=False) + "\n")  # NaN as Inspect writes it
    return {"samples": len(samples), "reductions": len(rollups)}


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
    p.add_argument("--at", help="the time of the conversion (RFC 3339 UTC; default now): sealedAt, and the run's "
                                "times when no event has one")
    p = sub.add_parser("to-inspect")
    p.add_argument("run")
    p.add_argument("out")
    p.add_argument("--ignore-overlays", action="store_true", help="leave the overlays out (IN-4)")
    a = parser.parse_args(argv)
    if a.command == "to-otel":
        return to_otel(a.run, a.out)
    if a.command == "from-otel":
        at = a.at or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        return from_otel(a.logs, a.out, a.run_id, a.source, a.subject, a.subject_kind, a.target_mode, at)
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
