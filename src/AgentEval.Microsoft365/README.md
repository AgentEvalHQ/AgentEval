# AgentEval.Microsoft365

A Microsoft 365 provider for [AgentEval](https://github.com/AgentEvalHQ/AgentEval): it evaluates a Microsoft 365
Copilot or declarative agent through the Work IQ A2A 1.0 endpoint, as an `IEvaluableAgent` in code or via
`agenteval --sut microsoft365` in the CLI. It has no Microsoft Agent Framework dependency.

> **Status: Experimental.** The live path (delegated device-code auth, A2A 1.0 transport, evidence
> normalisation) has **not** been independently verified against a real Microsoft 365 tenant yet.

> **Under construction.** The transport, conversation and agent layers exist; streaming, evidence normalisation
> and the CLI target are being built in parallel lanes and may still be stubs in a given commit.

## Install

> **Not published to NuGet yet.** The project sets `IsPackable=true`, but `.github/workflows/release.yml` does
> not pack or publish it. Its code reaches users only inside the `AgentEval.Cli` tool until a release-cut
> decision publishes it. Until then, reference the project directly or build from source.

```bash
# Future, once published:
# dotnet add package AgentEval.Microsoft365 --prerelease
```

## Usage

```csharp
using AgentEval.Microsoft365;

var config = Microsoft365Config.Load(new FileInfo("workiq.json"));   // tenantId, appClientId, optional agentId; no secrets

var options = new WorkIqOptions
{
    MaxTurns = 20,                                   // counted sends per agent instance (0 = no cap); nothing is metered
    ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,   // default: every InvokeAsync starts a new server context
    DiagnosticsDirectory = new DirectoryInfo(".agenteval/m365"),         // JSONL trace + issue register land here
};

// iUnderstandLiveSideEffects is required and has no default: a Microsoft 365 agent works on REAL tenant data
// and may act on it. Pass true only once you accept that. BuildLive makes no network call; the device-code
// sign-in and the agent-card discovery run on the first turn.
await using var agent = Microsoft365AgentFactory.BuildLive(
    config,
    iUnderstandLiveSideEffects: true,
    options,
    onTurn: result => Console.Error.WriteLine($"{result.Identity.TurnId}: {result.Outcome}"));   // sees every result, even before a throw

agent.NextCaseId = "q3-planning-summary";   // optional: correlate the turn with your own case id (one-shot)
try
{
    var reply = await agent.InvokeAsync("Summarise the Contoso Q3 planning document and cite your sources.");
    Console.WriteLine(reply.Text);                 // the selected answer text, citation markers kept
    var evidence = reply.Evidence();               // Microsoft365TurnEvidence: answer sources, citations, captures
}
catch (WorkIqTurnNotCompletedException ex)         // input/auth required, failed, rejected, canceled, indeterminate
{
    Console.Error.WriteLine(ex.Message);           // safe summary: outcome, ids, failure category
    var result = ex.TurnResult;                    // the typed result: task handle, pending interaction, evidence
}
catch (WorkIqTurnCanceledException ex)             // your cancellation token fired; the remote task was NOT canceled
{
    var result = ex.TurnResult;
}
catch (WorkIqTurnBudgetExceededException)          // MaxTurns reached: a FatalEvaluationException, every loop stops
{
}
```

What the live path does, in order, on each turn:

1. `WorkIqDeviceCodeTokenProvider` acquires a delegated token (silent from the OS-encrypted MSAL cache, else a
   device-code prompt on stderr) for `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`.
2. `WorkIqAuthHandler` attaches `Authorization: Bearer`, `A2A-Version: 1.0` and a `User-Agent`, and records the
   service `request-id`; `RawCaptureHandler` tees the bytes under the content policy.
3. `WorkIqA2AClient` fetches the agent card once (`{endpoint}/{agentId}/.well-known/agent-card.json`), selects the
   `JSONRPC` 1.0 interface on a trusted host (anything else is refused before a token goes there), then posts a
   blocking `SendMessage`; a `submitted`/`working` task is polled with `GetTask` under `RecoveryBudget`.
   A send is never retried; 429/503 backoff applies to discovery, `GetTask` and `CancelTask` only.
4. `WorkIqConversation` turns the reply into a `WorkIqTurnResult` (outcome, transport, answer completeness,
   evidence, pending interaction, failure) and `WorkIqEvaluableAgent` applies the completion gate: only a
   `Completed` turn becomes an `AgentResponse`.

Every stage writes trace events (`auth.*`, `discovery.*`, `rpc.*`, `task.state`, `turn.outcome`, `budget.*`)
to `m365-trace-<runId>.jsonl` and warnings/errors to `m365-issues.jsonl` with a category and a suggested cause,
so a tester can see exactly which stage failed and why. Tokens never appear in them. The MSAL device-code message
(where to sign in, which code to enter) is the one auth text that is shown.

The rich API (`IWorkIqConversation`: `RunTurnAsync`, `GetTaskAsync`, `CancelTaskAsync`, `AbandonAndResetAsync`,
`StreamTurnAsync`, `SubscribeAsync`) is reachable through `agent.Conversation`; tests wrap a fake conversation
with `Microsoft365AgentFactory.FromConversation`.

## What this does NOT do (yet)

- **Not live-verified.** No run against a real tenant has been independently checked.
- **No streaming through `IStreamableAgent` yet.** Turns are blocking sends; streaming is not surfaced.
- **No interaction continuation yet.** A turn that pauses for confirmation or input ends as
  `InputRequired`/`AuthRequired` with the pending interaction preserved on the result; `ContinueAsync` is not
  supported until a later wave. Nothing is ever approved automatically.
- **Tool and retrieval evidence only as the tenant emits it.** Citations, attributions and tool activity are
  recorded only when Work IQ returns them; nothing is inferred, and AgentEval's generic tool-usage report stays
  empty for this provider.
