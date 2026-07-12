# Gatekeeper from Python (and any language)

`agenteval gatekeeper` turns Gatekeeper's runtime gates into a **language-neutral policy service**: a non-.NET
process shells out, passes a JSON payload on stdin, and gets a versioned verdict JSON + an exit code back — no .NET
reference, no FFI, no model for the deterministic gates.

`gatekeeper_smoke.py` is a pure-stdlib proof (no pip deps).

## Run

Against a locally built CLI (after `dotnet build src/AgentEval.Cli`) — `AGENTEVAL_BIN` points at the built
`AgentEval.Cli.dll` (its path varies by build config and target framework, so let the shell find it):

```bash
# from the repo root
AGENTEVAL_BIN="dotnet $(ls src/AgentEval.Cli/bin/*/*/AgentEval.Cli.dll | head -1)" \
  python samples/interop/python/gatekeeper_smoke.py
```

Against the installed dotnet tool (`dotnet tool install --global AgentEval.Cli --prerelease`):

```bash
python samples/interop/python/gatekeeper_smoke.py     # uses `agenteval` on PATH
```

## The contract

- **Verdict JSON** — schema at `src/AgentEval.Cli/Schemas/gatekeeper-verdict.schema.json` (mirrored under
  `docs/schemas/`). Validate against it from any language.
- **Exit codes**: `0` Allow · `5` Block · `6` fail-closed (the CLI couldn't evaluate — e.g. a history gate with no
  `messages`) · `7` not certified (the honesty guard) · `2` usage · `3` runtime. A CI step can gate on these:
  `if ! agenteval gatekeeper inspect …; then fail; fi`.

## What the smoke test shows

1. deterministic **Block** (`keyword-injection`) — credential-free, exit 5
2. deterministic **Allow** — exit 0
3. tool flow-gate **Block** on an invented id (`tool:referential-integrity`) — recomputes from the passed `messages`
4. **fail-closed** exit 6 when a history gate gets no `messages` (never a silent Allow)
5. `rendered-exfil` surfaces the **sanitized** `redactedText` (secret-free)
6. `list-gates --json` discovery
7. the **honesty guard**: an uncertified `judge:*` gate refuses with exit 7 (run `gatekeeper calibrate … --certify`
   first), and the two sensitive judge axes never echo `matches`/`redactedText`

## Using it from your own code

```python
import json, subprocess
def inspect(gate, payload, *flags):
    p = subprocess.run(["agenteval", "gatekeeper", "inspect", "--gate", gate, *flags],
                       input=json.dumps(payload), capture_output=True, text=True)
    return p.returncode, (json.loads(p.stdout) if p.stdout.strip() else None)

code, verdict = inspect("keyword-injection", {"text": user_or_agent_text})
if code == 5:            # blocked
    handle_block(verdict["reason"])
```

Lead with the **deterministic** gates for CI (zero credentials, byte-stable). For the `judge:*` gates, calibrate once
per model with `gatekeeper calibrate --gate judge:<axis> --model … --certify`, then `inspect` will honor the
certificate. The `gatekeeper serve` command (for stateful accumulator gates like budgets and sequences) is currently a stub — not implemented.
