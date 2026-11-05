---
name: m365-demo-setup
version: 0.1.0
description: Take a tester from zero to a first run of the AgentEval Microsoft 365 demo - tenant prerequisites (Work IQ, Copilot licence, a public-client app registration with WorkIQAgent.Ask and admin consent, a non-prod test user), the workiq.json config, the first --readiness-only run, reading readiness.md, the full run with consent and --zip, and what to send back. Use when someone wants to connect the demo to a tenant or run it for the first time.
---

# m365-demo-setup

From nothing to a zipped run folder, in order. Steps 1–5 send **no prompt** to the agent. Step 6 acts as the
signed-in user and needs the user's explicit consent.

Where to run: the commands below run from this folder (`samples/AgentEval.Microsoft365Demo`, the workspace root, where
VS Code's terminal opens) as `dotnet run -- …`. The demo reads `./workiq.json` and writes `./m365-demo-runs/` in the
folder it runs from. From the repository root the same commands are
`dotnet run --project samples/AgentEval.Microsoft365Demo -- …` (the form the demo's own help and messages print, and the
README's quick start), with `workiq.json` in the repository root. Pick one place for `workiq.json`; if it is at the
repository root and you run from here, add `--config ../../workiq.json` (or set `M365_CONFIG_PATH`).

## Step 1 — Prerequisites (an administrator does most of this once)

| Item | Why | Where |
|---|---|---|
| A **non-production** tenant and a **test user** | The agent acts as that user on their real mailbox, files and calendar. | The organisation's test tenant. Never a production identity. |
| **Work IQ enabled** in the tenant | Without it the service answers HTTP 403. | Microsoft 365 admin center (an administrator). |
| A **Microsoft 365 Copilot licence** on the test user | Without it the service answers HTTP 403. | Microsoft 365 admin center → Users → the user → Licenses. |
| An **Entra app registration**, configured as a **public client** | The demo signs in with the device-code flow and holds no secret. Without this setting the sign-in fails with `AADSTS7000218`. | Entra admin center → App registrations → New registration (single tenant is fine) → *Authentication* → *Advanced settings* → **Allow public client flows = Yes** → Save. Add no secret and no certificate. |
| The **delegated permission `WorkIQAgent.Ask`** with **admin consent** | The only scope the service accepts: `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`. Without consent the sign-in fails with `AADSTS65001`. It covers reads **and writes** of the user's data. | The app's *API permissions* → *Add a permission* → *APIs my organization uses* → the Work IQ API (`api://workiq.svc.cloud.microsoft`) → *Delegated* → `WorkIQAgent.Ask` → *Add* → **Grant admin consent**. |
| The **tenant id** and the app's **client id** | For `workiq.json`. | The app registration's *Overview*: *Directory (tenant) ID*, *Application (client) ID*. |
| Optional: a short document titled **"Demo Q3 Planning"** in the test user's OneDrive or SharePoint | One default case asks the agent to summarise and cite it. Without it that case can fail, which is still a valid result. | Any short document with that title. |
| The **.NET 10 SDK** and **git** | To build and run the demo. | <https://dotnet.microsoft.com/download/dotnet/10.0>, <https://git-scm.com> |

Check the SDK with `dotnet --version` (it must start with `10.`).

## Step 2 — The config file

```bash
cp workiq.example.json workiq.json        # the example is a copy of ../datasets/m365-config.template.json
```

Open `workiq.json` and set the two required values; leave the rest unless you have a reason:

| Field | Put | Notes |
|---|---|---|
| `tenantId` | the *Directory (tenant) ID* | Required. A `REPLACE-WITH-…` value is refused before anything is sent. A value that is neither a GUID nor a domain name gets a readiness warning (it would fail at sign-in with `AADSTS90002`). |
| `appClientId` | the *Application (client) ID* | Required. Your own app registration, never a Microsoft-owned client id (`AADSTS65002`). A non-GUID value gets a warning (`AADSTS700016` would follow). |
| `agentId` | `null` | `null` = Work IQ's default agent (Microsoft 365 Copilot itself). For a declarative agent see the customise skill. |
| `agentName` | any label | Appears in the reports. |
| `timeZone` | your IANA zone (`Europe/Zurich`, `America/New_York`) | Sent with every prompt so date questions are deterministic. Remove the line if unsure. |
| `diagnosticProfile` | `PublicAnswerOnly` | `--profile native118` overrides it per run (EXPERIMENTAL). |
| `endpoint`, `scope`, `trustedHosts` | unchanged | A token is only ever sent to a host in `trustedHosts`. |

The file holds no secret, but it names the tenant: **never commit it** and never send it back (check `git status`
before any commit; the same goes for `m365-demo-runs/`). The demo reads `./workiq.json` by default; `--config <file>` or the `M365_CONFIG_PATH` variable point elsewhere.

Without a config file the demo prints what is missing and how to create it, contacts nothing, and exits `11`.

## Step 3 — First run: readiness only (no prompt)

```bash
dotnet run -- --readiness-only
```

The first `dotnet run` builds the project. Then the banner says what will run and against which agent, and the
readiness stages run:

| Stage | What it does | Network |
|---|---|---|
| `config` | Reads and validates `workiq.json`; records the endpoint, card URL, agent, tenant, app, scope, profile, trusted hosts, time zone and the config's SHA-256. | none |
| `auth` | Gets a token: silently from the OS-encrypted cache, else by device-code sign-in. | Entra sign-in |
| `discovery` | Fetches the agent card and picks the A2A 1.0 JSON-RPC interface on a trusted host. | Work IQ (the card) |
| `protocol` | `GetTask` for a random task id; the expected answer is JSON-RPC `-32001` TaskNotFound, which proves the endpoint speaks A2A 1.0 and accepts the token. | Work IQ (one call) |

**The sign-in.** On the first run the console prints a message such as *"To sign in, use a web browser to open the
page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate."* Open the page on any device,
enter the code and sign in **as the test user**. Once that message is shown the stage waits until the code expires
(about 15 minutes); `--timeout-seconds` does not cut a sign-in short. Later runs sign in silently from the cache.

Expected: every stage `✓`, exit `0`. Anything else → the triage skill.

## Step 4 — Read `readiness.md`

Open the newest `m365-demo-runs/<yyyyMMdd-HHmmss>Z/readiness.md` with the user. Point out:

- the stage table (stage · result · summary · what it means · what to do);
- `auth`: how the token was acquired (device-code or silent), the token's tenant and whether it matches the config;
- `discovery`: the card's **name and version** (this is the agent that answers), every advertised interface, the one
  chosen, `capabilities.streaming`;
- `protocol`: HTTP status, JSON-RPC code (`-32001` expected), `request-id`;
- any `warning:` line (for example a tenant id that is not a GUID).

`summary.md` in the same folder has the same stages plus the issues grouped by category with cause and action.

## Step 5 — Second readiness run (the token cache)

```bash
dotnet run -- --readiness-only
```

Exit `0`, no sign-in message, `auth` says silently from the cache. If the sign-in message appears again, the cache
could not be written or read (Windows: `%LOCALAPPDATA%\AgentEval\msal_cache\`; macOS: the keychain; Linux: the secret
service): sign in again and note it.

## Step 6 — The full run (consent; ask first)

Before this step, ask the user to confirm: the tenant and user in `workiq.json` are **non-production**, and they accept
that the agent acts as that user. Then:

```bash
dotnet run -- --i-understand-live-side-effects --zip
```

It runs readiness again, then the single prompt (default *"In one sentence, what can you help me with?"*), then the
benchmark: 5 read-only cases from `samples/datasets/m365-smoke.json`, one fresh conversation each. That is 6 agent
turns; `--max-turns` (default `12`) stops anything beyond the cap.

The judge: if a provider is configured in the environment (`AI_INFERENCE_PROVIDER` and its variables, see the
README's "The judge"), every case's criteria are graded by it and each call costs money on that provider — say so and
wait for a yes first, or add `--no-judge` to make no judge call at all. Without a judge (or with `--no-judge`) the
criteria are `NOT MEASURED` and the run exits `11` when nothing failed; the deterministic checks (turn completed,
answer not empty, response time recorded, evidence availability recorded) still run.

Read the final table: `PASS` / `FAIL` / `ERROR` (the turn did not complete; never graded) / `NOT MEASURED`.
Exit `0` all passed and measured · `1` a stage failed (readiness, or the one prompt did not complete) or a case failed
a check · `11` something not measured (no judge, a benchmark turn that did not complete, a case stopped by
`--max-turns`) · `3` runtime error or a file not written (the folder is still written) · `2` usage.

More on screen: add `--verbosity detailed` (every trace event and a DETAIL block per issue) or `diagnostic`. The run
folder has everything either way.

## Step 7 — Zip and send back

`--zip` in step 6 wrote `<run folder>.zip` next to the run folder and printed its path. Do not run step 6 again just to
get a zip (that sends every prompt again): zip the existing run folder yourself instead
(`Compress-Archive m365-demo-runs/<run folder> <run folder>.zip` in PowerShell, `zip -r` elsewhere). Before
sending, read the README's "What to send back": the folder may contain the agent's answers, the tenant id and request
ids; it never contains a token, an auth code or the `Authorization` header. Redact the unzipped folder first if
something cannot be shared, then zip it again. Send the zip, the exit code and a few lines on anything surprising (or
`../../docs/microsoft365-tester-RESULTS-TEMPLATE.md`).

## If a step fails

Use the [triage skill](../m365-demo-triage/SKILL.md): it maps every stage, category and code (AADSTS, HTTP, JSON-RPC,
turn outcome) to the cause and the next action.
