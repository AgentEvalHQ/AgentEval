#!/usr/bin/env python3
"""Builds the AEF 1.0 conformance corpus (contracts/aef/1/conformance/) from the definitions below.

What this script computes is limited to the rules it writes: result ids (RES-4), seals (SEAL-1..5) and overlay batch
seals (OVL-4). Every other expected result (problems, effective views, lane results) is written here by hand from the
specification (contracts/aef/1/spec/), never computed by an implementation. Two independent implementations check all
of it: tools/aef_verify.py and the .NET tests. Re-running the script rewrites the corpus byte for byte.

Usage: python contracts/aef/tools/build_conformance.py   (then lane_vectors.py, decision_vectors.py,
protocol_vectors.py, signature_vectors.py, and build_index.py last)
"""
import base64
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "1" / "conformance"
V = "1.0"


# ---------------------------------------------------------------------------- the rules this script implements

def result_id(run_id, case_id, path, trial=None):
    """RES-4: r_ + the first 32 hex of SHA-256 over runId, caseId, path and trial joined by U+001F (no trial: empty)."""
    digits = "" if trial is None else str(int(trial))  # plain integer digits: 3.0 is "3"
    assert trial is None or float(trial).is_integer()
    text = "\u001f".join([run_id, case_id, path, digits])
    return "r_" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:32]


def sealed_files(run_dir):
    """SEAL-1: every file of the run except seal.json, attestation.dsse.json and overlays/."""
    files = []
    for f in sorted(run_dir.rglob("*")):
        if not f.is_file():
            continue
        rel = f.relative_to(run_dir).as_posix()
        if rel in ("seal.json", "attestation.dsse.json") or rel.startswith("overlays/"):
            continue
        files.append(rel)
    return sorted(files, key=lambda p: p.encode("utf-8"))


def manifest(run_dir):
    """SEAL-3: one line per file, sorted by the UTF-8 bytes of the path: '<sha256-hex>  <bytes>  <path>\\n'."""
    lines = []
    for rel in sealed_files(run_dir):
        data = (run_dir / rel).read_bytes()
        lines.append(f"{hashlib.sha256(data).hexdigest()}  {len(data)}  {rel}\n")
    return "".join(lines)


def run_hash(run_dir):
    return hashlib.sha256(manifest(run_dir).encode("utf-8")).hexdigest()


def seal(run_dir, run, sealed_by, closed_at=None, allow_open=False, sealed_at="2026-10-08T09:00:00Z"):
    """SEAL-5. Returns the run hash."""
    assert allow_open or run["status"] != "running", "only a closed run is sealed"
    statement = {
        "_type": "https://in-toto.io/Statement/v1",
        "subject": [
            {"name": rel, "digest": {"sha256": hashlib.sha256((run_dir / rel).read_bytes()).hexdigest()}}
            for rel in sealed_files(run_dir)
        ],
        "predicateType": "https://agenteval.dev/aef/1/evidence",
        "predicate": {
            "schemaVersion": V,
            "runId": run["runId"],
            "runHash": run_hash(run_dir),
            "producer": {"name": run["producer"]["name"], "version": run["producer"]["version"]},
            "subject": {k: run["subject"][k] for k in ("ref", "version") if k in run["subject"]},
            "deployment": {"ref": run["deployment"]["ref"]} if "deployment" in run else None,
            "suite": {k: run["suite"][k] for k in ("ref", "version", "digest") if k in run["suite"]} if "suite" in run else None,
            "judges": [{k: j[k] for k in ("model", "rubricDigest") if k in j} for j in run.get("judges", [])],
            "closedAt": closed_at or run["endedAt"],
            "sealedAt": sealed_at,
            "sealedBy": sealed_by,
        },
    }
    write_json(run_dir / "seal.json", statement)
    return statement["predicate"]["runHash"]


def overlay_batches(run_dir, run_id, batches, the_run_hash):
    """OVL-4: appends each batch of events to overlays/events.ndjson and seals it, chained to the previous seal."""
    events = run_dir / "overlays" / "events.ndjson"
    offset, previous = 0, None
    data = b""
    for n, batch in enumerate(batches, start=1):
        chunk = "".join(json.dumps(e, ensure_ascii=False, separators=(",", ":")) + "\n" for e in batch).encode("utf-8")
        data += chunk
        write_bytes(events, data)
        seal_path = run_dir / "overlays" / f"seal-{n:04d}.json"
        write_json(seal_path, {
            "_type": "https://in-toto.io/Statement/v1",
            "subject": [{"name": "overlays/events.ndjson", "digest": {"sha256": hashlib.sha256(chunk).hexdigest()}}],
            "predicateType": "https://agenteval.dev/aef/1/overlay-batch",
            "predicate": {"schemaVersion": V, "runId": run_id, "runHash": the_run_hash, "batch": n, "offset": offset,
                          "length": len(chunk), "previous": previous},
        })
        previous = {"path": f"overlays/seal-{n:04d}.json", "sha256": hashlib.sha256(seal_path.read_bytes()).hexdigest()}
        offset += len(chunk)


# ---------------------------------------------------------------------------- writing

def write_bytes(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def write_json(path, obj):
    write_bytes(path, (json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


def ndjson_bytes(objs):
    return "".join(json.dumps(o, ensure_ascii=False, separators=(",", ":")) + "\n" for o in objs).encode("utf-8")


def write_ndjson(path, objs):
    write_bytes(path, ndjson_bytes(objs))


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def h(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def by(identity="oidc:https://login.example.com/u-7f3a"):
    return {"identity": identity, "assurance": "self-attested"}


# ---------------------------------------------------------------------------- valid runs

COMPLETED_ID = "01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10"
TRACE, SPAN = "4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"
JUDGE_SPAN = "b7ad6b7169203331"  # the judge's own call: evidence, not the evaluated operation


def completed_eval(run_dir):
    """A completed evaluation of an agent: a composite case with three children, one top-level case per other state,
    judge reasoning captured as a blob (non-ASCII and a CRLF inside, sealed byte for byte), a trace, evidence, a gate
    decision, a summary that follows SUM-3..5 exactly, and two sealed overlay batches."""
    run_id = COMPLETED_ID
    reasoning = "The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n".encode("utf-8")
    blob = hashlib.sha256(reasoning).hexdigest()
    write_bytes(run_dir / "blobs" / "sha256" / blob[:2] / blob, reasoning)

    rubric = "sha256:" + h("rubric-v3")
    run = {
        "schemaVersion": V, "runId": run_id, "status": "completed",
        "producer": {"name": "agenteval-cli", "version": "1.0.0", "runtime": {"name": "dotnet", "version": "10.0.4"}},
        "subject": {"ref": "agent:support/support-triage", "kind": "agent", "version": "git:3f2a1c", "environment": "dev",
                    "externalIds": {"entraAgentId": None}, "telemetry": {"agentId": "support-triage", "serviceName": "support-api"}},
        "deployment": {"ref": "deployment:support/support-triage@dev", "environment": "dev", "endpoint": "http://localhost:5080"},
        "execution": {"targetMode": "live", "stimulus": "suite"},
        "suite": {"ref": "suite:support/triage-scenarios", "version": "4", "digest": "sha256:" + h("triage-scenarios@4"),
                  "frozen": True, "executionPolicy": {"trialsPerCase": 1}},
        "judges": [{"model": "gpt-5.1", "provider": "azure.ai.openai", "mode": "single", "rubricDigest": rubric,
                    "calibration": {"labelSet": "labels:support/triage-golden@2", "n": 120, "accuracy": 0.925, "kappa": 0.81,
                                    "dangerousErrors": 1, "measuredAt": "2026-09-20T10:00:00Z"}}],
        "config": {"thresholds": {"triage": {"op": ">=", "value": 0.8}}, "temperature": 0},
        "startedAt": "2026-10-02T14:02:11.120Z", "endedAt": "2026-10-02T14:06:23.004Z",
        "otel": {"semconvVersion": "1.41.0", "dialects": ["gen_ai"]},
        "contentCapture": "on",
        "costPolicy": {"maxUsd": 3.0, "priceTable": "2026-09-30"},
        "ext": {},
    }
    write_json(run_dir / "run.json", run)

    root = result_id(run_id, "case-17", "triage")
    policy = result_id(run_id, "case-17", "triage/policy")
    helpful = result_id(run_id, "case-17", "triage/helpfulness")
    grounded = result_id(run_id, "case-17", "triage/groundedness")
    write_ndjson(run_dir / "results.ndjson", [
        {"schemaVersion": V, "resultId": root, "parentResultId": None, "caseId": "case-17", "path": "triage",
         "evaluator": {"id": "composite:triage", "version": "2"}, "state": "failed", "severity": "medium",
         "scores": [{"metric": "triage", "value": 0.55, "normalized": 0.55}],
         "verdictRule": {"expr": "triage >= threshold", "threshold": 0.8, "source": "run.config.thresholds.triage"},
         "aggregation": {"strategy": "WeightedSum", "threshold": 0.8, "score": 0.55, "rulePath": "threshold",
                         "measured": 2, "total": 3, "minimumMeasuredShare": 0.5,
                         "unmeasured": {"not_measured": 0, "not_applicable": 1, "skipped": 0, "error": 0}, "decisive": [helpful]},
         "durationMs": 4210, "turns": 3, "evidence": ["E-1"]},
        {"schemaVersion": V, "resultId": policy, "parentResultId": root, "caseId": "case-17", "path": "triage/policy",
         "evaluator": {"id": "code:refund-escalation", "version": "1"}, "state": "passed",
         "scores": [{"metric": "policy", "value": 1.0, "normalized": 1.0}],
         "annotator": {"kind": "CODE"}, "component": {"weight": 0.5, "required": True}},
        {"schemaVersion": V, "resultId": helpful, "parentResultId": root, "caseId": "case-17", "path": "triage/helpfulness",
         "evaluator": {"id": "llm:helpfulness", "version": "3"}, "state": "failed", "severity": "medium",
         "scores": [{"metric": "helpfulness", "value": 0.1, "normalized": 0.1}],
         "verdictRule": {"expr": "helpfulness >= threshold", "threshold": 0.7, "source": "suite"},
         "annotator": {"kind": "LLM", "model": "gpt-5.1", "promptHash": "sha256:" + h("prompt"), "rubricDigest": rubric},
         "reasoning": {"blob": "sha256:" + blob, "bytes": len(reasoning)},
         "usage": {"gen_ai.usage.input_tokens": 1747, "gen_ai.usage.output_tokens": 488, "costUsd": 0.012,
                   "costSource": "price-table:2026-09-30", "role": "judge"},
         "traceLink": {"traceId": TRACE, "spanId": SPAN},
         "component": {"weight": 0.5, "required": False}, "evidence": ["E-2"]},
        {"schemaVersion": V, "resultId": grounded, "parentResultId": root, "caseId": "case-17", "path": "triage/groundedness",
         "evaluator": {"id": "llm:groundedness", "version": "1"}, "state": "not_applicable",
         "reason": "No retrieved context was recorded for this case: nothing to ground against.",
         "component": {"weight": 0.0, "required": False}},
    ] + [
        # One top-level case per other state. The inconclusive reason holds a raw U+2028: inside a JSON string it is
        # text, not a line break (ENC-6).
        {"schemaVersion": V, "resultId": result_id(run_id, case, "triage"), "parentResultId": None, "caseId": case, "path": "triage",
         "evaluator": {"id": "composite:triage", "version": "2"}, "state": state, **extra}
        for case, state, extra in [
            ("case-18", "warn", {"severity": "low", "scores": [{"metric": "triage", "value": 0.78, "normalized": 0.78}]}),
            ("case-19", "inconclusive", {"reason": "The panel split 1–1 (a third judge timed out)."}),
            ("case-20", "not_measured", {"reason": "The agent's reply was empty: there was nothing to grade."}),
            ("case-21", "skipped", {"reason": "Skipped by --max-cases 20."}),
            ("case-22", "error", {"reason": "The judge returned HTTP 429 three times."}),
        ]
    ])
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": [
        {"id": "triage", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
        {"id": "policy", "kind": "verdict", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
        {"id": "helpfulness", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
        {"id": "groundedness", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
        {"id": "costUsd", "kind": "cost", "direction": "none", "scale": "unbounded", "unit": "USD"},
    ]})
    # SUM-3..5 by hand. triage at path "triage": case-17 (0.55) and case-18 (0.78) are measured; case-19 is
    # inconclusive with no triage score, case-20/21/22 are typed absences: N=6, n=2, notMeasured=4, sum=1.33,
    # sumSq=0.3025+0.6084=0.9109, value=1.33/2=0.665. helpfulness at triage/helpfulness: one measured line, 0.1.
    # groundedness at triage/groundedness: its one line is not_applicable and is left out: N=0, n=0.
    write_json(run_dir / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": [
        {"lane": "quality", "metrics": [
            {"metric": "triage", "path": "triage", "n": 2, "N": 6, "notMeasured": 4, "value": 0.665, "stderr": 0.115,
             "ci": None, "verdict": "failed", "rule": "triage >= 0.8", "sum": 1.33, "sumSq": 0.9109},
            {"metric": "helpfulness", "path": "triage/helpfulness", "n": 1, "N": 1, "notMeasured": 0, "value": 0.1,
             "verdict": "failed", "rule": "helpfulness >= 0.7", "sum": 0.1, "sumSq": 0.01},
            {"metric": "groundedness", "path": "triage/groundedness", "n": 0, "N": 0, "notMeasured": 0, "value": None,
             "verdict": "not_measured", "rule": "groundedness >= 0.7", "sum": 0, "sumSq": 0}]}],
        "cost": {"totalUsd": 0.012, "source": "price-table:2026-09-30"}})
    write_ndjson(run_dir / "evidence.ndjson", [
        {"schemaVersion": V, "evidenceId": "E-1", "kind": "span", "link": {"traceId": TRACE, "spanId": JUDGE_SPAN},
         "description": "The judge call for triage/helpfulness"},
        {"schemaVersion": V, "evidenceId": "E-2", "kind": "judge_reasoning", "digest": "sha256:" + blob,
         "link": {"blob": "sha256:" + blob}, "description": "Judge reasoning for triage/helpfulness"},
    ])
    write_ndjson(run_dir / "gates.ndjson", [
        {"schemaVersion": V, "gateId": "gate:support/pr", "decisionId": "D-31",
         "rule": {"strategy": "threshold", "inputs": ["quality"]}, "inputs": {"results": [root]},
         "comparability": "not_applicable", "outcome": "no_ship", "exitCode": 1, "decisive": [helpful],
         "decidedAt": "2026-10-02T14:06:23.004Z"},
    ])
    write_ndjson(run_dir / "traces.otlp.jsonl", [{"resourceSpans": [{
        "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "support-api"}}]},
        # The evaluated operation (the agent's invocation, which traceLink names, RES-10) and the judge's call (cited
        # as evidence E-1), inside the run's time span: 2026-10-02T14:02:11.120Z is 1790949731120000000 ns.
        "scopeSpans": [{"scope": {"name": "agenteval"}, "spans": [
            {"traceId": TRACE, "spanId": SPAN, "name": "invoke_agent support-triage", "kind": 3,
             "startTimeUnixNano": "1790949731120000000", "endTimeUnixNano": "1790949733400000000",
             "attributes": [{"key": "gen_ai.operation.name", "value": {"stringValue": "invoke_agent"}},
                            {"key": "gen_ai.agent.name", "value": {"stringValue": "support-triage"}}]},
            {"traceId": TRACE, "spanId": JUDGE_SPAN, "parentSpanId": SPAN, "name": "chat gpt-5.1", "kind": 3,
             "startTimeUnixNano": "1790949733500000000", "endTimeUnixNano": "1790949735000000000",
             "attributes": [{"key": "gen_ai.operation.name", "value": {"stringValue": "chat"}},
                            {"key": "gen_ai.request.model", "value": {"stringValue": "gpt-5.1"}},
                            {"key": "gen_ai.usage.input_tokens", "value": {"intValue": "1747"}},
                            {"key": "gen_ai.usage.output_tokens", "value": {"intValue": "488"}}]}]}]}]}])
    the_hash = seal(run_dir, run, "producer", sealed_at="2026-10-02T14:06:24Z")
    overlay_batches(run_dir, run_id, [
        [{"schemaVersion": V, "eventId": "ov_0001", "kind": "annotate", "target": {"run": run_id, "result": helpful},
          "reason": "Rubric §3 reads « escalate » strictly: see thread.", "by": by(), "at": "2026-10-02T15:01:00Z"}],
        [{"schemaVersion": V, "eventId": "ov_0002", "kind": "waive", "target": {"run": run_id, "requirement": "REQ-09"},
          "reason": "rubric under review", "expires": "2026-11-01T00:00:00Z", "by": by(), "at": "2026-10-02T15:10:00Z"}],
    ], the_hash)
    return the_hash


def aborted_early(run_dir):
    run_id = "01928f3f-0000-7000-8000-000000000001"
    run = {"schemaVersion": V, "runId": run_id, "status": "aborted", "abortReason": "The endpoint refused every request (HTTP 401).",
           "producer": {"name": "agenteval-cli", "version": "1.0.0"},
           "subject": {"ref": "agent:people/hr-bot", "kind": "agent"},
           "execution": {"targetMode": "live"},
           "startedAt": "2026-10-03T09:00:00Z", "endedAt": "2026-10-03T09:00:02Z"}
    write_json(run_dir / "run.json", run)
    write_bytes(run_dir / "results.ndjson", b"")
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": []})
    write_json(run_dir / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": []})
    return seal(run_dir, run, "ingest")


def running_trials(run_dir):
    run_id = "01928f40-1111-7111-8111-111111111111"
    run = {"schemaVersion": V, "runId": run_id, "status": "running",
           "producer": {"name": "agenteval-py", "version": "0.1.0a1", "runtime": {"name": "python", "version": "3.13"}},
           "subject": {"ref": "workflow:travel/trip-planner", "kind": "workflow"},
           "execution": {"targetMode": "live", "stimulus": "generated"},
           "suite": {"ref": "suite:travel/bookings", "version": "1",
                     "executionPolicy": {"trialsPerCase": 3, "requirePasses": 2, "aggregation": "MajorityVote"}},
           "startedAt": "2026-10-04T10:00:00Z"}
    write_json(run_dir / "run.json", run)
    lines = [
        {"schemaVersion": V, "resultId": result_id(run_id, "case-3", "booking", t), "caseId": "case-3", "path": "booking",
         "trial": t, "evaluator": {"id": "code:booking-made"}, "state": state}
        for t, state in ((0, "passed"), (1, "failed"), (2, "passed"))
    ]
    lines.append({"schemaVersion": V, "resultId": result_id(run_id, "case-3", "booking"), "caseId": "case-3", "path": "booking",
                  "trials": {"n": 3, "passed": 2, "aggregation": "MajorityVote", "agree": False},
                  "evaluator": {"id": "code:booking-made"}, "state": "passed"})
    lines.append({"schemaVersion": V, "resultId": result_id(run_id, "case-4", "booking", 0), "caseId": "case-4", "path": "booking",
                  "trial": 0, "evaluator": {"id": "code:booking-made"}, "state": "pending", "reason": "Still running."})
    write_ndjson(run_dir / "results.ndjson", lines)
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": [
        {"id": "booking", "kind": "rate", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]})


def redteam_campaign(run_dir):
    """A red-team run: attack fields, severities, a scripted stand-in target (so not live evidence), content off."""
    run_id = "01928f41-2222-7222-8222-222222222222"
    run = {"schemaVersion": V, "runId": run_id, "status": "completed",
           "producer": {"name": "agenteval-cli", "version": "1.0.0"},
           "subject": {"ref": "agent:support/support-triage", "kind": "agent", "version": "git:3f2a1c"},
           "execution": {"targetMode": "scripted", "stimulus": "generated"},
           "startedAt": "2026-10-05T08:00:00Z", "endedAt": "2026-10-05T08:03:00Z", "contentCapture": "off"}
    write_json(run_dir / "run.json", run)
    cases = [
        ("probe-1", "passed", None, {"technique": "prompt-injection", "taxonomy": [{"scheme": "owasp-llm", "id": "LLM01"}, {"scheme": "owasp-agentic", "id": "ASI01"}], "success": False}),
        ("probe-2", "failed", "high", {"technique": "tool-escalation", "taxonomy": [{"scheme": "owasp-agentic", "id": "ASI02"}, {"scheme": "mitre-atlas", "id": "AML.T0051"}], "success": True}),
        ("probe-3", "failed", "low", {"technique": "encoding-evasion", "taxonomy": [{"scheme": "owasp-llm", "id": "LLM01"}], "success": True}),
    ]
    write_ndjson(run_dir / "results.ndjson", [
        {"schemaVersion": V, "resultId": result_id(run_id, case, "attack"), "caseId": case, "path": "attack",
         "evaluator": {"id": "judge:attack-success", "version": "1"}, "state": state,
         **({"severity": severity} if severity else {}), "attack": attack, "turns": 1, "durationMs": 900}
        for case, state, severity, attack in cases])
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": [
        {"id": "resisted", "kind": "rate", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]})
    write_json(run_dir / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": [{"lane": "security", "metrics": [
        {"metric": "resisted", "path": "attack", "n": 3, "N": 3, "notMeasured": 0, "value": 1 / 3, "verdict": "failed",
         "rule": "no attack succeeds", "sum": 1, "sumSq": 1}]}]})
    return seal(run_dir, run, "producer")


# ---------------------------------------------------------------------------- invalid documents (kind: document)

def base_run(**over):
    run = {"schemaVersion": V, "runId": "r-1", "status": "completed", "producer": {"name": "p", "version": "1"},
           "subject": {"ref": "agent:a/b", "kind": "agent"}, "execution": {"targetMode": "live"},
           "startedAt": "2026-10-01T00:00:00Z", "endedAt": "2026-10-01T00:01:00Z"}
    run.update(over)
    return {k: v for k, v in run.items() if v is not DROP}


DROP = object()
RID = result_id("r-1", "c", "p")
BASE_RESULT = {"schemaVersion": V, "resultId": RID, "caseId": "c", "path": "p", "evaluator": {"id": "e"}, "state": "passed"}
EVENT = {"schemaVersion": V, "eventId": "ov_1", "kind": "annotate", "target": {"run": "r-1"}, "by": {"identity": "git:a@b", "assurance": "self-attested"},
         "at": "2026-10-01T00:00:00Z"}
SEAL_PREDICATE = {"schemaVersion": V, "runId": "r-1", "runHash": "0" * 64, "producer": {"name": "p", "version": "1"},
                  "subject": {"ref": "agent:a/b"}, "deployment": None, "suite": None, "judges": [],
                  "closedAt": "2026-10-01T00:00:00Z", "sealedAt": "2026-10-01T00:00:00Z", "sealedBy": "producer"}


def statement(subjects, predicate=SEAL_PREDICATE, ptype="https://agenteval.dev/aef/1/evidence"):
    return {"_type": "https://in-toto.io/Statement/v1", "subject": [{"name": n, "digest": {"sha256": "0" * 64}} for n in subjects],
            "predicateType": ptype, "predicate": predicate}


def invalid_cases():
    """(name, schema, document, reader verdict, rules, why). The writer schema refuses every one."""
    summary_entry = {"metric": "m", "path": "p", "n": 3, "N": 3, "notMeasured": 0, "value": 0.5, "verdict": "passed"}
    return [
        ("run-unknown-major", "run", base_run(schemaVersion="3.0"), "invalid", ["VER-4"], "an unknown major version is refused"),
        ("run-newer-minor-unknown-field", "run", base_run(schemaVersion="1.7", newField={"x": 1}), "valid", ["VER-3"],
         "a newer minor with a field this version does not know: the writer refuses it, a reader accepts it"),
        ("run-aborted-without-reason", "run", base_run(status="aborted"), "invalid", ["RUN-5"], "an aborted run says why"),
        ("run-completed-without-end", "run", base_run(endedAt=DROP), "invalid", ["RUN-5"], "a completed run has endedAt"),
        ("run-running-with-end", "run", base_run(status="running"), "invalid", ["RUN-5"], "an open run has not ended"),
        ("run-aborted-end-null", "run", base_run(status="aborted", abortReason="x", endedAt=None), "invalid", ["RUN-5"],
         "an aborted run has an end time, not null"),
        ("run-without-execution", "run", base_run(execution=DROP), "invalid", ["RUN-7"], "a run says how its target was driven"),
        ("run-endpoint-with-credentials", "run",
         base_run(deployment={"ref": "deployment:a/b@prod", "endpoint": "https://admin:hunter2@agent.example.com/chat"}), "invalid",
         ["RUN-10"], "an endpoint never carries credentials"),
        ("run-bad-timestamp", "run", base_run(startedAt="yesterdayZ"), "invalid", ["ENC-8", "ENC-16"],
         "a time is RFC 3339 UTC by its pattern, whether or not a validator asserts format"),
        ("run-id-trailing-newline", "run", base_run(runId="r-1\n"), "invalid", ["ENC-15"],
         "a pattern's $ is the end of the input: a final newline does not match"),
        ("result-bad-result-id", "result", dict(BASE_RESULT, resultId="r_xyz"), "invalid", ["RES-4"], "resultId is r_ and 32 hex"),
        ("result-id-trailing-newline", "result", dict(BASE_RESULT, resultId=RID + "\n"), "invalid", ["RES-4"],
         "a result id is exactly r_ and 32 hex characters (refused by its length too)"),
        ("version-trailing-newline", "run", base_run(subject={"ref": "agent:a/b", "kind": "agent", "version": "v1\n"}), "invalid",
         ["ENC-15", "ENC-10"], "a version pattern's $ is the end of the input: only that refuses this value"),
        ("result-absence-without-reason", "result", dict(BASE_RESULT, state="not_measured"), "invalid", ["RES-2"],
         "a typed absence says why"),
        ("result-pending-without-reason", "result", dict(BASE_RESULT, state="pending"), "invalid", ["RES-2"],
         "pending is a typed absence too"),
        ("result-absence-with-scores", "result", dict(BASE_RESULT, state="skipped", reason="x", scores=[{"metric": "m", "value": 0}]),
         "invalid", ["RES-2"], "a typed absence carries no scores: it is never a 0"),
        ("result-trial-and-rollup", "result", dict(BASE_RESULT, trial=0, trials={"n": 1, "passed": 1, "aggregation": "MajorityVote", "agree": True}),
         "invalid", ["RES-8"], "a line is a trial or a case's rollup, not both"),
        ("result-case-id-control-char", "result", dict(BASE_RESULT, caseId="a\u001fb"), "invalid", ["RES-4"],
         "caseId and path hold no control character, so the U+001F a result id joins with cannot appear in them"),
        ("summary-value-without-measurement", "summary",
         {"schemaVersion": V, "runId": "r-1", "lanes": [{"lane": "quality", "metrics": [dict(summary_entry, n=0, notMeasured=3)]}]},
         "invalid", ["SUM-5", "SUM-6"], "nothing measured has no value and is not_measured, never a pass"),
        ("summary-entry-without-path", "summary",
         {"schemaVersion": V, "runId": "r-1", "lanes": [{"lane": "quality", "metrics": [{k: v for k, v in summary_entry.items() if k != "path"}]}]},
         "invalid", ["SUM-3"], "an entry names the result path it summarises"),
        ("evidence-two-links", "evidence",
         {"schemaVersion": V, "evidenceId": "E-1", "kind": "document", "digest": "sha256:" + "0" * 64,
          "link": {"blob": "sha256:" + "0" * 64, "uri": "https://example.com/x"}}, "invalid", ["EVD-1"], "a link is exactly one of blob, span or uri"),
        ("evidence-blob-and-span-id", "evidence",
         {"schemaVersion": V, "evidenceId": "E-1", "kind": "document", "digest": "sha256:" + "0" * 64,
          "link": {"blob": "sha256:" + "0" * 64, "spanId": "00f067aa0ba902b7"}}, "invalid", ["EVD-1"], "a link is exactly one of blob, span or uri"),
        ("evidence-blob-without-digest", "evidence",
         {"schemaVersion": V, "evidenceId": "E-1", "kind": "document", "link": {"blob": "sha256:" + "0" * 64}}, "invalid", ["EVD-2"],
         "a blob link carries the blob's digest"),
        ("evidence-span-with-digest", "evidence",
         {"schemaVersion": V, "evidenceId": "E-1", "kind": "span", "digest": "sha256:" + "0" * 64,
          "link": {"traceId": TRACE, "spanId": SPAN}}, "invalid", ["EVD-2"], "a span link has no digest"),
        ("gate-ship-when-incomparable", "gate-decision",
         {"schemaVersion": V, "gateId": "gate:a/b", "decisionId": "D-1", "rule": {"strategy": "baseline"}, "inputs": {},
          "comparability": "incomparable", "outcome": "ship", "decidedAt": "2026-10-01T00:00:00Z"}, "invalid", ["GATE-2"],
         "an incomparable comparison never yields ship"),
        ("gate-baseline-without-hash", "gate-decision",
         {"schemaVersion": V, "gateId": "gate:a/b", "decisionId": "D-1", "rule": {"strategy": "baseline"}, "inputs": {"baselineRun": {"runId": "r-0"}},
          "comparability": "comparable", "outcome": "ship", "decidedAt": "2026-10-01T00:00:00Z"}, "invalid", ["GATE-1"],
         "a baseline is one exact run: its id and its run hash"),
        ("overlay-waive-without-expiry", "overlay-event",
         dict(EVENT, kind="waive", target={"run": "r-1", "requirement": "REQ-1"}, reason="later"), "invalid", ["OVL-1"], "a waiver says until when"),
        ("overlay-override-without-result", "overlay-event",
         dict(EVENT, kind="override", state="passed", reason="re-graded"), "invalid", ["OVL-1"], "an override names the result it changes"),
        ("overlay-redact-without-blob", "overlay-event", dict(EVENT, kind="redact", reason="personal data"), "invalid", ["OVL-1"],
         "a redaction names the blob it withholds"),
        ("overlay-target-without-run", "overlay-event", dict(EVENT, target={"requirement": "REQ-1"}), "invalid", ["OVL-2"],
         "an event names its own run"),
        ("overlay-event-id-trailing-newline", "overlay-event", dict(EVENT, eventId="ov_1\n"), "invalid", ["ENC-15"],
         "an event id's pattern ends at the end of the input"),
        ("overlay-seal-without-run-hash", "overlay-seal",
         statement(["overlays/events.ndjson"], {"schemaVersion": V, "runId": "r-1", "batch": 1, "offset": 0, "length": 10, "previous": None},
                   "https://agenteval.dev/aef/1/overlay-batch"), "invalid", ["OVL-4"], "a batch is bound to the run hash it was appended to"),
        ("seal-absolute-path", "seal", statement(["/etc/passwd"]), "invalid", ["SEAL-5", "RUN-3"],
         "a subject name is a path inside the run folder: no leading slash"),
        ("seal-dot-segment", "seal", statement(["ext/./a"]), "invalid", ["RUN-3"], "no segment is . or .."),
        ("seal-trailing-dot", "seal", statement(["ext/a."]), "invalid", ["RUN-3"], "no segment ends in ."),
        ("seal-wrong-predicate-type", "seal", statement(["run.json"], ptype="https://slsa.dev/provenance/v1"), "invalid", ["SEAL-5"],
         "an AEF seal's predicate type is https://agenteval.dev/aef/1/evidence"),
        ("seal-without-sealed-at", "seal", statement(["run.json"], {k: v for k, v in SEAL_PREDICATE.items() if k != "sealedAt"}), "invalid",
         ["SEAL-5"], "a seal says when it was made"),
    ]


def reader_only_cases():
    """(name, schema, document, reads, rules): a value a later minor could write. The writer schema refuses it; a reader
    accepts it and MUST read it as §7.3 says (reads: field -> the value it reads as)."""
    gate = {"schemaVersion": V, "gateId": "gate:a/b", "decisionId": "D-1", "rule": {"strategy": "threshold"}, "inputs": {},
            "comparability": "comparable", "outcome": "no_ship", "decidedAt": "2026-10-01T00:00:00Z"}
    return [
        ("run-status-unknown", "run", base_run(status="sealed"), {"status": "running"}, ["VER-3"]),
        ("run-target-mode-unknown", "run", base_run(execution={"targetMode": "simulated"}), {"execution.targetMode": "mocked"}, ["VER-3", "RUN-7"]),
        ("run-stimulus-unknown", "run", base_run(execution={"targetMode": "live", "stimulus": "crowd"}), {"execution.stimulus": "other"}, ["VER-3"]),
        ("run-content-capture-unknown", "run", base_run(contentCapture="partial"), {"contentCapture": "on"}, ["VER-3", "RUN-11"]),
        ("result-state-unknown", "result", dict(BASE_RESULT, state="flaky"), {"state": "inconclusive"}, ["VER-3", "RES-1"]),
        ("result-severity-unknown", "result", dict(BASE_RESULT, state="failed", severity="catastrophic"), {"severity": "critical"}, ["VER-3", "RES-9"]),
        ("gate-outcome-unknown", "gate-decision", dict(gate, outcome="ship_with_warnings"), {"outcome": "inconclusive"}, ["VER-3"]),
        ("gate-comparability-unknown", "gate-decision", dict(gate, comparability="partly"), {"comparability": "incomparable"}, ["VER-3"]),
        ("overlay-kind-unknown", "overlay-event", dict(EVENT, kind="escalate"), {"kind": "annotate"}, ["VER-3", "OVL-1"]),
        ("overlay-assurance-unknown", "overlay-event", dict(EVENT, by={"identity": "git:a@b", "assurance": "root"}),
         {"by.assurance": "self-attested"}, ["VER-3", "OVL-3"]),
        ("metrics-direction-unknown", "metrics",
         {"schemaVersion": V, "metrics": [{"id": "m", "kind": "score", "direction": "sideways", "scale": {"min": 0, "max": 1}}]},
         {"metrics[0].direction": "none"}, ["VER-3"]),
        ("seal-sealed-by-unknown", "seal", statement(["run.json"], dict(SEAL_PREDICATE, sealedBy="notary")), {"predicate.sealedBy": "ingest"}, ["VER-3"]),
    ]


# ---------------------------------------------------------------------------- a small run, for run, encoding and view vectors

SMALL_ID = "run-small-1"


def small_run(run_dir, **over):
    """A valid completed run, small enough to read: one composite case with two children, one plain case, a blob, a
    span, evidence, a gate decision and a summary (SUM-3..5: path q has 0.4 (failed) and 0.9 (passed): N=n=2,
    sum=1.3, sumSq=0.97, value=0.65). Returns the dict of its line ids."""
    rid = lambda case, path: result_id(SMALL_ID, case, path)
    ids = {"L1": rid("k1", "q"), "L2": rid("k1", "q/a"), "L3": rid("k1", "q/b"), "L4": rid("k2", "q")}
    reasoning = b"The reply skipped the escalation step.\n"
    blob = hashlib.sha256(reasoning).hexdigest()
    ids["blob"] = blob
    write_bytes(run_dir / "blobs" / "sha256" / blob[:2] / blob, reasoning)
    run = {"schemaVersion": V, "runId": SMALL_ID, "status": "completed", "producer": {"name": "p", "version": "1"},
           "subject": {"ref": "agent:a/b", "kind": "agent", "version": "v1"}, "execution": {"targetMode": "live"},
           "startedAt": "2026-10-01T00:00:00Z", "endedAt": "2026-10-01T00:01:00Z", "contentCapture": "on"}
    run.update(over.pop("run", {}))
    run = {k: v for k, v in run.items() if v is not DROP}
    lines = [
        {"schemaVersion": V, "resultId": ids["L1"], "caseId": "k1", "path": "q", "evaluator": {"id": "composite:q"}, "state": "failed",
         "severity": "medium", "scores": [{"metric": "m", "value": 0.4}],
         "aggregation": {"strategy": "Min", "threshold": 0.5, "score": 0.4, "rulePath": "threshold", "measured": 2, "total": 2,
                         "unmeasured": {"not_measured": 0, "not_applicable": 0, "skipped": 0, "error": 0}, "decisive": [ids["L3"]]},
         "traceLink": {"traceId": TRACE, "spanId": SPAN}, "evidence": ["E-1"]},
        {"schemaVersion": V, "resultId": ids["L2"], "parentResultId": ids["L1"], "caseId": "k1", "path": "q/a", "evaluator": {"id": "code:a"},
         "state": "passed", "scores": [{"metric": "m", "value": 1.0}], "component": {"weight": 1, "required": True}},
        {"schemaVersion": V, "resultId": ids["L3"], "parentResultId": ids["L1"], "caseId": "k1", "path": "q/b", "evaluator": {"id": "llm:b"},
         "state": "failed", "severity": "medium", "scores": [{"metric": "m", "value": 0.4}], "component": {"weight": 1, "required": True},
         "reasoning": {"blob": "sha256:" + blob, "bytes": len(reasoning)}},
        {"schemaVersion": V, "resultId": ids["L4"], "caseId": "k2", "path": "q", "evaluator": {"id": "composite:q"}, "state": "passed",
         "scores": [{"metric": "m", "value": 0.9}]},
    ]
    lines = over.pop("lines", lambda ls: ls)(lines)
    metrics = {"schemaVersion": V, "metrics": [{"id": "m", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]}
    summary = {"schemaVersion": V, "runId": SMALL_ID, "lanes": [{"lane": "main", "metrics": [
        {"metric": "m", "path": "q", "n": 2, "N": 2, "notMeasured": 0, "value": 0.65, "verdict": "failed", "rule": "m >= 0.8",
         "sum": 1.3, "sumSq": 0.97}]}]}
    evidence = [{"schemaVersion": V, "evidenceId": "E-1", "kind": "judge_reasoning", "digest": "sha256:" + blob, "link": {"blob": "sha256:" + blob}}]
    gates = [{"schemaVersion": V, "gateId": "gate:a/b", "decisionId": "D-1", "rule": {"strategy": "threshold"},
              "inputs": {"results": [ids["L1"]]}, "comparability": "not_applicable", "outcome": "no_ship", "decidedAt": "2026-10-01T00:01:00Z"}]
    traces = [{"resourceSpans": [{"resource": {"attributes": []}, "scopeSpans": [{"scope": {"name": "p"}, "spans": [
        {"traceId": TRACE, "spanId": SPAN, "name": "invoke_agent a", "kind": 1,
         "startTimeUnixNano": "1790812810000000000", "endTimeUnixNano": "1790812812000000000",
         **({"attributes": over.pop("traces_attributes")} if "traces_attributes" in over else {})}]}]}]}]
    metrics = over.pop("metrics", lambda m: m)(metrics)
    summary = over.pop("summary", lambda s: s)(summary)
    evidence = over.pop("evidence", lambda e: e)(evidence)
    gates = over.pop("gates", lambda g: g)(gates)
    assert not over, over
    write_json(run_dir / "run.json", run)
    write_ndjson(run_dir / "results.ndjson", lines)
    write_json(run_dir / "metrics.json", metrics)
    write_json(run_dir / "summary.json", summary)
    write_ndjson(run_dir / "evidence.ndjson", evidence)
    write_ndjson(run_dir / "gates.ndjson", gates)
    write_ndjson(run_dir / "traces.otlp.jsonl", traces)
    return run, ids


def set_line(i, **fields):
    def change(lines):
        lines = [dict(l) for l in lines]
        lines[i].update(fields)
        return [{k: v for k, v in l.items() if v is not DROP} for l in lines]
    return change


def run_vectors():
    """(name, build(run_dir) -> None, expected problems [path, code], outcome, rules). Each run is sealed after its
    defect, so the seal verifies and only the rule under test is broken."""
    out = ROOT / "runs"
    blob_other = h("not here")
    vectors = []

    def vec(name, problems, rules, outcome="invalid", **over):
        vectors.append((name, over, problems, outcome, rules))

    vec("valid-small", [], ["RUN-1", "SUM-5"], outcome="intact")
    L = lambda i: f"results.ndjson:{i}"
    vec("result-id-mismatch", [[L(4), "result-id"]], ["RES-4"], lines=set_line(3, resultId=result_id(SMALL_ID, "k2", "other")))
    vec("result-id-duplicate", [[L(5), "result-id"]], ["RES-4"], lines=lambda ls: ls + [dict(ls[1])])
    vec("parent-dangling", [[L(2), "parent"]], ["RES-5"], lines=set_line(1, parentResultId=result_id(SMALL_ID, "k1", "nope")))
    vec("aggregation-counts", [[L(1), "aggregation"]], ["RES-6"],
        lines=lambda ls: set_line(0, aggregation=dict(ls[0]["aggregation"], unmeasured={"not_measured": 0, "not_applicable": 0, "skipped": 1, "error": 0}))(ls))
    vec("decisive-not-child", [[L(1), "aggregation"]], ["RES-6"],
        lines=lambda ls: set_line(0, aggregation=dict(ls[0]["aggregation"], decisive=[ls[3]["resultId"]]))(ls))
    vec("trials-passed-over-n", [[L(5), "trials"]], ["RES-8"], lines=lambda ls: ls + [
        {"schemaVersion": V, "resultId": result_id(SMALL_ID, "k3", "t"), "caseId": "k3", "path": "t", "evaluator": {"id": "code:t"},
         "state": "passed", "trials": {"n": 2, "passed": 3, "aggregation": "MajorityVote", "agree": True}}])
    vec("pending-in-closed-run", [[L(5), "pending"]], ["RES-3"], lines=lambda ls: ls + [
        {"schemaVersion": V, "resultId": result_id(SMALL_ID, "k3", "t"), "caseId": "k3", "path": "t", "evaluator": {"id": "code:t"},
         "state": "pending", "reason": "never finished"}])
    vec("evidence-missing", [[L(1), "evidence"]], ["EVD-1"], lines=set_line(0, evidence=["E-9"]))
    vec("evidence-id-duplicate", [["evidence.ndjson:2", "evidence-id"]], ["EVD-1"], evidence=lambda e: e + [dict(e[0])])
    vec("evidence-digest", [["evidence.ndjson:1", "evidence-digest"]], ["EVD-2"], evidence=lambda e: [dict(e[0], digest="sha256:" + blob_other)])
    vec("blob-missing", [[L(3), "blob"]], ["EVD-3"],
        lines=lambda ls: set_line(2, reasoning={"blob": "sha256:" + blob_other, "bytes": 8})(ls))
    vec("reasoning-size", [[L(3), "reasoning-size"]], ["EVD-3"],
        lines=lambda ls: set_line(2, reasoning=dict(ls[2]["reasoning"], bytes=7))(ls))
    # k2 loses its m score, so path q has one measured line (0.4) and one not measured: the summary says so, and only
    # the undeclared metric is wrong.
    vec("metric-undeclared", [[L(4), "metric"]], ["SUM-1"], lines=set_line(3, scores=[{"metric": "zzz", "value": 0.9}]),
        summary=lambda s: {**s, "lanes": [{"lane": "main", "metrics": [
            dict(s["lanes"][0]["metrics"][0], n=1, notMeasured=1, value=0.4, sum=0.4, sumSq=0.16)]}]})
    vec("metric-declared-twice", [["metrics.json", "metric"]], ["SUM-1"], metrics=lambda m: dict(m, metrics=m["metrics"] + [dict(m["metrics"][0])]))
    vec("metric-scale-inverted", [["metrics.json", "metric"]], ["SUM-1"],
        metrics=lambda m: dict(m, metrics=[dict(m["metrics"][0], scale={"min": 1, "max": 0})]))
    vec("summary-run-id", [["summary.json", "summary-run-id"]], ["SUM-2"], summary=lambda s: dict(s, runId="another-run"))
    vec("summary-counts", [["summary.json", "summary"]], ["SUM-5"],
        summary=lambda s: {**s, "lanes": [{"lane": "main", "metrics": [dict(s["lanes"][0]["metrics"][0], n=1, notMeasured=1)]}]})
    vec("summary-value", [["summary.json", "summary"]], ["SUM-5"],
        summary=lambda s: {**s, "lanes": [{"lane": "main", "metrics": [dict(s["lanes"][0]["metrics"][0], value=0.7)]}]})
    vec("gate-unknown-result", [["gates.ndjson:1", "gate"]], ["GATE-1"],
        gates=lambda g: [dict(g[0], inputs={"results": [result_id(SMALL_ID, "k9", "q")]})])
    vec("trace-link-unresolved", [[L(1), "trace-link"]], ["RUN-14"], lines=set_line(0, traceLink={"traceId": TRACE, "spanId": "0123456789abcdef"}))
    # With capture off: reasoning (line 3), a judge_reasoning record, and a prompt hash (line 2) are all content.
    vec("content-capture-off", [["evidence.ndjson:1", "content-capture"], [L(2), "content-capture"], [L(3), "content-capture"]],
        ["RUN-11", "SEC-3"], run={"contentCapture": "off"},
        lines=set_line(1, annotator={"kind": "LLM", "model": "gpt-5.1", "promptHash": "sha256:" + h("the judge prompt")}))
    vec("content-capture-off-trace", [["traces.otlp.jsonl:1", "content-capture"]], ["SEC-6", "RUN-11"],
        run={"contentCapture": "off"}, lines=lambda ls: [{k: v for k, v in l.items() if k not in ("reasoning", "evidence")} for l in ls],
        evidence=lambda e: [], remove=["blobs/"], traces_attributes=[{"key": "gen_ai.input.messages",
                                                   "value": {"stringValue": "[{\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"content\":\"Refund my order\"}]}]"}}])
    vec("panel-agree-over-of", [[L(3), "annotator"]], ["RES-10"],
        lines=set_line(2, annotator={"kind": "LLM", "model": "gpt-5.1", "panel": {"agree": 4, "of": 3}}))
    vec("aggregation-pending-counts", [], ["RES-6"], outcome="unsealed", unsealed=True,
        run={"status": "running", "endedAt": DROP},
        lines=lambda ls: [dict(ls[0], aggregation=dict(ls[0]["aggregation"], total=3,
                                                         unmeasured={"not_measured": 0, "not_applicable": 0, "skipped": 0, "error": 0, "pending": 1}))]
        + ls[1:3] + [{"schemaVersion": V, "resultId": result_id(SMALL_ID, "k1", "q/c"), "parentResultId": ls[0]["resultId"],
                      "caseId": "k1", "path": "q/c", "evaluator": {"id": "code:c"}, "state": "pending", "reason": "Still running.",
                      "component": {"weight": 1, "required": False}}] + ls[3:])
    vec("run-times", [["run.json", "run-times"]], ["RUN-5"], run={"endedAt": "2025-12-31T23:59:59Z"})
    vec("schema-invalid-line", [[L(4), "schema"]], ["VER-3"], lines=set_line(3, evaluator=DROP))
    vec("time-does-not-exist", [["run.json", "schema"]], ["ENC-8"], run={"startedAt": "2026-02-31T00:00:00Z"})
    vec("time-leap-day-exists", [], ["ENC-8"], outcome="intact", run={"startedAt": "2028-02-29T23:59:59.999999999Z", "endedAt": "2028-03-01T00:00:00Z"})
    # A blob whose bytes do not hash to its name (EVD-3): unreferenced, so only blob-digest is wrong.
    wrong_name = h("the name of other bytes")
    vectors.append(("blob-digest", {"extra": {f"blobs/sha256/{wrong_name[:2]}/{wrong_name}": b"these bytes have another hash\n"}},
                    [[f"blobs/sha256/{wrong_name[:2]}/{wrong_name}", "blob-digest"]], "invalid", ["EVD-3"]))

    # 64 levels of nesting (run.json is level 1, ext level 2): within the limit, so a reader MUST NOT refuse it (ENC-17).
    deep = {}
    node = deep
    for _ in range(61):
        node["d"] = {}
        node = node["d"]
    vec("nesting-depth-64-accepted", [], ["ENC-17"], outcome="intact", run={"ext": {"agenteval.deep": deep}})
    vec("required-file-missing", [["metrics.json", "schema"]], ["RUN-2"], remove=["metrics.json"])
    vectors.append(("manifest-byte-order", {"extra": {"ext/B.txt": b"upper case sorts first by byte\n", "ext/a.txt": b"lower\n",
                                                      "ext/a_b.txt": b"underscore after letters\n", "ext/a-b.txt": b"hyphen\n",
                                                      # by bytes: x-y.txt (2d) < x.y.txt (2e) < x/y.txt (2f); segment by
                                                      # segment would put x/y.txt first
                                                      "ext/x-y.txt": b"1\n", "ext/x.y.txt": b"2\n", "ext/x/y.txt": b"3\n"}},
                    [], "intact", ["SEAL-3"]))
    pending = [{"schemaVersion": V, "resultId": result_id(SMALL_ID, f"k{i}", "t"), "caseId": f"k{i}", "path": "t",
                "evaluator": {"id": "code:t"}, "state": "pending", "reason": "never finished"} for i in range(3, 9)]
    vec("problem-order-by-line-number", [[L(i), "pending"] for i in range(5, 11)], ["RES-3", "CONF-2"],
        lines=lambda ls: ls + pending)
    # Reader-valid only: a later minor's comparability value reads as incomparable (VER-8), so ship is refused (GATE-2).
    vec("gate-ship-on-unknown-comparability", [["gates.ndjson:1", "gate"]], ["GATE-2", "VER-8"],
        gates=lambda g: [dict(g[0], comparability="partly", outcome="ship", exitCode=0)])
    vec("anchored", [], ["SIG-8"], outcome="intact", anchors="self")
    vec("not-anchored", [], ["SIG-8"], outcome="intact", anchors=[h("another run")])
    return out, vectors


def build_run_vectors():
    out, vectors = run_vectors()
    for name, over, problems, outcome, rules in vectors:
        run_dir = out / name / "run"
        over = dict(over)
        extra = over.pop("extra", {})
        remove = over.pop("remove", [])
        anchors = over.pop("anchors", None)
        unsealed = over.pop("unsealed", False)
        run, _ = small_run(run_dir, **over)
        for rel, data in extra.items():
            write_bytes(run_dir / rel, data)
        for rel in remove:
            shutil.rmtree(run_dir / rel) if rel.endswith("/") else (run_dir / rel).unlink()
        the_hash = None if unsealed else seal(run_dir, run, "producer")
        expected = {"kind": "run", "run": "run", "outcome": outcome, "problems": problems, "rules": rules}
        if anchors is not None:
            write_json(out / name / "anchors.json", [the_hash] if anchors == "self" else anchors)
            expected.update(anchors="anchors.json", anchored=anchors == "self")
        write_json(out / name / "expected.json", expected)


def encoding_vectors():
    """Files that break ENC-1..7, in otherwise valid sealed runs. A file that does not read is not read further, and
    the rules across files are not checked while any file does not read (spec 03, §3.9)."""
    out = ROOT / "encoding"
    cases = []

    def case(name, rel, transform, problem_path=None, rules=("ENC-2",)):
        cases.append((name, rel, transform, problem_path or rel, list(rules)))

    case("duplicate-member", "run.json", lambda b: b.replace(b'"status": "completed"', b'"status": "aborted", "status": "completed"', 1), rules=["ENC-2"])
    case("unpaired-surrogate-json", "metrics.json", lambda b: b.replace(b'"id": "m"', b'"id": "m\\udc00"', 1), rules=["ENC-2"])
    case("lone-surrogate", "results.ndjson",
         lambda b: b.replace(b'"evaluator":{"id":"composite:q"},"state":"passed"', b'"evaluator":{"id":"composite:q","note":"\\ud800"},"state":"passed"', 1),
         "results.ndjson:4", ["ENC-2"])
    case("byte-order-mark", "metrics.json", lambda b: b"\xef\xbb\xbf" + b, rules=["ENC-1"])
    case("not-a-number", "metrics.json", lambda b: b.replace(b'"max": 1', b'"max": NaN', 1), rules=["ENC-3"])
    case("crlf-lines", "results.ndjson", lambda b: b.replace(b"\n", b"\r\n"), rules=["ENC-5"])
    case("duplicate-member-in-line", "results.ndjson",
         lambda b: b.replace(b'"evaluator":{"id":"code:a"},"state":"passed"', b'"evaluator":{"id":"code:a"},"state":"failed","state":"passed"', 1),
         "results.ndjson:2", ["ENC-2"])
    case("number-overflows", "metrics.json", lambda b: b.replace(b'"max": 1', b'"max": 1e400', 1), rules=["ENC-3"])
    case("integer-written-2e0-accepted", "summary.json", lambda b: b.replace(b'"n": 2,', b'"n": 2e0,', 1), None, ["ENC-4"])
    case("ndjson-byte-order-mark", "evidence.ndjson", lambda b: b"\xef\xbb\xbf" + b, rules=["ENC-5"])
    case("no-final-newline", "results.ndjson", lambda b: b[:-1], rules=["ENC-5", "ENC-7"])
    case("blank-line", "results.ndjson", lambda b: b.replace(b"\n", b"\n\n", 1), rules=["ENC-5"])
    case("not-an-object", "evidence.ndjson", lambda b: b + b"[1,2]\n", "evidence.ndjson:2", ["ENC-5"])
    case("not-json-line", "gates.ndjson", lambda b: b + b'{"schemaVersion": "1.0",\n', "gates.ndjson:2", ["ENC-7"])
    case("invalid-utf8", "evidence.ndjson", lambda b: b.replace(b"judge_reasoning", b"judge_reas\xc3\x28ning", 1), "evidence.ndjson:1", ["ENC-1"])
    case("empty-results-is-valid-framing", "results.ndjson", lambda b: b"", None, [])
    for name, rel, transform, path, rules in cases:
        run_dir = out / name / "run"
        if name == "integer-written-2e0-accepted":
            # Read as binary64, 2e0 is the integer 2: the run is intact (ENC-4).
            run, _ = small_run(run_dir)
            target = run_dir / rel
            target.write_bytes(transform(target.read_bytes()))
            assert b"2e0" in target.read_bytes()
            seal(run_dir, run, "producer")
            write_json(out / name / "expected.json", {"kind": "encoding", "run": "run", "outcome": "intact", "problems": [],
                                                      "rules": rules})
            continue
        if name == "empty-results-is-valid-framing":
            # An empty results file reads as no lines (ENC-5); the run is then wrong only where the other files
            # still name its results: the gate, and the summary that counted them.
            run, _ = small_run(run_dir, lines=lambda ls: [], evidence=lambda e: e,
                               gates=lambda g: [dict(g[0], inputs={})],
                               summary=lambda s: {**s, "lanes": [{"lane": "main", "metrics": [
                                   {"metric": "m", "path": "q", "n": 0, "N": 0, "notMeasured": 0, "value": None,
                                    "verdict": "not_measured", "rule": "m >= 0.8", "sum": 0, "sumSq": 0}]}]})
            seal(run_dir, run, "producer")
            write_json(out / name / "expected.json", {"kind": "run", "run": "run", "outcome": "intact", "problems": [],
                                                      "rules": ["ENC-5", "SUM-5", "SUM-6"]})
            continue
        run, _ = small_run(run_dir)
        target = run_dir / rel
        before = target.read_bytes()
        after = transform(before)
        assert after != before, name
        target.write_bytes(after)
        seal(run_dir, run, "producer")
        write_json(out / name / "expected.json", {"kind": "encoding", "run": "run", "outcome": "invalid",
                                                  "problems": [[path, "encoding"]], "rules": rules})


def path_vectors():
    """[RUN-3] over lists of paths, with the problems a verifier reports (path, then code, in byte order). These
    paths cannot all exist in a checkout on Windows or macOS, which is the point of the rule."""
    long_ok = "ext/" + "a" * 251
    cases = [
        ("ascii", ["run.json", "ext/a-b_c.d/e.txt"], []),
        ("leading-dot", [".DS_Store", "ext/.hidden", "ext/a/.git/x"], [".DS_Store", "ext/.hidden", "ext/a/.git/x"]),
        ("space", ["ext/a b.txt"], ["ext/a b.txt"]),
        ("not-ascii", ["ext/caf\u00e9.txt"], ["ext/caf\u00e9.txt"]),
        ("backslash", ["ext\\a.txt"], ["ext\\a.txt"]),
        ("dot-segment", ["ext/./a"], ["ext/./a"]),
        ("dot-dot-segment", ["ext/../run.json"], ["ext/../run.json"]),
        ("trailing-dot", ["ext/a."], ["ext/a."]),
        ("empty-segment", ["ext//a"], ["ext//a"]),
        ("leading-slash", ["/ext/a"], ["/ext/a"]),
        ("reserved-name", ["ext/CON"], ["ext/CON"]),
        ("reserved-name-any-case-and-extension", ["ext/con.txt", "ext/Aux.tar.gz", "ext/lpt9.log", "ext/nul"],
         ["ext/Aux.tar.gz", "ext/con.txt", "ext/lpt9.log", "ext/nul"]),
        ("not-reserved", ["ext/COM10", "ext/console.txt", "ext/CONx", "ext/LPT0"], []),
        ("case-clash", ["ext/A.txt", "ext/a.txt"], ["ext/a.txt"]),
        ("case-clash-in-a-folder", ["ext/Data/x", "ext/data/y"], ["ext/data/y"]),
        ("length-255", [long_ok], []),
        ("length-256", [long_ok + "b"], [long_ok + "b"]),
    ]
    assert len(long_ok.encode()) == 255
    return [{"name": n, "paths": paths, "problems": [[p, "path"] for p in sorted(bad, key=lambda s: s.encode("utf-8"))],
             "rules": ["RUN-3"]} for n, paths, bad in cases]


# ---------------------------------------------------------------------------- seal vectors

def seal_vectors(valid):
    out = ROOT / "seal-vectors"

    def copy(name, source="aborted-early"):
        run = out / name / "run"
        shutil.copytree(valid / source / "run", run)
        return run

    def expect(name, problems, rules, with_manifest=None):
        doc = {"kind": "seal", "run": "run", "problems": problems, "rules": rules}
        if with_manifest is not None:
            write_bytes(out / name / "expected-manifest.txt", manifest(with_manifest).encode("utf-8"))
            doc["manifest"] = "expected-manifest.txt"
        write_json(out / name / "expected.json", doc)

    for name in ("completed-eval", "aborted-early", "redteam-campaign"):
        run = copy(name, name)
        expect(name, [], ["SEAL-1", "SEAL-3", "SEAL-4", "SEAL-6"], with_manifest=run)

    run = copy("tampered", "completed-eval")
    results = run / "results.ndjson"
    results.write_bytes(results.read_bytes().replace(b'"state":"failed"', b'"state":"passed"', 1))
    expect("tampered", [["results.ndjson", "digest"]], ["SEAL-6"])

    run = copy("tampered-blob", "completed-eval")
    blob_rel = next(p for p in sealed_files(run) if p.startswith("blobs/"))
    (run / blob_rel).write_bytes((run / blob_rel).read_bytes() + b"!")
    expect("tampered-blob", [[blob_rel, "digest"]], ["SEAL-6"])

    run = copy("added-file")
    write_bytes(run / "notes.txt", b"added after the seal\n")
    expect("added-file", [["notes.txt", "not-sealed"]], ["SEAL-6", "RUN-2"])

    run = copy("missing-file")
    (run / "metrics.json").unlink()
    expect("missing-file", [["metrics.json", "missing"]], ["SEAL-6"])

    # Paths that only sort right by their UTF-8 bytes (Z < a; a-b < a.b < a/b), and an attestation, never sealed.
    run = copy("path-order")
    (run / "seal.json").unlink()
    for name in ("ext/a.b", "ext/a-b", "ext/a/b", "ext/Z"):
        write_bytes(run / name, f"{name}\n".encode("utf-8"))
    seal(run, read_json(run / "run.json"), "ingest")
    write_json(run / "attestation.dsse.json", {"payloadType": "application/vnd.in-toto+json",
                                               "payload": base64.b64encode((run / "seal.json").read_bytes()).decode("ascii"),
                                               "signatures": [{"keyid": "sha256:" + "0" * 64, "sig": "AAAA"}]})
    expect("path-order", [], ["SEAL-3"], with_manifest=run)

    def edit_seal(name, change, source="aborted-early"):
        run = copy(name, source)
        s = read_json(run / "seal.json")
        change(s)
        write_json(run / "seal.json", s)
        return run

    edit_seal("wrong-run-hash", lambda s: s["predicate"].update(runHash="0" * 64))
    expect("wrong-run-hash", [["seal.json", "run-hash"]], ["SEAL-6"])
    edit_seal("other-run-id", lambda s: s["predicate"].update(runId="another-run"))
    expect("other-run-id", [["seal.json", "run-id"]], ["SEAL-6"])
    run = edit_seal("duplicate-subject", lambda s: s["subject"].append(dict(s["subject"][0])))
    expect("duplicate-subject", [[read_json(run / "seal.json")["subject"][0]["name"], "duplicate-subject"]], ["SEAL-6"])
    edit_seal("predicate-differs", lambda s: s["predicate"]["subject"].update(version="git:good"), "completed-eval")
    expect("predicate-differs", [["seal.json", "predicate"]], ["SEAL-6"])
    run = copy("seal-duplicate-member")
    sp = run / "seal.json"
    sp.write_bytes(sp.read_bytes().replace(b'"sealedBy": "ingest"', b'"sealedBy": "producer", "sealedBy": "ingest"', 1))
    expect("seal-duplicate-member", [["seal.json", "seal-invalid"]], ["SEAL-6", "ENC-2"])
    edit_seal("closed-at-other-form", lambda s: s["predicate"].update(closedAt=s["predicate"]["closedAt"].replace("Z", ".000Z")))
    expect("closed-at-other-form", [], ["SEAL-6", "ENC-8"])
    edit_seal("seal-invalid", lambda s: s["predicate"].pop("runHash"))
    expect("seal-invalid", [["seal.json", "seal-invalid"]], ["SEAL-6"])
    edit_seal("subject-path", lambda s: s["subject"].append({"name": "overlays/events.ndjson", "digest": {"sha256": "0" * 64}}), "completed-eval")
    expect("subject-path", [["overlays/events.ndjson", "subject-path"]], ["SEAL-6"])

    run = out / "open-run" / "run"
    shutil.copytree(valid / "running-trials" / "run", run)
    seal(run, read_json(run / "run.json"), "producer", "2026-10-04T10:05:00Z", allow_open=True)
    expect("open-run", [["run.json", "run-open"], ["seal.json", "predicate"]], ["SEAL-6", "SEAL-5", "RUN-5"])

    # A blob withheld by a sealed redact overlay: reported as withheld, not missing, and the run stays verifiable.
    run = copy("withheld-blob", "completed-eval")
    blob_rel = next(p for p in sealed_files(run) if p.startswith("blobs/"))
    blob_hex = blob_rel.rsplit("/", 1)[1]
    the_hash = read_json(run / "seal.json")["predicate"]["runHash"]
    events = [json.loads(l) for l in (run / "overlays" / "events.ndjson").read_text(encoding="utf-8").splitlines()]
    shutil.rmtree(run / "overlays")
    overlay_batches(run, COMPLETED_ID, [events[:1], events[1:], [
        {"schemaVersion": V, "eventId": "ov_0003", "kind": "redact", "target": {"run": COMPLETED_ID, "blob": blob_hex},
         "reason": "The judge's reasoning quoted a customer's address.", "by": by(), "at": "2026-10-03T09:00:00Z"}]], the_hash)
    (run / blob_rel).unlink()
    for folder in (run / blob_rel).parents:  # no empty folders left behind: git would not keep them
        if folder == run or any(folder.iterdir()):
            break
        folder.rmdir()
    expect("withheld-blob", [[blob_rel, "withheld"]], ["SEAL-6", "OVL-10"])
    shutil.copytree(run, ROOT / "runs" / "withheld-blob" / "run")
    write_json(ROOT / "runs" / "withheld-blob" / "expected.json", {
        "kind": "run", "run": "run", "outcome": "intact", "problems": [[blob_rel, "withheld"]], "rules": ["OVL-10", "SEAL-6"]})


# ---------------------------------------------------------------------------- chain vectors

def chain_vectors(valid):
    chain = ROOT / "chain-vectors"
    source = valid / "completed-eval" / "run"
    base_events = [json.loads(l) for l in (source / "overlays" / "events.ndjson").read_text(encoding="utf-8").splitlines()]
    the_hash = read_json(source / "seal.json")["predicate"]["runHash"]

    def copy(name):
        run = chain / name / "run"
        shutil.copytree(source, run)
        return run

    def expect(name, problems, rules):
        write_json(chain / name / "expected.json", {"kind": "chain", "run": "run", "problems": problems, "rules": rules})

    def rebatch(run, batches):
        shutil.rmtree(run / "overlays")
        overlay_batches(run, COMPLETED_ID, batches, the_hash)

    def edit_seal(run, n, change):
        path = run / "overlays" / f"seal-{n:04d}.json"
        s = read_json(path)
        change(s)
        write_json(path, s)

    def rewrite_range(run, n, offset, length):
        """Re-states batch n's range and digest over the current events file, as a writer that got it wrong would."""
        events = (run / "overlays" / "events.ndjson").read_bytes()

        def change(s):
            s["predicate"]["offset"], s["predicate"]["length"] = offset, length
            s["subject"][0]["digest"]["sha256"] = hashlib.sha256(events[offset:offset + length]).hexdigest()
        edit_seal(run, n, change)

    copy("intact")
    expect("intact", [], ["OVL-4", "OVL-5"])

    run = copy("altered-batch")
    events = run / "overlays" / "events.ndjson"
    events.write_bytes(events.read_bytes().replace(b"strictly", b"strict!y", 1))
    expect("altered-batch", [["overlays/seal-0001.json", "batch-digest"]], ["OVL-5"])

    run = copy("missing-seal")
    (run / "overlays" / "seal-0001.json").unlink()
    expect("missing-seal", [["overlays/events.ndjson", "uncovered"], ["overlays/seal-0001.json", "missing"],
                            ["overlays/seal-0002.json", "previous"]], ["OVL-5"])

    run = copy("with-batch-signature")
    write_json(run / "overlays" / "seal-0001.dsse.json", {"payloadType": "application/vnd.in-toto+json", "payload": "",
                                                           "signatures": [{"keyid": "sha256:" + "0" * 64, "sig": "AAAA"}]})
    expect("with-batch-signature", [], ["OVL-5"])

    run = copy("overlapping-batch")
    first = read_json(run / "overlays" / "seal-0001.json")["predicate"]
    total = len((run / "overlays" / "events.ndjson").read_bytes())
    start = first["offset"] + first["length"] - 10
    rewrite_range(run, 2, start, total - start)
    expect("overlapping-batch", [["overlays/seal-0002.json", "line-boundary"], ["overlays/seal-0002.json", "offset"]], ["OVL-5"])

    run = copy("batch-ends-mid-line")
    first = read_json(run / "overlays" / "seal-0001.json")["predicate"]
    rewrite_range(run, 1, 0, first["length"] - 1)
    expect("batch-ends-mid-line", [["overlays/events.ndjson", "uncovered"], ["overlays/seal-0001.json", "line-boundary"],
                                   ["overlays/seal-0002.json", "offset"], ["overlays/seal-0002.json", "previous"]], ["OVL-5"])

    run = copy("unsealed-tail")
    events = run / "overlays" / "events.ndjson"
    events.write_bytes(events.read_bytes() + ndjson_bytes([
        {"schemaVersion": V, "eventId": "ov_0003", "kind": "annotate", "target": {"run": COMPLETED_ID}, "by": by(), "at": "2026-10-02T16:00:00Z"}]))
    expect("unsealed-tail", [["overlays/events.ndjson", "uncovered"]], ["OVL-5"])

    run = copy("batch-number")
    edit_seal(run, 2, lambda s: s["predicate"].update(batch=3))
    expect("batch-number", [["overlays/seal-0002.json", "batch-number"]], ["OVL-5"])

    run = copy("other-run-id")
    edit_seal(run, 2, lambda s: s["predicate"].update(runId="another-run"))
    expect("other-run-id", [["overlays/seal-0002.json", "run-id"]], ["OVL-5"])

    run = copy("other-run-hash")
    edit_seal(run, 2, lambda s: s["predicate"].update(runHash="0" * 64))
    expect("other-run-hash", [["overlays/seal-0002.json", "run-hash"]], ["OVL-4", "OVL-5"])

    run = copy("batch-invalid")
    edit_seal(run, 2, lambda s: s["predicate"].pop("length"))
    expect("batch-invalid", [["overlays/events.ndjson", "uncovered"], ["overlays/seal-0002.json", "batch-invalid"]], ["OVL-5"])

    run = copy("unexpected-file")
    write_bytes(run / "overlays" / "notes.txt", b"a file that is not an overlay\n")
    expect("unexpected-file", [["overlays/notes.txt", "unexpected-file"]], ["OVL-5", "RUN-2"])

    run = copy("event-id-order")
    many = [dict(base_events[0], eventId=f"ov_{i:04d}", at=f"2026-10-02T15:{i:02d}:00Z") for i in range(1, 9)]
    many += [dict(base_events[0], eventId="ov_0001", at=f"2026-10-02T16:{i:02d}:00Z") for i in range(9, 13)]
    rebatch(run, [many[:6], many[6:]])
    expect("event-id-order", [[f"overlays/events.ndjson:{i}", "event-id"] for i in range(9, 13)], ["OVL-5", "CONF-2"])

    run = copy("integer-forms")
    edit_seal(run, 2, lambda s: s["predicate"].update(batch=2.0))
    assert b'"batch": 2.0' in (run / "overlays" / "seal-0002.json").read_bytes()
    expect("integer-forms", [], ["OVL-5", "ENC-4"])

    run = copy("target-run-hash")
    rebatch(run, [base_events[:1], [dict(base_events[1], target=dict(base_events[1]["target"], runHash="0" * 64))]])
    expect("target-run-hash", [["overlays/events.ndjson:2", "target"]], ["OVL-2", "OVL-5"])

    run = copy("event-invalid")
    rebatch(run, [base_events[:1], [{k: v for k, v in base_events[1].items() if k != "kind"}]])
    expect("event-invalid", [["overlays/events.ndjson:2", "event-invalid"]], ["OVL-1", "OVL-5"])

    run = copy("seal-0000")
    shutil.copyfile(run / "overlays" / "seal-0001.json", run / "overlays" / "seal-0000.json")
    expect("seal-0000", [["overlays/seal-0000.json", "batch-number"]], ["OVL-4", "OVL-5"])

    run = copy("event-id-twice")
    rebatch(run, [base_events[:1], [dict(base_events[1], eventId=base_events[0]["eventId"])]])
    expect("event-id-twice", [["overlays/events.ndjson:2", "event-id"]], ["OVL-1", "OVL-5"])

    run = copy("target-other-run")
    rebatch(run, [base_events[:1], [dict(base_events[1], target={"run": "another-run", "requirement": "REQ-09"})]])
    expect("target-other-run", [["overlays/events.ndjson:2", "target"]], ["OVL-2", "OVL-5"])


# ---------------------------------------------------------------------------- overlay views

def overlay_view_vectors():
    """The effective view (§4.3) of a run with overlays, at a given time, written by hand."""
    out = ROOT / "overlay-views"
    run_dir = out / "review-history" / "run"
    run, ids = small_run(run_dir)
    the_hash = seal(run_dir, run, "producer")
    rid = SMALL_ID
    e = lambda n, **f: dict({"schemaVersion": V, "eventId": f"ov_{n:04d}", "by": by(), "at": f"2026-10-0{min(n, 7)}T10:00:00Z"}, **f)
    batches = [
        [e(1, kind="approve", target={"run": rid}),
         e(2, kind="override", target={"run": rid, "result": ids["L3"]}, state="passed", reason="Re-graded by hand.")],
        [e(3, kind="reject", target={"run": rid, "runHash": the_hash}),
         e(4, kind="waive", target={"run": rid, "requirement": "REQ-1"}, reason="Rubric under review.", expires="2026-10-10T00:00:00Z"),
         e(5, kind="waive", target={"run": rid, "result": ids["L4"]}, reason="Known flake.", expires="2026-10-05T00:00:00Z")],
        [e(6, kind="adjudicate", target={"run": rid, "result": ids["L3"]}, state="failed", reason="Panel of three: 2 to 1."),
         e(7, kind="redact", target={"run": rid, "blob": ids["blob"]}, reason="The reasoning quoted an e-mail address."),
         e(8, kind="annotate", target={"run": rid, "result": ids["L1"]}, reason="See the incident review.")],
    ]
    overlay_batches(run_dir, rid, batches, the_hash)
    events = run_dir / "overlays" / "events.ndjson"
    events.write_bytes(events.read_bytes() + ndjson_bytes([
        e(9, kind="override", target={"run": rid, "result": ids["L2"]}, state="failed", reason="Not sealed: no effect.")]))
    write_json(out / "review-history" / "expected.json", {
        "kind": "overlay-view", "run": "run", "at": "2026-10-08T12:00:00Z", "rules": ["OVL-6", "OVL-7", "OVL-8", "OVL-9", "OVL-10"],
        "view": {
            "results": [{"resultId": ids["L3"], "sealedState": "failed", "effectiveState": "failed", "event": "ov_0006"}],
            "reviews": [{"target": "run", "status": "reject", "event": "ov_0003"}],
            "waivers": [{"target": {"requirement": "REQ-1"}, "expires": "2026-10-10T00:00:00Z", "active": True, "event": "ov_0004"},
                        {"target": {"result": ids["L4"]}, "expires": "2026-10-05T00:00:00Z", "active": False, "event": "ov_0005"}],
            "withheld": [ids["blob"]],
            "unsealedEvents": 1,
        },
    })


# ---------------------------------------------------------------------------- checkpoint manifests (kind: checkpoint)

def checkpoints():
    """Checkpoint manifests: (name, document, writer verdict, reader verdict, problems, rules, why). problems are what
    the checkpoint verifier reports from the manifest alone ([CKP-7]), written by hand."""
    hh = lambda c: c * 64
    lanes = [
        {"lane": "quality", "rule": {"kind": "threshold", "lane": "quality", "metric": "triage", "path": "triage", "op": ">=", "value": 0.8},
         "requirements": ["REQ-07"], "runs": [{"runId": "Q-184", "runHash": hh("a"), "origin": "launched"}], "blocking": True},
        {"lane": "security", "rule": {"kind": "severity", "max": "low"}, "requirements": ["REQ-15"],
         "runs": [{"runId": "R-921", "runHash": hh("b"), "origin": "adopted:ci"},
                  {"runId": "R-930", "runHash": hh("c"), "origin": "launched"}], "blocking": True, "freshness": "P14D"},
        {"lane": "memory", "rule": {"kind": "comparison", "lane": "memory", "metric": "recall", "path": "recall",
                                    "baseline": {"runId": "M-77", "runHash": hh("d")}, "significance": 0.05, "minimumPairs": 20,
                                    "axes": ["suite", "judges", "target-mode"]},
         "runs": [], "blocking": False},
    ]
    decision_input = {
        "subjectVersion": "git:3f2a1c", "evaluatedAt": "2026-10-02T14:30:00Z", "supersededBy": None,
        "lanes": [
            {"lane": "quality", "blocking": True,
             "result": {"status": "passed", "subjectVersion": "git:3f2a1c", "oldestClosedAt": "2026-10-02T13:10:00Z"}},
            {"lane": "security", "blocking": True, "freshness": "P14D",
             "result": {"status": "passed", "subjectVersion": "git:3f2a1c", "oldestClosedAt": "2026-09-30T08:00:00Z"}},
            {"lane": "memory", "blocking": False, "result": None},
        ],
    }
    decision = {"outcome": "inconclusive",
                "lanes": [{"lane": "quality", "status": "passed", "blocking": True},
                          {"lane": "security", "status": "passed", "blocking": True},
                          {"lane": "memory", "status": "missing", "blocking": False}],
                "reasons": ["missing:memory", "outcome:inconclusive"]}
    decided = {
        "schemaVersion": V, "checkpointId": "cp_01J9K4", "template": {"ref": "template:support/release-candidate", "version": "3"},
        "subject": {"ref": "agent:support/support-triage", "version": "git:3f2a1c", "image": "sha256:" + "9d" * 32,
                    "deployment": "deployment:support/support-triage@prod-eu", "resolvedFrom": "latest", "resolvedAt": "2026-10-02T13:58:00Z"},
        "lanes": lanes,
        "budget": {"approvedUsd": 10.0, "spentUsd": 6.4, "approvedBy": {"identity": "oidc:https://login.example.com/u-7f3a", "assurance": "self-attested"}},
        "state": "decided", "outcome": "inconclusive", "decisionInput": decision_input, "decision": decision,
    }
    planned = {k: v for k, v in decided.items() if k not in ("decision", "decisionInput")}
    planned.update(state="planned", outcome=None)
    aborted = dict(planned, state="decided", outcome="aborted", abortReason="The approver did not approve the spend.")
    unknown_kind = dict(planned, lanes=[dict(lanes[0], rule={"kind": "drift", "window": "P7D"})])
    other_input = dict(decision_input, lanes=[dict(decision_input["lanes"][0], result=dict(decision_input["lanes"][0]["result"], status="failed"))]
                       + decision_input["lanes"][1:])
    C7 = ["CKP-7"]
    return [
        ("valid-decided", decided, "valid", "valid", [], ["CKP-4", "CKP-7"], "a decided checkpoint, its decision recomputable from its input"),
        ("valid-planned", planned, "valid", "valid", [], ["CKP-4"], "a planned checkpoint has no outcome yet"),
        ("valid-aborted", aborted, "valid", "valid", [], ["CKP-4"], "an abandoned checkpoint says why and records no decision"),
        ("decided-without-outcome", dict(decided, outcome=None), "invalid", "invalid", None, ["CKP-4"], "a decided checkpoint has an outcome"),
        ("decided-without-input", {k: v for k, v in decided.items() if k != "decisionInput"}, "invalid", "invalid", None, ["CKP-4"],
         "a decision is recorded with its input, so it can be recomputed"),
        ("aborted-without-reason", {k: v for k, v in aborted.items() if k != "abortReason"}, "invalid", "invalid", None, ["CKP-4"],
         "an abandoned checkpoint says why"),
        ("aborted-with-decision", dict(aborted, decision=decision), "invalid", "invalid", None, ["CKP-4"],
         "the decision function never aborts: an aborted checkpoint has no decision"),
        ("state-sealed-is-gone", dict(decided, state="sealed"), "invalid", "valid", ["unverifiable"], ["CKP-4", "VER-3"],
         "1.0 has no sealed state (a decided checkpoint's integrity is its signature); a reader takes it as unknown"),
        ("latest-is-not-a-version", dict(planned, subject=dict(planned["subject"], version="Latest")), "invalid", "invalid", None, ["CKP-1"],
         "'latest' (any case) is resolved to an exact version before anything runs"),
        ("version-with-space", dict(planned, subject=dict(planned["subject"], version="1.2 beta")), "invalid", "invalid", None, ["ENC-10"],
         "a version has no whitespace"),
        ("lane-name-with-space", dict(planned, lanes=[dict(lanes[0], lane="red team")]), "invalid", "invalid", None, ["CKP-2"],
         "a lane name is an id, so a reason code can carry it"),
        ("freshness-empty", dict(planned, lanes=[dict(lanes[1], freshness="P")]), "invalid", "invalid", None, ["ENC-9"],
         "a duration is not empty"),
        ("run-without-hash", dict(planned, lanes=[dict(lanes[0], runs=[{"runId": "Q-184", "origin": "launched"}])]), "invalid", "invalid", None,
         ["CKP-2"], "a lane's runs are frozen by their run hashes"),
        ("threshold-without-path", dict(planned, lanes=[dict(lanes[0], rule={"kind": "threshold", "lane": "quality", "metric": "m", "op": ">=", "value": 1})]),
         "invalid", "invalid", None, ["LANE-2"], "a threshold rule names the summary entry: lane, metric and path"),
        ("comparison-unknown-axis", dict(planned, lanes=[lanes[0], lanes[1], dict(lanes[2], rule=dict(lanes[2]["rule"], axes=["weather"]))]),
         "invalid", "valid", [], ["LANE-6", "VER-3"], "an axis a later minor adds: a reader accepts it, and the comparison is incomparable"),
        ("unknown-rule-kind", unknown_kind, "invalid", "valid", [], ["VER-3"],
         "a rule kind this version does not know: the writer refuses it, a reader accepts it and the lane is not measured"),
        ("planned-with-outcome", dict(planned, outcome="approved"), "invalid", "invalid", None, ["CKP-4"], "no outcome before the decision"),
        ("outcome-differs", dict(decided, outcome="approved"), "valid", "valid", ["outcome"], C7, "the outcome is the decision's"),
        ("decision-not-recomputed", dict(decided, decisionInput=other_input), "valid", "valid", ["decision"], C7,
         "the recorded decision is what the recorded input gives"),
        ("lanes-differ", dict(decided, decisionInput=dict(decision_input, lanes=decision_input["lanes"][:2])), "valid", "valid",
         ["decision", "lanes"], C7, "the input decides exactly the manifest's lanes"),
        ("version-differs", dict(decided, decisionInput=dict(decision_input, subjectVersion="git:000000")), "valid", "valid",
         ["decision", "version"], C7, "the input is for the checkpoint's version"),
        ("evidence-without-runs", dict(decided, lanes=[dict(lanes[0], runs=[])] + lanes[1:]), "valid", "valid", ["evidence"], C7,
         "a lane with a result names the runs it came from"),
        ("runs-without-evidence", dict(decided, decisionInput=dict(decision_input, lanes=decision_input["lanes"][:1] + [
            dict(decision_input["lanes"][1], result=None)] + decision_input["lanes"][2:])), "valid", "valid", ["decision", "evidence"], C7,
         "a lane with runs has a result (here the input drops it, so the recorded decision is not recomputed either)"),
        ("newer-outcome", {k: v for k, v in dict(decided, outcome="waived").items() if k not in ("decision", "decisionInput")},
         "invalid", "valid", ["unverifiable"], C7,
         "an outcome a later minor adds: a reader cannot recompute it, and says so rather than call it tampering"),
        ("input-status-unknown", dict(decided, decisionInput=dict(decision_input, lanes=[dict(decision_input["lanes"][0], result=dict(
            decision_input["lanes"][0]["result"], status="flaky"))] + decision_input["lanes"][1:])), "invalid", "valid", ["unverifiable"], C7,
         "a lane status a later minor adds, in the recorded input: the decision cannot be recomputed"),
    ]


# ---------------------------------------------------------------------------- main

def main():
    # What this script owns. decision-vectors/, protocol/, lane-vectors/, signature-vectors/ are written by their own
    # scripts.
    owned = ("valid", "invalid", "reader-only", "runs", "encoding", "seal-vectors", "chain-vectors", "overlay-views", "checkpoints")
    for name in owned:
        if (ROOT / name).exists():
            shutil.rmtree(ROOT / name)
    for name in ("result-ids.json", "paths.json"):
        if (ROOT / name).exists():
            (ROOT / name).unlink()
    valid = ROOT / "valid"
    completed_eval(valid / "completed-eval" / "run")
    aborted_early(valid / "aborted-early" / "run")
    running_trials(valid / "running-trials" / "run")
    redteam_campaign(valid / "redteam-campaign" / "run")
    expectations = {"completed-eval": "intact", "aborted-early": "intact", "running-trials": "unsealed", "redteam-campaign": "intact"}
    for name, outcome in expectations.items():
        write_json(valid / name / "expected.json", {"kind": "run", "run": "run", "outcome": outcome, "problems": [],
                                                     "rules": ["RUN-1", "RUN-2", "RES-1", "SUM-5"]})
    write_json(ROOT / "paths.json", path_vectors())

    for name, schema, doc, reader, rules, why in invalid_cases():
        write_json(ROOT / "invalid" / name / "document.json", doc)
        write_json(ROOT / "invalid" / name / "expected.json", {"kind": "document", "schema": schema, "writer": "invalid",
                                                               "reader": reader, "rules": rules, "why": why})
    for name, schema, doc, reads, rules in reader_only_cases():
        write_json(ROOT / "reader-only" / name / "document.json", doc)
        write_json(ROOT / "reader-only" / name / "expected.json", {"kind": "reader-only", "schema": schema, "writer": "invalid",
                                                                   "reader": "valid", "reads": reads, "rules": rules})

    build_run_vectors()
    encoding_vectors()
    seal_vectors(valid)
    chain_vectors(valid)
    overlay_view_vectors()

    for name, doc, writer, reader, problems, rules, why in checkpoints():
        write_json(ROOT / "checkpoints" / name / "document.json", doc)
        expected = {"kind": "checkpoint", "schema": "checkpoint", "writer": writer, "reader": reader, "rules": rules, "why": why}
        if problems is not None:
            expected["problems"] = problems
        write_json(ROOT / "checkpoints" / name / "expected.json", expected)

    vectors = [
        ("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage", None),
        ("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage/helpfulness", None),
        ("r-1", "case-3", "booking", 0),
        ("r-1", "case-3", "booking", 12),
        ("run-7", "casé-été ラン", "مرحبا/—", None),
        ("run-7", "case-3", "booking", 3.0),
    ]
    write_json(ROOT / "result-ids.json", [
        {"runId": r, "caseId": c, "path": p, "trial": t, "resultId": result_id(r, c, p, t)} for r, c, p, t in vectors])


if __name__ == "__main__":
    main()
