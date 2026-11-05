# Microsoft 365 connector — tester results template

Copy this file to `./m365-results/RESULTS.md`, fill it in while you follow the
[tester guide](microsoft365-tester-guide.md), and include it in the zip you send back. Leave a cell empty when a step
did not run, and say why under "Free text". Write values exactly as the tool printed them; do not paraphrase codes.
Never paste a token, a key, the device code or the content of `workiq.json`.

---

## 1. Environment

| Item | Value |
|---|---|
| Date and time of the runs (UTC) | |
| Branch and commit (`git log -1 --oneline`) | |
| CLI version (`agenteval --version`) | |
| How the CLI was run (dotnet run function / local tool) | |
| Operating system and shell | |
| Agent (`default` or the agent id) and `agentName` | |
| Profile used for Step 3 (`public` unless you changed it) | |
| Tenant type (test tenant / other) — a production identity was **not** used: yes / no | |
| Network: proxy in use? yes / no | |
| Judge for Step 3 (endpoint host and model; no key) | |
| Judge for Step 4 (provider and deployment; no key) | |

## 2. Prerequisites confirmed

| Prerequisite | Confirmed (yes / no / unsure) | Note |
|---|---|---|
| Work IQ enabled in the tenant | | |
| Microsoft 365 Copilot licence on the test user | | |
| App registration: *Allow public client flows* = Yes | | |
| Delegated `WorkIQAgent.Ask` added, admin consent granted | | |
| "Demo Q3 Planning" document present in the test user's files | | |

## 3. Step 1 — readiness check (no prompt)

Exit code: ___ · Report folder name (`doctor-…Z`): ___

| Stage | Result (PASS / FAIL / SKIPPED) | Summary line, as printed | Code shown (AADSTS / HTTP / JSON-RPC), if any | Elapsed ms (`readiness.md`) |
|---|---|---|---|---|
| config | | | | |
| auth | | | | |
| discovery | | | | |
| protocol | | | | |

From `readiness.md`, the evidence of the `discovery` stage:

- Interfaces advertised by the card (binding, version, host): 
- Interface chosen: 
- `capabilities.streaming` / `capabilities.pushNotifications`: 
- Card SHA-256: 
- HTTP status and `request-id` of the card fetch: 

From the `auth` stage: acquired by device-code sign-in or silently: ___ · tenant matches the config: ___ · any warning: ___

If a stage failed: the `meaning:` and `do:` lines, what you did, and whether it fixed it:

## 4. Step 1b — second run (the token cache)

Exit code: ___ · The `auth` line said `token acquired silently from the cache`: yes / no · A sign-in message appeared again: yes / no

## 5. Step 2 — one real prompt (probe and census)

Exit code: ___ · Report folder name: ___

| Stage | Result | Summary line, as printed |
|---|---|---|
| probe | | |
| census | | |

From `readiness.md`, the probe's evidence:

| Fact | Value |
|---|---|
| outcome / transport | |
| task id received (yes / no; do not paste it if you consider it sensitive) | |
| observed states (for example `TASK_STATE_WORKING → TASK_STATE_COMPLETED`) | |
| recovery polls (GetTask) — `0` means the blocking send returned the final state directly | |
| answer completeness and length | |
| answer preview is the word READY: yes / no | |
| attributions (count or availability) | |
| citation markers (count or availability) | |
| elapsed ms | |
| warnings, if any | |

The census's evidence:

| Fact | Value |
|---|---|
| census turn (the probe turn, or a second turn under native118) | |
| diagnostic modes accepted (yes / no / unknown, with the reason printed) | |
| Developer Info artifact | |
| Retrieval Info artifact | |
| citation marker format | |
| CancelTask on a completed task | |
| streaming (card) / pushNotifications (card) | |

## 6. Step 3 — the smoke benchmark

Exit code: ___ · Summary as printed (passed / failed / not measured): ___ · Elapsed (approx.): ___

`agenteval aef verify <run folder>` result (intact / unsealed / invalid) and exit code: ___

Per case, from `m365-smoke.json` (`passed`, `score`, `error`). For a case without a verdict, the outcome is in its
`error` (`outcome=InputRequired`, `Indeterminate`, …). The citation columns are optional: the trace file has them per
case (search it for `normalize.citations`).

| Case id | Outcome (Completed / InputRequired / AuthRequired / Failed / Rejected / Canceled / Indeterminate) | Verdict (pass / fail / no verdict) | Score | Citation sources (optional) | Citation markers (optional) | Note |
|---|---|---|---|---|---|---|
| identity_disclosure | | | | | | |
| capability_summary | | | | | | |
| grounded_summary_with_citation | | | | | | |
| explicit_date_reasoning | | | | | | |
| out_of_scope_live_data | | | | | | |
| prompt_injection_resist | | | | | | |
| pii_restraint | | | | | | |
| action_restraint_read_only | | | | | | |

Did a sign-in message appear during Step 3? yes / no · Was `--aef-content on` used? yes / no (if no, why):

## 7. Step 4 — OWASP smoke (optional)

Ran it: yes / no · Exit code: ___ · Gate verdict (PASS / WARN / FAIL / INCOMPLETE): ___ · Elapsed (approx.): ___

| Attack | Verdict as printed | Note |
|---|---|---|
| PromptInjection | | |
| Jailbreak | | |
| PIILeakage | | |

INCOMPLETE? If yes, the reason printed and how far it got:

## 8. Redaction and what is in the zip

| Item | Included (yes / no) | Redaction applied (e-mails / URLs / tenant id / user name / names by hand / none) |
|---|---|---|
| `.agenteval/m365/` (doctor folders, traces, issues) | | |
| `./m365-results/m365-smoke.json` | | |
| `./m365-results/aef/` (never edited; leave it out if it cannot be shared) | | not editable |
| `./m365-raw/` (only if the maintainers asked) | | |

## 9. Free text

What surprised you, what took long, what you had to work around, and any message that did not tell you what to do:

