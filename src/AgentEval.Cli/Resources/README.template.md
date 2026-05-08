# .agenteval/

This folder is the canonical AgentEval workspace. It holds run manifests,
scenario results, baselines, history, compliance evidence, and red-team
campaigns for every subject (agent or workflow) evaluated in this solution.

## Layout

- `solution.json` — solution-level identity (UUID, name).
- `subjects/agents/{Name}/` and `subjects/workflows/{Name}/` — per-subject
  data: `subject.json`, `baseline.json`, `history.jsonl`, `runs/{runId}/`.
- `runs/{runId}/manifest.json|summary.json|scenarios/|traces/|reports/`.
- `compliance/{regulation}/{subject}/{ts}/evidence.json` — audit-chain
  compliance evidence linked to a source run.
- `red-team/{campaign}_{ts}/` — adversarial campaign artifacts.
- `runs-index/recent.jsonl` — append-only log of completed runs.

Run `agenteval doctor` to validate this folder. Run `agenteval migrate` to
move legacy artifacts (e.g. `.AgentEval/`, `TestResults/traces/`) into the
canonical layout.

See https://github.com/AgentEvalHQ/AgentEval for documentation.
