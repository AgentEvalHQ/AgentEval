#!/usr/bin/env python3
"""Writes contracts/aef/1/conformance/protocol/: run plans and runner manifests (valid and invalid), plan-to-runner
matching, runner event streams with the problems a stream verifier must report ([STRM-3]), and streams with the runs
they name and the problems of [STRM-4]. Every expectation below (a verdict, a match, a problem list, the rule ids a
vector concerns) is written by hand from 1/spec/06-runners.md, never computed by an implementation.

Some data is computed, as data: the plan digest in each job.accepted and in each run's provenance is the SHA-256 of
the plan file's bytes; the runs are made with build_conformance's helpers (result ids, seals, overlay batches); a
redaction's batch is signed with a test key of signature_vectors.py (never trust it); the run hash each
evidence.produced announces is the run's.

Usage: python contracts/aef/tools/protocol_vectors.py   (vector formats: tools/aef_stream.py)
"""
import hashlib
import json
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_conformance import ndjson_bytes, overlay_batches, result_id, run_hash, seal, write_bytes, write_json, write_ndjson  # noqa: E402


def dumps_line(obj):
    return ndjson_bytes([obj])
from signature_vectors import ID, INTOTO, KA, WHO, envelope, policy, sig  # noqa: E402  (test keys: never trust them)

ROOT = Path(__file__).resolve().parents[1] / "1" / "conformance" / "protocol"
V = "1.0"


def dumps(obj):
    """The bytes write_json writes: what a plan digest is computed over."""
    return (json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


CREDENTIALS = [
    {"name": "AZURE_OPENAI_API_KEY", "scheme": "env", "path": "AZURE_OPENAI_API_KEY", "purpose": "judge"},
    {"name": "SUPPORT_API_KEY", "scheme": "keychain", "path": "agenteval/support-dev", "purpose": "subject"},
]
SUBJECT = "agent:support/support-triage"
RUBRIC = "sha256:" + "a9" * 32

PLAN = {
    "schemaVersion": V, "planId": "plan-41", "checkpointId": "cp_01J9K4",
    "subject": {"ref": SUBJECT, "version": "git:3f2a1c", "endpoint": "http://localhost:5080/v1"},
    "suites": [{"ref": "suite:support/triage-scenarios", "version": "4", "lane": "quality"},
               {"ref": "suite:owasp/llm-top10", "version": "2026.1", "lane": "security"}],
    "limits": {"maxUsd": 3.0, "cases": 200, "timeout": "PT2H"},
    "contentCapture": "off", "isolation": "process", "provider": "local",
    "judges": [{"model": "gpt-5.1", "provider": "azure.ai.openai", "rubricDigest": RUBRIC}],
    "baseline": {"policy": "latest-sealed-on-main"},
    "comparability": {"required": ["judges", "rubrics"]},
    "credentialRefs": CREDENTIALS,
    "runnerSelector": ["os:linux"],
}
SMALL = dict(PLAN, planId="plan-42", limits={"maxUsd": 3.0, "cases": 2, "timeout": "PT1M"})
DAY = dict(PLAN, planId="plan-46", limits={"maxUsd": 3.0, "timeout": "P1D"})  # a timeout in days (ENC-9)

RUNNER = {
    "schemaVersion": V, "runnerId": "runner-local-01", "identity": {"workloadId": "git:owner@example.com"},
    "kind": "local", "os": "linux", "runtime": {"name": "dotnet", "version": "10.0.4"},
    "providers": ["local", "docker"], "tags": ["os:linux", "gpu:none"], "gpu": False, "version": "0.44.0",
}


def documents():
    """(folder, name, schema, document, writer verdict, reader verdict, rules, why)."""
    return [
        ("plans", "valid-local", "run-plan", PLAN, "valid", "valid", ["PLAN-1", "PLAN-2", "PLAN-3"],
         "a local process run: an exact subject version, a budget, credentials as references"),
        ("plans", "valid-container", "run-plan",
         dict(PLAN, isolation="container", provider="docker",
              subject=dict(PLAN["subject"], image="sha256:" + "9d" * 32, repository="ghcr.io/example/support-triage")),
         "valid", "valid", ["PLAN-1"], "a container run of an image manifest digest"),
        ("plans", "latest-is-not-a-version", "run-plan", dict(PLAN, subject=dict(PLAN["subject"], version="LATEST")),
         "invalid", "invalid", ["PLAN-1"], "'latest' in any case is resolved before the plan exists"),
        ("plans", "container-without-image", "run-plan", dict(PLAN, isolation="container", provider="docker"),
         "invalid", "invalid", ["PLAN-1"], "a container run evaluates an already-built image, named by digest"),
        ("plans", "remote-zone-without-zone", "run-plan", dict(PLAN, isolation="remote-zone"), "invalid", "invalid",
         ["PLAN-7"], "a remote-zone run names its zone: the zone a runner must be in to take the plan"),
        ("plans", "secret-value", "run-plan", dict(PLAN, credentialRefs=["sk-live-0123456789abcdef"]),
         "invalid", "invalid", ["PLAN-3", "PLAN-4"],
         "a credential is a reference with a name and a purpose, never a bare value"),
        ("plans", "credentials-in-endpoint", "run-plan",
         dict(PLAN, subject=dict(PLAN["subject"], endpoint="https://user:hunter2@api.example.com/v1")),
         "invalid", "invalid", ["PLAN-1", "PLAN-4"], "an endpoint never carries credentials"),
        ("plans", "endpoint-with-query", "run-plan",
         dict(PLAN, subject=dict(PLAN["subject"], endpoint="https://api.example.com/v1?api-key=sk-live-0123456789abcdef")),
         "invalid", "invalid", ["PLAN-1", "PLAN-4", "RUN-10"],
         "an endpoint is scheme, host and path only, as in run.json: a query string is where a key hides"),
        ("plans", "endpoint-with-fragment", "run-plan",
         dict(PLAN, subject=dict(PLAN["subject"], endpoint="https://api.example.com/v1#sk-live-0123456789abcdef")),
         "invalid", "invalid", ["PLAN-1", "PLAN-4", "RUN-10"], "an endpoint has no fragment either"),
        ("plans", "unknown-provider", "run-plan", dict(PLAN, provider="podman"), "invalid", "valid", ["PLAN-7", "VER-8"],
         "a provider this version does not know: the writer refuses it, a reader takes it, and a runner refuses the plan"),
        ("plans", "no-budget", "run-plan", dict(PLAN, limits={"cases": 10}), "invalid", "invalid", ["PLAN-2"],
         "a plan always caps spend"),
        ("plans", "valid-timeout-in-days", "run-plan", dict(PLAN, limits={"maxUsd": 3.0, "timeout": "P1D"}), "valid", "valid",
         ["PLAN-2", "ENC-9"], "a timeout is a duration (ENC-9): days, hours and minutes, like a freshness"),
        ("plans", "timeout-in-seconds", "run-plan", dict(PLAN, limits={"maxUsd": 3.0, "timeout": "PT30S"}), "invalid", "invalid",
         ["PLAN-2", "ENC-9"], "a duration has no seconds"),
        ("plans", "unknown-isolation", "run-plan", dict(PLAN, isolation="vm"), "invalid", "valid", ["PLAN-7", "VER-8"],
         "an isolation this version does not know: the writer refuses it, a reader takes it, and a runner refuses the plan"),
        ("plans", "unknown-credential-scheme", "run-plan",
         dict(PLAN, credentialRefs=[dict(CREDENTIALS[0], scheme="hsm"), CREDENTIALS[1]]), "invalid", "valid",
         ["PLAN-3", "PLAN-7", "VER-8"],
         "a credential scheme this version does not know: a reader takes the plan, and a runner refuses it"),
        ("plans", "unknown-credential-purpose", "run-plan",
         dict(PLAN, credentialRefs=[CREDENTIALS[0], dict(CREDENTIALS[1], purpose="observer")]), "invalid", "valid",
         ["PLAN-3", "PLAN-7", "VER-8"],
         "a credential purpose this version does not know: a reader takes the plan, and a runner refuses it"),
        ("plans", "valid-ci-provider", "run-plan", dict(PLAN, provider="ci:github"), "valid", "valid", ["PLAN-1", "VER-8"],
         "a ci:<name> provider is known: the writer schema accepts it through its pattern, so a reader keeps it"),
        ("plans", "unknown-content-capture", "run-plan", dict(PLAN, contentCapture="partial"), "invalid", "valid",
         ["PLAN-7", "VER-8"], "a content capture this version does not know: a reader takes the plan, and a runner refuses it"),
        ("plans", "valid-target-mode-scripted", "run-plan", dict(PLAN, targetMode="scripted"), "valid", "valid",
         ["PLAN-7", "RUN-7"],
         "a plan that asks for a scripted stand-in: a runner that cannot drive one refuses it, and its runs are checked "
         "against scripted, not live"),
        ("plans", "unknown-target-mode", "run-plan", dict(PLAN, targetMode="simulated"), "invalid", "valid",
         ["PLAN-7", "VER-8"], "a target mode this version does not know: a reader takes the plan, and a runner refuses it"),
        ("runners", "valid", "runner", RUNNER, "valid", "valid", ["PLAN-6"], "a local runner"),
        ("runners", "no-provider", "runner", dict(RUNNER, providers=[]), "invalid", "invalid", ["PLAN-6"],
         "a runner supports at least one provider"),
        ("runners", "unknown-os", "runner", dict(RUNNER, os="plan9"), "invalid", "valid", ["PLAN-6", "VER-8"],
         "an os this version does not know: the writer refuses it, a reader accepts it and shows it as written"),
        ("runners", "unknown-kind", "runner", dict(RUNNER, kind="warehouse"), "invalid", "valid", ["PLAN-6", "VER-8"],
         "a runner kind this version does not know: the writer refuses it, a reader accepts it and shows it as written"),
        ("runners", "valid-target-modes", "runner", dict(RUNNER, targetModes=["live", "scripted"]), "valid", "valid",
         ["PLAN-6", "RUN-7"], "a runner that gives the live subject and a scripted stand-in"),
        ("runners", "target-modes-empty", "runner", dict(RUNNER, targetModes=[]), "invalid", "invalid", ["PLAN-6"],
         "a manifest that names target modes names at least one: without the field it gives live only"),
        ("runners", "target-modes-twice", "runner", dict(RUNNER, targetModes=["scripted", "scripted"]), "invalid",
         "invalid", ["PLAN-6"], "a target mode is named once"),
        ("runners", "target-mode-unknown", "runner", dict(RUNNER, targetModes=["scripted", "simulated"]), "invalid",
         "valid", ["PLAN-6", "VER-8"],
         "a target mode this version does not know: the writer refuses it, a reader accepts the manifest (no plan of "
         "this version can ask for it)"),
    ]


# How a reader reads each value of §7.3 a document vector holds (VER-8), as tools/aef_verify.py's `document` command
# reports it in "reads": a plan a runner must refuse reads as "refused"; a runner's kind and os are shown as written.
READS = {
    ("plans", "unknown-provider"): {"provider": "refused"},
    ("plans", "unknown-isolation"): {"isolation": "refused"},
    ("plans", "unknown-credential-scheme"): {"credentialRefs[0].scheme": "refused"},
    ("plans", "unknown-credential-purpose"): {"credentialRefs[1].purpose": "refused"},
    ("plans", "valid-ci-provider"): {"provider": "ci:github"},
    ("plans", "unknown-content-capture"): {"contentCapture": "refused"},
    ("plans", "valid-target-mode-scripted"): {"targetMode": "scripted"},
    ("plans", "unknown-target-mode"): {"targetMode": "refused"},
    ("runners", "unknown-os"): {"os": "plan9"},
    ("runners", "unknown-kind"): {"kind": "warehouse"},
}


def matching():
    """(name, plan, runner, matches, why). Every matching vector concerns PLAN-7."""
    zone_plan = dict(PLAN, isolation="remote-zone", zone="eu-1", provider="k8s")
    return [
        ("tags-and-provider", PLAN, RUNNER, True, "it carries every selector tag and supports the plan's provider"),
        ("missing-tag", dict(PLAN, runnerSelector=["os:linux", "gpu:a100"]), RUNNER, False, "it lacks a selector tag"),
        ("provider-not-supported", dict(PLAN, provider="k8s"), RUNNER, False, "it does not support the plan's provider"),
        ("zone-differs", zone_plan, dict(RUNNER, kind="remote", providers=["k8s"], networkZone="us-1"), False,
         "a remote-zone plan runs only in its zone"),
        ("zone-matches", zone_plan, dict(RUNNER, kind="remote", providers=["k8s"], networkZone="eu-1"), True,
         "same zone, provider supported, tags carried"),
        ("runner-kind-unknown", PLAN, dict(RUNNER, kind="warehouse"), True,
         "a runner kind this version does not know takes no part in matching (VER-8): tags and provider decide"),
        ("isolation-unknown", dict(PLAN, isolation="microvm"), RUNNER, False,
         "a runner does not take a plan whose isolation it does not know, even when it could run it (PLAN-7)"),
        ("provider-unknown-but-listed", dict(PLAN, provider="podman"), dict(RUNNER, providers=["podman", "local"]), False,
         "a runner of this version does not take a provider it does not know, even one its manifest lists (PLAN-7, VER-8)"),
        ("runner-os-unknown", PLAN, dict(RUNNER, os="plan9"), True,
         "a runner os this version does not know takes no part in matching (VER-8): tags and provider decide"),
        ("target-mode-unknown", dict(PLAN, targetMode="simulated"), RUNNER, False,
         "a runner does not take a plan whose target mode it does not know (PLAN-7, VER-8)"),
        ("target-mode-not-in-manifest", dict(PLAN, targetMode="replayed"), RUNNER, False,
         "a manifest without targetModes gives live only, so a runner that names none does not take a plan that asks "
         "to replay the subject (PLAN-6, PLAN-7)"),
        ("scripted-plan-manifest-without-modes", dict(PLAN, targetMode="scripted"), RUNNER, False,
         "a scripted plan, and a manifest without targetModes: it gives live only"),
        ("scripted-plan-scripted-runner", dict(PLAN, targetMode="scripted"), dict(RUNNER, targetModes=["scripted"]),
         True, "a scripted plan, and a runner that gives scripted"),
        ("live-plan-scripted-runner", dict(PLAN, targetMode="live"), dict(RUNNER, targetModes=["scripted"]), False,
         "a plan that asks for live by name, and a runner that gives only a scripted stand-in"),
        ("live-plan-live-and-scripted-runner", dict(PLAN, targetMode="live"),
         dict(RUNNER, targetModes=["live", "scripted"]), True, "a live plan, and a runner that gives live among others"),
        ("plan-without-mode-mocked-runner", PLAN, dict(RUNNER, targetModes=["mocked"]), False,
         "a plan that names no target mode asks for live, and the runner gives only a mock"),
    ]


# Matching vectors whose plan or runner manifest only a reader accepts (a value a later minor may add): their extra
# rule is VER-8.
MATCHING_READER_ONLY = {"runner-kind-unknown", "runner-os-unknown", "isolation-unknown", "provider-unknown-but-listed",
                        "target-mode-unknown"}


def ev(seq, kind, at, **fields):
    return {"schemaVersion": V, "seq": seq, "kind": kind, "jobId": "job-7", "at": at, **fields}


T = "2026-10-08T12:00:{:02d}Z"
RUN_HASH = "5ad6d0d7e4dbb218bd5503ec4448bd88c8542f18740e0074c4abed6156484985"
OTHER_HASH = "1406e38d556046d439c0218e150c737fe737a650f9dfd5f988a0c55d24f8bff4"

# The rules each stream vector concerns.
STREAM_RULES = {
    "complete-sealed": ["STRM-1", "STRM-3"],
    "refused": ["STRM-1", "STRM-3"],
    "stopped-at-budget": ["PLAN-2", "PLAN-9", "STRM-1", "STRM-3"],
    "spend-at-budget": ["PLAN-2", "STRM-3"],
    "cancelled": ["STRM-1", "STRM-3"],
    "over-budget": ["PLAN-2", "STRM-3"],
    "sequence-gap-then-on": ["STRM-1", "STRM-3"],
    "after-terminal": ["STRM-1", "STRM-3"],
    "no-terminal": ["STRM-1", "STRM-3"],
    "last-line-without-lf": ["STRM-2", "STRM-3"],
    "spend-decreased-once": ["STRM-3"],
    "unannounced-run": ["STRM-3"],
    "announced-not-sealed": ["STRM-3"],
    "run-hash-changed": ["STRM-3"],
    "first-not-accepted": ["STRM-3"],
    "other-job": ["STRM-1", "STRM-3"],
    "seq-and-job-id-at-one-event": ["STRM-3", "CONF-2"],
    "event-invalid-mid-stream": ["STRM-3"],
    "event-not-json": ["STRM-3", "ENC-2"],
    "event-duplicate-member": ["STRM-3", "ENC-2"],
    "first-line-invalid": ["STRM-3"],
    "framing-crlf": ["STRM-3", "ENC-5"],
    "other-plan": ["PLAN-5", "STRM-3"],
    "plan-changed": ["PLAN-5", "STRM-3"],
    "accepted-twice": ["STRM-3"],
    "estimate-inverted": ["STRM-3"],
    "time-backwards": ["STRM-3"],
    "time-backwards-by-a-nanosecond": ["ENC-8", "STRM-3"],
    "over-cases": ["PLAN-2", "STRM-3"],
    "over-time": ["PLAN-2", "PLAN-9", "STRM-3"],
    "over-time-in-days": ["PLAN-2", "STRM-3", "ENC-9"],
    "unknown-kind-mid-stream": ["STRM-1", "VER-8"],
    "case-completed-names-run": ["STRM-1", "STRM-3", "PLAN-8"],
}
UNFINISHED = {"last-line-without-lf"}  # its last line has no LF (STRM-2): it is still being written


def streams(digest, small_digest, day_digest):
    """(name, plan file, events, problems as (where, problem) in reporting order, reader only). A vector in
    UNFINISHED is written without the LF after its last event."""
    accepted = ev(1, "job.accepted", T.format(0), planId="plan-41", planDigest=digest, runnerId="runner-local-01")
    small = ev(1, "job.accepted", T.format(0), planId="plan-42", planDigest=small_digest, runnerId="runner-local-01")
    day = ev(1, "job.accepted", T.format(0), planId="plan-46", planDigest=day_digest, runnerId="runner-local-01")
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
    p, s, d = "plan.json", "plan-small.json", "plan-day.json"
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
        ("last-line-without-lf", p, [accepted, ev(2, "spend.updated", T.format(1), spentUsd=0.2), cancel(3, 2)],
         [("stream", "no-terminal")], False),
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
        ("over-time-in-days", d, [day, ev(2, "spend.updated", "2026-10-09T12:00:00Z", spentUsd=0.1),
                                  ev(3, "spend.updated", "2026-10-09T12:00:00.000000001Z", spentUsd=0.2),
                                  ev(4, "job.cancelled", "2026-10-09T12:00:01Z", reason="x")],
         [("event:3", "over-time")], False),
        ("unknown-kind-mid-stream", p, [accepted, ev(2, "job.paused", T.format(1)), cancel(3, 2)], [], True),
        ("case-completed-names-run", p,
         sealed[:3] + [dict(e, runId="R-1") for e in sealed[3:5]] + sealed[5:], [], False),
        ("event-invalid-mid-stream", p, [accepted, json.dumps(dict(ev(2, "spend.updated", T.format(1), spentUsd=0.1), seq="2"),
                                                              separators=(",", ":")).encode(),
                                          ev(3, "spend.updated", T.format(2), spentUsd=0.2), cancel(4, 3)],
         [("event:2", "event-invalid")], False),
        ("event-not-json", p, [accepted, b'{"schemaVersion":"1.0","seq":2,', ev(3, "spend.updated", T.format(2), spentUsd=0.2),
                               cancel(4, 3)], [("event:2", "event-invalid")], False),
        ("event-duplicate-member", p, [accepted, b'{"schemaVersion":"1.0","seq":2,"seq":2,"kind":"spend.updated","jobId":"job-7","at":"2026-10-08T12:00:01Z","spentUsd":0.1}',
                                       ev(3, "spend.updated", T.format(2), spentUsd=0.2), cancel(4, 3)],
         [("event:2", "event-invalid")], False),
        ("first-line-invalid", p, [b"[]", accepted, cancel(2, 1)], [("event:1", "event-invalid")], False),
        ("framing-crlf", p, [accepted, cancel(2, 1)], [("stream", "encoding")], False),
        ("seq-and-job-id-at-one-event", p, [accepted, dict(ev(3, "spend.updated", T.format(1), spentUsd=0.1), jobId="job-8"),
                                            cancel(4, 2)], [("event:2", "job-id"), ("event:2", "seq")], False),
    ]


# ---------------------------------------------------------------------------- STRM-4: the runs a stream names

TRIAGE = {"ref": "suite:support/triage-scenarios", "version": "4", "digest": "sha256:" + "4c" * 32}
SECURITY = {"ref": "suite:owasp/llm-top10", "version": "2026.1"}
# The plan of the STRM-4 vectors: the quality suite is frozen (named by its digest); maxUsd 3.0 and cases 3 for the job.
# Its jobs are accepted at ACCEPTED and end (their terminal event) at TERMINAL; a run starts and ends between them.
ACCEPTED, TERMINAL = "2026-10-08T12:00:00Z", "2026-10-08T12:02:00Z"
DEPLOYMENT = {"ref": "deployment:support/support-triage@dev", "endpoint": PLAN["subject"]["endpoint"]}
STAGING = "deployment:support/support-triage@staging"
# The plan of the STRM-4 vectors names the deployment and its endpoint (PLAN-1).
CPLAN = dict(PLAN, planId="plan-43", subject=dict(PLAN["subject"], deployment=DEPLOYMENT["ref"]),
             suites=[dict(PLAN["suites"][0], digest=TRIAGE["digest"]), PLAN["suites"][1]],
             limits={"maxUsd": 3.0, "cases": 3, "timeout": "PT2H"})
NO_JUDGES = {k: v for k, v in dict(CPLAN, planId="plan-44").items() if k != "judges"}
FOUR_CASES = dict(CPLAN, planId="plan-49", limits={"maxUsd": 3.0, "cases": 4, "timeout": "PT2H"})
CAPTURED = dict(CPLAN, planId="plan-45", contentCapture="on")
# Plans that name their target mode (a plan without one, as CPLAN, asks for live).
SCRIPTED_PLAN = dict(CPLAN, planId="plan-47", targetMode="scripted")
LIVE_PLAN = dict(CPLAN, planId="plan-48", targetMode="live")
SCRIPTED = {"targetMode": "scripted", "stimulus": "suite"}
V6 = "git:2b19e0"  # the subject version before the plan's git:3f2a1c
OTHER_RUBRIC = "sha256:" + "b7" * 32
SCORE = "triage-score"
DROP = object()
TWO_CASES = (("case-17", "composite", 0.4), ("case-18", "plain", 0.9))
ONE_CASE = (("case-17", "composite", 0.4),)
SECURITY_CASE = (("LLM01-001", "plain", 1.0),)
REASONING = b"The reply quoted the customer's street address back to them.\n"
REDACTOR = policy("ecdsa-a", may=("ecdsa-a",))  # the test key ecdsa-a (alice) may authorize redactions


def case_lines(run_id, case, form, score):
    """One case's result lines at path triage. form: 'plain' (one root line), 'composite' (a root line and a child) or
    'trials' (two trial lines and their rollup, all three without a parent)."""
    state = "passed" if score >= 0.8 else "failed"
    verdict = {"state": state, **({} if state == "passed" else {"severity": "medium"}), "scores": [{"metric": SCORE, "value": score}]}
    root = {"schemaVersion": V, "resultId": result_id(run_id, case, "triage"), "caseId": case, "path": "triage",
            "evaluator": {"id": "composite:triage" if form == "composite" else "code:triage"}, **verdict}
    if form == "plain":
        return [root]
    if form == "composite":
        child = {"schemaVersion": V, "resultId": result_id(run_id, case, "triage/escalation"), "parentResultId": root["resultId"],
                 "caseId": case, "path": "triage/escalation", "evaluator": {"id": "code:escalation"}, **verdict,
                 "component": {"weight": 1, "required": True}}
        root["aggregation"] = {"strategy": "Min", "threshold": 0.8, "score": score, "rulePath": "threshold", "measured": 1, "total": 1,
                               "unmeasured": {"not_measured": 0, "not_applicable": 0, "skipped": 0, "error": 0},
                               "decisive": [child["resultId"]]}
        return [root, child]
    trials = [{"schemaVersion": V, "resultId": result_id(run_id, case, "triage", t), "caseId": case, "path": "triage", "trial": t,
               "evaluator": {"id": "code:triage"}, **verdict} for t in (0, 1)]
    root["trials"] = {"n": 2, "passed": 2 if state == "passed" else 0, "aggregation": "MajorityVote", "agree": True}
    return trials + [root]


def make_run(runs, folder, run_id, plan, digest, *, cases=TWO_CASES, lane="quality", cost=1.0, sealed=True, reasoning=None, **over):
    """A small completed run of the plan's subject, as a runner seals it, with run.json changed as over says (DROP
    removes a field): results (the first line citing `reasoning` as a blob, when given), metrics, and a summary that
    follows SUM-3..5 (data, so that the run is intact). Returns its run hash."""
    run_dir = runs / folder
    run = {"schemaVersion": V, "runId": run_id, "status": "completed", "producer": {"name": "agenteval-cli", "version": "1.0.0"},
           "subject": {"ref": SUBJECT, "kind": "agent", "version": "git:3f2a1c"}, "deployment": dict(DEPLOYMENT),
           "suite": dict(TRIAGE),
           "judges": [{"model": "gpt-5.1", "provider": "azure.ai.openai", "mode": "single", "rubricDigest": RUBRIC}],
           "startedAt": "2026-10-08T12:00:10Z", "endedAt": "2026-10-08T12:00:50Z", "contentCapture": "off",
           "execution": {"targetMode": "live", "stimulus": "suite"},
           "provenance": {"planId": plan["planId"], "planDigest": digest, "jobId": "job-7", "runnerId": "runner-local-01"}}
    run.update(over)
    run = {k: v for k, v in run.items() if v is not DROP}
    values = [score for _, _, score in cases]
    total = sum(values)
    lines = [line for case in cases for line in case_lines(run_id, *case)]
    if reasoning is not None:
        blob = hashlib.sha256(reasoning).hexdigest()
        write_bytes(run_dir / "blobs" / "sha256" / blob[:2] / blob, reasoning)
        lines[0]["reasoning"] = {"blob": "sha256:" + blob, "bytes": len(reasoning)}
    write_json(run_dir / "run.json", run)
    write_ndjson(run_dir / "results.ndjson", lines)
    write_json(run_dir / "metrics.json", {"schemaVersion": V, "metrics": [
        {"id": SCORE, "kind": "score", "direction": "higher_better", "scale": {"min": 0, "max": 1}}]})
    write_json(run_dir / "summary.json", {"schemaVersion": V, "runId": run_id, "lanes": [{"lane": lane, "metrics": [
        {"metric": SCORE, "path": "triage", "n": len(values), "N": len(values), "notMeasured": 0, "value": total / len(values),
         "verdict": "passed" if total / len(values) >= 0.8 else "failed", "rule": "triage-score >= 0.8",
         "sum": total, "sumSq": sum(v * v for v in values)}]}],
        **({"cost": {"totalUsd": cost, "source": "provider-billing"}} if cost is not None else {})})
    # Sealed when it closed or at 12:00:55, whichever is later: a seal is never earlier than closedAt (SEAL-1).
    sealed_at = max("2026-10-08T12:00:55Z", run.get("endedAt") or "")  # same date and hour: strings order as times
    return seal(run_dir, run, "producer", sealed_at=sealed_at) if sealed else run_hash(run_dir)


def security_run(runs, run_id, plan, digest, *, cases=SECURITY_CASE, **kw):
    """A run of the plan's second suite (not frozen: the plan names no digest for it)."""
    return make_run(runs, run_id, run_id, plan, digest, cases=cases, lane="security", suite=SECURITY, **kw)


def redact(run_dir, run_id, the_hash, blob):
    """After close: an overlay batch with one redact event for the blob, its batch seal signed by the test key ecdsa-a
    for the event's identity (alice), and the blob deleted (OVL-10)."""
    event = {"schemaVersion": V, "eventId": "ov_0001", "kind": "redact", "target": {"run": run_id, "blob": blob},
             "reason": "The judge's reasoning quoted a customer's address.",
             "by": {"identity": WHO["ecdsa-a"], "assurance": "signed"}, "at": "2026-10-08T13:00:00Z"}
    overlay_batches(run_dir, run_id, [[event]], the_hash)
    batch = (run_dir / "overlays" / "seal-0001.json").read_bytes()
    write_json(run_dir / "overlays" / "seal-0001.dsse.json", envelope(INTOTO, batch, [(ID["ecdsa-a"], sig(INTOTO, batch, KA))]))
    path = run_dir / "blobs" / "sha256" / blob[:2] / blob
    path.unlink()
    for folder in path.parents:  # no empty folder is left behind
        if folder == run_dir or any(folder.iterdir()):
            break
        folder.rmdir()


def job(plan, digest, announced, named, *, failed=False):
    """The stream of a job that keeps STRM-3: accepted at ACCEPTED, one evidence.produced per announced (runId,
    runHash), and a job.sealed (or a job.failed at maxUsd) naming the runs, at TERMINAL."""
    events = [ev(1, "job.accepted", ACCEPTED, planId=plan["planId"], planDigest=digest, runnerId="runner-local-01")]
    for run_id, the_hash in announced:
        events.append(ev(len(events) + 1, "evidence.produced", f"2026-10-08T12:01:{len(events):02d}Z", runId=run_id, runHash=the_hash))
    n = len(events) + 1
    if failed:
        events.append(ev(n, "job.failed", TERMINAL, reason="The next case would pass the $3.00 limit.", limit="maxUsd", runs=named))
    else:
        events.append(ev(n, "job.sealed", TERMINAL, runs=named))
    return events


def one_run(problems, *, failed=False, **over):
    """A job that sealed one run, R-1, made as over says; problems as written for the vector."""
    def build(runs, plan, digest):
        return job(plan, digest, [("R-1", make_run(runs, "R-1", "R-1", plan, digest, **over))], ["R-1"], failed=failed), problems
    return build


def two_runs(problems, first, second):
    """A job that sealed R-1 (the quality suite, made as `first` says) and R-2 (the security suite, as `second` says)."""
    def build(runs, plan, digest):
        h1 = make_run(runs, "R-1", "R-1", plan, digest, **first)
        h2 = security_run(runs, "R-2", plan, digest, **second)
        return job(plan, digest, [("R-1", h1), ("R-2", h2)], ["R-1", "R-2"]), problems
    return build


def v_numeric_run_ids(runs, plan, digest):
    """§3.9: 'run:10' and 'run:9' are not line paths, so they order by their bytes: run:10 first."""
    wrong = {"subject": {"ref": SUBJECT, "kind": "agent", "version": V6}}
    h10 = make_run(runs, "10", "10", plan, digest, **wrong)
    h9 = security_run(runs, "9", plan, digest, **wrong)
    return job(plan, digest, [("9", h9), ("10", h10)], ["9", "10"]), [("run:10", "subject"), ("run:9", "subject")]


def v_run_missing(runs, plan, digest):
    h1 = make_run(runs, "R-1", "R-1", plan, digest)
    return job(plan, digest, [("R-1", h1), ("R-2", OTHER_HASH)], ["R-1", "R-2"]), [("run:R-2", "run-missing")]


def v_resealed(runs, plan, digest):
    announced = make_run(runs, "R-1", "R-1", plan, digest, subject={"ref": SUBJECT, "kind": "agent", "version": V6},
                         contentCapture="on")
    run_dir = runs / "R-1"
    run = json.loads((run_dir / "run.json").read_text(encoding="utf-8"))
    run["subject"]["version"] = "git:3f2a1c"  # edited after sealing to look like the plan's subject, and sealed again
    write_json(run_dir / "run.json", run)
    assert seal(run_dir, run, "producer", sealed_at="2026-10-08T13:00:00Z") != announced
    return job(plan, digest, [("R-1", announced)], ["R-1"]), [("run:R-1", "run-hash")]


def v_unsealed(runs, plan, digest):
    the_hash = make_run(runs, "R-1", "R-1", plan, digest, sealed=False)
    return job(plan, digest, [("R-1", the_hash)], ["R-1"]), [("run:R-1", "run-hash")]


def v_two_folders(runs, plan, digest):
    make_run(runs, "earlier", "R-1", plan, digest, subject={"ref": SUBJECT, "kind": "agent", "version": V6})
    announced = make_run(runs, "later", "R-1", plan, digest)
    return job(plan, digest, [("R-1", announced)], ["R-1"]), []


def v_named_twice(runs, plan, digest):
    h1 = make_run(runs, "R-1", "R-1", plan, digest, contentCapture="on", cost=2.0)
    return job(plan, digest, [("R-1", h1)], ["R-1", "R-1"]), [("run:R-1", "content-capture")]


def v_provenance(runs, plan, digest):
    h1 = make_run(runs, "R-1", "R-1", plan, digest,
                  provenance={"planId": plan["planId"], "planDigest": digest, "jobId": "job-6", "runnerId": "runner-local-01"})
    return job(plan, digest, [("R-1", h1)], ["R-1"]), [("run:R-1", "provenance")]


def v_several(runs, plan, digest):
    h1 = make_run(runs, "R-1", "R-1", plan, digest, subject={"ref": SUBJECT, "kind": "agent", "version": V6},
                  judges=[{"model": "gpt-4o-mini", "provider": "openai", "mode": "single", "rubricDigest": RUBRIC}],
                  contentCapture="on", execution={"targetMode": "replayed", "stimulus": "suite"}, cost=2.5,
                  deployment={"ref": STAGING, "endpoint": "http://localhost:5081/v1"},
                  startedAt="2026-10-01T12:00:10Z", endedAt="2026-10-01T12:00:50Z")
    h2 = security_run(runs, "R-2", plan, digest, provenance=DROP)
    return job(plan, digest, [("R-1", h1), ("R-2", h2)], ["R-2", "R-1"]), [
        ("job", "over-budget"),
        ("run:R-1", "content-capture"), ("run:R-1", "deployment"), ("run:R-1", "judges"), ("run:R-1", "subject"),
        ("run:R-1", "target-mode"), ("run:R-1", "time"),
        ("run:R-2", "provenance")]


def v_redacted(problems):
    """R-1 kept the judge's reasoning (the plan captures content); after close, an authorized redaction withheld it."""
    def build(runs, plan, digest):
        the_hash = make_run(runs, "R-1", "R-1", plan, digest, contentCapture="on", reasoning=REASONING)
        redact(runs / "R-1", "R-1", the_hash, hashlib.sha256(REASONING).hexdigest())
        return job(plan, digest, [("R-1", the_hash)], ["R-1"]), problems
    return build


def conformance():
    """(name, plan, build(runs, plan, digest) -> (events, problems), trust policy or None, rules, why). The job's limits
    (CPLAN): maxUsd 3.0, cases 3. Problems are written in the order of STRM-4: by path (UTF-8 bytes: job first), then by
    code."""
    judge = lambda **j: [dict({"model": "gpt-5.1", "provider": "azure.ai.openai", "mode": "single", "rubricDigest": RUBRIC}, **j)]
    return [
        ("valid", CPLAN, two_runs([], dict(cases=ONE_CASE, cost=1.5), {}), None, ["STRM-4", "RUN-12"],
         "two runs, one per suite (the frozen one with the plan's digest), each the plan's subject, judges and capture, "
         "live; the job has 2 cases and cost $2.50"),
        ("at-the-limits", CPLAN,
         two_runs([], dict(cases=ONE_CASE + (("case-19", "trials", 0.8),), cost=2.0, startedAt="2026-10-08T12:00:00.000Z"),
                  dict(endedAt="2026-10-08T12:02:00.000000000Z")), None, ["STRM-4", "PLAN-2", "ENC-8"],
         "the job's cost ($2.00 + $1.00) equals maxUsd, which is within it; its cases are three, though five lines have "
         "no parent (a case's two trials and their rollup) and one line is a child; R-1 starts as the job is accepted "
         "and R-2 ends as it ends, the same instants written otherwise"),
        ("plan-names-no-judges", NO_JUDGES, one_run([]), None, ["STRM-4"],
         "a plan without judges leaves the runner's judges unchecked"),
        ("run-missing", CPLAN, v_run_missing, None, ["STRM-4", "RUN-1"], "R-2 was announced and sealed, but no folder holds it"),
        ("numeric-run-ids", CPLAN, v_numeric_run_ids, None, ["STRM-4", "CONF-2"],
         "runs 9 and 10 of another subject version: run:10 is reported first, by bytes, as it is not a line path"),
        ("run-hash-resealed", CPLAN, v_resealed, None, ["STRM-4", "SEAL-4"],
         "run.json was edited after sealing (to the plan's subject version) and sealed again: intact, but its seal has "
         "another run hash than the one announced; nothing else is checked, so its content capture is not reported"),
        ("run-hash-unsealed", CPLAN, v_unsealed, None, ["STRM-4", "SEAL-4"],
         "no seal.json, so the run hash is recomputed from the files, and it is the one announced; but an unsealed run is "
         "not intact"),
        ("redacted-run", CAPTURED, v_redacted([]), REDACTOR, ["STRM-4", "SEAL-4", "OVL-10"],
         "a blob withheld by a redaction the trust policy authorizes leaves the run intact, with its seal's run hash, the "
         "one announced"),
        ("redacted-run-no-policy", CAPTURED, v_redacted([("run:R-1", "run-hash")]), None, ["STRM-4", "SEAL-4", "OVL-10"],
         "the same run with no trust policy: nothing authorizes the redaction, the blob is missing, the run is not intact"),
        ("two-folders-one-run-id", CPLAN, v_two_folders, None, ["STRM-4", "RUN-1", "RUN-13"],
         "two runs have the runId R-1, so they are different runs: the one in path order first is of an earlier version; "
         "the one with the announced run hash keeps to the plan"),
        ("named-twice", CPLAN, v_named_twice, None, ["STRM-4", "PLAN-2"],
         "job.sealed names R-1 twice: it is checked and reported once, and its $2.00 counts once in the job's cost"),
        ("provenance", CPLAN, v_provenance, None, ["STRM-4", "RUN-12"], "the run names another job of the same plan and runner"),
        ("subject", CPLAN, one_run([("run:R-1", "subject")], subject={"ref": SUBJECT, "kind": "agent", "version": V6}), None,
         ["STRM-4", "PLAN-1"], "a run of another version of the subject"),
        ("suite", CPLAN, one_run([("run:R-1", "suite")], suite=dict(TRIAGE, version="3")), None, ["STRM-4"],
         "a suite version the plan does not name"),
        ("suite-digest", CPLAN, one_run([("run:R-1", "suite")], suite=dict(TRIAGE, digest="sha256:" + "5d" * 32)), None,
         ["STRM-4", "RUN-8", "PLAN-8"], "the plan's suite and version, with other content than the digest the plan names"),
        ("deployment", CPLAN, one_run([("run:R-1", "deployment")], deployment=dict(DEPLOYMENT, ref=STAGING)), None,
         ["STRM-4", "PLAN-1", "RUN-6"], "the run records another deployment than the plan's"),
        ("deployment-endpoint", CPLAN,
         one_run([("run:R-1", "deployment")], deployment=dict(DEPLOYMENT, endpoint="http://localhost:5081/v1")), None,
         ["STRM-4", "PLAN-1", "RUN-6"], "the plan's deployment, reached at another endpoint than the plan's"),
        ("started-before-acceptance", CPLAN, one_run([("run:R-1", "time")], startedAt="2026-10-08T11:59:59.999999999Z"),
         None, ["STRM-4", "ENC-8"],
         "the run started a nanosecond before the job was accepted: evidence the runner had before it was asked, adopted "
         "and not produced"),
        ("ended-after-terminal", CPLAN, one_run([("run:R-1", "time")], endedAt="2026-10-08T12:02:00.000000001Z"), None,
         ["STRM-4", "ENC-8"], "the run ended a nanosecond after the job's terminal event"),
        ("judges", CPLAN, one_run([("run:R-1", "judges")], judges=judge(model="gpt-4o-mini")), None, ["STRM-4"],
         "another judge model, with the plan's rubric"),
        ("judges-rubric", CPLAN, one_run([("run:R-1", "judges")], judges=judge(rubricDigest=OTHER_RUBRIC)), None, ["STRM-4"],
         "the plan's judge model, with another rubric"),
        ("content-capture", CPLAN, one_run([("run:R-1", "content-capture")], contentCapture="on"), None, ["STRM-4", "RUN-11"],
         "content kept on a plan that keeps none"),
        ("content-capture-absent", CPLAN, one_run([("run:R-1", "content-capture")], contentCapture=DROP), None,
         ["STRM-4", "RUN-11"], "no contentCapture reads as on, and the plan says off"),
        ("target-mode", CPLAN, one_run([("run:R-1", "target-mode")], execution={"targetMode": "replayed", "stimulus": "suite"}),
         None, ["STRM-4", "RUN-7"], "recorded answers played back, not the live subject"),
        ("target-mode-as-planned", SCRIPTED_PLAN,
         two_runs([], dict(cases=ONE_CASE, cost=1.5, execution=SCRIPTED), dict(execution=SCRIPTED)), None,
         ["STRM-4", "RUN-7", "PLAN-7"],
         "the plan asks for a scripted stand-in, and both runs are scripted: what the plan asked, so no problem"),
        ("target-mode-live-plan", LIVE_PLAN, one_run([("run:R-1", "target-mode")], execution=SCRIPTED), None,
         ["STRM-4", "RUN-7"], "a scripted run on a plan that asks for live by name"),
        ("target-mode-absent-is-live", CPLAN,
         one_run([("run:R-1", "target-mode")], execution={"targetMode": "mocked", "stimulus": "suite"}), None,
         ["STRM-4", "RUN-7"], "a mocked run on a plan that names no target mode, which asks for live"),
        ("target-mode-live-run-on-scripted-plan", SCRIPTED_PLAN, one_run([("run:R-1", "target-mode")]), None,
         ["STRM-4", "RUN-7"],
         "a live run on a plan that asks for scripted: not what the plan asked, though live evidence is the stronger "
         "kind; the target mode is compared, not ranked"),
        ("over-cases", CPLAN, one_run([("job", "over-cases")], cases=TWO_CASES + (("case-19", "plain", 0.8), ("case-20", "plain", 0.9))),
         None, ["STRM-4", "PLAN-2"], "one run of four cases on a plan of three"),
        ("over-budget", CPLAN, one_run([("job", "over-budget")], failed=True, cost=3.4), None, ["STRM-4", "PLAN-2"],
         "one run, named by job.failed, that cost $3.40 on a plan of $3.00"),
        ("split-over-cases", CPLAN,
         two_runs([("job", "over-cases")], {}, dict(cases=SECURITY_CASE + (("LLM01-002", "plain", 1.0),))), None,
         ["STRM-4", "PLAN-2", "PLAN-8"],
         "two runs of two cases each: each within the plan's three, the job's four above it"),
        ("shared-case-ids-over-cases", CPLAN, two_runs([("job", "over-cases")], {}, dict(cases=TWO_CASES)), None,
         ["STRM-4", "PLAN-2", "PLAN-8"],
         "two suites that share the case ids case-17 and case-18: a case is its suite with its id, so the job has four "
         "cases, above the plan's three (counted by caseId alone it would have two)"),
        ("shared-case-ids-at-cases", FOUR_CASES, two_runs([], {}, dict(cases=TWO_CASES)), None,
         ["STRM-4", "PLAN-2", "PLAN-8"],
         "the same two runs on a plan of four cases: the job's four cases are exactly its limit, which is within it"),
        ("split-over-budget", CPLAN, two_runs([("job", "over-budget")], dict(cases=ONE_CASE, cost=2.0), dict(cost=2.0)), None,
         ["STRM-4", "PLAN-2"], "two runs of $2.00 each: each within the plan's $3.00, the job's $4.00 above it"),
        ("no-cost", CPLAN, one_run([("run:R-1", "no-cost")], cost=None), None, ["STRM-4", "PLAN-2", "SUM-7"],
         "R-1's summary states no cost: with a budget to keep, that is a problem, not $0"),
        ("several-problems", CPLAN, v_several, None, ["STRM-4", "PLAN-1", "PLAN-2", "RUN-7", "RUN-11", "RUN-12"],
         "job.sealed names R-2 before R-1; the job cost $3.50; R-1 is another version, in another deployment at another "
         "endpoint, judged by another model, with content kept, replayed, and a week before the job; R-2 has no "
         "provenance: ordered by path (job first), then by code"),
    ]


def main():
    if ROOT.exists():
        shutil.rmtree(ROOT)
    for folder, name, schema, doc, writer, reader, rules, why in documents():
        write_json(ROOT / folder / name / "document.json", doc)
        expected = {"kind": "plan", "schema": schema, "writer": writer, "reader": reader}
        if (folder, name) in READS:
            expected["reads"] = READS[(folder, name)]
        expected.update(rules=rules, why=why)
        write_json(ROOT / folder / name / "expected.json", expected)
    for name, plan, runner, matches, why in matching():
        write_json(ROOT / "matching" / name / "plan.json", plan)
        write_json(ROOT / "matching" / name / "runner.json", runner)
        expected = {"kind": "matching", "matches": matches}
        if name in MATCHING_READER_ONLY:
            expected["readerOnly"] = True
        expected.update(rules=["PLAN-7", "VER-8"] if name in MATCHING_READER_ONLY else ["PLAN-7"], why=why)
        write_json(ROOT / "matching" / name / "expected.json", expected)
    write_json(ROOT / "streams" / "plan.json", PLAN)
    write_json(ROOT / "streams" / "plan-small.json", SMALL)
    write_json(ROOT / "streams" / "plan-day.json", DAY)
    digest = hashlib.sha256(dumps(PLAN)).hexdigest()
    small_digest = hashlib.sha256(dumps(SMALL)).hexdigest()
    vectors = streams(digest, small_digest, hashlib.sha256(dumps(DAY)).hexdigest())
    assert sorted(STREAM_RULES) == sorted(name for name, *_ in vectors)
    for name, plan, events, problems, reader_only in vectors:
        if any(isinstance(e, bytes) for e in events):  # raw lines a writer would never produce (STRM-3)
            write_bytes(ROOT / "streams" / name / "events.ndjson",
                        b"".join(e + b"\n" if isinstance(e, bytes) else dumps_line(e) for e in events))
        else:
            write_ndjson(ROOT / "streams" / name / "events.ndjson", events)
        if name == "framing-crlf":
            path = ROOT / "streams" / name / "events.ndjson"
            path.write_bytes(path.read_bytes().replace(b"\n", b"\r\n"))
        if name in UNFINISHED:  # the job.cancelled is still being written: a verifier does not read it
            path = ROOT / "streams" / name / "events.ndjson"
            path.write_bytes(path.read_bytes()[:-1])
        expected = {"kind": "stream", "plan": f"../{plan}", "problems": [[w, p] for w, p in problems]}
        if reader_only:
            expected["readerOnly"] = True
        expected["rules"] = STREAM_RULES[name]
        write_json(ROOT / "streams" / name / "expected.json", expected)
    for name, plan, build, trust, rules, why in conformance():
        folder = ROOT / "plan-conformance" / name
        write_json(folder / "plan.json", plan)
        events, problems = build(folder / "runs", plan, hashlib.sha256(dumps(plan)).hexdigest())
        write_ndjson(folder / "events.ndjson", events)
        expected = {"kind": "plan-conformance", "events": "events.ndjson", "plan": "plan.json", "runs": "runs"}
        if trust is not None:
            write_json(folder / "policy.json", trust)
            expected["policy"] = "policy.json"
        expected.update(problems=[list(p) for p in problems], rules=rules, why=why)
        write_json(folder / "expected.json", expected)


if __name__ == "__main__":
    main()
