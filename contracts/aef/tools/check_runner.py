#!/usr/bin/env python3
"""Runs the reference runner (aef_runner.py) where the corpus's job vectors do not reach, and judges each job it
writes with the job vectors' own judge (aef_conformance.judge_job_output, spec 09 §9.3): STRM-3 and STRM-4 find
nothing, every run is intact and writer-valid with targetMode scripted, its provenance and the PLAN-10 derived
fields, the runs, cases, spend, estimate and end are the expected ones, and no credential value or path is written.
The job vectors themselves (conformance/jobs/) run through aef_conformance.py like every other kind.

What is checked here:
  - The sweep: the run plans of conformance/protocol/ (the plans of plans/, the stream vectors' plans, the
    plan-conformance plan, and each matching pair with its runner manifest), against a scripted target with the two
    suites those plans name (three cases each, $0.25 and 20 seconds a case, one second to close a run). The runner
    gives scripted only and resolves only env credentials (PLAN-3), so a plan the reader accepts is given as a copy it
    can run: with targetMode scripted added when it names none, and its keychain and vault credentials left out (the
    job vectors refuse those; one of a purpose the runner does not know stays); each env credential's variable is
    set; a manifest that names no targetModes is given with scripted added. A plan the reader refuses is given as
    written. The runner takes a plan exactly when the independent tools say it can: aef_stream.py's PLAN-7 matching,
    the target mode scripted, the isolation process (a scripted target runs in the runner's process, spec 09
    §9.2.1), and the suites the target has, once each, with the digests the plan names (PLAN-8). What an accepted
    job ends with is written in CORPUS_STOPS (a limit) or is every case of every suite. Two corpus plans are also given asking for live (no
    targetMode) and mocked: refused. Each job runs twice with --at, and the outputs must be byte-identical (this
    runner's own property: ids are free for other runners).
  - The system clock: one job without --at, judged with every check but the clock's.
  - The input errors no job vector holds (the job vectors hold the others, as refused: true): an OUT that is not
    empty (the conformance runner always gives a fresh one) and a suite of the target with a member §9.2.1 does not
    name each exit 2 and write nothing; and `aef_verify.py stream` and `conform`, given a plan the reader
    refuses (plans/timeout-in-seconds), exit 2 with a message, never a traceback.
  - The conformance runner's own judging of a refusal (CONF-3: an input error is exit 2 with a message on standard
    error, and nothing else): driven through --command, a program that crashes (exit 1, a traceback), one that exits
    3 and one that exits 2 with no message pass none of the corpus's vectors that expect a refusal (`refused`,
    `policyRefused`, `expectedError`).

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

import aef_conformance  # noqa: E402  (the job judge, spec 09 §9.3)
import aef_stream  # noqa: E402  (PLAN-7)
import aef_verify  # noqa: E402  (schemas)

AEF = TOOLS.parent / "1"
PROTOCOL = AEF / "conformance" / "protocol"
AT = "2026-10-09T09:00:00Z"
CASE_USD, CASE_SECONDS, CLOSE_SECONDS = 0.25, 20, 1
TARGET_MODE = "scripted"  # the one target mode the reference runner gives
PURPOSES = aef_verify._writer_enum("run-plan.schema.json#/properties/credentialRefs/items/properties/purpose/enum")


def _suite(ref, version, cases):
    """A suite of the sweep's target; cases are (caseId, state, severity or None: RES-9, failed and warn only)."""
    return {"ref": ref, "version": version, "content": f"{ref}@{version}\n" + "".join(c[0] + "\n" for c in cases),
            "cases": [dict({"caseId": c, "state": s}, **({"severity": v} if v else {}), usd=CASE_USD, usdBound=CASE_USD,
                           seconds=CASE_SECONDS, secondsBound=CASE_SECONDS) for c, s, v in cases]}


# The scripted target of the sweep: the two suites the protocol plans name.
TARGET = {"suites": [_suite("suite:support/triage-scenarios", "4",
                            [("case-1", "passed", None), ("case-2", "passed", None), ("case-3", "failed", "high")]),
                     _suite("suite:owasp/llm-top10", "2026.1",
                            [("LLM01-001", "passed", None), ("LLM01-002", "warn", "low"), ("LLM01-003", "passed", None)])],
          "closeSeconds": CLOSE_SECONDS, "priceTable": "check-runner-sweep"}
# The corpus plans the runner takes and stops at a limit: (the limit, the number of cases each run has, in order).
CORPUS_STOPS = {
    # cases 2: the second case of the first suite is the last; the run is closed as aborted
    "streams/plan-small.json": ("cases", [2]),
}
# Corpus plans also given with another target mode than scripted (None: no targetMode, which asks for live).
OTHER_MODES = (("plans/valid-local", None), ("plans/valid-local", "mocked"))


def plan_path(name):
    return PROTOCOL / name / "document.json" if name.startswith("plans/") else PROTOCOL / name


def _write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    return path


def plan_copy(source, mode, folder):
    """The plan file to give the runner: as written when the reader refuses it; otherwise a copy with targetMode mode
    added (after contentCapture) when it names none and mode is not None, and without its keychain and vault
    credentials of a purpose this version knows (one of an unknown scheme or purpose stays: the runner refuses it,
    PLAN-7), or the plan itself when neither changes it."""
    plan = json.loads(source.read_bytes().decode("utf-8"))
    if not (isinstance(plan, dict) and aef_verify.schema_valid("reader", "run-plan", plan)):
        return source
    copy = {}
    for key, value in plan.items():
        if key == "credentialRefs":
            value = [r for r in value if r.get("scheme") not in ("keychain", "vault") or r.get("purpose") not in PURPOSES]
            if not value:
                continue
        copy[key] = value
        if key == "contentCapture" and mode is not None and "targetMode" not in plan:
            copy["targetMode"] = mode
    if mode is not None:
        copy.setdefault("targetMode", mode)
    if copy == plan:
        return source
    return _write(folder / (source.relative_to(PROTOCOL).as_posix().replace("/", "--") + f".{mode}.json"), copy)


def runner_copy(source, folder):
    """The runner manifest to give the runner: with targetModes scripted added when it names none."""
    runner = json.loads(source.read_bytes().decode("utf-8"))
    if "targetModes" in runner:
        return source
    return _write(folder / (source.relative_to(PROTOCOL).as_posix().replace("/", "--")), dict(runner, targetModes=[TARGET_MODE]))


def takes(plan_file, runner_file):
    """Whether the runner takes the plan, by the independent tools: the reader accepts it, aef_stream.py's PLAN-7
    matching says so, it asks for scripted and the isolation process, and its suites are the target's, once each, with
    the digests it names."""
    plan = json.loads(plan_file.read_bytes().decode("utf-8"))
    if not (isinstance(plan, dict) and aef_verify.schema_valid("reader", "run-plan", plan)):
        return False
    have = {(s["ref"], s["version"]): s for s in TARGET["suites"]}
    keys = [(s["ref"], s["version"]) for s in plan["suites"]]
    return (aef_stream.matches(plan, json.loads(runner_file.read_bytes().decode("utf-8")))
            and aef_stream.target_mode(plan) == TARGET_MODE and plan["isolation"] == "process"
            and all(r.get("scheme") == "env" for r in plan.get("credentialRefs", []))
            and len(set(keys)) == len(keys) and all(k in have for k in keys)
            and all(s["digest"] == "sha256:" + hashlib.sha256(have[(s["ref"], s["version"])]["content"]
                                                              .encode("utf-8")).hexdigest()
                    for s in plan["suites"] if "digest" in s))


def expected_job(plan_file, accepted, stop=None):
    """The job the judge expects: refused, or every case of the plan's suites, or the cases CORPUS_STOPS gives before
    its limit. The spend, the estimate and the end follow from the target's fixed cost and duration."""
    if not accepted:
        return {"terminal": "job.refused", "limit": None, "runs": [], "spentUsd": 0, "estimated": None, "endsAt": AT}
    plan = json.loads(plan_file.read_bytes().decode("utf-8"))
    suites = [next(s for s in TARGET["suites"] if (s["ref"], s["version"]) == (p["ref"], p["version"]))
              for p in plan["suites"]]
    limit, counts = stop if stop else (None, [len(s["cases"]) for s in suites])
    runs = [{"suite": {"ref": s["ref"], "version": s["version"]},
             "status": "completed" if n == len(s["cases"]) else "aborted",
             "cases": [[c["caseId"], c["state"]] for c in s["cases"][:n]]} for s, n in zip(suites, counts)]
    total = sum(len(s["cases"]) for s in suites)
    counted = min(total, plan["limits"].get("cases", total))
    done = sum(counts)
    seconds = done * CASE_SECONDS + len(runs) * CLOSE_SECONDS
    return {"terminal": "job.failed" if limit else "job.sealed", "limit": limit, "runs": runs,
            "spentUsd": done * CASE_USD, "estimated": {"cases": counted, "usdLow": counted * CASE_USD,
                                                       "usdHigh": counted * CASE_USD},
            "endsAt": f"2026-10-09T09:{seconds // 60:02d}:{seconds % 60:02d}Z"}


def jobs(tmp):
    """Every job of the sweep: {name, plan, runner, expected}."""
    copies, runner = tmp / "inputs", runner_copy(PROTOCOL / "runners" / "valid" / "document.json", tmp / "inputs")
    found = []

    def add(name, plan_file, runner_file):
        accepted = takes(plan_file, runner_file)
        found.append({"name": name, "plan": plan_file, "runner": runner_file,
                      "expected": expected_job(plan_file, accepted, CORPUS_STOPS.get(name) if accepted else None)})

    names = [f"plans/{d.name}" for d in sorted((PROTOCOL / "plans").iterdir()) if d.is_dir()]
    names += [f"streams/{p.name}" for p in sorted((PROTOCOL / "streams").glob("*.json"))]
    names += ["plan-conformance/valid/plan.json"]
    for name in names:
        add(name, plan_copy(plan_path(name), TARGET_MODE, copies), runner)
    for name, mode in OTHER_MODES:
        add(f"{name} (targetMode {mode or 'absent: live'})", plan_copy(plan_path(name), mode, copies), runner)
    for d in sorted(p for p in (PROTOCOL / "matching").iterdir() if p.is_dir()):
        add(f"matching/{d.name}", plan_copy(d / "plan.json", TARGET_MODE, copies / d.name),
            runner_copy(d / "runner.json", copies / d.name))
    return found


class Runner(aef_conformance.External):
    """aef_runner.py, driven through the command-line contract of spec 09 §9.3."""

    def __init__(self):
        self.command = [sys.executable, "-X", "utf8", "-I", str(TOOLS / "aef_runner.py")]
        self.name = "aef_runner.py"


def environment(plan_file):
    """A fresh random value for each env credential's variable (the judge checks none is written)."""
    plan = json.loads(plan_file.read_bytes().decode("utf-8"))
    refs = plan.get("credentialRefs") if isinstance(plan, dict) else None
    return aef_conformance.job_environment({r["path"]: "set" for r in refs or []
                                            if isinstance(r, dict) and r.get("scheme") == "env"})


def files_of(folder):
    return sorted(p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file())


def same_output(a, b):
    return files_of(a) == files_of(b) and all((a / f).read_bytes() == (b / f).read_bytes() for f in files_of(a))


def main():
    failed, count = 0, 0
    engine = Runner()
    with tempfile.TemporaryDirectory(prefix="aef-runner-") as tmp:
        tmp = Path(tmp)
        target = _write(tmp / "inputs" / "target.json", TARGET)

        def report(name, problems):
            nonlocal failed, count
            count += 1
            if problems:
                failed += 1
                print(f"FAIL {name}")
                for p in problems:
                    print(f"  {p}")

        def run(job, out, at):
            env = environment(job["plan"])
            printed = engine.call(["job", job["plan"], job["runner"], target, out] + (["--at", at] if at else []), env=env)
            return printed, [v for v in env.values() if v is not None]

        sweep = jobs(tmp)
        for n, job in enumerate(sweep):
            outs = [tmp / f"{n}-a", tmp / f"{n}-b"]
            (printed, values), _ = run(job, outs[0], AT), run(job, outs[1], AT)
            problems = aef_conformance.judge_job_output(outs[0], printed, job["plan"], target, job["expected"], values,
                                                        at=AT)
            if not problems and not same_output(*outs):
                problems.append("two jobs with the same inputs and --at differ (not reproducible)")
            report(job["name"], problems)

        # The system clock: every check but the clock's.
        job = next(j for j in sweep if j["name"] == "streams/plan.json")
        out = tmp / "system-clock"
        printed, values = run(job, out, None)
        report("streams/plan.json on the system clock",
               aef_conformance.judge_job_output(out, printed, job["plan"], target,
                                                {k: v for k, v in job["expected"].items() if k != "endsAt"}, values))

        # Usage and input errors: exit 2, nothing written.
        plan, runner = job["plan"], job["runner"]
        # The job vectors hold the other input errors (refused: true); these two they do not.
        first = TARGET["suites"][0]
        out = tmp / f"error-{count}"
        printed = engine.call(["job", plan, runner, _write(tmp / "inputs" / "bad-target.json", dict(
            TARGET, suites=[dict(first, lane="quality")])), out, "--at", AT])
        report("input error: a target's suite with a member §9.2.1 does not name",
               [] if aef_conformance.input_error(printed) and not out.exists() else
               [f"{printed}, OUT {'written' if out.exists() else 'absent'}"])
        out = tmp / "not-empty"
        out.mkdir()
        (out / "keep.txt").write_bytes(b"x")
        printed = engine.call(["job", plan, runner, target, out, "--at", AT])
        report("usage error: an OUT that is not empty",
               [] if aef_conformance.input_error(printed) and files_of(out) == ["keep.txt"] else
               [f"{printed}, OUT holds {files_of(out)}"])

        # A plan the reader refuses is no plan to check against: an input error (exit 2), never a traceback.
        refused = PROTOCOL / "plans" / "timeout-in-seconds" / "document.json"
        events = next(tmp.glob("*-a/events.ndjson"))
        for argv in (("stream", events, refused), ("conform", events, refused, events.parent)):
            done = subprocess.run([sys.executable, "-X", "utf8", "-I", str(TOOLS / "aef_verify.py")] + [str(a) for a in argv],
                                  capture_output=True, encoding="utf-8")
            report(f"aef_verify.py {argv[0]} with a plan the reader refuses",
                   [] if done.returncode == 2 and "Traceback" not in done.stderr and done.stderr.strip() else
                   [f"exit {done.returncode}: {done.stderr.strip()[-200:]}"])

        # CONF-3: only exit 2, with a message, is an input error. Programs that never run pass no refusal vector.
        vectors, refusals = aef_conformance.from_index(aef_conformance.CORPUS, aef_conformance.CORPUS / "index.json")
        expecting = [v for v in vectors if isinstance(v.expected, dict) and (
            v.expected.get("refused") or v.expected.get("policyRefused") or "expectedError" in v.expected)]
        for name, code in (("crashes (exit 1, a traceback)", "raise RuntimeError('crashed')"),
                           ("exits 3 with a message", "import sys; sys.stderr.write('no\\n'); sys.exit(3)"),
                           ("exits 2 with no message", "import sys; sys.exit(2)")):
            never = aef_conformance.External.__new__(aef_conformance.External)
            never.command, never.name = [sys.executable, "-X", "utf8", "-I", "-c", code], name
            _, failing = aef_conformance.run_all(never, expecting, [], quiet=True, show=lambda *a: None)
            passed = [v.id for v in expecting if v.id not in failing]
            report(f"the conformance runner, given a program that {name}: {len(expecting)} refusal vectors",
                   [f"{len(passed)} pass: {', '.join(passed[:5])}"] if passed or not expecting else [])

    print(f"{count - failed} of {count} runner checks pass")
    return 1 if failed or not count else 0


if __name__ == "__main__":
    sys.exit(main())
