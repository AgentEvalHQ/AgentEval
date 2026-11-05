# GitHub Copilot instructions — AgentEval Microsoft 365 demo

This workspace is `samples/AgentEval.Microsoft365Demo` of the AgentEval repository, opened on its own. It is a .NET 10
console app that connects to a Microsoft 365 agent over **Work IQ A2A 1.0**, runs the provider's readiness check,
sends one read-only prompt, runs a small AgentEval benchmark and writes one run folder with everything it saw. The
person using it is usually an external tester with a **non-production** Microsoft 365 tenant and no knowledge of the
repository. Read [README.md](../README.md) for the options, the run folder and the exit codes.

## Where things are

- This folder: the demo (`Program.cs`, `DemoHost` and the rest of the `.cs` files, the embedded default dataset, the
  `.csproj`). The repository root is two levels up (`../..`).
- `../AgentEval.Microsoft365Demo.Tests`: the offline tests (xUnit), through the real provider with a fake HTTP handler
  and a fake sign-in.
- `../../src/AgentEval.Microsoft365`: the provider (config, sign-in, A2A client, evidence, diagnostics, readiness
  check). The demo uses its **public** API only.
- `../datasets/m365-config.template.json` (the config template) and `../datasets/m365-smoke.json` (the 8-case
  dataset the default 5 cases come from).
- `../../docs/microsoft365.md` (the provider) and `../../docs/microsoft365-tester-guide.md` (the CLI path for the same
  validation).

## Rules that are never relaxed

1. **Non-production only, consent first.** Every step after the readiness check acts as the signed-in user, whose
   delegated `WorkIQAgent.Ask` permission covers reads **and writes** of their Microsoft 365 data. Never pass
   `--i-understand-live-side-effects` (or set `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`) on the user's behalf: ask,
   and only after they confirm the config names a non-production tenant and test user.
2. **Never resend a prompt.** A turn that ended `Indeterminate` may have been accepted by the service. Read its task
   id in `turns.jsonl` / `trace.jsonl` first; never re-run the same prompt automatically.
3. **Never approve an action.** `InputRequired` and `AuthRequired` are reported as non-completed turns (verdict
   `ERROR`). Do not write code that answers a confirmation card.
4. **Evidence honesty.** A non-completed turn is never an answer and never a graded zero: it is an `ERROR` row with
   its outcome. Nothing unmeasured becomes a value: criteria without a judge are `NOT MEASURED`, never passed.
   `NotRequested` (not asked for), `NotProvided` (asked, nothing returned) and `Provided` mean exactly that; absence
   of evidence is not proof that nothing happened. The demo never falls back to a mock target.
5. **No secret, no tenant data in git.** Never commit `workiq.json`, a run folder (`m365-demo-runs/`), a zip, or
   anything under `raw/`. Tokens, auth codes and the `Authorization` header never reach the screen, a file or an
   exception message: keep it that way in any change.
6. **Paid judge calls need a yes.** A run with a judge provider configured calls a paid model. Before such a run say
   what runs, how many agent turns and judge calls, and which model, and wait for the user's answer.

## Code conventions (match the repository)

- SPDX header on every `.cs` file:

  ```csharp
  // SPDX-License-Identifier: MIT
  // Copyright (c) 2026 AgentEval Contributors
  // Licensed under the MIT License.
  ```

- File-scoped namespaces (`AgentEval.Microsoft365Demo`), nullable enabled, XML docs on public members, `sealed` by
  default, async all the way with a `CancellationToken`.
- Tests: xUnit, `Method_State_Expected` names, offline only (the in-test HTTP handler and token provider seams of
  `DemoHostOverrides`); never a live tenant and never a real judge in a test.
- Do not add a parallel harness or a mock fallback: the benchmark runs through AgentEval's `MAFEvaluationHarness`, and
  a missing config or consent is an honest stop with exit `11`.
- When an option, a file of the run folder or an exit code changes, update `README.md` and the skills under
  `.github/skills/` in the same change.

## Build and test

```bash
dotnet build                                   # from this folder
dotnet run -- --help                           # every option
dotnet run -- --readiness-only                 # needs ./workiq.json; sends no prompt
```

Run the tests with every provider variable unset, so nothing can reach a paid model:

```bash
for v in $(env | grep -iE '^(AZURE|AZUREOPENAI|OPENAI|FOUNDRY|MS_FOUNDRY|BITDEER|AI_INFERENCE|OPENROUTER|TYPESAFE|JEV_|COPILOTSTUDIO|ANTHROPIC|GEMINI|MISTRAL|OLLAMA|M365_|AGENTEVAL_M365|AGENTEVAL_A2A|APPLICATIONINSIGHTS|MAFARTCOMP)' | cut -d= -f1); do unset "$v"; done
dotnet test ../AgentEval.Microsoft365Demo.Tests
```

## The agent, skills and prompts in this folder

- Agent `m365-demo` (`.github/agents/m365-demo.agent.md`): the rules above plus routing to the skills.
- Skills: `.github/skills/m365-demo-setup/SKILL.md` (from zero to the first run), `.github/skills/m365-demo-customize/SKILL.md`
  (another agent, prompts, cases, checks, verbosity, profile), `.github/skills/m365-demo-triage/SKILL.md` (read a run
  folder: what failed, why, what next).
- Prompts: `/run-demo`, `/triage-run`, `/add-case` (`.github/prompts/`).
- `.github/instructions/m365-demo.instructions.md` applies to every `.cs` file.
