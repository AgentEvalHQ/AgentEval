# Microsoft 365 connector: tester results template

Copy this file to `RESULTS.md` in the repository root, fill it in while you follow the
[demo README](../samples/AgentEval.Microsoft365Demo/README.md), and send it with the zip. Write values exactly as the
tool printed them. Leave a cell empty when a step did not run, and say why under *Free text*. Never paste a token, a
key, the device code or the content of `workiq.json`.

---

## 1. Environment

| Item | Value |
|---|---|
| Date and time of the runs (UTC) | |
| Commit (`git log -1 --oneline`) | |
| Operating system and shell | |
| Agent (`default`, or the agent id) | |
| A test tenant, and no production identity (yes / no) | |
| Proxy in use (yes / no) | |
| Demo judge: the `judge:` line the demo printed, or `none` (no key) | |

## 2. Prerequisites

| Prerequisite | Confirmed (yes / no / unsure) |
|---|---|
| Work IQ enabled in the tenant | |
| Test user in a usage-based billing plan (Copilot Credits) | |
| App registration: *Allow public client flows* = Yes | |
| Delegated `WorkIQAgent.Ask` with admin consent | |
| "Demo Q3 Planning" document in the test user's files | |

## 3. Demo run

Copy the values from the final table on screen or from `summary.md`.

Command (options only): ___ · Exit code: ___ · `Result:` line: ___ · Zip name: ___

| Stage | Result (PASS / FAIL / SKIPPED / NOT MEASURED) | Detail, as printed |
|---|---|---|
| readiness: config | | |
| readiness: auth | | |
| readiness: discovery | | |
| readiness: protocol | | |
| prompt | | |
| benchmark | | |

| Case | Verdict (PASS / FAIL / ERROR / NOT MEASURED) | Turn outcome (`Completed`, `InputRequired`, …) | Judge score | Reason, as printed |
|---|---|---|---|---|
| identity_disclosure | | | | |
| capability_summary | | | | |
| grounded_summary_with_citation | | | | |
| explicit_date_reasoning | | | | |
| out_of_scope_live_data | | | | |

`Issues:` line of the final table: ___ · `AEF run:` line of the final table: ___

| Redaction of the zip | Value |
|---|---|
| The README's commands run (e-mails, URLs, tenant id): yes / no | |
| Names replaced by hand: yes / no | |
| `aef/` left out because it could not be shared: yes / no | |

---

## CLI path: fill in only if the maintainers asked

### 4. Install

CLI version (`agenteval --version`): ___ · Build succeeded: yes / no

### 5. Step 1: readiness check

Exit code: ___ · Report folder (`doctor-…Z`): ___

| Stage | Result | Summary line, as printed | Code shown (AADSTS / HTTP / JSON-RPC) |
|---|---|---|---|
| config | | | |
| auth | | | |
| discovery | | | |
| protocol | | | |

From `readiness.md`, `discovery` evidence: interfaces advertised (binding, version, host): ___ · interface chosen: ___ ·
`capabilities.streaming` / `capabilities.pushNotifications`: ___

If a stage failed: its `meaning:` and `do:` lines, what you did, and whether it fixed it:

### 6. Step 1b: the token cache

Exit code: ___ · `auth` said `token acquired silently from the cache`: yes / no

### 7. Step 2: probe and census

Exit code: ___ · Report folder: ___

| Fact, from `readiness.md` | Value |
|---|---|
| `probe` result and summary line | |
| observed task states (for example `TASK_STATE_WORKING → TASK_STATE_COMPLETED`) | |
| `GetTask` polls (`0`: the send returned the final state) | |
| answer is the word READY (yes / no) | |
| attributions and citation markers, as printed | |
| `census` result and summary line | |
| diagnostic modes accepted (yes / no / unknown, with the reason) | |
| `CancelTask` on a completed task | |

### 8. Step 3: the smoke benchmark

Exit code: ___ · Passed / failed / not measured: ___ · Judge (endpoint host and model; no key): ___

`agenteval aef verify` result (intact / unsealed / invalid): ___ · `--aef-content on` used: yes / no (why not):

| Case id | Outcome (`Completed`, `InputRequired`, `Indeterminate`, …) | Verdict (pass / fail / not measured) | Score | Note |
|---|---|---|---|---|
| identity_disclosure | | | | |
| capability_summary | | | | |
| grounded_summary_with_citation | | | | |
| explicit_date_reasoning | | | | |
| out_of_scope_live_data | | | | |
| prompt_injection_resist | | | | |
| pii_restraint | | | | |
| action_restraint_read_only | | | | |

A `[m365] WARNING … no answer was selected` line appeared (case ids): ___ · A sign-in message appeared: yes / no

### 9. Step 4: OWASP smoke (optional)

Ran it: yes / no · Exit code: ___ · Gate verdict (PASS / WARN / FAIL / INCOMPLETE): ___ · Judge (provider and
deployment; no key): ___

| Attack | Verdict as printed |
|---|---|
| PromptInjection | |
| Jailbreak | |
| PIILeakage | |

### 10. What is in the CLI zip

| Item | Included (yes / no) | Redacted (yes / no) |
|---|---|---|
| `.agenteval/m365/` | | |
| `./m365-results/m365-smoke.json` | | |
| `./m365-results/aef/` (never edited) | | not editable |
| `./m365-raw/` (only if asked) | | |

---

## Free text

What surprised you, what took long, what you had to work around, and any message that did not tell you what to do:

