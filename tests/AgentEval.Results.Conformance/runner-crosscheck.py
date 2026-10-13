#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 AgentEval Contributors
"""Check retained runner jobs with the CLI and independent Python/.NET protocol implementations."""
import argparse
import http.server
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import threading


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def query(command):
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=60)
    require(result.returncode == 0, f"Verifier exit {result.returncode}: {result.stderr.strip()}")
    return json.loads(result.stdout)


def verify_job(root, folder, tfm, expected="valid"):
    python = [sys.executable, "-X", "utf8", "-I", str(root / "contracts/aef/tools/aef_verify.py")]
    cli = ["dotnet", str(root / f"src/AgentEval.Cli/bin/Debug/{tfm}/AgentEval.Cli.dll")]
    driver = ["dotnet", str(root / f"tests/AgentEval.Results.Conformance/bin/Debug/{tfm}/aef-dotnet.dll")]
    runs = list((folder / "runs").glob("*/run.json"))
    for header in runs:
        for checked in (query(python + ["run", str(header.parent)]), query(cli + ["aef", "verify", str(header.parent), "--json"])):
            require(checked.get("outcome") == "intact" and not checked.get("problems"), f"Invalid run {header.parent.name}: {checked}")
    for operation in ("stream", "conform"):
        args = [operation, str(folder / "events.ndjson"), str(folder / "plan.json")]
        if operation == "conform":
            # Refusals and pre-readiness stops need not create runs/. Their job folder is an empty run store.
            args.append(str(folder / "runs" if (folder / "runs").is_dir() else folder))
        results = [query(command + args) for command in (python, driver)]
        problems = [sorted(tuple(p) for p in result["problems"]) for result in results]
        require(problems[0] == problems[1], f"{folder.name}: {operation} verifier disagreement: {problems}")
        if expected == "valid":
            require(not problems[0], f"{folder.name}: {operation} {problems[0]}")
        else:
            require(expected == "budget-violation" and problems[0] and all(p[1] == "over-budget" for p in problems[0]),
                    f"{folder.name}: unexpected {operation} findings: {problems[0]}")
    return len(runs)


def exercise_cli(root, directory, tfm):
    """Drive the actual executable against loopback only, including its signal/cancellation boundary."""
    cli = ["dotnet", str(root / f"src/AgentEval.Cli/bin/Debug/{tfm}/AgentEval.Cli.dll")]
    scenarios = ["success", "judge-keys", "cases", "timeout", "budget", "upfront", "unknown-model",
                 "missing-key", "readiness-failure", "missing-usage", "provider-overrun", "cancel"]
    for scenario in scenarios:
        if scenario == "cancel" and os.name == "nt":
            print("skip actual SIGINT scenario on Windows; cooperative cancellation is covered by the .NET tests")
            continue
        folder = directory / scenario
        folder.mkdir(parents=True)
        requests, entered, release = [], threading.Event(), threading.Event()

        class Endpoint(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                role = "judge" if self.path.startswith("/judge/") else "subject"
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                requests.append((role, self.headers.get("Authorization"), request))
                if scenario == "cancel" and len(requests) == 2:
                    entered.set()
                    release.wait(20)
                status = 503 if scenario == "readiness-failure" else 200
                reply = json.dumps({"overallScore": 90, "summary": "good", "criteriaResults": [
                    {"criterion": "1. correct", "met": True, "explanation": "private-reason"}]}) if role == "judge" else "Hi"
                output = 1_000_000 if scenario == "provider-overrun" and len(requests) == 2 else 5
                usage = None if scenario == "missing-usage" else {
                    "prompt_tokens": 10, "completion_tokens": output, "total_tokens": 10 + output}
                body = {"error": {"message": "private-provider-error"}} if status != 200 else {
                    "id": "local", "object": "chat.completion", "created": 0, "model": "gpt-4o-mini",
                    "choices": [{"index": 0, "message": {"role": "assistant", "content": reply}, "finish_reason": "stop"}], "usage": usage}
                encoded = json.dumps(body).encode()
                try:
                    self.send_response(status)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(encoded)))
                    self.end_headers()
                    self.wfile.write(encoded)
                except (BrokenPipeError, ConnectionResetError):
                    pass

            def log_message(self, *_):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Endpoint)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        base = f"http://127.0.0.1:{server.server_port}"
        cases = [{"id": "case-1", "input": "private-prompt", "expected": "Hi"}]
        settings = {"inputCap": 8192, "outputCap": 100, "caseSeconds": 30}
        plan = {"schemaVersion": "1.0", "planId": "cli-" + scenario,
                "subject": {"ref": "model:gpt-4o-mini", "version": "1", "endpoint": base + "/subject/v1"},
                "suites": [{"ref": "dataset:cases.json", "version": "1", "lane": "quality"}],
                "limits": {"maxUsd": 1}, "contentCapture": "off", "provider": "local", "targetMode": "live",
                "isolation": "process", "ext": {"agenteval.run": settings}}
        environment = os.environ.copy()
        home = folder / "home"; home.mkdir()
        environment.update(HOME=str(home), AGENTEVAL_RUNNER_ID="crosscheck", OPENAI_API_KEY="ambient-must-not-be-used")
        flags = ["--max-usd", ".01"]
        expected_exit, expected_terminal, expected_calls = 0, "job.sealed", 2
        if scenario in ("judge-keys", "missing-key"):
            plan["credentialRefs"] = [{"name": "SUBJECT_KEY", "scheme": "env", "path": "AEF_TEST_SUBJECT", "purpose": "subject"}]
            environment.pop("AEF_TEST_SUBJECT", None)
            if scenario == "judge-keys":
                cases[0]["evaluationCriteria"] = ["correct"]
                plan["judges"] = [{"model": "gpt-4o-mini"}]
                settings["judge"] = {"endpoint": base + "/judge/v1", "inputCap": 8192, "outputCap": 100}
                plan["credentialRefs"].append({"name": "JUDGE_KEY", "scheme": "env", "path": "AEF_TEST_JUDGE", "purpose": "judge"})
                environment.update(AEF_TEST_SUBJECT="subject-only-secret", AEF_TEST_JUDGE="judge-only-secret")
                expected_calls = 4
            else:
                expected_exit, expected_terminal, expected_calls = 3, "job.refused", 0
        elif scenario == "cases":
            cases.append({"id": "case-2", "input": "private-second", "expected": "Hi"})
            plan["limits"]["cases"] = 1
            expected_exit, expected_terminal = 11, "job.failed"
        elif scenario == "timeout":
            plan["limits"]["timeout"] = "PT1M"; settings["preparationSeconds"] = 60
            expected_exit, expected_terminal, expected_calls = 11, "job.failed", 0
        elif scenario in ("budget", "upfront"):
            flags = ["--max-usd", "0"] + (["--refuse-over-budget"] if scenario == "upfront" else [])
            expected_exit, expected_terminal, expected_calls = (3, "job.refused", 0) if scenario == "upfront" else (8, "job.failed", 0)
        elif scenario == "unknown-model":
            settings["model"] = "not-a-priced-model"
            expected_exit, expected_terminal, expected_calls = 3, "job.refused", 0
        elif scenario == "readiness-failure":
            expected_exit, expected_terminal, expected_calls = 3, "job.failed", 1
        elif scenario == "provider-overrun":
            # Cross the plan cap as well as the optional operator cap: both independent readers must flag it.
            plan["limits"]["maxUsd"] = .01
            expected_exit, expected_terminal = 8, "job.failed"
        elif scenario == "cancel":
            expected_exit, expected_terminal = 3, "job.cancelled"
        (folder / "cases.json").write_text(json.dumps(cases), encoding="utf-8")
        plan_bytes = (json.dumps(plan, indent=2) + "\n ").encode()
        (folder / "plan.json").write_bytes(plan_bytes)
        command = cli + ["run", "--plan", str(folder / "plan.json"), "--root", str(folder), "--runner-id", "crosscheck"]
        process = None
        try:
            before = {p: p.read_bytes() for p in folder.rglob("*") if p.is_file()}
            dry = subprocess.run(command + flags + ["--dry-run", "--json", "--log-file", str(folder / "diagnostic.log"),
                "--capture-fixture", str(folder / "capture.jsonl")], env=environment, capture_output=True, timeout=30)
            dry_exit = 3 if expected_terminal == "job.refused" else 0
            require(dry.returncode == dry_exit and json.loads(dry.stdout)["accepted"] == (dry_exit == 0), f"{scenario}: dry-run acceptance")
            require(not requests and before == {p: p.read_bytes() for p in folder.rglob("*") if p.is_file()}, f"{scenario}: dry-run made requests or files")
            process = subprocess.Popen(command + flags + ["--progress", "ndjson"], env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            if scenario == "cancel":
                require(entered.wait(15), "Cancellation scenario did not enter its case request")
                process.send_signal(signal.SIGINT)
            stdout, stderr = process.communicate(timeout=30)
            require(process.returncode == expected_exit, f"{scenario}: exit {process.returncode}, expected {expected_exit}; {stderr.decode(errors='replace')}")
            events = [json.loads(line) for line in stdout.splitlines()]
            require(events[-1]["kind"] == expected_terminal and len(requests) == expected_calls, f"{scenario}: wrong terminal or call count")
            job = folder / ".agenteval/aef/jobs" / events[0]["jobId"]
            require((job / "events.ndjson").read_bytes() == stdout, f"{scenario}: progress bytes differ")
            require((job / "plan.json").read_bytes() == plan_bytes, f"{scenario}: plan bytes changed")
            headers = [json.loads(p.read_text(encoding="utf-8")) for p in (job / "runs").glob("*/run.json")]
            if scenario == "cancel":
                require(len(headers) == 1 and headers[0]["status"] == "aborted", "Cancellation did not retain an aborted run")
            if scenario == "judge-keys":
                require(all(key == "Bearer " + ("subject-only-secret" if role == "subject" else "judge-only-secret") for role, key, _ in requests), "Role credential isolation failed")
                require(len(headers) == 1 and len(headers[0].get("judges", [])) == 1, "Actual judge was not recorded")
            contents = "".join(p.read_text(encoding="utf-8") for p in job.rglob("*") if p.is_file())
            require(all(secret not in contents for secret in ("private-prompt", "private-reason", "private-provider-error", "subject-only-secret", "judge-only-secret", "ambient-must-not-be-used")), f"{scenario}: private content leaked")
            verify_job(root, job, tfm, "budget-violation" if scenario == "provider-overrun" else "valid")
            print(f"ok actual CLI {scenario}: {expected_terminal}, {expected_calls} requests")
        finally:
            release.set()
            if process is not None and process.poll() is None:
                process.kill(); process.communicate(timeout=10)
            server.shutdown(); server.server_close(); thread.join(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixtures", type=Path)
    parser.add_argument("--tfm", choices=["net8.0", "net10.0"], required=True)
    parser.add_argument("--exercise-cli", action="store_true", help="Also run executable-level loopback and SIGINT scenarios")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    jobs = sorted(args.fixtures.glob("*/events.ndjson"))
    require(jobs, "The runner tests retained no job events")
    runs = violations = 0
    for events in jobs:
        folder = events.parent
        expected = (folder / "expected-stream.txt").read_text(encoding="utf-8")
        runs += verify_job(root, folder, args.tfm, expected)
        violations += expected == "budget-violation"
        print(f"ok {folder.name}: {expected}")
    require(runs > 0 and violations == 1, "Expected sealed runs and exactly one deliberate provider-budget violation")
    print(f"{len(jobs)} jobs / {runs} runs cross-verified; {violations} deliberate budget violation")
    if args.exercise_cli:
        exercise_cli(root, args.fixtures / "actual-cli", args.tfm)


if __name__ == "__main__":
    main()
