---
name: m365-demo-triage
version: 0.2.0
description: Read a run folder of the AgentEval Microsoft 365 demo (summary.md, readiness.md, issues.jsonl, trace.jsonl, turns.jsonl, results.json) and say, per issue, which stage failed, what the category and codes (AADSTS, HTTP, JSON-RPC, turn outcome) mean, the likely cause and the next action. Use when a run exited 1, 3 or 11, a stage shows a cross, or someone asks what went wrong.
---

# m365-demo-triage

Read a run folder (`m365-demo-runs/<yyyyMMdd-HHmmss>Z/`, or the `--out` folder, or an unzipped zip) and answer three
questions for every problem: **where** (stage, case), **why** (category, code, cause) and **what next** (the action).
Quote codes, ids and messages exactly as written; never paraphrase a code. Never resend a prompt to "check", and never
suggest approving an action.

## Order of reading

1. **`summary.md`** — the header (tenant, app, agent, profile, versions), the stage table, the per-case table, the
   issues grouped by category with cause and action. Most answers are here.
2. **`readiness.md`** — the failed stage's row (summary · what it means · what to do) and its evidence: the AADSTS code
   and MSAL error code for `auth`; the HTTP status, `request-id` and every advertised interface for `discovery`; the
   HTTP status and JSON-RPC code for `protocol`.
3. **`issues.jsonl`** — one JSON object per issue. Fields: `at`, `stage`, `category`, `code`, `message`,
   `suggestedCause`, and when known `aadstsCode`, `httpStatus`, `rpcErrorCode`, `a2aState`, `requestId`, `taskId`,
   `contextId`, `caseId`, `turnId`, `runId`, `configSha256`, `adapterVersion`, `a2aVersion`. An absent fact is omitted, never
   written as `0` or `""`.
4. **`turns.jsonl`** (one line per turn: `caseId`, `outcome`, `transport`, `answerCompleteness`, `taskId`,
   `contextId`, `taskState`, `answerLength`, timings, `recoveryRequests`, the evidence availability, and `failure`
   with category, operation, certainty, HTTP status, JSON-RPC code, remote task state and `requestId`) and
   **`results.json`** / `results.md` (per case: the verdict and every check as `PASS`, `FAIL`, `NOT MEASURED` or
   `NOT RUN` with what was observed).
5. **`trace.jsonl`** — only to place an issue in time: the events just before it (same `caseId` / `taskId`), for
   example `auth.device_code_prompted`, `discovery.card_fetched`, `rpc.sent`, `rpc.error`, `task.state`,
   `turn.outcome`. Every issue is mirrored into the trace as `issue.<category>`.

`console.log` is the screen transcript plus every trace event at diagnostic detail (no colours; the device-code
message with its code masked); use it when the user asks "what did it print".

What the user saw on screen, by verbosity: at `normal` one line per issue,
`! issue <Category> <code> (<Stage>) HTTP … · JSON-RPC … · AADSTS… request-id=… case=… — <message>`; at `detailed`
also a block per issue, `! ISSUE <Category> <code>` followed by `stage`, `category` (with who can fix it), `code`,
`codes`, `request-id` (or "not received"), `task/context`, `case/turn`, `A2A state`, `message`, `cause`, `action` and
`run`. Each distinct issue (stage, code, message) is shown once; a repeat is only counted, and the `Repeated issues`
list before the final table gives `×N` (the files keep every occurrence, so count in `issues.jsonl`). A failed
readiness stage's own issue (`doctor.<stage>.failed`) stays one line at `detailed`: the stage's `FAILED` block has the
detail. A redirected screen shows `[PASS] [FAIL] [SKIP]` for `✓ ✗ –`. The demo splits the cause from the action when the
provider's suggested cause carries both (the readiness check's "… What to do: …", the AADSTS catalogue's
"CODE: cause → fix"); otherwise the action comes from the category (a `judge.*` issue names the judge's provider settings instead):

| Category | Who can fix it (the block's `category` line) |
|---|---|
| `Config` | your config file or the options |
| `Authentication` | the sign-in (you, or the app registration) |
| `Permission` | an Entra / Microsoft 365 administrator (consent, licence, Work IQ enabled) |
| `Network` | the network between this machine and Work IQ |
| `Protocol` | the connector maintainers (the reply is not the A2A 1.0 shape) |
| `Service` | the Work IQ service (note the `request-id`) |
| `Capture` | the raw capture settings (the run itself is unaffected) |
| `Budget` | the run's limits (`--max-turns`, `--timeout-seconds`) |
| `Unknown` | not classified — send the folder back |

Reading the JSONL while a run is still going: open with a reader that shares the file (`Get-Content -Wait`, `tail -f`).

## Exit code first

| Exit | Meaning | Look at |
|---|---|---|
| `0` | Every stage that ran passed and everything requested was measured. | Nothing to triage; report the per-case table. |
| `1` | A stage **failed** (a readiness stage, or the one prompt did not complete), or a case **failed** a check. | The `✗` stage in `summary.md`, or the `FAIL` rows. A case `FAIL` is a result about the agent, not a bug. |
| `11` | Nothing failed, but something was **not measured**. | Which: no consent (stopped after readiness), no judge or `--no-judge` (criteria `NOT MEASURED`), an `ERROR` row (a benchmark turn that did not complete), cases stopped by `--max-turns`, or no config file. |
| `3` | A runtime error, or a file could not be written. The folder is still written. | The error lines of `console.log` and the last events of `trace.jsonl`; report them. |
| `2` | A command-line mistake (an unknown option or value, a dataset that does not fit, a non-empty `--out`). Nothing was created or sent. | The message on screen; fix the command or the dataset. |

## Readiness stages

| Stage | Fails because | Typical evidence |
|---|---|---|
| `config` | the file is missing, not JSON, misses `tenantId`/`appClientId`, holds a `REPLACE-WITH-…` placeholder, or a field is invalid | the message names the field; nothing was sent |
| `auth` | the Entra sign-in failed or was not completed | `AADSTS…` code, MSAL error code, Entra HTTP status, correlation id |
| `discovery` | the card could not be fetched, is untrusted, or offers no A2A 1.0 JSON-RPC interface | HTTP status, `request-id`, interfaces and hosts |
| `protocol` | `GetTask` on a random id did not come back as JSON-RPC `-32001` | HTTP status, JSON-RPC code, `request-id` |

A stage that did not run is `–` / SKIPPED with its reason — never a pass, and never the cause of the failure.

## By issue category

### Config

| Code / message | Cause | Action |
|---|---|---|
| `config.invalid`, `Microsoft 365 config is missing required field(s): …` | `tenantId` or `appClientId` absent or blank | fill in the named field |
| `… field '…' still holds the template placeholder 'REPLACE-WITH-…'` | the template value was never replaced | put the value the message names (from the app registration's *Overview*) |
| `… field 'endpoint' …`, `'trustedHosts' …`, `'agentId' …`, `'timeZone' …`, `'diagnosticProfile' …` | one field has a bad value | fix that field exactly as the message says |
| a `config` warning: `tenantId '…' is neither a GUID nor a domain name` / `appClientId '…' is not a GUID` | a value that cannot be right | copy the ids from the app registration's *Overview*; the sign-in would fail with `AADSTS90002` / `AADSTS700016` |
| `http.404` (discovery) | the `agentId` (or the endpoint) is wrong, or the user cannot reach that agent | check `--agent-id` / `agentId`; try the default agent (`null`) |
| `discovery.untrusted_host` | the card advertised a URL on a host outside `trustedHosts`; **no token was sent there** | report it with the card evidence; add a host only if it is clearly Microsoft's |
| `auth.untrusted_destination` | a request was about to go to a host outside `trustedHosts`; refused before any token | the same as `discovery.untrusted_host`: the fix, if any, is in `trustedHosts` |

### Authentication (sign-in and token)

The `auth` stage maps an MSAL failure to its AADSTS code with the provider's catalogue
(`src/AgentEval.Microsoft365/Diagnostics/WorkIqAadstsCatalog.cs`); the issue's `suggestedCause` carries
`CODE: cause → fix`. A code outside the catalogue is kept as-is with a pointer to
<https://learn.microsoft.com/entra/identity-platform/reference-error-codes>.

| AADSTS | Cause | Action |
|---|---|---|
| `AADSTS65001` | no consent for the `WorkIQAgent.Ask` delegated permission | an administrator grants admin consent on the app's *API permissions* page |
| `AADSTS7000218` | public client flows are disabled, so Entra asks for a secret | *Authentication* → *Allow public client flows* = **Yes** |
| `AADSTS700016` | the app was not found in the tenant (`appClientId` wrong, or registered in another tenant) | check `appClientId` and `tenantId`; both must belong to the same tenant |
| `AADSTS90002` | the tenant was not found: a placeholder, a typo, a deleted tenant, or another cloud | check `tenantId` (Directory (tenant) ID, or the verified domain) |
| `AADSTS50059` | no tenant could be identified (a personal account, or a sign-in through the common endpoint) | set `tenantId`; sign in with a work account of that tenant |
| `AADSTS90019` | the sign-in request named no tenant | set `tenantId` |
| `AADSTS50020` | the signed-in account is not in that tenant | sign in as the test user of that tenant, or correct `tenantId` |
| `AADSTS50076` / `AADSTS50079` | MFA required / MFA enrolment required | complete it in the browser during the sign-in, then run again |
| `AADSTS50058` | a silent sign-in needs interaction (expected on a first run with a new cache) | run again and complete the device-code sign-in |
| `AADSTS50105` | the user is not assigned to the enterprise application | an administrator assigns the user, or turns off *Assignment required* |
| `AADSTS53003` | blocked by a conditional access policy | an administrator reviews the policy the Entra sign-in log names |
| `AADSTS530003` | the device must be managed or compliant | sign in from a compliant device, or ask for an exception |
| `AADSTS70011` | the scope is invalid | `scope` must be exactly `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` |
| `AADSTS65002` | a Microsoft first-party client id was used as `appClientId` | use your own app registration's client id |
| `AADSTS900144` | a required parameter is missing from the sign-in request | check `tenantId`, `appClientId`, `scope`; if complete, report the exact message |

MSAL errors without an AADSTS code: `code_expired` (the sign-in was not finished in time — run again and finish it),
`authentication_canceled`, `invalid_grant` (the cached sign-in is no longer valid — sign in again),
`unauthorized_client` (enable public client flows), `invalid_client` (check `appClientId` / `tenantId`),
`request_timeout` / `service_not_available` (Entra did not answer — check the path to `login.microsoftonline.com`).

Other Authentication codes:

| Code / message | Cause | Action |
|---|---|---|
| `auth.sign_in_timeout`, `sign-in not completed: the device code expired at … UTC before the sign-in finished` | nobody completed the device-code sign-in while the code was valid (about 15 minutes); nothing was sent | run again, open the page, enter the code, sign in as the test user |
| `auth.sign_in_timeout`, `sign-in not completed in time: no token within N s` | no sign-in was started and the token provider did not answer within the stage timeout | check the path to `login.microsoftonline.com`; raise `--timeout-seconds` |
| `auth.sign_in_not_completed` | a sign-in during a turn was not finished before the wait ended (the device code expired, or Ctrl+C); nothing was sent. A sign-in inside a turn waits for the code, not `--timeout-seconds` (`turn.sign_in_wait` in the trace) | run `--readiness-only` first (it caches the token), then run again |
| `judge.call_failed` with HTTP 401 / 403 | the **judge** model's provider refused its key or deployment — not the agent, not Work IQ, not the Microsoft 365 sign-in; the case's criteria are `NOT MEASURED`, its other checks were measured | check `AI_INFERENCE_PROVIDER` and that provider's key, endpoint and model / deployment; or run with `--no-judge` |
| `http.401`, `…unauthorized`, `HTTP 401` on discovery / protocol / a turn | the service rejected the token (audience, scope, expiry, other tenant) | check `scope`, `tenantId`, `appClientId`; run again to sign in afresh |

### Permission

| Code | Cause | Action |
|---|---|---|
| `http.403`, `…forbidden`, `auth.forbidden` | the user or app lacks `WorkIQAgent.Ask` or its admin consent, the user has no Microsoft 365 Copilot licence, or Work IQ is not enabled — the three look the same from outside | the administrator checks all three; keep the `request-id` for Microsoft support |

### Network

| Code | Cause | Action |
|---|---|---|
| `transport.dns` | the host name does not resolve | check the endpoint and the DNS / proxy path to `workiq.svc.cloud.microsoft` |
| `transport.connect`, `transport.tls`, `transport.proxy`, `transport.io`, `transport.error` | connection, TLS or proxy failure | check connectivity; the tool honours `HTTPS_PROXY` / `NO_PROXY` and the system proxy |
| `transport.timeout` | no answer within the timeout; **the request may have reached the service** | for a readiness stage raise `--timeout-seconds`; for a turn see `Indeterminate` below — never resend blindly |

### Protocol

| Code / JSON-RPC | Cause | Action |
|---|---|---|
| `protocol.version_mismatch`, `rpc.version_not_supported` (`-32009`), "the reply has the A2A 0.3 shape" | the service ignored the `A2A-Version: 1.0` header or rejected 1.0; the provider never falls back to 0.3 | report it with `trace.jsonl` and the `request-id`; nothing on the tester's side causes it |
| `rpc.task_not_found` (`-32001`) on a turn | the task is inaccessible, expired or purged (not proof it never existed) | report it; never resend |
| `-32001` on the readiness `protocol` stage | **expected** — the stage passes on it; it is kept out of `issues.jsonl` | nothing |
| HTTP 404 on the `protocol` stage instead of `-32001` | the selected interface URL is not the A2A 1.0 JSON-RPC endpoint | report it with the `request-id` |
| `protocol.malformed`, `reduce.*` | a reply could not be parsed or broke a one-of rule | run once more with `--capture-raw` only if the maintainers ask; report the folder |
| `rpc.method_not_found` (`-32601`), `rpc.invalid_params` (`-32602`), `rpc.invalid_request` (`-32600`), `rpc.parse_error` (`-32700`) | the service refused the envelope or its parameters | report it with the `request-id` |

### Service

| Code | Cause | Action |
|---|---|---|
| `http.429` (`retry-after=N` in the message) | throttled; reads back off, a send is never retried | wait, then run again later |
| `http.500` … `http.599`, `rpc.internal_error` (`-32603`), `remote.error` | a service-side error | report the `request-id`; do not resend a prompt |
| another `http.4xx` (400, 415, …) | the service refused the request; the raw capture would say why | report it with the `request-id` |
| `rpc.content_type_not_supported` (`-32005`) | the requested output modes were refused — usually `--profile native118` | use `--profile public`; record the refusal (a fact about the tenant) |
| `rpc.task_not_cancelable` (`-32002`) | the task was already terminal when cancelled | informational |
| `task.failed`, `task.rejected`, `task.canceled` | the service ended the task with that state (a partial artifact is never an answer) | the status message in the evidence says why; report it |
| `judge.call_failed` (no 401/403) | the **judge** model's provider failed or could not be reached (network, timeout, overload); the agent was measured, the criteria are `NOT MEASURED` | check the judge provider's endpoint and status; run again, or with `--no-judge` |
| `turn.input_required` (`InputRequired`) | the agent paused for a confirmation or more input; **nothing was approved** | unexpected for a read-only prompt: note the case id; never approve it; make the prompt read-only |
| `turn.auth_required` (`AuthRequired`) | the agent needs a connector sign-in the demo cannot complete | sign in to that connector in Microsoft 365 Copilot Chat, then run again |

### Budget

| Code | Cause | Action |
|---|---|---|
| `budget.exceeded` | the turn cap (`--max-turns`) or a time budget stopped the run before the next send | raise `--max-turns` only after checking the tenant's usage reporting |
| `budget.recovery_exhausted` | polling (`GetTask`) for a running task ran out of time; the outcome is `Indeterminate` | see `Indeterminate`; a later `budget.recovered_after_cancel` means the task did complete |
| `local.canceled` | the run was stopped locally (Ctrl+C); the remote task state is unknown | nothing was cancelled remotely; read the task id |

### Capture and Unknown

| Code | Cause | Action |
|---|---|---|
| `capture.truncated`, `capture.interrupted`, `capture.write_failed` | the raw capture hit its bounds, a stream ended early, or `raw/` could not be written | only matters with `--capture-raw`; check disk space |
| `turn.indeterminate`, `turn.not_completed` | see the outcome table below | — |
| `exception.<Type>`, `failure.unknown` | not classified | report the folder; the message and the trace say what is known |

## Turn outcomes (`turns.jsonl`, the `ERROR` rows)

| Outcome | Meaning | Action |
|---|---|---|
| `Completed` | the task finished | the checks ran; read the verdict and each check's detail in `results.md` |
| `Completed`, `answerCompleteness` `Ambiguous` | the task finished with several answer candidates and none selected (`FinishReason` `completed-no-answer`); *answer non-empty* and the criteria are `NOT MEASURED` | a finding about the agent's output shape; `evidence/<caseId>.json` keeps every candidate |
| `Completed`, `answerCompleteness` `None` / `Empty` | no answer artifact, or an empty answer; *answer non-empty* fails | a result about the agent: record it |
| `InputRequired` / `AuthRequired` | paused; nothing approved | see Service above |
| `Failed` / `Rejected` / `Canceled` | remote terminal state | see `task.*` above |
| `Indeterminate` | no final state observed (timeout, a dropped connection, or the recovery budget); **the prompt may have been accepted and was not resent** | read the task id; never resend automatically; if it repeats, report both run folders |

The same outcomes on the **prompt step** (case id `prompt`) make that stage `✗` (exit `1`) instead of an `ERROR` row,
and the benchmark is then skipped. A case that never ran (the turn cap, Ctrl+C) is `NOT MEASURED` with
"not run: …", and nothing was sent for it.

## Evidence channels (`evidence/<caseId>.json`)

`NotRequested` = not asked for (tool and retrieval channels under `--profile public`) · `NotProvided` = asked, the
tenant returned nothing · `Provided` = data came back (`Validation` says whether it parsed). Citation links are
usually `Unresolved`: expected, not a failure. Never read absence as "did not happen".

## What to tell the user

For each problem: the stage or case, the category and code exactly as written, the `request-id` when there is one,
the cause in one sentence, and the next action. If the cause is on the service's side (protocol, 5xx, an unexpected
JSON-RPC answer), say so plainly and ask them to send the zip back — nothing on their side causes it.
