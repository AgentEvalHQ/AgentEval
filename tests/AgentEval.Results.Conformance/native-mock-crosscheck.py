#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 AgentEval Contributors
"""Verify explicit mocked scan evidence through the CLI without providers."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[2]
tfm = sys.argv[1] if len(sys.argv) > 1 else "net10.0"
cli = ["dotnet", str(root / f"src/AgentEval.Cli/bin/Debug/{tfm}/AgentEval.Cli.dll")]


def invoke(args, expected, cwd):
    result = subprocess.run(cli + args, cwd=cwd, capture_output=True, text=True, timeout=60)
    assert result.returncode == expected, (args, result.returncode, result.stdout, result.stderr)
    return result


with tempfile.TemporaryDirectory(prefix="aef-mock-") as temporary:
    for family in ("owasp", "mitre", "nist", "gdpr", "eu-ai-act", "agentic", "perf", "autoaudit"):
        workspace = Path(temporary) / family
        store = workspace / ".agenteval"
        store.mkdir(parents=True)
        preset = "standard" if family in ("gdpr", "eu-ai-act") else "agentic-execution" if family == "agentic" else "smoke"
        args = ["bench", family, "--root", str(workspace), "--subject", "mock-subject",
                "--preset", preset, "--sut", "mock", "--input", "test input"]
        if family == "perf":
            args = ["bench", "perf", "latency", "--root", str(workspace), "--subject", "mock-subject", "--sut", "mock"]
        elif family == "autoaudit":
            args = ["bench", "autoaudit", "--sut", "mock"]
        invoke(args, 11, workspace)
        assert not list(store.iterdir()), "Default mock wrote legacy data or AEF"
        destination = workspace / "evidence"
        invoke(args + ["--aef", str(destination)], 11, workspace)
        assert not list(store.iterdir()), "Explicit mock wrote legacy data"
        headers = list(destination.rglob("run.json"))
        assert len(headers) == (3 if family == "autoaudit" else 1), headers
        for header_path in headers:
            run = header_path.parent
            header = json.loads(header_path.read_text())
            assert header["execution"]["targetMode"] == "mocked"
            assert not header.get("judges"), header
            gates = [json.loads(line) for line in (run / "gates.ndjson").read_text().splitlines()]
            assert len(gates) == 1 and gates[0]["exitCode"] == 11 and gates[0]["outcome"] == "inconclusive"
            for command in (cli + ["aef", "verify", str(run), "--json"],
                            [sys.executable, str(root / "contracts/aef/tools/aef_verify.py"), "run", str(run)]):
                result = subprocess.run(command, capture_output=True, text=True, timeout=60)
                report = json.loads(result.stdout)
                assert result.returncode == 0 and report["outcome"] == "intact" and not report["problems"], report
        disabled = workspace / "disabled"
        invoke(args + ["--aef", str(disabled), "--no-aef"], 11, workspace)
        assert not disabled.exists()
        blocked = workspace / "blocked"
        blocked.write_text("not a directory")
        invoke(args + ["--aef", str(blocked)], 3, workspace)
        assert not list(store.iterdir())
print(f"{tfm}: 32 actual CLI mock scenarios passed; 10 mocked runs accepted by CLI and Python")
