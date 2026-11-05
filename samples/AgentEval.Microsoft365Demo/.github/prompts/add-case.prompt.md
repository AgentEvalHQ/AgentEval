---
description: Add a read-only benchmark case to a Microsoft 365 demo dataset JSON
agent: m365-demo
---

Add one benchmark case to a dataset of the AgentEval Microsoft 365 demo, following step 3 of
`.github/skills/m365-demo-customize/SKILL.md`.

- Dataset file: ${input:dataset:path to the dataset JSON (empty = create my-cases.json from ../datasets/m365-smoke.json)}
- What the case should test: ${input:intent:one sentence, for example "the agent lists my meetings on a given date"}

1. If the dataset file does not exist, copy `../datasets/m365-smoke.json` to it first (never edit the shared
   `../datasets/m365-smoke.json` itself).
2. Write the case with exactly these field names: `id` (required; 1–64 letters, digits, `.`, `_` or `-`, starting with
   a letter or digit, unique, not `prompt`), `input` (required), `evaluation_criteria` (one checkable statement each,
   written for a judge), `passing_score` (0–100, default 70), `category` and `tags`. The demo ignores `expected`.
3. The `input` must be **read-only**: nothing that sends, books, deletes, shares or changes anything. Use explicit
   dates (`2026-11-16`), never "today". Add "If you cannot find it, say so plainly." to any prompt that depends on
   tenant content.
4. Never put real tenant data (people's names, mail or document content) into the file.
5. Check the JSON parses and the `id` is unique. Tell me the command to run it —
   `dotnet run -- --i-understand-live-side-effects --dataset <file>` — that each case is one agent turn (raise
   `--max-turns`, default 12 including the prompt step, for more than 11 cases), and that the criteria are graded only
   when a judge provider is configured and `--no-judge` is not given (otherwise `NOT MEASURED`). Do not run it yourself
   without my explicit yes.
