#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 AgentEval Contributors
"""Exercise signed runner evidence and immutable-plan controls with a loopback target."""
import http.server
import json
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import threading

root = Path(__file__).resolve().parents[2]
tfm = sys.argv[1] if len(sys.argv) > 1 else "net10.0"
cli = ["dotnet", str(root / f"src/AgentEval.Cli/bin/Debug/{tfm}/AgentEval.Cli.dll")]
keys = root / "contracts/aef/1/conformance/signature-vectors/keys"
requests = []


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        request = self.rfile.read(int(self.headers["Content-Length"]))
        requests.append(request)
        reply = json.dumps({"overallScore": 90, "summary": "private-judge-summary",
                            "criteriaResults": [{"criterion": "1. correct", "met": True, "explanation": "private-reason"}]}) \
            if json.loads(request).get("model") == "gpt-4o" else "Hi"
        body = json.dumps({"id": "test", "object": "chat.completion", "created": 1, "model": "gpt-4o-mini",
                           "choices": [{"index": 0, "message": {"role": "assistant", "content": reply}, "finish_reason": "stop"}],
                           "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
try:
    with tempfile.TemporaryDirectory(prefix="aef-runner-controls-") as temp:
        folder = Path(temp)
        (folder / "cases.json").write_text('[{"id":"one","input":"private-input","expected":"Hi"}]')
        plan = {"schemaVersion": "1.0", "planId": "signed-cli", "subject": {"ref": "model:gpt-4o-mini", "version": "1",
                 "endpoint": f"http://127.0.0.1:{server.server_port}/v1"}, "suites": [{"ref": "dataset:cases.json", "version": "1", "lane": "quality"}],
                "limits": {"maxUsd": 1}, "contentCapture": "off", "targetMode": "live", "isolation": "process", "provider": "local",
                "ext": {"agenteval.run": {"inputCap": 8192, "outputCap": 100, "caseSeconds": 30}}}
        plan_bytes = (json.dumps(plan) + "\n ").encode()
        path = folder / "plan.json"
        path.write_bytes(plan_bytes)
        destination = folder / "explicit-evidence"
        base = ["run", "--plan", str(path), "--root", str(folder), "--runner-id", "test:runner", "--aef", str(destination), "--json"]

        def invoke(options, expected):
            result = subprocess.run(cli + base + options, capture_output=True, text=True, timeout=60)
            assert result.returncode == expected, (options, result.returncode, result.stdout, result.stderr)
            return result

        diagnostic = folder / "diagnostic.log"
        for options in (["--no-aef"], ["--aef-unsealed"], ["--aef-key", "missing.pem"], ["--aef-content", "invalid"],
                        ["--dry-run", "--subject", "model:other"], ["--dry-run", "--subject-version", "2"],
                        ["--dry-run", "--aef-content", "on"]):
            invoke(options + ["--log-file", str(diagnostic)], 2)
            assert not requests and not destination.exists() and not diagnostic.exists()
        controls = ["--aef-key", str(keys / "ecdsa-a.key.pem"), "--subject", "model:gpt-4o-mini", "--subject-version", "1", "--aef-content", "off"]
        invoke(controls + ["--dry-run", "--log-file", str(diagnostic)], 0)
        assert not requests and not destination.exists() and not diagnostic.exists()
        result = json.loads(invoke(controls, 0).stdout)
        job = destination / "jobs" / result["jobId"]
        assert len(requests) == 2 and (job / "plan.json").read_bytes() == plan_bytes
        assert not (folder / ".agenteval").exists()
        assert json.loads((job / "runner-options.json").read_text())["signingKeyId"]
        policy = folder / "policy.json"
        policy.write_text(json.dumps({"keys": [{"identity": "test:runner", "publicKey": (keys / "ecdsa-a.pub.pem").read_text()}]}))
        def verify_identity(run):
            for command in (cli + ["aef", "verify", str(run), "--json", "--policy", str(policy)],
                            [sys.executable, str(root / "contracts/aef/tools/aef_verify.py"), "run", str(run), "--policy", str(policy)]):
                checked = subprocess.run(command, capture_output=True, text=True, timeout=60)
                report = json.loads(checked.stdout)
                assert checked.returncode == 0 and report["outcome"] == "intact" and not report["problems"] and report["signedBy"] == ["test:runner"], report
        for header in (job / "runs").glob("*/run.json"):
            verify_identity(header.parent)
        verify = runpy.run_path(str(root / "tests/AgentEval.Results.Conformance/runner-crosscheck.py"))["verify_job"]
        assert verify(root, job, tfm) == 1
        assert "private-input" not in "".join(p.read_text() for p in job.rglob("*") if p.is_file())

        # Capture is permission from the plan, not an override by the command line.
        plan["contentCapture"] = "on"
        plan["judges"] = [{"model": "gpt-4o"}]
        plan["ext"]["agenteval.run"]["judge"] = {"endpoint": plan["subject"]["endpoint"]}
        path.write_text(json.dumps(plan))
        (folder / "cases.json").write_text('[{"id":"one","input":"private-input","expected":"Hi","evaluationCriteria":["correct"]}]')
        base[base.index("--aef") + 1] = str(folder / "captured")
        controls[controls.index("--aef-content") + 1] = "on"
        result = json.loads(invoke(controls, 0).stdout)
        captured = folder / "captured" / "jobs" / result["jobId"]
        assert len(requests) == 6 and (captured / "plan.json").read_bytes() == path.read_bytes()
        assert verify(root, captured, tfm) == 1
        run = next((captured / "runs").glob("*/run.json")).parent
        verify_identity(run)
        evidence = [json.loads(line) for line in (run / "evidence.ndjson").read_text().splitlines()]
        assert {e["kind"] for e in evidence} == {"input", "output", "expected", "judge_reasoning"}, evidence
        lines = [json.loads(line) for line in (run / "results.ndjson").read_text().splitlines()]
        assert any(len(line.get("evidence", [])) == 4 and line.get("reasoning") for line in lines)
        assert "private-input" in "".join(p.read_text() for p in (run / "blobs").rglob("*") if p.is_file())
        assert "private-reason" in "".join(p.read_text() for p in (run / "blobs").rglob("*") if p.is_file())
        print(f"{tfm}: signed runner, off/on content with judge, stream/conform, exact plans and 8 no-request/no-write preflights passed")
finally:
    server.shutdown()
    server.server_close()
    thread.join(timeout=5)
