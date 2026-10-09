#!/usr/bin/env python3
"""Runs the reference runner (aef_runner.py) on plans and checks what it writes with the independent tools: the
stream verifier and plan matching of aef_stream.py, and the reference verifier aef_verify.py. Not conformance
vectors: what a runner writes is a job, which the corpus cannot hold (spec 09 §9.1).

The jobs:
  1/runner-examples/<name>/   plan.json and expected.json: `runner` (the manifest, relative to the folder), `at`
                              (the fixed clock), `terminal` (the last event's kind), `limit` (job.failed's, or
                              null), `runs` (per run in order, its `status` and number of `cases`), `spentUsd` (the
                              last spend.updated, 0 when there is none), `why`
  conformance/protocol/       the valid plans of plans/ and the stream plans, with runners/valid; each matching/
                              pair, which the runner takes exactly when expected.json says it matches; the plans
                              the writer schema refuses, which the runner refuses (with job.refused)
The runner's target is scripted, so it takes only a plan that asks for targetMode scripted (PLAN-7). A corpus plan
that names no targetMode asks for live: it is run as a copy with targetMode scripted added (its own bytes, so its
own planDigest), and once as written (plans/valid-local, which the runner refuses) and with targetMode mocked (refused
too). A corpus plan that names a targetMode is run as written.

For every job, run with --at (twice), the checks are:
  - the runner exits 0, and OUT holds events.ndjson and, per run announced, runs/<runId>/ with run.json,
    results.ndjson, metrics.json, summary.json and seal.json, and nothing else;
  - every event is valid against the writer runner-event schema; the stream starts with job.accepted or job.refused
    as expected, and, for a plan the reader accepts, as aef_stream.py's PLAN-7 matching says once the target mode is
    counted (the runner takes a plan when matching says it does and the plan asks for scripted: a manifest does not
    say which target modes a runner gives) and as the suites' digests say (PLAN-8: computed here from the scripted
    content aef_runner.py documents); it ends with the expected terminal event and limit, and its terminal event
    names the runs evidence.produced announced; an accepted job's plan.estimated gives the suites' cases, at most the
    plan's cases limit;
  - STRM-3: aef_stream.py's verification of the stream against the plan and its bytes finds no problem;
  - STRM-4: `aef_verify.py conform` over the stream, the plan and the runs finds no problem: every run is the
    plan's, in the target mode the plan asks for;
  - `aef_verify.py run` finds every run intact with no problem; its files are valid against the writer schemas; its
    run.json says targetMode scripted, carries the job.accepted's provenance (RUN-12), and has the subject.kind and
    deployment.ref PLAN-10 derives (the ref's kind, or other; endpoint: and the endpoint, encoded as ENC-13 says,
    for a plan that names only an endpoint);
  - case ids are unique within the job: no caseId of a result line without a parent is in two runs (PLAN-8);
  - no credential reference's path, and no bare string in credentialRefs, appears in any file written (PLAN-3);
  - the second run gives byte-identical output.
STRM-3 and STRM-4 are checked only for a plan the reader schema accepts: they are defined against a plan. A plan the
reader refuses gets a stream of one writer-valid job.refused naming its planId and the SHA-256 of its bytes; and
`aef_verify.py stream` and `conform`, given that plan, exit 2 with a message (an input error, spec 09 §9.3), never a
traceback (plans/timeout-in-seconds: a timeout that is no duration). One job also runs on the system clock (without
--at), with every check but reproducibility. Usage errors: a manifest the reader refuses, an impossible --at and a
non-empty OUT each exit 2 and write nothing.

Usage: python check_runner.py     (exits 1 on any failed check)
"""
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_stream  # noqa: E402  (STRM-3 and PLAN-7)
import aef_verify  # noqa: E402  (STRM-4, runs, schemas)

AEF = TOOLS.parent / "1"
PROTOCOL = AEF / "conformance" / "protocol"
EXAMPLES = AEF / "runner-examples"
AT = "2026-10-09T09:00:00Z"
RUN_FILES = ("metrics.json", "results.ndjson", "run.json", "seal.json", "summary.json")
SCHEMA_OF = {"run.json": "run", "metrics.json": "metrics", "summary.json": "summary", "results.ndjson": "result"}
TERMINAL = ("job.sealed", "job.failed", "job.cancelled", "job.refused")
TWO_SUITES = [{"status": "completed", "cases": 3}] * 2
VALID_RUNNER = PROTOCOL / "runners" / "valid" / "document.json"
TARGET_MODE = "scripted"  # the one target mode the reference runner gives

# The corpus plans the runner is given, with runners/valid: what the job ends with.
CORPUS_JOBS = {
    "plans/valid-local": ("job.sealed", None, TWO_SUITES),
    "plans/valid-container": ("job.sealed", None, TWO_SUITES),
    "plans/valid-timeout-in-days": ("job.sealed", None, TWO_SUITES),
    "plans/valid-target-mode-scripted": ("job.sealed", None, TWO_SUITES),  # asks for scripted itself
    "plans/valid-ci-provider": ("job.refused", None, []),  # runners/valid does not support ci:github
    "streams/plan.json": ("job.sealed", None, TWO_SUITES),
    "streams/plan-day.json": ("job.sealed", None, TWO_SUITES),
    # cases 2: the second case of the first suite is the last; the run is closed as aborted
    "streams/plan-small.json": ("job.failed", "cases", [{"status": "aborted", "cases": 2}]),
    # the quality suite's digest is not that of the scripted suite's content: refused before acceptance (PLAN-8)
    "plan-conformance/valid/plan.json": ("job.refused", None, []),
}
# Corpus plans also run with another target mode than scripted (None: as written, no targetMode, so live).
OTHER_MODES = (("plans/valid-local", None), ("plans/valid-local", "mocked"))


def scripted_digest(suite):
    """The digest of a scripted suite's content, as aef_runner.py documents it: its case ids <ref>@<version>/case-1 to
    case-3, each followed by LF."""
    content = "".join(f"{suite['ref']}@{suite['version']}/case-{n}\n" for n in (1, 2, 3))
    return "sha256:" + hashlib.sha256(content.encode("utf-8")).hexdigest()


def digests_hold(plan):
    """PLAN-8: every suite the plan names with a digest resolves to content with that digest."""
    return all(s["digest"] == scripted_digest(s) for s in plan["suites"] if "digest" in s)


def enc13_name(text):
    """ENC-13: a ref's name derived from free text (here an endpoint): each UTF-8 byte from ! to ~ but % kept, every
    other byte as %XX; - for an empty name, %2D for -; beyond 256 characters, the first 239, ~ and 16 hex digits of
    the SHA-256 of the text."""
    name = "".join(chr(b) if 0x21 <= b <= 0x7E and b != 0x25 else "%{:02X}".format(b) for b in text.encode("utf-8"))
    if name in ("", "-"):
        return {"": "-", "-": "%2D"}[name]
    return name if len(name) <= 256 else name[:239] + "~" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def derived_fields(plan):
    """PLAN-10: (subject.kind, deployment.ref) a run of the plan has: the ref's kind when run.json knows it, else
    other; the plan's deployment, or endpoint: and its endpoint encoded as ENC-13 says, or none."""
    kinds = json.loads((AEF / "schemas" / "writer" / "run.schema.json").read_bytes())[
        "properties"]["subject"]["properties"]["kind"]["enum"]
    subject = plan["subject"]
    kind = subject["ref"].split(":", 1)[0]
    ref = subject.get("deployment") or ("endpoint:" + enc13_name(subject["endpoint"]) if "endpoint" in subject else None)
    return (kind if kind in kinds else "other"), ref


def plan_path(name):
    return PROTOCOL / name / "document.json" if name.startswith("plans/") else PROTOCOL / name


def with_mode(source, mode, folder):
    """The plan file to give the runner: the corpus plan itself when it names a targetMode, or when mode is None;
    otherwise a copy (written as the corpus writes plans) with targetMode mode added after contentCapture."""
    plan = json.loads(source.read_bytes().decode("utf-8"))
    if mode is None or not isinstance(plan, dict) or "targetMode" in plan:
        return source
    copy = {}
    for key, value in plan.items():
        copy[key] = value
        if key == "contentCapture":
            copy["targetMode"] = mode
    copy.setdefault("targetMode", mode)
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (source.relative_to(PROTOCOL).as_posix().replace("/", "--") + f".{mode}.json")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(copy, indent=2, ensure_ascii=False) + "\n")
    return path


def jobs(tmp):
    """Every job to run: {name, plan, runner, at, terminal, limit, runs, spent}; copies of plans go under tmp."""
    copies = tmp / "plans"
    found = []
    for d in sorted(p for p in EXAMPLES.iterdir() if (p / "expected.json").is_file()):
        e = json.loads((d / "expected.json").read_text(encoding="utf-8"))
        found.append({"name": f"runner-examples/{d.name}", "plan": d / e["plan"], "runner": (d / e["runner"]).resolve(),
                      "at": e["at"], "terminal": e["terminal"], "limit": e["limit"], "runs": e["runs"],
                      "spent": e["spentUsd"]})
    for name, (terminal, limit, runs) in CORPUS_JOBS.items():
        found.append({"name": name, "plan": with_mode(plan_path(name), TARGET_MODE, copies), "runner": VALID_RUNNER,
                      "at": AT, "terminal": terminal, "limit": limit, "runs": runs, "spent": None})
    for name, mode in OTHER_MODES:
        found.append({"name": f"{name} (targetMode {mode or 'absent: live'})",
                      "plan": with_mode(plan_path(name), mode, copies), "runner": VALID_RUNNER, "at": AT,
                      "terminal": "job.refused", "limit": None, "runs": [], "spent": None})
    for d in sorted(p for p in (PROTOCOL / "plans").iterdir() if p.is_dir()):
        e = json.loads((d / "expected.json").read_text(encoding="utf-8"))
        if e["writer"] == "invalid":  # PLAN-7 (an unknown value), or not a plan the reader accepts (given as written)
            mode = TARGET_MODE if e["reader"] == "valid" else None
            found.append({"name": f"plans/{d.name}", "plan": with_mode(d / "document.json", mode, copies),
                          "runner": VALID_RUNNER, "at": AT, "terminal": "job.refused", "limit": None, "runs": [],
                          "spent": None})
    for d in sorted(p for p in (PROTOCOL / "matching").iterdir() if p.is_dir()):
        plan = with_mode(d / "plan.json", TARGET_MODE, copies)
        # the runner takes the plan when matching says so and it asks for the target mode the runner gives
        takes = (json.loads((d / "expected.json").read_text(encoding="utf-8"))["matches"]
                 and aef_stream.target_mode(json.loads(plan.read_bytes())) == TARGET_MODE)
        found.append({"name": f"matching/{d.name}", "plan": plan,
                      "runner": d / "runner.json", "at": AT, "terminal": "job.sealed" if takes else "job.refused",
                      "limit": None, "runs": TWO_SUITES if takes else [], "spent": None})
    return found


def run_runner(plan, runner, out, at):
    command = [sys.executable, "-X", "utf8", "-I", str(TOOLS / "aef_runner.py"), "run", str(plan), str(runner),
               str(out), "--target", "scripted"] + (["--at", at] if at else [])
    done = subprocess.run(command, capture_output=True, encoding="utf-8")
    return done.returncode, done.stdout, done.stderr


def verify(*argv):
    """An aef_verify.py operation, in process: its JSON value, or {"inputError": message}."""
    try:
        return json.loads(json.dumps(aef_verify.dispatch([str(a) for a in argv])))
    except aef_verify.InputError as error:
        return {"inputError": str(error)}


def verify_exit(*argv):
    """An aef_verify.py operation, as a process: (exit status, standard error)."""
    command = [sys.executable, "-X", "utf8", "-I", str(TOOLS / "aef_verify.py")] + [str(a) for a in argv]
    done = subprocess.run(command, capture_output=True, encoding="utf-8")
    return done.returncode, done.stderr


def files_of(folder):
    return sorted(p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file())


def secrets_of(plan):
    """What a runner must never write (PLAN-3): every credential reference's path, and a bare value written where a
    reference belongs."""
    refs = plan.get("credentialRefs") if isinstance(plan, dict) else None
    out = []
    for ref in refs if isinstance(refs, list) else []:
        if isinstance(ref, str):
            out.append(ref)
        elif isinstance(ref, dict) and isinstance(ref.get("path"), str):
            out.append(ref["path"])
    return out


def check_job(job, out, stdout):
    """The problems of one job's output, as text."""
    problems = []
    plan_bytes = job["plan"].read_bytes()
    plan, digest = json.loads(plan_bytes.decode("utf-8")), hashlib.sha256(plan_bytes).hexdigest()
    readable = verify("document", "run-plan", job["plan"]).get("reader") == "valid"
    printed = json.loads(stdout)

    events_path = out / "events.ndjson"
    text = events_path.read_bytes().decode("utf-8")
    if not text.endswith("\n") or "\r" in text or "\n\n" in text:
        return ["events.ndjson is not finished NDJSON (ENC-5, ENC-7, STRM-2)"]
    events = [json.loads(line) for line in text[:-1].split("\n")]
    if verify("document", "runner-event", events_path).get("writer") != "valid":
        problems.append("an event is not valid against the writer runner-event schema")
    first, last = events[0], events[-1]
    accepted = first["kind"] == "job.accepted"
    if first["kind"] != ("job.refused" if job["terminal"] == "job.refused" else "job.accepted"):
        problems.append(f"the stream starts with {first['kind']}")
    if readable:
        # PLAN-7: matching tells what the manifest tells; the runner also refuses a target mode it cannot give, and
        # PLAN-8 a suite whose content does not have the plan's digest
        takes = bool(verify("match", job["plan"], job["runner"]).get("matches")
                     and aef_stream.target_mode(plan) == TARGET_MODE and digests_hold(plan))
        if takes != accepted:
            problems.append("aef_stream.py's PLAN-7 matching, with the target mode and the suites' digests, does not "
                            "agree with the runner")
    if (first.get("planId"), first.get("planDigest")) != (plan.get("planId"), digest):
        problems.append("the first event does not name the plan's id and the SHA-256 of its bytes")
    if [e["kind"] for e in events].count(last["kind"]) != 1 or any(e["kind"] in TERMINAL for e in events[:-1]):
        problems.append("the stream has more than one terminal event, or one before its end")
    if (last["kind"], last.get("limit")) != (job["terminal"], job["limit"]):
        problems.append(f"the stream ends with {last['kind']} (limit {last.get('limit')}), not {job['terminal']} "
                        f"(limit {job['limit']})")
    announced = [e["runId"] for e in events if e["kind"] == "evidence.produced"]
    if last.get("runs", []) != announced or printed.get("runs") != announced:
        problems.append(f"the terminal event names {last.get('runs')}, the runner printed {printed.get('runs')}, and "
                        f"evidence.produced announced {announced}")
    if printed.get("terminal") != last["kind"] or printed.get("jobId") != first["jobId"]:
        problems.append("what the runner printed is not what its stream says")
    estimates = [e["cases"] for e in events if e["kind"] == "plan.estimated"]
    if accepted:  # the suites' cases (three each, scripted), at most the plan's cases limit
        cases = 3 * len(plan["suites"])
        wanted = min(cases, plan["limits"]["cases"]) if "cases" in plan["limits"] else cases
        if estimates != [wanted]:
            problems.append(f"plan.estimated says {estimates} case(s), not [{wanted}]")
    spends = [e["spentUsd"] for e in events if e["kind"] == "spend.updated"]
    if job["spent"] is not None and (spends[-1:] or [0]) != [job["spent"]]:
        problems.append(f"the last spend.updated is {spends[-1:]}, not {job['spent']}")

    expected_files = ["events.ndjson"] + [f"runs/{r}/{f}" for r in sorted(announced) for f in RUN_FILES]
    if files_of(out) != sorted(expected_files):
        problems.append(f"OUT holds {files_of(out)}, not the stream and the runs announced")

    runs_dir = out / "runs" if (out / "runs").is_dir() else out
    if readable:
        stream = aef_stream.verify(aef_stream.read_stream_lines(events_path), plan, digest)
        if stream:
            problems.append(f"STRM-3 (aef_stream.py): {stream}")
        conform = verify("conform", events_path, job["plan"], runs_dir)
        if conform.get("problems") != []:
            problems.append(f"STRM-4 (aef_verify.py conform): {conform}, not no problem")
    else:
        # A plan the reader refuses is no plan to check against: an input error (exit 2), never a traceback.
        for argv in (("stream", events_path, job["plan"]), ("conform", events_path, job["plan"], runs_dir)):
            code, stderr = verify_exit(*argv)
            if code != 2 or "Traceback" in stderr or not stderr.strip():
                problems.append(f"aef_verify.py {argv[0]} with a plan the reader refuses exits {code}, not 2 with a "
                                f"message: {stderr.strip()[-200:]}")

    seen_cases = {}
    for k, run_id in enumerate(announced):
        folder = out / "runs" / run_id
        result = verify("run", folder)
        if result != {"outcome": "intact", "problems": []}:
            problems.append(f"{run_id}: aef_verify.py run gives {result}")
        for name, schema in SCHEMA_OF.items():
            if verify("document", schema, folder / name).get("writer") != "valid":
                problems.append(f"{run_id}/{name} is not valid against the writer {schema} schema")
        doc = json.loads((folder / "run.json").read_text(encoding="utf-8"))
        if doc.get("execution", {}).get("targetMode") != TARGET_MODE:
            problems.append(f"{run_id}: targetMode is not {TARGET_MODE}")
        kind, deployment = derived_fields(plan)
        if (doc.get("subject", {}).get("kind"), doc.get("deployment", {}).get("ref")) != (kind, deployment):
            problems.append(f"{run_id}: subject.kind and deployment.ref are not {kind} and {deployment} (PLAN-10)")
        if doc.get("provenance") != {"planId": first.get("planId"), "planDigest": first.get("planDigest"),
                                     "jobId": first["jobId"], "runnerId": first.get("runnerId")}:
            problems.append(f"{run_id}: provenance is not the job.accepted's (RUN-12)")
        lines = [json.loads(l) for l in (folder / "results.ndjson").read_text(encoding="utf-8").splitlines()]
        cases = {l["caseId"] for l in lines if "parentResultId" not in l}
        for case in sorted(cases & set(seen_cases)):
            problems.append(f"{run_id}: case {case} is also in {seen_cases[case]}: case ids are not unique within "
                            f"the job (PLAN-8)")
        seen_cases.update((case, run_id) for case in cases)
        got = {"status": doc.get("status"), "cases": len(cases)}
        if job["runs"] is not None and (k >= len(job["runs"]) or job["runs"][k] != got):
            problems.append(f"{run_id}: {got}, not {job['runs'][k] if k < len(job['runs']) else 'no run'}")
    if job["runs"] is not None and len(announced) != len(job["runs"]):
        problems.append(f"{len(announced)} run(s), not {len(job['runs'])}")

    secrets = secrets_of(plan)
    for name in files_of(out):
        data = (out / name).read_bytes().decode("utf-8")
        for secret in secrets:
            if secret in data:
                problems.append(f"{name} holds the credential reference {secret!r} (PLAN-3)")
    return problems


def same_output(a, b):
    if files_of(a) != files_of(b):
        return False
    return all((a / f).read_bytes() == (b / f).read_bytes() for f in files_of(a))


def main():
    failed, count = 0, 0
    with tempfile.TemporaryDirectory(prefix="aef-runner-") as tmp:
        tmp = Path(tmp)

        def report(name, problems):
            nonlocal failed, count
            count += 1
            if problems:
                failed += 1
                print(f"FAIL {name}")
                for p in problems:
                    print(f"  {p}")

        all_jobs = jobs(tmp)
        for n, job in enumerate(all_jobs):
            outs, problems = [tmp / f"{n}-a", tmp / f"{n}-b"], []
            results = [run_runner(job["plan"], job["runner"], out, job["at"]) for out in outs]
            for code, stdout, stderr in results:
                if code != 0:
                    problems.append(f"the runner exits {code}: {stderr.strip()}")
            if not problems:
                problems += check_job(job, outs[0], results[0][1])
                if not same_output(*outs):
                    problems.append("two runs with the same inputs and --at differ (not reproducible)")
            report(job["name"], problems)

        # The system clock: everything but reproducibility.
        live = next(j for j in all_jobs if j["name"] == "runner-examples/several-cases")
        out = tmp / "system-clock"
        code, stdout, stderr = run_runner(live["plan"], live["runner"], out, None)
        report("runner-examples/several-cases on the system clock",
               [f"the runner exits {code}: {stderr.strip()}"] if code else check_job(live, out, stdout))

        # Usage and input errors: exit 2, nothing written.
        plan = EXAMPLES / "several-cases" / "plan.json"
        for name, runner, at in (("a runner manifest the reader refuses", PROTOCOL / "runners" / "no-provider" /
                                  "document.json", AT),
                                 ("an --at that is no time", VALID_RUNNER, "2026-02-31T09:00:00Z")):
            out = tmp / f"error-{count}"
            code, _, _ = run_runner(plan, runner, out, at)
            report(f"usage error: {name}", [] if code == 2 and not out.exists() else
                   [f"exit {code}, OUT {'written' if out.exists() else 'absent'}"])
        out = tmp / "not-empty"
        out.mkdir()
        (out / "keep.txt").write_bytes(b"x")
        code, _, _ = run_runner(plan, VALID_RUNNER, out, AT)
        report("usage error: an OUT that is not empty", [] if code == 2 and files_of(out) == ["keep.txt"] else
               [f"exit {code}, OUT holds {files_of(out)}"])

    print(f"{count - failed} of {count} runner checks pass")
    return 1 if failed or not count else 0


if __name__ == "__main__":
    sys.exit(main())
