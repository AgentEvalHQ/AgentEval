# Microsoft 365 connector — tester guide

This guide is for one person: the external tester who has a Microsoft 365 tenant and runs the connector against it
for the first time. It assumes no knowledge of this repository. Every step gives the exact command, what you should
see, what each exit code means, what a failure means and what to do next.

The maintainers have no tenant. Nothing in this guide has been run live by them: your run is the first evidence.
Allow about an hour, most of it for the prerequisites.

## Start here: the demo project

The fastest way through this validation is the demo project
[`samples/AgentEval.Microsoft365Demo`](../samples/AgentEval.Microsoft365Demo/README.md): one console app that runs the
readiness check, one read-only prompt and a small benchmark, prints every trace event and issue at the verbosity you
choose, and saves everything in one run folder that you zip and send back. You need the prerequisites of
[section 2](#2-what-you-need) and the config file of [section 4](#4-the-config-file) either way.

```bash
git clone --branch work/dev https://github.com/AgentEvalHQ/AgentEval.git
cd AgentEval
cp samples/datasets/m365-config.template.json workiq.json        # put your tenantId and appClientId in it (section 4)
dotnet run --project samples/AgentEval.Microsoft365Demo -- --readiness-only
dotnet run --project samples/AgentEval.Microsoft365Demo -- --i-understand-live-side-effects --zip
```

The `workiq.json` in the clone's root is the same file the rest of this guide uses.

1. **`--readiness-only`** checks the config, signs you in (the first time with a device-code message: open the page,
   enter the code, sign in as the test user), fetches the agent card and checks the protocol. No prompt reaches the
   agent. It must exit `0`. If a stage fails, open `summary.md` or `readiness.md` in the run folder it printed: the
   failed stage says what it means and what to do, and the tables of [Step 1](#step-1--readiness-check-no-prompt)
   below apply unchanged.
2. **The full run** needs `--i-understand-live-side-effects`, because the agent now acts as the signed-in user: use a
   non-production identity only. It sends one prompt and five read-only benchmark prompts (6 agent turns). A case
   whose turn did not complete is an `ERROR` row, never a graded zero. The criteria are graded only by a judge model
   configured in your environment (`AI_INFERENCE_PROVIDER` and that provider's variables; each call costs money,
   `--no-judge` makes none); without one they are `NOT MEASURED` and the run exits `11`, which is an honest result,
   not a failure.
3. **Send back** the `.zip` it printed and the exit code, plus the [results file](microsoft365-tester-RESULTS-TEMPLATE.md)
   if you have time. Read the demo README's *What to send back* first: the folder holds the agent's answers and your
   tenant id, and never a token.

The [demo README](../samples/AgentEval.Microsoft365Demo/README.md) lists every option, the verbosity levels, the run
folder, the exit codes and how to redact. Opened in VS Code on its own, the folder brings its own GitHub Copilot agent
(`m365-demo`) that sets it up with you, adapts it, and reads a run folder back.

The rest of this guide is the same validation with the `agenteval` command-line tool (`m365 doctor`, `eval`, `bench`).
Follow it when the maintainers ask for the CLI results, or when the demo cannot run. (Developers who want the same path
in a few lines of C# can read the samples R1 *Hello World* and R2 *Live Walkthrough* in `samples/AgentEval.Samples`;
you do not need them.)

## 1. What you will check

The connector lets AgentEval evaluate a Microsoft 365 agent (Microsoft 365 Copilot, or a declarative agent) over the
Work IQ A2A 1.0 protocol. In four steps you check that it can:

1. read its config file, sign you in, fetch the agent's card and talk A2A 1.0 to the service — **no prompt is sent**;
2. run one real, read-only prompt end to end;
3. run an 8-prompt smoke benchmark graded by a judge model of your choice;
4. optionally, run the OWASP benchmark's smoke preset (23 adversarial prompts).

Every step writes files that say exactly what happened, stage by stage. You send those files back with a filled-in
[results file](microsoft365-tester-RESULTS-TEMPLATE.md). You never need to read code.

## 2. What you need

| Item | Why | Where to get it |
|---|---|---|
| A **non-production** Microsoft 365 tenant and a **test user** in it | The agent acts as the signed-in user on that user's real mailbox, files and calendar. The connector never approves an action and never resends a prompt, but a prompt can make the agent propose one. | Your organisation's test tenant. Do not use a production identity. |
| **Work IQ enabled** in the tenant | Without it the service answers HTTP 403. | An administrator, in the Microsoft 365 admin center. |
| A **Microsoft 365 Copilot licence** on the test user | Without it the service answers HTTP 403. | Microsoft 365 admin center → Users → the test user → Licenses. |
| An **Entra app registration** configured as a **public client** | The connector signs in with the device-code flow and holds no secret. Without this setting the sign-in fails with `AADSTS7000218`. | Entra admin center → App registrations → New registration (single tenant is fine). Then *Authentication* → *Advanced settings* → **Allow public client flows = Yes** → Save. No client secret or certificate is used. |
| The **delegated permission `WorkIQAgent.Ask`** on that app, with **admin consent** | The token scope the service accepts (`api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`). Without consent the sign-in fails with `AADSTS65001`. The permission covers reads **and writes** of the user's Microsoft 365 data. | The app's *API permissions* → *Add a permission* → *APIs my organization uses* → the Work IQ API (application ID URI `api://workiq.svc.cloud.microsoft`) → *Delegated* → `WorkIQAgent.Ask` → *Add*. Then **Grant admin consent** for the tenant. |
| The **tenant id** and the app's **client id** | For the config file. | The app registration's *Overview* page: *Directory (tenant) ID* and *Application (client) ID*. |
| A small synthetic corpus in the test user's OneDrive or SharePoint, with a document titled **"Demo Q3 Planning"** | One smoke prompt asks the agent to summarise it and cite it. | Create any short document with that title. Missing it makes that case fail, which is still a valid result. |
| An OpenAI-compatible **judge endpoint, model and key** you are allowed to spend on | Step 3 grades the answers with a judge model. | Your own provider: OpenAI, an Azure OpenAI deployment, or any OpenAI-compatible host. |
| Optional, for Step 4: an **Azure OpenAI deployment** for the benchmark's judge | `bench` takes its judge from environment variables, not flags. | Any Azure OpenAI resource you may spend on. |
| The **.NET 10 SDK** | To build the command-line tool. | <https://dotnet.microsoft.com/download/dotnet/10.0> |
| **git** | To get the code. | <https://git-scm.com> |
| A browser, on any device | To complete the device-code sign-in. | — |
| Optional: the **agent id** of a declarative agent | To target an agent other than the default Microsoft 365 Copilot. | The experimental `workiq list-agents` command of Microsoft's WorkIQ CLI, or the agent's Copilot Chat URL. Ids look like `U_abc.def` (a letter, an underscore, opaque text); pass the whole string exactly as shown. Word, Excel and PowerPoint agents are bound to their application and do not answer usefully here. |

Tell your administrator before Step 4: it sends prompt-injection, jailbreak and personal-data extraction attempts to the
agent as the test user, and the tenant's security tooling may record them.

## 3. Install

### 3.1 Get and build the code

```bash
git clone --branch work/dev https://github.com/AgentEvalHQ/AgentEval.git
cd AgentEval
dotnet build src/AgentEval.Cli
```

The build must end with `Build succeeded`. If it ends with `Build failed`, send the last 50 lines of the output back
and stop. Run **every** command in this guide from this `AgentEval` folder (the clone's root).

### 3.2 Make the `agenteval` command available

This guide writes every command as `agenteval …`. Pick one of the two options and keep it for the whole session.

**Option A — run from the clone (recommended).** Define a shell function once per terminal session:

```bash
# Bash (Linux, macOS, Git Bash on Windows)
agenteval() { dotnet run --project src/AgentEval.Cli --framework net10.0 --no-build -- "$@"; }
```

```powershell
# PowerShell
function agenteval { dotnet run --project src/AgentEval.Cli --framework net10.0 --no-build '--' @args }
```

`--framework net10.0` is needed because the tool is built for two .NET versions; `--no-build` reuses the build from
3.1 (after a `git pull`, run `dotnet build src/AgentEval.Cli` again). Without the function, type the whole prefix:
`dotnet run --project src/AgentEval.Cli --framework net10.0 -- m365 doctor …`.

**Option B — pack and install the tool locally.** This gives a real `agenteval` executable under `./tools`. The
version must differ from the one on NuGet, so a local version string is passed:

```bash
dotnet pack src/AgentEval.Cli -c Release -o ./nupkg -p:PackageVersion=1.0.0-m365.1
dotnet tool install --tool-path ./tools AgentEval.Cli --version 1.0.0-m365.1 --add-source ./nupkg
```

Then type `./tools/agenteval` (Windows: `.\tools\agenteval.exe`) where this guide says `agenteval`. To remove it:
`dotnet tool uninstall --tool-path ./tools AgentEval.Cli`.

### 3.3 Check the install

```bash
agenteval --version
agenteval m365 doctor --help
agenteval eval --help
```

`--version` prints a version such as `1.0.0-rc.1+<commit>`. `m365 doctor --help` must list `--m365-config`,
`--stage`, `--probe`, `--i-understand-live-side-effects`, `--continue`, `--report` and `--timeout-seconds`.
`eval --help` must list `--sut` (with `microsoft365` among the valid values), `--m365-config`, `--m365-max-turns`,
`--m365-profile`, `--m365-capture-raw`, `--m365-verbose`, `--m365-trace` and `--i-understand-live-side-effects`. If
anything is missing you are on the wrong branch: send the output of `git log -1 --oneline` back.

### 3.4 Create the workspace folder and the results folder

```bash
agenteval init-workspace
mkdir m365-results
```

`init-workspace` prints `✔ Initialized .agenteval/ at <path>` (or `Already initialized at …`). It creates the
`.agenteval/` folder in the clone's root. Every trace, issue register and readiness report of this guide then lands in
`.agenteval/m365/`, so there is one folder to collect at the end. Without it the files go to your temp folder
(`<temp>/agenteval-m365/`); the commands still work and print where they wrote. Git ignores `.agenteval/`.

## 4. The config file

Copy the template to `workiq.json` in the clone's root and fill in the two required values:

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

| Field | What to put | Notes |
|---|---|---|
| `tenantId` | **Required.** The *Directory (tenant) ID*. | A value still reading `REPLACE-WITH-…` is refused by the `config` stage, with nothing sent. A value that is neither a GUID nor a domain name passes with a warning: the sign-in would fail with `AADSTS90002`. |
| `appClientId` | **Required.** The *Application (client) ID* of the public-client app. | Your own app registration, never a Microsoft-owned client id (`AADSTS65002`). A placeholder is refused; a value that is not a GUID passes with a warning (`AADSTS700016` would follow). |
| `agentId` | `null` for the default Microsoft 365 Copilot, or the declarative agent's id as a string. | One path segment, passed as-is: no `/`, `\`, `?`, `#` or spaces. |
| `agentName` | Any label. | Appears in reports. |
| `timeZone` | Your IANA time zone, such as `Europe/Zurich` or `America/New_York`. | Sent with every prompt so date questions are deterministic. Delete the line if unsure. |
| `diagnosticProfile` | Leave `PublicAnswerOnly`. | The experimental alternative is tested by the doctor's census in Step 2. |
| `endpoint`, `scope`, `trustedHosts` | Leave as they are. | The token is only ever sent to a host in `trustedHosts`. |

The file holds **no secret**: the sign-in happens at run time in your browser, and the token is kept in an
OS-encrypted cache outside the repository. Do not commit `workiq.json` (Git does not ignore it in the clone's root)
and do not send it back.

## 5. The sequence

| Step | Command (short) | Prompts sent to the agent | Spends | Go on when |
|---|---|---|---|---|
| 1 | `agenteval m365 doctor --m365-config ./workiq.json` | none | nothing | exit `0` |
| 2 | the same `--probe --i-understand-live-side-effects` | 1 or 2 (the same read-only prompt) | nothing beyond the agent turns | exit `0` |
| 3 | `agenteval eval --sut microsoft365 …` on `samples/datasets/m365-smoke.json` | 8 | 8 agent turns and your judge's calls | any exit (record it) |
| 4 (optional) | `agenteval bench owasp --preset smoke --sut microsoft365 …` | 23 | 23 agent turns and judge calls | — |

Run the steps in order. When a step fails, read its "If it fails" table. If you cannot fix it, stop and send the
files of [section 6](#6-what-to-send-back).

The console output of the doctor is long: every line that starts with `[m365 ` is one trace event (the same events
are saved in the trace file). The block that starts `Microsoft 365 readiness (m365 doctor)` at the end is the
summary that matters.

### Step 1 — readiness check (no prompt)

```bash
agenteval m365 doctor --m365-config ./workiq.json
```

**What it does.** It runs four stages in order and stops at the first failure:

| Stage | What it does | Network |
|---|---|---|
| `config` | Reads and validates `workiq.json`; records the endpoint, card URL, agent, tenant, app id, scope, profile, trusted hosts, time zone and the config's SHA-256. | none |
| `auth` | Gets a token: silently from the cache, else by device-code sign-in. | Entra sign-in |
| `discovery` | Fetches the agent's card and picks the A2A 1.0 JSON-RPC interface on a trusted host. | Work IQ (the card) |
| `protocol` | Asks for a task with a random id (`GetTask`). The expected answer is the JSON-RPC error "task not found" (`-32001`): it proves the endpoint speaks A2A 1.0 and accepts your token. | Work IQ (one RPC) |

Nothing is sent to the agent itself.

**What you should see.** First a line naming the report folder:

```text
m365 doctor: report folder <clone>/.agenteval/m365/doctor-20261105-184133Z; every trace event follows on this stream (--m365-verbose false hides them).
```

Then trace lines. During `auth`, on the first run, a sign-in message:

```text
To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate.
```

Open the page (on any device), enter the code and sign in **as the test user**. Once that message is shown, the stage
waits until the code expires (about 15 minutes); `--timeout-seconds` does not cut a sign-in short. The trace file
keeps the message, and the time the code is valid until, with the code replaced by `[redacted]`. Then the summary,
which looks like this (your ids, names and times differ):

```text
Microsoft 365 readiness (m365 doctor) — run m365-20261105-184133-5f30
  [PASS]    config     config valid: My Non-Prod Microsoft 365 Agent; default agent; tenant <your tenant id>; profile PublicAnswerOnly
  [PASS]    auth       token acquired by device-code sign-in; tenant <your tenant id>; expires 2026-11-05 19:41 UTC
  [PASS]    discovery  card '<card name>' fetched; 2 interface(s); chose JSONRPC 1.0 at workiq.svc.cloud.microsoft
  [PASS]    protocol   the service answered GetTask on a random task id with JSON-RPC TaskNotFound (-32001)
  [SKIPPED] probe      not requested: the probe sends one real prompt to the agent; ask for it with --probe (it also needs --i-understand-live-side-effects)
  [SKIPPED] census     not requested: the census runs after the probe (--probe --i-understand-live-side-effects)

Result: PASS — every requested stage passed.

Report folder: <clone>/.agenteval/m365/doctor-20261105-184133Z
  readiness.md:   <clone>/.agenteval/m365/doctor-20261105-184133Z/readiness.md
  readiness.json: <clone>/.agenteval/m365/doctor-20261105-184133Z/readiness.json
Trace:  <clone>/.agenteval/m365/doctor-20261105-184133Z/m365-trace-m365-20261105-184133-5f30.jsonl
Issues: <clone>/.agenteval/m365/doctor-20261105-184133Z/m365-issues.jsonl
Send back the report folder, the trace and the issues file. None of these files contains a token, an auth code or the Authorization header.
```

`probe` and `census` are `SKIPPED` because they were not requested; that is expected here and does not change the
result. A failed stage prints three more lines under it — `issue:` (category, code, AADSTS code, HTTP status,
JSON-RPC code), `meaning:` and `do:` — and `readiness.md` has the same table plus every stage's evidence.

**Exit code.**

| Code | Meaning | Next |
|---|---|---|
| `0` | Every requested stage passed. | Step 1b. |
| `1` | A stage failed. The summary and `readiness.md` name it, say why and what to do. | The table below. |
| `2` | A command-line mistake (missing `--m365-config`, unknown `--stage` or `--m365-profile` value, `--timeout-seconds` below 1). The message says which. | Fix the command. |
| `3` | The report folder or the report files could not be written, or the check could not start. The message names the path. | Check disk space and permissions on `.agenteval/`; send the message back if unclear. |

**If it fails.** Read the stage's `meaning:` and `do:` lines first; they are written for this situation. The usual
causes:

| Stage | What the summary or `readiness.md` shows | What it means | What to do |
|---|---|---|---|
| `config` | `Microsoft 365 config file not found: …` | The path is wrong. | Run from the clone's root; check `./workiq.json` exists. |
| `config` | `Invalid Microsoft 365 config JSON (…): …` | The file is not valid JSON. | Fix the syntax at the line the message names. |
| `config` | `Microsoft 365 config is missing required field(s): …` | `tenantId` or `appClientId` is missing or blank. | Fill in the named fields. |
| `config` | `Microsoft 365 config field '…' still holds the template placeholder 'REPLACE-WITH-…'` | A template value was never replaced. Nothing was sent. | Put the value the message names (from the app registration's *Overview* page). |
| `config` | `Microsoft 365 config field '…' …` | One field has a bad value (endpoint not https, `trustedHosts` without the endpoint host, an `agentId` with a `/`, an unknown time zone or profile). | Fix that field as the message says. |
| `config` | `PASS` with a warning: `tenantId '…' is neither a GUID nor a domain name` or `appClientId '…' is not a GUID` | A value that cannot be right; the sign-in would fail with `AADSTS90002` or `AADSTS700016`. | Copy both ids from the app registration's *Overview* page. |
| `auth` | `AADSTS65001` | Admin consent for `WorkIQAgent.Ask` is missing. | An administrator grants admin consent on the app's *API permissions* page. |
| `auth` | `AADSTS7000218` | The app is not a public client. | Set *Allow public client flows* to **Yes** on the app's *Authentication* page. |
| `auth` | `AADSTS700016` | The app was not found in that tenant. | Check `appClientId` and `tenantId`; both must belong to the same tenant. |
| `auth` | `AADSTS90002` | The tenant was not found: a typo, a deleted tenant, or a tenant in another cloud. | Check `tenantId`: the *Directory (tenant) ID*, or the tenant's verified domain. |
| `auth` | `AADSTS50059` / `AADSTS90019` | No tenant could be identified (for example a personal account), or the request named none. | Set `tenantId`, and sign in with a work account of that tenant. |
| `auth` | `AADSTS50020` | The account you signed in with is not in that tenant. | Sign in as the test user of that tenant. |
| `auth` | `AADSTS50076` / `AADSTS50079` | Multi-factor authentication is required, or must be set up first. | Complete it in the browser, then run again. |
| `auth` | `AADSTS50105` | The user is not assigned to the app. | An administrator assigns the user, or turns off *Assignment required* on the enterprise application. |
| `auth` | `AADSTS53003` / `AADSTS530003` | A conditional access policy blocked the sign-in (for example, a managed device is required). | An administrator checks the policy in the Entra sign-in log; sign in from a compliant device. |
| `auth` | `sign-in not completed: the device code expired at … UTC before the sign-in finished` / `code_expired` | The browser sign-in was not finished while the code was valid (about 15 minutes). Nothing was sent to Work IQ. | Run again, open the page, enter the code and sign in as the test user. |
| `auth` | `sign-in not completed in time: no token within 120 s` | No sign-in was started and the token cache or Entra did not answer within the stage timeout. Nothing was sent. | Check the path to `login.microsoftonline.com`; run again, with `--timeout-seconds 300` if it is slow. |
| `auth` | any other `AADSTS…` code | Not in the doctor's catalogue; the code is kept with a link to Microsoft's list. | Look it up at <https://learn.microsoft.com/entra/identity-platform/reference-error-codes>; send the folder back if unclear. |
| `discovery` or `protocol` | `HTTP 401` | The service rejected the token. | Check that `scope` is unchanged and that `tenantId` and `appClientId` name the app that holds `WorkIQAgent.Ask`; run again. |
| `discovery` or `protocol` | `HTTP 403` | The user or app lacks the permission or its consent, the user has no Copilot licence, or Work IQ is not enabled. The three look the same from outside. | Ask the administrator to check all three, then run again. |
| `discovery` | `HTTP 404` | The `agentId` (or the endpoint) is wrong, or the user cannot reach that agent. | Check `agentId`; try `null` (the default agent). |
| `discovery` | `the card points at a host outside trustedHosts; no token was sent there` | The card advertised a URL on a host the config does not trust. | Send the folder back. Add a host to `trustedHosts` only if it is clearly Microsoft's. |
| `discovery` | `the card advertises no A2A 1.0 JSON-RPC interface (no 1.0 interface)` | The card does not offer the protocol this connector speaks. It never falls back to 0.3. | Send the folder back; nothing on your side causes it. |
| `discovery` or `protocol` | `the service rejected A2A 1.0 (JSON-RPC -32009 VersionNotSupported)` or `the reply has the A2A 0.3 shape` | The service ignored the `A2A-Version: 1.0` header (see the FAQ). | Send the folder back; nothing on your side causes it. |
| `discovery` or `protocol` | `Transport on …` or `no answer within the stage timeout of 120 s` | Network, proxy or DNS problem, or no answer in time. | Check the path to `workiq.svc.cloud.microsoft`; run again, with `--timeout-seconds 300` if the service is slow. |
| `discovery` or `protocol` | `RateLimited on …` / `HTTP 429` | The service throttled. | Wait a few minutes and run again. |
| `protocol` | `HTTP 404 on GetTask instead of the JSON-RPC TaskNotFound error (-32001)` | The selected interface URL does not behave like the A2A 1.0 JSON-RPC endpoint. | Send the folder back. |
| `protocol` | `JSON-RPC error … on GetTask instead of TaskNotFound (-32001)` | The service answered, but not as A2A 1.0 says. | Send the folder back. |
| `protocol` | `PASS` with a warning: a task came back for a random id | Unexpected, but the endpoint speaks A2A 1.0 and accepted the token. | Continue; mention it in the results file. |

`--continue` runs the remaining stages after a failure, which can tell you more in one go (for example whether
`protocol` also fails after a `discovery` failure). `--stage auth` (or `discovery`, `protocol`) runs one stage, plus
the `config` stage it needs; a later stage acquires its own token and card as needed. `--stage census` also runs the
probe (both need the consent flag of Step 2).

### Step 1b — run Step 1 again (the token cache)

```bash
agenteval m365 doctor --m365-config ./workiq.json
```

Exit `0`, no sign-in message, and the `auth` line now says `token acquired silently from the cache`. If the sign-in
message appears again, the token cache could not be written or read; complete the sign-in and note it in the results
file. The cache is an OS-encrypted file under your user profile (`%LOCALAPPDATA%\AgentEval\msal_cache\` on Windows,
the platform equivalent elsewhere); never send it.

### Step 2 — readiness check with one real prompt

This step sends a prompt to the agent for the first time, so it needs the consent flag: the agent now acts as the
signed-in user.

```bash
agenteval m365 doctor --m365-config ./workiq.json --probe --i-understand-live-side-effects
```

**What it does.** The four stages of Step 1 (sign-in is now silent), then:

| Stage | What it does |
|---|---|
| `probe` | Sends one read-only prompt, `Reply with the single word READY.`, in a fresh conversation, and waits for the task to finish. Passes only if the turn completes. Nothing is ever approved on your behalf. |
| `census` | Records what your tenant does: sends the same prompt **once more** under the experimental `native118` profile (which also asks for the diagnostic *Developer Info* and *Retrieval Info* output), then calls `CancelTask` on the finished task to see what the service answers. Informational: it passes whether the tenant accepts or refuses the experimental profile. |

With `--m365-profile native118` the probe already runs under that profile and the census sends no second prompt.

**What you should see.** The Step 1 lines, then (your values differ):

```text
  [PASS]    probe      the turn completed in 5321 ms: 5-character answer, 2 attribution(s)
  [PASS]    census     native118 accepted; Developer Info …; Retrieval Info …; citations …; CancelTask …
```

`readiness.md` shows the prompt, the first 80 characters of the answer, the task and context ids, the task states
observed, how many `GetTask` polls were needed, the attribution and citation-marker counts and the timings — the
facts the maintainers cannot see without a tenant. A `warning:` line under `probe` saying the answer is not the single
word READY is informational.

**Exit code.**

| Code | Meaning | Next |
|---|---|---|
| `0` | Every requested stage passed. | Step 3. |
| `1` | A stage failed. | The table below. |
| `11` | Nothing failed, but `probe` and `census` were **skipped**: the consent flag is missing (`Result: INCOMPLETE`). No prompt was sent. | Add `--i-understand-live-side-effects`. |
| `2` / `3` | As in Step 1. | As in Step 1. |

**If it fails.**

| What the summary or `readiness.md` shows | What it means | What to do |
|---|---|---|
| `probe` … `the agent paused (InputRequired)` | The agent asked for a confirmation or more input. Nothing was approved. | Unexpected for this prompt: send the folder back. |
| `probe` … `the agent paused (AuthRequired)` | The agent needs a connector sign-in that this tool cannot complete. | Sign in to that connector in Microsoft 365 Copilot Chat, then run again. |
| `probe` … `Indeterminate: the send may have been accepted; do not resend` | No final task state arrived before the 120 s stage limit or a network drop. The prompt was not resent. | Run again once, with `--timeout-seconds 300` for a slow agent. If it repeats, send both report folders back. |
| `probe` … `the turn ended Failed` / `Rejected` / `Canceled` | The service ended the task with an error. | Send the folder back; the HTTP status, JSON-RPC code and `request-id` are in it. |
| `probe` with `HTTP 429` | Throttled. | Wait a few minutes and run again. |
| `probe` with `HTTP 401` / `403` | As in Step 1. | As in Step 1. |
| `probe` `PASS` with a warning about an empty answer or no answer text | The task completed without a usable answer. A finding about the agent, not your mistake. | Continue; mention it in the results file. |
| `census` `PASS` with `native118 refused` | Your tenant refuses the experimental profile. A fact worth recording, not a failure. | Continue: the evaluation uses the default profile. |
| `census` `FAIL` `the census could not be measured` | The second turn ended without an answer or a clear refusal. | Send the folder back. |

### Step 3 — the smoke benchmark

Eight read-only prompts from `samples/datasets/m365-smoke.json`, graded by your judge. Set the judge's key in an
environment variable so it does not land in your shell history:

```bash
export OPENAI_API_KEY="<your-judge-key>"          # PowerShell: $env:OPENAI_API_KEY = "<your-judge-key>"
```

Then (PowerShell: replace each trailing `\` with a backtick `` ` ``, or put the command on one line):

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 10 \
  --judge <judge-base-url> --judge-model <judge-model> \
  --subject agent:m365-smoke --aef ./m365-results/aef --aef-content on \
  --format json -o ./m365-results/m365-smoke.json
```

- `--judge` is an OpenAI-compatible base URL: `https://api.openai.com/v1` for OpenAI; for Azure OpenAI its
  OpenAI-compatible v1 endpoint `https://<resource>.openai.azure.com/openai/v1/`. `--judge-model` is the model, or the
  Azure deployment name; always pass it (its default is the agent's name, which is not a model). The key is read from
  `OPENAI_API_KEY`; `--api-key <key>` also works.
- `--m365-max-turns 10` stops the run before an 11th agent turn, whatever happens. It counts turns; it does not
  meter cost (the service reports none).
- `--aef-content on` records each case's prompt, the agent's answer and the connector's evidence document (answer,
  citations, which evidence channels were provided) in the sealed evidence folder. That is what lets the maintainers
  see the real shapes. Leave it out if you cannot share answers at all, and say so in the results file.
- Cost: 8 agent turns and, typically, 8 to 24 judge calls.

**What you should see.** On stderr, one line before the run starts:

```text
Note: --sut microsoft365 observes the agent's final answer and the evidence the tenant emits; tool and retrieval evidence is reported as NotRequested / NotProvided / Provided per channel, never inferred. Trace: <clone>/.agenteval/m365/m365-trace-<run id>.jsonl · Issues: <clone>/.agenteval/m365/m365-issues.jsonl Turn cap: 10 (turns counted, consumption not metered).
```

Then the cases run (one agent turn each; no sign-in message, the token is cached since Step 1), a summary block
with passed, failed and not-measured counts, and the folder and hash of the sealed evidence run. The report is
`./m365-results/m365-smoke.json`: per case, its name, score, passed or not, the agent's answer (`output`) and, for a
case without a verdict, the reason (`error`).

Then check the evidence folder (use the run folder the command printed):

```bash
agenteval aef verify <the AEF run folder the command printed>
```

It must say the run is intact (exit `0`); exit `1` means invalid — note it.

**Exit code.**

| Code | Meaning | Next |
|---|---|---|
| `0` | All eight cases passed their judge criteria. | Step 4 (optional), then section 6. |
| `1` | At least one case failed its criteria. That is a **result** about the agent, not an error. | Record which; do not change the dataset. |
| `11` | Nothing failed, but at least one case has **no verdict**: its turn did not complete, or the judge gave no answer. | See the table below. |
| `3` | The run stopped: the consent flag or the config is missing or invalid (`Error: …` before any case), the judge URL is malformed, the turn cap was reached, or the evidence folder could not be written. | Read the `Error:` line. |
| `2` | A command-line mistake. | Fix the command. |

**If it fails.**

| Symptom | What it means | What to do |
|---|---|---|
| Exit `3`, `Error: --sut microsoft365 drives a LIVE Microsoft 365 agent …` | The consent flag is missing. Nothing was sent. | Add `--i-understand-live-side-effects`. |
| Exit `3`, `Error: Microsoft 365 config …` | The config failed validation. Nothing was sent. | As in Step 1, `config`. |
| Exit `3`, `Error: Work IQ evaluation stopped: … MaxTurns (10)` | The turn cap was reached. It should not be with 8 cases. | Send the files back. |
| Exit `11`, a case's `error` says `Work IQ turn did not complete: outcome=InputRequired` | The agent asked to confirm an action on a read-only prompt. Nothing was approved. | A finding: note the case id. |
| Exit `11`, `outcome=Indeterminate`, `Failed` or `Rejected` | The turn did not complete. The prompt was not resent. | Run Step 3 once more; if it repeats, send the files back. |
| Exit `11`, every turn completed but the cases are "not measured" | The judge did not answer (wrong URL, key or model). | Test the judge with any other tool, fix it, run Step 3 again (8 more agent turns). |
| A line `[m365] WARNING case=… the turn COMPLETED but no answer was selected (answer Ambiguous)` (or `None`), and that case's `output` is empty | The task completed, but its answer could not be chosen: several answer candidates and none named as the answer (`Ambiguous`), or no answer at all (`None`). The case is graded on an empty text that is **not** the agent's answer; the response carries `FinishReason` `completed-no-answer`. | A finding about the agent's output shape: note the case id. The evidence folder (`--aef-content on`) keeps every candidate. |
| A sign-in message appears | The cache expired or was not reused. | Complete the sign-in; note it. |
| A case's `error` says `… sign-in not completed: no token was acquired before the wait was stopped …; nothing was sent` | A sign-in started during the run and was not finished in time. Nothing reached the agent. | Run Step 1 again (it caches the token), then Step 3. |
| Exit `1` | Some cases failed their criteria (for example the "Demo Q3 Planning" case when the document is missing). | Record the per-case outcomes. Do not rewrite the prompts. |

**Optional: the raw protocol traffic, once.** Only if the maintainers ask for it:

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 2 --no-aef --m365-verbose \
  --m365-capture-raw ./m365-raw --format json -o ./m365-raw/m365-raw.json
```

`--m365-verbose` prints every trace event to the console, plus one `[m365 turn] case=… outcome=… …` line per turn.
`--m365-capture-raw` writes the raw requests, responses and stream frames under `./m365-raw/` (authorization headers
and tokens are removed first). **Those files contain tenant content** (answers, document names, people): send one
only when the maintainers ask for it, after reading it. The run stops after two turns.

### Step 4 — optional: the OWASP benchmark, smoke preset

Only if Steps 1–3 worked, your administrator knows (section 2), and you accept 23 more agent turns. The benchmark's
judge comes from environment variables, not from flags. Set the three Azure OpenAI judge variables:

```bash
export AZURE_OPENAI_JUDGE_ENDPOINT="https://<your-resource>.openai.azure.com/"
export AZURE_OPENAI_JUDGE_API_KEY="<key>"
export AZURE_OPENAI_JUDGE_DEPLOYMENT="<deployment>"
# PowerShell: $env:AZURE_OPENAI_JUDGE_ENDPOINT = "https://…" and so on
```

(Any provider the [CLI reference](cli.md#ai_inference_provider--which-provider-the-cli-talks-to) describes also
works.) Then:

```bash
agenteval bench owasp --preset smoke --subject m365-smoke \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --m365-max-turns 30 --aef ./m365-results/aef
```

**What it does.** It checks the judge with one short call, then sends the smoke preset's 23 probes (prompt injection,
jailbreak, personal-data leakage) to the agent, one turn each, grades every answer, and prints a gate verdict (PASS,
WARN or FAIL) with a per-attack table. The preset has a 10-minute time limit; a slow agent can run out of time, which
makes the run INCOMPLETE (exit `11`), never a pass.

**Exit code.**

| Code | Meaning | Next |
|---|---|---|
| `0` | The gate passed. | Section 6. |
| `9` | The gate failed: the agent gave in to at least one attack. A **result**, not an error. | Record the verdict and the per-attack table. |
| `10` | WARN. | Record it. |
| `11` | INCOMPLETE: a judge call failed mid-scan, or time ran out. Never a pass. | Record how far it got; send the files back. |
| `3` | The judge is not configured or did not answer the first call. | Fix the three judge variables. |
| `2` | A command-line mistake, an unknown preset, or the consent flag missing (for `bench` a missing consent flag is a usage error). | Fix the command. |

If the turn cap stops the scan, it ends with an error and a non-zero exit, never a pass.

## 6. What to send back

Zip exactly these:

1. The folder `.agenteval/m365/` — every `doctor-*` report folder (`readiness.md`, `readiness.json`, its trace and
   issue files), every `m365-trace-*.jsonl` of Steps 3 and 4, and `m365-issues.jsonl`.
2. The folder `./m365-results/` — `m365-smoke.json` and the sealed evidence folder `aef/`.
3. Your filled-in results file (section 8).

Run the redaction of section 7 first. **Do not send** `workiq.json`, the `./m365-raw/` folder (unless asked), the
token cache, screenshots of the sign-in page, or any key.

What these files may contain, and what they never contain:

| File | May contain | Never contains |
|---|---|---|
| `readiness.md` / `readiness.json` | Your tenant id, app id, agent id, a masked account name, the card's URLs, HTTP statuses, error codes, the service's `request-id`, the config's SHA-256, file paths on your machine; after Step 2 the probe prompt and the first 80 characters of the answer. | A token, an auth code, the device code, the `Authorization` header. |
| `m365-trace-*.jsonl` | The same ids and codes, timings, event codes, counts, file paths; the device-code sign-in message with its code replaced by `[redacted]`. | Prompts, answers, tokens, the device code. |
| `m365-issues.jsonl` | The same, plus suggested causes and AADSTS codes. | Prompts, answers, tokens. |
| `m365-smoke.json` | The prompts' case names, the agent's **answers**, scores, error messages. | Tokens. |
| `aef/` (with `--aef-content on`) | The prompts, the agent's **answers**, citation names and URLs, the evidence documents, verdicts. | Tokens, keys. |

## 7. Redact before you send

The commands below replace e-mail addresses, URLs and your tenant id in the trace, issue and readiness files and in
`m365-smoke.json`. They edit the files in place: keep an unredacted copy for yourself. Run them in the clone's root.

**Do not edit the `aef/` folder.** It is sealed: any change makes `agenteval aef verify` report it invalid, and the
maintainers can then no longer trust it. If its content (the agent's answers) cannot be shared as it is, leave the
`aef/` folder out of the zip and say so in the results file.

Bash (Linux, macOS, Git Bash on Windows; uses `perl`, which all three have):

```bash
export TENANT="<your-tenant-guid>"
find .agenteval/m365 -type f \( -name '*.jsonl' -o -name '*.json' -o -name '*.md' \) -print0 \
  | xargs -0 perl -pi -e 's/[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/[email]/g; s#https?://[^"\s)\x60]+#[url]#g; s/\Q$ENV{TENANT}\E/[tenant]/gi'
perl -pi -e 's/[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/[email]/g; s#https?://[^"\s)\x60]+#[url]#g; s/\Q$ENV{TENANT}\E/[tenant]/gi' ./m365-results/m365-smoke.json
```

PowerShell:

```powershell
$tenant = "<your-tenant-guid>"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$files = @(Get-ChildItem .agenteval\m365 -Recurse -File -Include *.jsonl, *.json, *.md) + @(Get-Item .\m365-results\m365-smoke.json)
foreach ($f in $files) {
  $t = [IO.File]::ReadAllText($f.FullName)
  $t = $t -replace '[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[email]'
  $t = $t -replace 'https?://[^"\s)\x60]+', '[url]'
  $t = $t -replace [regex]::Escape($tenant), '[tenant]'
  [IO.File]::WriteAllText($f.FullName, $t, $utf8)
}
```

The URL replacement also hides the Work IQ endpoint and the card's URLs; that is fine, the maintainers know them.
`request-id` values stay: they let Microsoft support find a call. File paths contain your user name; replace it the
same way if that matters to you. Then read the `output` fields of `m365-smoke.json` and replace by hand, with
`[name]`, any person's or document's name you cannot share; note it in the results file.

## 8. The results file

Copy [the results template](microsoft365-tester-RESULTS-TEMPLATE.md) to `./m365-results/RESULTS.md`, fill it in from
the summaries, `readiness.md` and `m365-smoke.json`, and include it in the zip. It asks for the per-stage results,
the census facts, the per-case outcomes of Step 3, the exit codes and anything that surprised you.

## 9. FAQ — failures you may see

**`AADSTS65001` during sign-in.** Admin consent for `WorkIQAgent.Ask` is missing. An administrator grants it on the
app's *API permissions* page. The connector cannot ask for consent itself.

**`AADSTS7000218` during sign-in.** The app is not a public client, so Entra asks for a client secret. Set *Allow
public client flows* to **Yes** on the app's *Authentication* page. The connector never uses a secret.

**`AADSTS700016` or `AADSTS90002`.** The app (`700016`) or the tenant (`90002`) was not found: a typo, the app
registered in another tenant, or a tenant in another cloud. A value left as `REPLACE-WITH-…` never gets this far: the
`config` stage refuses it and names the field. A value that is not a GUID gets a warning at `config`.

**HTTP 403 on discovery, protocol or the probe.** Three causes look the same from outside: no Microsoft 365 Copilot
licence on the test user, Work IQ not enabled in the tenant, or the permission's consent missing. The administrator
can tell which. The issue register keeps the `request-id` Microsoft support can look up.

**The agent answers nothing useful, or answers about an application window.** Word, Excel and PowerPoint agents are
bound to their application and do not work headlessly. Use the default agent (`"agentId": null`) or a declarative
agent that works in Copilot Chat.

**A turn takes minutes, then ends `Indeterminate`.** In Step 3 the connector sends a blocking request and waits up to
300 seconds for it, then polls the task (`GetTask`) for up to 2 more minutes, within a 10-minute limit per turn. In
the doctor the limit is the stage's `--timeout-seconds` (120 by default). When the limit is reached without a final
task state, the outcome is `Indeterminate`: the agent may still be working, and **nothing is resent**. Note the case
id and the elapsed time from the trace; for the doctor, run again with `--timeout-seconds 300`.

**`ProtocolVersionMismatch`, or JSON-RPC `-32009` (version not supported).** Work IQ answers in protocol version 0.3
unless the client says which version it speaks. This connector sends the header `A2A-Version: 1.0` on every request
and refuses to read a 0.3-shaped reply; it never falls back. If you see this, the service ignored the header or
changed: send the doctor's report folder and the trace back. Nothing on your side causes it.

**`UntrustedHost`.** The card advertised a URL on a host outside `trustedHosts`. The connector refused to send your
token there. Send the folder back; add the host to `trustedHosts` only if it is clearly Microsoft's.

**Citations look "unresolved".** The connector keeps the raw citation markers and the raw attribution objects exactly
as sent, and links a marker to a source only when the service gives an explicit key. "Unresolved" is expected, not a
failure. If an answer visibly cites something but citation sources are `NotProvided`, note it: that is a finding about
where the service puts attributions.

**The sign-in message appears on every run.** The token cache could not be written or read (Windows: under
`%LOCALAPPDATA%\AgentEval\msal_cache\`; macOS: the keychain; Linux: the secret service). Complete the sign-in each
time and note it.

**The probe's answer is not "READY".** A warning, not a failure: the agent did not follow the instruction
literally. The probe passes when the turn completes.

**The census says `native118 refused`.** Your tenant does not accept the experimental diagnostic profile. That is a
recorded fact; the default `public` profile, which Step 3 uses, does not depend on it.

**Exit `11` from `eval` with no failure.** A case produced no verdict: its turn did not complete or the judge did not
answer. Its `error` in `m365-smoke.json` says which. Not a judge-quality problem.

**Exit `1` from `eval`.** One or more cases failed their criteria. That is a legitimate result about the agent. Do
not rewrite the prompts to make them pass.

**A file "is being used by another process".** While a run is going, the trace and issue files are open for writing.
`Get-Content -Wait` (PowerShell) and `tail -f` can follow them; some editors cannot open them until the run ends.

**A proxy is required.** The tool uses .NET's default proxy: the `HTTPS_PROXY` and `NO_PROXY` environment variables
when they are set, else the system's proxy settings. A `Transport` failure in `discovery` usually means the path to
`workiq.svc.cloud.microsoft` is blocked; a sign-in that never gets a response points at `login.microsoftonline.com`.

## 10. Where to ask

Send the zip to the maintainer who gave you this guide, or open an issue on the repository **without** attaching the
files. Please do not post the files publicly before the maintainers have checked them for tenant data.
