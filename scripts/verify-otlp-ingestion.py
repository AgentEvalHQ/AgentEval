#!/usr/bin/env python3
"""Verify the production OTLP/HTTP receiver on loopback with no provider calls."""
import argparse
import gzip
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    server = parser.parse_args().server.resolve()
    fixtures = Path(__file__).resolve().parent.parent / "tests/AgentEval.Results.Tests/Observations/Fixtures"
    key = "local-fixture-only-otlp-key-0123456789012345"
    with tempfile.TemporaryDirectory(prefix="ae-http-otlp-") as temporary:
        root = Path(temporary)
        storage = root / "observations"
        records = storage / "records"
        log_path = root / "server.log"
        environment = {k: v for k, v in os.environ.items() if not k.lower().startswith("agenteval__")}
        environment.update({
            "ASPNETCORE_URLS": "http://127.0.0.1:0", "AgentEval__Root": str(root),
            "AgentEval__Otlp__Enabled": "true", "AgentEval__Otlp__Root": str(storage),
            "AgentEval__Otlp__Source": "loopback-fixture", "AgentEval__Otlp__ApiKey": key,
            "AgentEval__Otlp__MaxRequestBytes": "32768", "AgentEval__Otlp__MaxDecodedBytes": "16384",
            "AgentEval__Otlp__MaxConcurrentRequests": "1", "AgentEval__Otlp__TimeoutSeconds": "2",
        })
        process = None
        port = None

        def stop():
            nonlocal process
            if process is not None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
                process = None

        def start(capacity=64):
            nonlocal process, port
            environment["AgentEval__Otlp__MaxRecords"] = str(capacity)
            with log_path.open("w", encoding="utf-8") as log:
                process = subprocess.Popen(["dotnet", str(server)], cwd=root, env=environment,
                                           stdout=log, stderr=subprocess.STDOUT)
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                text = log_path.read_text(encoding="utf-8")
                match = re.search(r"Now listening on: http://127\.0\.0\.1:(\d+)", text)
                if match:
                    port = int(match.group(1))
                    return
                if process.poll() is not None:
                    raise AssertionError("Server startup failed:\n" + text[-4000:])
                time.sleep(0.05)
            raise AssertionError("Server did not announce its loopback listener")

        def post(body=b"{}", media="application/json", compressed=False, chunked=False, authorized=True, extra=None):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=8)
            headers = {"Content-Type": media}
            if authorized:
                headers["Authorization"] = "Bearer " + key
            if compressed:
                body = gzip.compress(body)
                headers["Content-Encoding"] = "gzip"
            headers.update(extra or {})
            try:
                payload = (body[i:i + 257] for i in range(0, len(body), 257)) if chunked else body
                connection.request("POST", "/v1/traces", payload, headers, encode_chunked=chunked)
                response = connection.getresponse()
                data = response.read()
                assert str(root).encode() not in data, "Response leaked a storage path"
                assert response.getheader("Cache-Control") == "no-store"
                assert response.getheader("Content-Type").split(";")[0] == media
                if response.status >= 400:
                    if media == "application/json":
                        assert json.loads(data)["message"]
                    else:
                        assert data[0] == 0x12 and data[1] == len(data) - 2, "Not a protobuf Status.message"
                else:
                    assert data == (b"{}" if media == "application/json" else b"")
                return response.status, data
            finally:
                connection.close()

        def inventory():
            found = {}
            for path in records.glob("*.json"):
                data = path.read_bytes()
                assert path.stem == "batch-" + hashlib.sha256(data).hexdigest()
                found[path.name] = data
            return found

        try:
            start()
            assert post(authorized=False)[0] == 401
            assert post(extra={"Origin": "https://fixture.invalid"})[0] == 403
            assert not inventory()
            for name in ("complete", "merge-and-unknown", "wire-scalars"):
                before = len(inventory())
                for encoding, media in (("json", "application/json"), ("pb", "application/x-protobuf")):
                    for compressed in (False, True):
                        status, body = post((fixtures / (name + "." + encoding)).read_bytes(), media, compressed, chunked=True)
                        assert status == 200, (name, encoding, compressed, status, body)
                assert len(inventory()) == before + 1, "Equivalent wire encodings duplicated retained evidence"
            assert post(b"", "application/x-protobuf", compressed=True)[0] == 200
            before = inventory()
            assert post(b"\x00", "application/x-protobuf")[0] == 400
            assert post(b"{broken")[0] == 400
            assert post(b"{}", extra={"Content-Encoding": "br"})[0] == 415
            oversized = json.dumps({"unknown": "x" * 33000}).encode()
            assert post(oversized)[0] == 413
            assert post(oversized, chunked=True)[0] == 413
            assert post(oversized, compressed=True)[0] == 413
            assert inventory() == before, "Failed request published a record"

            slow = http.client.HTTPConnection("127.0.0.1", port, timeout=8)
            try:
                slow.putrequest("POST", "/v1/traces")
                slow.putheader("Authorization", "Bearer " + key)
                slow.putheader("Content-Type", "application/json")
                slow.putheader("Transfer-Encoding", "chunked")
                slow.endheaders()
                slow.send(b"1\r\n{\r\n")
                deadline = time.monotonic() + 1
                overloaded = False
                while time.monotonic() < deadline:
                    status, _ = post()
                    if status == 429:
                        overloaded = True
                        break
                    assert status == 200
                    time.sleep(0.01)
                assert overloaded, "Concurrent request was not refused"
                assert inventory() == before, "Incomplete body was acknowledged or published"
                response = slow.getresponse()
                assert response.status == 503, response.status
                assert json.loads(response.read())["message"] == "request-timeout"
            finally:
                slow.close()
            assert post()[0] == 200, "Timeout did not release receiver capacity"
            assert inventory() == before

            secret = {"resourceSpans": [{"scopeSpans": [{"spans": [{
                "traceId": "0123456789abcdef0123456789abcdef", "spanId": "0123456789abcdef",
                "name": "fixture-private-prompt", "attributes": [
                    {"key": "unknown.content", "value": {"stringValue": "fixture-private-output"}}]}]}]}]}
            assert post(json.dumps(secret).encode())[0] == 200
            accepted = inventory()
            assert b"fixture-private-" not in b"".join(accepted.values())
            stop()
            (storage / "incoming.tmp").write_bytes(b"interrupted scratch")
            start(capacity=len(accepted))
            assert not (storage / "incoming.tmp").exists()
            assert post((fixtures / "complete.pb").read_bytes(), "application/x-protobuf")[0] == 200
            assert inventory() == accepted, "Restart retry changed accepted records"
            changed = json.loads(json.dumps(secret))
            changed["resourceSpans"][0]["scopeSpans"][0]["spans"][0]["spanId"] = "1123456789abcdef"
            assert post(json.dumps(changed).encode())[0] == 503
            assert inventory() == accepted, "Full storage evicted or overwrote evidence"
            stop()
            start(capacity=len(accepted) + 1)
            assert post(json.dumps(changed).encode())[0] == 200
            assert len(inventory()) == len(accepted) + 1
            print("PASS: actual OTLP JSON/protobuf/gzip, independent fixture parity, durable acknowledgements, "
                  "chunked limits, overload/timeout, content filtering, restart retry and capacity recovery.")
        finally:
            stop()


if __name__ == "__main__":
    main()
