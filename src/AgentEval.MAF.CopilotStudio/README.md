# AgentEval.MAF.CopilotStudio

A Microsoft Copilot Studio (MCS) → `IEvaluableAgent`/`IChatClient` bridge for [AgentEval](https://github.com/AgentEvalHQ/AgentEval), usable directly in code — no CLI required.

> **Status: Experimental.** The live connector (device-code auth, streaming activity mapping) has not yet
> been independently verified against a real Copilot Studio tenant — see the
> [Feature Maturity table](https://github.com/AgentEvalHQ/AgentEval#feature-maturity) in the main repo README.

## Install

```bash
dotnet add package AgentEval.MAF.CopilotStudio --prerelease
```

> **Not yet published.** This package builds, packs, and is consumable via `ProjectReference` today, but
> publishing it to NuGet.org is a separate, not-yet-made release-cut decision — `.github/workflows/release.yml`
> currently only packs `AgentEval`/`AgentEval.Cli`. Until that lands, reference the project directly or build
> from source.

This is a separate, opt-in package — installing the main `AgentEval` package does **not** pull in the
Copilot Studio SDK or MSAL. Add this package only if you need to evaluate a live Copilot Studio agent.

## Usage

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

// BuildLive constructs the live connector (MSAL device-code auth, streaming activity bridge) and returns
// an IEvaluableAgent — the same seam AgentEval's fluent assertions / stochastic runner / benchmarks use
// for every other agent type. iUnderstandLiveSideEffects is required and has no default: this agent's
// connectors/flows can fire REAL production actions and cannot be sandboxed — pass true only once you've
// confirmed `config` points at a NON-PROD agent.
IEvaluableAgent agent = CopilotStudioAgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true);

var result = await agent.InvokeAsync("What's the status of order #12345?");
```

`CopilotStudioAgentFactory.FromAgent(AIAgent)` is the credential-free seam if you already have a MAF
`AIAgent` wrapping a Copilot Studio connection built some other way.

## Testing your own code against this

`CopilotStudioChatClient` takes an `ICopilotStudioConversationClient` — implement that interface with your
own scripted/fake conversation client to unit-test code that depends on this bridge without a live tenant
or real credentials.

## What this does NOT do (yet)

- **Tool-call evidence.** The Copilot Studio conversation channel is structurally text-only — server-side
  tool/connector/knowledge calls are never surfaced to the client. Evidence fidelity tops out at `Verbal`.
- **Live-tenant verification.** The device-code prompt, silent-refresh, and persisted-cache round trip are
  unit-tested against a fake conversation client but have not been exercised against a real MCS agent.
- **Credit-cost enforcement.** The underlying SDK exposes no per-turn cost field.

See [`strategy/CopilotStudio/`](https://github.com/AgentEvalHQ/AgentEval) (local-only planning docs, not
part of this package) in the main repo for the full backlog.
