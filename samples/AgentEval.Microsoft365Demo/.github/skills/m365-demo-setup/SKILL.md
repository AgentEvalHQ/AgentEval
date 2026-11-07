---
name: m365-demo-setup
version: 0.3.0
description: Take a tester from nothing to a first zipped run of the AgentEval Microsoft 365 demo. Covers the tenant prerequisites (Work IQ enabled, the user in a usage-based billing plan, a public-client app registration with WorkIQAgent.Ask and admin consent; steps in docs/microsoft365-tenant-setup.md), workiq.json, the first --readiness-only run, reading readiness.md, the full run with consent, and --zip. Use when someone wants to connect the demo to a tenant, run it for the first time, or send a run back. To read a failed run use m365-demo-triage; to change what the demo tests use m365-demo-customize.
---

# m365-demo-setup

Follow the steps in order. Steps 1 to 4 send no prompt to the agent. Step 5 acts as the signed-in user, so the rules
in `.github/copilot-instructions.md` matter most there.

Run every command from this folder, as `dotnet run -- …`. The README's quick start puts `workiq.json` at the
repository root, so add `--config ../../workiq.json` to every command. The run folders then land in this folder's
`m365-demo-runs/`. The demo's own messages print the repository-root form,
`dotnet run --project samples/AgentEval.Microsoft365Demo -- …`.

## Step 1: Prerequisites

[Connect to a Microsoft 365 tenant](../../../../../docs/microsoft365-tenant-setup.md) (from this folder: `../../docs/microsoft365-tenant-setup.md`) gives
each step, who does it, and what a failure means. Copilot may ask before it reads a file outside the workspace. Check
each item with the user, in this order:

1. A Global Administrator enabled Work IQ in the tenant (guide step 1).
2. The test user is in a usage-based billing plan (step 2). A new assignment can take up to 30 minutes to apply.
3. The app registration is a public client: *Allow public client flows* = Yes (steps 3 and 4).
4. It has the delegated permission `WorkIQAgent.Ask`, with admin consent granted (step 5).
5. Conditional Access allows device-code sign-in for the test user (step 6). The demo signs in with a device code only.

The tenant and the test user must be non-production. Check that `dotnet --version` starts with `10.`.

The document titled "Demo Q3 Planning" matters. Without it the grounding case can still PASS on a plain "not found",
and then it measures nothing about grounding.

## Step 2: The config file

Use `../../workiq.json` if it exists. Otherwise create it there from the template:

```bash
cp ../datasets/m365-config.template.json ../../workiq.json
```

Set `tenantId` to the *Directory (tenant) ID* and `appClientId` to the *Application (client) ID*. Set `timeZone` to
the tester's IANA zone, such as `Europe/Zurich`, and leave the rest. A value still reading `REPLACE-WITH-…` is refused
before anything is sent. `workiq.json` holds no secret but names the tenant: check `git status` before any commit.

## Step 3: Readiness only

```bash
dotnet run -- --config ../../workiq.json --readiness-only
```

The first run builds the project. Then the stages `config`, `auth` (the sign-in), `discovery` (the agent card) and
`protocol` (a handshake) run, and no prompt reaches the agent. `readiness.md` says what each stage checked.

The first run prints a device-code message. Open <https://microsoft.com/devicelogin>, enter the code and sign in as
the test user. The stage waits until the code expires, about 15 minutes, whatever `--timeout-seconds` says.

Expected: every stage passes and the exit is `0`. A first connection usually fails on the tenant setup.
`readiness.md` names the code, and the guide's step fixes it ([If a step fails](../../../../../docs/microsoft365-tenant-setup.md#if-a-step-fails)):

| You see | Cause | Guide step |
|---|---|---|
| `AADSTS7000218` or `AADSTS7000215` | Public client flows are off. | 4 |
| `AADSTS65001` | Admin consent for `WorkIQAgent.Ask` is missing. | 5 |
| `AADSTS650052` | Work IQ is not enabled in the tenant. | 1, then 5 |
| `AADSTS53003` | Conditional Access blocked the device-code sign-in. | 6 |
| `AADSTS90002` or `AADSTS700016` | `tenantId` or `appClientId` is wrong. | 7 |
| HTTP `401` | The token is not for Work IQ: check `scope` in `workiq.json`. | 7 |
| HTTP `403` that names the required scopes | Admin consent is missing. | 5 |
| HTTP `403` with no scope named | The user is not in the billing plan, or was added less than 30 minutes ago. | 2 |

Fix that step, then run `--readiness-only` again. For anything else, use the m365-demo-triage skill.

## Step 4: Read readiness.md together

Open the newest `m365-demo-runs/<yyyyMMdd-HHmmss>Z/readiness.md` with the user. Point out the stage table, how `auth`
got the token, the card's name and version under `discovery` (the agent that answers), the HTTP status and
`request-id` under `protocol`, and any **Warning**, for example a tenant id that is neither a GUID nor a domain name.

Run `--readiness-only` a second time. It should pass with no sign-in message. If the message appears again, the token
cache could not be written or read: note it for the maintainers.

## Step 5: The full run

Ask the user to confirm that the tenant and the user are non-production, and that the agent may act as that user.
Then:

```bash
dotnet run -- --config ../../workiq.json --i-understand-live-side-effects --zip
```

The run repeats readiness and sends one prompt. Then it runs 5 read-only cases, one fresh conversation each: 6 agent
turns in all, capped by `--max-turns` (default 12).

The judge: when a judge provider is configured, each case with criteria makes one judge call, plus one retry when a
verdict cannot be parsed. All 5 default cases have criteria. `AI_INFERENCE_PROVIDER` picks the provider; without it,
an exported key such as `OPENAI_API_KEY` is enough (the README's judge section lists them). State that cost and wait
for a yes, or add `--no-judge`. Without a judge the criteria are `NOT MEASURED` and the run exits `11` when nothing
failed. The other checks run either way.

The README's verdict table says what each case verdict means, and the triage skill explains each exit code.
`--verbosity detailed` shows more on screen; the run folder always holds everything.

## Step 6: Send it back

`--zip` wrote `<run folder>.zip` next to the run folder and printed its path. Do not repeat step 5 to get a zip, as
that sends every prompt again. Zip the existing folder instead (`Compress-Archive` in PowerShell, `zip -r` elsewhere).

Before sending, read the README's "What to send back". The folder can hold the agent's answers, the tenant id and
request ids, but never a token, an auth code or the `Authorization` header. If something cannot be shared, redact the
unzipped folder as the README shows, then zip it again. Send the zip, the exit code and a few lines on anything
surprising, or fill in `../../docs/microsoft365-tester-RESULTS-TEMPLATE.md`.
