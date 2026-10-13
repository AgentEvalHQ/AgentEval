#!/usr/bin/env python3
"""Exercise rebuild interruption and restart using fresh real .NET processes; no providers or network."""
import argparse
import hashlib
import json
from pathlib import Path
import queue
import subprocess
import tempfile
import threading


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    args = parser.parse_args()
    command = ["dotnet", str(args.fixture.resolve())]
    with tempfile.TemporaryDirectory(prefix="ae-rebuild-process-") as temporary:
        root = Path(temporary)
        target = root / "active.sqlite"

        def run(mode, *extra, expected=0):
            result = subprocess.run(command + [mode, str(target), *map(str, extra)], capture_output=True, text=True, timeout=30)
            assert result.returncode == expected, (mode, result.returncode, result.stdout, result.stderr)
            return json.loads(result.stdout) if result.stdout.strip() else None

        original = run("initialize")
        original_hash = hashlib.sha256(target.read_bytes()).hexdigest()
        holding = subprocess.Popen(command + ["hold", str(target)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        lines = queue.Queue()
        threading.Thread(target=lambda: lines.put(holding.stdout.readline()), daemon=True).start()
        try:
            assert lines.get(timeout=30).strip() == "READY_TO_KILL"
            holding.kill()
            holding.communicate(timeout=10)
        finally:
            if holding.poll() is None:
                holding.kill()
                holding.communicate(timeout=10)
        assert hashlib.sha256(target.read_bytes()).hexdigest() == original_hash
        candidates = list(root.glob(".ae-index-rebuild-*"))
        assert len(candidates) == 1
        interrupted = run("inspect", candidates[0])
        assert interrupted["Ready"] is False and interrupted["Candidate"] is None
        run("discard", candidates[0])
        assert not candidates[0].exists()
        assert run("read")["InstanceId"] == original["InstanceId"]

        checkpoint = root / "checkpoint.json"
        checkpoint.write_text(json.dumps(run("capture")), encoding="utf-8")
        prepared = run("prepare")
        directory = Path(prepared["Directory"])
        assert run("inspect", directory)["Ready"] is True
        run("promote-exit", directory, checkpoint, expected=74)
        after = run("read")
        assert after["InstanceId"] == prepared["Schema"]["InstanceId"]
        assert after["new"] is True and after["old"] is False
        retry = run("promote", directory, checkpoint)
        assert retry["AlreadyPublished"] is True
        backup = Path(retry["PreviousDatabasePath"])
        assert hashlib.sha256(backup.read_bytes()).hexdigest() == original_hash
        run("discard", directory)
        assert not directory.exists()
        assert run("read")["InstanceId"] == after["InstanceId"]
        other_root = root / "different-source"
        other_root.mkdir()
        assert run("rebind", other_root, expected=3)["Code"] == "root-binding-conflict"

        stale_checkpoint = root / "stale.json"
        stale_checkpoint.write_text(json.dumps(run("capture")), encoding="utf-8")
        stale = run("prepare")
        # Simulate a later target change within this uniquely owned fixture directory.
        target.write_bytes(b"later target bytes")
        refusal = run("promote", stale["Directory"], stale_checkpoint, expected=3)
        assert refusal["Code"] == "stale-rebuild-target"
        assert target.read_bytes() == b"later target bytes"
        run("discard", stale["Directory"])
        print("PASS: killed preparation, restart inspection/discard, committed replacement retry, exact backup, persistent root binding, stale-target refusal")


if __name__ == "__main__":
    main()
