---
description: Add a read-only benchmark case to a Microsoft 365 demo dataset JSON
agent: m365-demo
---

Add one benchmark case to a dataset of the AgentEval Microsoft 365 demo. Follow step 3 of
`.github/skills/m365-demo-customize/SKILL.md`; it holds the field rules.

- Dataset file: ${input:dataset:path to the dataset JSON (empty = create my-cases.json from ../datasets/m365-smoke.json)}
- What the case should test: ${input:intent:one sentence, for example "the agent lists my meetings on a given date"}

1. If the dataset file does not exist, copy `../datasets/m365-smoke.json` to it. Never edit the shared file itself.
2. Write the case with a read-only prompt and explicit dates. Put no real tenant data in it.
3. Check that the JSON parses and that the `id` is unique.
4. Tell me the command: `dotnet run -- --config ../../workiq.json --i-understand-live-side-effects --dataset <file>`.
   Say that each case is one agent turn, and that its criteria are graded only when a judge runs. Do not run it
   without my yes.
