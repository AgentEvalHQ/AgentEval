# Benchmark Report Skill

Opens the memory benchmark HTML report via a local HTTP server.

## Instructions

1. Kill any existing python http servers first.
2. Start a Python HTTP server from the **parent** `.agenteval/benchmarks/` directory (NOT an individual agent folder). This allows the report switcher dropdown to discover all agent reports.
3. Open the report URL in the default browser.
4. Run the server in the background so the user can keep working.

Available reports are at `.agenteval/benchmarks/{agent-name}/report.html`.
Known agents: `memoryagent`, `longmemeval-agent`.

## Steps (PowerShell / Windows)

```powershell
Stop-Process -Name python -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Set-Location .agenteval/benchmarks
Start-Process python -ArgumentList "-m http.server 8090" -WindowStyle Hidden
Start-Sleep -Seconds 1
Start-Process "http://localhost:8090/memoryagent/report.html"
```

To open LongMemEval instead:
```powershell
Start-Process "http://localhost:8090/longmemeval-agent/report.html"
```

## Steps (bash / Linux / macOS)

```bash
pkill -f "python.*http.server" 2>/dev/null; sleep 1
cd .agenteval/benchmarks && python3 -m http.server 8090 &
sleep 1
xdg-open "http://localhost:8090/memoryagent/report.html" 2>/dev/null \
  || open "http://localhost:8090/memoryagent/report.html" 2>/dev/null \
  || echo "Open http://localhost:8090/memoryagent/report.html in your browser"
```

To open LongMemEval instead, replace `memoryagent` with `longmemeval-agent` in the URL.

If port 8090 is busy, try 8091, 8092, etc.
The report has a dropdown switcher in the top-right to navigate between agent reports.
