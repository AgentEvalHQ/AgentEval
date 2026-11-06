---
description: Triage a Microsoft 365 demo run folder - what failed, where, why, what to do next and who can fix it
agent: m365-demo
---

Triage this run of the AgentEval Microsoft 365 demo. Follow `.github/skills/m365-demo-triage/SKILL.md`.

Run folder: ${input:runFolder:path to the run folder (empty = the newest folder under m365-demo-runs/)}

Start with the exit code and what it means. Then cover every failed or skipped stage, every `FAIL`, `ERROR` and
`NOT MEASURED` case, and every issue, as the skill's last step describes. Say plainly which problems are on my side
and which need the zip sent back. Quote answer text or tenant names only as far as the explanation needs.
