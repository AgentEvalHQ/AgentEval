# Microsoft 365 (`--sut microsoft365`)

## New to this? Start here

**The problem:** you have an agent inside Microsoft 365 — Microsoft 365 Copilot itself, or a declarative agent your
tenant publishes — and you want to run a benchmark against it the way you would against any other agent: send
prompts, grade the answers with a judge, keep the evidence. But a Microsoft 365 agent has no plain HTTP endpoint you
can point a chat client at. It needs a real Entra sign-in as a real user, it speaks the Work IQ A2A protocol, and
every prompt runs against that user's live mailbox, files and calendar.

**What this connector does:** it is a client of a Microsoft 365 agent over **Work IQ A2A 1.0**. It sends one
benchmark prompt per turn, waits for the task to complete, collects the externally observable result — the final
answer, its citations, and whatever diagnostic artifacts the tenant emits — and hands that to AgentEval as an
ordinary `IEvaluableAgent`. The same agent reaches the in-code API, `agenteval eval` for your own dataset, and the
`agenteval bench` families for the standard benchmarks, through the `--sut microsoft365` target on the seam the
Copilot Studio connector already uses.

**The one honest trade-off — evidence by availability:** the connector only ever sees what the service returns.
Whether a tool fired, what was retrieved, which skill ran — none of that is visible unless the tenant emits it, and
by default the connector does not even ask for it. So every evidence channel carries an availability label
(`NotRequested`, `NotProvided`, `Provided`, …) and nothing is inferred from silence. An empty tool list means "no
tool telemetry was observed", never "no tool was used". The [evidence model](#the-evidence-model) section says
exactly what each label means.

Just want the fastest path? Run the [demo project](#the-demo-project-samplesagentevalmicrosoft365demo) — one command
checks the connection, sends one prompt, runs a small benchmark and saves everything in one folder. For your own code
read [Using it directly in code](#using-it-directly-in-code-no-cli); for the CLI jump to
[Running a custom benchmark](#running-a-custom-benchmark-agenteval-eval). If you are the external tester with a
tenant, start with the demo, then the [tester guide](microsoft365-tester-guide.md).

---

> **Status: Experimental.** The provider (`src/AgentEval.Microsoft365/`), its credential-free tests, the CLI target
> and the readiness check (`agenteval m365 doctor`) are implemented. **The maintainers have not run the live path:
> they have no Microsoft 365 tenant, and an external tester validates it on theirs** with the
> [tester guide](microsoft365-tester-guide.md). What is verified offline: config loading and every validation rule;
> the trust policy; the redaction policy; the trace and issue sinks; the task reducer, answer selector and evidence
> normaliser against synthetic A2A 1.0 fixtures (cards, JSON-RPC envelopes, SSE streams); the agent lifecycle with a
> fake conversation; the readiness check against a fake HTTP handler and token provider; and the whole CLI path
> (`eval`, the `bench` families and `m365 doctor`) through the credential-free seams. What is **not** verified by the
> maintainers: the MSAL device-code sign-in and token cache, the real card, the real wire shapes of a completed,
> paused or failed task, the citation format a live agent emits, and whether the experimental diagnostic modes are
> accepted. See [What's verified vs. what still needs a live check](#whats-verified-vs-what-still-needs-a-live-check).
> No claim on this page means "tested against a tenant".

## What it is

`microsoft365` is a built-in `--sut` target for `agenteval eval` and `agenteval bench` (`redteam` is excluded in
this phase). Selecting it swaps the default `--endpoint`/`--azure` agent for a purpose-built one that:

- owns its own CLI flags (`--m365-config`, `--m365-max-turns`, `--m365-profile`, `--m365-capture-raw`,
  `--m365-verbose`, `--m365-trace`) and reads the common `--i-understand-live-side-effects` consent flag,
- validates everything it can **before any network call**: consent, config present, turn cap not negative,
  profile valid, then every field of the config file,
- signs in as a real user with MSAL device-code authentication (a public-client app registration, no secret),
- discovers the agent card, selects the JSON-RPC 1.0 interface, and refuses any host outside `trustedHosts`,
- runs each prompt as one blocking `SendMessage` turn (`A2A-Version: 1.0` on every request), reconciles a
  still-running task with bounded `GetTask` polling, and **never resends a message automatically**,
- returns an `AgentResponse` only for a completed task; every other outcome is a typed exception that carries the
  full observation,
- normalises what it saw into a versioned evidence document (`agenteval.microsoft365.turn-evidence/v1`) with an
  availability label per channel,
- writes a redacted JSONL trace and an issue register for every run, so a failure can be located by stage without
  reading code,
- comes with a staged readiness check, [`agenteval m365 doctor`](#readiness-check-agenteval-m365-doctor), that proves
  the config, the sign-in, the card and the protocol handshake before any prompt is sent.

Source: `src/AgentEval.Microsoft365/` — `Microsoft365Config.cs`, `Microsoft365AgentFactory.cs`, `WorkIqOptions.cs`,
`WorkIqEvaluableAgent.cs`, `WorkIqConversation.cs`, `WorkIqExceptions.cs`, `Auth/`, `Protocol/`, `Capture/`,
`Evidence/`, `Diagnostics/`, `Readiness/`. The project has no Microsoft Agent Framework dependency; the A2A .NET SDK
(`A2A` 1.0.0-preview2) sits behind the provider's own seams.

## The demo project (`samples/AgentEval.Microsoft365Demo`)

A console app (.NET 10) that takes a tester from a config file to one zipped folder of evidence. It uses the provider's
public API only, and never falls back to a mock target:

```bash
cp samples/datasets/m365-config.template.json workiq.json        # put your tenantId and appClientId in it
dotnet run --project samples/AgentEval.Microsoft365Demo -- --readiness-only                        # no prompt is sent
dotnet run --project samples/AgentEval.Microsoft365Demo -- --i-understand-live-side-effects --zip  # prompt + benchmark
```

1. **Readiness** — `Microsoft365ReadinessCheck` (the same stages as [`agenteval m365 doctor`](#readiness-check-agenteval-m365-doctor)).
2. **One prompt** — `--prompt`, one turn in a fresh context. Needs consent.
3. **A small benchmark** — by default 5 read-only cases from `samples/datasets/m365-smoke.json` (`--dataset` for
   others) through `MAFEvaluationHarness` over the agent `BuildLive` returns. Deterministic checks always run (turn
   completed, answer not empty, response time recorded, evidence availability recorded); the judge criteria only when
   a judge provider is configured (`InferenceProviderEnvironment`) and `--no-judge` is not given, otherwise they are
   `NOT MEASURED`. A turn that did not complete is an `ERROR` row, never a graded zero. Needs consent.
4. **One run folder**, whatever the screen shows: `summary.md`, `console.log`, `trace.jsonl`, `issues.jsonl`,
   `readiness.md` / `.json`, `turns.jsonl`, `evidence/<caseId>.json`, `results.md` / `.json`, `raw/` with
   `--capture-raw`, and a zip with `--zip`.

It targets Work IQ's **default agent** (Microsoft 365 Copilot's built-in agent) unless `--agent-id` names a declarative
agent. `--verbosity quiet|normal|detailed|diagnostic` sets how much reaches the screen: stage lines, a line per turn and
per issue, every trace event, and a detail block per issue (stage, category, codes, `request-id`, cause, action). Exit
codes: `0` all passed and measured · `1` a stage failed (readiness, or the one prompt did not complete) or a case
failed a check · `2` usage · `3` runtime error or a file not written (the folder is still written) · `11` nothing
failed but something was not measured (no consent past readiness, no judge, a benchmark turn that did not complete, a
case stopped by `--max-turns`, no config file).

Every option, the run-folder table and how to redact are in the
[demo README](../samples/AgentEval.Microsoft365Demo/README.md). Opened on its own in VS Code, the folder brings its own
GitHub Copilot setup (`.github/`: an `m365-demo` agent, skills for setup, customisation and triage, and prompt files).
The offline tests in `samples/AgentEval.Microsoft365Demo.Tests` drive it through the real provider with a fake HTTP
handler and a fake token provider.

## Using it directly in code (no CLI)

The provider lives in its own project, `AgentEval.Microsoft365`, so the A2A SDK and MSAL are never forced on
consumers who do not use it. It is **not published to NuGet yet**: build it from source and add a project reference
to `src/AgentEval.Microsoft365/AgentEval.Microsoft365.csproj`.

```csharp
using AgentEval.Core;                  // AgentResponse, IEvaluableAgent
using AgentEval.Microsoft365;          // Microsoft365Config, Microsoft365AgentFactory, WorkIqOptions, Evidence()
using AgentEval.Microsoft365.Evidence; // Microsoft365TurnEvidence, EvidenceAvailability

var config = Microsoft365Config.Load(new FileInfo("workiq.json"));   // loads + validates; the file holds no secret

// iUnderstandLiveSideEffects is required and has no default. The delegated WorkIQAgent.Ask permission covers
// reads AND writes of the signed-in user's Microsoft 365 data, and a prompt can trigger a real action that
// cannot be sandboxed. Pass true only for a non-production tenant and identity.
await using WorkIqEvaluableAgent agent = Microsoft365AgentFactory.BuildLive(
    config,
    iUnderstandLiveSideEffects: true,
    options: new WorkIqOptions
    {
        MaxTurns = 10,                                                      // turns are counted, not metered
        ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,  // the default; ContinueContext for multi-turn
    });

AgentResponse reply;
try
{
    reply = await agent.InvokeAsync("In one sentence, what can you help me with? Cite nothing.");
}
catch (WorkIqTurnNotCompletedException ex)
{
    // Only a completed turn becomes an AgentResponse. Every other outcome lands here with the typed result:
    // InputRequired, AuthRequired, Failed, Rejected, Canceled or Indeterminate. No partial text is returned.
    Console.Error.WriteLine($"not completed: {ex.TurnResult.Outcome} task={ex.TurnResult.Task?.TaskId ?? "(none)"}");
    if (ex.TurnResult.Outcome == WorkIqTurnOutcome.InputRequired)
    {
        // ex.TurnResult.PendingInteraction describes the confirmation card. Nothing was approved.
    }

    return;
}

Console.WriteLine(reply.Text);

Microsoft365TurnEvidence? evidence = reply.Evidence();   // AdditionalProperties[Microsoft365Evidence.Key]
if (evidence is not null)
{
    var sources = evidence.CitationSources;
    Console.WriteLine($"citation sources: {sources.Availability} ({(sources.Items.IsDefaultOrEmpty ? 0 : sources.Items.Length)})");
    Console.WriteLine($"tool executions:  {evidence.ToolExecutions.Availability}");   // NotRequested under PublicAnswerOnly
}
```

What the pieces are:

- `Microsoft365Config.Load(FileInfo)` reads and validates the JSON file (see [the config table](#authoring-the-config-json)).
  `Validate()`, `ResolveProfile()`, `ResolveEndpointUri()` and `ResolveCardUri()` are pure; nothing touches the
  network.
- `Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects, options, tokenProvider, onTurn, innerHandler)`
  builds the authenticated transport and returns a `WorkIqEvaluableAgent` (`IEvaluableAgent`,
  `ISessionResettableAgent`, `IAsyncDisposable`). It makes **no network call**; sign-in and card discovery run on
  the first turn. `tokenProvider` defaults to `WorkIqDeviceCodeTokenProvider` (MSAL public client, silent then
  device code, OS-encrypted persisted cache); pass your own `IWorkIqTokenProvider` to own the sign-in policy.
  `onTurn` receives every `WorkIqTurnResult`, completed or not, before the agent returns or throws — use it to
  persist the evidence of interrupted turns.
- `WorkIqOptions` carries the timeouts (`SendTimeout` 300 s, `TurnDeadline` 10 min, `RecoveryBudget` 2 min,
  `RecoveryPollInterval` 2 s), the `ConversationPolicy`, `MaxTurns` (0 = no cap), the `ContentPolicy`
  (`SelectedContent` by default; `MetadataOnly` and `FullEvidence` opt-in), `RawCaptureDirectory`, the capture bounds
  (`MaxCaptureBytes` 8 MiB, `MaxCaptureFrames` 10 000), `CancelRemoteTaskOnTimeout` (off: a local stop never implies
  a remote cancel), `ProfileOverride`, and the diagnostics settings (`Verbose`, `TraceFile`, `IssueFile`,
  `DiagnosticsDirectory`, `RunId`, and `TraceSinks` / `IssueSinks`: extra `IWorkIqTraceSink` / `IWorkIqIssueSink`
  instances that receive every event and issue the JSONL files receive, already stamped and redacted; the caller owns
  their lifetime). `Validate()` rejects any negative value and a null sink entry.
- `reply.Evidence()` (extension in `AgentEval.Microsoft365`) returns the `Microsoft365TurnEvidence` carried under
  `AgentResponse.AdditionalProperties[Microsoft365Evidence.Key]`, or `null` when the response did not come from
  this provider. `reply.TryGetEvidence(out var evidence)` is the non-throwing form. `AgentResponse.RawMessages`,
  `TokenUsage` and `ModelId` stay `null`: the service reports none of them, and nothing is invented.
  `AgentResponse.FinishReason` is `completed` (`WorkIqEvaluableAgent.CompletedFinishReason`), or
  `completed-no-answer` (`CompletedNoAnswerFinishReason`) when the task completed but no answer could be selected —
  several answer candidates and none named as the answer (`Ambiguous`), or none at all (`None`). The text is then an
  empty string that is **not** the agent's answer, `AdditionalProperties[Microsoft365Evidence.NoAnswerSelectedKey]`
  is `true`, and the evidence keeps every candidate. `eval --sut microsoft365` prints a
  `[m365] WARNING … the turn COMPLETED but no answer was selected …` line for such a case, even without
  `--m365-verbose`.
- `WorkIqTurnNotCompletedException.TurnResult` is the immutable `WorkIqTurnResult`: `Outcome` (`WorkIqTurnOutcome`),
  `Transport` (`TransportOutcome`), `AnswerCompleteness` (`OutputCompleteness`), `Telemetry`, `Task`
  (`WorkIqTaskHandle` with `TaskId` and `ContextId`), `AnswerText`, `Evidence` (always present), `PendingInteraction`,
  `Failure` (`WorkIqFailure`: `Category`, `SafeMessage`, `Certainty`, `Operation`, `HttpStatus`, `RpcErrorCode`,
  `RemoteTaskState`, `RequestId`, …), `Timing` and `Identity`. The exception message carries ids and categories
  only — never a prompt, an answer or a token.
- Two more exceptions: `WorkIqTurnCanceledException` (an `OperationCanceledException`; your token stopped the local
  wait, the remote state is on `TurnResult`) and `WorkIqTurnBudgetExceededException` (a `FatalEvaluationException`;
  the `MaxTurns` cap was reached **before** the send that would cross it, so every batch loop stops once).
  `WorkIqProtocolException` carries a `WorkIqFailure` when the transport fails before any turn result exists.
- Multi-turn in code: set `ConversationPolicy.ContinueContext`, keep one agent per conversation, and call
  `ResetSessionAsync()` to start a new context (local only; it cancels nothing remotely). Concurrent turns on one
  agent are rejected with `InvalidOperationException`.
- The credential-free seam: `Microsoft365AgentFactory.FromConversation(IWorkIqConversation conversation, string name, …)`
  wraps any conversation implementation — a fake in tests — as the same `WorkIqEvaluableAgent`. It is test-only as a
  target: the CLI never substitutes it for a live agent.

For a judge-graded batch use `MAFEvaluationHarness` (namespace `AgentEval.MAF`) with a judge `IChatClient` and
`RunBatchAsync`, exactly as for any other agent; see [LLM as a Judge](llm-as-judge.md).

The readiness check is public too (`AgentEval.Microsoft365.Readiness`):
`Microsoft365ReadinessCheck.RunAsync(config, configFile, options)` takes exactly one of a `Microsoft365Config` and a
config `FileInfo`, runs the stages `ReadinessCheckOptions.Stages` selects (null = the default set; `Probe` and
`IUnderstandLiveSideEffects` add the probe and the census), and returns a `ReadinessReport` (`Stages`, `Verdict`,
`AllPassed`, `AnyFailed`, `AnySkippedForConsent`, `TraceFile`, `IssueFile`, `ConfigSha256`). Runtime failures become
failed stages, not exceptions. `ReadinessCheckOptions.TraceSinks` / `IssueSinks` stream every event and issue to your
own sinks while the check runs (the demo project uses them to print the trace live). For hosts and tests, the overload
`RunAsync(config, configFile, options, innerHandler, tokenProvider, cancellationToken)` runs the check over your own
innermost `HttpMessageHandler` and `IWorkIqTokenProvider` (null for either keeps the default), the same seams
`BuildLive` takes.
`ReadinessReportRenderer.ToMarkdown`, `ToJson` and `WriteConsole` render it exactly as
the CLI does. To know the trace and issue paths before a live run, mint the run id first:
`new WorkIqOptions { RunId = Microsoft365Diagnostics.NewRunId() }`, then `Microsoft365Diagnostics.Describe(options)`
returns the `TraceFile` and `IssueFile` the run will write (namespace `AgentEval.Microsoft365.Diagnostics`).

## Prerequisites

- **Work IQ enabled** in the tenant by an administrator, and a **Microsoft 365 Copilot licence** for the test user.
  Without both, the service answers 403 and the connector reports `Forbidden`.
- An **Entra app registration configured as a public client**: in the app's *Authentication* blade set
  *Allow public client flows* to **Yes**. There is no client secret anywhere; device-code sign-in needs this setting
  (otherwise Entra returns `AADSTS7000218`).
- The **delegated permission `WorkIQAgent.Ask`** (`api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`) on that app,
  with **admin consent** granted. Without consent the first sign-in fails with `AADSTS65001`. The permission covers
  reads **and writes** of the signed-in user's Microsoft 365 data.
- The **Entra tenant id** and the app's **client id** for the config file.
- A **non-production identity**: a test user in a test tenant, with a small synthetic corpus (for example a document
  named "Demo Q3 Planning"). The consent flag below exists because there is no sandbox.
- Optional: the **agent id** of a declarative agent the test user can reach. `null` targets the default Work IQ
  agent (Microsoft 365 Copilot). Word, Excel and PowerPoint agents are bound to their application UI and do not
  answer usefully headlessly.
- The **.NET 10 SDK** (the repository pins it in `global.json`) to build the CLI from source.

## Authoring the config JSON

`--m365-config <file.json>` (and `Microsoft365Config.Load` in code) point at a connection file. It carries **no
secret** — the token is acquired at run time by MSAL device-code sign-in (first run: a browser sign-in with a
printed code; later runs: silently from a persisted, OS-encrypted cache) and is never stored in this file.

| Field | Required | Default | Notes |
|---|:--:|---|---|
| `tenantId` | yes | — | The Entra tenant id the app registration lives in. |
| `appClientId` | yes | — | The app registration's client id — a **public client**, never a secret. |
| `endpoint` | no | `https://workiq.svc.cloud.microsoft/a2a/` | The Work IQ A2A service root. Must be an absolute `https` URL without query string or fragment; its host must be in `trustedHosts`. |
| `agentId` | no | `null` (the default Work IQ agent) | An opaque id such as `U_abc.def`, passed as-is, never parsed. The card is discovered at `{endpoint}/{agentId}/.well-known/agent-card.json`. Must be one path segment: no `/`, `\`, `?`, `#`, whitespace or control characters. |
| `scope` | no | `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` | The delegated token scope. The audience is Work IQ, not Microsoft Graph. |
| `agentName` | no | — | Display name for reports. `DisplayName` falls back to `agentId`, then to `Work IQ default agent`. |
| `diagnosticProfile` | no | `PublicAnswerOnly` | `PublicAnswerOnly` or `Native118Diagnostics` (case-insensitive). See [Diagnostic profiles](#diagnostic-profiles). The CLI's `--m365-profile` overrides it. |
| `timeZone` | no | — | An IANA id such as `Europe/Zurich`. When set, every user message carries `metadata.Location { timeZone, timeZoneOffset }` with a DST-aware offset for the evaluation instant, so date prompts are deterministic. |
| `trustedHosts` | no | `["workiq.svc.cloud.microsoft"]` | The only hosts a bearer token may be sent to (card discovery and every advertised interface URL). Must include the endpoint's host; a blank entry is rejected. |

Property names are matched case-insensitively; the convention is camelCase. A ready template is
`samples/datasets/m365-config.template.json`:

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

If a required field is missing, `Microsoft365Config.Load` throws before anything else runs:

```text
Microsoft 365 config is missing required field(s): tenantId, appClientId. Required: tenantId, appClientId
(endpoint and scope have defaults and must not be blank when set).
```

A missing file, an unreadable file, malformed JSON and every bad field value each produce their own clear,
field-scoped `InvalidOperationException` — see [Troubleshooting](#troubleshooting) for the exact texts. A value
still reading `REPLACE-WITH-…` (the template's placeholders, in any field) is refused, naming the field:

```text
Microsoft 365 config field 'tenantId' still holds the template placeholder 'REPLACE-WITH-ENTRA-TENANT-GUID': replace
it with the Directory (tenant) ID from the app registration's Overview page. Nothing was sent; a placeholder would
only fail later, at sign-in.
```

Validation does not check that `tenantId` and `appClientId` name a real tenant and app; that is the sign-in's job.
The doctor's `config` stage adds a warning when `tenantId` is neither a GUID nor a domain name (the sign-in would
fail with `AADSTS90002`) or `appClientId` is not a GUID (`AADSTS700016`).

## The consent flag and why it's required

`--i-understand-live-side-effects` is checked **first** — before the config file, before the turn cap, before
anything that could lead to a network call. Omit it and the command refuses immediately, says so and sends nothing:
`eval` exits `3` (`Error: --sut microsoft365 drives a LIVE Microsoft 365 agent … (nothing was sent).`), a `bench`
family exits `2`. `agenteval m365 doctor` reads the same flag for its probe and census only (see
[Readiness check](#readiness-check-agenteval-m365-doctor)). In code the same acknowledgment is the
`iUnderstandLiveSideEffects` parameter of `BuildLive`, which has no default; passing `false` throws:

```text
BuildLive drives a LIVE Microsoft 365 agent over Work IQ as the signed-in user: the delegated WorkIQAgent.Ask
permission covers reads AND writes of that user's Microsoft 365 data, and a prompt can trigger REAL actions that
cannot be sandboxed. Pass iUnderstandLiveSideEffects: true only after confirming config points at a NON-PROD tenant
and identity (nothing was sent).
```

The reasoning is structural: the agent acts as the signed-in user, with that user's permissions, on that user's real
mailbox, files and calendar. A benchmark prompt such as "schedule a meeting" can make the agent propose a real action.
The connector never approves a confirmation card and never resends a message, but it cannot undo what the agent
does with a prompt it accepted. The flag is your acknowledgment that the config points at a **non-production**
identity, not a promise the tool can enforce.

For `eval` and the `bench` families the flag is registered once per verb by the shared `--sut` resolver (it was
hoisted from the Copilot Studio target; no behaviour changed for that target), so `copilot-studio` and `microsoft365`
share one flag.

## Readiness check: `agenteval m365 doctor`

The first thing to run with a new tenant, before `eval --sut microsoft365`. It checks the connection stage by stage
and tells whoever reads the report which stage failed, why, and what to do next — without reading code. The
[tester guide](microsoft365-tester-guide.md) walks through it step by step.

```bash
agenteval m365 doctor --m365-config ./workiq.json                                         # no prompt is sent
agenteval m365 doctor --m365-config ./workiq.json --probe --i-understand-live-side-effects # one real, read-only prompt
```

Options (exactly as `agenteval m365 doctor --help` lists them):

| Option | Default | What it does |
|---|---|---|
| `--m365-config <file>` | required | The connection JSON ([the config table](#authoring-the-config-json)). |
| `--stage <stage>` | `all` | `config`, `auth`, `discovery`, `protocol`, `probe`, `census` or `all`. `all` runs `config`, `auth`, `discovery` and `protocol`, plus `probe` and `census` with `--probe`. A single stage runs with `config` (and `census` with `probe`); a network stage acquires its own token and card as needed. |
| `--probe` | off | Adds the probe and the census. They need `--i-understand-live-side-effects`; without it both are SKIPPED, nothing reaches the agent, and the command exits `11`. |
| `--i-understand-live-side-effects` | off | Consent for the probe and the census. The other stages run without it. |
| `--continue` | off | Runs the remaining stages after a FAIL instead of stopping (a stage still needs a config that loaded). |
| `--report <dir>` | see below | The folder for `readiness.md`, `readiness.json` and the trace and issue files. |
| `--m365-profile public\|native118` | `public` | The profile of the auth, discovery, protocol and probe stages. With `native118` the probe already runs under the experimental profile and the census sends no second prompt. |
| `--m365-capture-raw <dir>` | off | Retains the raw A2A bytes and stream frames under `<dir>` (may contain tenant data). |
| `--m365-verbose` | **on** | Prints every trace event to stderr. `--m365-verbose false` turns it off; the trace and issue files are written either way. |
| `--m365-trace <file>` | in the report folder | Writes the JSONL trace there instead. |
| `--timeout-seconds <n>` | `120` | The most one stage may take for its network calls: also the HTTP timeout and the probe's turn deadline. A device-code sign-in is not cut short by it: once the sign-in prompt is shown, the `auth` stage waits until the code expires (about 15 minutes). |

The stages, in order. The check stops at the first FAIL unless `--continue`; a stage that does not run is SKIPPED
with its reason, never a pass.

| # | Stage | What it does | Evidence it records | PASS means |
|---|---|---|---|---|
| 1 | `config` | Loads and validates the file. No network. | The file path and SHA-256, the endpoint, card URI, agent (or "default Work IQ agent"), display name, tenant id, app id, scope, profile and where it came from, trusted hosts, time zone, the config's identity SHA-256, the adapter and A2A versions, the OS, .NET and machine time zone. | Every validation rule passed (a template placeholder fails it). A warning when `tenantId` is neither a GUID nor a domain name, or `appClientId` is not a GUID, or the profile is `native118`. |
| 2 | `auth` | Gets a delegated token: silently from the OS-encrypted cache, else by device-code sign-in (the MSAL sign-in message is printed; it is the one auth text shown, and the trace keeps it with the code masked). Once the prompt is shown the stage waits until the code expires, not just `--timeout-seconds`. | The token cache file and whether it existed, whether a token was acquired and how (silently or by sign-in), its expiry, the tenant id in the token and whether it matches the config, the masked account, the scopes; on failure the AADSTS code, the MSAL error code, the Entra HTTP status and correlation id. The token never appears. | A token for the Work IQ scope was acquired. |
| 3 | `discovery` | Fetches the agent card through the same handler chain a live run uses and selects the A2A 1.0 JSON-RPC interface on a trusted host. | The card URI, trusted hosts, HTTP status, `request-id`, byte count, card name and version, every advertised interface (binding, version, host), the one chosen, `capabilities.streaming` and `capabilities.pushNotifications`, the skills count, the card SHA-256. | A JSON-RPC 1.0 interface on a trusted host was selected, and the token was accepted for the card. |
| 4 | `protocol` | Sends `GetTask` for a random task id (`doctor-<guid>`). | The endpoint, HTTP status, JSON-RPC code, `request-id`, elapsed. | The service answered JSON-RPC `-32001` (TaskNotFound): it speaks A2A 1.0 JSON-RPC and accepted the token. (A task returned for the random id also passes, with a warning.) |
| 5 | `probe` | Opt-in, needs consent: one turn with the prompt `Reply with the single word READY.` in a fresh context. Never answers a pause. | The prompt, profile, outcome, transport, task and context ids, the task states observed, the answer's completeness, length and first 80 characters, attribution and citation-marker counts, elapsed, `GetTask` recovery polls, `request-id`; on failure the HTTP status, JSON-RPC code and failure. | The turn completed. An empty answer or an answer that is not READY adds a warning. |
| 6 | `census` | After a probe, needs consent: under the `public` profile it sends the same prompt **a second time** under `native118`; then it calls `CancelTask` on a completed task. | Whether the diagnostic modes were accepted (or refused, with the reason), whether *Developer Info* and *Retrieval Info* artifacts appeared, the citation marker format, what `CancelTask` returned, the card's streaming and push-notification declarations. | Informational: it passes whether the tenant accepts or refuses `native118`; it fails only when it could not be measured. |

**Output.** The console view prints one line per stage — `[PASS]`, `[FAIL]` or `[SKIPPED]`, the stage, a one-line
summary — and, under a failed stage, `issue:` (category, code, AADSTS code, HTTP status, JSON-RPC code), `meaning:`
and `do:`; then `Result: PASS`, `FAIL` or `INCOMPLETE` with one sentence, the report folder and the file paths. The
report folder (`--report`, default `.agenteval/m365/doctor-<yyyyMMdd-HHmmss>Z/` in a workspace — the first
`.agenteval/` found walking up from the current directory — else `<temp>/agenteval-m365/doctor-<yyyyMMdd-HHmmss>Z/`)
holds:

- `readiness.md` — the run facts, the stage table (stage · result · summary · what it means · what to do), the
  evidence of every stage, and what to send back;
- `readiness.json` — the same, schema `agenteval.m365.readiness/v1`;
- `m365-trace-<run id>.jsonl` and `m365-issues.jsonl` — the trace and the issue register of this run (unless
  `--m365-trace` points the trace elsewhere).

Every stage is traced on the `Doctor` stage as `doctor.<stage>.start`, then `.pass`, `.fail` or `.skipped`, next to
the events of the seams it drives, and every failed stage is registered as a `doctor.<stage>.failed` issue. The two
replies the doctor provokes on purpose — TaskNotFound on the random id and TaskNotCancelable on the finished task —
are kept out of the issue register; the trace records them and says so. Nothing written contains a token, an auth
code or the `Authorization` header; the trace never carries the prompt or the answer.

**Exit codes.** `0` every requested stage passed; `1` a stage failed; `11` nothing failed but a requested stage did
not run — `--probe` without `--i-understand-live-side-effects`; `2` usage (missing `--m365-config`, an unknown
`--stage` or `--m365-profile` value, `--timeout-seconds` below 1), nothing created and nothing sent; `3` the report
folder or files could not be written, or the check could not start.

## Running a custom benchmark (`agenteval eval`)

```bash
agenteval eval --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects \
  --dataset samples/datasets/m365-smoke.json --m365-max-turns 10 \
  --judge https://<judge-endpoint>/v1 --judge-model <judge-model> \
  --subject agent:m365-default-copilot --aef ./m365-results/aef --aef-content on \
  --format json -o ./m365-results/m365-smoke.json
```

What the command does, in order:

1. Refuses if the dataset file does not exist, then if `--i-understand-live-side-effects` is absent.
2. Refuses if `--m365-config` is absent, if `--m365-max-turns` is negative, or if `--m365-profile` is not `public`
   or `native118`.
3. Loads and validates the config (fail fast, before anything is sent). Each refusal prints `Error: …` and exits
   `3`; nothing was sent.
4. Prints a one-line note to stderr if you also passed `--temperature`, `--max-tokens`, `--system-prompt` or
   `--system-prompt-file` — the target configures its own model, so they are ignored (not printed with `--quiet`).
5. Prints the target badge to stderr: *Note: --sut microsoft365 observes the agent's final answer and the evidence
   the tenant emits; tool and retrieval evidence is reported as NotRequested / NotProvided / Provided per channel,
   never inferred.* The same line names the trace and issue file paths of this run (`Trace: … · Issues: …`), the
   experimental-profile statement when `native118` is in effect, and the turn cap when one is set.
6. Builds the live agent — still no network call.
7. Runs the dataset. The **first** turn is where sign-in and card discovery happen; with no cached token the
   console prints the device-code message (open the URL, enter the code). Every case runs in a fresh server
   context (`ConversationPolicy.FreshContextPerInvocation`; the CLI always uses it), so cases are independent even
   though the batch reuses one agent.

The dataset is the ordinary `eval` dataset (`id`, `input`, `expected`, `evaluation_criteria`, `tags`,
`passing_score`; see [`agenteval eval`](cli.md#agenteval-eval)). `samples/datasets/m365-smoke.json` is an 8-case
example: identity disclosure, capability summary, grounded summary with citation, explicit date reasoning,
out-of-scope live data, prompt-injection resistance, PII restraint and read-only action restraint. Keep prompts
read-only and give explicit dates instead of "today".

The judge: `--judge <endpoint> --judge-model <model>` is an OpenAI-compatible endpoint; its key is `--api-key`
(falls back to `OPENAI_API_KEY`, which keeps it out of the shell history). Pass `--judge-model` explicitly — its
default is the target's name, which is not a model. Without `--judge`, `expected` degrades to a case-insensitive
contains check and `evaluation_criteria` is ignored, so a run without a judge proves reachability only.

Exit codes are `eval`'s, unchanged: `0` every case passed; `1` a case failed; `2` usage; `3` runtime or
configuration error (including a refused consent flag or config, and the turn cap being hit); `11` nothing failed
but a case has no verdict. **A turn that did not complete is an error row** (`InputRequired`, `AuthRequired`,
`Failed`, `Rejected`, `Canceled` or `Indeterminate` in its error, `Work IQ turn did not complete: outcome=…`), never
a graded zero: the run exits `11` when nothing else failed. The JSON export (`--format json`) carries each case's
error and answer; the Markdown export shows every case's status but the error only of a case that failed with a
verdict. See
[Exit codes](cli.md#exit-codes).

Evidence: `--aef <dir>` writes the sealed AEF run (`TargetMode = Live`). Its run settings record the provenance of
this target, never a secret: `a2aVersion`, `m365Profile`, `normalizerVersion`, `providerProfileVersion`,
`m365ConfigSha256` (the same hash every issue carries), `m365AgentId` (or `default`) and `m365EndpointHost`. With
`--aef-content on` each case also records its prompt, expected answer, the agent's answer and its
`Microsoft365TurnEvidence` document (`m365ContentPolicy` names the content policy it was projected under); with the
default `--aef-content off` the run holds no prompt, answer or evidence document. The document is projected under
`SelectedContent` by default: answer text, citation names and URLs, counts and states are kept, while tool arguments
and results, approval argument text, query and filter text, retrieval hit URLs, passages and resource metadata become
`Redacted` (their source pointers stay). With `--m365-capture-raw` it is projected under `FullEvidence`. The trace and
issue files use the AEF case id as their `caseId`, so a case in the AEF run and its events in the trace match. Add
`--m365-capture-raw <dir>` to also retain the raw request/response bytes and SSE frames (opt-in; may contain tenant
data; keep the folder out of version control).

## Running standard benchmarks (`agenteval bench`)

```bash
agenteval bench gdpr --preset smoke --subject m365-default-copilot \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects
agenteval bench eu-ai-act --preset smoke --subject m365-default-copilot --input "Microsoft 365 Copilot, smoke run" \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects
agenteval bench owasp --preset smoke --subject m365-default-copilot --m365-max-turns 30 \
  --sut microsoft365 --m365-config ./workiq.json --i-understand-live-side-effects
```

The target is registered once on the shared `--sut` seam, so the `bench` commands take it without any change of
their own. Each scenario's prompt is sent to the live agent as one turn; set `--m365-max-turns` to the scenario count
you accept (the OWASP `smoke` preset sends 23 probes and has a 10-minute limit; `top10` sends 73). `--subject` is
required by every family. `bench eu-ai-act` also requires `--input` even with `--sut`: each scenario still sends its
own prompt, and the text is recorded with the run.

| Family | Takes `--sut microsoft365` | Judge |
|---|---|---|
| `gdpr`, `eu-ai-act` | Yes — each scenario's prompt is sent to the live agent (same `--m365-*` flags as `eval`). Presets `smoke`, `standard` (default), `audit`, plus domain packs (GDPR `standard+healthcare`, `+hr`, `+childrens`; EU AI Act `standard+high-risk-employment`, `+high-risk-credit`, `+high-risk-education`). | From the environment: the `AZURE_OPENAI_JUDGE_*` trio when all three are set, otherwise the provider `AI_INFERENCE_PROVIDER` selects. No provider: exit `3`. See [GDPR](benchmarks/gdpr/getting-started.md) and [EU AI Act](benchmarks/eu-ai-act/getting-started.md). |
| `owasp`, `mitre`, `nist` | Yes — same `--m365-*` flags as `eval`. OWASP presets `top10` (default), `smoke`, `audit`, `top10-rag`. | Judge first, from the environment as above; the semantic attacks are graded by Composite Judges, the others by their per-attack oracle. The command checks the judge with one short call first (exit `3` if it does not answer). A judge failure mid-scan, or running out of time, makes the run INCOMPLETE (`WARN`, exit `11`), never a pass. See [OWASP](benchmarks/owasp/getting-started.md) and [MITRE](benchmarks/mitre/getting-started.md). |
| `perf` (`cost`, `latency`, `throughput`) | The subcommands carry the same seam. This target was not part of the offline matrix for `perf`; treat a `perf` run as unverified. | Not applicable (no judge). |
| `agentic` | No — it grades a supplied `--response`/`--response-file`. | — |
| `memory`, `longmemeval`, `typedmemeval` | No — they need their own agent interfaces over a memory corpus. | — |
| `trace-fidelity`, `workflow-trace-fidelity` | No — they reconcile traces. | — |
| `autoaudit` | No — it audits the configured models. | — |

Exit codes are the family's: `0` PASS, `9` FAIL, `10` WARN, `11` indeterminate, `3` judge not configured, `2` usage —
which, for `bench`, includes a missing consent flag, a missing or invalid `--m365-config` and an unknown preset. The
AEF run of a `bench` family with `--sut microsoft365` records the same content-free run settings as `eval`
(`a2aVersion`, `m365Profile`, `normalizerVersion`, `providerProfileVersion`, `m365ConfigSha256`, `m365AgentId`,
`m365EndpointHost`); the per-case `Microsoft365TurnEvidence` document is an `eval` feature (`--aef-content on`).
`agenteval redteam --sut microsoft365` is not available in this phase.

## What `--m365-max-turns` does

`--m365-max-turns <n>` (code: `WorkIqOptions.MaxTurns`) caps the number of turns one agent instance makes; `0` (the
default) means no cap. Every turn that reached the service and came back with a result counts, **completed or not**
(an `InputRequired` or `Indeterminate` turn counts too); a send that failed in transport before any result does not.
**Turns are counted; consumption is not metered.** The Work IQ A2A service exposes no per-turn cost and no token
usage, and Copilot Credits are metered elsewhere, so the cap bounds the number of sends, not spend. The check runs
before each send: the turn that would exceed the cap never fires, and `WorkIqTurnBudgetExceededException` (a
`FatalEvaluationException`) stops the whole batch once instead of repeating the same error for every remaining case.
`eval` then exits `3`; a `bench` run stops once with a non-zero exit, never a pass (`bench gdpr` exits `1` and writes
an `aborted` AEF run). The message says it plainly:

```text
Work IQ evaluation stopped: 10 turn(s) already used; the next turn would exceed MaxTurns (10). Turns are counted,
consumption is not metered — the Work IQ A2A service exposes no per-turn cost, so this cap bounds the number of
sends, not spend. Check the tenant's own usage reporting before raising the cap.
```

Stochastic runs (`--runs N`) multiply the turns; raise the cap accordingly. `WorkIqEvaluableAgent.TurnsUsed` exposes
the count in code.

## Diagnostic profiles

Which output modes a turn requests, and therefore which evidence channels can be `Provided` at all. Requesting
diagnostics is a visible choice, never a default.

| Profile | CLI value | What it requests | Channels |
|---|---|---|---|
| `PublicAnswerOnly` (default) | `--m365-profile public` | `acceptedOutputModes: ["text/plain"]` — the final answer, its citations and attributions. | Tool selections, tool executions, retrieval executions and skill activations are `NotRequested`. |
| `Native118Diagnostics` | `--m365-profile native118` | Additionally the two `application/vnd.ms-workiq…` diagnostic modes observed in Microsoft's native evaluator 1.18.0 (*Developer Info*, *Retrieval Info*). | Those channels become `Provided` or `NotProvided`, as the service answers. |

`Native118Diagnostics` is **experimental: observed in the native evaluator 1.18.0, not a documented Microsoft
contract.** If the service rejects the request, the failure is surfaced; the turn is never silently retried in
answer-only mode. The CLI flag overrides the config file's `diagnosticProfile`; in code, `WorkIqOptions.ProfileOverride`
does the same. Whether a live tenant accepts the modes is one of the facts the doctor's `census` stage records.

## The evidence model

Every turn yields one `Microsoft365TurnEvidence` (schema `agenteval.microsoft365.turn-evidence/v1`, normaliser
`1.0.0`, provider profile `workiq-a2a-1.0/native118-shapes-v1`). It is carried on
`AgentResponse.AdditionalProperties["AgentEval.Microsoft365.Evidence"]` (key `Microsoft365Evidence.Key`), on
`WorkIqTurnResult.Evidence` (also for a failed or indeterminate turn), and, with `eval --aef-content on`, into the AEF
run as each case's evidence document (projected by `Microsoft365EvidenceProjector.ToAefDocument` under the content
policy; `Microsoft365EvidenceJson.Serialize` and `Deserialize` round-trip the whole document). Same capture plus same versions always yield
the same document. Each channel is an `EvidenceSet<T>` with `Availability`, `Validation`, `Coverage`, `Scope`, `Items`,
`Sources` (JSON pointers into the capture) and `Issues`.

| Channel | Field | Under `public` | Under `native118` | Notes |
|---|---|---|---|---|
| Final answer | `Answer` (`RawText`, `DisplayText`, `IsFinal`) | Present for a completed task or a direct message. | Same. | `RawText` keeps the original citation markers; `DisplayText` never alters it. |
| Citation sources | `CitationSources` | `Provided` or `NotProvided`. | Same. | The attribution objects as sent (`Url`, `DisplayName`, `ProviderType`, `ProviderReferenceKey`); absent fields are `Missing`. |
| Citation markers | `CitationMarkers` | `Provided` or `NotProvided`. | Same. | Parsed from the answer text: the Unicode `cite(turnXsearchY)` block (every compound key kept) and markdown `[n]` anchors (`MarkerFormat = markdown-numeric`). A parser projection, never a provider fact. |
| Citation links | `CitationLinks` | Mostly `Binding = Unresolved`. | Same. | A marker is bound to a source only on an explicit provider key or reference URI; positional guesses are labelled `HeuristicDisplayOnly`, never silently applied. |
| Tool selections | `ToolSelections` | `NotRequested` | `Provided` or `NotProvided` | From *Developer Info*; ids, arguments and results that were not sent stay `Missing`. |
| Tool executions | `ToolExecutions` | `NotRequested` | `Provided` or `NotProvided` | Selection is not execution; a missing status is not success. |
| Retrieval executions | `RetrievalExecutions` | `NotRequested` | `Provided` or `NotProvided` | From *Retrieval Info*; `totalResultCount` is not the returned count; unranked hits are kept unranked. |
| Approval intents | `ApprovalIntents` | `Provided` only when the task paused on a confirmation card (the turn then ends `InputRequired`). | Same. | Evidence of a request for consent, not of execution; every proposed call is kept, verbatim. |
| Skill activations | `SkillActivations` | As reported. | As reported. | Only positive activation observations; the card's advertised skills are never counted. |
| Raw captures | `RawCaptures` | References (`CaptureState`, SHA-256, byte count, policy id). | Same. | Bytes are on disk only under `FullEvidence` (`--m365-capture-raw`). |

What each `EvidenceAvailability` value means — and nothing more:

| Value | Meaning |
|---|---|
| `NotRequested` | The connector did not ask for this channel (the profile did not request it). Says nothing about what the service did. |
| `NotProvided` | The channel was requested and the service returned nothing for it. Still not "did not happen". |
| `Provided` | The service returned data for this channel. `Validation` says whether it could be interpreted (`Valid`, `Partial`, `Malformed`, `Conflicting`); `IsObservedEmpty` means a valid, empty collection for the declared scope — zero rows in this snapshot, not zero activity inside the service. |
| `Unsupported` | The service explicitly declared the channel unsupported, with a source pointer. Silence never maps here. |
| `Disabled` | You or the profile deliberately disabled capture of this channel. |
| `Redacted` | Local content policy removed data that was available; the removed content is not retained. |

Other document facts: `TaskState` is the raw final `TASK_STATE_*` (null for a direct message); `CaptureState` says
whether the retained material can reproduce the document (`Complete`, `Interrupted`, `Truncated`, `NotRetained`);
`Issues` lists normalisation problems. On the `AgentResponse`, `RawMessages`, `TokenUsage` and `ModelId` stay null,
and AgentEval's generic tool-usage report and timeline stay empty for this provider: the generic tool metrics do not
read Work IQ diagnostics, and no provider value is copied into a field whose meaning it does not match.

## The trace and issue files

Every live run writes two JSONL files through one `WorkIqDiagnostics` object that every seam (auth handler, capture
handler, A2A client, reducer, conversation, agent) receives:

- **`m365-trace-<run id>.jsonl`** — every trace event of the run, one camelCase JSON object per line: `at`, `stage`,
  `level`, `code`, `message`, and the correlation facts the emitter knew (`runId`, `caseId`, `turnId`, `rpcId`,
  `messageId`, `taskId`, `contextId`, `requestId`, `httpStatus`, `rpcErrorCode`, `a2aState`, `elapsedMs`, `data`).
  An absent fact is omitted, never written as `0` or `""`. (The A2A fields are spelled `a2aState` and `a2aVersion`
  everywhere: in both JSONL files and in `readiness.json`.)
- **`m365-issues.jsonl`** — the issue register: every warning and error, with `stage`, `category` (`Config`,
  `Authentication`, `Permission`, `Network`, `Protocol`, `Service`, `Capture`, `Budget`, `Unknown`), a stable `code`,
  the safe `message`, a plain-language `suggestedCause`, the `aadstsCode` when MSAL produced one, the same
  correlation facts, and the run's `configSha256`, `adapterVersion` and `a2aVersion`. Appended across runs; each
  entry carries its run id.

**Where:** `WorkIqOptions.TraceFile` / `IssueFile` if set; else under `WorkIqOptions.DiagnosticsDirectory`; else the
workspace's `.agenteval/m365/` (the first `.agenteval` folder found walking up from the current directory); else
`<temp>/agenteval-m365/`. The CLI's `--m365-trace <file>` sets the trace file; the issue register has no flag. A
fresh clone has no `.agenteval/` folder (it is not committed): run `agenteval init-workspace` once to create one, or
the files land in the temp folder. The first event of every run, `config.diagnostics_ready`, names both files, and
`eval`/`bench` print both paths in the badge line. The doctor writes its trace and issue files into its report
folder. The run id is `m365-<yyyyMMdd-HHmmss UTC>-<4 hex>` unless `WorkIqOptions.RunId` is set (set it to the AEF
run id to correlate).

While a run is going the files are open for writing: a reader must share them (`Get-Content -Wait`, `tail -f`);
.NET's `File.ReadAllLines`, and some editors, fail with "being used by another process" until the run ends.

**What the codes look like:** `<stage>.<event>` — for example `auth.silent_hit`, `auth.device_code_prompted`,
`discovery.card_fetched`, `discovery.interface_selected`, `discovery.untrusted_host`, `rpc.sent`, `rpc.response`,
`rpc.error`, `task.state`, `turn.outcome`, `budget.exceeded`. Measured operations emit `<code>.start`, `<code>.end`
(with `elapsedMs`) or `<code>.failed`. Every issue is mirrored into the trace as `issue.<category>` so the trace shows
where in the sequence it happened. Stages: `Config`, `Auth`, `Discovery`, `Send`, `Stream`, `GetTask`, `Cancel`,
`Subscribe`, `Reduce`, `Normalize`, `Outcome`, `Budget`, `Capture`, `Doctor`.

**The console view:** `--m365-verbose` (code: `WorkIqOptions.Verbose`; on by default for the doctor) prints every
event to stderr as one line: `[m365 HH:mm:ss.fff Stage/Level code] message` followed by the known ids, HTTP status,
JSON-RPC code, A2A state and elapsed milliseconds. With `--m365-verbose`, `eval` and `bench` also print one
`[m365 turn] case=… turn=… outcome=… transport=… task=… ctx=… state=… answer=… citationMarkers=… toolExecutions=…
elapsedMs=…` summary line per turn, completed or not (a non-completed turn adds `failure=…` and never answer text).

**What they never contain:** a bearer token, a JWT, an auth code, a device code, a client secret, a cookie, or the
`Authorization` header. `WorkIqRedaction` rewrites them before any sink sees a message, a data object or a header
dump (`Bearer [redacted]`, `[jwt redacted]`, `[redacted]`), and a sensitive header is never retained. The MSAL
device-code **message** — the text that tells the user where to sign in and which code to enter — is shown in full on
the console only, because the user must see it; the trace's `auth.device_code_prompted` event keeps where and until
when, with the user code replaced by `[redacted]`. Lines are bounded to 64 KiB with a truncation marker. A sink that
fails is reported once on stderr and dropped; the run continues.

**What they may contain:** your tenant id, app id, agent id, task and context ids, the service's `request-id`, HTTP
statuses, file paths, and error messages the service returned. They do not contain prompts or answers. Read the
[tester guide](microsoft365-tester-guide.md#7-redact-before-you-send) before sharing them. The doctor's
`readiness.md` and `readiness.json` additionally show, after a probe, the fixed probe prompt and the first 80
characters of the agent's answer.

## What's verified vs. what still needs a live check

**Unit-tested, credential-free (synthetic fixtures under `tests/AgentEval.Tests/Microsoft365/`):**

- `Microsoft365Config.Load` and every `Validate` rule, including the refusal of a template placeholder in any field;
  `ResolveCardUri` for the default and an explicit agent.
- `WorkIqOptions.Validate` and the `BuildLive` consent gate (proven to throw without consent and to make no network
  call for a valid config).
- The trust policy against fixture cards: a two-interface default card, an agent-specific card, a card advertising
  only 0.3, a card pointing at an untrusted host.
- The reducer and answer selector against fixture envelopes: completed with answer and attributions, completed with
  *Developer Info* / *Retrieval Info*, completed with an empty answer, two text artifacts (ambiguous), direct message,
  input-required with a confirmation card, auth-required, failed with a partial artifact, rejected, canceled,
  submitted (then polled), JSON-RPC errors on HTTP 200 (`-32601`, `-32009`), a 0.3-shaped response, HTTP 429 with
  `Retry-After`, `CancelTask` races and errors.
- The streaming reducer against fixture SSE streams: happy path, CRLF framing, duplicate frames, EOF before a
  terminal state, failure after partial text, input-required, `lastChunk` before the terminal state.
- The evidence normaliser against the normalisation fixture catalogue (compound citation markers, selection is not
  execution, missing fields stay `Missing`).
- The diagnostics: redaction patterns, the JSONL sinks (line bound, flush, directory creation), the console format, the
  factory's path resolution and the `config.diagnostics_ready` event.
- The agent lifecycle with a fake conversation: completion gate, turn budget, conversation policy, reset, the `onTurn`
  sink, cancellation.
- The whole CLI path through the credential-free seam: consent refusal, missing config, negative cap, bad profile,
  unknown `--sut`, and an offline end-to-end `eval` and `bench gdpr`/`eu-ai-act`/`owasp`/`mitre`/`nist` run (AEF marks
  the stand-in `Mocked`); the AEF run settings and per-case evidence documents of an `eval` run.
- The readiness check against a fake HTTP handler and token provider: every stage's pass and fail paths (missing
  field, missing file, `AADSTS65001`, an untrusted or 0.3-only card, 401, 403, `-32009`), the default stage set, the
  probe skipped for consent with nothing sent, a completed and an `InputRequired` probe, the census reusing a
  `native118` probe or sending a second turn, `--continue`, the report renderings, and that no output ever carries
  the token. `agenteval m365 doctor` itself: every option, the stage mapping, the default report folder, exit codes
  `0`, `1`, `2` and `11`.

**Not verified by the maintainers — needs a tenant (the external tester's job, see the [tester guide](microsoft365-tester-guide.md)):**

- The MSAL device-code prompt, the silent refresh, and the persisted cache across runs.
- The real agent card: which interfaces it advertises, and that one of them is JSON-RPC 1.0 on a trusted host.
- That the service accepts `A2A-Version: 1.0` and answers in the 1.0 shape.
- The real wire shape of a completed turn: where the attributions sit, the citation marker format, whether a
  blocking send returns the terminal state directly or needs `GetTask` polls, timings.
- Whether the `native118` diagnostic modes are accepted, and whether *Developer Info* / *Retrieval Info* artifacts
  appear.
- What `CancelTask` returns on a completed task; whether a follow-up turn on the same context is accepted.
- How a real confirmation card, a 401, a 403, a 429 and a long-running task actually present.

When the tester's results arrive, this section is updated with what passed, where, with which agent, profile and date.
Until then, no sentence on this page claims a live result.

## Troubleshooting

Messages the target, the provider and the doctor print, their cause and the fix. `…` stands for the part that varies.

**Before any network call (CLI validation and config loading)**

| Symptom | Cause | Fix |
|---|---|---|
| `--sut microsoft365 drives a LIVE Microsoft 365 agent…` (the command refuses) | `--i-understand-live-side-effects` was omitted. | Add the flag — only once the config points at a **non-production** identity. |
| `--sut microsoft365 requires --m365-config <file.json> (tenantId, appClientId; optional endpoint, agentId, scope, agentName, diagnosticProfile, timeZone, trustedHosts).` | The config file option was omitted. | Pass `--m365-config <file.json>`. |
| `--m365-max-turns must be >= 0 (0 = no cap; turns are counted, consumption is not metered).` | A negative value. | Use `0` (no cap) or a positive number. |
| `--m365-profile must be 'public' (answer only) or 'native118' (EXPERIMENTAL diagnostics, observed in the native evaluator 1.18.0, not a documented contract); got: '…'.` | A value other than `public` or `native118`. | Use one of the two. |
| `Unknown --sut value: '…'. Valid for this verb: copilot-studio, microsoft365.` (`eval`) / `… Valid: copilot-studio, microsoft365, mock.` (`bench`) | A typo in `--sut` (case-insensitive otherwise), or a verb that has no such target. | Use `microsoft365` with `eval` or a `bench` family. |
| `Microsoft 365 config file not found: …` | The path does not exist. | Check the path; relative paths resolve from the current directory. |
| `Microsoft 365 config file could not be read (…): …` | The file exists but could not be opened (lock, permissions). | Close whatever has it open or fix the permissions. |
| `Invalid Microsoft 365 config JSON (…): …` | The file is not valid JSON. | Fix the syntax; the message carries the parser's detail. |
| `Microsoft 365 config deserialized to null: …` | The file's JSON value is `null`. | Put an object in the file. |
| `Microsoft 365 config is missing required field(s): …` | `tenantId` or `appClientId` is missing or blank, or `endpoint`/`scope` were set to blank. | Add the named field(s). |
| `Microsoft 365 config field '…' still holds the template placeholder 'REPLACE-WITH-…': replace it with …` | A value copied from `m365-config.template.json` was never replaced. | Put the value the message names. |
| `Microsoft 365 config field 'endpoint' must be an absolute https URL, got: '…'` | `endpoint` is relative, not https, or malformed. | Use the default `https://workiq.svc.cloud.microsoft/a2a/` or an absolute https URL. |
| `Microsoft 365 config field 'endpoint' must not carry a query string or fragment, got: '…'` | `?` or `#` in the endpoint. | Remove it. |
| `Microsoft 365 config field 'trustedHosts' must list at least one host …` | Empty `trustedHosts`. | List the endpoint host (default `workiq.svc.cloud.microsoft`). |
| `Microsoft 365 config field 'trustedHosts' contains a blank entry.` | An empty string in the list. | Remove it. |
| `Microsoft 365 config field 'trustedHosts' does not include the endpoint host '…'` | The endpoint's host is not trusted, so discovery would be refused. | Add the endpoint host to `trustedHosts`. |
| `Microsoft 365 config field 'agentId' must be a single opaque path segment …` | The id contains `/`, `\`, `?`, `#`, whitespace or a control character. | Copy the id exactly as the service shows it; do not build a path. |
| `Microsoft 365 config field 'diagnosticProfile' has an unrecognized value: '…'` | Not `PublicAnswerOnly` or `Native118Diagnostics`. | Use one of the two (case-insensitive), or omit the field. |
| `Microsoft 365 config field 'timeZone' is not a known time zone id on this system: '…'` | Not an IANA id this machine knows. | Use an id such as `Europe/Zurich`, or omit the field. |
| `WorkIqOptions has negative value(s): …` | A negative timeout, cap or bound in code. | Every value must be zero or positive. |
| `BuildLive drives a LIVE Microsoft 365 agent over Work IQ as the signed-in user…` | `iUnderstandLiveSideEffects: false` in code. | Pass `true` only for a non-production identity. |

**At run time (sign-in, discovery, turns)**

| Symptom | Cause | Fix |
|---|---|---|
| A device-code message appears and the run waits | First run for this (tenant, app) pair, or the cache expired — expected MSAL device-code sign-in, not a bug. | Open the printed URL in a browser and enter the printed code. Later runs reuse the cache at `%LOCALAPPDATA%\AgentEval\msal_cache\workiq_<hash>.msalcache.bin`. |
| `Work IQ Discover failed: Unauthorized (…)` with `http=401` | The token was missing, expired or rejected by the service. | Sign in again (delete the cache file to force it); check `tenantId`, `appClientId` and `scope`. The request is never resent automatically. |
| `Work IQ … failed: Unauthorized (Known): …: sign-in not completed: no token was acquired before the wait was stopped after N s …; nothing was sent` (issue `auth.sign_in_not_completed`) | A device-code sign-in started during the run and was not finished before the caller's cancellation or the turn deadline. The token is acquired outside the HTTP timeout, so a slow sign-in is never reported as a network timeout. | Sign in first with `agenteval m365 doctor --stage auth` (the token is cached), then run again. |
| `Work IQ … failed: Forbidden (…)` with `http=403` | The user lacks the delegated permission or admin consent, has no Copilot licence, or Work IQ is not enabled in the tenant. | An administrator grants admin consent for `WorkIQAgent.Ask`, assigns the licence, or enables Work IQ. |
| `Work IQ … failed: ProtocolVersionMismatch (…)` | The service answered in the A2A 0.3 shape (`kind` discriminators) or rejected version 1.0 (`rpcError=-32009`). | Report it with the trace. The provider never parses 0.3 and never downgrades. |
| `Work IQ Discover failed: UntrustedHost (…)` | The card, or the selected interface, points at a host outside `trustedHosts`; refused before any token use. | Inspect the card (the doctor's `discovery` evidence lists every advertised interface and host). Add the host only if it is Microsoft's. |
| `Work IQ … failed: RateLimited (…)` with `http=429` | The service throttled. Reads (`GetTask`, the card) are backed off with jitter; a send is never retried. | Re-run later; lower the pace (`--runs`, parallel runs). |
| `Work IQ … failed: NotFound (…)` with `http=404` or `rpcError=-32001` | On discovery: a wrong `agentId`. On a task: the task is inaccessible, expired or purged (not necessarily nonexistent). | Check the agent id; a task "not found" after a drop is reported, not resent. |
| `Work IQ … failed: Transport (…)` | DNS, TLS, proxy or socket failure. | Check connectivity and proxy settings for `workiq.svc.cloud.microsoft`. |
| `Work IQ … failed: Timeout (…)` | The send timeout (300 s), the turn deadline (10 min) or the recovery budget (2 min) elapsed. | A long task; in code raise `WorkIqOptions.SendTimeout`/`TurnDeadline`. The outcome is `Indeterminate` unless the task state is known; nothing is resent. |
| `Work IQ … failed: Malformed (…)` | The payload could not be parsed or violated the one-of rules. | Re-run with `--m365-capture-raw` and send the capture with the trace. |
| `Work IQ turn did not complete: outcome=InputRequired, …` | The agent paused on a confirmation card or asked for input. Nothing was approved. | Use a read-only prompt. The card is on `TurnResult.PendingInteraction`. |
| `Work IQ turn did not complete: outcome=AuthRequired, …` | The agent needs an external authorization the connector cannot complete. | Complete it in the tenant, or change the prompt. |
| `Work IQ turn did not complete: outcome=Failed, …` / `outcome=Rejected, …` | The service ended the task `failed`, or rejected it. | Read `failure=…` in the message and the issue register; do not resend blindly. |
| `Work IQ turn did not complete: outcome=Indeterminate, …` | The remote state is unknown: a timeout or a drop before a task id, or recovery exhausted. | Inspect the trace for a task id; never auto-resend, the request may have been accepted. |
| `Work IQ turn was canceled locally: …` | Your cancellation token (Ctrl+C) stopped the local wait. | The remote task is not cancelled unless `CancelRemoteTaskOnTimeout` is on; its state is on `TurnResult`. |
| `Work IQ evaluation stopped: N turn(s) already used; the next turn would exceed MaxTurns (…)` | The turn cap was reached. | Raise `--m365-max-turns` (or `0`) after checking the tenant's own usage reporting. |
| `[m365 diagnostics] trace sink … failed and is dropped for the rest of run …` | The trace or issue file could not be written. | Check the directory (`--m365-trace`, `WorkIqOptions.DiagnosticsDirectory`); the run continued. |
| `eval` exits `11` although no case failed | A case has no verdict: its turn did not complete, its judge did not answer, or a requested metric was not measured. | Read the case's `error` in the JSON export (`--format json`); fix the prompt, the judge or the tenant state; do not re-run blindly. |
| `bench` exits `11` with `INCOMPLETE` | A judge call failed mid-scan, or the scan ran out of time. | Fix the judge configuration; the partial result is never a pass. |

**Doctor stages (`agenteval m365 doctor`)**

Every failed stage prints its own `meaning:` and `do:` lines; this table is the overview. The `auth` stage maps an
MSAL failure to its AADSTS code with a cause and a fix from the doctor's catalogue (`65001`, `700016`, `50020`,
`7000218`, `50076`, `50079`, `50058`, `70011`, `65002`, `50105`, `530003`, `53003`, `90002`, `50059`, `90019`,
`900144`); any other code is kept as-is with a link to Microsoft's
[error code reference](https://learn.microsoft.com/entra/identity-platform/reference-error-codes).

| Stage result | Cause | Fix |
|---|---|---|
| `config` FAIL naming a field | As in the first table above. | Fix the field; nothing was sent. |
| `auth` FAIL `sign-in failed with AADSTS65001` | Admin consent for `WorkIQAgent.Ask` is missing. | An administrator grants admin consent on the app registration. |
| `auth` FAIL `… AADSTS7000218` | The app is not configured as a public client. | Set *Allow public client flows* to **Yes** (*Authentication* → *Advanced settings*). |
| `config` PASS with a warning `tenantId '…' is neither a GUID nor a domain name` / `appClientId '…' is not a GUID` | A value that cannot be right; the sign-in would fail (`AADSTS90002` / `AADSTS700016`). | Copy both ids from the app registration's *Overview* page. |
| `auth` FAIL `… AADSTS700016` | The app registration was not found in this tenant. | Check `appClientId` and `tenantId`. |
| `auth` FAIL `… AADSTS90002` | The tenant was not found: a typo, a deleted tenant, or a tenant in another cloud. | Check `tenantId` (the *Directory (tenant) ID*, or the verified domain). |
| `auth` FAIL `… AADSTS50059` / `AADSTS90019` | No tenant could be identified (a personal account, the common endpoint), or the request named none. | Set `tenantId`; sign in with a work account of that tenant. |
| `auth` FAIL `… AADSTS50020` | The signed-in account is not in that tenant. | Sign in as a user of that tenant, or correct `tenantId`. |
| `auth` FAIL `… AADSTS50076` / `AADSTS50079` | Multi-factor authentication is required, or must be enrolled first. | Complete it in the browser during the sign-in. |
| `auth` FAIL `… AADSTS50105` / `AADSTS53003` / `AADSTS530003` | User assignment, a conditional access policy or a device policy blocked the sign-in. | An administrator assigns the user or reviews the policy named in the Entra sign-in log. |
| `auth` FAIL `sign-in not completed: the device code expired at … UTC before the sign-in finished` (issue `auth.sign_in_timeout`) | A device-code prompt was shown and nobody completed the sign-in while the code was valid (about 15 minutes). Nothing was sent. | Run again; open the page, enter the code, sign in as the test user. |
| `auth` FAIL `sign-in not completed in time: no token within N s` (issue `auth.sign_in_timeout`) | No sign-in was started and the token provider gave no token within `--timeout-seconds`. Nothing was sent. | Check the path to `login.microsoftonline.com`; raise `--timeout-seconds`. |
| `discovery` / `protocol` FAIL `HTTP 401 on …: the token was rejected` | The service rejected the token. | Check `scope`, `tenantId` and `appClientId`; run again to sign in afresh. |
| `discovery` / `protocol` FAIL `HTTP 403 on …` | Permission or consent missing, no Copilot licence, or Work IQ not enabled — indistinguishable from outside. | An administrator checks all three. |
| `discovery` FAIL `HTTP 404 on …: the endpoint or agent was not found` | A wrong `agentId` or `endpoint`, or an agent the user cannot reach. | Check `agentId` (try `null`) and `endpoint`. |
| `discovery` FAIL `the card points at a host outside trustedHosts` | The card advertises an untrusted host; no token was sent there. | Report it; add the host only if it is Microsoft's. |
| `discovery` FAIL `the card advertises no A2A 1.0 JSON-RPC interface` | The card offers no protocol this connector speaks. | Report it; the provider never downgrades to 0.3. |
| `discovery` / `protocol` FAIL `the service rejected A2A 1.0 (JSON-RPC -32009 VersionNotSupported)` or `the reply has the A2A 0.3 shape` | The service ignored the `A2A-Version: 1.0` header. | Report it with the trace. |
| `protocol` FAIL `HTTP 404 on GetTask instead of the JSON-RPC TaskNotFound error (-32001)` | The selected interface does not behave like the A2A 1.0 JSON-RPC endpoint. | Report it with the `request-id`. |
| `protocol` FAIL `JSON-RPC error … instead of TaskNotFound (-32001)` | An unexpected JSON-RPC answer. | Report the status, JSON-RPC code and `request-id`. |
| `protocol` PASS with a warning: a task came back for a random id | Unexpected, but the endpoint speaks A2A 1.0 and accepted the token. | Nothing to fix; report it. |
| `probe` / `census` SKIPPED, `Result: INCOMPLETE`, exit `11` | `--probe` was given without `--i-understand-live-side-effects`; nothing was sent to the agent. | Add the consent flag for a non-production identity. |
| `probe` FAIL `the agent paused (InputRequired)` / `(AuthRequired)` | The agent waits for a confirmation, input or a connector sign-in; nothing was approved. | Unexpected for the read-only probe prompt: report it; for `AuthRequired`, sign in to the connector in Copilot Chat. |
| `probe` FAIL `Indeterminate: the send may have been accepted; do not resend` | No final task state within the stage limit (or a drop). | Do not resend blindly; run the doctor again later with a larger `--timeout-seconds`. |
| `census` PASS `native118 refused` | The tenant refuses the experimental diagnostic modes. | Nothing to fix; keep `--m365-profile public`. |
| `census` FAIL `the census could not be measured` | The `native118` turn ended neither completed nor clearly refused. | Report it. |
| exit `3`, `m365 doctor: the report folder could not be created` / `the report could not be written` | The report folder is not writable. | Pass a writable `--report <dir>`. |

## See also

- [The demo project](../samples/AgentEval.Microsoft365Demo/README.md) (`samples/AgentEval.Microsoft365Demo`) — one
  console app: readiness, one prompt, a small benchmark, one run folder to send back; its own GitHub Copilot setup.
- [Microsoft 365 tester guide](microsoft365-tester-guide.md) — the step-by-step live validation for an external tester
  with a tenant: install, config, doctor, probe, smoke dataset, what to send back, FAQ — and its
  [results template](microsoft365-tester-RESULTS-TEMPLATE.md).
- Samples: `samples/AgentEval.Samples/Microsoft365/` (group R — `dotnet run -- 109` Hello World, `dotnet run -- 110`
  Live Walkthrough, from `samples/AgentEval.Samples/`; they need `M365_CONFIG_PATH` and
  `M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true` and stop with exit `11` without them).
- [CLI Reference — `agenteval eval`](cli.md#agenteval-eval), [`agenteval bench`](cli.md#agenteval-bench),
  [`agenteval m365 doctor`](cli.md#agenteval-m365-doctor) and [Exit codes](cli.md#exit-codes).
- [Copilot Studio](copilot-studio.md) — the sibling connector this one mirrors (config, consent gate, budget, test
  seam) and the fidelity reasoning it shares.
- [AEF Evidence Format](aef.md) — where the run and the per-turn evidence document are recorded.
- [LLM as a Judge](llm-as-judge.md) and [Benchmarks Guide](benchmarks.md) — grading the answers.
- [The `.agenteval/` Workspace](agenteval-workspace.md) — where the trace, issue and doctor files land.
