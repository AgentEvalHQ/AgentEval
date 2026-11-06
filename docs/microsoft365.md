# Microsoft 365 (`--sut microsoft365`)

## New to this? Start here

**The problem.** You have an agent in Microsoft 365: Microsoft 365 Copilot, or a declarative agent your tenant
publishes. You want to benchmark it like any other agent. But it has no plain HTTP endpoint. It needs a real Entra
sign-in as a real user, it speaks the Work IQ A2A protocol, and it works on that user's live mailbox, files and calendar.

**What this connector does.** It talks to the agent over Work IQ A2A 1.0, one prompt per turn. It keeps what the
service returns: the answer, its citations and any diagnostic artifacts the tenant emits. AgentEval sees an ordinary
`IEvaluableAgent`, from code, from `agenteval eval` or from the `agenteval bench` families.

**The one trade-off: evidence by availability.** The connector sees only what the service returns, and by default it
asks for the answer only. So every evidence channel carries an availability label (`NotRequested`, `NotProvided`,
`Provided`). An empty tool list means "no tool telemetry was observed", never "no tool was used". See
[the evidence model](#the-evidence-model).

**Where to start.** The [demo project](../samples/AgentEval.Microsoft365Demo/README.md) is the fastest path: one command
checks the connection, sends one prompt, runs a five-case benchmark and saves everything in one run folder. Its run
folder also holds an AEF run of the benchmark. For your own code, read
[Using it directly in code](#using-it-directly-in-code-no-cli). For the CLI, start with the
[readiness check](#readiness-check-agenteval-m365-doctor). An external tester starts with the
[demo README](../samples/AgentEval.Microsoft365Demo/README.md); the [tester guide](microsoft365-tester-guide.md) holds
the prerequisites and a CLI path for when the maintainers ask.

---

> **Status: Experimental.** The provider, the CLI target and `agenteval m365 doctor` are built and tested offline. The
> maintainers have no Microsoft 365 tenant and have not run the live path; an external tester validates it with the
> [tester guide](microsoft365-tester-guide.md). No sentence on this page claims a live result. See
> [what is verified, and what still needs a tenant](#whats-verified-vs-what-still-needs-a-live-check).

## What it is

`microsoft365` is a built-in `--sut` target for `agenteval eval` and the `agenteval bench` families, not for
`agenteval redteam`. It replaces the `--endpoint`/`--azure` agent with one that:

- owns the `--m365-*` options and reads the shared `--i-understand-live-side-effects` consent flag;
- checks the consent flag, the options and every config field before any network call;
- signs in as a real user with MSAL device-code sign-in (a public-client app registration, no secret);
- discovers the agent card, selects the JSON-RPC 1.0 interface and refuses any host outside `trustedHosts`;
- runs each prompt as one blocking `SendMessage` turn, polls a running task with `GetTask`, and never resends a message;
- returns an `AgentResponse` only for a completed task; every other outcome is a typed exception with the full result;
- records a versioned evidence document per turn, and a redacted trace and issue register per run.

Source: `src/AgentEval.Microsoft365/`. It has no Microsoft Agent Framework dependency. The A2A .NET SDK (`A2A`
1.0.0-preview2) sits behind the provider's own interfaces.

## Using it directly in code (no CLI)

The provider is its own project, so consumers who do not use it never get the A2A SDK and MSAL. It is not on NuGet yet:
build it from source. Reference three projects, because the provider references Abstractions and Core privately. Every
public type of the provider is `[Experimental("AGENTEVAL_M365_PREVIEW001")]`, so the build fails until you suppress the
diagnostic:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);AGENTEVAL_M365_PREVIEW001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\AgentEval\src\AgentEval.Abstractions\AgentEval.Abstractions.csproj" />
  <ProjectReference Include="..\AgentEval\src\AgentEval.Core\AgentEval.Core.csproj" />
  <ProjectReference Include="..\AgentEval\src\AgentEval.Microsoft365\AgentEval.Microsoft365.csproj" />
</ItemGroup>
```

<!-- snippet: m365-in-code -->
```csharp
using AgentEval.Core;                  // AgentResponse
using AgentEval.Microsoft365;          // Microsoft365Config, Microsoft365AgentFactory, WorkIqOptions, Evidence()
using AgentEval.Microsoft365.Evidence; // Microsoft365Evidence, Microsoft365TurnEvidence

var config = Microsoft365Config.Load(new FileInfo("workiq.json"));

// No default: the agent acts as the signed-in user on real Microsoft 365 data.
// Pass true only for a non-production tenant and identity.
await using WorkIqEvaluableAgent agent = Microsoft365AgentFactory.BuildLive(
    config,
    iUnderstandLiveSideEffects: true,
    options: new WorkIqOptions { MaxTurns = 10 });

try
{
    AgentResponse reply = await agent.InvokeAsync("In one sentence, what can you help me with?");
    if (!Microsoft365Evidence.IsAnswerMeasured(reply))
    {
        Console.WriteLine("Not measured: several answer candidates, and none was selected.");
        return;
    }

    Console.WriteLine(reply.Text);
    Microsoft365TurnEvidence? evidence = reply.Evidence();
    Console.WriteLine($"citation markers: {evidence?.CitationMarkers.Describe("marker(s)")}");
    Console.WriteLine($"tool executions:  {evidence?.ToolExecutions.Describe("execution(s)")}");
}
catch (WorkIqTurnNotCompletedException ex)
{
    // Only a completed turn becomes an AgentResponse. Nothing was approved or resent.
    Console.Error.WriteLine($"not completed: {ex.TurnResult.Outcome}");
}
```

- `Microsoft365Config.Load` reads and validates the [config file](#authoring-the-config-json), with no network call.
- `BuildLive(config, iUnderstandLiveSideEffects, options, tokenProvider, onTurn, innerHandler)` makes no network call
  either: sign-in and card discovery happen on the first turn. `iUnderstandLiveSideEffects` has no default; `false`
  throws.
- Sign-in uses MSAL device code with an OS-encrypted token cache, and prints its message on stderr. To print it
  elsewhere, pass `new WorkIqDeviceCodeTokenProvider(tenantId, appClientId, scope, deviceCodePrompt)` as
  `tokenProvider`, and the same instance to the readiness check. A sign-in during a turn waits until its code expires
  (about 15 minutes), not until `TurnDeadline`.
- `onTurn` receives every `WorkIqTurnResult`, completed or not, before the agent returns or throws.
- `WorkIqOptions` holds the timeouts (`SendTimeout` 300 s, `TurnDeadline` 10 min, `RecoveryBudget` 2 min), `MaxTurns`
  (0 = no cap), the conversation and content policies, raw capture and the diagnostics settings.
- A turn that completes with several answer candidates and none selected is **not measured**:
  `Microsoft365Evidence.IsAnswerMeasured` returns `false`. Its `Text` is empty. Only `IsAnswerMeasured` tells it apart:
  a completed turn with no answer at all has the same `FinishReason`, `completed-no-answer`, and is measured, and fails.
- `reply.Evidence()` returns the turn's evidence document, or `null` for a reply from another provider.
  `TryGetEvidence` is the `bool` form. `TokenUsage`, `ModelId` and `RawMessages` stay `null`.
- Three exceptions end a turn. `WorkIqTurnNotCompletedException` carries the result of a turn that did not complete.
  `WorkIqTurnCanceledException`, an `OperationCanceledException`, means your token stopped the wait.
  `WorkIqTurnBudgetExceededException`, a `FatalEvaluationException`, stops the batch at the `MaxTurns` cap. Their
  messages carry ids and categories, never a prompt, an answer or a token.
- For several turns in one conversation, set `ConversationPolicy.ContinueContext`. `ResetSessionAsync()` starts a new
  context, locally. Concurrent turns on one agent throw `InvalidOperationException`.
- `agent.Conversation` is the rich API (`IWorkIqConversation`). Its `ContinueAsync` is not supported in this preview:
  it always throws `NotSupportedException`, so a paused task cannot be continued.
- `Microsoft365AgentFactory.FromConversation` wraps any `IWorkIqConversation`, such as a test fake, as the same agent.

For a judge-graded batch, use `MAFEvaluationHarness` as for any other agent ([LLM as a Judge](llm-as-judge.md)); it
needs a reference to `src/AgentEval.MAF` too. `Microsoft365ReadinessCheck.RunAsync` (namespace
`AgentEval.Microsoft365.Readiness`) runs the doctor's stages and returns a `ReadinessReport`; a runtime failure becomes
a failed stage. `ReadinessReportRenderer` writes the report as the CLI does. The XML docs of `ReadinessCheckOptions`
describe the rest.

## Prerequisites

[Connect to a Microsoft 365 tenant](microsoft365-tenant-setup.md) gives the steps in order. In short:

- **Work IQ enabled** in the tenant (a Global Administrator, once) and the test user in a **usage-based billing plan**
  for Copilot Credits: Work IQ calls are billed separately from any subscription. Without either one,
  the service answers HTTP 403.
- An **Entra app registration configured as a public client**: *Allow public client flows* set to **Yes** on its
  *Authentication* page. No client secret is used. Without the setting, Entra refuses the sign-in (`AADSTS7000218`).
- The **delegated permission `WorkIQAgent.Ask`** on that app, with **admin consent**. Without consent the first sign-in
  fails with `AADSTS65001`. The permission covers reads **and writes** of the user's Microsoft 365 data.
- The **tenant id** and the app's **client id**.
- A **non-production identity**: a test user in a test tenant, with a small synthetic corpus (for example a document
  named "Demo Q3 Planning").
- Optional: the **agent id** of a declarative agent the user can reach. Without one, the connector talks to the default
  Work IQ agent (Microsoft 365 Copilot). Word, Excel and PowerPoint agents need their application and do not answer
  usefully here.
- The **.NET 10 SDK**.

The [setup page](microsoft365-tenant-setup.md) gives the click-by-click steps and the first connection test.

## Authoring the config JSON

`--m365-config <file.json>` and `Microsoft365Config.Load` read a connection file. It holds **no secret**. The token
comes from MSAL device-code sign-in at run time: the first run prints a code to enter in a browser, and later runs sign
in silently from an OS-encrypted cache.

| Field | Required | Default | Notes |
|---|:--:|---|---|
| `tenantId` | yes | none | The Entra tenant id of the app registration. |
| `appClientId` | yes | none | The app registration's client id: a public client, never a secret. |
| `endpoint` | no | `https://workiq.svc.cloud.microsoft/a2a/` | An absolute `https` URL without query or fragment. Its host must be in `trustedHosts`. |
| `agentId` | no | none: the default Work IQ agent | An opaque id such as `U_abc.def`, passed as is. One path segment: no `/`, `\`, `?`, `#`, whitespace or control characters. |
| `scope` | no | `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` | The delegated scope. The audience is Work IQ, not Microsoft Graph. |
| `agentName` | no | none | The name in reports. Without it: the agent id, then `Work IQ default agent`. |
| `diagnosticProfile` | no | `PublicAnswerOnly` | Or `Native118Diagnostics`, in any case. See [Diagnostic profiles](#diagnostic-profiles). `--m365-profile` overrides it. |
| `timeZone` | no | none | An IANA id such as `Europe/Zurich`. Each message then carries the time zone and its offset, so date prompts are deterministic. |
| `trustedHosts` | no | `["workiq.svc.cloud.microsoft"]` | The only hosts a token may go to. Must include the endpoint's host. |

Property names match in any case. The template is `samples/datasets/m365-config.template.json`:

```json
{
  "endpoint": "https://workiq.svc.cloud.microsoft/a2a/",
  "agentId": null,
  "tenantId": "REPLACE-WITH-ENTRA-TENANT-GUID",
  "appClientId": "REPLACE-WITH-PUBLIC-CLIENT-APP-REGISTRATION-GUID",
  "scope": "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask",
  "agentName": "My Non-Prod Microsoft 365 Agent",
  "diagnosticProfile": "PublicAnswerOnly",
  "timeZone": "Europe/Zurich",
  "trustedHosts": ["workiq.svc.cloud.microsoft"]
}
```

`Load` checks the file before anything else runs. A missing or unreadable file, malformed JSON, a missing field and a
bad value each throw an `InvalidOperationException` that names the field and the fix. A value that still reads
`REPLACE-WITH-…` is refused too. Whether `tenantId` and `appClientId` name a real tenant and app is the sign-in's job.
The doctor's `config` stage warns when `tenantId` is neither a GUID nor a domain name, or `appClientId` is not a GUID.

## The consent flag and why it's required

`--i-understand-live-side-effects` is checked before the config file and before anything that could reach the network.
Without it the command refuses and sends nothing: `eval` exits `3`, a `bench` family exits `2`. `agenteval m365 doctor`
needs it only for its probe and census. The CLI reads only the flag. In code it is the `iUnderstandLiveSideEffects`
parameter of `BuildLive`.

The agent acts as the signed-in user, on that user's real mailbox, files and calendar. A prompt such as "schedule a
meeting" can make it propose a real action. The connector never approves a confirmation card and never resends a
message, but it cannot undo what the agent does with a prompt it accepted. The flag records your statement that the
config points at a **non-production** identity; the tool cannot check it. `copilot-studio` and `microsoft365` share the
one flag.

## Readiness check: `agenteval m365 doctor`

Run it first with a new tenant. It checks the connection stage by stage, and for a failed stage says what failed, why
and what to do. Its options and exit codes are in the [CLI reference](cli.md#agenteval-m365-doctor).

A published `agenteval` tool may not have the Microsoft 365 target yet. If `agenteval m365 doctor --help` reports an
unknown command, run the CLI from a clone: `agenteval …` on this page means
`dotnet run --project src/AgentEval.Cli --framework net10.0 -- …`, from the repository root. The tester guide's
[Install](microsoft365-tester-guide.md#install) defines it once per terminal.

```bash
agenteval m365 doctor --m365-config ./workiq.json                                         # no prompt is sent
agenteval m365 doctor --m365-config ./workiq.json --probe --i-understand-live-side-effects # one real, read-only prompt
```

The stages run in order and stop at the first FAIL unless you pass `--continue`. A stage that does not run is SKIPPED
with its reason, never a pass.

| Stage | What it does | PASS means |
|---|---|---|
| `config` | Loads and validates the file. No network. | Every rule passed. It warns when an id cannot be right, or the profile is `native118`. |
| `auth` | Gets a token from the cache, else by device-code sign-in. Once the prompt shows, it waits until the code expires. | A token for the Work IQ scope was acquired. The token never appears in any output. |
| `discovery` | Fetches the agent card through the handlers a live run uses, and selects the A2A 1.0 JSON-RPC interface on a trusted host. | The interface was selected, and the service accepted the token. |
| `protocol` | Sends `GetTask` for a random task id (`doctor-<guid>`). | The service answered JSON-RPC `-32001` (TaskNotFound): it speaks A2A 1.0 and accepted the token. |
| `probe` | Needs `--probe` and consent. Sends `Reply with the single word READY.` in a fresh context. Never answers a pause. | The turn completed. An answer that is empty or not READY adds a warning. |
| `census` | Runs after the probe. Under `public` it sends the same prompt again under `native118`, then calls `CancelTask` on the finished task. | It records what the tenant supports. A refusal of `native118` still passes; it fails only when it could not measure. |

The console prints one line per stage: `[PASS]`, `[FAIL]` or `[SKIPPED]`, the stage and a summary. Under a failed stage
it prints `issue:`, `meaning:` and `do:`, and it ends with `Result: PASS`, `FAIL` or `INCOMPLETE`. The report folder
holds `readiness.md` (every stage's evidence, and what to send back), `readiness.json` (schema
`agenteval.m365.readiness/v1`) and the run's trace and issue files.

One failure gives one issue. When a lower layer already registered a stage's failure (for example `http.403`), the
stage verdict `doctor.<stage>.failed` goes to the trace only; otherwise the stage registers it. The replies the doctor
provokes on purpose (TaskNotFound on the random id, TaskNotCancelable on the finished task) are never issues.

## Running a custom benchmark (`agenteval eval`)

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 10 \
  --judge https://<judge-endpoint>/v1 --judge-model <judge-model> \
  --subject agent:m365-default-copilot --aef ./m365-results/aef --aef-content on \
  --format json -o ./m365-results/m365-smoke.json
```

Before any network call, the command checks the dataset file, the consent flag, the `--m365-*` options and the config
file. A refusal prints `Error: …`, exits `3` and sends nothing. Then a badge line on stderr says what the target
observes and names this run's trace and issue files. The first turn signs in; with no cached token, the console shows
the device-code message. Every case runs in a fresh server context.

**The dataset** is the ordinary [`eval` dataset](cli.md#agenteval-eval). `samples/datasets/m365-smoke.json` has 8
read-only cases: identity, capabilities, a grounded summary with citation, date reasoning, out-of-scope live data,
prompt injection, PII restraint and action restraint. Keep prompts read-only, with explicit dates instead of "today".

**The judge.** `--judge <url> --judge-model <model>` names an OpenAI-compatible judge; its key comes from `--api-key`,
else `OPENAI_API_KEY`. Always pass `--judge-model`: its default is the target's name. Without `--judge`, `expected`
becomes a contains check, and a case with `evaluation_criteria` is **NOT MEASURED** (exit `11`, AEF `not_measured`):
no judge graded it. The answer is kept, and `--metrics` still score it.

**Results.** A case is never graded on something that is not the agent's answer:

- A turn that did not complete is an error row (`Work IQ turn did not complete: outcome=…`).
- A turn that completed with several answer candidates and none selected is **NOT MEASURED**: an error row that starts
  `Not measured:`, with no score. `--metrics` records each metric for it as not measured. The AEF run records the case
  as `not_measured`, with that cause as its reason.
- A turn that completed with no answer at all is graded on the empty text, and fails.
- Both completed cases print an `[m365] WARNING … no answer was selected` line, even without `--m365-verbose`.

When nothing failed but a case was not measured, `eval` exits `11` ([exit codes](cli.md#exit-codes)).

**The AEF run.** `--aef <dir>` writes the sealed AEF run. Without `--subject`, the subject is `agent:<display name>`. The
run settings record the provenance, never a secret: `a2aVersion`, `m365Profile`, `normalizerVersion`,
`providerProfileVersion`, `m365ConfigSha256` (the hash every issue carries), `m365AgentId` (or `default`),
`m365EndpointHost` and `m365ContentPolicy`. The keys come from the provider's public
`Microsoft365RunSettings.For(config, profile, contentPolicy)`; the demo's AEF run records the same keys. With
`--aef-content on`, each case also records its prompt, the expected answer, the agent's answer and the turn's evidence
document. The trace and issue files use the AEF case id as `caseId`.

The evidence document keeps the answer text, citation names and URLs, counts and states (`SelectedContent`). Tool
arguments and results, approval argument text, query text, retrieval hit URLs, passages and resource metadata become
`Redacted`, with their source pointers kept. `--m365-capture-raw <dir>` switches to `FullEvidence` and keeps the raw
bytes and stream frames under `<dir>`. They may hold tenant data: keep the folder out of version control.

## Running standard benchmarks (`agenteval bench`)

```bash
agenteval bench gdpr --preset smoke --subject m365-default-copilot \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects
agenteval bench eu-ai-act --preset smoke --subject m365-default-copilot --input "Microsoft 365 Copilot, smoke run" \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects
agenteval bench owasp --preset smoke --subject m365-default-copilot --m365-max-turns 30 \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects
```

Each scenario's prompt goes to the agent as one turn, with the same `--m365-*` options as `eval`. Set `--m365-max-turns`
to the number of scenarios you accept: the OWASP `smoke` preset sends 23 probes within 10 minutes. Every family requires
`--subject`. `bench eu-ai-act` also requires `--input`, which is recorded with the run.

| Family | `--sut microsoft365` | Judge |
|---|---|---|
| `gdpr`, `eu-ai-act` | Yes. Presets `smoke`, `standard` (default), `audit`, and the domain packs. | The `AZURE_OPENAI_JUDGE_*` trio when all three are set, else the provider `AI_INFERENCE_PROVIDER` selects. No judge: exit `3`. |
| `owasp`, `mitre`, `nist` | Yes. OWASP presets `top10` (default), `smoke`, `audit`, `top10-rag`. | As above. A judge failure or a timeout makes the run INCOMPLETE (exit `11`), never a pass. |
| `perf` (`cost`, `latency`, `throughput`) | Accepted, but not tested with this target: treat a run as unverified. | None. |
| `agentic`, `memory`, `longmemeval`, `typedmemeval`, `trace-fidelity`, `workflow-trace-fidelity`, `autoaudit` | No. | Not applicable. |

A missing consent flag or a bad config is exit `2`. The AEF run records the same settings as `eval`, without
`m365ContentPolicy`, and no per-case evidence document. On `bench`, a turn that completed with several answer
candidates and none selected is never graded: `gdpr` and `eu-ai-act` record it as an error, `owasp`, `mitre` and
`nist` as inconclusive, and the `[m365] WARNING` line says the case is not measured. See the [GDPR](benchmarks/gdpr/getting-started.md),
[EU AI Act](benchmarks/eu-ai-act/getting-started.md), [OWASP](benchmarks/owasp/getting-started.md) and
[MITRE](benchmarks/mitre/getting-started.md) guides.

## What `--m365-max-turns` does

`--m365-max-turns <n>` (in code, `WorkIqOptions.MaxTurns`) caps the turns one agent makes; `0`, the default, means no
cap. Every turn that starts counts, completed or not, including one that ends in an exception. A turn refused because
another turn is active does not count. **Turns are counted, consumption is not
metered**: Work IQ reports no per-turn cost, so the cap bounds the sends, not the spend.

The check runs before each send, so the turn that would cross the cap never fires. The batch stops once with
`Work IQ evaluation stopped: N turn(s) already used; the next turn would exceed MaxTurns (N).` `eval` exits `3`; a
`bench` run stops with a non-zero exit (`bench gdpr` exits `1` and writes an aborted AEF run). `--runs N` multiplies the
turns. `WorkIqEvaluableAgent.TurnsUsed` gives the count in code.

## Diagnostic profiles

The profile sets which output modes a turn requests, and so which channels can be `Provided` at all.

| Profile | CLI value | Requests | Channels |
|---|---|---|---|
| `PublicAnswerOnly` (default) | `--m365-profile public` | `text/plain`: the answer, citations and attributions. | Tools, retrieval and skills are `NotRequested`. |
| `Native118Diagnostics` | `--m365-profile native118` | Also the two diagnostic modes of Microsoft's native evaluator 1.18.0 (*Developer Info*, *Retrieval Info*). | Those channels become `Provided` or `NotProvided`. |

`Native118Diagnostics` is **experimental**: Microsoft's native evaluator 1.18.0 uses these modes, but no Microsoft
document defines them. If the service refuses them, the turn reports the failure and is never retried in answer-only
mode. `--m365-profile` overrides the config's `diagnosticProfile` (in code, `WorkIqOptions.ProfileOverride`). The
doctor's `census` stage records whether a tenant accepts the modes.

## The evidence model

Every turn yields one `Microsoft365TurnEvidence` (schema `agenteval.microsoft365.turn-evidence/v1`, normaliser
`1.0.0`, provider profile `workiq-a2a-1.0/native118-shapes-v1`). It travels on the `AgentResponse` (key
`Microsoft365Evidence.Key`), on `WorkIqTurnResult.Evidence` (also when the turn did not complete), and with
`eval --aef-content on` into the AEF run. The same capture and versions always give the same document.

Seven channels are `EvidenceSet<T>` values, with `Availability`, `Validation`, `Coverage`, `Scope`, `Items`, `Sources`
(JSON pointers into the capture) and `Issues`. `Answer`, `CitationLinks` and `RawCaptures` are plain values.

| Channel | Under `public` | Under `native118` | Notes |
|---|---|---|---|
| `Answer` | The selected answer. | Same. | `RawText` keeps the citation markers. |
| `CitationSources` | `Provided` or `NotProvided`. | Same. | Attribution objects as sent. A field not sent is `Missing`. |
| `CitationMarkers` | `Provided` or `NotProvided`. | Same. | Parsed from the answer text (`cite(…)` blocks, markdown `[n]`): a parser result, not a provider fact. |
| `CitationLinks` | `Unresolved`, unless an explicit provider key binds marker and source. | Same. | Never a positional guess. |
| `ToolSelections`, `ToolExecutions` | `NotRequested`. | `Provided` or `NotProvided`. | Selection is not execution; a missing status is not success. |
| `RetrievalExecutions` | `NotRequested`. | `Provided` or `NotProvided`. | `totalResultCount` is not the returned count. |
| `ApprovalIntents` | `Provided` only for a pause on a confirmation card. | Same. | A request, not an execution. Several pause parts, one asking for input, make it `Conflicting`. |
| `SkillActivations` | As reported. | Same. | Advertised skills never count. |
| `RawCaptures` | References: state, SHA-256, size. | Same. | Bytes on disk only with `--m365-capture-raw`. |

| Availability | Meaning |
|---|---|
| `NotRequested` | The profile did not ask for the channel. It says nothing about what the service did. |
| `NotProvided` | Requested, and the service returned nothing. Still not "did not happen". |
| `Provided` | The service returned data. `Validation` says whether it was readable: `Valid`, `Partial`, `Malformed` or `Conflicting`. |

`EvidenceSet<T>.Describe(noun)` is the one text form of a channel, in the CLI, the demo and the samples:
`NotRequested`; `Provided (observed empty: 0 execution(s) in this snapshot)` for a valid, empty channel (zero rows in
this snapshot, not zero activity); `Provided (2 marker(s))`; and `Provided (0 execution(s), Conflicting)` for a channel
that could not be read.

Schema v1 also defines the availabilities `Unsupported`, `Disabled` and `Redacted`, the `CoverageBasis` field and the
bindings `ExplicitReferenceUri` and `HeuristicDisplayOnly`. This version never emits them; the content policy marks
single fields `Redacted`, never a whole channel. `TaskState` is the raw final `TASK_STATE_*`. `CaptureState` says whether
the kept material can rebuild the document. AgentEval's generic tool-usage report stays empty for this provider: it does
not read Work IQ diagnostics.

## The trace and issue files

Every live run writes two JSONL files:

- **`m365-trace-<run id>.jsonl`**: every trace event, one JSON object per line: `at`, `stage`, `level`, `code`,
  `message`, and the ids and statuses the emitter knew (`caseId`, `turnId`, `taskId`, `requestId`, `httpStatus`,
  `rpcErrorCode`, `a2aState`, `elapsedMs`, …). An unknown fact is left out.
- **`m365-issues.jsonl`**: the issue register. Each issue has a `stage`, a `category` (`Config`, `Authentication`,
  `Permission`, `Network`, `Protocol`, `Service`, `Capture`, `Budget`, `Case`, `Unknown`), a stable `code`, a `message`, a
  `suggestedCause`, the `aadstsCode` when Entra gave one, the same ids, and the run's `configSha256`, `adapterVersion`
  and `a2aVersion`. Runs append to it. [Troubleshooting](#troubleshooting) lists every code.

**Where.** `WorkIqOptions.TraceFile` and `IssueFile` when set, else `DiagnosticsDirectory`, else the workspace's
`.agenteval/m365/` (found by walking up from the current directory), else `<temp>/agenteval-m365/`. A fresh clone has no
`.agenteval/`: run `agenteval init-workspace` once. `--m365-trace <file>` moves the trace. The first event,
`config.diagnostics_ready`, names both files, and `eval` and `bench` print both paths. The doctor writes them into its
report folder. The run id is `m365-<yyyyMMdd-HHmmss>-<4 hex>` (UTC) unless `WorkIqOptions.RunId` sets it. A run keeps
the files open, so read them with a tool that shares the file (`tail -f`, `Get-Content -Wait`).

**Codes.** Trace codes read `<stage>.<event>`, such as `auth.silent_hit`, `discovery.interface_selected` or
`turn.outcome`. A measured operation emits `<code>.start`, then `.end` or `.failed`. Each issue is mirrored into the
trace as `issue.<category>`, so the trace shows when it happened.

**One failure, one issue.** The layer that ends the operation registers the failure, once:

- Inside a turn, the turn's issue `turn.<outcome>` is the only one. It keeps the failure's category, HTTP status and
  JSON-RPC code: one HTTP 403 gives one `turn.failed` issue with category `Permission`.
- A call outside a turn (discovery on its own, an explicit `GetTask`, a doctor stage) registers the client's code, such
  as `http.403` or `discovery.untrusted_host`.
- A missing token is registered once, by the auth layer (`auth.sign_in_not_completed`, `auth.empty_token`,
  `auth.token_acquisition_failed`). Nothing was sent.
- The auth handler's `auth.unauthorized`, `auth.forbidden` and `auth.untrusted_destination` are trace events, not
  issues.

**The console view.** `--m365-verbose` (on by default for the doctor) prints every event to stderr as
`[m365 HH:mm:ss.fff Stage/Level code] message` with the known ids and statuses. `eval` and `bench` then also print one
line per turn, completed or not:

```text
[m365 turn] case=… turn=… outcome=Completed transport=Completed task=… ctx=… state=TASK_STATE_COMPLETED answer=Complete(212 chars) citationMarkers=Provided (2 marker(s)) toolExecutions=NotRequested elapsedMs=…
```

A turn that did not complete adds `failure=…` and never shows answer text.

**What the files never contain:** a bearer token, a JWT, an auth code, a device code, a client secret, a cookie or the
`Authorization` header. `WorkIqRedaction` rewrites them before any sink sees them. The device-code message shows in
full on the console only, because you must read it; the trace keeps it with the code replaced by `[redacted]`. Lines
are capped at 64 KiB. A failing sink is reported once on stderr and dropped; the run continues.

**What they may contain:** your tenant, app and agent ids, task and context ids, the service's `request-id`, HTTP
statuses, file paths and the service's error messages. No prompt and no answer. After a probe, `readiness.md` and
`readiness.json` also show the probe prompt and the first 80 characters of the answer. Read the
[tester guide](microsoft365-tester-guide.md#send-back-the-cli-files) before you share them.

## What's verified vs. what still needs a live check

**Tested offline, without credentials** (fixtures under `tests/AgentEval.Tests/Microsoft365/`):

- Config loading and every validation rule, the consent gate of `BuildLive`, and `WorkIqOptions.Validate`.
- The trust policy against fixture cards: two interfaces, an agent card, an A2A 0.3 card, an untrusted host.
- The task and streaming reducers and the answer selector against fixture envelopes and SSE streams: completed, paused,
  failed, rejected, canceled and polled tasks, JSON-RPC errors, a 0.3 reply, HTTP 429, `CancelTask` races, CRLF
  framing and an early end of stream.
- The evidence normaliser against the normalisation fixtures.
- The diagnostics: redaction, the JSONL sinks, the console format, the paths, and one issue per failure on the
  blocking, streaming and doctor paths.
- The agent with a fake conversation: the completion gate, the turn cap, the policies, reset, `onTurn`, cancellation
  and the not-measured rule.
- The readiness check with a fake HTTP handler, token provider and clock: every stage's pass and fail paths, consent,
  `--continue`, the reports, and that no output carries the token.
- The CLI end to end with a stand-in agent: every refusal, `eval`, the `bench` families (AEF marks the run `Mocked`), the
  AEF run settings and evidence documents, and `m365 doctor`'s options and exit codes.

**Needs a tenant** (the external tester's job, see the [tester guide](microsoft365-tester-guide.md)):

- The MSAL device-code prompt, the silent refresh and the token cache across runs.
- The real agent card: its interfaces, and a JSON-RPC 1.0 interface on a trusted host.
- That the service accepts `A2A-Version: 1.0` and answers in the 1.0 shape.
- The real shape of a completed turn: where attributions sit, the citation marker format, whether a blocking send needs
  `GetTask` polls, and the timings.
- Whether the `native118` modes are accepted, and whether *Developer Info* and *Retrieval Info* artifacts appear.
- What `CancelTask` returns on a completed task, and whether a follow-up turn on the same context is accepted.
- How a real confirmation card, a 401, a 403, a 429 and a long-running task present.

When the tester's results arrive, this section records what passed, where, with which agent and profile, and when.

## Troubleshooting

Start with the doctor: it prints `meaning:` and `do:` under a failed stage. Then read `m365-issues.jsonl`. This table
lists every issue code the provider and the demo report, in the issue register or under a doctor stage, sorted by code.
`AADSTS` codes are Entra sign-in errors, carried in an issue's `aadstsCode`. Trace-only codes are not listed.

| Code | Who can fix it | Cause | Action |
|---|---|---|---|
| `AADSTS50020` | You (sign-in) | The account is not in the tenant `tenantId` names. | Sign in with a work account of that tenant, or correct `tenantId`. |
| `AADSTS50058` | You (sign-in) | Silent sign-in needs interaction: a first run or an expired session. | Run again and complete the device-code sign-in. |
| `AADSTS50059`, `AADSTS90002`, `AADSTS90019` | You (config) | No tenant was named, or it was not found (a typo, a placeholder, another cloud). | Set `tenantId` to the *Directory (tenant) ID* on the app's *Overview* page. |
| `AADSTS50076`, `AADSTS50079` | You (sign-in) | Multi-factor authentication is required, or must be set up. | Complete or set up MFA in the browser during the sign-in. |
| `AADSTS50105`, `AADSTS53003`, `AADSTS530003` | Tenant admin | User assignment, conditional access or a device policy blocked the sign-in. | Assign the user, or review the policy the Entra sign-in log names. |
| `AADSTS65001`, `AADSTS65004`, `AADSTS650052`, `AADSTS90094` | Tenant admin | Consent for `WorkIQAgent.Ask` is missing or declined. `650052`: Work IQ is not enabled in the tenant. | Grant admin consent on the app registration. For `650052`, a Global Administrator enables Work IQ first ([setup](microsoft365-tenant-setup.md), step 1). |
| `AADSTS65002`, `AADSTS700016` | You (config) | `appClientId` is wrong, in another tenant, or a Microsoft-owned app. | Use your own app registration's client id. |
| `AADSTS7000215`, `AADSTS7000218` | App registration owner | Public client flows are off, so Entra asks for a secret. | Set *Allow public client flows* to **Yes** (*Authentication*, *Advanced settings*). |
| `AADSTS70011`, `AADSTS900144` | You (config) | The scope is wrong, or a sign-in parameter is missing. | Use the default `scope`; check `tenantId` and `appClientId`. |
| `answer.ambiguous` | Maintainers | Several answer candidates, none selected: the case is NOT MEASURED. The evidence document keeps every candidate. | Note the case id and send the files back. Add a raw capture (`--m365-capture-raw`) only if the maintainers ask. |
| `approval.ambiguous_interaction_part`, `approval.call_field_invalid`, `approval.call_not_object`, `approval.card_malformed`, `approval.conflicting_twins`, `approval.twin_unreadable` | Maintainers | The confirmation card has an unknown shape. Nothing was approved. | Send the trace and a raw capture. |
| `auth.aadsts` | See the `AADSTS` rows | The doctor's sign-in failed with the Entra code in `aadstsCode`. | Follow that code's row. |
| `auth.empty_token` | You (sign-in) | The token provider returned an empty token. Nothing was sent. | Sign in again. A host's own provider must return the Work IQ token. |
| `auth.msal` | You (sign-in) | MSAL failed without an Entra code, for example `code_expired`. | Run again and complete the sign-in. |
| `auth.sign_in_not_completed` | You (sign-in) | A sign-in started during a run and did not finish in time. Nothing was sent. | Run `agenteval m365 doctor --stage auth` first; the token is cached. |
| `auth.sign_in_timeout` | You (sign-in) | Doctor only: the code expired, or no token came within `--timeout-seconds`. | Run again and finish the sign-in. Check the route to `login.microsoftonline.com`. |
| `auth.token_acquisition_failed` | You (sign-in); Tenant admin for a consent refusal | The token provider failed during a run. Nothing was sent. | Follow the row of its `aadstsCode`, if any; else sign in again. |
| `budget.exceeded` | You (run limits) | The `MaxTurns` cap was reached; the next send was refused. | Raise `--m365-max-turns` after checking the tenant's usage reporting. |
| `budget.recovered_after_cancel` | You (run limits) | The task outlived `RecoveryBudget`, then finished on its own. | Raise `WorkIqOptions.RecoveryBudget`. |
| `capture.after_complete`, `capture.interrupted`, `capture.truncated`, `capture.write_failed` | You (capture settings) | The raw capture is incomplete or not written. The run is unaffected. `capture.interrupted` and `capture.truncated` are issues only with `--m365-capture-raw`; otherwise they are trace events. | Use a writable `--m365-capture-raw` folder, or raise `MaxCaptureBytes`. Report `capture.after_complete`. |
| `citations.attribution_field_invalid`, `citations.attribution_not_object`, `citations.attributions_not_array`, `citations.attributions_null`, `citations.marker_block_malformed`, `citations.metadata_not_object` | Maintainers | Attributions or citation markers have an unknown shape. | Send the trace and a raw capture. |
| `config.invalid` | You (config) | Doctor `config` stage: the file is missing, unreadable or invalid. | Fix the field or path the message names. |
| `diagnostics.ambiguous_artifact`, `diagnostics.array_not_array`, `diagnostics.array_null`, `diagnostics.container_malformed`, `diagnostics.field_invalid`, `diagnostics.no_object_data_part`, `diagnostics.row_not_object` | Maintainers | A `native118` diagnostic artifact has an unknown shape. | Send a raw capture; use `--m365-profile public` meanwhile. |
| `discovery.untrusted_host` | You (config) | The card points at a host outside `trustedHosts`. No token went there. | Add the host only if it is Microsoft's. |
| `doctor.auth.failed`, `doctor.census.failed`, `doctor.config.failed`, `doctor.discovery.failed`, `doctor.probe.failed`, `doctor.protocol.failed` | As the stage's issue | A doctor stage failed. The message ends with the stage's issue code in brackets. | Follow that code's row; `readiness.md` says what to do. |
| `doctor.canceled` | You | The doctor was stopped during a stage. | Run it again and let it finish. |
| `exception.<type>` | Maintainers | An unclassified exception, such as `exception.NullReferenceException`. | Send the trace. |
| `failure.unknown` | Maintainers | A failure the provider could not classify. | Send the trace. |
| `http.401` | You (sign-in) | The token was rejected: wrong scope, expired, or another app. | Sign in again; check `tenantId`, `appClientId` and `scope`. |
| `http.403` | Tenant admin | A reply that names the required scopes: admin consent is missing. No scope named: the test user is not in the usage-based billing plan (or was added less than 30 minutes ago), or Work IQ is not enabled. | Follow [setup](microsoft365-tenant-setup.md#if-a-step-fails) steps 5, 2 or 1. |
| `http.404` | You (config) | A wrong `agentId` or `endpoint`, or an agent the user cannot reach. | Check `endpoint`, and `agentId` (or leave it out). |
| `http.429` | Work IQ service | Throttled. Reads back off; a send is never retried. | Wait, then run at a lower pace. |
| `http.<status>` | Work IQ service | Another status: `5xx` is a service fault, others a refusal or a proxy. | Note the `request-id`; report it if it repeats. |
| `judge.call_failed` | You (judge settings) | Demo only: the judge call failed, so the case's criteria are NOT MEASURED. | Check `AI_INFERENCE_PROVIDER` and its variables, or pass `--no-judge`. |
| `local.canceled` | You | Your cancellation token stopped the wait. The remote task was not cancelled. | Nothing to fix. |
| `normalize.failed` | Maintainers | The evidence normaliser threw; no turn result was built. | Send the trace and a raw capture. |
| `protocol.malformed` | Maintainers | A reply is not A2A 1.0 JSON: a 0.3 endpoint or a gateway page. A send answered this way is Indeterminate: the service may have created a task. | Send a raw capture. |
| `protocol.version_mismatch` | Maintainers | The reply has the A2A 0.3 shape, or the card offers no A2A 1.0 JSON-RPC interface. A send answered this way is Indeterminate. | Report it with the trace. |
| `reduce.frame_malformed`, `reduce.interrupted`, `reduce.<deviation>` | Maintainers | The task stream deviates from A2A 1.0 (for example `reduce.message.task_id_mismatch`) or was cut off. | Send the trace and a raw capture. |
| `rpc.content_type_not_supported` | You (config) | The agent refused the requested output modes. | Use `--m365-profile public`. |
| `rpc.error`, `rpc.extended_card_not_configured`, `rpc.extension_support_required`, `rpc.internal_error`, `rpc.invalid_agent_response`, `rpc.push_notification_not_supported`, `rpc.unsupported_operation` | Work IQ service | The service answered with this JSON-RPC error. | Report it with the `request-id`. |
| `rpc.invalid_params`, `rpc.invalid_request`, `rpc.method_not_found`, `rpc.parse_error` | Maintainers | The service rejected the request, or the interface is not the A2A 1.0 endpoint. | Report it with the trace. |
| `rpc.task_not_cancelable` | Nobody | The task had already ended; `CancelTask` changed nothing. | Nothing to fix. |
| `rpc.task_not_found` | Maintainers | The service does not know the task id: not accessible, expired or purged. | Do not resend; report it with the `request-id`. |
| `rpc.version_not_supported` | Maintainers | The service rejected A2A 1.0 (JSON-RPC `-32009`). | Report it with the trace. |
| `stream.capture_failed`, `subscribe.capture_failed` | You (capture settings) | A stream's raw capture could not be closed. The frames were delivered. | Check `RawCaptureDirectory`. |
| `stream.id_mismatch`, `subscribe.id_mismatch` | Maintainers | A stream frame carried another task or context id. | Send the trace and a raw capture. |
| `transport.connect`, `transport.dns`, `transport.error`, `transport.invalid_response`, `transport.io`, `transport.proxy`, `transport.response_ended`, `transport.tls` | Your network | A DNS, connection, TLS or proxy failure, or a cut-off reply. | Check the route and proxy to `workiq.svc.cloud.microsoft`. |
| `transport.timeout` | Your network | No response within `SendTimeout` (300 s). | Retry; in code, raise `SendTimeout`. |
| `turn.abandon_not_quiesced` | You (run limits) | An abandoned turn did not stop within `RecoveryBudget`; the task may still run. | Start a new conversation. Raise `RecoveryBudget` if it repeats. |
| `turn.auth_required` | You (connector sign-in) | The agent needs a connector sign-in this provider cannot complete. | Sign in to that connector in Microsoft 365 Copilot Chat, then rerun the case. |
| `turn.canceled`, `turn.failed`, `turn.rejected` | Per the issue's `category` | The turn ended without an answer. The issue keeps the failure's category and codes. | Follow the row of its status or code (a 403 is `http.403`). Do not resend blindly. |
| `turn.indeterminate` | Per the issue's `category` | The remote state is unknown: a timeout, a drop, or a local budget ran out. Nothing was resent. | Find the task id in the trace; raise the budget the cause names. |
| `turn.input_required` | You (the case) | The agent paused for a confirmation or input. Nothing was approved. | Rephrase the case as read-only. |

Symptoms without a code:

- **A device-code message appears and the run waits:** a first sign-in, or an expired cache. Sign in as the test user.
- **`Error: …` before anything is sent:** a consent, option or config refusal. The message names the flag or field.
- **`eval` exits `11` and no case failed:** a case was not measured. Read its `error` in the JSON export.
- **`bench` exits `11` with `INCOMPLETE`:** a judge call failed mid-scan, or the scan ran out of time.
- **"being used by another process" when you read a JSONL file:** the run still has it open.

## See also

- [The demo project](../samples/AgentEval.Microsoft365Demo/README.md): readiness, one prompt and a benchmark in one
  command, and one run folder to send back.
- [Microsoft 365 tester guide](microsoft365-tester-guide.md) and its
  [results template](microsoft365-tester-RESULTS-TEMPLATE.md): the live validation an external tester runs.
- Samples in `samples/AgentEval.Samples/Microsoft365/`: `dotnet run -- 109` (Hello World) and `dotnet run -- 110` (Live
  Walkthrough), run from `samples/AgentEval.Samples/`. They need `M365_CONFIG_PATH` and
  `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`, else they stop with exit `11`.
- CLI reference: [`agenteval eval`](cli.md#agenteval-eval), [`agenteval bench`](cli.md#agenteval-bench),
  [`agenteval m365 doctor`](cli.md#agenteval-m365-doctor) and [exit codes](cli.md#exit-codes).
- [Copilot Studio](copilot-studio.md): the sibling connector, with the same config, consent and test-hook pattern.
- [AEF Evidence Format](aef.md), [LLM as a Judge](llm-as-judge.md), the [Benchmarks Guide](benchmarks.md) and
  [the `.agenteval/` workspace](agenteval-workspace.md).
