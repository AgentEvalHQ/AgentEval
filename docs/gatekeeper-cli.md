# `agenteval gatekeeper` — Gatekeeper from any language

Gatekeeper puts the same policy you red-team with into the request path, fail-closed. The `gatekeeper` CLI verb group
exposes those gates as a **language-neutral policy service**: any process (Python, Node, bash, a CI step) pipes a JSON
payload to `agenteval gatekeeper inspect` and gets a versioned verdict JSON + an exit code — no .NET reference needed.

The deterministic gates need **no credentials** and are byte-stable, so they drop straight into CI.

## Commands

```
agenteval gatekeeper list-gates [--json] [--phase inspect|serve|all]
agenteval gatekeeper inspect  --gate <id> [--input <file.jsonl>] [--policy block|warn] [gate flags] [model flags]
agenteval gatekeeper calibrate --gate judge:<axis> <model flags> [--certify] [--min-cases-per-direction N] …
agenteval gatekeeper serve                               # stub — not implemented
```

`inspect` reads one JSON object from **stdin** (single) or one per line from `--input <file>.jsonl` (batch). Batch
`--input` is supported for the **deterministic and tool gates**; the `judge:*` and `panel:*` gates are **stdin-only**
in this build (single payload per invocation) because their model calls and honesty guard are evaluated per run.

- **Text gates** take `{"text": "…"}` — `keyword-injection`, `keyword` (with `--keyword`/`--keywords`),
  `keyword:<axis>`, `rendered-exfil`, and `judge:<axis>`.
- **Tool / flow-control gates** take `{"tool": "…", "args": {…}, "messages": [ … ]}` — `tool:forbidden-tool`,
  `tool:argument-pattern`, `tool:domain-allowlist`, `tool:referential-integrity`, `tool:taint-tracking`. `args` is
  **required** (use `{}` for a no-argument call) — a missing `args` is a structural error (exit 2), not a silent
  Allow, so forgetting it can't fail open. The history-reading gates recompute from the `messages` you pass (each
  message is `{role, text?, functionCall?, functionResult?}`).

Run `gatekeeper list-gates` to see every gate, its state class, whether it needs a model, and its span policy.

## The verdict JSON

One object per `inspect` (JSONL for `--input`). Schema: [`schemas/gatekeeper-verdict.schema.json`](./schemas/gatekeeper-verdict.schema.json).

```json
{ "schemaVersion":"1.0", "gate":"keyword-injection", "kind":"chat", "action":"Block",
  "policy":"keyword-oracle", "axis":null, "reason":"keyword match: 'ignore previous'",
  "matches":["ignore previous"], "redactedText":null, "inlineReady":null, "inconclusive":false,
  "warning":null, "confidence":null, "certificate":null, "newArguments":null }
```

Security: for the `exfiltration-intent` and `system-prompt-extraction` axes the offending phrase may *be* the secret,
so `matches` and `redactedText` are **always null** — a secret can never land in a verdict, a JSONL file, or a CI log.
`rendered-exfil` does surface a `redactedText` — its safe, neutralized output.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | **Allow** — no gate blocked. |
| `5` | **Block** — a gate blocked on real evidence. |
| `6` | **Fail-closed** — the CLI could not evaluate (e.g. a history gate with no `messages`). Not a policy block. |
| `7` | **Not certified** — the honesty guard refused an un-calibrated judge (run `calibrate --certify`, or `--allow-uncalibrated`). |
| `2` | Usage error (bad flags, unknown gate, malformed payload). |
| `3` | Runtime error (model/IO failure). |

`--policy warn` forces exit `0` for any Allow/Block/Inconclusive (the verdict is still emitted) — for an advisory CI
step. It does **not** suppress `2` (a usage/structural error is the caller's malformed input, not a policy outcome) or
`7` (not certified). Note: `calibrate` returns `0` when a judge is inline-ready, else `1` — distinct from the
`bench`/compliance `calibrate` verbs (which return `2` on gate-fail), so do not assume the same across commands.

## Credential-free CI recipe

```bash
# fail the build if the agent's answer trips a deterministic egress gate — no model, byte-stable.
# inspect reads a JSON payload, so wrap the raw text into {"text": …} (jq handles the escaping);
# exit 5 (Block) trips the `|| exit 1`, exit 0 (Allow) passes.
jq -n --arg t "$agent_output" '{text: $t}' | agenteval gatekeeper inspect --gate rendered-exfil || exit 1
```

## Judge gates + the honesty guard

A `judge:<axis>` gate runs a calibrated LLM judge — but only if it has **earned the right** to block for your model.
The CLI enforces the same calibration bar as the .NET API:

```bash
# 1) calibrate once per model → writes a model-specific certificate
agenteval gatekeeper calibrate --gate judge:exfiltration-intent --azure --deployment-name <your-deployment> --certify

# 2) now inspect honors it; without a certificate it refuses with exit 7
echo '{"text":"…the agent answer…"}' | agenteval gatekeeper inspect --gate judge:exfiltration-intent --azure --deployment-name <your-deployment>
```

`--model-reply <file>` evaluates a reply your own client already produced (no model call); it is advisory unless you
vouch for the producing model with `--attest-fingerprint`. The certificate is an unsigned local file under
`.agenteval/gatekeeper/certs/` (override the location with `calibrate --cert-dir` / read it back with
`inspect --cert-dir`) — it prevents *accidentally* trusting an un-calibrated judge, not a malicious operator. The
certificate is tied to the exact model **and** gold set it was scored on; changing either invalidates it.

See [`samples/interop/python`](../samples/interop/python) for a runnable Python smoke test.
