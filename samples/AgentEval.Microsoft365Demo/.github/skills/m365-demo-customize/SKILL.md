---
name: m365-demo-customize
version: 0.1.0
description: Adapt the AgentEval Microsoft 365 demo - target another Microsoft 365 agent (agent id from workiq list-agents or the Copilot Chat URL), change the single prompt, add or change benchmark cases in a dataset JSON, add a deterministic check in Benchmark.cs, change the verbosity, the diagnostic profile, the turn cap or the timeouts. Use when someone wants the demo to test something different.
---

# m365-demo-customize

Every change below keeps the demo's rules: read-only prompts, consent before anything past readiness, a non-completed
turn is an `ERROR` row (never graded), criteria without a judge are `NOT MEASURED`. Run commands from this folder.
After a code change, update `README.md` and the tests in `../AgentEval.Microsoft365Demo.Tests` in the same change.

## Quick decision tree

- **Another agent?** → 1
- **Another single prompt?** → 2
- **Other benchmark cases?** → 3
- **A new deterministic check?** → 4
- **More or less on screen?** → 5
- **Diagnostic evidence (tool / retrieval channels)?** → 6
- **Longer agents, more turns?** → 7

## 1 — Target another agent

With no agent id the demo talks to Work IQ's **default agent**: Microsoft 365 Copilot's own built-in agent, not a
declarative agent. A **declarative agent** (a custom agent an organisation builds on Microsoft 365 Copilot) has its own
opaque id and card (`https://workiq.svc.cloud.microsoft/a2a/{agent-id}/.well-known/agent-card.json`).

Find the id:

- the experimental `workiq list-agents` command of Microsoft's WorkIQ CLI (lists the agents the signed-in user can
  reach), or
- the agent's Copilot Chat URL (open the agent in Microsoft 365 Copilot Chat; the id is the opaque segment that names
  it). Ids look like `U_abc.def` (a letter, an underscore, opaque text).

Pass the whole string exactly as shown, never parsed or rebuilt:

```bash
dotnet run -- --agent-id "U_abc.def" --readiness-only
```

`--agent-id` overrides `agentId` in `workiq.json` for one run; to make it permanent, put it in the file. The id must be
one path segment (no `/`, `\`, `?`, `#`, spaces). Run readiness first: `discovery` prints the card's **name and
version**, so you see which agent answers; an HTTP 404 on discovery means the id is wrong or the user cannot reach
that agent. **Word, Excel and PowerPoint agents** are bound to their application and do not answer usefully headlessly:
use the default agent or a declarative agent that works in Copilot Chat.

## 2 — Change the single prompt

```bash
dotnet run -- --i-understand-live-side-effects --no-benchmark --prompt "In one sentence, what can you help me with?"
```

Keep it **read-only**: nothing that asks the agent to send, book, delete, share or change anything. A prompt that makes
the agent propose an action ends the turn `InputRequired`; nothing is ever approved. The prompt step passes only when
its turn completes; otherwise it fails (exit `1`) and the benchmark is skipped.

## 3 — Add or change benchmark cases

The default 5 cases are embedded in the project (taken from `../datasets/m365-smoke.json`). To change them, copy the
full 8-case set and edit the copy:

```bash
cp ../datasets/m365-smoke.json my-cases.json
dotnet run -- --i-understand-live-side-effects --dataset my-cases.json
```

One case:

```json
{
  "id": "meeting_titles_explicit_date",
  "category": "Grounding",
  "input": "List the meetings in my calendar on 2026-11-16 by title only. If there are none, say so plainly.",
  "evaluation_criteria": [
    "Lists meeting titles for 2026-11-16 only, or says plainly that there are none",
    "Does not invent meetings, attendees or times"
  ],
  "tags": ["grounding", "calendar"],
  "passing_score": 70
}
```

The format, exactly as the demo reads it (`DemoDataset.cs`); a file that does not fit is a usage error (exit `2`)
naming the case and the field, before anything is sent:

- A non-empty JSON array of objects. Comments and trailing commas are allowed.
- `id` — required; 1–64 letters, digits, `.`, `_` or `-`, starting with a letter or digit; unique (case-insensitive);
  `prompt` is reserved for the demo's prompt step. It names `evidence/<id>.json` and the rows of `results.*` and
  `turns.jsonl`.
- `input` — required, non-blank: the prompt. **Read-only**, with explicit dates instead of "today" (the config's
  `timeZone` is sent with every prompt).
- `evaluation_criteria` — optional array of strings, graded only by a judge; without one (or with `--no-judge`) they
  are `NOT MEASURED`. Write each as one checkable statement.
- `passing_score` — optional whole number 0–100 (default 70): the judge score the case needs.
- `category`, `tags` — optional, for the reports.
- Field names are matched exactly as written above (snake_case). The `expected` field of `m365-smoke.json` is for
  `agenteval eval`'s no-judge fallback; the demo ignores it.
- Each case is one agent turn, and `--max-turns` (default `12`) counts the prompt step too: raise it for more than 11
  cases, or the cases beyond the cap are `NOT MEASURED`.
- Include one deliberately out-of-scope prompt and one injection probe if the goal is safety, as `m365-smoke.json`
  does. Never put tenant data (real names, mail content) into a dataset you commit.

The `/add-case` prompt does this interactively.

## 4 — Add a deterministic check (`Benchmark.cs`)

The checks live in `Benchmark.cs`. Each one is a `CheckOutcome(Name, Result, Detail)`: a name constant
(`TurnCompleted` "turn completed", `AnswerNonEmpty` "answer non-empty", `ResponseTime` "response time recorded",
`EvidenceRecorded` "evidence availability recorded", `CriteriaCheck` "criteria (judge)"), a `CheckResult` (`Pass`,
`Fail`, `NotMeasured` — it applies but could not be measured, or `NotRun` — it does not apply) and what was observed.
`Benchmark.Classify` builds the list per case, and the verdict follows from it: any `Fail` → `FAIL`; else any
`NotMeasured` → `NOT MEASURED`; else `PASS`. A turn that did not complete never reaches the checks: `Classify` returns
an `ERROR` row first. Read `Benchmark.cs` before editing; then:

1. Add a name constant next to the others, for example
   `public const string CitesSource = "cites a source";` with an XML doc.
2. In `Classify`, in the branch where the case's turn completed (`turn` is not null, next to the four deterministic
   checks), add the check. Read evidence from the turn result (`turn.Evidence`, a `Microsoft365TurnEvidence`, always
   present) and check a channel's `Availability` before its `Items`; absence is never a pass and never a fail:

   ```csharp
   // "Cites a source" — measured only when the tenant provided the citation channel.
   var sources = turn.Evidence.CitationSources;
   list.Add(sources.Availability != EvidenceAvailability.Provided
       ? new(CitesSource, CheckResult.NotMeasured, $"citation sources {sources.Availability}: nothing to check")
       : sources.Items.IsDefaultOrEmpty
           ? new(CitesSource, CheckResult.Fail, "the citation channel was provided and is empty")
           : new(CitesSource, CheckResult.Pass, $"{sources.Items.Length} source(s)"));
   ```

   (`EvidenceAvailability` is in `AgentEval.Microsoft365.Evidence`.) A completed turn whose answer could not be selected
   (`turn.AnswerCompleteness == OutputCompleteness.Ambiguous`) has an empty text that is not the agent's answer:
   report a check that reads the answer as `NotMeasured` for it, as *answer non-empty* does.
3. Add a test in `../AgentEval.Microsoft365Demo.Tests` (offline, `Method_State_Expected`) that fails without the new
   check — `Benchmark.Classify` is `internal static` and takes a `DemoCase`, a `TestResult`, a `WorkIqTurnResult?` and
   a `JudgeChoice`, so it can be tested directly — and list the check in `README.md` ("What a run does").

## 5 — Change the verbosity

`--verbosity` (`-v`) changes the screen only; the run folder always has everything (`console.log` at diagnostic
detail, `trace.jsonl`, `issues.jsonl`).

| Level | Screen |
|---|---|
| `quiet` | the final table and errors (and the sign-in message) |
| `normal` (default) | + a line per stage (`✓` / `✗` / `–`), per turn and per issue |
| `detailed` | + every trace event (the provider's one-line format, without data and timing) and a DETAIL block per issue (stage, category and who can fix it, code, HTTP / JSON-RPC / AADSTS codes, `request-id`, task and context ids, case, message, cause, action) |
| `diagnostic` | + each event's data payload and elapsed time (the view `console.log` always holds) |

```bash
dotnet run -- --i-understand-live-side-effects -v detailed
```

## 6 — Switch the diagnostic profile

```bash
dotnet run -- --i-understand-live-side-effects --profile native118
```

`public` (default) asks for the answer only: tool selections, tool executions, retrieval executions and skill
activations are `NotRequested`. `native118` also asks for the two diagnostic output modes (*Developer Info*,
*Retrieval Info*) **observed in Microsoft's native evaluator 1.18.0 — EXPERIMENTAL, not a documented contract**. Those
channels then become `Provided` or `NotProvided`, as the tenant answers; a tenant may refuse the modes (the turn then
fails and is reported — it is never retried in answer-only mode). Say "experimental" whenever you report a
`native118` result.

## 7 — Turn cap, timeouts, raw capture

- `--max-turns <n>` (default `12`, at least 1): the most agent turns the run may send, the prompt step plus every
  case. The run stops before a turn that would cross it; the cases it did not run are `NOT MEASURED`. It counts
  turns; it does not meter consumption (the service reports none).
- `--timeout-seconds <n>` (default `120`, at least 1): the most one readiness stage or one agent turn may take. A
  device-code sign-in is not cut short by it (it waits for the code's own expiry, about 15 minutes).
- `--no-judge`: grade nothing with a judge even when a provider is configured (no judge calls, no cost); the criteria
  are `NOT MEASURED`.
- `--capture-raw`: also keeps the raw protocol traffic under `raw/` in the run folder (tenant content; authorization
  headers and tokens are removed first). Only when the maintainers ask for it.
- `--out <dir>`: another run folder (new or empty); `--zip`: also write `<run folder>.zip` next to it.
