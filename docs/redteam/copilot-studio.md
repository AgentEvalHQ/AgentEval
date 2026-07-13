# Copilot Studio (`--sut copilot-studio`)

Red-teams a **live Microsoft Copilot Studio (MCS) agent** through the same `agenteval redteam` scanner used for
any OpenAI-compatible/Azure endpoint — with its own CLI options, a ship-blocking consent gate, and a fidelity
ceiling that is reported honestly rather than guessed at.

> **This is a scaffold, not a finished live integration.** The CLI surface, the config schema, every validation
> rule, and the credential-free test seam are real and enforced today. The one piece that is **not** wired yet is
> the live connector itself — `CopilotStudioAgentFactory.BuildLive` throws unconditionally (see
> [What's deferred](#whats-deferred--the-live-connector)). So right now, running `--sut copilot-studio` against a
> real config will pass every validation check and then fail with a clear "not wired yet" error — it cannot yet
> complete an actual scan against a live agent.

## What it is

`copilot-studio` is one of the two built-in `--sut` targets (`IRedTeamBuiltInTarget`; the other is
`gatekeeper-demo`). Selecting it swaps out the default endpoint/`--azure` path for a purpose-built target that:

- owns its own CLI flags (`--copilotstudio-config`, `--i-understand-live-side-effects`, `--max-credits`),
- validates a set of MCS-specific safety rules **before any network call**,
- builds the system-under-test as a MAF `AIAgent` wrapped in `MAFAgentAdapter` — the same adapter seam the
  Foundry integration and `AzureChatAgentFactory` use, per `CopilotStudioAgentFactory.FromAgent`,
- and — once the live connector ships — will drive a real MCS conversation at **text-only / `Verbal`** fidelity.

Source: `src/AgentEval.Cli/CopilotStudio/CopilotStudioConfig.cs`,
`src/AgentEval.Cli/CopilotStudio/CopilotStudioRedTeamTarget.cs`,
`src/AgentEval.Cli/CopilotStudio/CopilotStudioAgentFactory.cs`.

## Prerequisites

To author and validate a config today, you need:

- an AgentEval CLI build that includes this target (shipped from `0.16.0-beta`),
- the target agent's **Power Platform environment id** and **schema name** (the MCS bot identifier),
- the **Entra tenant id** the agent + app registration live in,
- an **Entra app-registration client id** (a public client — no secret) for the eventual device-code auth,
- a **non-production** copy of the agent — the consent flag below exists precisely because there is no sandbox.

None of this currently gets you a completed live scan — see the callout above. The only way to drive
`--sut copilot-studio` end-to-end today is the `sutOverride` test seam
(`CopilotStudioAgentFactory.FromAgent`), which is how the project's own test suite
(`tests/AgentEval.Tests/Cli/CopilotStudio/`) exercises the whole path credential-free and offline.

## Authoring the config JSON

`--copilotstudio-config <file.json>` points at a connection file. It carries **no secret** — the token is meant
to be acquired at run time (device-code → persisted MSAL cache) once the live connector ships; it is never
stored in this file.

The CLI help text for the flag says this plainly:

> JSON file with the Copilot Studio connection (environmentId, schemaName, tenantId, appClientId; optional
> cloud, agentName). No secret is stored here; the live connector (not implemented yet) will acquire the token
> at run time. Required by `--sut copilot-studio`.

| Field | Required | Notes |
|---|:--:|---|
| `environmentId` | yes | The Power Platform environment id hosting the agent. |
| `schemaName` | yes | The agent's schema name (the MCS bot identifier), e.g. `cr1a2_myAgent`. |
| `tenantId` | yes | The Entra tenant id the agent + app registration live in. |
| `appClientId` | yes | The Entra app-registration client id used for device-code auth (a public client, never a secret). |
| `cloud` | no | Power Platform cloud, e.g. `Prod` (default), `Gov`, `High`. |
| `agentName` | no | Display name for reports; falls back to `schemaName` if omitted or blank. |

Loading is case-insensitive on property names, but the convention (and every test fixture) uses camelCase:

```json
{
  "environmentId": "00000000-0000-0000-0000-000000000001",
  "schemaName": "cr1a2_myAgent",
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "appClientId": "22222222-2222-2222-2222-222222222222",
  "cloud": "Prod",
  "agentName": "My Support Agent (non-prod)"
}
```

If any of the four required fields is missing or blank, `CopilotStudioConfig.Load` throws before anything else
runs:

```text
Copilot Studio config is missing required field(s): schemaName, appClientId. Required: environmentId,
schemaName, tenantId, appClientId.
```

A missing file, unreadable file, or malformed JSON each produce their own clear, path-tagged
`InvalidOperationException` (not a raw `IOException`/`JsonException`) — see
[Troubleshooting](#troubleshooting) for the exact text.

## The consent flag and why it's required

`--i-understand-live-side-effects` is checked **first**, before the config file, before `--max-credits`,
before parallelism — before anything that could lead to a network call. Omit it and the command refuses
immediately:

```text
--sut copilot-studio drives a LIVE Copilot Studio agent whose connectors/flows can fire REAL production actions
and cannot be sandboxed. Re-run against a NON-PROD agent with --i-understand-live-side-effects to proceed
(nothing was sent).
```

The reasoning is structural, not a formality: an MCS agent's connectors and Power Automate flows can send email,
write to a CRM, or trigger any other real action it's wired to. AgentEval's red-team probes are specifically
designed to *try* to make an agent do things it shouldn't — there is no sandbox layer between the scan and those
real side effects. The flag is your explicit acknowledgment that you're pointing the scan at a **non-production**
agent, not a promise the tool can enforce on your behalf.

The CLI help text for the flag says this plainly:

> Required consent for `--sut copilot-studio`: acknowledge that scanning a LIVE agent can fire real
> connector/flow actions. Use a NON-PROD agent. Without this the command refuses before any network call.

## Running a scan

Once the live connector ships, a scan will look like this:

```bash
agenteval redteam --sut copilot-studio \
  --copilotstudio-config ./mcs-agent.json \
  --i-understand-live-side-effects \
  --intensity quick --format sarif -o redteam.sarif
```

What the command does with these flags **today**, in order:

1. Refuses immediately if `--i-understand-live-side-effects` is absent.
2. Refuses if `--copilotstudio-config` is absent.
3. Refuses if `--max-credits` is negative.
4. Refuses if `--parallelism` is greater than 1 — a live MCS session is stateful/non-reentrant, so the target
   hard-requires the default of 1.
5. Refuses if you pass `--judge` without `--judge-model`, or `--attacker` without `--attacker-model` — this
   target has no model of its own to fall back to (see [Honest reasoning](#how-it-fits-red-team-fidelity)).
6. Loads and validates the config JSON (fail fast on a bad file, before the scan starts).
7. Prints a one-line note to stderr (unless `--quiet`) if you also passed `--endpoint`, `--azure`, `--model`,
   `--deployment-name`, a non-default `--sut-tier`, `--system-prompt`, or `--system-prompt-canary` — all of
   these are ignored for this target.
8. Reaches the live build step and throws — see [What's deferred](#whats-deferred--the-live-connector).

Every step through 6 is exercised by the test suite with a fake agent (no config file needed for those tests to
be meaningful — only for you to reach step 6 yourself). Step 8 is the current, deliberate stop.

## What `--max-credits` does today

`--max-credits <n>` is **parsed and validated, but not yet enforced.** Concretely:

- The option defaults to `0` ("no cap") and must be `>= 0` — a negative value is rejected at validation time,
  before the config even loads.
- There is no code path today that spends or checks a Copilot Credit budget, because there is no live connector
  making calls yet.
- Once the live connector ships, hitting the cap is designed to stop the scan with **exit code 8**
  (`ExitCodes.BudgetExceeded`) — reserved now, not emitted by any current build. See the
  [exit-code table](../cli.md#exit-codes) in the CLI reference.

The CLI help text for the flag says this plainly:

> Cap the Copilot Credits a live `--sut copilot-studio` scan may spend (0 = no cap). Enforcement is deferred
> with the live connector; once it ships, hitting the cap will stop the scan with exit 8 (BudgetExceeded).
> Every turn burns credits, and a reasoning turn costs substantially more than a scripted one.

Don't rely on `--max-credits` as a real spend guard yet — because the live connector itself isn't wired, there
is currently no scan that could overspend in the first place.

## What's deferred — the live connector

`CopilotStudioAgentFactory.BuildLive` is the one function standing between this scaffold and a real scan. It
validates the config (so a bad config surfaces *your* error, not a deferral message), then throws:

```text
The live Copilot Studio connector is not wired yet, so --sut copilot-studio cannot run a live scan in this
release. The scaffold, safety gates, and credential-free CI path are complete; wiring the connector (the
Microsoft.Agents.CopilotStudio.Client package + device-code auth, verified against the current MAF release)
is still to come. For now, red-team an OpenAI-compatible or Azure endpoint with --endpoint / --azure, or try
the credential-free --sut gatekeeper-demo.
```

`CopilotStudioAgentFactory.FromAgent`, by contrast, **is** finished and tested — it wraps any already-constructed
MAF `AIAgent` as an `IEvaluableAgent` via `MAFAgentAdapter`. It's the seam the live path is designed to reuse once
the connector exists, and today it's exactly what `tests/AgentEval.Tests/Cli/CopilotStudio/*.cs` uses (a
`ChatClientAgent` over a fake `IChatClient`) to prove the CLI parsing → validation → gate → scan → reporting path
end-to-end without any credential or network call. There is no supported way, as a CLI user, to substitute your
own pre-built agent for `FromAgent` outside of tests — it isn't exposed as a flag.

Until the connector ships, use `--endpoint`/`--azure` against your own agent, or the credential-free
`--sut gatekeeper-demo` target, to exercise the rest of the red-team suite.

## How it fits red-team fidelity

AgentEval's red-team scoring is honest about *how much* evidence a verdict is based on
(`EvidenceFidelity`: `Verbal` / `IntentToAct` / `Behavioral` — see [Honesty & evidence fidelity](../redteam.md#honesty--evidence-fidelity)). Copilot Studio has a hard, structural ceiling here:

- **The conversation channel makes server-side tool calls invisible.** MCS agents run their own connectors and
  flows server-side; the red-team scanner only ever sees the text that comes back. There is no way for this
  target to observe whether a tool actually ran.
- **So every scan is `SutTier.TextOnly` / `EvidenceFidelity.Verbal`, by design, unconditionally.** A probe whose
  verdict would depend on tool-call evidence resolves to **Inconclusive** — never a fabricated **Behavioral**
  pass. Reaching `Behavioral` fidelity for MCS would require a deferred L2 telemetry-enrichment path (reading
  MCS-side execution telemetry), which does not exist yet.
- **This is why `--sut-tier`, `--system-prompt`, and `--system-prompt-canary` are ignored** for this target
  (step 7 above) — there is no tool-harness tier to select and no system prompt this target controls.
- **Evidence capture is off** (`IncludeEvidence => false`) — a live MCS response can carry real PII, so raw
  request/response text is never written into a report or CI log for this target, unlike the default
  `IncludeEvidence => true` for `gatekeeper-demo` and the endpoint/`--azure` path.
- **No gate-trace summary.** `WritePostScanSummary` is a deliberate no-op for this target — there's no local
  Gatekeeper trace for a live conversational agent; fidelity is reported per-verdict by the evaluators instead.
- **No model of its own.** A judge (`--judge`) or attacker (`--attacker`) needs an explicit `--judge-model` /
  `--attacker-model` when paired with this target, because there's no natural "same model as the SUT" to fall
  back to the way there is for an `--endpoint`/`--model` pair.
- **`--parallelism` is hard-floored to 1.** A live MCS session is stateful and non-reentrant, so concurrent
  probes would race the same conversation.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `--sut copilot-studio drives a LIVE Copilot Studio agent…` | `--i-understand-live-side-effects` was omitted. | Add the flag — but only once you've confirmed the config points at a **non-production** agent. |
| `--sut copilot-studio requires --copilotstudio-config <file.json>…` | `--copilotstudio-config` was omitted. | Pass the path to your connection JSON. |
| `Copilot Studio config is missing required field(s): …` | The JSON is missing one of `environmentId` / `schemaName` / `tenantId` / `appClientId`. | Add the listed field(s) — the error names exactly which ones. |
| `Copilot Studio config file not found: …` | The path passed to `--copilotstudio-config` doesn't exist. | Check the path (relative paths resolve from the current working directory). |
| `Copilot Studio config file could not be read (…)` | The file exists but couldn't be opened (locked, permissions). | Close whatever else has it open, or fix file permissions. |
| `Invalid Copilot Studio config JSON (…)` | The file isn't valid JSON. | Fix the syntax — the wrapped message includes the underlying `JsonException` detail. |
| `--sut copilot-studio runs at --parallelism 1…` | `--parallelism` was set above 1. | Drop `--parallelism` (default is already 1) or set it explicitly to 1. |
| `--sut copilot-studio has no model of its own; pass --judge-model <name>…` | `--judge <url>` was passed without `--judge-model`. | Add `--judge-model <name>`. Same fix, with `--attacker-model`, if the message names `--attacker`. |
| `The live Copilot Studio connector is not wired yet, so --sut copilot-studio cannot run a live scan in this release.` | Expected, for every invocation right now — this is not a bug in your config. | Nothing to fix on your end yet. Use `--endpoint`/`--azure` against your own agent, or `--sut gatekeeper-demo`, in the meantime. |
| `Unknown --sut value: '…'. Valid: gatekeeper-demo, copilot-studio.` | A typo in `--sut` (it's case-insensitive, but must otherwise match). | Use `copilot-studio` (or `gatekeeper-demo`). |

## See also

- [Red Team Security](../redteam.md) — the full scanner: attacks, evidence fidelity, judge modes, CI baseline gate.
- [CLI Reference — Exit codes](../cli.md#exit-codes) — the full exit-code table, including the reserved `8`
  (`BudgetExceeded`) this target will use once `--max-credits` enforcement ships.
- [Attack the gate](../gatekeeper/attack-the-gate.md) — the credential-free `--sut gatekeeper-demo` closed loop,
  useful today while the Copilot Studio connector is deferred.
