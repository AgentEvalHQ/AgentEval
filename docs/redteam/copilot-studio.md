# Copilot Studio (`--sut copilot-studio`)

Red-teams a **live Microsoft Copilot Studio (MCS) agent** through the same `agenteval redteam` scanner used for
any OpenAI-compatible/Azure endpoint — with its own CLI options, a ship-blocking consent gate, and a fidelity
ceiling that is reported honestly rather than guessed at.

> **The live connector is wired but not independently live-verified.** The CLI surface, the config schema, every
> validation rule, the credential-free test seam, and (as of this release) `CopilotStudioAgentFactory.BuildLive`
> itself all construct successfully and are covered by tests. What has **not** been exercised against a real
> Copilot Studio agent is the actual network round trip — the MSAL device-code sign-in, the persisted token cache,
> and whether a real agent's activity stream matches what the connector shim assumes (see
> [What's verified vs. what still needs a live check](#whats-verified-vs-what-still-needs-a-live-check)). If you
> run this against a real config and hit an unexpected error mid-scan, that gap is why — please file it.

## What it is

`copilot-studio` is one of the two built-in `--sut` targets (`IRedTeamBuiltInTarget`; the other is
`gatekeeper-demo`). Selecting it swaps out the default endpoint/`--azure` path for a purpose-built target that:

- owns its own CLI flags (`--copilotstudio-config`, `--i-understand-live-side-effects`, `--max-credits`),
- validates a set of MCS-specific safety rules **before any network call**,
- builds the system-under-test as a MAF `AIAgent` wrapped in `MAFAgentAdapter` — the same adapter seam the
  Foundry integration and `AzureChatAgentFactory` use, per `CopilotStudioAgentFactory.FromAgent`,
- and drives a real MCS conversation at **text-only / `Verbal`** fidelity via `CopilotStudioAgentFactory.BuildLive`
  (a real `CopilotClient` bridged into an `IChatClient` by `CopilotStudioChatClient`) — see
  [What's verified vs. what still needs a live check](#whats-verified-vs-what-still-needs-a-live-check).

Source: `src/AgentEval.MAF.CopilotStudio/CopilotStudioConfig.cs`,
`src/AgentEval.Cli/CopilotStudio/CopilotStudioRedTeamTarget.cs`,
`src/AgentEval.MAF.CopilotStudio/CopilotStudioAgentFactory.cs`.

## Using it directly in code (no CLI)

The connector itself — `CopilotStudioConfig`, `CopilotStudioAgentFactory`, `CopilotStudioChatClient`,
`CopilotStudioTokenProvider` — lives in its own package, **`AgentEval.MAF.CopilotStudio`**, separate from the
main `AgentEval` package so the Copilot Studio SDK + MSAL dependency tree is never forced on consumers who
don't use it:

```bash
dotnet add package AgentEval.MAF.CopilotStudio --prerelease
```

> **Not yet published to NuGet.org** — the package builds/packs correctly, but `release.yml` doesn't push it
> yet. Reference the project directly or build from source until that lands.

```csharp
using AgentEval.Core;              // IEvaluableAgent
using AgentEval.MAF.CopilotStudio;

var config = new CopilotStudioConfig
{
    EnvironmentId = "Default-xxxxxxxx",
    SchemaName    = "cr1a2_myAgent",
    TenantId      = "<tenant-guid>",
    AppClientId   = "<entra-app-client-id>",
};

// iUnderstandLiveSideEffects is required, no default — this agent's connectors/flows can fire REAL
// production actions and cannot be sandboxed. Pass true only once you've confirmed a NON-PROD target.
IEvaluableAgent agent = CopilotStudioAgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true);
var result = await agent.InvokeAsync("What's the status of order #12345?");
```

This is the exact same factory the CLI's `--sut copilot-studio` target calls — no functional difference,
just reachable without going through `agenteval` at all. See the package's own README for the
`ICopilotStudioConversationClient` seam if you want to unit-test your own code against a fake conversation
client instead of a live tenant.

## Prerequisites

To author and validate a config, you need:

- an AgentEval CLI build that includes the live connector (from this release; `0.16.0-beta` had the credential-free
  scaffold only),
- the target agent's **Power Platform environment id** and **schema name** (the MCS bot identifier),
- the **Entra tenant id** the agent + app registration live in,
- an **Entra app-registration client id** (a public client — no secret) for device-code auth,
- a **non-production** copy of the agent — the consent flag below exists precisely because there is no sandbox.

The connector is wired and unit-tested, but has not been exercised end-to-end against a real MCS agent (see the
callout above) — treat a first real run as a smoke test, not a proven-in-production path. You can still drive
`--sut copilot-studio` fully credential-free via the `sutOverride` test seam
(`CopilotStudioAgentFactory.FromAgent`), which is how the project's own test suite
(`tests/AgentEval.Tests/Cli/CopilotStudio/`) exercises the CLI parsing → validation → gate → scan → reporting path
offline.

## Authoring the config JSON

`--copilotstudio-config <file.json>` points at a connection file. It carries **no secret** — the token is
acquired at run time via MSAL device-code auth (first run: sign in via a browser using a printed code; later
runs: silently, from a persisted OS-encrypted cache) and is never stored in this file.

The CLI help text for the flag says this plainly:

> JSON file with the Copilot Studio connection (environmentId, schemaName, tenantId, appClientId; optional
> cloud, agentName). No secret is stored here; the token is acquired at run time via MSAL device-code auth + a
> persisted cache. Required by `--sut copilot-studio`.

| Field | Required | Notes |
|---|:--:|---|
| `environmentId` | yes | The Power Platform environment id hosting the agent. |
| `schemaName` | yes | The agent's schema name (the MCS bot identifier), e.g. `cr1a2_myAgent`. |
| `tenantId` | yes | The Entra tenant id the agent + app registration live in. |
| `appClientId` | yes | The Entra app-registration client id used for device-code auth (a public client, never a secret). |
| `cloud` | no | Power Platform cloud — matches `PowerPlatformCloud`'s member names, case-insensitively: `Prod` (default), `Gov`, `High`, `DoD`, `Mooncake`, `GovFR`, `Ex`, `Rx`, `Local`, `Other` (plus the Microsoft-internal-only `Exp`/`Dev`/`Test`/`Preprod`/`FirstRelease`/`Prv`). An unrecognized value fails validation before any network call. |
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

```bash
agenteval redteam --sut copilot-studio \
  --copilotstudio-config ./mcs-agent.json \
  --i-understand-live-side-effects \
  --intensity quick --format sarif -o redteam.sarif
```

What the command does with these flags, in order:

1. Refuses immediately if `--i-understand-live-side-effects` is absent.
2. Refuses if `--copilotstudio-config` is absent.
3. Refuses if `--max-credits` is negative.
4. Refuses if `--parallelism` is greater than 1 — a live MCS session is stateful/non-reentrant, so the target
   hard-requires the default of 1.
5. Refuses if you pass `--judge` without `--judge-model`, or `--attacker` without `--attacker-model` — this
   target has no model of its own to fall back to (see [Honest reasoning](#how-it-fits-red-team-fidelity)).
6. Loads and validates the config JSON, including `cloud` (fail fast on a bad file, before the scan starts).
7. Prints a one-line note to stderr (unless `--quiet`) if you also passed `--endpoint`, `--azure`, `--model`,
   `--deployment-name`, a non-default `--sut-tier`, `--system-prompt`, or `--system-prompt-canary` — all of
   these are ignored for this target.
8. Builds the live connector (`ConnectionSettings` + token scope + `CopilotClient`) — still no network call yet.
9. Starts the scan. The **first** request of the run is where a network call and, if no cached token exists yet,
   an MSAL device-code prompt actually happen — the console will print a sign-in URL + code; complete it in a
   browser once, and later runs reuse the persisted cache silently.

Steps 1–8 are exercised by the test suite with a fake agent or without ever calling `agent.InvokeAsync` (no config
file needed for those tests to be meaningful — only for you to reach step 6 yourself). Step 9 — the actual network
round trip — has not been independently live-verified; see the callout at the top of this page.

## What `--max-credits` does today

`--max-credits <n>` is **parsed and validated, but not enforced.** Concretely:

- The option defaults to `0` ("no cap") and must be `>= 0` — a negative value is rejected at validation time,
  before the config even loads.
- There is no code path that spends or checks a Copilot Credit budget: the Copilot Studio SDK's activity/response
  models expose no credit-cost field to enforce against, so there's nothing to wire this up to yet.
- Exit code 8 (`ExitCodes.BudgetExceeded`) stays reserved and unemitted until a credit-cost signal exists. See the
  [exit-code table](../cli.md#exit-codes) in the CLI reference.

The CLI help text for the flag says this plainly:

> Cap the Copilot Credits a live `--sut copilot-studio` scan may spend (0 = no cap). NOT YET ENFORCED — the SDK
> exposes no credit-cost field to enforce against, so this is parsed/validated only; exit 8 (BudgetExceeded)
> stays reserved. Every turn burns credits, and a reasoning turn costs substantially more than a scripted one.

Don't rely on `--max-credits` as a real spend guard — track Copilot Credit consumption through Power Platform's
own admin tooling instead.

## What's verified vs. what still needs a live check

`CopilotStudioAgentFactory.BuildLive` constructs a real `CopilotClient` from your config: `ConnectionSettings`
mapped 1:1 (`EnvironmentId`/`SchemaName`/`Cloud`), the token scope from `CopilotClient.ScopeFromSettings` (never
hardcoded), and a `CopilotStudioTokenProvider` (MSAL device-code + persisted cache) wired as the token callback.
None of this makes a network call — the callback is only invoked lazily, by `CopilotClient` itself, on the first
real request. The result is wrapped in a `ChatClientAgent` over a new `CopilotStudioChatClient` (bridges the
SDK's streaming Bot Framework activity API into `IChatClient`), then `CopilotStudioAgentFactory.FromAgent` — the
same seam the credential-free tests already exercise.

**Unit-tested, credential-free:** the `Cloud` → `PowerPlatformCloud` mapping; `BuildLive`'s construction path
(proven not to throw and not to touch the network for a valid config); the activity-stream bridging logic in
`CopilotStudioChatClient` (message-vs-non-message filtering, conversation-id tracking, first-turn vs. subsequent-turn
behavior) against a hand-rolled fake client.

**NOT independently live-verified — needs a real Entra app registration + non-prod Copilot Studio agent:** the
MSAL device-code prompt and silent-refresh path; the persisted-cache round trip across runs; the real HTTP
round trip and response parsing; whether a real agent's `StartConversationAsync`/`AskQuestionAsync` activity
stream matches what the shim assumes (in particular, any non-`message` activity worth surfacing). A gated,
`Skip`-by-default test (`CopilotStudioLiveConnectorManualTests` in `tests/AgentEval.Tests/MAF/CopilotStudio/`) is
ready to run once you have credentials — see its XML doc for setup.

`CopilotStudioAgentFactory.FromAgent` is unchanged and still the credential-free seam: it wraps any
already-constructed MAF `AIAgent` as an `IEvaluableAgent` via `MAFAgentAdapter`, and it's exactly what
`tests/AgentEval.Tests/Cli/CopilotStudio/*.cs` uses (a `ChatClientAgent` over a fake `IChatClient`) to prove the
CLI parsing → validation → gate → scan → reporting path end-to-end without any credential or network call. There
is no supported way, as a CLI user, to substitute your own pre-built agent for `FromAgent` outside of tests — it
isn't exposed as a flag.

If you don't yet have Entra credentials for a non-prod MCS agent, use `--endpoint`/`--azure` against your own
agent, or the credential-free `--sut gatekeeper-demo` target, to exercise the rest of the red-team suite in the
meantime — the connector itself is wired, but the live network path described above still awaits its first
real-credential run.

**A second, lower-level test double** now backs the connector's own test suite:
`MockCopilotStudioConversationClient` (`tests/AgentEval.Tests/MAF/CopilotStudio/`) implements
`ICopilotStudioConversationClient` directly — the seam `CopilotStudioChatClient` talks to — rather than faking
the higher-level `IChatClient`. It supports scripted multi-turn conversations, server-assigned
conversation-id tracking, and configurable error injection (auth failure, a mid-conversation
rate-limit-shaped exception, hang-until-cancelled), so the activity-stream bridging logic itself can be
exercised credential-free, closer to what a real `CopilotClient` would do, without touching the network.

**The `--sut` seam also reaches `eval` and `bench` now.** `ISutTarget` + `SutTargetResolver`
(`src/AgentEval.Cli/Commands/Targets/ISutTarget.cs`) let `CopilotStudioRedTeamTarget` resolve the same way from
`agenteval eval` and `agenteval bench owasp`/`mitre`/`nist` as it already did from `agenteval redteam` — a
`Validate`-drift contract test proves the redteam and shared-seam validation paths agree. `agenteval eval --sut
copilot-studio --dataset <file>` evaluates your dataset's prompts + judge criteria against a live MCS agent;
`agenteval bench owasp --sut copilot-studio --subject <name>` (and `mitre`/`nist`) run the curated compliance
scan against one. `bench`'s three Tier-1 families also gained a generic `--endpoint <url> --model <name>
[--api-key <key>]` option set for a plain OpenAI-compatible endpoint, independent of Copilot Studio. See
[docs/cli.md](../cli.md#agenteval-eval) for the exact flags. Bench Tier 2 (`gdpr`/`eu-ai-act`) does not have
this yet — scoped separately.

## Resilience: retry + config-identity drift detection

Two connector-health features (P6) close a real "does it just work" gap for a live scan:

- **429 retry.** `CopilotStudioChatClient`'s `StartConversationAsync`/`AskQuestionAsync` calls are now wrapped
  in `AgentEval.Core.RetryPolicy` (the same general-purpose retry engine already used elsewhere in this repo —
  nothing new was built) via a 429-specific classifier (`CopilotStudioRetryPolicy`): a rate-limit-shaped
  `HttpRequestException` (`StatusCode == 429`) is retried with exponential backoff; a permanent failure (e.g.
  401 auth) fails immediately instead of wasting the retry budget. Verified against
  `MockCopilotStudioConversationClient`'s existing rate-limit injection — **not yet against a real Copilot
  Studio 429 response**, since the exact exception shape a live `CopilotClient` surfaces for a rate limit has
  not been observed (same "not independently live-verified" boundary as the rest of this connector; see above).
- **Config-identity drift detection.** `--copilotstudio-save-config-baseline <path>` pins the resolved config's
  agent identity (`environmentId`/`schemaName`/`cloud` — deliberately excluding `tenantId`/`appClientId`, which
  are about *how you authenticate*, not *which agent you're talking to*) as a SHA-256 fingerprint, mirroring
  `AgentEval.Skills.SkillManifestBaseline`'s exact JSON-baseline shape (Skills Phase 4b's hash-pin-and-diff
  primitive, reused verbatim — `ManifestFingerprint`/`ManifestDriftDetector`, zero new hashing/diffing code).
  `--copilotstudio-config-baseline <path>` then compares a later run's config against that pin: drift **warns**
  by default (an agent's identity changing is very likely intentional — pointing at a different/updated agent
  on purpose), or **hard-stops before any network call** with `--fail-on-config-drift`. The primary use case is
  data integrity: comparing today's `--baseline` RESULTS against a stale baseline captured under a *different*
  agent's identity would be meaningless. These three flags are **redteam-only** — not part of the shared
  `eval`/`bench` `--sut` seam.

## Fidelity badge

Every scan's post-summary now prints an aggregate `EvidenceFidelity` breakdown for this target specifically
(`WritePostScanSummary` — no longer a no-op): `Verbal N/T · IntentToAct N/T · Behavioral N/T`. Because this
target is structurally text-only (see below), `Behavioral` is always `0` in practice — the point is making that
ceiling visible in the CLI's own summary output, not just internally on each `ProbeResult`.

**Update (2026-07-17):** the broader, all-targets fidelity column across the shared RedTeam report renderers is
now done too, closing the gap this section used to flag. Every renderer shows a per-probe fidelity badge:
`JSON`/`SARIF` already carried a `Fidelity` field; `Markdown` (an inline `` `[behavioral]` ``/`` `[verbal]` ``/
`` `[intent-to-act]` `` tag next to each compromised probe), `JUnit` (a `Fidelity:` line in the failure body,
plus the fidelity label folded into the inconclusive `<error>` message), and `PDF` (a bracketed label next to
each listed probe) now show it too — not just this target's own CLI summary.

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
  **Correlation-key spike (2026-07-17):** investigated whether a client-side `conversationId`
  (`Activity.Conversation.Id`, the same id `CopilotStudioChatClient` already tracks and stamps onto every
  `ChatResponseUpdate`) can be reliably joined against Copilot Studio's own server-side telemetry to recover
  real tool-call evidence. Two candidate telemetry sources exist, and **both carry real, structural obstacles
  that only a live-tenant test can fully resolve** (not run here — this spike is desk research against
  Microsoft's own current documentation, not a live-tenant confirmation):
    - **Dataverse session transcripts** (`SessionID`, `TopicId`, `ChannelId` — downloadable per-agent, 29-day
      window) are **NOT written until ~30 minutes after conversation inactivity**. A red-team scan grades each
      probe as it runs; telemetry-based enrichment at that latency cannot be inline — it can only be a
      **separate, deferred, offline reconciliation pass** run well after a scan completes, not a live
      `TraceEnrichingChatClient` decorator on the hot path as originally sketched. Whether `SessionID` is
      actually the same value as the client SDK's `conversationId` is not confirmed by public documentation.
    - **Application Insights** telemetry (tool input/output captured in OpenTelemetry spans when "Log
      conversation details" is enabled) is **opt-in, per-agent, and requires the target agent's OWNER** to
      configure an Azure subscription + connection string + explicit logging toggles in that agent's own
      settings. AgentEval red-teams agents it does not administer in the general case — it cannot assume, set
      up, or even detect whether a given target has this configured. Reading it also needs a DIFFERENT
      credential/permission scope (Application-Insights Log Analytics read access, or Dataverse's dedicated
      **Bot Transcript Viewer** role) than what's needed just to converse with the agent.
  **Conclusion: the join is plausible but not confirmed, and even if confirmed, cannot be inline** — it forces
  a design change from "decorate the live chat client" to "a separate post-hoc enrichment command," and even
  then only for target agents whose owner has opted into one of the two telemetry paths. Per this repo's
  honesty discipline, an ambiguous or unavailable join must degrade to **Inconclusive**, never a fabricated
  **Behavioral**. P5 (L2 enrichment) remains correctly deferred — this spike's outcome is a scoping correction,
  not a green light to build the original design.
- **This is why `--sut-tier`, `--system-prompt`, and `--system-prompt-canary` are ignored** for this target
  (step 7 above) — there is no tool-harness tier to select and no system prompt this target controls.
- **Evidence capture is off** (`IncludeEvidence => false`) — a live MCS response can carry real PII, so raw
  request/response text is never written into a report or CI log for this target, unlike the default
  `IncludeEvidence => true` for `gatekeeper-demo` and the endpoint/`--azure` path.
- **No gate-trace summary, but an aggregate fidelity badge.** There's no local Gatekeeper trace for a live
  conversational agent — `WritePostScanSummary` renders nothing gate-related — but it does now print the
  aggregate `Verbal`/`IntentToAct`/`Behavioral` breakdown across the scan (see "Fidelity badge" above);
  per-verdict fidelity is still reported by the evaluators independently either way.
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
| `Copilot Studio config field 'cloud' has an unrecognized value: '…'` | `cloud` in the config JSON doesn't match a `PowerPlatformCloud` member name. | Use one of the values listed in the [config table](#authoring-the-config-json) above (case-insensitive). |
| A device-code prompt appears and the scan seems to hang | First run against a given (tenant, app) pair with no cached token yet — this is expected MSAL device-code sign-in, not a bug. | Open the printed URL in a browser and enter the printed code. Later runs reuse the persisted cache silently. |
| `Unknown --sut value: '…'. Valid: gatekeeper-demo, copilot-studio.` | A typo in `--sut` (it's case-insensitive, but must otherwise match). | Use `copilot-studio` (or `gatekeeper-demo`). |

## See also

- [Red Team Security](../redteam.md) — the full scanner: attacks, evidence fidelity, judge modes, CI baseline gate.
- [CLI Reference — Exit codes](../cli.md#exit-codes) — the full exit-code table, including the reserved `8`
  (`BudgetExceeded`) this target will use once `--max-credits` enforcement has a credit-cost signal to enforce.
- [Attack the gate](../gatekeeper/attack-the-gate.md) — the credential-free `--sut gatekeeper-demo` closed loop,
  useful for CI where a live Copilot Studio agent + credentials aren't available.
