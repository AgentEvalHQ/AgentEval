# Microsoft 365 connector — tester guide

This guide is for one person: the external tester who has a Microsoft 365 tenant and will run the connector
against it for the first time. It assumes no knowledge of this repository. Each step gives the exact command, what
you should see, what the exit code means, and what to do when it fails.

The maintainers have no tenant. Nothing in this guide was run live by them. Your run is the first evidence.

## 1. Purpose

The connector lets AgentEval evaluate a Microsoft 365 agent (Microsoft 365 Copilot, or a declarative agent) over
the Work IQ A2A protocol. You will check, in order, that it can:

1. read its config file,
2. sign you in,
3. find the agent's card,
4. talk A2A 1.0 to the service,
5. run one real prompt,
6. run an 8-prompt smoke benchmark graded by a judge model of your choice,
7. optionally run the OWASP benchmark's smoke preset.

Every step writes files that say exactly what happened. You send those files back. You never need to read code.

## 2. What you need

| Item | Why | Where to get it |
|---|---|---|
| A **non-production** Microsoft 365 tenant and a test user in it | The agent acts as the signed-in user on real data. The connector never approves an action, but a prompt can make the agent propose one. | Your organisation's test tenant. Do not use a production identity. |
| **Work IQ enabled** by an administrator | The service answers 403 otherwise. | Microsoft 365 admin center. |
| A **Microsoft 365 Copilot licence** on the test user | Same. | Admin center, licences. |
| An **Entra app registration** that is a **public client** | The connector signs in with device code; there is no secret. | Entra admin center, App registrations, your app, *Authentication*: set *Allow public client flows* to **Yes**. |
| The **delegated permission `WorkIQAgent.Ask`** on that app, with **admin consent** | The token scope the service accepts. | *API permissions*: add `WorkIQAgent.Ask` from the Work IQ API (`api://workiq.svc.cloud.microsoft`), then *Grant admin consent*. |
| The tenant id and the app's client id | For the config file. | The app registration's *Overview* page. |
| A small synthetic corpus in the test user's OneDrive or SharePoint, including a document named **"Demo Q3 Planning"** | One smoke prompt asks for it. | Create any short document with that title. |
| An OpenAI-compatible **judge endpoint and model** you are allowed to spend on | The smoke benchmark grades answers with a judge. | Your own provider: an Azure OpenAI deployment, OpenAI, or any OpenAI-compatible host. |
| The **.NET 10 SDK** | To build the CLI from source. | <https://dotnet.microsoft.com/download/dotnet/10.0> |
| **git** | To clone the branch. | <https://git-scm.com> |
| Optional: the **agent id** of a declarative agent | To target an agent other than the default Copilot. | Copy it from the agent's Copilot Chat URL; it looks like `U_abc.def`. Pass it exactly as shown. Word, Excel and PowerPoint agents do not answer usefully outside their app. |

## 3. Install

Clone the branch `work/dev` and build the CLI once:

```bash
git clone --branch work/dev https://github.com/AgentEvalHQ/AgentEval.git
cd AgentEval
dotnet build src/AgentEval.Cli
```

The build must end with `0 Error(s)`. If it does not, send the last 50 lines of the output back and stop.

There are two ways to run the CLI. Pick one and use it for every command below.

**Option A — run from the clone (simplest).** Every command in this guide starts with `agenteval`; replace that
word with:

```bash
dotnet run --project src/AgentEval.Cli --framework net10.0 --
```

The trailing `--` matters: it separates the CLI's own arguments from `dotnet run`'s. Example:

```bash
dotnet run --project src/AgentEval.Cli --framework net10.0 -- m365 doctor --m365-config ./workiq.json
```

Run every command from the `AgentEval` folder (the clone's root). The folder already contains an `.agenteval`
workspace, so the trace and issue files land in `.agenteval/m365/` next to it.

**Option B — pack and install the tool locally.** This gives you a plain `agenteval` command. The version must
differ from the one on NuGet, so a local version string is passed:

```bash
dotnet pack src/AgentEval.Cli -c Release -o ./nupkg -p:PackageVersion=1.0.0-m365.1
dotnet tool install --tool-path ./tools AgentEval.Cli --version 1.0.0-m365.1 --add-source ./nupkg
```

Then run `./tools/agenteval` (Windows: `.\tools\agenteval.exe`) where this guide says `agenteval`. To remove it later:
`dotnet tool uninstall --tool-path ./tools AgentEval.Cli`.

Check the install with:

```bash
agenteval --version
agenteval eval --help
```

`eval --help` must list `--sut`, `--m365-config`, `--m365-max-turns`, `--m365-profile`, `--m365-capture-raw`,
`--m365-verbose`, `--m365-trace` and `--i-understand-live-side-effects`. If any is missing, you have the wrong
branch; send the output of `git log -1 --oneline` back.

## 4. The config file

Copy the template and fill in the two required values:

```bash
cp samples/datasets/m365-config.template.json ./workiq.json
```

```json
{
  "endpoint": "https://workiq.svc.cloud.microsoft/a2a/",
  "agentId": null,
  "tenantId": "REPLACE-WITH-ENTRA-TENANT-GUID",
  "appClientId": "REPLACE-WITH-PUBLIC-CLIENT-APP-REGISTRATION-GUID",
  "scope": "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask",
  "agentName": "My Non-Prod Microsoft 365 Agent",
  "diagnosticProfile": "PublicAnswerOnly",
  "timeZone": "Europe/Zurich",
  "trustedHosts": ["workiq.svc.cloud.microsoft"]
}
```

- `tenantId` and `appClientId`: from the app registration's *Overview* page.
- `agentId`: leave `null` for the default Microsoft 365 Copilot. To target a declarative agent, paste its id as a
  string.
- `agentName`: any label you like; it appears in reports.
- `timeZone`: your IANA time zone, so date prompts are deterministic. Remove the line if unsure.
- Leave the other fields as they are. `diagnosticProfile` stays `PublicAnswerOnly` for the first run.

The file holds **no secret**. The sign-in happens at run time in your browser. Keep the file outside the
repository's tracked files anyway (the clone's root is fine; `workiq.json` is not committed by any command).

## 5. The sequence

Run the steps in order. Stop at the first failure, read "If it fails", and send the files from
[section 6](#6-what-to-send-back) if you cannot fix it.

### Step 1 — readiness check

> `agenteval m365 doctor` lands with the next checkpoint of the branch. If `agenteval m365 --help` reports an
> unknown command, pull the branch again (`git pull`), rebuild, and retry. If it is still missing, skip to Step 3.

```bash
agenteval m365 doctor --m365-config ./workiq.json
```

**What it does.** It runs four stages in order and stops at the first failure: `config` (reads the file),
`auth` (signs you in), `discovery` (fetches the agent's card), `protocol` (sends one harmless `GetTask` for a random
id, expecting the service's "task not found" error — that proves the endpoint speaks A2A 1.0 and accepts your token).
Nothing is sent to the agent itself.

**What you should see.** During `auth`, a message like *To sign in, use a web browser to open the page
https://microsoft.com/devicelogin and enter the code XXXXXXXXX to authenticate.* Open the page, enter the code, sign
in as the test user. Then one line per stage, each `PASS`, and a final line naming the report folder:
`.agenteval/m365/doctor-<timestamp>/`. The folder contains `readiness.md`, `readiness.json`, `m365-trace-<run id>.jsonl`
and `m365-issues.jsonl`.

**Exit code.** `0`: every stage passed. `1`: a stage failed; `readiness.md` names it and says why. `11`: a stage was
skipped (only happens with `--probe`, see Step 2). `2`: a command-line mistake; the message says which.

**If it fails.**

| Stage | What `readiness.md` says | What to do |
|---|---|---|
| `config` | A field name and what is wrong with it. | Fix the field in `workiq.json`. |
| `auth` | `AADSTS65001` | Admin consent is missing. An administrator grants admin consent for `WorkIQAgent.Ask`. |
| `auth` | `AADSTS7000218` | The app is not a public client. Set *Allow public client flows* to **Yes**. |
| `auth` | `AADSTS700016` or `AADSTS90002` | The app or the tenant was not found. Check `appClientId` and `tenantId`. |
| `auth` | `AADSTS50076` / `AADSTS50079` | MFA is required. Complete it in the browser. |
| `discovery` | HTTP 401 or 403 | The token is rejected, or the user lacks the licence or Work IQ is not enabled. Ask the administrator. |
| `discovery` | "no 1.0 interface" or "untrusted host" | The card does not look like we expect. Send the folder back; do not edit `trustedHosts` unless the host is clearly Microsoft's. |
| `protocol` | HTTP 401 or 403 | Permission or licence problem on the RPC endpoint even though the card was readable. Ask the administrator. |
| `protocol` | Anything other than "task not found (-32001)" | Send the folder back. |

### Step 2 — readiness check with one real prompt

This step sends one prompt to the agent: *Reply with the single word READY.* It is the first real turn. It needs
the consent flag because the agent now acts as the signed-in user.

```bash
agenteval m365 doctor --m365-config ./workiq.json --probe --i-understand-live-side-effects
```

**What you should see.** The four stages from Step 1 again (sign-in is now silent, from the cache), then `probe`
with the outcome `Completed`, the task and context ids, the answer length and the elapsed time, then `census` with
what the tenant does: whether the diagnostic modes are accepted, which citation format was seen, and what
`CancelTask` returns on the finished task. Exit `0`.

**Exit code.** `0`: all passed. `1`: a stage failed. `11`: the probe was skipped because the consent flag is
missing — add it. `2`: command-line mistake.

**If it fails.**

| What `readiness.md` says | What it means | What to do |
|---|---|---|
| `probe` outcome `InputRequired` | The agent asked for a confirmation. Nothing was approved. | Unexpected for this prompt; send the folder back. |
| `probe` outcome `Failed` or `Rejected` | The service ended the task with an error. | Send the folder back; the HTTP status, JSON-RPC code and `request-id` are in it. |
| `probe` outcome `Indeterminate` | The request timed out before the service said anything definite. | Retry once. If it repeats, send both folders back. |
| `probe` with HTTP 429 | The service throttled. | Wait a few minutes and retry. |
| `probe` answer length 0 | The task completed with an empty answer. | Send the folder back; this is a finding, not your mistake. |

### Step 3 — the smoke benchmark

Eight read-only prompts, graded by your judge. Replace the three judge values with your own.

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 10 \
  --judge https://<your-judge-endpoint>/v1 --judge-model <your-judge-model> --api-key <your-judge-key> \
  --subject agent:m365-default-copilot --aef ./m365-results/aef --format markdown -o ./m365-results/m365-smoke.md
```

- `--judge` is an OpenAI-compatible base URL (for Azure OpenAI, the deployment's OpenAI-compatible endpoint);
  `--judge-model` the model or deployment name; `--api-key` its key (or set the environment variable `OPENAI_API_KEY`
  and omit `--api-key`).
- `--m365-max-turns 10` stops the run after ten agent turns, whatever happens. It counts turns; it does not meter
  cost.
- Cost: 8 agent turns and roughly 8 to 24 judge calls.

**What you should see.** A progress line per case on stderr, then a summary with passed/failed counts. The report is
`./m365-results/m365-smoke.md`; the sealed evidence run is under `./m365-results/aef/`; the trace and issue files are
under `.agenteval/m365/` (`m365-trace-<run id>.jsonl`, `m365-issues.jsonl`).

**Exit code.** `0`: all eight cases passed. `1`: at least one case failed its judge criteria — that is a result, not
an error; note which. `11`: nothing failed, but a case has no verdict — its turn did not complete (the error column
says `InputRequired`, `Failed`, `Indeterminate`, …). `3`: a runtime problem — the judge could not be reached, the
config failed, or the turn cap was hit. `2`: a command-line mistake.

**If it fails.**

| Symptom | What to do |
|---|---|
| Exit `3` before any case runs, message mentions the judge | Check `--judge`, `--judge-model`, `--api-key`. Test them with any other tool first. |
| Exit `3`, message `Work IQ evaluation stopped: … MaxTurns` | The cap was hit; it should not be with 8 cases and a cap of 10. Send the files back. |
| Exit `11`, one or more cases with `InputRequired` | The agent asked to confirm an action on a read-only prompt. That is a finding; note the case id. |
| Exit `11`, cases with `Indeterminate` or `Failed` | Retry once. If it repeats, send the files back. |
| A device-code prompt appears again | The cache was not reused. Complete the sign-in; note it in your results. |
| Exit `1` | Expected for some prompts on some tenants (for example the "Demo Q3 Planning" case when the document is missing). Record the per-case outcomes; do not change the dataset. |

Optional, if Step 3 passed and you want to see the raw protocol traffic once:

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 2 --no-aef --m365-verbose \
  --m365-capture-raw ./m365-results/raw --format markdown -o ./m365-results/m365-raw.md
```

`--m365-verbose` prints every trace event to the console. `--m365-capture-raw` writes the raw requests, responses
and stream frames under `./m365-results/raw/`. **Those files can contain tenant content** (answers, document names,
people). Do not send them unless the maintainers ask for a specific one, and redact it first (section 7).

### Step 4 — optional: the OWASP benchmark, smoke preset

Only if Steps 1–3 worked and you accept more turns. The judge for `bench` comes from environment variables, not
from flags. Set the three Azure OpenAI judge variables (or configure a provider as in the
[CLI reference](cli.md#ai_inference_provider--which-provider-the-cli-talks-to)):

```bash
export AZURE_OPENAI_JUDGE_ENDPOINT=https://<your-resource>.openai.azure.com/
export AZURE_OPENAI_JUDGE_API_KEY=<key>
export AZURE_OPENAI_JUDGE_DEPLOYMENT=<deployment>

agenteval bench owasp --preset smoke --subject m365-default-copilot --m365-max-turns 40 \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --aef ./m365-results/aef
```

PowerShell: `$env:AZURE_OPENAI_JUDGE_ENDPOINT = "https://…"` and so on.

**What you should see.** The command checks the judge with one short call, then runs the smoke preset's probes
against the agent, one turn each, and prints a gate verdict (PASS, WARN or FAIL) with a per-attack table. The report
is written under `.agenteval/` and the AEF run under `./m365-results/aef/`.

**Exit code.** `0`: the gate passed. `9`: the gate failed — a result, not an error. `10`: WARN. `11`: the run is
INCOMPLETE (a judge call failed mid-scan, or the time ran out) — never a pass. `3`: the judge is not configured or
did not answer. `2`: command-line mistake, for example an unknown preset.

**If it fails.** Exit `3` before the scan: fix the judge variables. Exit `11`: send the files back; note how far it
got. Exit `9` or `10`: record the verdict and the per-attack table; that is the result.

## 6. What to send back

Send exactly these, zipped:

1. Every `.agenteval/m365/doctor-*/` folder (`readiness.md`, `readiness.json`, and the trace and issue files inside).
2. `.agenteval/m365/m365-trace-*.jsonl` — one file per run.
3. `.agenteval/m365/m365-issues.jsonl` — one file, all runs appended.
4. The AEF folder: `./m365-results/aef/`.
5. The markdown report: `./m365-results/m365-smoke.md` (and the OWASP report from `.agenteval/` if you ran Step 4).
6. Your filled-in [results template](#8-results-template).

Nothing else. In particular, **do not send** `workiq.json`, the `./m365-results/raw/` folder, screenshots of the
sign-in page, or your judge key.

What these files may contain, and what they never contain:

| File | May contain | Never contains |
|---|---|---|
| `readiness.md` / `readiness.json` | Your tenant id, app id, agent id, the card's URLs, HTTP statuses, error codes, the service's `request-id`, the config's SHA-256, file paths on your machine. | A token, an auth code, your user code, the `Authorization` header. |
| `m365-trace-*.jsonl` | The same ids and codes, timings, event codes, file paths. | Prompts, answers, tokens. |
| `m365-issues.jsonl` | The same, plus suggested causes and AADSTS codes. | Tokens. |
| AEF folder | The prompts, the agent's **answers**, citation display names and URLs, the judge's reasoning. | Tokens. |
| `m365-smoke.md` | The same as the AEF folder, rendered. | Tokens. |

The AEF folder and the markdown report therefore show what the agent answered. If an answer names a person or a
document you cannot share, redact it (next section) and say so in the results template.

## 7. Redact before you send

The one-liners below replace e-mail addresses and URLs in the JSONL files and the markdown report, and your tenant
id everywhere. Run them in the `AgentEval` folder. They edit the files in place; keep an unredacted copy for yourself.

Bash (Linux, macOS, Git Bash on Windows):

```bash
TENANT="<your-tenant-guid>"
find .agenteval/m365 ./m365-results -type f \( -name '*.jsonl' -o -name '*.json' -o -name '*.md' \) -print0 \
  | xargs -0 sed -E -i \
      -e 's/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/[email]/g' \
      -e 's#https?://[^"[:space:])]+#[url]#g' \
      -e "s/$TENANT/[tenant]/g"
```

PowerShell:

```powershell
$tenant = "<your-tenant-guid>"
Get-ChildItem .agenteval\m365, .\m365-results -Recurse -Include *.jsonl, *.json, *.md | ForEach-Object {
  (Get-Content $_.FullName -Raw) `
    -replace '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[email]' `
    -replace 'https?://[^"\s)]+', '[url]' `
    -replace [regex]::Escape($tenant), '[tenant]' |
    Set-Content $_.FullName -NoNewline
}
```

The URL replacement also hides the Work IQ endpoint and the card URLs; that is fine, the maintainers know them. If
an answer in the markdown report names a person or a document, replace the name by hand with `[name]` and note it in
the template.

## 8. Results template

Copy this into a text file, fill it in, and include it in the zip.

```markdown
# Microsoft 365 connector — live validation results

## Environment
- Date (UTC):
- Branch and commit (`git log -1 --oneline`):
- CLI version (`agenteval --version`):
- How the CLI was run: dotnet run / local tool
- Agent id: default / <id>  (agent name:)
- Profile: public / native118
- Tenant type: test tenant / other (no production identity used: yes / no)
- Judge for Step 3 (endpoint host and model, no key):
- Judge for Step 4 (provider and model, no key):
- Redactions applied: e-mails / URLs / tenant id / names by hand

## Doctor stages
| Step | Stage | Result (PASS / FAIL / SKIPPED) | Code shown (AADSTS / HTTP / JSON-RPC / A2A state) | Exit code |
|---|---|---|---|---|
| 1 | config | | | |
| 1 | auth | | | |
| 1 | discovery | | | |
| 1 | protocol | | | |
| 2 | probe | | | |
| 2 | census | | | |

Census facts (from readiness.md): diagnostic modes accepted: yes / no / not tested · citation format seen: · CancelTask on a completed task returned:

## Smoke benchmark (Step 3) — exit code:
| Case id | Outcome (Completed / InputRequired / Failed / Indeterminate / …) | Verdict (pass / fail / no verdict) | Citation sources availability | Note |
|---|---|---|---|---|
| identity_disclosure | | | | |
| capability_summary | | | | |
| grounded_summary_with_citation | | | | |
| explicit_date_reasoning | | | | |
| out_of_scope_live_data | | | | |
| prompt_injection_resist | | | | |
| pii_restraint | | | | |
| action_restraint_read_only | | | | |

Was the "Demo Q3 Planning" document present in the test user's files? yes / no

## OWASP smoke (Step 4, optional) — exit code:
- Gate verdict:
- Turns used:
- Anything INCOMPLETE:

## Free text
What surprised you, what took long, anything you had to work around, anything a message did not explain:

```

## 9. FAQ — failures you may see

**`AADSTS65001` during sign-in.** The administrator has not granted consent for `WorkIQAgent.Ask`. Granting consent
on the app registration's *API permissions* page fixes it. The connector cannot ask for consent itself.

**`AADSTS7000218` during sign-in.** Entra wants a client secret because the app is not a public client. Set *Allow
public client flows* to **Yes** on the app's *Authentication* page. The connector never uses a secret.

**HTTP 403 on discovery or protocol.** Three causes look the same from outside: the test user has no Microsoft 365
Copilot licence, Work IQ is not enabled in the tenant, or admin consent is missing on the RPC endpoint. The
administrator can tell which. The issue register records the `request-id` Microsoft support can look up.

**The agent answers nothing useful, or answers about the application UI.** Word, Excel and PowerPoint agents are
bound to their application and do not work headlessly. Target the default agent (`"agentId": null`) or a declarative
agent that works in Copilot Chat.

**A turn takes minutes and then ends `Indeterminate` or `Timeout`.** The connector sends a blocking request and waits
up to 300 seconds for the reply, then polls the task for up to 2 minutes more. A long-running task can exceed that.
Nothing is resent. Note the case id and the elapsed time from the trace; the maintainers may raise the limits.

**"The service answered in A2A 0.3" / `ProtocolVersionMismatch`.** Work IQ defaults to protocol version 0.3 when a
client does not say which version it speaks. This connector always sends the header `A2A-Version: 1.0` and refuses
to read a 0.3-shaped reply. If you see this, the service ignored the header or changed; send the trace and the
doctor's card dump back. Nothing on your side causes it.

**Citations look different from what the report expects.** Microsoft is changing the citation format. The connector
keeps the raw marker text and the raw attribution objects exactly as sent and labels every marker-to-source binding
`Unresolved` unless the service gave an explicit key. A citation that is present but "unresolved" is expected, not a
failure. If a case shows citation sources `NotProvided` although the answer visibly cites something, note it: that is
a finding about where the service puts attributions.

**The device-code prompt appears on every run.** The token cache (`%LOCALAPPDATA%\AgentEval\msal_cache\` on Windows,
the OS keychain or secret service elsewhere) could not be written or read. Complete the sign-in each time; note it.

**Exit `11` from `eval` with no failure.** A case produced no verdict: its turn did not complete, so the judge never
saw an answer. The error column names the outcome. This is a connector or tenant finding, not a judge problem.

**Exit `1` from `eval`.** One or more cases failed their criteria. That is a legitimate result about the agent. Do not
rewrite the prompts to make them pass.

## 10. Where to ask

Open an issue on the repository, or send the zip to the maintainer who gave you this guide. Include the results
template. Please do not post the files publicly before the maintainers have checked them for tenant data.
