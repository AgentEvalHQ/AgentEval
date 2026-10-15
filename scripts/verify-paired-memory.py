#!/usr/bin/env python3
"""Run paired LongMemEval through the actual CLI against a loopback-only fixture and verify both AEF arms."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    args = parser.parse_args()
    cli = ["dotnet", str(args.cli.resolve())]
    verifier = Path(__file__).resolve().parent.parent / "contracts/aef/tools/aef_verify.py"
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            index = len(requests)
            requests.append(body)
            answer = "PRIVATE answer" if index % 2 == 0 else "no" if index < 4 else "yes"
            response = json.dumps({"id": "fixture", "object": "chat.completion", "created": 1,
                                   "model": "fixture-model", "choices": [{"index": 0,
                                   "message": {"role": "assistant", "content": answer}, "finish_reason": "stop"}],
                                   "usage": {"prompt_tokens": 10, "completion_tokens": 2, "total_tokens": 12}}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix="ae-paired-memory-") as temporary:
            root = Path(temporary)
            (root / "fixture.slnx").write_text("<Solution />", encoding="utf-8")
            dataset = root / "questions.json"
            dataset.write_text(json.dumps([{
                "question_id": f"q{i}", "question_type": "single-session-user", "question": "PRIVATE question",
                "answer": "PRIVATE gold", "question_date": "2026/07/29 (Wed) 00:00",
                "haystack_sessions": [[{"role": "user", "content": "PRIVATE history", "has_answer": True}]],
                "haystack_dates": ["2026/07/01"], "haystack_session_ids": ["s1"], "answer_session_ids": ["s1"]
            } for i in range(2)]), encoding="utf-8")
            env = dict(os.environ, AI_INFERENCE_PROVIDER="openai-compatible",
                       OPENAI_COMPATIBLE_ENDPOINT=f"http://127.0.0.1:{server.server_port}/v1",
                       OPENAI_COMPATIBLE_MODEL="fixture-model", OPENAI_COMPATIBLE_API_KEY="offline-fixture",
                       LONGMEMEVAL_DATASET_PATH=str(dataset), NO_PROXY="127.0.0.1,localhost", no_proxy="127.0.0.1,localhost")

            def run(arguments, expected):
                result = subprocess.run(cli + arguments, cwd=root, env=env, capture_output=True, text=True, timeout=90)
                assert result.returncode == expected, (result.returncode, result.stdout, result.stderr)
                return result

            run(["init-workspace", "--name", "paired-memory-fixture"], 0)
            command = ["bench", "longmemeval", "--paired-oracle", "--subject", "fixture", "--root", str(root)]
            for sealed in (True, False):
                requests.clear()
                destination = root / ("sealed" if sealed else "unsealed")
                output = run(command + ["--aef", str(destination)] + ([] if sealed else ["--aef-unsealed"]), 9)
                assert len(requests) == 8, len(requests)
                assert "100.0 percentage points" in output.stdout
                arms = {}
                for header in destination.rglob("run.json"):
                    directory = header.parent
                    report = json.loads(subprocess.check_output([sys.executable, str(verifier), "run", str(directory)], text=True))
                    assert report["outcome"] == ("intact" if sealed else "unsealed"), report
                    pair = json.loads((directory / "summary.json").read_text())["ext"]["agenteval.memoryPair"]
                    arms[pair["role"]] = (directory, pair)
                    assert pair["plannedQuestions"] == pair["observedQuestions"] == pair["scoredQuestions"] == 2
                    assert pair["totalLlmCalls"] == 4
                    assert all("PRIVATE" not in file.read_text() for file in directory.rglob("*") if file.is_file())
                normal_dir, normal = arms["normal"]
                oracle_dir, oracle = arms["oracle"]
                assert normal["pairId"] == oracle["pairId"]
                assert normal["sourceInputsDigest"] == oracle["sourceInputsDigest"]
                assert normal["accuracyPercent"] == 0 and oracle["accuracyPercent"] == 100
                assert oracle["oracleGapPercentagePoints"] == 100
                assert (normal_dir / "gates.ndjson").exists() and not (oracle_dir / "gates.ndjson").exists()
                if sealed:
                    verification = json.loads(run(["aef", "verify", str(normal_dir), "--json"], 0).stdout)
                    assert oracle["normalRunHash"] == verification["runHash"]
                else:
                    assert oracle["normalRunHash"] is None
            requests.clear()
            before = {str(p): p.read_bytes() for p in root.rglob("*") if p.is_file()}
            run(command + ["--no-aef"], 9)
            assert len(requests) == 8
            assert before == {str(p): p.read_bytes() for p in root.rglob("*") if p.is_file()}
            occupied = root / "occupied"
            occupied.write_text("not a directory", encoding="utf-8")
            requests.clear()
            run(command + ["--aef", str(occupied)], 3)
            assert not requests
        print("4 paired CLI scenarios passed: sealed, unsealed, no AEF, invalid destination; independent Python verification; loopback calls only.")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


if __name__ == "__main__":
    main()
