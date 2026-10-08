#!/usr/bin/env python3
"""Writes contracts/aef/v2/conformance/protocol/: run plans and runner manifests (valid and invalid), and runner event
streams with the problems a verifier must report. Every expectation below is written by hand from v2/README.md
('Run plans and runners', 'The event stream'), never computed by an implementation.

Usage: python contracts/aef/tools/protocol_vectors.py
"""
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "v2" / "conformance" / "protocol"
V = "2.0"


def write_json(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


def write_ndjson(path, objs):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes("".join(json.dumps(o, ensure_ascii=False, separators=(",", ":")) + "\n" for o in objs).encode("utf-8"))


PLAN = {
    "schemaVersion": V, "planId": "plan-41", "checkpointId": "cp_01J9K4",
    "subject": {"ref": "agent:support/support-triage", "version": "git:3f2a1c", "endpoint": "http://localhost:5080/v1"},
    "suites": [{"ref": "suite:support/triage-scenarios", "version": "4", "lane": "quality"},
               {"ref": "suite:owasp/llm-top10", "version": "2026.1", "lane": "security"}],
    "limits": {"maxUsd": 3.0, "cases": 200, "timeout": "PT2H"},
    "contentPolicy": "off", "isolation": "process", "provider": "local",
    "credentialRefs": ["env:AZURE_OPENAI_API_KEY", "keychain:agenteval/support-dev"],
    "runnerSelector": ["os:linux"],
}

RUNNER = {
    "schemaVersion": V, "runnerId": "runner-local-01", "identity": {"workloadId": "git:owner@example.com"},
    "kind": "local", "os": "linux", "runtime": {"name": "dotnet", "version": "10.0.4"},
    "providers": ["local", "docker"], "tags": ["os:linux", "gpu:none"], "gpu": False, "version": "0.44.0",
}


def documents():
    """(folder, name, schema, document, writer verdict, reader verdict, why)."""
    return [
        ("plans", "valid-local", "run-plan", PLAN, "valid", "valid", "a local process run"),
        ("plans", "valid-container", "run-plan",
         dict(PLAN, isolation="container", provider="docker", subject=dict(PLAN["subject"], image="sha256:" + "9d" * 32)),
         "valid", "valid", "a container run of an image digest"),
        ("plans", "latest-is-not-a-version", "run-plan", dict(PLAN, subject=dict(PLAN["subject"], version=" Latest ")),
         "invalid", "invalid", "'latest' is resolved before the plan exists, in any spelling"),
        ("plans", "container-without-image", "run-plan", dict(PLAN, isolation="container", provider="docker"),
         "invalid", "invalid", "a container run evaluates an already-built image, named by digest"),
        ("plans", "secret-value", "run-plan", dict(PLAN, credentialRefs=["sk-live-0123456789abcdef"]),
         "invalid", "invalid", "a plan holds credential references, never a secret value"),
        ("plans", "no-budget", "run-plan", dict(PLAN, limits={"cases": 10}), "invalid", "invalid",
         "a plan always caps spend"),
        ("runners", "valid", "runner", RUNNER, "valid", "valid", "a local runner"),
        ("runners", "no-provider", "runner", dict(RUNNER, providers=[]), "invalid", "invalid", "a runner supports at least one provider"),
        ("runners", "unknown-os", "runner", dict(RUNNER, os="plan9"), "invalid", "valid",
         "an os this version does not know: the writer refuses it, a reader accepts it as other"),
    ]


def ev(seq, kind, at, **fields):
    return {"schemaVersion": V, "seq": seq, "kind": kind, "jobId": "job-7", "at": at, **fields}


T = "2026-10-08T12:00:{:02d}Z"
ACCEPTED = ev(1, "job.accepted", T.format(0), planId="plan-41", runnerId="runner-local-01")
RUN_HASH = "5ad6d0d7e4dbb218bd5503ec4448bd88c8542f18740e0074c4abed6156484985"


def streams():
    """(name, events, problems): problems as (where, problem) in the order a verifier reports them."""
    sealed = [
        ACCEPTED,
        ev(2, "plan.estimated", T.format(1), cases=2, usdLow=0.5, usdHigh=1.5, priceTable="2026-09-30"),
        ev(3, "spend.updated", T.format(2), spentUsd=0.4),
        ev(4, "case.completed", T.format(3), caseId="case-17", state="failed"),
        ev(5, "case.completed", T.format(4), caseId="case-18", state="not_measured"),
        ev(6, "spend.updated", T.format(5), spentUsd=1.1),
        ev(7, "lane.completed", T.format(6), lane="quality", status="failed"),
        ev(8, "evidence.produced", T.format(7), runId="R-1", runHash=RUN_HASH),
        ev(9, "job.sealed", T.format(8), runs=["R-1"]),
    ]
    return [
        ("complete-sealed", sealed, []),
        ("stopped-at-budget", [ACCEPTED, ev(2, "spend.updated", T.format(1), spentUsd=2.9),
                               ev(3, "job.failed", T.format(2), reason="The next case would pass the $3.00 limit.", limit="maxUsd")], []),
        ("cancelled", [ACCEPTED, ev(2, "job.cancelled", T.format(1), reason="Cancelled by the approver.")], []),
        ("over-budget", [ACCEPTED, ev(2, "spend.updated", T.format(1), spentUsd=3.4),
                         ev(3, "job.failed", T.format(2), reason="x")], [("event:2", "over-budget")]),
        ("sequence-gap", [ACCEPTED, ev(2, "spend.updated", T.format(1), spentUsd=0.1),
                          ev(4, "job.cancelled", T.format(2), reason="x")], [("event:3", "seq")]),
        ("after-terminal", [ACCEPTED, ev(2, "job.cancelled", T.format(1), reason="x"),
                            ev(3, "spend.updated", T.format(2), spentUsd=0.2)], [("event:3", "after-terminal")]),
        ("no-terminal", [ACCEPTED, ev(2, "spend.updated", T.format(1), spentUsd=0.2)], [("stream", "no-terminal")]),
        ("spend-decreased", [ACCEPTED, ev(2, "spend.updated", T.format(1), spentUsd=0.9),
                             ev(3, "spend.updated", T.format(2), spentUsd=0.5),
                             ev(4, "job.cancelled", T.format(3), reason="x")], [("event:3", "spend-decreased")]),
        ("unannounced-run", sealed[:8] + [ev(9, "job.sealed", T.format(8), runs=["R-1", "R-2"])], [("event:9", "unannounced-run")]),
        ("first-not-accepted", [ev(1, "spend.updated", T.format(0), spentUsd=0.1),
                                ev(2, "job.cancelled", T.format(1), reason="x")], [("event:1", "first")]),
        ("other-job", [ACCEPTED, dict(ev(2, "job.cancelled", T.format(1), reason="x"), jobId="job-8")], [("event:2", "job-id")]),
        ("other-plan", [dict(ACCEPTED, planId="plan-40"), ev(2, "job.cancelled", T.format(1), reason="x")], [("event:1", "plan-id")]),
        ("time-backwards", [ACCEPTED, ev(2, "spend.updated", T.format(5), spentUsd=0.1),
                            ev(3, "job.cancelled", T.format(4), reason="x")], [("event:3", "time")]),
    ]


def main():
    if ROOT.exists():
        shutil.rmtree(ROOT)
    for folder, name, schema, doc, writer, reader, why in documents():
        write_json(ROOT / folder / name / "document.json", doc)
        write_json(ROOT / folder / name / "expected.json", {"schema": schema, "writer": writer, "reader": reader, "rule": why})
    write_json(ROOT / "streams" / "plan.json", PLAN)
    for name, events, problems in streams():
        write_ndjson(ROOT / "streams" / name / "events.ndjson", events)
        write_json(ROOT / "streams" / name / "expected.json",
                   {"plan": "../plan.json", "problems": [{"where": w, "problem": p} for w, p in problems]})


if __name__ == "__main__":
    main()
