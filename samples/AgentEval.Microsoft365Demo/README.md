# Microsoft 365 demo: check, prompt and benchmark a Microsoft 365 agent

This console app tests a Microsoft 365 agent over **Work IQ A2A 1.0**: a connection check, one read-only prompt and a
five-case benchmark. It writes everything into **one run folder** that you zip and send back. It uses the real provider
and never a mock.

> **Testers start here.** Read [Before you start](#before-you-start), [Quick start](#quick-start) and
> [What to send back](#what-to-send-back). The [tester guide](../../docs/microsoft365-tester-guide.md) has the portal
> steps for the prerequisites, and a command-line path only for when the maintainers ask.

> **Status: Experimental.** The maintainers have no tenant and have not run this demo live. Your run is the first live
> evidence. Use a **non-production** tenant and test user only.

## Before you start

You need, in a non-production tenant:

- Work IQ enabled, and the test user in a usage-based billing plan (Work IQ calls are billed as Copilot Credits);
- an Entra app registration that is a public client, with the delegated permission `WorkIQAgent.Ask` and admin
  consent; its tenant id and client id;
- a document titled **"Demo Q3 Planning"** in the test user's OneDrive or SharePoint ([why](#the-grounding-case));
- the .NET 10 SDK and git;
- optionally, a judge model for the benchmark's criteria ([The judge](#the-judge-optional)).

[Connect to a Microsoft 365 tenant](../../docs/microsoft365-tenant-setup.md) gives every step in order, who does
it, and the first connection test. [Microsoft 365 at a glance](../../docs/microsoft365-at-a-glance.md) shows how the
pieces fit, with screenshots.

The permission covers reads **and writes** of the test user's data. The demo never approves an action or resends a
prompt, and every step after the readiness check needs your consent.

## Quick start

Run every command from the repository root: the `AgentEval` folder the clone creates. The demo reads `./workiq.json`
there and writes its run folders to `./m365-demo-runs/`. From another folder, add `--config <path to workiq.json>`.

```bash
git clone https://github.com/AgentEvalHQ/AgentEval.git
cd AgentEval
cp samples/datasets/m365-config.template.json workiq.json     # then fill in tenantId and appClientId
dotnet run --project samples/AgentEval.Microsoft365Demo -- --readiness-only
dotnet run --project samples/AgentEval.Microsoft365Demo -- --i-understand-live-side-effects --zip
```

If the maintainers gave you a branch or a tag, run `git checkout <name>` right after `cd AgentEval`. If `dotnet run`
cannot find `samples/AgentEval.Microsoft365Demo`, the checkout has no demo: ask the maintainers which branch to use.

1. `--readiness-only` checks the config, signs you in, fetches the agent card and checks the protocol. **No prompt is
   sent.** The first time, a message asks you to open `https://microsoft.com/devicelogin` and enter a code. Do it on any
   device and sign in **as the test user**. The token is cached outside the repository, so later runs sign in silently.
   This step must exit `0`. If a stage fails, `summary.md` in the run folder says what to do, and the
   [troubleshooting catalogue](../../docs/microsoft365.md#troubleshooting) lists every message.
2. The second run repeats the check, sends one prompt and runs the benchmark. It prints the judge it will use before
   any judge call, then zips the run folder and prints the zip's path. An exported judge key spends money on each
   case: add `--no-judge` if you do not want that ([The judge](#the-judge-optional)).
3. Send back the zip and the exit code. Read [What to send back](#what-to-send-back) first.

Without a config file the demo says how to create one, contacts nothing and exits `11`.

**The config file.** `workiq.json` holds no secret. From the app registration's *Overview* page, put the *Directory
(tenant) ID* in `tenantId` and the *Application (client) ID* in `appClientId`. Set `timeZone` to your IANA time zone,
such as `Europe/Zurich`, and leave the rest. A `REPLACE-WITH-…` value is refused before anything is sent. Git ignores
`workiq.json`; never send it back.

**Which agent.** With `agentId` `null`, the demo talks to Microsoft 365 Copilot itself. To test a declarative agent,
pass `--agent-id <id>` (an id such as `U_abc.def`). When it differs from the config's `agentId`, the run names the
agent by that id, not by the config's `agentName`.

## What a run does

1. **Banner**: the plan, with the agent, the tenant, the judge and the limits.
2. **Readiness**: `config`, `auth` (the sign-in), `discovery` (the agent card) and `protocol` (a handshake). No prompt
   reaches the agent.
3. **Prompt**: `--prompt` as one turn.
4. **Benchmark**: five read-only cases from `samples/datasets/m365-smoke.json` through `MAFEvaluationHarness`, one
   fresh conversation each.
5. **Summary**: the final table and the run folder.

Steps 3 and 4 need consent: `--i-understand-live-side-effects` or `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`. Without
it the run stops after readiness and exits `11`. A failed readiness stage skips steps 3 and 4; a failed prompt skips
step 4.

**The prompt step** passes when the turn completes with an answer. It fails (exit `1`) on an empty answer or a turn
that does not complete. It is NOT MEASURED when several answer candidates came and none was selected.

**Every benchmark case** gets one verdict. Each case runs the checks *turn completed*, *answer non-empty*, *response
time recorded*, *evidence availability recorded* and *criteria (judge)*; `results.md` shows what each observed.

| Verdict | Meaning |
|---|---|
| `PASS` | Every check that applies passed. |
| `FAIL` | A check failed: an empty answer, no answer, or a judge score below the passing score. A result about the agent. |
| `ERROR` | The turn did not complete (`InputRequired`, `AuthRequired`, `Failed`, `Rejected`, `Canceled`, `Indeterminate`), or the agent failed before a turn result. Partial text is never graded. |
| `NOT MEASURED` | Nothing failed, but a check was not measured: no judge, a judge failure, no answer selected among several, or the turn cap. Never a pass. |

### The grounding case

`grounded_summary_with_citation` asks for a summary of "Demo Q3 Planning" with a citation. It also passes when the agent
says plainly that it cannot find the document, and then grounding and citations are not measured. Create the document
for this case to mean anything.

## The judge (optional)

A judge model grades each case's `evaluation_criteria`. The demo reads it from the environment, like every AgentEval
sample:

| `AI_INFERENCE_PROVIDER` | Variables |
|---|---|
| `azure` | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT` |
| `bitdeer` | `BITDEER_API_KEY` (`BITDEER_ENDPOINT`, `BITDEER_MODEL` optional) |
| `openai` | `OPENAI_API_KEY` (`OPENAI_BASE_URL`, `OPENAI_MODEL` optional) |
| `foundry` | `FOUNDRY_ENDPOINT`, `FOUNDRY_API_KEY`, `FOUNDRY_MODEL` |
| `openai-compatible` | `OPENAI_COMPATIBLE_ENDPOINT`, `OPENAI_COMPATIBLE_MODEL` (`OPENAI_COMPATIBLE_API_KEY` optional) |

With `AI_INFERENCE_PROVIDER` unset, the first provider in this table whose required variables are set is used. **An
exported `OPENAI_API_KEY` is enough: the demo uses it, and each judge call costs money.** `--no-judge` makes no judge
call. The CLI's `--judge` option and `AZURE_OPENAI_JUDGE_*` variables are not read here.

Before any judge call, at every verbosity, the demo prints the judge and why it chose it, for example
`judge: OpenAI, model … (auto-detected from OPENAI_API_KEY; AI_INFERENCE_PROVIDER is not set)`. The key is never shown.

Without a judge, the criteria are NOT MEASURED and the run exits `11` when nothing failed. A failed judge call (HTTP
401, 403, 404, an outage) is the judge's problem: the case keeps its other checks, and its criteria are NOT MEASURED.
Each failed call registers a `judge.call_failed` issue, which `summary.md` and the final table list under **Judge**.

## The run folder

`./m365-demo-runs/<yyyyMMdd-HHmmss>Z/` (UTC) unless `--out` says otherwise. It is written on every run once the command
line is valid and a config file exists, whatever failed.

| File | What it holds |
|---|---|
| `summary.md` | **Read this first.** The result, the settings, the stages, the cases, the issues grouped by who can fix them. |
| `console.log` | Everything printed, plus every trace event in full. |
| `readiness.md`, `readiness.json` | The readiness report: each stage's result, meaning, action and evidence. |
| `trace.jsonl` | Every trace event, one per line. |
| `issues.jsonl` | Every warning and error with its category, code, cause, AADSTS code and `request-id`. |
| `turns.jsonl` | One line per turn: case id (`prompt` for the prompt step), outcome, answer completeness, ids, timings, the evidence line, failure codes. Never the answer text. |
| `evidence/<case>.json` | Each turn's evidence document: the answer, citations and every evidence channel. |
| `results.md`, `results.json` | Per case: the verdict, every check, and the judge's score and explanation. |
| `aef/` | When the benchmark ran: the benchmark as a sealed AEF run under `aef/<yyyy>/<MM>/<run id>/`, with each case's prompt, answer, state and evidence document. Never edit it. |
| `raw/` | With `--capture-raw`: the raw protocol traffic. |
| `<run folder>.zip` | With `--zip`, next to the run folder. |

**The AEF run** is the benchmark in AgentEval's evidence format. Each case's state is its verdict: PASS is `passed`,
FAIL is `failed`, ERROR is `error`, and NOT MEASURED is `not_measured` (`error` when the judge's call failed or gave no
verdict). A benchmark that stops early closes the run aborted (`turn-cap-reached`, `cancelled` or `evaluation-failed`),
and the cases it did not reach have no line. Checking it is optional. The demo installs no `agenteval` command, so run
this from the repository root, with the quoted path the final table prints after `aef verify`. It must print `intact`.

```bash
dotnet run --project src/AgentEval.Cli --framework net10.0 -- aef verify "<AEF run folder>"
```

The evidence line in `turns.jsonl`, `results.json` and `summary.md` gives the state of each evidence channel:

```text
answer Present; citation markers Provided (2 marker(s)); citation sources NotProvided; tool selections NotRequested; …
```

`NotRequested`: the demo did not ask. `NotProvided`: nothing came. `Provided (observed empty: 0 source(s) in this
snapshot)`: the channel came empty. A word after the count, such as `Conflicting`, `Partial` or `Malformed`, means the
channel came but is not fully valid.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Everything that ran passed and was measured (`--readiness-only`: readiness passed). |
| `1` | A stage failed (readiness or the prompt), or a case failed a check. |
| `2` | A command-line mistake, such as an unknown option, a missing or invalid `--dataset` file, or a non-empty `--out` folder. Nothing is created or sent. |
| `3` | A runtime error, or a file of the run (or the zip) could not be written. |
| `11` | Nothing failed, but something was not measured: no consent, no judge, a case `ERROR`, the prompt or a case `NOT MEASURED`, the turn cap, or no config file. |

The exit code is decided once, in that order: `3`, then `1`, then `11`, else `0`. `results.json`, `results.md`,
`summary.md` and the final table all show it. A later failure to write `console.log` or the zip turns it into `3`: the
reports are written again, and the screen says `The result is now ERROR (exit 3) …`.

## What to send back

1. The zip the run printed, or the run folder zipped.
2. The exit code and anything that surprised you. The
   [results template](../../docs/microsoft365-tester-RESULTS-TEMPLATE.md) helps if you have time.

The run folder holds your tenant, app and agent ids, `request-id`s and file paths with your user name. It holds the
agent's **answers** in `evidence/`, `aef/` and `console.log`, and with `--capture-raw` everything the service sent. The
judge's explanations in `results.md` and `results.json` may quote the answers. `trace.jsonl`, `issues.jsonl` and
`turns.jsonl` hold no prompt or answer. No file holds a token, an auth code, the `Authorization` header, `workiq.json`
or the token cache. The device code shows as `[redacted]`.

If something cannot be shared, redact the **unzipped** folder, then zip it yourself. The commands below replace e-mail
addresses, URLs and your tenant id in every text file of one run folder, in place: keep a copy. They skip `aef/`,
because it is sealed and any edit makes it invalid. If `aef/` holds something you cannot share, delete it and say so.

```bash
# Bash (Linux, macOS, Git Bash). RUN is the run folder the demo printed, with no trailing slash.
export TENANT="<your-tenant-guid>" RUN="m365-demo-runs/<run folder>"
find "$RUN" -path "$RUN/aef" -prune -o -type f \( -name '*.jsonl' -o -name '*.json' -o -name '*.md' -o -name '*.log' -o -name '*.sse' \) -print0 \
  | xargs -0 perl -pi -e 's/[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/[email]/g; s#https?://[^"\s)\x60]+#[url]#g; s/\Q$ENV{TENANT}\E/[tenant]/gi'
```

```powershell
# PowerShell. $run is the run folder the demo printed.
$tenant = "<your-tenant-guid>"; $run = "m365-demo-runs\<run folder>"
$aef = (Join-Path (Resolve-Path $run).Path 'aef') + [IO.Path]::DirectorySeparatorChar
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($f in Get-ChildItem $run -Recurse -File -Include *.jsonl, *.json, *.md, *.log, *.sse) {
  if ($f.FullName.StartsWith($aef, [StringComparison]::OrdinalIgnoreCase)) { continue }
  $t = [IO.File]::ReadAllText($f.FullName)
  $t = $t -replace '[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[email]'
  $t = $t -replace 'https?://[^"\s)\x60]+', '[url]'
  $t = $t -replace [regex]::Escape($tenant), '[tenant]'
  [IO.File]::WriteAllText($f.FullName, $t, $utf8)
}
```

Then read `evidence/`, `console.log`, `results.md` and `results.json`, and replace by hand any name you cannot share
with `[name]`.
Keep the `request-id` values: Microsoft support uses them to find a call.

## Options

`dotnet run --project samples/AgentEval.Microsoft365Demo -- --help` describes each option in full.

| Option | What it does |
|---|---|
| `--config <file>` | The connection file. Default `./workiq.json`, or the path in `M365_CONFIG_PATH`. |
| `--agent-id <id>` | The agent to test. Default: the config's `agentId`. |
| `-v`, `--verbosity <level>` | The screen only: see [Verbosity](#verbosity). Default `normal`. |
| `--i-understand-live-side-effects` | Consent for the prompt and the benchmark. `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true` also gives it. |
| `--readiness-only` | The readiness check only. |
| `--prompt <text>` | The prompt of step 3. Keep it read-only. |
| `--dataset <file>` | Cases in the `samples/datasets/m365-smoke.json` format. A broken rule is a usage error. Fields the demo does not read, such as `expected`, are ignored and named in the banner and `summary.md`. Default: the 5 embedded cases. |
| `--no-benchmark` | Readiness and the prompt only. |
| `--no-judge` | No judge call; the criteria are NOT MEASURED. |
| `--profile <profile>` | `public` (answer only, the default) or the experimental `native118` (also asks for two undocumented diagnostic modes). |
| `--out <dir>` | The run folder. It must be new or empty. |
| `--capture-raw` | Also keep the raw protocol traffic in `raw/`, without tokens. It holds tenant content. |
| `--zip` | Also write `<run folder>.zip`. |
| `--max-turns <n>` | The most agent turns, default `12`. Cases past the cap are NOT MEASURED. |
| `--timeout-seconds <n>` | The most one readiness stage or turn may take, default `120`. A sign-in waits for its code instead. |

## Verbosity

`--verbosity` changes the screen only; `console.log` always holds the `diagnostic` view. `quiet` shows the sign-in
message, the judge line, a failed stage, errors and the final table. `normal`, the default, adds the banner and one line
per stage (`✓`, `✗`, `–`), turn and issue. `detailed` adds every trace event, each case's checks and a detail block per
issue. `diagnostic` adds each event's data and timing. A repeated issue shows once and is counted in a
`Repeated issues` list. A redirected screen prints `[PASS]`, `[FAIL]` and `[SKIP]` instead of the symbols.

## GitHub Copilot: open this folder in VS Code

This folder has its own Copilot setup in [`.github/`](.github/). Open **this folder** in VS Code, not the repository
root. Pick the **`m365-demo`** agent in Copilot Chat. It routes to three skills:
[m365-demo-setup](.github/skills/m365-demo-setup/SKILL.md) (from zero to the zip),
[m365-demo-customize](.github/skills/m365-demo-customize/SKILL.md) (another agent, prompt or case) and
[m365-demo-triage](.github/skills/m365-demo-triage/SKILL.md) (read a run folder). The prompts `/run-demo`,
`/triage-run` and `/add-case` are shortcuts. From this folder, add `--config ../../workiq.json` to use the config at the
repository root. The run folders then land in this folder's `m365-demo-runs/`.

## For developers

`samples/AgentEval.Microsoft365Demo.Tests` tests the demo offline, through the real provider, with an in-test HTTP
handler and a fake sign-in (`DemoHostOverrides`). Run them from the repository root with every provider variable
unset, so nothing reaches a paid model:

```bash
for v in $(env | grep -iE '^(AZURE|AZUREOPENAI|OPENAI|FOUNDRY|MS_FOUNDRY|BITDEER|AI_INFERENCE|OPENROUTER|TYPESAFE|JEV_|COPILOTSTUDIO|ANTHROPIC|GEMINI|MISTRAL|OLLAMA|M365_|AGENTEVAL_M365|AGENTEVAL_A2A|APPLICATIONINSIGHTS|MAFARTCOMP)' | cut -d= -f1); do unset "$v"; done
dotnet test samples/AgentEval.Microsoft365Demo.Tests
```

`DemoRun.cs` orders the steps (`ReadinessStep.cs`, `PromptStep.cs`, `LiveSteps.cs`). The cases and their checks are in
`Benchmark.cs`, the AEF run in `BenchmarkAef.cs` and the exit decision in `DemoExitCodes.cs`.

## See also

- [Microsoft 365 tester guide](../../docs/microsoft365-tester-guide.md): the prerequisites, the CLI path and the
  [results template](../../docs/microsoft365-tester-RESULTS-TEMPLATE.md).
- [Microsoft 365 (`--sut microsoft365`)](../../docs/microsoft365.md): the provider, the config, the evidence model and
  the [troubleshooting catalogue](../../docs/microsoft365.md#troubleshooting).
