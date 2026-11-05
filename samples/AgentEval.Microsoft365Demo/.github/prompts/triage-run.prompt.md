---
description: Triage a Microsoft 365 demo run folder - what failed, where, why, and what to do next
agent: m365-demo
---

Triage this run of the AgentEval Microsoft 365 demo, following `.github/skills/m365-demo-triage/SKILL.md`.

Run folder: ${input:runFolder:path to the run folder (empty = the newest folder under m365-demo-runs/)}

1. Read, in this order: `summary.md`, `readiness.md`, `issues.jsonl`, `turns.jsonl`, `results.json`, and
   `trace.jsonl` only to place an issue in time.
2. Start with the exit code (from `summary.md` or `console.log`) and what it means.
3. For every failed or skipped stage, every `FAIL`, `ERROR` and `NOT MEASURED` case and every issue: the stage or
   case id, the category and code exactly as written, the AADSTS / HTTP / JSON-RPC code and the `request-id` when
   present, the cause in one sentence, and the next action.
4. Group repeated issues (same category and code) into one line with a count.
5. Say plainly which problems are on my side (config, sign-in, permissions, network) and which are on the service's
   side (protocol, 5xx, unexpected JSON-RPC answers) and need the zip sent back.

Do not run anything that sends a prompt, do not suggest resending an `Indeterminate` turn, and do not quote answer
text or tenant names back beyond what the explanation needs.
