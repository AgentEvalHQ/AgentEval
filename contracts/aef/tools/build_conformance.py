#!/usr/bin/env python3
"""Builds the AEF v2 conformance corpus (contracts/aef/v2/conformance/) from the definitions below.

This script is one implementation of the AEF v2 rules it writes (result ids, the seal); the .NET conformance tests
are another and recompute every value. Re-running it rewrites the corpus byte for byte: commit both together.

Usage: python contracts/aef/tools/build_conformance.py
"""
import base64
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "v2" / "conformance"
V = "2.0"


# ---------------------------------------------------------------------------- rules

def result_id(run_id, case_id, path, trial=None):
    """r_ + the first 32 hex of SHA-256 over runId, caseId, path and trial joined by U+001F (no trial: empty)."""
    digits = "" if trial is None else str(int(trial))  # plain integer digits: 3.0 is "3"
    assert trial is None or float(trial).is_integer()
    text = "\u001f".join([run_id, case_id, path, digits])
    return "r_" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:32]


def sealed_files(run_dir):
    """Every file of the run except seal.json, attestation.dsse.json and overlays/ (written after close)."""
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
    """One line per file, sorted by path (ordinal over UTF-8): '<sha256-hex>  <bytes>  <path>\\n'."""
    lines = []
    for rel in sealed_files(run_dir):
        data = (run_dir / rel).read_bytes()
        lines.append(f"{hashlib.sha256(data).hexdigest()}  {len(data)}  {rel}\n")
    return "".join(lines)


def seal(run_dir, run, sealed_by, closed_at=None, allow_open=False):
    assert allow_open or run["status"] != "running", "only a closed run is sealed"
    text = manifest(run_dir)
    statement = {
        "_type": "https://in-toto.io/Statement/v1",
        "subject": [
            {"name": rel, "digest": {"sha256": hashlib.sha256((run_dir / rel).read_bytes()).hexdigest()}}
            for rel in sealed_files(run_dir)
        ],
        "predicateType": "https://agenteval.dev/evidence/v2",
        "predicate": {
            "schemaVersion": V,
            "runId": run["runId"],
            "runHash": hashlib.sha256(text.encode("utf-8")).hexdigest(),
            "producer": {"name": run["producer"]["name"], "version": run["producer"]["version"]},
            "subject": {k: run["subject"][k] for k in ("ref", "version") if k in run["subject"]},
            "deployment": {"ref": run["deployment"]["ref"]} if "deployment" in run else None,
            "suite": {k: run["suite"][k] for k in ("ref", "version", "digest") if k in run["suite"]} if "suite" in run else None,
            "judges": [{k: j[k] for k in ("model", "rubricDigest") if k in j} for j in run.get("judges", [])],
            "closedAt": closed_at or run["endedAt"],
            "sealedBy": sealed_by,
        },
    }
    write_json(run_dir / "seal.json", statement)
    return text


# ---------------------------------------------------------------------------- writing

def write_bytes(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def write_json(path, obj):
    write_bytes(path, (json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


def write_ndjson(path, objs):
    write_bytes(path, "".join(json.dumps(o, ensure_ascii=False, separators=(",", ":")) + "\n" for o in objs).encode("utf-8"))


def overlay_batches(run_dir, run_id, batches):
    """Appends each batch of events to overlays/events.ndjson and seals it, chained to the previous seal."""
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
            "predicateType": "https://agenteval.dev/evidence/v2/overlay-batch",
            "predicate": {"schemaVersion": V, "runId": run_id, "batch": n, "offset": offset, "length": len(chunk), "previous": previous},
        })
        previous = {"path": f"overlays/seal-{n:04d}.json", "sha256": hashlib.sha256(seal_path.read_bytes()).hexdigest()}
        offset += len(chunk)


# ---------------------------------------------------------------------------- valid runs

def completed_eval(run_dir):
    run_id = "01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10"
    reasoning = "The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n".encode("utf-8")
    blob = hashlib.sha256(reasoning).hexdigest()
    write_bytes(run_dir / "blobs" / "sha256" / blob[:2] / blob, reasoning)

    run = {
        "schemaVersion": V, "runId": run_id, "status": "completed",
        "producer": {"name": "agenteval-cli", "version": "0.44.0", "runtime": {"name": "dotnet", "version": "10.0.4"}},
        "subject": {"ref": "agent:support/support-triage", "kind": "agent", "version": "git:3f2a1c", "environment": "dev",
                    "externalIds": {"entraAgentId": None}, "telemetry": {"agentId": "support-triage", "serviceName": "support-api"}},
        "deployment": {"ref": "deployment:support/support-triage@dev", "environment": "dev", "endpoint": "http://localhost:5080"},
        "suite": {"ref": "suite:support/triage-scenarios", "version": "4",
                  "digest": "sha256:" + hashlib.sha256(b"triage-scenarios@4").hexdigest(), "frozen": True,
                  "executionPolicy": {"trialsPerCase": 1}},
        "judges": [{"model": "gpt-5.1", "provider": "azure.ai.openai", "mode": "single",
                    "rubricDigest": "sha256:" + hashlib.sha256(b"rubric-v3").hexdigest()}],
        "config": {"thresholds": {"triage": {"op": ">=", "value": 0.8}}, "temperature": 0},
        "startedAt": "2026-10-02T14:02:11.120Z", "endedAt": "2026-10-02T14:06:23.004Z",
        "otel": {"semconvVersion": "1.41.0", "dialects": ["gen_ai"]},
        "contentCapture": "off",
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
         "evaluator": {"id": "composite:triage", "version": "2"}, "state": "failed",
         "scores": [{"metric": "triage", "value": 0.55, "normalized": 0.55}],
         "verdictRule": {"expr": "triage >= threshold", "threshold": 0.8, "source": "run.config.thresholds.triage"},
         "aggregation": {"strategy": "WeightedSum", "threshold": 0.8, "score": 0.55, "rulePath": "threshold",
                         "measured": 2, "total": 3, "minimumMeasuredShare": 0.5,
                         "unmeasured": {"not_measured": 0, "not_applicable": 1, "skipped": 0, "errored": 0}, "decisive": [helpful]},
         "evidence": ["E-1"]},
        {"schemaVersion": V, "resultId": policy, "parentResultId": root, "caseId": "case-17", "path": "triage/policy",
         "evaluator": {"id": "code:refund-escalation", "version": "1"}, "state": "passed",
         "scores": [{"metric": "policy", "value": 1.0, "normalized": 1.0}],
         "annotator": {"kind": "CODE"}, "component": {"weight": 0.5, "required": True}},
        {"schemaVersion": V, "resultId": helpful, "parentResultId": root, "caseId": "case-17", "path": "triage/helpfulness",
         "evaluator": {"id": "llm:helpfulness", "version": "3"}, "state": "failed",
         "scores": [{"metric": "helpfulness", "value": 0.1, "normalized": 0.1}],
         "verdictRule": {"expr": "helpfulness >= threshold", "threshold": 0.7, "source": "suite"},
         "annotator": {"kind": "LLM", "model": "gpt-5.1", "promptHash": "sha256:" + hashlib.sha256(b"prompt").hexdigest(),
                       "rubricDigest": "sha256:" + hashlib.sha256(b"rubric-v3").hexdigest()},
         "reasoning": {"blob": "sha256:" + blob, "bytes": len(reasoning)},
         "usage": {"gen_ai.usage.input_tokens": 1747, "gen_ai.usage.output_tokens": 488, "costUsd": 0.012,
                   "costSource": "price-table:2026-09-30", "role": "judge"},
         "traceLink": {"traceId": "4bf92f3577b34da6a3ce929d0e0e4736", "spanId": "00f067aa0ba902b7"},
         "component": {"weight": 0.5, "required": False}, "evidence": ["E-2"]},
        {"schemaVersion": V, "resultId": grounded, "parentResultId": root, "caseId": "case-17", "path": "triage/groundedness",
         "evaluator": {"id": "llm:groundedness", "version": "1"}, "state": "not_applicable",
         "reason": "No retrieved context was recorded for this case — nothing to ground against.",
         "component": {"weight": 0.0, "required": False}},
    ] + [
        # One top-level case per remaining state. The inconclusive reason holds a raw U+2028: inside a JSON string it is
        # text, not a line break, and an NDJSON reader that splits on it breaks the line.
        {"schemaVersion": V, "resultId": result_id(run_id, case, "triage"), "parentResultId": None, "caseId": case, "path": "triage",
         "evaluator": {"id": "composite:triage", "version": "2"}, "state": state, **extra}
        for case, state, extra in [
            ("case-18", "warn", {"scores": [{"metric": "triage", "value": 0.78, "normalized": 0.78}]}),
            ("case-19", "inconclusive", {"reason": "The panel split 1–1\u2028(a third judge timed out)."}),
            ("case-20", "not_measured", {"reason": "The agent's reply was empty: there was nothing to grade."}),
            ("case-21", "skipped", {"reason": "Skipped by --max-cases 20."}),
            ("case-22", "error", {"reason": "The judge returned HTTP 429 three times."}),
        ]
    ])
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": [
        {"id": "triage", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
        {"id": "policy", "kind": "verdict", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
        {"id": "helpfulness", "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}},
        {"id": "costUsd", "kind": "cost", "direction": "none", "scale": "unbounded", "unit": "USD"},
    ]})
    write_json(run_dir / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": [
        {"lane": "quality", "metrics": [
            {"metric": "triage", "n": 1, "N": 1, "notMeasured": 0, "value": 0.55, "stderr": None, "ci": None,
             "verdict": "failed", "rule": "triage >= 0.8", "sum": 0.55, "sumSq": 0.3025},
            {"metric": "groundedness", "n": 0, "N": 1, "notMeasured": 1, "value": None, "verdict": "not_measured",
             "rule": "groundedness >= 0.7"}]}]})
    write_ndjson(run_dir / "evidence.ndjson", [
        {"schemaVersion": V, "evidenceId": "E-1", "kind": "span", "digest": "sha256:" + hashlib.sha256(b"span").hexdigest(),
         "link": {"traceId": "4bf92f3577b34da6a3ce929d0e0e4736", "spanId": "00f067aa0ba902b7"}},
        {"schemaVersion": V, "evidenceId": "E-2", "kind": "judge_reasoning", "digest": "sha256:" + blob,
         "link": {"blob": "sha256:" + blob}, "description": "Judge reasoning for triage/helpfulness"},
    ])
    write_ndjson(run_dir / "gates.ndjson", [
        {"schemaVersion": V, "gateId": "gate:support/pr", "decisionId": "D-31",
         "rule": {"strategy": "threshold", "inputs": ["quality"]}, "inputs": {"results": [root]},
         "comparability": "not_applicable", "outcome": "no_ship", "exitCode": 1, "decisive": [helpful],
         "decidedAt": "2026-10-02T14:06:23.004Z"},
    ])
    seal(run_dir, run, "producer")
    overlay_batches(run_dir, run_id, [
        [{"schemaVersion": V, "eventId": "ov_0001", "kind": "annotate", "target": {"result": helpful},
          "reason": "Rubric §3 reads « escalate » strictly — see thread.", "by": {"identity": "git:owner@example.com", "assurance": "self-attested"},
          "at": "2026-10-02T15:01:00Z"}],
        [{"schemaVersion": V, "eventId": "ov_0002", "kind": "waive", "target": {"requirement": "REQ-09", "run": run_id},
          "reason": "rubric under review", "expires": "2026-11-01T00:00:00Z",
          "by": {"identity": "git:owner@example.com", "os": "owner", "assurance": "self-attested"}, "at": "2026-10-02T15:10:00Z"}],
    ])


def aborted_early(run_dir):
    run_id = "01928f3f-0000-7000-8000-000000000001"
    run = {"schemaVersion": V, "runId": run_id, "status": "aborted", "abortReason": "The endpoint refused every request (HTTP 401).",
           "producer": {"name": "agenteval-cli", "version": "0.44.0"},
           "subject": {"ref": "agent:people/hr-bot", "kind": "agent"},
           "startedAt": "2026-10-03T09:00:00Z", "endedAt": "2026-10-03T09:00:02Z"}
    write_json(run_dir / "run.json", run)
    write_bytes(run_dir / "results.ndjson", b"")
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": []})
    write_json(run_dir / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": []})
    seal(run_dir, run, "ingest")


def running_trials(run_dir):
    run_id = "01928f40-1111-7111-8111-111111111111"
    run = {"schemaVersion": V, "runId": run_id, "status": "running",
           "producer": {"name": "agenteval-py", "version": "0.1.0a1", "runtime": {"name": "python", "version": "3.13"}},
           "subject": {"ref": "workflow:travel/trip-planner", "kind": "workflow"},
           "suite": {"ref": "suite:travel/bookings", "version": "1", "executionPolicy": {"trialsPerCase": 3, "requirePasses": 2, "aggregation": "MajorityVote"}},
           "startedAt": "2026-10-04T10:00:00Z", "endedAt": None}
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
                  "trial": 0, "evaluator": {"id": "code:booking-made"}, "state": "pending"})
    write_ndjson(run_dir / "results.ndjson", lines)
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": [
        {"id": "booking", "kind": "rate", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]})


# ---------------------------------------------------------------------------- invalid documents

def invalid_cases():
    """(name, schema, document, reader verdict, the rule it breaks). The writer schema rejects every one."""
    base_run = {"schemaVersion": V, "runId": "r-1", "status": "completed", "producer": {"name": "p", "version": "1"},
                "subject": {"ref": "agent:a/b", "kind": "agent"}, "startedAt": "2026-10-01T00:00:00Z", "endedAt": "2026-10-01T00:01:00Z"}
    rid = result_id("r-1", "c", "p")
    base_result = {"schemaVersion": V, "resultId": rid, "caseId": "c", "path": "p", "evaluator": {"id": "e"}, "state": "passed"}
    return [
        ("run-unknown-major", "run", dict(base_run, schemaVersion="3.0"), "invalid", "an unknown major version is refused"),
        ("run-newer-minor-unknown-field", "run", dict(base_run, schemaVersion="2.7", newField={"x": 1}), "valid",
         "a newer minor with a field this version does not know: the writer refuses it, a reader accepts it"),
        ("run-status-sealed", "run", dict(base_run, status="sealed"), "valid",
         "there is no 'sealed' status (sealed is a fact about seal.json); a reader takes an unknown status as other"),
        ("run-aborted-without-reason", "run", dict(base_run, status="aborted"), "invalid", "an aborted run says why"),
        ("run-completed-without-end", "run", {k: v for k, v in base_run.items() if k != "endedAt"}, "invalid", "a completed run has endedAt"),
        ("result-bad-result-id", "result", dict(base_result, resultId="r_xyz"), "invalid", "resultId is r_ and 32 hex"),
        ("result-absence-without-reason", "result", dict(base_result, state="not_measured"), "invalid",
         "a typed absence (not_measured, not_applicable, skipped, error) says why"),
        ("result-trial-and-rollup", "result", dict(base_result, trial=0, trials={"n": 1, "passed": 1, "aggregation": "MajorityVote", "agree": True}),
         "invalid", "a line is a trial or a case's rollup, not both"),
        ("summary-value-without-measurement", "summary",
         {"schemaVersion": V, "runId": "r-1", "lanes": [{"lane": "quality", "metrics": [
             {"metric": "m", "n": 0, "N": 3, "notMeasured": 3, "value": 0.5, "verdict": "passed"}]}]},
         "invalid", "nothing measured has no value and is not_measured, never a pass"),
        ("evidence-two-links", "evidence",
         {"schemaVersion": V, "evidenceId": "E-1", "kind": "document", "digest": "sha256:" + "0" * 64,
          "link": {"blob": "sha256:" + "0" * 64, "uri": "https://example.com/x"}}, "invalid", "a link is exactly one of blob, span or uri"),
        ("gate-ship-when-incomparable", "gate-decision",
         {"schemaVersion": V, "gateId": "gate:a/b", "decisionId": "D-1", "rule": {"strategy": "baseline"}, "inputs": {},
          "comparability": "incomparable", "outcome": "ship", "decidedAt": "2026-10-01T00:00:00Z"}, "invalid",
         "an incomparable comparison never yields ship"),
        ("overlay-waive-without-expiry", "overlay-event",
         {"schemaVersion": V, "eventId": "ov_1", "kind": "waive", "target": {"requirement": "REQ-1"}, "reason": "later",
          "by": {"identity": "git:a@b", "assurance": "self-attested"}, "at": "2026-10-01T00:00:00Z"}, "invalid",
         "a waiver says until when"),
        ("overlay-override-without-result", "overlay-event",
         {"schemaVersion": V, "eventId": "ov_2", "kind": "override", "target": {"run": "r-1"}, "state": "passed", "reason": "re-graded",
          "by": {"identity": "git:a@b", "assurance": "self-attested"}, "at": "2026-10-01T00:00:00Z"}, "invalid",
         "an override names the result it changes"),
        ("run-aborted-end-null", "run", dict(base_run, status="aborted", abortReason="x", endedAt=None), "invalid",
         "an aborted run has an end time, not null"),
        ("run-bad-timestamp", "run", dict(base_run, startedAt="yesterdayZ"), "invalid",
         "a time is RFC 3339 UTC by its pattern, whether or not a validator asserts format"),
        ("result-id-trailing-newline", "result", dict(base_result, resultId=rid + "\n"), "invalid",
         "a pattern's end is the end of the string: '$' alone also matches before a final newline"),
        ("run-id-trailing-newline", "run", dict(base_run, runId="r-1\n"), "invalid",
         "an id's pattern ends at the end of the string, not before a final newline"),
        ("overlay-event-id-trailing-newline", "overlay-event",
         {"schemaVersion": V, "eventId": "ov_1\n", "kind": "annotate", "target": {"run": "r-1"},
          "by": {"identity": "git:a@b", "assurance": "self-attested"}, "at": "2026-10-01T00:00:00Z"}, "invalid",
         "an event id's pattern ends at the end of the string"),
        ("result-case-id-control-char", "result", dict(base_result, caseId="a\u001fb"), "invalid",
         "caseId and path hold no control character, so the U+001F a result id joins with cannot appear in them"),
        ("evidence-blob-and-span-id", "evidence",
         {"schemaVersion": V, "evidenceId": "E-1", "kind": "document", "digest": "sha256:" + "0" * 64,
          "link": {"blob": "sha256:" + "0" * 64, "spanId": "00f067aa0ba902b7"}}, "invalid", "a link is exactly one of blob, span or uri"),
        ("seal-wrong-predicate-type", "seal",
         {"_type": "https://in-toto.io/Statement/v1", "subject": [{"name": "run.json", "digest": {"sha256": "0" * 64}}],
          "predicateType": "https://slsa.dev/provenance/v1",
          "predicate": {"schemaVersion": V, "runId": "r-1", "runHash": "0" * 64, "producer": {"name": "p", "version": "1"},
                        "subject": {"ref": "agent:a/b"}, "closedAt": "2026-10-01T00:00:00Z", "sealedBy": "producer"}},
         "invalid", "an AEF seal's predicate type is https://agenteval.dev/evidence/v2"),
    ]


# ---------------------------------------------------------------------------- checkpoints

def checkpoints():
    """Checkpoint manifests: (name, document, writer verdict, reader verdict, problems, why). problems are what the
    checkpoint verifier reports on a schema-valid manifest (v2/README.md, 'Checkpoints'), written by hand."""
    h = lambda c: c * 64
    lanes = [
        {"lane": "quality", "rule": {"kind": "threshold", "metric": "triage.passRate", "op": ">=", "value": 0.8},
         "requirements": ["REQ-07"], "runs": [{"runId": "Q-184", "runHash": h("a"), "origin": "launched"}], "blocking": True},
        {"lane": "security", "rule": {"kind": "severity", "max": "low"}, "requirements": ["REQ-15"],
         "runs": [{"runId": "R-921", "runHash": h("b"), "origin": "adopted:ci"},
                  {"runId": "R-930", "runHash": h("c"), "origin": "launched"}], "blocking": True, "freshness": "P14D"},
        {"lane": "memory", "rule": {"kind": "comparison", "baseline": "policy:same-branch", "significance": 0.05},
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
        "lanes": lanes, "comparability": {"required": ["judge.modelId", "judge.rubricDigest", "stimulus", "executionPolicy"]},
        "budget": {"approvedUsd": 10.0, "spentUsd": 6.4, "approvedBy": {"identity": "git:owner@example.com", "assurance": "self-attested"}},
        "state": "decided", "outcome": "inconclusive", "decisionInput": decision_input, "decision": decision,
    }
    planned = {k: v for k, v in decided.items() if k not in ("decision", "decisionInput")}
    planned.update(state="planned", outcome=None)
    aborted = dict(planned, state="decided", outcome="aborted", abortReason="The approver did not approve the spend.")
    unknown_kind = dict(planned, lanes=[dict(lanes[0], rule={"kind": "drift", "window": "P7D"})])
    other_input = dict(decision_input, lanes=[dict(decision_input["lanes"][0], result=dict(decision_input["lanes"][0]["result"], status="failed"))]
                       + decision_input["lanes"][1:])
    return [
        ("valid-decided", decided, "valid", "valid", [], "a decided checkpoint, its decision recomputable from its input"),
        ("valid-planned", planned, "valid", "valid", [], "a planned checkpoint has no outcome yet"),
        ("valid-aborted", aborted, "valid", "valid", [], "an abandoned checkpoint says why and records no decision"),
        ("decided-without-outcome", dict(decided, outcome=None), "invalid", "invalid", None, "a decided checkpoint has an outcome"),
        ("decided-without-input", {k: v for k, v in decided.items() if k != "decisionInput"}, "invalid", "invalid", None,
         "a decision is recorded with its input, so it can be recomputed"),
        ("aborted-without-reason", {k: v for k, v in aborted.items() if k != "abortReason"}, "invalid", "invalid", None,
         "an abandoned checkpoint says why"),
        ("aborted-with-decision", dict(aborted, decision=decision), "invalid", "invalid", None,
         "the decision function never aborts: an aborted checkpoint has no decision"),
        ("latest-is-not-a-version", dict(planned, subject=dict(planned["subject"], version="Latest")), "invalid", "invalid", None,
         "'latest' (any case) is resolved to an exact version before anything runs"),
        ("version-with-space", dict(planned, subject=dict(planned["subject"], version="1.2 beta")), "invalid", "invalid", None,
         "a version has no whitespace"),
        ("lane-name-with-space", dict(planned, lanes=[dict(lanes[0], lane="red team")]), "invalid", "invalid", None,
         "a lane name is an id, so a reason code can carry it"),
        ("run-without-hash", dict(planned, lanes=[dict(lanes[0], runs=[{"runId": "Q-184", "origin": "launched"}])]), "invalid", "invalid", None,
         "a lane's runs are frozen by their run hashes"),
        ("threshold-without-value", dict(planned, lanes=[dict(lanes[0], rule={"kind": "threshold", "metric": "m", "op": ">="})]),
         "invalid", "invalid", None, "a threshold rule names its value"),
        ("unknown-rule-kind", unknown_kind, "invalid", "valid", None,
         "a rule kind this version does not know: the writer refuses it, a reader accepts it as other"),
        ("planned-with-outcome", dict(planned, outcome="approved"), "invalid", "invalid", None, "no outcome before the decision"),
        ("outcome-differs", dict(decided, outcome="approved"), "valid", "valid", ["outcome"],
         "the outcome is the decision's"),
        ("decision-not-recomputed", dict(decided, decisionInput=other_input), "valid", "valid", ["decision"],
         "the recorded decision is what the recorded input gives"),
        ("lanes-differ", dict(decided, decisionInput=dict(decision_input, lanes=decision_input["lanes"][:2])), "valid", "valid",
         ["decision", "lanes"], "the input decides exactly the manifest's lanes"),
        ("version-differs", dict(decided, decisionInput=dict(decision_input, subjectVersion="git:000000")), "valid", "valid",
         ["decision", "version"], "the input is for the checkpoint's version"),
        ("evidence-without-runs", dict(decided, lanes=[dict(lanes[0], runs=[])] + lanes[1:]), "valid", "valid", ["evidence"],
         "a lane with a result names the runs it came from, and a lane with runs has a result"),
    ]


# ---------------------------------------------------------------------------- seal vectors

def seal_vectors(valid):
    out = ROOT / "seal-vectors"
    # Each valid run, as sealed.
    for name in ("completed-eval", "aborted-early"):
        write_bytes(out / name / "expected-manifest.txt", manifest(valid / name).encode("utf-8"))
        write_json(out / name / "expected.json", {"run": f"valid/{name}", "verdict": "match", "mismatches": []})

    # Tampered: one byte of a sealed file changed after the seal.
    tampered = out / "tampered" / "run"
    shutil.copytree(valid / "completed-eval", tampered)
    results = tampered / "results.ndjson"
    results.write_bytes(results.read_bytes().replace(b'"state":"failed"', b'"state":"passed"', 1))
    write_json(out / "tampered" / "expected.json", {"run": "seal-vectors/tampered/run", "verdict": "mismatch",
                                                     "mismatches": [{"path": "results.ndjson", "problem": "digest"}]})

    # A file added after the seal, and one removed.
    added = out / "added-file" / "run"
    shutil.copytree(valid / "aborted-early", added)
    write_bytes(added / "notes.txt", "added after the seal\n".encode("utf-8"))
    write_json(out / "added-file" / "expected.json", {"run": "seal-vectors/added-file/run", "verdict": "mismatch",
                                                       "mismatches": [{"path": "notes.txt", "problem": "not-sealed"}]})
    missing = out / "missing-file" / "run"
    shutil.copytree(valid / "aborted-early", missing)
    (missing / "metrics.json").unlink()
    write_json(out / "missing-file" / "expected.json", {"run": "seal-vectors/missing-file/run", "verdict": "mismatch",
                                                         "mismatches": [{"path": "metrics.json", "problem": "missing"}]})


def seal_vectors_more(valid):
    out = ROOT / "seal-vectors"

    def copy(name, source="aborted-early"):
        run = out / name / "run"
        shutil.copytree(valid / source, run)
        return run

    def expect(name, verdict, mismatches):
        write_json(out / name / "expected.json", {"run": f"seal-vectors/{name}/run", "verdict": verdict, "mismatches": mismatches})

    # Paths that only sort right by their UTF-8 bytes (a-b < a.b < a/b; Z < a), and an attestation, never sealed.
    run = out / "path-order" / "run"
    shutil.copytree(valid / "aborted-early", run)
    (run / "seal.json").unlink()
    for name in ("ext/a.b", "ext/a-b", "ext/a/b", "ext/Z"):  # Z (0x5A) sorts before a: not case-insensitive
        write_bytes(run / name, f"{name}\n".encode("utf-8"))
    seal(run, json.loads((run / "run.json").read_text(encoding="utf-8")), "ingest")
    # A well-formed DSSE envelope over seal.json; its signature is not checked here (DSSE vectors come later).
    write_json(run / "attestation.dsse.json", {"payloadType": "application/vnd.in-toto+json",
                                               "payload": base64.b64encode((run / "seal.json").read_bytes()).decode("ascii"),
                                               "signatures": [{"keyid": "test-key-not-verified", "sig": "AAAA"}]})
    write_bytes(out / "path-order" / "expected-manifest.txt", manifest(run).encode("utf-8"))
    expect("path-order", "match", [])

    # The seal's run hash is wrong although every file matches its subject.
    run = copy("wrong-run-hash")
    statement = json.loads((run / "seal.json").read_text(encoding="utf-8"))
    statement["predicate"]["runHash"] = "0" * 64
    write_json(run / "seal.json", statement)
    expect("wrong-run-hash", "mismatch", [{"path": "seal.json", "problem": "run-hash"}])

    # The seal names another run.
    run = copy("other-run-id")
    statement = json.loads((run / "seal.json").read_text(encoding="utf-8"))
    statement["predicate"]["runId"] = "another-run"
    write_json(run / "seal.json", statement)
    expect("other-run-id", "mismatch", [{"path": "seal.json", "problem": "run-id"}])

    # A subject listed twice.
    run = copy("duplicate-subject")
    statement = json.loads((run / "seal.json").read_text(encoding="utf-8"))
    statement["subject"].append(dict(statement["subject"][0]))
    write_json(run / "seal.json", statement)
    expect("duplicate-subject", "mismatch", [{"path": statement["subject"][0]["name"], "problem": "duplicate-subject"}])

    # The seal's predicate says another version than run.json.
    run = copy("predicate-differs", "completed-eval")
    statement = json.loads((run / "seal.json").read_text(encoding="utf-8"))
    statement["predicate"]["subject"]["version"] = "git:good"
    write_json(run / "seal.json", statement)
    expect("predicate-differs", "mismatch", [{"path": "seal.json", "problem": "predicate"}])

    # seal.json itself is not a valid seal.
    run = copy("seal-invalid")
    statement = json.loads((run / "seal.json").read_text(encoding="utf-8"))
    del statement["predicate"]["runHash"]
    write_json(run / "seal.json", statement)
    expect("seal-invalid", "mismatch", [{"path": "seal.json", "problem": "seal-invalid"}])

    # A run sealed while it was still running.
    run = out / "open-run" / "run"
    shutil.copytree(valid / "running-trials", run)
    seal(run, json.loads((run / "run.json").read_text(encoding="utf-8")), "producer", "2026-10-04T10:05:00Z", allow_open=True)
    # (an open run has no endedAt; the closedAt given here is the producer's claim)
    expect("open-run", "mismatch", [{"path": "run.json", "problem": "run-open"}])

    # The overlay chain: a byte changed inside batch 1, batch 1's seal missing, bytes appended after the last batch.
    chain = ROOT / "chain-vectors"
    if chain.exists():
        shutil.rmtree(chain)

    def chain_copy(name):
        run = chain / name / "run"
        shutil.copytree(valid / "completed-eval", run)
        return run

    def chain_expect(name, problems):
        write_json(chain / name / "expected.json", {"run": f"chain-vectors/{name}/run", "problems": problems})

    run = chain_copy("altered-batch")
    events = run / "overlays" / "events.ndjson"
    events.write_bytes(events.read_bytes().replace(b"strictly", b"strict!y", 1))
    chain_expect("altered-batch", [{"path": "overlays/seal-0001.json", "problem": "batch-digest"}])

    run = chain_copy("missing-seal")
    (run / "overlays" / "seal-0001.json").unlink()
    chain_expect("missing-seal", [{"path": "overlays/events.ndjson", "problem": "uncovered"},
                                  {"path": "overlays/seal-0001.json", "problem": "missing"},
                                  {"path": "overlays/seal-0002.json", "problem": "previous"}])

    run = chain_copy("unsealed-tail")
    events = run / "overlays" / "events.ndjson"
    events.write_bytes(events.read_bytes() + b'{"schemaVersion":"2.0","eventId":"ov_0003","kind":"annotate","target":{"run":"x"},"by":{"identity":"git:a@b","assurance":"self-attested"},"at":"2026-10-02T16:00:00Z"}\n')
    chain_expect("unsealed-tail", [{"path": "overlays/events.ndjson", "problem": "uncovered"}])


def main():
    # Only what this script writes: decision-vectors/ are hand-written and stay.
    for owned in ("valid", "invalid", "seal-vectors"):
        if (ROOT / owned).exists():
            shutil.rmtree(ROOT / owned)
    valid = ROOT / "valid"
    completed_eval(valid / "completed-eval")
    aborted_early(valid / "aborted-early")
    running_trials(valid / "running-trials")

    for name, schema, doc, reader, rule in invalid_cases():
        write_json(ROOT / "invalid" / name / "document.json", doc)
        write_json(ROOT / "invalid" / name / "expected.json", {"schema": schema, "writer": "invalid", "reader": reader, "rule": rule})

    seal_vectors(valid)
    seal_vectors_more(valid)

    if (ROOT / "checkpoints").exists():
        shutil.rmtree(ROOT / "checkpoints")
    for name, doc, writer, reader, problems, rule in checkpoints():
        write_json(ROOT / "checkpoints" / name / "document.json", doc)
        expected = {"schema": "checkpoint", "writer": writer, "reader": reader, "rule": rule}
        if problems is not None:
            expected["problems"] = problems
        write_json(ROOT / "checkpoints" / name / "expected.json", expected)

    vectors = [(run, case, path, trial) for run, case, path, trial in [
        ("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage", None),
        ("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage/helpfulness", None),
        ("r-1", "case-3", "booking", 0),
        ("r-1", "case-3", "booking", 12),
        ("run-7", "casé-été ラン", "مرحبا/—", None),
        ("run-7", "case-3", "booking", 3.0),
    ]]
    write_json(ROOT / "result-ids.json", [
        {"runId": r, "caseId": c, "path": p, "trial": t, "resultId": result_id(r, c, p, t)} for r, c, p, t in vectors])


if __name__ == "__main__":
    main()
