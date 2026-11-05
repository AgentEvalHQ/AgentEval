---
description: Run the Microsoft 365 demo safely - readiness first, then the full run only after the user confirms a non-production identity and gives consent
agent: m365-demo
---

Run the AgentEval Microsoft 365 demo from this folder, following `.github/skills/m365-demo-setup/SKILL.md`.

Settings for this run (empty = the default):

- Agent id: ${input:agentId:empty for Work IQ's default agent (Microsoft 365 Copilot)}
- Verbosity: ${input:verbosity:quiet, normal, detailed or diagnostic (default normal)}

Do this, in order, and stop at the first problem:

1. Check that `workiq.json` exists here (or that `M365_CONFIG_PATH` points at a file). If not, walk me through
   setup steps 1–2; do not create the file with values you guessed. Never print its tenant or app id back in full.
2. Run the readiness check only — no consent flag, no prompt is sent:
   `dotnet run -- --readiness-only` plus `--agent-id "<id>"` and `--verbosity <level>` when I gave them.
3. Read the new run folder's `summary.md` and `readiness.md` and tell me per stage: passed, failed or skipped, and
   for a failure the code, the cause and the action (use `.github/skills/m365-demo-triage/SKILL.md`). Stop here if a
   stage failed.
4. Ask me to confirm that the tenant and user in `workiq.json` are **non-production** and that I consent to the agent
   acting as that user. If a judge provider is configured in my environment, also say that each judge call is billed
   and how many cases will be judged. Wait for my explicit yes.
5. Only after that yes: `dotnet run -- --i-understand-live-side-effects --zip` with the same agent id and verbosity.
6. Report the exit code, the stage table, the per-case verdicts (`PASS` / `FAIL` / `ERROR` / `NOT MEASURED`) and the
   path of the zip. For every `ERROR` row give the outcome and the task id; never re-run a prompt that ended
   `Indeterminate`.
