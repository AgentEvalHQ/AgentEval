# Microsoft 365 demo — check, prompt and benchmark a Microsoft 365 agent in one run

This console app connects to a Microsoft 365 agent over **Work IQ A2A 1.0**, checks the connection stage by stage,
sends one read-only prompt, runs a small AgentEval benchmark, and writes everything it saw into **one run folder** that
you can zip and send back. It is built for the person who validates the connector against a real tenant: one command
per step, every trace event and issue on screen at the verbosity you choose, and the full record on disk whatever the
screen shows. It uses the real provider (`src/AgentEval.Microsoft365`) through its public API. It never falls back to a
mock: without a config file it stops and says what is missing, and without your consent it stops after the readiness
check.

> **Status: Experimental.** The maintainers have no Microsoft 365 tenant and have not run this against one. The
> offline tests (`samples/AgentEval.Microsoft365Demo.Tests`) drive it through the real provider with an in-test HTTP
> handler and a fake sign-in, which is not a live result: your run is the first live evidence. Use a
> **non-production** tenant and test user only.

## Which agent it talks to

With no agent id the demo talks to Work IQ's **default agent**: Microsoft 365 Copilot's own built-in agent, whose card
is `https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json`. It is **not** a declarative agent.

A **declarative agent** is a custom agent an organisation builds on Microsoft 365 Copilot (a manifest with
instructions, knowledge and actions). Each one has its own opaque agent id and its own card
(`https://workiq.svc.cloud.microsoft/a2a/{agent-id}/.well-known/agent-card.json`). To target one, pass
`--agent-id <id>` (it overrides `agentId` in the config file). Find the id with the experimental `workiq list-agents`
command of Microsoft's WorkIQ CLI, or in the agent's Copilot Chat URL; ids look like `U_abc.def` — pass the whole
string exactly as shown. Word, Excel and PowerPoint agents are bound to their application and do not answer usefully
headlessly.

Start with the default agent. The readiness report prints the card's name and version, so you see exactly which agent
answered.

## Before you start

You need, in a **non-production** tenant (the [setup skill](.github/skills/m365-demo-setup/SKILL.md) and the
[tester guide](../../docs/microsoft365-tester-guide.md#2-what-you-need) walk through each one):

- **Work IQ enabled** in the tenant, and a **Microsoft 365 Copilot licence** on the test user (without either the
  service answers HTTP 403);
- an **Entra app registration** configured as a **public client** (*Authentication* → *Allow public client flows* =
  **Yes**; no secret is ever used), with the **delegated permission `WorkIQAgent.Ask`** and **admin consent**;
- the tenant id and the app's client id;
- the **.NET 10 SDK** and git;
- optionally, a judge model for the benchmark's criteria (see [The judge](#the-judge-optional)).

The permission covers reads **and writes** of the signed-in user's Microsoft 365 data, and a prompt can make the agent
propose a real action. The demo never approves an action and never resends a prompt, but it cannot undo what the agent
does with a prompt it accepted: that is why every step after the readiness check needs your explicit consent.

## Quick start

From the repository root (the commands the demo's own help and messages use):

```bash
git clone --branch work/dev https://github.com/AgentEvalHQ/AgentEval.git
cd AgentEval
cp samples/datasets/m365-config.template.json workiq.json        # then put your tenantId and appClientId in it
dotnet run --project samples/AgentEval.Microsoft365Demo -- --readiness-only
dotnet run --project samples/AgentEval.Microsoft365Demo -- --i-understand-live-side-effects --zip
```

1. The first `dotnet run` builds the project. `--readiness-only` checks the config, signs you in, fetches the agent
   card and checks the protocol: **no prompt is sent**. During the sign-in the console prints a message such as
   *"To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code …"*: open the
   page on any device, enter the code and sign in **as the test user**. The token is cached (OS-encrypted, outside the
   repository), so later runs sign in silently.
2. The second run repeats readiness, sends one prompt and runs the benchmark, then zips the run folder and prints the
   zip's path: that zip is what you send back. Read [What to send back](#what-to-send-back) first.

The demo reads `./workiq.json` and writes `./m365-demo-runs/` in the folder you run it from. Run from this folder
instead and the commands are `dotnet run -- --readiness-only` and so on, with `workiq.json` here
(`cp workiq.example.json workiq.json`: the example is a copy of the template); `--config <file>` or `M365_CONFIG_PATH`
point anywhere else. Without a config file the demo prints what is missing and how to create one, contacts nothing,
and exits `11`.

### The config file

`workiq.json` holds **no secret**. Fill in the two required values from the app registration's *Overview* page and
leave the rest as the template has it:

| Field | Value |
|---|---|
| `tenantId` | **Required.** The *Directory (tenant) ID*. |
| `appClientId` | **Required.** The *Application (client) ID* of your public-client app registration. |
| `agentId` | `null` for the default agent, or a declarative agent's id (or pass `--agent-id`). |
| `agentName` | Any label; it appears in the reports. |
| `timeZone` | Your IANA time zone (`Europe/Zurich`, `America/New_York`); sent with every prompt so date questions are deterministic. |
| `diagnosticProfile` | Leave `PublicAnswerOnly` (`--profile` overrides it). |
| `endpoint`, `scope`, `trustedHosts` | Leave the defaults. A token is only ever sent to a host in `trustedHosts`. |

A value still reading `REPLACE-WITH-…` is refused before anything is sent, with a message naming the field. **Never
commit `workiq.json`** and never send it back: it names your tenant.

## What a run does

| Step | What happens | Needs |
|---|---|---|
| 1. Banner | The run id and folder, the config file, which agent (the default agent or the id, and where the id came from), the tenant and app, the trusted hosts, the profile, the plan (which steps will run, or why not), the judge, the turn cap and timeout. | — |
| 2. Readiness | The provider's readiness check: `config` → `auth` (silent from the cache, else device-code sign-in; the sign-in waits until the code expires, about 15 minutes, not `--timeout-seconds`) → `discovery` (the agent card) → `protocol` (`GetTask` on a random task id must come back as JSON-RPC `-32001` TaskNotFound). **No prompt reaches the agent.** One sign-in serves the whole run: the prompt and the benchmark use the same token source, and a sign-in they need (a token that can no longer be refreshed) prints the same message and also waits for the code, not the turn's timeout. | a config file |
| 3. One prompt | `--prompt` (default *"In one sentence, what can you help me with?"*) as one turn in a fresh conversation. Passes only if the turn completes; a turn that does not complete fails this step (exit `1`). | consent |
| 4. Benchmark | The dataset (default: 5 read-only cases taken from `samples/datasets/m365-smoke.json`: identity, capabilities, a grounded summary of a "Demo Q3 Planning" document, date arithmetic, out-of-scope live data) through AgentEval's `MAFEvaluationHarness`, one fresh conversation per case. Deterministic checks always; judge criteria only when a judge is configured. | consent; a judge for the criteria |
| 5. Summary | The final table on screen and every file of the run folder. | — |

Without `--i-understand-live-side-effects` (or `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`) the run stops after step 2
and exits `11`: nothing was measured past readiness, and nothing was sent to the agent. `--readiness-only` stops after
step 2 on purpose and exits `0` when readiness passed. `--no-benchmark` skips step 4. A failed readiness stage skips
steps 3 and 4, and a failed prompt skips step 4 (the benchmark would only send its turns into the same failure).

**The prompt step and the benchmark read a non-completed turn differently, on purpose.** The prompt step is the
connectivity check of the live path: if one plain read-only prompt does not complete (`InputRequired`, `Failed`,
`Indeterminate`, …), the agent cannot be evaluated at all, so the step **fails** (exit `1`) and the benchmark is not run.
In the benchmark the same outcome is an `ERROR` row, **not measured** (exit `11` when nothing failed): one case's turn
that did not complete says nothing about the answers the agent gives, it is never graded and never counted as a
`FAIL` score. Both name the outcome, the failure codes and the `request-id`.

Every case gets one of four verdicts, and only one of them is a grade:

| Verdict | Meaning |
|---|---|
| `PASS` | Every check that applies ran and passed. |
| `FAIL` | A check ran and failed — an empty answer, no answer at all, or the judge's score below the case's passing score. A result about the agent, not an error. |
| `ERROR` | The turn did not complete (`InputRequired`, `AuthRequired`, `Failed`, `Rejected`, `Canceled` or `Indeterminate`), or the agent failed before a turn result. The row names the outcome and the failure (codes, `request-id`). It is never graded: no partial text is scored. |
| `NOT MEASURED` | Nothing failed, but a check that applies could not be measured: no judge for the criteria, the judge gave no verdict, the judge call failed, no answer was selected, or the turn cap stopped the case before it ran. Never counted as a pass. |

The checks of every case (`results.md` / `results.json` list each one as `PASS`, `FAIL`, `NOT MEASURED` or `NOT RUN`
with what was observed): **turn completed**; **answer non-empty**; **response time recorded** (client side: network
and service, not model latency); **evidence availability recorded** (every channel's `NotRequested` / `NotProvided` /
`Provided`); and **criteria (judge)** when the case declares criteria. A turn that completed with several answer
candidates and none selected (`answerCompleteness` `Ambiguous` in `turns.jsonl`) makes *answer non-empty* and the
criteria `NOT MEASURED` — its empty text is not the agent's answer, and `evidence/<case>.json` keeps every candidate;
a completed turn with no answer artifact at all fails *answer non-empty*.

## Options

Every option `dotnet run --project samples/AgentEval.Microsoft365Demo -- --help` lists:

| Option | Default | What it does |
|---|---|---|
| `--config <file>` | `./workiq.json` (or the path in `M365_CONFIG_PATH`) | The connection file. It holds no secret. |
| `--agent-id <id>` | the config's `agentId`; when that is `null` too, the default agent | The agent to test, as its opaque id (overrides the config's `agentId`). |
| `-v`, `--verbosity <level>` | `normal` | `quiet`, `normal`, `detailed` or `diagnostic` — the screen only; see [Verbosity](#verbosity). |
| `--i-understand-live-side-effects` | off (or `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`) | Consent for everything after readiness: the prompt and the benchmark drive the live agent as the signed-in user. Non-production identities only. Without it the demo runs readiness only and exits `11`. |
| `--readiness-only` | off | Run the readiness check only; no prompt is sent and no consent is needed. Exit `0` when every stage passed. |
| `--prompt <text>` | *In one sentence, what can you help me with?* | The one prompt of step 3. Keep it read-only. |
| `--dataset <file>` | the 5 embedded cases | The benchmark cases: a JSON array of objects with `id` and `input`, and optional `evaluation_criteria` (strings), `passing_score` (0–100, default 70), `category` and `tags` — the format of `samples/datasets/m365-smoke.json`. Ids use letters, digits, `.`, `_` or `-`. |
| `--no-benchmark` | off | Skip the benchmark: readiness and the one prompt only. |
| `--no-judge` | off | Do not grade the criteria even when a judge model is configured (no judge calls, no judge cost). The criteria are then `NOT MEASURED`. |
| `--profile <profile>` | `public` | `public` (answer only) or `native118` (EXPERIMENTAL: also requests the two diagnostic output modes observed in Microsoft's native evaluator 1.18.0; not a documented contract). |
| `--out <dir>` | `./m365-demo-runs/<yyyyMMdd-HHmmss>Z` (UTC) | The run folder. It must not exist yet, or be empty. |
| `--capture-raw` | off | Also keep the raw A2A requests, responses and stream frames under `raw/` (authorization headers and tokens are removed). They contain tenant content: read them before you send them. |
| `--zip` | off | Also write `<run folder>.zip` next to the run folder, ready to send back. |
| `--max-turns <n>` | `12` | The most agent turns this run may send: the prompt plus every case (at least 1). The run stops before a turn that would cross it; the cases it did not run are `NOT MEASURED`. Turns are counted; cost is not metered. |
| `--timeout-seconds <n>` | `120` | The most one readiness stage or one agent turn may take (at least 1). A device-code sign-in is not cut short by it: it waits for the sign-in code's own expiry (about 15 minutes). |

## Verbosity

`--verbosity` changes the screen only. The run folder always gets the full record.

| Level | On screen |
|---|---|
| `quiet` | The final table and errors (and the sign-in message, which you must see). |
| `normal` (default) | One line per stage (`✓` passed, `✗` failed, `–` skipped), one line per turn, one line per issue (`! issue <category> <code> (<stage>) HTTP … · JSON-RPC … · AADSTS… request-id=… — message`), then the final table. |
| `detailed` | Also every trace event in the provider's one-line format (`[m365 HH:mm:ss.fff Stage/Level code] message …` with the known ids, HTTP status, JSON-RPC code and A2A state), and a DETAIL block per issue (`! ISSUE <category> <code>`): stage, category and who can fix it, code, HTTP / JSON-RPC / AADSTS codes, `request-id`, task and context ids, case, A2A state, message, cause and action. A failed readiness stage's own issue (`doctor.<stage>.failed`) stays one line: the stage's `FAILED` block below it has the detail. |
| `diagnostic` | Also each event's data payload and elapsed time — the view `console.log` always holds. |

The screen shows each distinct issue (stage, code and message) once; when it occurs again (the same judge failure on
every case, for example) it is counted, and a `Repeated issues` list before the final table gives each count
(`×N`). `console.log` and `issues.jsonl` keep every occurrence.

Colours are used only when the console is interactive; `console.log` has none. When the output is redirected (a file,
a pipe, a CI log) or the console cannot show the symbols, the screen uses ASCII markers instead — `[PASS]`, `[FAIL]`,
`[SKIP]` for `✓ ✗ –`, and `-`, `->`, `...`, `|` for the other symbols — so nothing prints as `?`; `console.log` is
always UTF-8 with the symbols.

## The run folder

Default `./m365-demo-runs/<yyyyMMdd-HHmmss>Z/` (`--out` to choose). Once the command line is valid and a config file
is found, it is written on every run, whatever the verbosity and whatever failed (a usage error or a missing config
file creates none):

| File | What it holds |
|---|---|
| `summary.md` | **Read this first.** The header (tenant, app, agent, profile, versions), the stage table, the per-case table, the issues grouped by category with cause and action, and what to send back. |
| `console.log` | Everything printed, plus every trace event at diagnostic detail. No colours. |
| `readiness.md`, `readiness.json` | The readiness report: per stage PASS / FAIL / SKIPPED, what it means, what to do, and the evidence (schema `agenteval.m365.readiness/v1`). |
| `trace.jsonl` | Every trace event of the run, one JSON object per line (stage, level, code, message, ids, HTTP status, JSON-RPC code, A2A state, elapsed). |
| `issues.jsonl` | Every warning and error of the run — the readiness check writes it, the prompt and the benchmark append to it — with its category, code, suggested cause, AADSTS code, `request-id` and the config hash. |
| `turns.jsonl` | One line per turn (metadata only, never the answer text): case id (`prompt` for the prompt step), outcome, answer completeness, task and context ids, answer length, timings, the evidence availability, and for a turn that did not complete its failure category, codes and `request-id`. |
| `evidence/<caseId>.json` | The connector's evidence document of each turn (`agenteval.microsoft365.turn-evidence/v1`, under the run's content policy): the answer, citations and the availability of every evidence channel. |
| `results.md`, `results.json` | Per case: the verdict (`PASS` / `FAIL` / `ERROR` / `NOT MEASURED`), every check with what was observed, and the judge's score and explanation when it graded. |
| `raw/` | With `--capture-raw` only: the raw protocol traffic. |
| `<run folder>.zip` | With `--zip` only: the whole folder, next to it. |

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Every stage that ran passed and everything requested was measured (`--readiness-only`: readiness passed). |
| `1` | A stage **failed** (a readiness stage, or the one prompt did not complete), or a case **failed** a check. |
| `2` | A command-line mistake (an unknown option or value, a missing `--dataset` file, a non-empty `--out` folder, `--max-turns` or `--timeout-seconds` below 1). Nothing is created or sent. |
| `3` | A runtime error, or a file could not be written. The run folder is still written. |
| `11` | Nothing failed, but something was **not measured**: no consent for the steps after readiness, no judge (or `--no-judge`) for the criteria, a benchmark turn that did not complete (an `ERROR` row), a case stopped by `--max-turns`, or no config file (the demo then prints what is missing and how to create it, and contacts nothing). |

## The judge (optional)

The criteria of a case (`evaluation_criteria` in the dataset) are graded by a judge model only when one is configured.
The judge is read from the same environment variables as every AgentEval sample, resolved by
`InferenceProviderEnvironment` (AgentEval.Core):

| `AI_INFERENCE_PROVIDER` | Set |
|---|---|
| `azure` | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT` |
| `openai` | `OPENAI_API_KEY` (`OPENAI_BASE_URL`, `OPENAI_MODEL` optional) |
| `foundry` | `FOUNDRY_ENDPOINT`, `FOUNDRY_API_KEY`, `FOUNDRY_MODEL` |
| `bitdeer` | `BITDEER_API_KEY` (`BITDEER_ENDPOINT`, `BITDEER_MODEL` optional) |
| `openai-compatible` | `OPENAI_COMPATIBLE_ENDPOINT`, `OPENAI_COMPATIBLE_MODEL` (`OPENAI_COMPATIBLE_API_KEY` optional) |

With the selector unset, the first provider whose variables are set is used. Without any, or with `--no-judge`, every
criterion is `NOT MEASURED` (never passed) and the run exits `11` when nothing failed; the deterministic checks still
run. Each judge call costs money on your provider: use `--no-judge` to make none even when a provider is configured.

A judge call that fails (a mistyped key: HTTP 401; a wrong deployment: 403/404; a network error or an outage) is the
**judge's** problem, not the agent's: the case keeps its measured checks (the turn completed, the answer, the timing,
the evidence), its criteria are `NOT MEASURED` with `the judge call failed: <type>: <message>`, and one issue
`judge.call_failed` names the judge (category `Authentication` for 401/403, else `Service`) with what to check — the
judge provider's variables. Nothing is resent to the agent.

## What to send back

1. The zip the run printed (`--zip`), or the whole run folder.
2. The exit code and anything that surprised you, in a few lines (or the
   [results template](../../docs/microsoft365-tester-RESULTS-TEMPLATE.md)).

What the run folder contains, and what it never contains:

| May contain | Never contains |
|---|---|
| Your tenant id, app id and agent id; a masked account name; the card's URLs; HTTP statuses, AADSTS and JSON-RPC codes; the service's `request-id`s; the config's SHA-256; file paths on your machine (they include your user name). | A token, an auth code, the `Authorization` header or a client secret. `console.log` and the trace keep the device-code sign-in message with the code masked (`[redacted]`); only the screen shows it in full. |
| The prompts and the agent's **answers**, citation names and URLs (`evidence/`, and the prompt step's answer in `console.log`); the judge's explanations (`results.*`); everything the service sent under `raw/` with `--capture-raw`. `trace.jsonl`, `issues.jsonl` and `turns.jsonl` hold ids, codes and timings, never a prompt or an answer. | `workiq.json` itself, the token cache. |

If something cannot be shared, redact the **unzipped** folder and zip it yourself (do not send a zip made before the
redaction). These replace e-mail addresses, URLs and your tenant id in every text file of one run folder; they edit in
place, so keep a copy:

```bash
# Bash (Linux, macOS, Git Bash on Windows); RUN is the run folder the demo printed
export TENANT="<your-tenant-guid>" RUN="m365-demo-runs/<run folder>"
find "$RUN" -type f \( -name '*.jsonl' -o -name '*.json' -o -name '*.md' -o -name '*.log' \) -print0 \
  | xargs -0 perl -pi -e 's/[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/[email]/g; s#https?://[^"\s)\x60]+#[url]#g; s/\Q$ENV{TENANT}\E/[tenant]/gi'
```

```powershell
# PowerShell
$tenant = "<your-tenant-guid>"; $run = "m365-demo-runs\<run folder>"
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($f in Get-ChildItem $run -Recurse -File -Include *.jsonl, *.json, *.md, *.log) {
  $t = [IO.File]::ReadAllText($f.FullName)
  $t = $t -replace '[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[email]'
  $t = $t -replace 'https?://[^"\s)\x60]+', '[url]'
  $t = $t -replace [regex]::Escape($tenant), '[tenant]'
  [IO.File]::WriteAllText($f.FullName, $t, $utf8)
}
```

Then read the answers in `evidence/` and `console.log` and replace by hand, with `[name]`, any person or document name
you cannot share; say so when you send it. `request-id` values stay: they let Microsoft support find a call.

## GitHub Copilot: open this folder in VS Code

This folder carries its own GitHub Copilot setup in [`.github/`](.github/). Open **this folder** (not the repository
root) in VS Code — *File* → *Open Folder* → `samples/AgentEval.Microsoft365Demo` — and Copilot Chat loads it:

| What | Use it for |
|---|---|
| The **`m365-demo`** agent ([agents/m365-demo.agent.md](.github/agents/m365-demo.agent.md)) | Pick it in the Chat view's agent list. It knows the rules (non-production identity, consent, never resend a prompt, never approve an action, no tenant data in git) and routes to the three skills. |
| [m365-demo-setup](.github/skills/m365-demo-setup/SKILL.md) | From zero to the first run: tenant prerequisites, the app registration, the config file, `--readiness-only`, reading `readiness.md`, the full run, `--zip`. |
| [m365-demo-customize](.github/skills/m365-demo-customize/SKILL.md) | Target another agent, change the prompt or the cases, add a deterministic check, change the verbosity or the profile. |
| [m365-demo-triage](.github/skills/m365-demo-triage/SKILL.md) | Read a run folder and say what failed, why, and what to do next. |
| Prompts `/run-demo`, `/triage-run`, `/add-case` ([prompts/](.github/prompts/)) | One-click versions of the commonest requests. |
| [copilot-instructions.md](.github/copilot-instructions.md) and [instructions/m365-demo.instructions.md](.github/instructions/m365-demo.instructions.md) | The project's rules, loaded automatically (the second one when you edit a `.cs` file). |

Opened from the repository root instead, Copilot uses the repository's own setup; the `AgentEval M365` agent there
points back here.

## Tests

`samples/AgentEval.Microsoft365Demo.Tests` tests the demo offline: through the real provider, with an in-test HTTP
handler and a fake sign-in (the `DemoHostOverrides` seams), never a tenant and never a paid judge. Run them from the
repository root with every provider variable unset, so nothing can reach a paid model:

```bash
for v in $(env | grep -iE '^(AZURE|AZUREOPENAI|OPENAI|FOUNDRY|MS_FOUNDRY|BITDEER|AI_INFERENCE|OPENROUTER|TYPESAFE|JEV_|COPILOTSTUDIO|ANTHROPIC|GEMINI|MISTRAL|OLLAMA|M365_|AGENTEVAL_M365|AGENTEVAL_A2A|APPLICATIONINSIGHTS|MAFARTCOMP)' | cut -d= -f1); do unset "$v"; done
dotnet test samples/AgentEval.Microsoft365Demo.Tests
```

## See also

- [Microsoft 365 tester guide](../../docs/microsoft365-tester-guide.md) — the same validation with the `agenteval`
  CLI (`m365 doctor`, `eval`, `bench`), and its [results template](../../docs/microsoft365-tester-RESULTS-TEMPLATE.md).
- [Microsoft 365 (`--sut microsoft365`)](../../docs/microsoft365.md) — the provider: config, profiles, the evidence
  model, the trace and issue files, troubleshooting.
- `samples/datasets/m365-smoke.json` (the 8-case dataset the default cases come from) and
  `samples/datasets/m365-config.template.json` (the config template).
