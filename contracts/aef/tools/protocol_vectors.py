#!/usr/bin/env python3
"""Writes contracts/aef/v2/conformance/protocol/: run plans and runner manifests (valid and invalid), plan-to-runner
matching, and runner event streams with the problems a verifier must report. Every expectation below is written by
hand from v2/README.md ('Run plans and runners', 'The event stream'), never computed by an implementation. (The plan
digest in each job.accepted is data, not an expectation: the SHA-256 of the plan file's bytes.)

Usage: python contracts/aef/tools/protocol_vectors.py
"""
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "v2" / "conformance" / "protocol"
V = "2.0"


def dumps(obj):
    return (json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def write_json(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(dumps(obj))


def write_ndjson(path, objs):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes("".join(json.dumps(o, ensure_ascii=False, separators=(",", ":")) + "\n" for o in objs).encode("utf-8"))


CREDENTIALS = [
    {"name": "AZURE_OPENAI_API_KEY", "scheme": "env", "path": "AZURE_OPENAI_API_KEY", "purpose": "judge"},
    {"name": "SUPPORT_API_KEY", "scheme": "keychain", "path": "agenteval/support-dev", "purpose": "subject"},
]

PLAN = {
    "schemaVersion": V, "planId": "plan-41", "checkpointId": "cp_01J9K4",
    "subject": {"ref": "agent:support/support-triage", "version": "git:3f2a1c", "endpoint": "http://localhost:5080/v1"},
    "suites": [{"ref": "suite:support/triage-scenarios", "version": "4", "lane": "quality"},
               {"ref": "suite:owasp/llm-top10", "version": "2026.1", "lane": "security"}],
    "limits": {"maxUsd": 3.0, "cases": 200, "timeout": "PT2H"},
    "contentPolicy": "off", "isolation": "process", "provider": "local",
    "judges": [{"model": "gpt-5.1", "provider": "azure.ai.openai", "rubricDigest": "sha256:" + "a9" * 32}],
    "baseline": {"policy": "latest-sealed-on-main"},
    "comparability": {"required": ["judge.modelId", "judge.rubricDigest"]},
    "credentialRefs": CREDENTIALS,
    "runnerSelector": ["os:linux"],
}
SMALL = dict(PLAN, planId="plan-42", limits={"maxUsd": 3.0, "cases": 2, "timeout": "PT1M"})

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
         dict(PLAN, isolation="container", provider="docker",
              subject=dict(PLAN["subject"], image="sha256:" + "9d" * 32, repository="ghcr.io/example/support-triage")),
         "valid", "valid", "a container run of an image manifest digest"),
        ("plans", "latest-is-not-a-version", "run-plan", dict(PLAN, subject=dict(PLAN["subject"], version="LATEST")),
         "invalid", "invalid", "'latest' in any case is resolved before the plan exists"),
        ("plans", "container-without-image", "run-plan", dict(PLAN, isolation="container", provider="docker"),
         "invalid", "invalid", "a container run evaluates an already-built image, named by digest"),
        ("plans", "remote-zone-without-zone", "run-plan", dict(PLAN, isolation="remote-zone"), "invalid", "invalid",
         "a remote-zone run names its zone"),
        ("plans", "secret-value", "run-plan", dict(PLAN, credentialRefs=["sk-live-0123456789abcdef"]),
         "invalid", "invalid", "a credential is a reference with a name and a purpose, never a bare value"),
        ("plans", "credentials-in-endpoint", "run-plan",
         dict(PLAN, subject=dict(PLAN["subject"], endpoint="https://user:hunter2@api.example.com/v1")),
         "invalid", "invalid", "an endpoint never carries credentials"),
        ("plans", "unknown-provider", "run-plan", dict(PLAN, provider="podman"), "invalid", "valid",
         "a provider this version does not know: the writer refuses it, a reader takes it as other"),
        ("plans", "no-budget", "run-plan", dict(PLAN, limits={"cases": 10}), "invalid", "invalid", "a plan always caps spend"),
        ("runners", "valid", "runner", RUNNER, "valid", "valid", "a local runner"),
        ("runners", "no-provider", "runner", dict(RUNNER, providers=[]), "invalid", "invalid", "a runner supports at least one provider"),
        ("runners", "unknown-os", "runner", dict(RUNNER, os="plan9"), "invalid", "valid",
         "an os this version does not know: the writer refuses it, a reader accepts it as other"),
    ]


def matching():
    """(name, plan, runner, matches, why)."""
    zone_plan = dict(PLAN, isolation="remote-zone", zone="eu-1", provider="k8s")
    return [
        ("tags-and-provider", PLAN, RUNNER, True, "it carries every selector tag and supports the plan's provider"),
        ("missing-tag", dict(PLAN, runnerSelector=["os:linux", "gpu:a100"]), RUNNER, False, "it lacks a selector tag"),
        ("provider-not-supported", dict(PLAN, provider="k8s"), RUNNER, False, "it does not support the plan's provider"),
        ("zone-differs", zone_plan, dict(RUNNER, kind="remote", providers=["k8s"], networkZone="us-1"), False,
         "a remote-zone plan runs only in its zone"),
        ("zone-matches", zone_plan, dict(RUNNER, kind="remote", providers=["k8s"], networkZone="eu-1"), True,
         "same zone, provider supported, tags carried"),
    ]


def ev(seq, kind, at, **fields):
    return {"schemaVersion": V, "seq": seq, "kind": kind, "jobId": "job-7", "at": at, **fields}


T = "2026-10-08T12:00:{:02d}Z"
RUN_HASH = "5ad6d0d7e4dbb218bd5503ec4448bd88c8542f18740e0074c4abed6156484985"
OTHER_HASH = "1406e38d556046d439c0218e150c737fe737a650f9dfd5f988a0c55d24f8bff4"


def streams(digest, small_digest):
    """(name, plan file, events, problems as (where, problem) in reporting order, reader only)."""
    accepted = ev(1, "job.accepted", T.format(0), planId="plan-41", planDigest=digest, runnerId="runner-local-01")
    small = ev(1, "job.accepted", T.format(0), planId="plan-42", planDigest=small_digest, runnerId="runner-local-01")
    sealed = [
        accepted,
        ev(2, "plan.estimated", T.format(1), cases=2, usdLow=0.5, usdHigh=1.5, priceTable="2026-09-30"),
        ev(3, "spend.updated", T.format(2), spentUsd=0.4),
        ev(4, "case.completed", T.format(3), caseId="case-17", state="failed"),
        ev(5, "case.completed", T.format(4), caseId="case-18", state="not_measured"),
        ev(6, "spend.updated", T.format(5), spentUsd=1.1),
        ev(7, "lane.completed", T.format(6), lane="quality", status="failed"),
        ev(8, "evidence.produced", T.format(7), runId="R-1", runHash=RUN_HASH),
        ev(9, "job.sealed", T.format(8), runs=["R-1"]),
    ]
    cancel = lambda n, at: ev(n, "job.cancelled", T.format(at), reason="Cancelled by the approver.")
    p, s = "plan.json", "plan-small.json"
    return [
        ("complete-sealed", p, sealed, [], False),
        ("refused", p, [ev(1, "job.refused", T.format(0), planId="plan-41", planDigest=digest, runnerId="runner-local-01",
                           reason="The estimate's low end is above maxUsd.")], [], False),
        ("stopped-at-budget", p, [accepted, ev(2, "evidence.produced", T.format(1), runId="R-1", runHash=RUN_HASH),
                                  ev(3, "spend.updated", T.format(2), spentUsd=2.9),
                                  ev(4, "job.failed", T.format(3), reason="The next case would pass the $3.00 limit.", limit="maxUsd", runs=["R-1"])],
         [], False),
        ("spend-at-budget", p, [accepted, ev(2, "spend.updated", T.format(1), spentUsd=3.0), cancel(3, 2)], [], False),
        ("cancelled", p, [accepted, cancel(2, 1)], [], False),
        ("over-budget", p, [accepted, ev(2, "spend.updated", T.format(1), spentUsd=3.4), cancel(3, 2)], [("event:2", "over-budget")], False),
        ("sequence-gap-then-on", p, [accepted, ev(2, "spend.updated", T.format(1), spentUsd=0.1),
                                     ev(4, "spend.updated", T.format(2), spentUsd=0.2), ev(5, "spend.updated", T.format(3), spentUsd=0.3),
                                     cancel(6, 4)], [("event:3", "seq")], False),
        ("after-terminal", p, [accepted, cancel(2, 1), ev(3, "spend.updated", T.format(2), spentUsd=0.2)], [("event:3", "after-terminal")], False),
        ("no-terminal", p, [accepted, ev(2, "spend.updated", T.format(1), spentUsd=0.2)], [("stream", "no-terminal")], False),
        ("spend-decreased-once", p, [accepted, ev(2, "spend.updated", T.format(1), spentUsd=0.9),
                                     ev(3, "spend.updated", T.format(2), spentUsd=0.5), ev(4, "spend.updated", T.format(3), spentUsd=0.7),
                                     cancel(5, 4)], [("event:3", "spend-decreased")], False),
        ("unannounced-run", p, sealed[:8] + [ev(9, "job.sealed", T.format(8), runs=["R-1", "R-2"])], [("event:9", "unannounced-run")], False),
        ("announced-not-sealed", p, sealed[:8] + [ev(9, "evidence.produced", T.format(8), runId="R-2", runHash=OTHER_HASH),
                                                  ev(10, "job.sealed", T.format(9), runs=["R-1"])], [("event:10", "unsealed-run")], False),
        ("run-hash-changed", p, sealed[:8] + [ev(9, "evidence.produced", T.format(8), runId="R-1", runHash=OTHER_HASH),
                                              ev(10, "job.sealed", T.format(9), runs=["R-1"])], [("event:9", "run-hash-changed")], False),
        ("first-not-accepted", p, [ev(1, "spend.updated", T.format(0), spentUsd=0.1), cancel(2, 1)], [("event:1", "first")], False),
        ("other-job", p, [accepted, dict(ev(2, "spend.updated", T.format(1), spentUsd=0.1), jobId="job-8"),
                          dict(cancel(3, 2), jobId="job-8")], [("event:2", "job-id"), ("event:3", "job-id")], False),
        ("other-plan", p, [dict(accepted, planId="plan-40"), cancel(2, 1)], [("event:1", "plan-id")], False),
        ("plan-changed", p, [dict(accepted, planDigest="0" * 64), cancel(2, 1)], [("event:1", "plan-digest")], False),
        ("accepted-twice", p, [accepted, dict(accepted, seq=2, at=T.format(1)), cancel(3, 2)], [("event:2", "accepted-twice")], False),
        ("estimate-inverted", p, [accepted, ev(2, "plan.estimated", T.format(1), cases=3, usdLow=2.0, usdHigh=1.0), cancel(3, 2)],
         [("event:2", "estimate")], False),
        ("time-backwards", p, [accepted, ev(2, "spend.updated", T.format(5), spentUsd=0.1), cancel(3, 4)], [("event:3", "time")], False),
        ("time-backwards-by-a-nanosecond", p, [accepted, ev(2, "spend.updated", "2026-10-08T12:00:05.000000001Z", spentUsd=0.1),
                                              ev(3, "spend.updated", "2026-10-08T12:00:05Z", spentUsd=0.2), cancel(4, 6)],
         [("event:3", "time")], False),
        ("over-cases", s, [small, ev(2, "case.completed", T.format(1), caseId="c1", state="passed"),
                           ev(3, "case.completed", T.format(2), caseId="c2", state="passed"),
                           ev(4, "case.completed", T.format(3), caseId="c3", state="passed"), cancel(5, 4)], [("event:4", "over-cases")], False),
        ("over-time", s, [small, ev(2, "spend.updated", "2026-10-08T12:01:00Z", spentUsd=0.1),
                          ev(3, "spend.updated", "2026-10-08T12:01:01Z", spentUsd=0.2), ev(4, "job.cancelled", "2026-10-08T12:01:02Z", reason="x")],
         [("event:3", "over-time")], False),
        ("unknown-kind-mid-stream", p, [accepted, ev(2, "job.paused", T.format(1)), cancel(3, 2)], [], True),
    ]


def main():
    if ROOT.exists():
        shutil.rmtree(ROOT)
    for folder, name, schema, doc, writer, reader, why in documents():
        write_json(ROOT / folder / name / "document.json", doc)
        write_json(ROOT / folder / name / "expected.json", {"schema": schema, "writer": writer, "reader": reader, "rule": why})
    for name, plan, runner, matches, why in matching():
        write_json(ROOT / "matching" / name / "plan.json", plan)
        write_json(ROOT / "matching" / name / "runner.json", runner)
        write_json(ROOT / "matching" / name / "expected.json", {"matches": matches, "why": why})
    write_json(ROOT / "streams" / "plan.json", PLAN)
    write_json(ROOT / "streams" / "plan-small.json", SMALL)
    digest = hashlib.sha256(dumps(PLAN)).hexdigest()
    small_digest = hashlib.sha256(dumps(SMALL)).hexdigest()
    for name, plan, events, problems, reader_only in streams(digest, small_digest):
        write_ndjson(ROOT / "streams" / name / "events.ndjson", events)
        expected = {"plan": f"../{plan}", "problems": [{"where": w, "problem": p} for w, p in problems]}
        if reader_only:
            expected["readerOnly"] = True
        write_json(ROOT / "streams" / name / "expected.json", expected)


if __name__ == "__main__":
    main()
