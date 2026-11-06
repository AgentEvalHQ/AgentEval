# AgentEval.Microsoft365

A Microsoft 365 provider for [AgentEval](https://github.com/AgentEvalHQ/AgentEval). It evaluates Microsoft 365 Copilot
or a declarative agent over the Work IQ A2A 1.0 endpoint, as an `IEvaluableAgent` in code. The CLI reaches the same
agent with `agenteval eval --sut microsoft365` and the `agenteval bench` families. It has no Microsoft Agent Framework
dependency.

> **Status: Experimental.** The live path (device-code sign-in, A2A 1.0 transport, evidence) has not been verified
> against a real Microsoft 365 tenant yet. Every public type is `[Experimental("AGENTEVAL_M365_PREVIEW001")]` and may
> change in any release.

## Documentation

- [Microsoft 365 guide](https://github.com/AgentEvalHQ/AgentEval/blob/main/docs/microsoft365.md): prerequisites, the
  config file, the evidence model, the trace and issue files, and the troubleshooting table of every issue code.
- [Tester guide](https://github.com/AgentEvalHQ/AgentEval/blob/main/docs/microsoft365-tester-guide.md): the live
  validation on a tenant.
- [Demo project](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Microsoft365Demo/README.md):
  readiness, one prompt and a small benchmark in one command.

## Install

The package is not on NuGet yet. Build from source and reference three projects, because this provider references
`AgentEval.Abstractions` and `AgentEval.Core` privately. The build fails with `AGENTEVAL_M365_PREVIEW001` until you
suppress it:

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

## Usage

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

`BuildLive` makes no network call: the device-code sign-in and the agent card discovery run on the first turn. The
config file holds no secret. A send is never retried. Every run writes a redacted JSONL trace and an issue register;
neither ever contains a token. `agent.Conversation` gives the rich API (`IWorkIqConversation`), and
`Microsoft365AgentFactory.FromConversation` wraps a fake conversation for tests.

## What this does not do yet

- **No live verification.** No run against a real tenant has been checked.
- **No streaming through `IStreamableAgent`.** The agent sends blocking turns.
- **No continuation of a paused task.** A turn that pauses for a confirmation or input ends as `InputRequired` or
  `AuthRequired`, with the pause kept on the result. `IWorkIqConversation.ContinueAsync` always throws
  `NotSupportedException`. Nothing is ever approved automatically.
- **No inferred evidence.** Citations, tool activity and retrieval are recorded only when Work IQ returns them.
  AgentEval's generic tool-usage report stays empty for this provider.
