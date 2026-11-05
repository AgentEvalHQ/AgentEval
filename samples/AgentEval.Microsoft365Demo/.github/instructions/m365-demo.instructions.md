---
applyTo: '**/*.cs'
description: Code rules for the AgentEval Microsoft 365 demo and its tests - conventions, the honesty rules every change keeps, and what to update alongside a change
---

# Microsoft 365 demo — code rules

Applies to every `.cs` file of `samples/AgentEval.Microsoft365Demo` and `samples/AgentEval.Microsoft365Demo.Tests`.

## Conventions

- SPDX header first:

  ```csharp
  // SPDX-License-Identifier: MIT
  // Copyright (c) 2026 AgentEval Contributors
  // Licensed under the MIT License.
  ```

- File-scoped namespaces (`AgentEval.Microsoft365Demo`, tests `AgentEval.Microsoft365Demo.Tests`), nullable enabled,
  XML docs on every public member, `sealed` unless designed for inheritance, `internal` unless a test or host needs it
  (the tests see internals through `InternalsVisibleTo`).
- Async all the way; every async method takes a `CancellationToken` and passes it on.
- Read every type you call before calling it: the provider (`../../src/AgentEval.Microsoft365`) changes; never guess a
  member name. Use its **public** API only (`Microsoft365Config`, `Microsoft365AgentFactory.BuildLive`, `WorkIqOptions`,
  `Microsoft365ReadinessCheck`, `ReadinessReportRenderer`, `Microsoft365EvidenceJson`, `reply.Evidence()`).

## Honesty rules every change keeps

- **No mock fallback.** Without a config file: print exactly what is missing and how to create it (copy
  `samples/datasets/m365-config.template.json`), contact nothing, exit `11`. A stand-in agent exists only in the
  tests, through `DemoHostOverrides`.
- **Consent gates everything past readiness.** No prompt is sent without `--i-understand-live-side-effects` or
  `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`; without it the run stops after readiness with exit `11`.
- **A non-completed turn is never an answer.** `WorkIqTurnNotCompletedException` becomes an `ERROR` row carrying the
  outcome, task id, context id and the issue — never a `FAIL` score, never partial text. A completed turn with
  `FinishReason` `completed-no-answer` has an empty text that is not the agent's answer: flag it.
- **Nothing unmeasured becomes a value.** Judge criteria without a judge client are `NOT MEASURED`; a check whose
  input is absent is not measured, never passed. Exit `11` when nothing failed but something was not measured.
- **Never resend, never approve.** No retry of a send; no code that answers a confirmation card.
- **No secret anywhere.** Tokens, auth codes and the `Authorization` header never reach the screen, a file of the run
  folder, or an exception message. The device-code sign-in message is shown to the user; never copy its user code into
  a file. Route new output through the existing writers, which already redact.
- **The run folder is always written** (also on a failure, also on exit `3`), whatever the screen verbosity:
  `console.log`, `trace.jsonl`, `issues.jsonl`, `readiness.md/json`, `turns.jsonl`, `evidence/<caseId>.json`,
  `results.md/json`, `summary.md`; `raw/` with `--capture-raw`; `<folder>.zip` with `--zip`. No colours in
  `console.log`.
- **Evidence by availability.** Read a channel's `Availability` before its `Items`; `NotRequested` / `NotProvided` are
  never turned into "nothing happened".

## Exit codes (do not change their meaning)

`0` every stage that ran passed and everything requested was measured · `1` a stage or a case failed · `2` usage ·
`3` unhandled runtime error (folder still written) · `11` nothing failed but something was not measured.

## Alongside a change

- A new or changed option, run-folder file, verdict or exit code → `README.md` and the skills in `.github/skills/`.
- A new check or behaviour → an offline test in `../AgentEval.Microsoft365Demo.Tests` named `Method_State_Expected`
  that fails without the change; tests never reach a tenant or a paid judge (fake `HttpMessageHandler`, fake
  `IWorkIqTokenProvider`, no judge client unless a fake one).
- Run the tests with every provider environment variable unset (see `.github/copilot-instructions.md`).
