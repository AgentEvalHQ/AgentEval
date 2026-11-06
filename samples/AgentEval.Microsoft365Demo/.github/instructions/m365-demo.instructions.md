---
applyTo: '**/*.cs'
description: Code rules for the AgentEval Microsoft 365 demo and its tests - conventions, the behaviours every change keeps, and what to update alongside a change
---

# Microsoft 365 demo: code rules

For every `.cs` file of the demo and its tests. The rules in `.github/copilot-instructions.md` apply too.

## Conventions

- The SPDX header comes first:

  ```csharp
  // SPDX-License-Identifier: MIT
  // Copyright (c) 2026 AgentEval Contributors
  // Licensed under the MIT License.
  ```

- File-scoped namespaces, nullable enabled, XML docs on every public member. Types are `sealed` and `internal` unless
  a host or test needs more; the tests see internals through `InternalsVisibleTo`.
- Async all the way, with a `CancellationToken` passed on.
- Use the provider's public API only, and read each type before you call it. The project already suppresses
  `AGENTEVAL_M365_PREVIEW001`, the provider's experimental diagnostic.

## Behaviours every change keeps

- **No mock fallback.** A stand-in agent exists only in the tests, through `DemoHostOverrides`.
- **A non-completed turn is never an answer.** `WorkIqTurnNotCompletedException` becomes an `ERROR` row with the
  outcome, the ids and the issue. It is never a `FAIL` and never partial text.
- **Nothing unmeasured becomes a value.** A check whose input is absent is `NotMeasured`. So is a check that reads the
  answer when `Microsoft365Evidence.IsAnswerMeasured(turn)` is false. Read a channel's `Availability` before its
  `Items`.
- **No secret anywhere.** Tokens, auth codes and the `Authorization` header never reach the screen, a file or an
  exception message. Route new output through the existing writers; they redact.
- **The run folder is always written**, on a failure too, at every verbosity. `console.log` has no colours.
- **Exit codes keep their meaning.** `ExitPolicy.Decide` is the one place that decides them.

## Alongside a change

- A new or changed option, run-folder file, verdict or exit code: update `README.md` and `.github/skills/` in the same
  change.
- A new check or behaviour: an offline test in `../AgentEval.Microsoft365Demo.Tests`, named `Method_State_Expected`,
  that fails without the change. Tests use a fake `HttpMessageHandler` and a fake `IWorkIqTokenProvider`, and never a
  real judge.
