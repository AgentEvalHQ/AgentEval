# AgentEval.Microsoft365

A Microsoft 365 provider for [AgentEval](https://github.com/AgentEvalHQ/AgentEval): it evaluates a Microsoft 365
Copilot or declarative agent through the Work IQ A2A 1.0 endpoint, as an `IEvaluableAgent` in code or via
`agenteval --sut microsoft365` in the CLI. It has no Microsoft Agent Framework dependency.

> **Status: Experimental.** The live path (delegated device-code auth, A2A 1.0 transport, evidence
> normalisation) has **not** been independently verified against a real Microsoft 365 tenant yet.

> **Under construction.** This project is being built now; the types named below are the planned public
> surface and do not all exist yet.

## Install

> **Not published to NuGet yet.** The project sets `IsPackable=true`, but `.github/workflows/release.yml` does
> not pack or publish it. Its code reaches users only inside the `AgentEval.Cli` tool until a release-cut
> decision publishes it. Until then, reference the project directly or build from source.

```bash
# Future, once published:
# dotnet add package AgentEval.Microsoft365 --prerelease
```

## Usage (sketch)

```csharp
using AgentEval.Microsoft365;

var config = Microsoft365Config.Load(new FileInfo("workiq.json"));   // tenant, app client id, agent; no secrets

// iUnderstandLiveSideEffects is required and has no default: a Microsoft 365 agent works on REAL tenant data
// and may act on it. Pass true only once you accept that.
await using var agent = Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true);

var reply = await agent.InvokeAsync("Summarise the Contoso Q3 planning document and cite your sources.");
Console.WriteLine(reply.Text);   // a turn that did not complete throws WorkIqTurnNotCompletedException
```

## What this does NOT do (yet)

- **Not live-verified.** No run against a real tenant has been independently checked.
- **No streaming through `IStreamableAgent` yet.** Turns are blocking sends; streaming is not surfaced.
- **Tool and retrieval evidence only as the tenant emits it.** Citations, attributions and tool activity are
  recorded only when Work IQ returns them; nothing is inferred, and AgentEval's generic tool-usage report stays
  empty for this provider.
