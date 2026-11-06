---
description: Run the Microsoft 365 demo safely - readiness first, then the full run only after the user confirms a non-production identity and gives consent
agent: m365-demo
---

Run the AgentEval Microsoft 365 demo from this folder. Follow `.github/skills/m365-demo-setup/SKILL.md`.

Settings for this run (empty means the default):

- Agent id: ${input:agentId:empty for Work IQ's default agent (Microsoft 365 Copilot)}
- Verbosity: ${input:verbosity:quiet, normal, detailed or diagnostic (default normal)}

Do this in order, and stop at the first problem:

1. Check that `../../workiq.json` exists: the repository root, where the README's quick start puts it. Add
   `--config ../../workiq.json` to every command below. If it does not exist, walk me through setup steps 1 and 2.
   Never guess its values, and never print its ids back in full.
2. Run `dotnet run -- --readiness-only`. Add `--agent-id "<id>"` and `--verbosity <level>` when I gave them.
3. Read the new run folder's `summary.md` and `readiness.md` with me. If a stage failed, explain it with the triage
   skill and stop.
4. Ask me to confirm the non-production identity and my consent. If a judge provider is configured (an exported
   `OPENAI_API_KEY` is enough), say the number of judge calls. Wait for my yes.
5. After that yes only: `dotnet run -- --i-understand-live-side-effects --zip`, with the same agent id and verbosity.
6. Report the exit code, the stage table, each case's verdict and the zip's path. For each `ERROR` row, give the
   outcome and the task id.
