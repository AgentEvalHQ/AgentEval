# GitHub Copilot instructions: AgentEval Microsoft 365 demo

This workspace is `samples/AgentEval.Microsoft365Demo` of the AgentEval repository, opened on its own. The .NET 10
console app connects to a Microsoft 365 agent over Work IQ A2A 1.0. It runs a readiness check, one read-only prompt
and a small benchmark, and writes one run folder. The user is usually an external tester with a non-production tenant.
[README.md](../README.md) has the options, the run folder and the exit codes.

## Where things are

- This folder: the demo, its default cases (`Data/m365-demo-cases.json`) and `workiq.example.json`.
- `../AgentEval.Microsoft365Demo.Tests`: the offline tests.
- `../../src/AgentEval.Microsoft365`: the provider. The demo uses its public API only.
- `../datasets/`: the config template and `m365-smoke.json`, the 8-case set the defaults come from.
- `../../docs/microsoft365.md`: the provider reference and the troubleshooting catalogue.

## Rules

These rules apply to every agent, skill and prompt in this folder.

1. **Non-production only.** Before any live step, confirm with the user that `workiq.json` names a test tenant and a
   test user. The `WorkIQAgent.Ask` permission reads and writes that user's mail, files and calendar, and a prompt can
   make the agent propose a real action.
2. **Consent is the user's.** Add `--i-understand-live-side-effects` (or `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`)
   only after the user says yes to that run. Without it the demo stops after readiness with exit `11`: the safe
   default, not a failure.
3. **Ask before you spend.** A configured judge provider bills each call. Before a judged run, say how many agent turns
   and judge calls it makes and which model, then wait for a yes. `--no-judge` makes no judge call.
4. **Never resend a prompt.** An `Indeterminate` turn may have reached the service. Read its task id in `turns.jsonl`
   first.
5. **Never approve an action.** Write no code, prompt or case that answers a confirmation card or asks the agent to
   send, book, delete or share anything.
6. **Report only what was measured.** An `ERROR` row is never a graded zero, and `NOT MEASURED` is never a pass.
   `NotRequested`, `NotProvided` and `Provided` say what was asked for and what came back, not what the agent did.
   Say "experimental" whenever the `native118` profile is used. An offline test run is never a live result.
7. **No tenant data in git.** Never commit or paste `workiq.json`, a run folder, a zip, `raw/`, a token cache or a
   key. Repeat tenant ids, names or answers only as far as the task needs.
8. **Change the demo, not the provider.** If the provider looks wrong, give the maintainers the evidence: the stage,
   the code, the `request-id` and the trace lines.

## Code and tests

`.github/instructions/m365-demo.instructions.md` holds the code rules; it loads for every `.cs` file. Run the tests
with every provider variable unset, so nothing can reach a paid model:

```bash
for v in $(env | grep -iE '^(AZURE|AZUREOPENAI|OPENAI|FOUNDRY|MS_FOUNDRY|BITDEER|AI_INFERENCE|OPENROUTER|TYPESAFE|JEV_|COPILOTSTUDIO|ANTHROPIC|GEMINI|MISTRAL|OLLAMA|M365_|AGENTEVAL_M365|AGENTEVAL_A2A|APPLICATIONINSIGHTS|MAFARTCOMP)' | cut -d= -f1); do unset "$v"; done
dotnet test ../AgentEval.Microsoft365Demo.Tests
```

## Agent, skills and prompts

The `m365-demo` agent routes each request to a skill: `m365-demo-setup` (from zero to a first run),
`m365-demo-customize` (change what the demo tests) or `m365-demo-triage` (read a run folder). The prompts are
`/run-demo`, `/triage-run` and `/add-case`.
