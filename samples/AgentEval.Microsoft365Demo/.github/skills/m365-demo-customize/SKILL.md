---
name: m365-demo-customize
version: 0.2.0
description: Change what the AgentEval Microsoft 365 demo tests. Covers another Microsoft 365 agent (--agent-id, from workiq list-agents or the Copilot Chat URL), the single prompt, the benchmark cases in a dataset JSON, a deterministic check in Benchmark.cs, the native118 profile, the verbosity, the turn cap and the timeouts. Use when someone wants the demo to test something different. For a first run use m365-demo-setup; to read a run folder use m365-demo-triage.
---

# m365-demo-customize

Run commands from this folder, and add `--config ../../workiq.json` to each, as the setup skill says. After a change of
agent or config, run `--readiness-only` first. After a code change, update `README.md` and the tests in
`../AgentEval.Microsoft365Demo.Tests` in the same change.

## Step 1: Target another agent

With no agent id the demo talks to Work IQ's default agent, Microsoft 365 Copilot itself. A declarative agent has its
own opaque id. Find it with the experimental `workiq list-agents` command of Microsoft's WorkIQ CLI, or in the agent's
Copilot Chat URL. Pass the whole id exactly as shown:

```bash
dotnet run -- --config ../../workiq.json --agent-id "U_abc.def" --readiness-only
```

`--agent-id` overrides `agentId` in `workiq.json` for one run. The id is one path segment, with no `/`, `\`, `?`, `#`
or space. `discovery` prints the card's name and version, so you see which agent answers. HTTP 404 there means a wrong
id, or an agent the user cannot reach. Word, Excel and PowerPoint agents are bound to their application and do not
answer usefully here.

## Step 2: Change the single prompt

```bash
dotnet run -- --config ../../workiq.json --i-understand-live-side-effects --no-benchmark \
  --prompt "In one sentence, what can you help me with?"
```

Keep it read-only: a prompt that makes the agent propose an action ends the turn `InputRequired`. The prompt step
passes when the turn completes with an answer. It fails (exit `1`), and the benchmark is skipped, on an empty answer or
a turn that does not complete. It is `NOT MEASURED` when several answer candidates came and none was selected; the
benchmark still runs.

## Step 3: Change the benchmark cases

The 5 default cases are embedded (`Data/m365-demo-cases.json`, taken from `../datasets/m365-smoke.json`). To run other
cases, copy the 8-case set and edit the copy, never the shared file:

```bash
cp ../datasets/m365-smoke.json my-cases.json
dotnet run -- --config ../../workiq.json --i-understand-live-side-effects --dataset my-cases.json
```

```json
{
  "id": "meeting_titles_explicit_date",
  "input": "List the meetings in my calendar on 2026-11-16 by title only. If there are none, say so plainly.",
  "evaluation_criteria": [
    "Lists meeting titles for 2026-11-16 only, or says plainly that there are none",
    "Does not invent meetings, attendees or times"
  ],
  "passing_score": 70
}
```

The format, as `DemoDataset.cs` reads it:

- A non-empty JSON array of objects; comments and trailing commas are allowed.
- `id` (required): 1 to 64 letters, digits, `.`, `_` or `-`, starting with a letter or digit. Unique, ignoring case.
  `prompt` is reserved. It names `evidence/<id>.json`.
- `input` (required): the prompt. Read-only, with explicit dates instead of "today". Add "If you cannot find it, say
  so plainly." when it depends on tenant content.
- `evaluation_criteria` (optional): one checkable statement per string. Only a judge grades them.
- `passing_score` (optional): a whole number from 0 to 100, default 70. `category` and `tags` are optional labels.
- Any other field, such as `expected` or a misspelt name, is ignored. The banner and `summary.md` name each one.
- A file that breaks a rule is a usage error (exit `2`) naming the case and the field. Nothing is sent.
- Each case is one agent turn. `--max-turns` (default 12) counts the prompt step too, so raise it above 11 cases.
- Never commit real tenant data (names, mail or document content) in a dataset.

The `/add-case` prompt does this step interactively.

## Step 4: Add a deterministic check

The checks live in `Benchmark.cs`; read it first. Each check is a `CheckOutcome(Name, Result, Detail)`, where `Result`
is `Pass`, `Fail`, `NotMeasured` (applies, could not be measured) or `NotRun` (does not apply). `Benchmark.Classify`
builds the list for each case. Any `Fail` makes the case `FAIL`; otherwise any `NotMeasured` makes it `NOT MEASURED`;
otherwise it is `PASS`. A turn that did not complete never reaches the checks.

1. Add a name constant next to `TurnCompleted` and the others, with an XML doc:
   `public const string CitesSource = "cites a source";`.
2. In `Classify`, in the branch where the turn completed, add the check to `list`. Read `turn.Evidence` and check a
   channel's `Availability` before its `Items`. Absence is never a pass and never a fail:

   ```csharp
   var sources = turn.Evidence.CitationSources;
   list.Add(sources.Availability != EvidenceAvailability.Provided
       ? new(CitesSource, CheckResult.NotMeasured, "citation sources " + sources.Describe("source(s)"))
       : sources.Items.IsDefaultOrEmpty
           ? new(CitesSource, CheckResult.Fail, "the citation channel was provided and is empty")
           : new(CitesSource, CheckResult.Pass, sources.Describe("source(s)")));
   ```

   A check that reads the answer reports `NotMeasured` when `Microsoft365Evidence.IsAnswerMeasured(turn)` is false,
   as *answer non-empty* does.
3. Add an offline test that fails without the check; `Benchmark.Classify` is `internal static`, so a test can call it.
   List the check in the README's "What a run does".

## Step 5: Profile, screen and limits

- `--profile native118` also asks for the tool and retrieval channels, through diagnostic modes observed in
  Microsoft's native evaluator 1.18.0. It is experimental, not a documented contract. A tenant may refuse the modes;
  the turn then fails and is reported, never retried. The default `public` profile leaves those channels
  `NotRequested`.
- `--verbosity` (`-v`): `quiet`, `normal` (default), `detailed` or `diagnostic`. It changes the screen only;
  `console.log` always holds the full view.
- `--max-turns <n>` (default 12): the most agent turns the run sends. Cases beyond it are `NOT MEASURED`.
- `--timeout-seconds <n>` (default 120): the most one readiness stage or one turn may take. It does not shorten a
  device-code sign-in.
- `--no-judge`: no judge call; the criteria are `NOT MEASURED`.
- `--capture-raw`: keeps the raw protocol traffic under `raw/`, tokens removed. It holds tenant content, so use it only
  when the maintainers ask.
- `--out <dir>` picks a new or empty run folder; `--zip` also writes `<run folder>.zip`.
