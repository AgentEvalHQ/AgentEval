#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 AgentEval Contributors
"""Exercise native signing/unsealed controls through the actual CLI using loopback only."""
import http.server
import json
from pathlib import Path
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
        requests.append(self.rfile.read(int(self.headers["Content-Length"])))
        body = json.dumps({"id": "local", "object": "chat.completion", "created": 1, "model": "local",
                           "choices": [{"index": 0, "message": {"role": "assistant", "content": "Hi"},
                                        "finish_reason": "stop"}],
                           "usage": {"prompt_tokens": 10, "completion_tokens": 1, "total_tokens": 11}}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def invoke(args, expected=0):
    result = subprocess.run(cli + args, capture_output=True, text=True, timeout=60)
    assert result.returncode == expected, (args, result.returncode, result.stderr)
    return result


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
try:
    with tempfile.TemporaryDirectory(prefix="native-controls-") as temp:
        folder = Path(temp)
        dataset = folder / "dataset.yaml"
        dataset.write_text("- id: one\n  input: private-prompt\n  expected: Hi\n", encoding="utf-8")
        policy = folder / "policy.json"
        policy.write_text(json.dumps({"keys": [{"identity": "test:local", "publicKey":
                          (keys / "ecdsa-a.pub.pem").read_text()}]}), encoding="utf-8")
        base = ["eval", "--dataset", str(dataset), "--endpoint", f"http://127.0.0.1:{server.server_port}/v1",
                "--model", "local", "--api-key", "local-test", "--quiet"]
        diagnostic = folder / "diagnostic.log"
        for options in (["--aef-key", "missing.pem"], ["--aef-unsealed", "--aef-key", "missing.pem"],
                        ["--aef-unsealed", "--no-aef"]):
            invoke(base + options + ["--log-file", str(diagnostic)], 2)
            assert not requests and not diagnostic.exists(), "preflight performed work"
        invoke(["eval", "--help", "--aef-key", "missing.pem", "--log-file", str(diagnostic)])
        assert not requests and not diagnostic.exists(), "help performed work"
        for mode in ("signed", "unsealed"):
            destination = folder / mode
            options = ["--aef-key", str(keys / "ecdsa-a.key.pem")] if mode == "signed" else ["--aef-unsealed"]
            invoke(base + ["--aef", str(destination)] + options)
            headers = list(destination.rglob("run.json"))
            assert len(headers) == 1
            run = headers[0].parent
            commands = [cli + ["aef", "verify", str(run), "--json", "--policy", str(policy)],
                        [sys.executable, str(root / "contracts/aef/tools/aef_verify.py"), "run", str(run), "--policy", str(policy)]]
            for command in commands:
                checked = subprocess.run(command, capture_output=True, text=True, timeout=60)
                assert checked.returncode == 0, checked.stderr
                result = json.loads(checked.stdout)
                assert result["outcome"] == ("intact" if mode == "signed" else "unsealed"), result
                assert not result["problems"], result
                if mode == "signed":
                    assert "test:local" in result["signedBy"], result
            assert (run / "attestation.dsse.json").exists() == (mode == "signed")
            print(f"ok {tfm}: {mode} native CLI evidence cross-verified")
        assert len(requests) == 2, len(requests)
        print(f"ok {tfm}: conflicts, invalid key and help performed no HTTP or diagnostic writes")
finally:
    server.shutdown()
    server.server_close()
    thread.join(timeout=5)
