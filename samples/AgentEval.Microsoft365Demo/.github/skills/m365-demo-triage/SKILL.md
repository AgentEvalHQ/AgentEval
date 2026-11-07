---
name: m365-demo-triage
version: 0.4.0
description: Read a run folder of the AgentEval Microsoft 365 demo and say, for each problem, where it happened, why, what to do next and who can fix it. Reads the exit code, summary.md, readiness.md, issues.jsonl, turns.jsonl, results.json and trace.jsonl, and looks issue codes up in the troubleshooting catalogue of docs/microsoft365.md. Use when a run exited 1, 2, 3 or 11, a stage shows a cross, a case is ERROR or NOT MEASURED, or someone asks what went wrong. For a first run use m365-demo-setup; to change the demo use m365-demo-customize.
---

# m365-demo-triage

Read a run folder and answer three questions for each problem: where (the stage or the case), why (the category, the
code and the cause) and what next (the action, and who takes it). The folder is `m365-demo-runs/<yyyyMMdd-HHmmss>Z/`,
the `--out` folder, or an unzipped zip. Quote codes, ids and messages exactly as written. Run nothing that sends a
prompt.

## Step 1: The exit code

`summary.md` states the exit code and why; so does the end of `console.log`.

| Exit | Meaning | Look at |
|---|---|---|
| `0` | Every stage that ran passed and everything requested was measured. | Nothing to triage. |
| `1` | A stage failed (a readiness stage, or the prompt step), or a case failed a check. | The failed stage, or the `FAIL` rows. A case `FAIL` is a result about the agent, not a defect. |
| `11` | Nothing failed, but something was not measured. | `summary.md` says what: no consent (the run stopped after readiness), no judge, the prompt or a case `NOT MEASURED`, a case `ERROR`, the turn cap, or no config file. |
| `3` | A runtime error, or a file of the run (or the zip) could not be written. Every file that can be written still is. | The error lines of `console.log` and the last events of `trace.jsonl`. |
| `2` | A command-line mistake: an unknown option or value, a blank `--prompt`, a dataset that is missing or breaks the format, a non-empty `--out`. Nothing was created or sent. | The message on screen. |

## Step 2: Read the files in this order

1. **`summary.md`**: the result, the settings, the stage table, the case table, and the issues grouped by who can fix
   them, with cause and action. Most answers are here.
2. **`readiness.md`**: the failed stage's row and evidence: the AADSTS and MSAL codes for `auth`, the HTTP status,
   `request-id` and interfaces for `discovery`, the HTTP status and JSON-RPC code for `protocol`.
3. **`issues.jsonl`**: one issue per line, with `stage`, `category`, `code`, `message` and `suggestedCause`, plus the
   codes and ids that are known (`aadstsCode`, `httpStatus`, `rpcErrorCode`, `requestId`, `taskId`, `caseId`, …). An
   unknown fact is left out, never written as `0` or `""`.
4. **`turns.jsonl`**: one line per turn, with `caseId` (`prompt` for the prompt step), `outcome`,
   `answerCompleteness`, the ids, timings, the `evidence` line, and the `failure` of a turn that did not complete.
   **`results.md`** and **`results.json`** give each case's verdict and every check.
5. **`trace.jsonl`**: only to place an issue in time, by the events just before it with the same `caseId` or `taskId`.

`console.log` is the screen transcript plus every trace event; use it when the user asks what the run printed. The
screen shows each distinct issue once and counts repeats, so count in `issues.jsonl`. `aef/` is the benchmark as a
sealed AEF run: triage does not need it, and nobody edits it. Checking it is optional. The demo installs no
`agenteval` command, so from this folder run
`dotnet run --project ../../src/AgentEval.Cli --framework net10.0 -- aef verify "<AEF run folder>"`, with the quoted
path the final table prints. It must say `intact`.

## Step 3: Look up the cause

[Troubleshooting](../../../../../docs/microsoft365.md#troubleshooting) in `docs/microsoft365.md` is the catalogue of
messages, issue codes, causes and fixes. From this folder it is `../../docs/microsoft365.md`; Copilot may ask before
it reads a file outside the workspace. Find the issue's code or message there, and quote the issue's own
`suggestedCause` too.

A turn that did not complete is coded `turn.input_required`, `turn.auth_required`, `turn.failed`, `turn.rejected`,
`turn.canceled` or `turn.indeterminate`. Its category comes from its failure: a turn refused with HTTP 403 is
`turn.failed` in `Permission`. With no failure detail, `turn.input_required` is `Case`, `turn.auth_required` is
`Authentication`, `turn.failed`, `turn.rejected` and `turn.canceled` are `Service`, and `turn.indeterminate` is
`Unknown`. `doctor.<stage>.failed` means a readiness stage failed and no lower layer filed the
cause; `readiness.md` has the detail. For a sign-in or connection failure (an `AADSTS` code, HTTP `401` or `403`),
[If a step fails](../../../../../docs/microsoft365-tenant-setup.md#if-a-step-fails) in the tenant setup guide names the step that fixes it.

One code comes from the demo itself: `judge.call_failed`, any failed judge call (HTTP 401, 403, 404, an outage). The
agent was measured; only the criteria are `NOT MEASURED`. Check `AI_INFERENCE_PROVIDER` and its variables, or use
`--no-judge`.

## Step 4: Who can fix it

The category says who acts. The demo prints it on each issue and groups `summary.md` by it.

| Category | Who can fix it |
|---|---|
| `Config` | the tester: the config file or the options |
| `Authentication` | the sign-in: the tester, or the app registration |
| `Permission` | an administrator: admin consent, the billing plan, Work IQ enabled ([setup](../../../../../docs/microsoft365-tenant-setup.md#if-a-step-fails)) |
| `Network` | the network between this machine and Work IQ |
| `Protocol` | the connector maintainers: the reply is not the A2A 1.0 shape |
| `Service` | the Work IQ service: keep the `request-id` |
| `Capture` | the raw capture settings; the run itself is unaffected |
| `Budget` | the run's limits: `--max-turns`, `--timeout-seconds` |
| `Case` | the tester: the agent paused for a confirmation or input; reword the case to ask for information only |
| `Unknown` | not classified: send the folder back |
| Judge (`judge.*` codes) | the judge model's provider settings, not the agent or Work IQ |

`turn.auth_required` is `Authentication`, but the demo's own sign-in worked: a connector the agent uses needs the
test user's sign-in. Sign in to that connector once in Microsoft 365 Copilot Chat, then run again.

## Step 5: Verdicts and outcomes

- `ERROR`: the turn did not complete (`InputRequired`, `AuthRequired`, `Failed`, `Rejected`, `Canceled` or
  `Indeterminate`), or the agent failed before a turn result. Never graded.
- `NOT MEASURED`: nothing failed, but a check could not be measured. The causes are no judge, no verdict, a failed
  judge call, the turn cap, or several answer candidates and none selected (`answerCompleteness` `Ambiguous`;
  `evidence/<caseId>.json` keeps every candidate).
- `FAIL`: an empty answer, no answer artifact, or a judge score below `passing_score`.
- `PASS`: every check that applies ran and passed.

The prompt step fails (exit `1`) and skips the benchmark on an empty answer or a turn that did not complete. Several
answer candidates with none selected make it `NOT MEASURED`, and the benchmark still runs.

`Indeterminate` means no final task state was observed. The prompt may have reached the service and was not resent:
give the task id, and if it recurs ask for both run folders. `InputRequired` means the agent paused for a
confirmation or input, and nothing was approved. For a read-only prompt that is unexpected: name the case and suggest
a read-only wording.

The `evidence` line reads like
`answer Present; citation markers Provided (2 marker(s)); tool selections NotRequested; …`. `NotRequested`: not asked for (the tool and retrieval channels under the default `public` profile). `NotProvided`:
asked for, nothing came back. `Provided (observed empty: …)`: it came back with no items. Citation links are usually
`Unresolved`, as expected.

## Step 6: What to tell the user

For each problem: the stage or case, the category and code as written, the `request-id` when there is one, the cause
in one sentence, and the next action with who takes it. Group repeats of one code into one line with a count. If the
cause is on the service's side, say so and ask for the zip: nothing on the tester's side causes it. That is the
category `Protocol` or `Service`, or an unexpected JSON-RPC answer, except `judge.*` codes.
