# Microsoft 365 connector: tester guide

This guide is for the external tester who runs the Microsoft 365 connector against a tenant for the first time. It
assumes no knowledge of this repository. The maintainers have no tenant, so your run is the first live evidence. Allow
about an hour, most of it for the prerequisites.

## Part 1: run the demo

The demo project runs the whole validation and writes one run folder to send back. Its README is your guide:
[`samples/AgentEval.Microsoft365Demo`](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Microsoft365Demo/README.md).

1. Set up everything in [What you need](#what-you-need).
2. Follow the demo README's [Quick start](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Microsoft365Demo/README.md#quick-start):
   `--readiness-only` first, then the full run with `--zip`. If the maintainers gave you a branch or a tag, check it
   out right after the clone.
3. Send back the zip and the exit code, as its
   [What to send back](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Microsoft365Demo/README.md#what-to-send-back) says.

Stop there unless the maintainers ask for the command-line results in
[Part 2](#part-2-the-cli-path-only-if-the-maintainers-ask).

## What you need

Both parts need the same tenant setup. [Connect to a Microsoft 365 tenant](microsoft365-tenant-setup.md) gives
the steps in order, who does each one, and the first connection test.

| Item | Why | Where to get it |
|---|---|---|
| A **non-production** tenant and a **test user** in it | The agent acts as this user on real mail, files and calendar, and a prompt can make it propose an action. Nothing is ever approved for you. | Your organisation's test tenant. Never a production identity. |
| **Work IQ enabled** in the tenant | Without it the sign-in fails with `AADSTS650052`. | A Global Administrator, once: [setup](microsoft365-tenant-setup.md#1-enable-work-iq-in-the-tenant-once) step 1. |
| The test user in a **usage-based billing plan** | Work IQ calls are billed as Copilot Credits. Without the plan the service answers HTTP 403. | A billing administrator: [setup](microsoft365-tenant-setup.md#2-set-up-billing-for-the-test-user) step 2. Allow 30 minutes. |
| An **Entra app registration** that is a **public client** | The connector signs in with a device code and holds no secret. Without this setting the sign-in fails with `AADSTS7000218`. | Entra admin center → *App registrations* → *New registration* (single tenant is fine). Then *Authentication* → *Advanced settings* → **Allow public client flows = Yes** → *Save*. |
| The delegated permission **`WorkIQAgent.Ask`** with **admin consent** | The scope the service accepts. Without consent the sign-in fails with `AADSTS65001`. It covers reads **and writes** of the user's data. | The app's *API permissions* → *Add a permission* → *APIs my organization uses* → the Work IQ API (`api://workiq.svc.cloud.microsoft`) → *Delegated* → `WorkIQAgent.Ask` → *Add*. Then **Grant admin consent**. |
| The **tenant id** and the app's **client id** | For `workiq.json`. | The app registration's *Overview* page: *Directory (tenant) ID* and *Application (client) ID*. |
| A document titled **"Demo Q3 Planning"** in the test user's OneDrive or SharePoint | One case asks the agent to summarise and cite it. | Create any short document with that title. Without it the case can still PASS on a plain "not found", and then grounding and citations are not measured. Create it for the case to mean anything. |
| **Judge (optional)** | A judge model grades the answers. Each call costs money. | **Demo:** `AI_INFERENCE_PROVIDER` and its variables ([table](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Microsoft365Demo/README.md#the-judge-optional)), or `--no-judge`. An exported `OPENAI_API_KEY` is picked up and spends unless you pass `--no-judge`; the demo prints the judge it resolved before any call. **CLI `eval`** (Step 3): `--judge <url> --judge-model <model>`, with the key in `OPENAI_API_KEY`. **CLI `bench`** (Step 4): `AZURE_OPENAI_JUDGE_ENDPOINT`, `AZURE_OPENAI_JUDGE_API_KEY`, `AZURE_OPENAI_JUDGE_DEPLOYMENT`. |
| The **.NET 10 SDK**, **git** and a browser on any device | To build and run the code, and to complete the sign-in. | <https://dotnet.microsoft.com/download/dotnet/10.0>, <https://git-scm.com> |
| Optional: a declarative agent's **agent id** | To test an agent other than Microsoft 365 Copilot itself. | The experimental `workiq list-agents` command of Microsoft's WorkIQ CLI, or the agent's Copilot Chat URL. Ids look like `U_abc.def`. |

## Part 2: the CLI path (only if the maintainers ask)

Part 2 repeats the validation with the `agenteval` command-line tool: `m365 doctor`, `eval` and `bench`. It uses the
clone and the `workiq.json` from Part 1. Run every command from the repository root. You never need to read code.

### Install

```bash
dotnet build src/AgentEval.Cli
```

The build must end with `Build succeeded`. If it fails, send the last 50 lines back and stop.

This guide writes every command as `agenteval …`. Define it once per terminal session:

```bash
# Bash (Linux, macOS, Git Bash on Windows)
agenteval() { dotnet run --project src/AgentEval.Cli --framework net10.0 --no-build -- "$@"; }
```

```powershell
# PowerShell
function agenteval { dotnet run --project src/AgentEval.Cli --framework net10.0 --no-build '--' @args }
```

`--no-build` reuses the build above; after a `git pull`, build again. Then check the install, and create the workspace
and a results folder:

```bash
agenteval --version
agenteval m365 doctor --help
agenteval init-workspace
mkdir m365-results
```

`m365 doctor --help` must list `--m365-config`, `--probe`, `--i-understand-live-side-effects` and `--timeout-seconds`.
If it does not, send back the output of `git log -1 --oneline`. `init-workspace` creates `.agenteval/`, so every
doctor report, trace and issue file lands in `.agenteval/m365/`. Git ignores `.agenteval/`, `workiq.json`,
`m365-results/` and `m365-raw/`. Still, never commit them.

### The sequence

| Step | Command | Prompts sent | Go on when |
|---|---|---|---|
| 1 | `agenteval m365 doctor --m365-config ./workiq.json` | none | exit `0` |
| 1b | the same, again | none | exit `0`, no sign-in |
| 2 | the same, with `--probe --i-understand-live-side-effects` | 1 or 2 | exit `0` |
| 3 | `agenteval eval --sut microsoft365 …` on `samples/datasets/m365-smoke.json` | 8, plus judge calls | any exit; record it |
| 4 (optional) | `agenteval bench owasp --preset smoke --sut microsoft365 …` | 23, plus judge calls | any exit; record it |

When a step fails, read its table below. The complete list of messages and fixes is the
[troubleshooting catalogue](microsoft365.md#troubleshooting). If you cannot fix it, stop and
[send the files back](#send-back-the-cli-files).

### Step 1: readiness check (no prompt)

```bash
agenteval m365 doctor --m365-config ./workiq.json
```

The doctor runs four stages and stops at the first failure: `config` (reads `workiq.json`), `auth` (gets a token),
`discovery` (fetches the agent card) and `protocol` (one A2A 1.0 call that must answer "task not found"). Nothing is
sent to the agent. Lines that start with `[m365 ` are trace events; the summary at the end is what matters.

On the first run, `auth` asks you to open `https://microsoft.com/devicelogin` and enter a code. Do it on any device and
sign in **as the test user**. The stage waits until the code expires, about 15 minutes. The summary then looks like
this:

```text
Microsoft 365 readiness (m365 doctor) — run m365-20261105-184133-5f30
  [PASS]    config     config valid: My Non-Prod Microsoft 365 Agent; default agent; tenant <your tenant id>; profile PublicAnswerOnly
  [PASS]    auth       token acquired by device-code sign-in; tenant <your tenant id>; expires 2026-11-05 19:41 UTC
  [PASS]    discovery  card '<card name>' fetched; 2 interface(s); chose JSONRPC 1.0 at workiq.svc.cloud.microsoft
  [PASS]    protocol   the service answered GetTask on a random task id with JSON-RPC TaskNotFound (-32001)
  [SKIPPED] probe      not requested: …
  [SKIPPED] census     not requested: …

Result: PASS — every requested stage passed.
```

`probe` and `census` are skipped because Step 1 does not ask for them. A failed stage prints three more lines:
`issue:` (category, code and any AADSTS, HTTP or JSON-RPC code), `meaning:` and `do:`. `readiness.md` in the report
folder has the same table plus each stage's evidence.

| Exit | Meaning | Next |
|---|---|---|
| `0` | Every requested stage passed. | Step 1b. |
| `1` | A stage failed. | The table below. |
| `2` | A command-line mistake; the message says which. | Fix the command. |
| `3` | The report could not be written, or the check could not start. | Check disk space and permissions on `.agenteval/`. |

**If it fails**, read the stage's `meaning:` and `do:` lines first. The five most common causes:

| What the summary shows | What it means | What to do |
|---|---|---|
| `config` … `still holds the template placeholder 'REPLACE-WITH-…'` | A template value was never replaced. Nothing was sent. | Put the value the message names, from the app's *Overview* page. |
| `auth` … `AADSTS65001` | Admin consent for `WorkIQAgent.Ask` is missing. | An administrator grants it on the app's *API permissions* page. |
| `auth` … `AADSTS7000218` | The app is not a public client. | Set *Allow public client flows* to **Yes**. |
| `auth` … `sign-in not completed: the device code expired …` | Nobody finished the sign-in in time. Nothing was sent. | Run again and sign in while the code is valid (about 15 minutes). |
| `discovery` or `protocol` … `HTTP 403` | Scopes named: admin consent is missing. No scope named: the user is not in the billing plan, or Work IQ is off. | [Setup](microsoft365-tenant-setup.md#if-a-step-fails) steps 5, 2 or 1. |

`--continue` runs the remaining stages after a failure. `--stage auth` (or `discovery`, `protocol`) runs one stage
plus the `config` stage it needs.

### Step 1b: the token cache

Run Step 1 again. It must exit `0` with no sign-in message, and `auth` must say `token acquired silently from the
cache`. If the sign-in message appears again, the cache could not be written or read: sign in and note it. The cache
is an OS-encrypted file under your user profile. Never send it.

### Step 2: one real prompt

This step sends a prompt to the agent, so it needs the consent flag: the agent acts as the signed-in user.

```bash
agenteval m365 doctor --m365-config ./workiq.json --probe --i-understand-live-side-effects
```

After the Step 1 stages, `probe` sends `Reply with the single word READY.` in a fresh conversation and passes when the
turn completes. `census` then records what your tenant supports: it sends the same prompt once more under the
experimental `native118` profile, and calls `CancelTask` on the finished task. It passes whether the tenant accepts or
refuses the profile. You should see, with your own values:

```text
  [PASS]    probe      the turn completed in 5321 ms: 5-character answer, attributions Provided (2 attribution(s))
  [PASS]    census     native118 accepted; Developer Info …; Retrieval Info …; citations …; CancelTask …
```

`readiness.md` shows the prompt, the start of the answer, the task ids, the states observed and the timings. A warning
that the answer is not the word READY is informational.

| Exit | Meaning | Next |
|---|---|---|
| `0` | Every requested stage passed. | Step 3. |
| `1` | A stage failed. | The table below. |
| `11` | `Result: INCOMPLETE`: the consent flag is missing, so `probe` and `census` were skipped and nothing was sent. | Add `--i-understand-live-side-effects`. |
| `2`, `3` | As in Step 1. | As in Step 1. |

**If it fails**, the five most common causes:

| What the summary shows | What it means | What to do |
|---|---|---|
| `probe` … `the agent paused (InputRequired)` | The agent asked for a confirmation or input. Nothing was approved. | Unexpected for this prompt: send the folder back. |
| `probe` … `the agent paused (AuthRequired)` | The agent needs a connector sign-in that this tool cannot complete. | Sign in to that connector in Microsoft 365 Copilot Chat, then run again. |
| `probe` … `Indeterminate: the send may have been accepted; do not resend` | No final task state arrived within the stage limit (120 s). The prompt was not resent. | Run once more with `--timeout-seconds 300`. If it repeats, send both report folders back. |
| `probe` … `the turn ended Failed` (or `Rejected`, `Canceled`) | The service ended the task with an error. | Send the folder back: it holds the codes and the `request-id`. |
| `probe` … `HTTP 429` | The service throttled. | Wait a few minutes and run again. |

`census` PASS with `native118 refused` is a fact about your tenant, not a failure.

### Step 3: the smoke benchmark

Eight read-only prompts from `samples/datasets/m365-smoke.json`, graded by your judge. Put the judge's key in an
environment variable, so it is not on the `eval` command line. Type it at a prompt that does not echo it, so it stays
out of your shell history too:

```bash
read -rs OPENAI_API_KEY && export OPENAI_API_KEY      # paste the key, then press Enter
```

```powershell
$env:OPENAI_API_KEY = Read-Host -MaskInput 'judge key'   # PowerShell 7
```

A later demo run also picks this key up as its judge, unless you pass `--no-judge` there. Then run (in PowerShell,
replace each trailing `\` with a backtick):

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 10 \
  --judge <judge-base-url> --judge-model <judge-model> \
  --subject agent:m365-smoke --aef ./m365-results/aef --aef-content on \
  --format json -o ./m365-results/m365-smoke.json
```

- `--judge` is an OpenAI-compatible base URL: `https://api.openai.com/v1`, or
  `https://<resource>.openai.azure.com/openai/v1/` for Azure OpenAI. Always pass `--judge-model`: the model, or the
  Azure deployment name.
- `--m365-max-turns 10` stops the run before an 11th agent turn. It counts turns, not cost.
- `--aef-content on` keeps each case's prompt, answer and evidence in the sealed evidence folder. Leave it out if you
  cannot share answers, and say so in the results file.
- Cost: 8 agent turns and 8 to 16 judge calls (one per case, plus one retry when a verdict cannot be read).

stderr first shows a `Note: --sut microsoft365 …` line with the trace file, the issue file and the turn cap. The run
ends with the passed, failed and not-measured counts and the AEF run folder. `./m365-results/m365-smoke.json` holds,
per case, the score, the answer (`output`) and, for a case without a verdict, the reason (`error`). Then run
`agenteval aef verify <the AEF run folder it printed>`. It must say `intact` (exit `0`).

| Exit | Meaning | Next |
|---|---|---|
| `0` | All eight cases passed. | Step 4 (optional), then send the files back. |
| `1` | At least one case failed its criteria. A **result** about the agent, not an error. | Record which. Do not change the dataset. |
| `11` | Nothing failed, but at least one case has **no verdict**. Its `error` says why. | The table below. |
| `3` | The run stopped: no consent flag, an invalid config or judge URL, the turn cap, or the evidence folder could not be written. | Read the `Error:` line. |
| `2` | A command-line mistake. | Fix the command. |

**If it fails**, the five most common causes:

| Symptom | What it means | What to do |
|---|---|---|
| Exit `3`, `Error: --sut microsoft365 drives a LIVE Microsoft 365 agent …` | The consent flag is missing. Nothing was sent. | Add `--i-understand-live-side-effects`. |
| Exit `11`, an `error` with `Work IQ turn did not complete: outcome=InputRequired` | The agent asked to confirm an action on a read-only prompt. Nothing was approved. | A finding: note the case id. |
| Exit `11`, an `error` with `outcome=Indeterminate`, `Failed` or `Rejected` | The turn did not complete. The prompt was not resent. | Run Step 3 once more. If it repeats, send the files back. |
| Exit `11`, every turn completed but the cases are not measured | The judge did not answer: a wrong URL, key or model. | Fix the judge and run Step 3 again (8 more agent turns). |
| A line `[m365] WARNING case=… the turn COMPLETED but no answer was selected (answer Ambiguous)` | The task completed with several answer candidates and none marked as the answer. The case is NOT MEASURED: exit `11`, and its `error` starts `Not measured:`. | Note the case id and send the files back. The evidence folder keeps every candidate for the maintainers. |

The same warning with `answer None` means the agent gave no answer at all. That case is graded on the empty text and
normally fails. The grounding case can PASS on a plain "not found" when "Demo Q3 Planning" is missing; it then
measures nothing about grounding.

**Optional: the raw protocol traffic.** Only if the maintainers ask for it:

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 2 --no-aef --m365-verbose \
  --m365-capture-raw ./m365-raw --format json -o ./m365-raw/m365-raw.json
```

It stops after two turns. `--m365-capture-raw` writes the raw requests, responses and stream frames to `./m365-raw/`,
with tokens removed. **These files contain tenant content**: read them before you send them.

### Step 4 (optional): the OWASP smoke preset

Run it only if Steps 1 to 3 worked, your administrator knows, and you accept 23 more agent turns. It sends prompt
injection, jailbreak and personal-data extraction attempts as the test user, and the tenant's security tooling may
record them. The benchmark's judge comes from environment variables:

```bash
export AZURE_OPENAI_JUDGE_ENDPOINT="https://<your-resource>.openai.azure.com/"
export AZURE_OPENAI_JUDGE_DEPLOYMENT="<deployment>"
read -rs AZURE_OPENAI_JUDGE_API_KEY && export AZURE_OPENAI_JUDGE_API_KEY   # paste the key, then press Enter
# PowerShell: $env:AZURE_OPENAI_JUDGE_ENDPOINT = "https://…" and so on;
# the key with $env:AZURE_OPENAI_JUDGE_API_KEY = Read-Host -MaskInput 'judge key'
```

Without them, `bench` uses the provider `AI_INFERENCE_PROVIDER` selects
([CLI reference](cli.md#ai_inference_provider--which-provider-the-cli-talks-to)). Then:

```bash
agenteval bench owasp --preset smoke --subject m365-smoke \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --m365-max-turns 30 --aef ./m365-results/aef
```

It checks the judge with one short call, sends the 23 probes one turn each, grades every answer, and prints a gate
verdict (PASS, WARN or FAIL) with a per-attack table. The preset has a 10-minute limit. A run that hits it, or the turn
cap, never passes. Unlike `eval`, `bench` still grades a turn with no selected answer on its empty text; the warning
line says so.

| Exit | Meaning | Next |
|---|---|---|
| `0` | The gate passed. | Send the files back. |
| `9` | The gate failed: the agent gave in to at least one attack. A **result**, not an error. | Record the verdict and the per-attack table. |
| `10` | WARN. | Record it. |
| `11` | INCOMPLETE: a judge call failed mid-scan, or time ran out. Never a pass. | Record how far it got. |
| `3` | The judge is not configured or did not answer its first call. | Fix the judge variables. |
| `2` | A command-line mistake, an unknown preset, or no consent flag. | Fix the command. |

### Send back the CLI files

Zip these:

1. `.agenteval/m365/`: every `doctor-*` report folder, every `m365-trace-*.jsonl` and `m365-issues.jsonl`.
2. `./m365-results/`: `m365-smoke.json` and the sealed evidence folder `aef/`.
3. Your filled-in [results file](microsoft365-tester-RESULTS-TEMPLATE.md), `RESULTS.md`.

Never send `workiq.json`, the token cache, a key or screenshots of the sign-in page. Send `./m365-raw/` only if the
maintainers asked for it.

The trace and issue files hold ids, codes, timings and file paths, never a prompt, an answer or a token.
`readiness.md` holds the probe prompt and the start of its answer. `m365-smoke.json` and `aef/` hold the agent's
**answers**. The `error` of a failed case in `m365-smoke.json` holds the judge's explanation, which may quote the answer.

To redact, run the commands in the demo README's
[What to send back](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Microsoft365Demo/README.md#what-to-send-back) twice: with the folder set to
`.agenteval/m365`, then to `m365-results`. They skip the sealed `aef/` folder, because any edit makes it invalid. If the
answers in `aef/` cannot be shared, leave `aef/` out and say so. Then read the `output` and `error` fields of
`m365-smoke.json` and replace any name you cannot share with `[name]`.

## FAQ

**A proxy is required.** The tool uses .NET's default proxy: `HTTPS_PROXY` and `NO_PROXY` when set, else the system
settings. A `Transport` failure in `discovery` means the path to `workiq.svc.cloud.microsoft` is blocked. A sign-in that
never gets an answer points at `login.microsoftonline.com`.

**The sign-in message appears on every run.** The token cache could not be written or read. It lives under
`%LOCALAPPDATA%\AgentEval\msal_cache\` on Windows, in the keychain on macOS and in the secret service on Linux. Sign in
each time and note it.

**The agent answers nothing useful, or talks about an application window.** Word, Excel and PowerPoint agents are bound
to their application and do not work headlessly. Use the default agent or a declarative agent that works in Copilot
Chat.

**Citations look "unresolved".** The connector keeps the raw citation markers and attributions as sent. It links a
marker to a source only when the service gives an explicit key. If an answer visibly cites something but citation
sources are `NotProvided`, note it.

**A file "is being used by another process".** During a run the trace and issue files are open for writing. `tail -f`
and `Get-Content -Wait` can follow them; some editors cannot open them until the run ends.

## Where to ask

Send the zip to the maintainer who gave you this guide, or open an issue on the repository **without** the files.
Please do not post the files publicly before the maintainers have checked them for tenant data.
