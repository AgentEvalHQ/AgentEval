#!/usr/bin/env python3
"""Writes contracts/aef/1/conformance/jobs/: the `job` vectors of the Runner class (spec 09 §9.2.1, §9.3). Each
vector is a folder with a run plan (plan.json), a scripted target (target.json: the fixture that stands for the
subject, §9.2.1) and expected.json; the runner manifest most of them use is jobs/runner.json, shared. Every
expectation below (the terminal event and limit, the runs with their suites, statuses and cases, the spend, the
estimate, the time the job ends, the rule ids) is written by hand from spec 06 and §9.2.1, never computed by a runner.

Some data is computed, as data: a suite's content is its ref and version and its case ids, each line ended by LF,
and the digest a plan names for a frozen suite is sha256: and the hex SHA-256 of that content (a mismatch is written
by hand); the `[caseId, state]` pairs of a run are read from the target's cases, the first ones as the vector says.

Usage: python contracts/aef/tools/job_vectors.py   (vector format: spec 09 §9.2.1; judged by aef_conformance.py)
"""
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "1" / "conformance" / "jobs"
V = "1.0"
AT = "2026-10-09T09:00:00Z"
CLOSE = 1  # closeSeconds: closing and sealing a run takes one second
PRICE_TABLE = "aef-scripted-2026-10"
# Spec 09 §9.3: a keychain or vault reference of a job vector names a path that cannot exist, so that a runner that can
# read keychains or vaults refuses it as one that cannot.
ABSENT = "aef-conformance/absent-3f9d2c71-8b4e-4a6f-9e21-5c7d0a8b6e43"


def write_json(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


def case(case_id, state, usd=0.25, seconds=20, usd_bound=None, seconds_bound=None, severity=None):
    """A case of the scripted target: its state (and severity, exactly when it is failed or warn: RES-9), cost and
    duration, and their bounds (by default the same)."""
    assert (state in ("failed", "warn")) == (severity is not None), (case_id, state, severity)
    found = {"caseId": case_id, "state": state}
    if severity is not None:
        found["severity"] = severity
    return dict(found, usd=usd, usdBound=usd if usd_bound is None else usd_bound,
                seconds=seconds, secondsBound=seconds if seconds_bound is None else seconds_bound)


def suite(ref, version, cases):
    """A suite of the scripted target; its content is its ref and version, then its case ids, each line ended by LF."""
    content = f"{ref}@{version}\n" + "".join(c["caseId"] + "\n" for c in cases)
    return {"ref": ref, "version": version, "content": content, "cases": cases}


def digest(s):
    return "sha256:" + hashlib.sha256(s["content"].encode("utf-8")).hexdigest()


def target(*suites, close=CLOSE):
    return {"suites": list(suites), "closeSeconds": close, "priceTable": PRICE_TABLE}


TRIAGE = suite("suite:support/triage-scenarios", "4",
               [case("case-1", "passed"), case("case-2", "passed"), case("case-3", "failed", severity="high")])
SECURITY = suite("suite:owasp/llm-top10", "2026.1",
                 [case("LLM01-001", "passed"), case("LLM01-002", "warn", severity="low"), case("LLM01-003", "error")])
TONE = suite("suite:support/tone-checks", "2",
             [case("tone-1", "not_measured"), case("tone-2", "passed"), case("tone-3", "failed", severity="critical")])
ESCALATION = suite("suite:support/escalation-paths", "7", [case(f"path-{n}", "passed") for n in (1, 2, 3)])
HANDOFF = suite("suite:support/handoff", "3", [case(f"handoff-{n}", "passed") for n in (1, 2, 3)])
# A suite whose case ids are TRIAGE's (PLAN-8: case ids are the suite's, never rewritten).
REGRESSION = suite("suite:support/triage-regression", "1",
                   [case("case-1", "passed"), case("case-2", "failed", severity="medium"), case("case-3", "passed")])

RUNNER = {
    "schemaVersion": V, "runnerId": "runner-scripted-01", "identity": {"workloadId": "conformance:job-vectors"},
    "kind": "local", "os": "linux", "runtime": {"name": "any", "version": "1"}, "providers": ["local"],
    "tags": ["os:linux", "target:scripted"], "gpu": False, "targetModes": ["scripted"], "version": "1.0.0",
}
RUBRIC = "sha256:" + "a9" * 32
SUBJECT = {"ref": "agent:support/support-triage", "version": "git:3f2a1c",
           "deployment": "deployment:support/support-triage@dev"}


def plan(plan_id, suites, limits, *, subject=SUBJECT, mode="scripted", **more):
    """A run plan, its members in the schema's order; a suite is (target suite, lane or None, digest or None)."""
    doc = {"schemaVersion": V, "planId": plan_id}
    if "checkpointId" in more:
        doc["checkpointId"] = more.pop("checkpointId")
    doc["subject"] = subject
    doc["suites"] = []
    for s, lane, frozen in suites:
        entry = {"ref": s["ref"], "version": s["version"]}
        if frozen is not None:
            entry["digest"] = frozen
        if lane is not None:
            entry["lane"] = lane
        doc["suites"].append(entry)
    doc["limits"] = limits
    doc["contentCapture"] = more.pop("contentCapture", "off")
    if mode is not None:
        doc["targetMode"] = mode
    doc.update(isolation=more.pop("isolation", "process"), provider=more.pop("provider", "local"))
    doc.update(more)
    return doc


def run(s, status, n):
    """An expected run: its suite, status, and the first n cases of the target's suite as [caseId, state]."""
    return {"suite": {"ref": s["ref"], "version": s["version"]}, "status": status,
            "cases": [[c["caseId"], c["state"]] for c in s["cases"][:n]]}


def refused(rules, why, **extra):
    return dict(terminal="job.refused", limit=None, runs=[], spentUsd=0, estimated=None, endsAt=AT, rules=rules,
                why=why, **extra)


def input_error(rules, why, **extra):
    """An input error of spec 09 §9.3: the operation exits 2 and writes nothing (`refused: true`)."""
    return dict(refused=True, rules=rules, why=why, **extra)


def vectors():
    """(name, plan, target, own runner manifest or None, expected beyond kind and inputs). A plan given as bytes is
    written as they are; an expected `at` replaces the vectors' AT."""
    judge = [{"model": "gpt-5.1", "provider": "azure.ai.openai", "rubricDigest": RUBRIC}]
    judge_key = {"name": "AZURE_OPENAI_API_KEY", "scheme": "env", "path": "AEF_TEST_JUDGE_KEY", "purpose": "judge"}
    bounded_triage = suite(TRIAGE["ref"], TRIAGE["version"],
                           [dict(c, usdBound=0.5, secondsBound=30) for c in TRIAGE["cases"]])
    billing = suite("suite:support/billing-disputes", "1",
                    [case("bill-1", "passed"), case("bill-2", "passed"), case("bill-3", "failed", severity="none"),
                     case("bill-4", "passed", usd_bound=0.5)])
    slow = suite("suite:support/slow-lookups", "1",
                 [case("lookup-1", "passed"), case("lookup-2", "passed", seconds_bound=40), case("lookup-3", "passed")])
    padded = suite("suite:support/padded-costs", "1", [case(f"pad-{n}", "passed", usd_bound=0.5) for n in (1, 2, 3)])
    # R7R-3: costs whose sums round differently by job and by run
    refunds = suite("suite:support/refunds", "1", [case("refund-1", "passed", usd=0.1), case("refund-2", "passed", usd=0.1)])
    chargebacks = suite("suite:support/chargebacks", "1",
                        [case("chargeback-1", "passed", usd=0.1), case("chargeback-2", "failed", usd=0.3, severity="high")])
    rounding = suite("suite:support/rounding", "1",
                     [case(f"round-{n}", "passed") for n in (1, 2, 3, 4)] + [case("round-5", "passed", usd=1e-17)])
    two_suites = [(TRIAGE, "quality", None), (SECURITY, "security", None)]
    return [
        # -- from the reference runner's examples
        ("several-cases",
         plan("plan-job-several-cases", [(bounded_triage, "quality", digest(bounded_triage)),
                                         (SECURITY, "security", None), (TONE, None, None)],
              {"maxUsd": 5.0, "cases": 20, "timeout": "PT1H"}, checkpointId="cp_job_vectors",
              subject=dict(SUBJECT, endpoint="http://localhost:5080/v1"), contentCapture="on", judges=judge,
              credentialRefs=[judge_key], runnerSelector=["target:scripted"]),
         target(bounded_triage, SECURITY, TONE), None,
         dict(env={"AEF_TEST_JUDGE_KEY": "set"}, terminal="job.sealed", limit=None,
              runs=[run(bounded_triage, "completed", 3), run(SECURITY, "completed", 3), run(TONE, "completed", 3)],
              spentUsd=2.25, estimated={"cases": 9, "usdLow": 2.25, "usdHigh": 3.0}, endsAt="2026-10-09T09:03:03Z",
              rules=["PLAN-3", "PLAN-7", "PLAN-8", "PLAN-9", "PLAN-10", "RUN-9", "RUN-12", "STRM-1", "STRM-3",
                     "STRM-4"],
              why="three suites, one without a lane, the first named with the digest of its content, which the runner "
                  "checks before it accepts the job; an env credential that is set; a judge the plan names, which "
                  "grades no scripted case, so the runs name none (RUN-9); nine cases within every limit, "
                  "one run per suite, each sealed and announced, then job.sealed naming the three. usdHigh is the sum "
                  "of the cost bounds (the first suite's are $0.50); the job ends after 9 cases of 20 s and 3 closes "
                  "of 1 s")),
        ("budget-stops",
         plan("plan-job-budget-stops", two_suites, {"maxUsd": 1.0, "timeout": "PT2H"},
              subject={"ref": "agent:support/support-triage", "version": "git:3f2a1c",
                       "endpoint": "http://localhost:5080/v1"}, runnerSelector=["target:scripted"]),
         target(TRIAGE, SECURITY), None,
         dict(terminal="job.failed", limit="maxUsd",
              runs=[run(TRIAGE, "completed", 3), run(SECURITY, "aborted", 1)],
              spentUsd=1.0, estimated={"cases": 6, "usdLow": 1.5, "usdHigh": 1.5}, endsAt="2026-10-09T09:01:22Z",
              rules=["PLAN-2", "PLAN-9", "PLAN-10", "STRM-1", "RUN-5"],
              why="four cases spend exactly the plan's maxUsd (equal is within it); the fifth's bound would pass it, so "
                  "the runner closes the second suite's run as aborted with the one case it ran, seals it, and ends "
                  "with job.failed naming maxUsd and both runs")),
        ("cases-stop-at-suite-end",
         plan("plan-job-cases-stop-at-suite-end", two_suites, {"maxUsd": 5.0, "cases": 3},
              subject={"ref": "model:openai/gpt-5.1", "version": "2026-08-01",
                       "endpoint": "https://api.example.com/v1"}),
         target(suite(TRIAGE["ref"], TRIAGE["version"], [dict(c, usdBound=0.5) for c in TRIAGE["cases"]]), SECURITY),
         None,
         dict(terminal="job.failed", limit="cases", runs=[run(TRIAGE, "completed", 3)],
              spentUsd=0.75, estimated={"cases": 3, "usdLow": 0.75, "usdHigh": 1.5}, endsAt="2026-10-09T09:01:01Z",
              rules=["PLAN-9", "PLAN-10", "STRM-1"],
              why="a cases limit of 3, met as the first suite ends: its last case is run, so its run is closed "
                  "completed; before the second suite's first case the limit is reached, no second run is opened, and "
                  "job.failed names the limit and the one run. plan.estimated counts 3 of the 6 cases, and their "
                  "bounds. The plan names only an endpoint, so each run's deployment.ref is endpoint: and the "
                  "endpoint (PLAN-10)")),
        ("digest-mismatch",
         plan("plan-job-digest-mismatch", [(TRIAGE, "quality", "sha256:" + "4c" * 32)], {"maxUsd": 5.0},
              runnerSelector=["target:scripted"]),
         target(TRIAGE), None,
         refused(["PLAN-8"], "the plan names the suite's version 4 with a digest its content does not have: the runner "
                             "resolves and checks the suite before it accepts the job, so it refuses it")),
        ("live-refused",
         plan("plan-job-live-refused", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}, mode=None,
              runnerSelector=["target:scripted"]),
         target(TRIAGE), None,
         refused(["PLAN-6", "PLAN-7"], "the plan names no targetMode, so it asks for the live subject; the runner "
                                       "carries its tag and supports its provider, but gives scripted only")),
        ("same-suite-twice",
         plan("plan-job-same-suite-twice", [(TRIAGE, "quality", None), (TRIAGE, "quality-regression", None)],
              {"maxUsd": 5.0, "cases": 6}),
         target(TRIAGE), None,
         refused(["PLAN-8"], "the plan names one suite twice (the same ref and version, for two lanes): a job runs a "
                             "suite's cases once, so the plan is refused")),
        ("timeout-stops",
         plan("plan-job-timeout-stops", [(ESCALATION, "quality", None), (HANDOFF, "quality-handoff", None)],
              {"maxUsd": 10, "cases": 100, "timeout": "PT1M"},
              subject={"ref": "workflow:support/escalation", "version": "1.4.0",
                       "deployment": "deployment:support/escalation@staging"}),
         target(ESCALATION, HANDOFF), None,
         dict(terminal="job.failed", limit="timeout", runs=[run(ESCALATION, "aborted", 2)],
              spentUsd=0.5, estimated={"cases": 6, "usdLow": 1.5, "usdHigh": 1.5}, endsAt="2026-10-09T09:00:41Z",
              rules=["PLAN-9", "PLAN-10", "STRM-1", "STRM-3", "RUN-5"],
              why="a one-minute timeout: two 20-second cases fit; a third, with the second needed to close and seal "
                  "its run, would end after it (40 + 20 + 1 > 60), so the runner seals the first run as aborted and "
                  "ends with job.failed naming timeout, 41 seconds in")),
        # -- the bounds (R7-2)
        ("cost-bound-stops",
         plan("plan-job-cost-bound-stops", [(billing, "quality", None)], {"maxUsd": 1.0}),
         target(billing), None,
         dict(terminal="job.failed", limit="maxUsd", runs=[run(billing, "aborted", 3)],
              spentUsd=0.75, estimated={"cases": 4, "usdLow": 1.0, "usdHigh": 1.25}, endsAt="2026-10-09T09:01:01Z",
              rules=["PLAN-9"],
              why="after three cases the spend is $0.75; the fourth would cost $0.25, which fits, but its cost bound "
                  "is $0.50, which would take the spend to $1.25, above maxUsd: the runner checks the bound, not the "
                  "cost it cannot know in advance, and stops")),
        ("time-bound-stops",
         plan("plan-job-time-bound-stops", [(slow, "quality", None)], {"maxUsd": 5.0, "timeout": "PT1M"},
              subject={"ref": "pipeline:support/triage", "version": "7",
                       "deployment": "deployment:support/triage@dev"}),
         target(slow), None,
         dict(terminal="job.failed", limit="timeout", runs=[run(slow, "aborted", 1)],
              spentUsd=0.25, estimated={"cases": 3, "usdLow": 0.75, "usdHigh": 0.75}, endsAt="2026-10-09T09:00:21Z",
              rules=["PLAN-9", "PLAN-10"],
              why="after one case the job is 20 seconds in; the second would take 20 seconds, which fits a one-minute "
                  "timeout with the second kept for closing, but its time bound is 40 (20 + 40 + 1 > 60): the runner "
                  "stops. The subject's kind, pipeline, is not one run.json knows, so subject.kind is other (PLAN-10)")),
        ("bound-at-budget",
         plan("plan-job-bound-at-budget", [(padded, "quality", None)], {"maxUsd": 1.0}),
         target(padded), None,
         dict(terminal="job.sealed", limit=None, runs=[run(padded, "completed", 3)],
              spentUsd=0.75, estimated={"cases": 3, "usdLow": 0.75, "usdHigh": 1.5}, endsAt="2026-10-09T09:01:01Z",
              rules=["PLAN-9"],
              why="before the third case the spend is $0.50 and its cost bound $0.50: exactly maxUsd, which is within "
                  "it, so the case runs and the job is sealed")),
        ("budget-runs-sum-stops",
         plan("plan-job-budget-runs-sum-stops", [(refunds, "quality", None), (chargebacks, "quality-chargebacks", None)],
              {"maxUsd": 0.6}),
         target(refunds, chargebacks), None,
         dict(terminal="job.failed", limit="maxUsd", runs=[run(refunds, "completed", 2), run(chargebacks, "aborted", 1)],
              spentUsd=0.30000000000000004, estimated={"cases": 4, "usdLow": 0.6, "usdHigh": 0.6},
              endsAt="2026-10-09T09:01:02Z", rules=["PLAN-9", "SUM-5", "STRM-3", "STRM-4"],
              why="runs of $0.10 + $0.10 and $0.10 + $0.30 on a maxUsd of $0.60. Before the fourth case, the job's spend "
                  "with its bound, 0.1 + 0.1 + 0.1 + 0.3 added exactly and rounded once, is 0.6: within; the runs' "
                  "costs, 0.2 and 0.1 + 0.3 rounded to 0.4, added and rounded once, are 0.6000000000000001: above "
                  "it, as STRM-4 would find. So the runner stops before the fourth case")),
        ("budget-bound-rounds-within",
         plan("plan-job-budget-bound-rounds-within", [(rounding, "quality", None)], {"maxUsd": 1.0}),
         target(rounding), None,
         dict(terminal="job.sealed", limit=None, runs=[run(rounding, "completed", 5)],
              spentUsd=1.0, estimated={"cases": 5, "usdLow": 1.0, "usdHigh": 1.0}, endsAt="2026-10-09T09:01:41Z",
              rules=["PLAN-9", "SUM-5"],
              why="after four cases the spend is $1.00, the maxUsd; the fifth's cost bound is 1e-17, and $1.00 + 1e-17 "
                  "rounds to $1.00 in both sums the verifiers compute: within the limit, so the case runs (its exact "
                  "sum, above $1.00, is no sum a verifier computes)")),
        # -- credentials (PLAN-3)
        ("env-credential-unset",
         plan("plan-job-env-credential-unset", [(TRIAGE, "quality", None)], {"maxUsd": 5.0},
              credentialRefs=[{"name": "SUPPORT_API_KEY", "scheme": "env", "path": "AEF_TEST_UNSET_KEY",
                               "purpose": "subject"}]),
         target(TRIAGE), None,
         refused(["PLAN-3"], "the plan's env credential names a variable that is not set where the runner runs: it "
                             "cannot be resolved, so the plan is refused before job.accepted, though the scripted "
                             "target needs no credential", env={"AEF_TEST_UNSET_KEY": "absent"})),
        ("env-credential-set",
         plan("plan-job-env-credential-set", two_suites, {"maxUsd": 5.0, "timeout": "PT1H"}, judges=judge,
              credentialRefs=[{"name": "SUPPORT_API_KEY", "scheme": "env", "path": "AEF_TEST_SUBJECT_KEY",
                               "purpose": "subject"}, judge_key]),
         target(TRIAGE, SECURITY), None,
         dict(env={"AEF_TEST_SUBJECT_KEY": "set", "AEF_TEST_JUDGE_KEY": "set"}, terminal="job.sealed", limit=None,
              runs=[run(TRIAGE, "completed", 3), run(SECURITY, "completed", 3)],
              spentUsd=1.5, estimated={"cases": 6, "usdLow": 1.5, "usdHigh": 1.5}, endsAt="2026-10-09T09:02:02Z",
              rules=["PLAN-3", "PLAN-4", "SEC-1"],
              why="two env credentials, both set: the runner resolves them and runs the job; neither value, nor the "
                  "variable a path names, appears in any byte it writes")),
        ("env-credential-empty",
         plan("plan-job-env-credential-empty", [(TRIAGE, "quality", None)], {"maxUsd": 5.0},
              credentialRefs=[{"name": "SUPPORT_API_KEY", "scheme": "env", "path": "AEF_TEST_EMPTY_KEY",
                               "purpose": "subject"}]),
         target(TRIAGE), None,
         refused(["PLAN-3"], "the plan's env credential names a variable that is set to the empty string: an empty "
                             "value is no credential, so it does not resolve and the plan is refused",
                 env={"AEF_TEST_EMPTY_KEY": "empty"})),
        ("vault-credential",
         plan("plan-job-vault-credential", [(TRIAGE, "quality", None)], {"maxUsd": 5.0},
              credentialRefs=[{"name": "AZURE_OPENAI_API_KEY", "scheme": "vault", "path": ABSENT,
                               "purpose": "judge"}]),
         target(TRIAGE), None,
         refused(["PLAN-3"], "a vault reference whose path names nothing (spec 09 §9.3): whether or not the runner "
                             "can read a vault, the credential cannot be resolved, so the plan is refused before "
                             "job.accepted, not a job that fails later")),
        ("keychain-credential",
         plan("plan-job-keychain-credential", [(TRIAGE, "quality", None)], {"maxUsd": 5.0},
              credentialRefs=[{"name": "SUPPORT_API_KEY", "scheme": "keychain", "path": ABSENT,
                               "purpose": "subject"}]),
         target(TRIAGE), None,
         refused(["PLAN-3"], "a keychain reference whose path names nothing (spec 09 §9.3): whether or not the runner "
                             "can read a keychain, the credential cannot be resolved: refused before job.accepted")),
        # -- suites (PLAN-8)
        ("suite-missing",
         plan("plan-job-suite-missing", [(TRIAGE, "quality", None),
                                         ({"ref": "suite:support/unknown", "version": "1"}, "other", None)],
              {"maxUsd": 5.0}),
         target(TRIAGE), None,
         refused(["PLAN-8"], "the plan's second suite is not one the target has: the runner cannot resolve it, so it "
                             "refuses the plan before it runs the first")),
        ("shared-case-ids",
         plan("plan-job-shared-case-ids", [(TRIAGE, "quality", None), (REGRESSION, "regression", None)],
              {"maxUsd": 5.0, "cases": 6}),
         target(TRIAGE, REGRESSION), None,
         dict(terminal="job.sealed", limit=None, runs=[run(TRIAGE, "completed", 3), run(REGRESSION, "completed", 3)],
              spentUsd=1.5, estimated={"cases": 6, "usdLow": 1.5, "usdHigh": 1.5}, endsAt="2026-10-09T09:02:02Z",
              rules=["PLAN-8", "STRM-4"],
              why="two suites that share the case ids case-1 to case-3: each run keeps its suite's ids, unrewritten, "
                  "and the job's six cases (a case is its suite with its id) are exactly its cases limit")),
        ("cases-stop-mid-suite",
         plan("plan-job-cases-stop-mid-suite", [(TRIAGE, "quality", None)], {"maxUsd": 5.0, "cases": 2}),
         target(TRIAGE), None,
         dict(terminal="job.failed", limit="cases", runs=[run(TRIAGE, "aborted", 2)],
              spentUsd=0.5, estimated={"cases": 2, "usdLow": 0.5, "usdHigh": 0.5}, endsAt="2026-10-09T09:00:41Z",
              rules=["PLAN-9", "RUN-5", "RES-1"],
              why="a cases limit of 2 in a suite of 3: the run is cut off before its last case, closed aborted with "
                  "two result lines and none for the case not run")),
        # -- matching and the plan itself (PLAN-6, PLAN-7)
        ("manifest-without-target-modes",
         plan("plan-job-manifest-without-target-modes", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         target(TRIAGE), {k: v for k, v in RUNNER.items() if k != "targetModes"},
         refused(["PLAN-6", "PLAN-7"], "the runner acts as a manifest that names no targetModes, so it gives live "
                                       "only: it does not take a plan that asks for scripted")),
        ("container-isolation-refused",
         plan("plan-job-container-isolation-refused", [(TRIAGE, "quality", None)], {"maxUsd": 5.0},
              subject=dict(SUBJECT, image="sha256:" + "9d" * 32, repository="ghcr.io/example/support-triage"),
              isolation="container", provider="docker"),
         target(TRIAGE), dict(RUNNER, providers=["local", "docker"]),
         refused(["PLAN-7"], "the plan asks for a container; the runner supports the provider docker, but a scripted "
                             "target runs in the runner's process, so it gives the isolation process only")),
        # -- input errors (spec 09 §9.3): exit 2, nothing written in OUT
        ("input-plan-not-json",
         b'{"schemaVersion": "1.0", "planId": "plan-job-input-plan-not-json", "subject": \n',
         target(TRIAGE), None,
         input_error(["ENC-1"], "the plan file is not a JSON text (it ends mid-object): no plan, so no job.refused can "
                                "name it")),
        ("input-plan-without-plan-id",
         {k: v for k, v in plan("plan-job-input-plan-without-plan-id", [(TRIAGE, "quality", None)],
                                {"maxUsd": 5.0}).items() if k != "planId"},
         target(TRIAGE), None,
         input_error(["PLAN-5", "PLAN-7"], "a plan with no planId: a job.refused could not name it, so it is not a "
                                           "plan, and the operation writes no stream")),
        ("input-runner-reader-refuses",
         plan("plan-job-input-runner-reader-refuses", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         target(TRIAGE), dict(RUNNER, providers=[]),
         input_error(["PLAN-6", "VER-3"], "the manifest the runner acts as supports no provider: the reader schema "
                                          "refuses it, so there is no runner to run the job as")),
        ("input-target-unknown-member",
         plan("plan-job-input-target-unknown-member", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         dict(target(TRIAGE), schemaVersion="1.0"), None,
         input_error([], "the target has a member spec 09 §9.2.1 does not name (schemaVersion): not of its shape")),
        ("input-target-case-unknown-member",
         plan("plan-job-input-target-case-unknown-member", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         target(suite(TRIAGE["ref"], TRIAGE["version"], [dict(TRIAGE["cases"][0], reason="scripted")]
                      + TRIAGE["cases"][1:])), None,
         input_error([], "a case of the target has a member spec 09 §9.2.1 does not name (reason): not of its shape")),
        ("input-target-severity-on-passed",
         plan("plan-job-input-target-severity-on-passed", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         target(suite(TRIAGE["ref"], TRIAGE["version"], [dict(TRIAGE["cases"][0], severity="low")]
                      + TRIAGE["cases"][1:])), None,
         input_error(["RES-9"], "a passed case with a severity: a case has one exactly when it is failed or warn "
                                "(spec 09 §9.2.1)")),
        ("input-target-bound-below-cost",
         plan("plan-job-input-target-bound-below-cost", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         target(suite(TRIAGE["ref"], TRIAGE["version"], [dict(TRIAGE["cases"][0], usdBound=0.125)]
                      + TRIAGE["cases"][1:])), None,
         input_error(["PLAN-9"], "a case whose cost bound ($0.125) is below its cost ($0.25): a bound is the most a "
                                 "case can cost")),
        ("input-at-not-a-time",
         plan("plan-job-input-at-not-a-time", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         target(TRIAGE), None,
         input_error(["ENC-8"], "the clock's start is February 31st: not a time", at="2026-02-31T09:00:00Z")),
        ("input-at-clock-leaves-years",
         plan("plan-job-input-at-clock-leaves-years", [(TRIAGE, "quality", None)], {"maxUsd": 5.0}),
         target(TRIAGE), None,
         input_error(["ENC-8"], "the clock starts a minute before the end of 9999, and three cases of 20 seconds and "
                                "one close of a second would take it past: an input error found before anything is "
                                "written", at="9999-12-31T23:59:00Z")),
        ("plan-reader-refuses",
         plan("plan-job-reader-refuses", [(TRIAGE, "quality", None)], {"maxUsd": 5.0, "timeout": "PT30S"}),
         target(TRIAGE), None,
         refused(["PLAN-7", "VER-3", "ENC-9"], "the plan's timeout is no duration (ENC-9 has no seconds): the reader "
                                               "schema refuses the plan, and its planId can be read, so the runner "
                                               "refuses it with job.refused naming that planId and the SHA-256 of "
                                               "the plan's bytes")),
    ]


def main():
    if ROOT.exists():
        shutil.rmtree(ROOT)
    write_json(ROOT / "runner.json", RUNNER)
    names = [v[0] for v in vectors()]
    assert len(names) == len(set(names))
    for name, the_plan, the_target, own_runner, expected in vectors():
        folder = ROOT / name
        if isinstance(the_plan, bytes):  # a plan file that is not JSON
            folder.mkdir(parents=True, exist_ok=True)
            (folder / "plan.json").write_bytes(the_plan)
        else:
            write_json(folder / "plan.json", the_plan)
        write_json(folder / "target.json", the_target)
        if own_runner is not None:
            write_json(folder / "runner.json", own_runner)
        doc = {"kind": "job", "plan": "plan.json", "runner": "runner.json" if own_runner else "../runner.json",
               "target": "target.json", "at": expected.pop("at", AT)}
        if "env" in expected:
            doc["env"] = expected.pop("env")
        rules, why = expected.pop("rules"), expected.pop("why")
        doc.update(expected)
        doc.update(rules=rules, why=why)
        write_json(folder / "expected.json", doc)
    print(f"{len(names)} job vectors in {ROOT}")


if __name__ == "__main__":
    main()
