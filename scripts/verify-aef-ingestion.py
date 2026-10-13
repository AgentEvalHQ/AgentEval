#!/usr/bin/env python3
"""Exercise the standalone Mission Control ingestion endpoint over loopback; no provider calls."""
import argparse
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    server = parser.parse_args().server.resolve()
    key = "local-fixture-only-ingestion-key-0123456789"
    boundary = "aef-fixture"
    with tempfile.TemporaryDirectory(prefix="ae-http-ingestion-") as temporary:
        root = Path(temporary)
        storage = root / "ingestion"
        log_path = root / "server.log"
        env = {k: v for k, v in os.environ.items() if not k.lower().startswith("agenteval__")}
        env.update({
            "ASPNETCORE_URLS": "http://127.0.0.1:0",
            "AgentEval__Root": str(root),
            "AgentEval__Ingestion__Enabled": "true",
            "AgentEval__Ingestion__Root": str(storage),
            "AgentEval__Ingestion__ApiKey": key,
            "AgentEval__Ingestion__MaxBytes": "65536",
            "AgentEval__Ingestion__MaxFileBytes": "16384",
            "AgentEval__Ingestion__MaxConcurrentUploads": "1",
            "AgentEval__Ingestion__TimeoutSeconds": "2",
        })
        with log_path.open("w", encoding="utf-8") as log:
            process = subprocess.Popen(["dotnet", str(server)], env=env, cwd=root, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 20
            port = None
            while time.monotonic() < deadline:
                match = re.search(r"Now listening on: http://127\.0\.0\.1:(\d+)", log_path.read_text(encoding="utf-8"))
                if match:
                    port = int(match.group(1))
                    break
                if process.poll() is not None:
                    raise AssertionError("Server failed to start:\n" + log_path.read_text(encoding="utf-8")[-4000:])
                time.sleep(0.05)
            assert port, "Server did not announce its loopback port"

            def multipart(path="run.json", value=b"raw unverified evidence"):
                return (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{path}"\r\n\r\n'.encode()
                        + value + f"\r\n--{boundary}--\r\n".encode())

            def post(body, authenticated=True, chunked=False, route="/api/v1/ingestion/runs"):
                connection = http.client.HTTPConnection("127.0.0.1", port, timeout=8)
                headers = {"Content-Type": f"multipart/form-data; boundary={boundary}"}
                if authenticated:
                    headers["Authorization"] = "Bearer " + key
                try:
                    payload = (body[i:i + 257] for i in range(0, len(body), 257)) if chunked else body
                    connection.request("POST", route, payload, headers, encode_chunked=chunked)
                    response = connection.getresponse()
                    return response.status, response.read()
                finally:
                    connection.close()

            status, _ = post(multipart(), authenticated=False)
            assert status == 401, status
            assert not list((storage / ".staging").iterdir())
            status, body = post(multipart(), chunked=True)
            assert status == 202, (status, body)
            staged = json.loads(body)
            assert staged["status"] == "staged" and staged["verified"] is False
            assert str(root).encode() not in body
            assert (storage / ".staging" / staged["stageId"] / "content/run.json").read_bytes() == b"raw unverified evidence"
            assert post(multipart("../outside"))[0] == 400
            assert post(multipart(value=b"x" * 16385))[0] == 413
            status, body = post(multipart(value=b"x" * 65537), chunked=True)
            assert status == 413, (status, body)

            before = set((storage / ".staging").iterdir())
            slow = http.client.HTTPConnection("127.0.0.1", port, timeout=8)
            try:
                slow.putrequest("POST", "/api/v1/ingestion/runs")
                slow.putheader("Authorization", "Bearer " + key)
                slow.putheader("Content-Type", f"multipart/form-data; boundary={boundary}")
                slow.putheader("Transfer-Encoding", "chunked")
                slow.endheaders()
                partial = multipart().split(b"raw unverified evidence")[0] + b"partial"
                slow.send(f"{len(partial):x}\r\n".encode() + partial + b"\r\n")
                deadline = time.monotonic() + 1
                new_stages = set()
                while time.monotonic() < deadline:
                    new_stages = set((storage / ".staging").iterdir()) - before
                    if new_stages:
                        break
                    time.sleep(0.01)
                assert len(new_stages) == 1, "Slow request never reserved a stage"
                assert post(multipart())[0] == 429, "Concurrent request was not rejected"
                response = slow.getresponse()
                assert response.status == 408, response.status
                response.read()
                state = json.loads((next(iter(new_stages)) / "state.json").read_text())
                assert state["status"] == "interrupted", state
            finally:
                slow.close()
            assert not list((storage / "runs").iterdir()), "Staging published unverified evidence"
            assert post(multipart())[0] == 202, "Timeout did not release upload capacity"

            repository = Path(__file__).resolve().parent.parent
            fixture = repository / "contracts/aef/1/interop/examples/otel-aef/run"
            source_files = {p.relative_to(fixture).as_posix(): p.read_bytes() for p in fixture.rglob("*") if p.is_file()}
            def upload_files(files):
                parts = []
                for name, data in files.items():
                    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\n\r\n'.encode()
                                 + data + b"\r\n")
                parts.append(f"--{boundary}--\r\n".encode())
                status, body = post(b"".join(parts), chunked=True)
                assert status == 202, (status, body)
                return json.loads(body)["stageId"]

            stage_id = upload_files(source_files)
            route = f"/api/v1/ingestion/runs/{stage_id}/finalize"
            assert post(b"", authenticated=False, route=route)[0] == 401
            status, body = post(b"", route=route)
            assert status == 200, (status, body)
            accepted = json.loads(body)
            assert accepted["verified"] and accepted["status"] == "accepted"
            assert str(root).encode() not in body
            destination = storage / "runs" / accepted["runHash"]
            for name, data in source_files.items():
                assert (destination / name).read_bytes() == data, name
            verified = subprocess.run([sys.executable, str(repository / "contracts/aef/tools/aef_verify.py"), "run", str(destination)],
                                      capture_output=True, text=True, timeout=30)
            assert verified.returncode == 0 and json.loads(verified.stdout)["outcome"] == "intact", verified.stdout
            assert post(b"", route=route)[0] == 409
            unsealed_id = upload_files({p: b for p, b in source_files.items() if p != "seal.json"})
            assert post(b"", route=f"/api/v1/ingestion/runs/{unsealed_id}/finalize")[0] == 422
            tampered = dict(source_files)
            tampered["run.json"] += b" "
            tampered_id = upload_files(tampered)
            assert post(b"", route=f"/api/v1/ingestion/runs/{tampered_id}/finalize")[0] == 422
            assert len(list((storage / "runs").iterdir())) == 1
            print("PASS: Kestrel staging/access/limits/timeout; sealed finalization preserves bytes and passes independent verifier; unsealed/tampered refused")
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


if __name__ == "__main__":
    main()
