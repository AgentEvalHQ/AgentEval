#!/usr/bin/env python3
"""Builds the AEF v2 conformance corpus (contracts/aef/v2/conformance/) from the definitions below.

This script is one implementation of the AEF v2 rules it writes (result ids, the seal); the .NET conformance tests
are another and recompute every value. Re-running it rewrites the corpus byte for byte: commit both together.

Usage: python contracts/aef/tools/build_conformance.py
"""
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "v2" / "conformance"
V = "2.0"


# ---------------------------------------------------------------------------- rules

def result_id(run_id, case_id, path, trial=None):
    """r_ + the first 32 hex of SHA-256 over runId, caseId, path and trial joined by U+001F (no trial: empty)."""
    text = "\u001f".join([run_id, case_id, path, "" if trial is None else str(trial)])
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


def seal(run_dir, run, sealed_by, closed_at):
    text = manifest(run_dir)
    statement = {
        "_type": "https://in-toto.io/Statement/v1",
        "subject": [
            {"name": rel, "digest": {"sha256": hashlib.sha256((run_dir / rel).read_bytes()).hexdigest()}}
            for rel in sealed_files(run_dir)
        ],
        "predicateType": "https://agenteval.dev/evidence/v2",
        "predicate": {
            "runId": run["runId"],
            "runHash": hashlib.sha256(text.encode("utf-8")).hexdigest(),
            "producer": {"name": run["producer"]["name"], "version": run["producer"]["version"]},
            "subject": {k: run["subject"][k] for k in ("ref", "version") if k in run["subject"]},
            "deployment": {"ref": run["deployment"]["ref"]} if "deployment" in run else None,
            "suite": {k: run["suite"][k] for k in ("ref", "version", "digest") if k in run["suite"]} if "suite" in run else None,
            "judges": [{k: j[k] for k in ("model", "rubricDigest") if k in j} for j in run.get("judges", [])],
            "closedAt": closed_at,
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
            "predicate": {"runId": run_id, "batch": n, "offset": offset, "length": len(chunk), "previous": previous},
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
        "contentCapture": "hashes-only",
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
                         "unmeasured": {"skipped": 0, "inapplicable": 1, "errored": 0}, "decisive": [helpful]},
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
    seal(run_dir, run, "producer", "2026-10-02T14:06:23.004Z")
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
    seal(run_dir, run, "ingest", "2026-10-03T09:00:05Z")


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
        ("seal-wrong-predicate-type", "seal",
         {"_type": "https://in-toto.io/Statement/v1", "subject": [{"name": "run.json", "digest": {"sha256": "0" * 64}}],
          "predicateType": "https://slsa.dev/provenance/v1",
          "predicate": {"runId": "r-1", "runHash": "0" * 64, "producer": {"name": "p", "version": "1"},
                        "subject": {"ref": "agent:a/b"}, "closedAt": "2026-10-01T00:00:00Z", "sealedBy": "producer"}},
         "invalid", "an AEF seal's predicate type is https://agenteval.dev/evidence/v2"),
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


def main():
    if ROOT.exists():
        shutil.rmtree(ROOT)
    valid = ROOT / "valid"
    completed_eval(valid / "completed-eval")
    aborted_early(valid / "aborted-early")
    running_trials(valid / "running-trials")

    for name, schema, doc, reader, rule in invalid_cases():
        write_json(ROOT / "invalid" / name / "document.json", doc)
        write_json(ROOT / "invalid" / name / "expected.json", {"schema": schema, "writer": "invalid", "reader": reader, "rule": rule})

    seal_vectors(valid)

    vectors = [(run, case, path, trial) for run, case, path, trial in [
        ("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage", None),
        ("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage/helpfulness", None),
        ("r-1", "case-3", "booking", 0),
        ("r-1", "case-3", "booking", 12),
        ("ラン-1", "casé-été", "مرحبا/—", None),
    ]]
    write_json(ROOT / "result-ids.json", [
        {"runId": r, "caseId": c, "path": p, "trial": t, "resultId": result_id(r, c, p, t)} for r, c, p, t in vectors])


if __name__ == "__main__":
    main()
