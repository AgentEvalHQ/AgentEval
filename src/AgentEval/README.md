# AgentEval

> **The .NET Evaluation Toolkit for AI Agents**

Built first for **Microsoft Agent Framework (MAF)** and **Microsoft.Extensions.AI**. What RAGAS and DeepEval do for Python, AgentEval does for .NET.

> **Preview.** AgentEval is experimental: APIs and behavior may change without notice, and breaking changes are listed in the [CHANGELOG](https://github.com/AgentEvalHQ/AgentEval/blob/main/CHANGELOG.md). Only the Gatekeeper public surface is frozen by a test. Do not use it in production or safety-critical systems without your own review and testing.

## Features

- 🎯 **Tool Tracking** — Monitor tool/function calls with timing, arguments, and ordering
- ✅ **Fluent Assertions** — Expressive assertions with rich failure messages, `because` reasons, and assertion scopes
- 📊 **Performance Metrics** — TTFT, latency, tokens, cost estimation for 8+ models
- 🔬 **RAG Metrics** — Faithfulness, relevance, context precision/recall, answer correctness
- 🛡️ **Red Team Security** — 14 attack types, 264 probes, full OWASP LLM Top 10 coverage
- 🚪 **Gatekeeper** — Fail-closed runtime enforcement: block forbidden/poisoned tool calls before they run, red-team probes as live guards, and human-in-the-loop approval
- ⚖️ **Responsible AI** — Toxicity, bias, and misinformation detection metrics
- 📈 **Stochastic Evaluation** — Statistical model comparison with multi-run analysis
- 🔄 **Trace Record & Replay** — Deterministic CI testing without LLM calls
- 🎯 **Calibrated Judge** — Several LLM judges score the same reply and vote, with their agreement reported
- 🔌 **Extensible** — Adapter pattern for any agent framework

## Quick Start

```csharp
using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;

// Create evaluation harness (evaluatorClient: the IChatClient that grades replies)
var harness = new MAFEvaluationHarness(evaluatorClient);

// Wrap your Microsoft Agent Framework AIAgent for evaluation
var agent = new MAFAgentAdapter(aiAgent);

// Run evaluation with tool tracking. ModelName selects the price used to estimate cost;
// without a name found in the price table, no cost is estimated.
var result = await harness.RunEvaluationAsync(agent, new TestCase
{
    Name = "Feature Planning Test",
    Input = "Plan a user authentication feature",
    EvaluationCriteria = ["Should include security considerations"]
}, new EvaluationOptions { ModelName = "gpt-4o" });

// Assert tool usage with "because" reasons
result.ToolUsage!
    .Should()
    .HaveCalledTool("SecurityTool", because: "auth features require security review")
        .BeforeTool("FeatureTool")
        .WithoutError()
    .And()
    .HaveNoErrors();

// Assert performance. A metric that was not captured (such as cost with no ModelName) cannot
// fail its check: inside an AgentEvalScope it is recorded as inconclusive, outside one it is skipped.
result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveEstimatedCostUnder(0.10m);
```

## Red Team Security Scanning

```csharp
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;

var result = await AttackPipeline.Create()
    .WithAllAttacks()
    .ScanAsync(agent);

result.Should()
    .HavePassed()            // fails on any compromised probe, and on a scan too inconclusive to trust
    .HaveMinimumScore(85);   // percentage of probes resisted

await new SarifReportExporter().ExportToFileAsync(result, "security-report.sarif");
```

## Trace Record & Replay

Capture agent executions for deterministic replay — no LLM calls needed in CI:

```csharp
using AgentEval.Tracing;

// Record
await using var recorder = new TraceRecordingAgent(realAgent, "weather_test");
var response = await recorder.InvokeAsync("What's the weather?");
await recorder.SaveAsync("trace.json");

// Replay (deterministic, free)
var trace = await TraceSerializer.LoadFromFileAsync("trace.json");
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.InvokeAsync("What's the weather?");
```

## Model Comparison

```csharp
using AgentEval.Comparison;

var comparer = new ModelComparer(new StochasticRunner(harness));

// CreateAgent(deployment) is your code: it returns an IEvaluableAgent for that model
var results = await comparer.CompareModelsAsync(
    factories: new IAgentFactory[]
    {
        new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
        new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini"))
    },
    testCases: testSuite,
    options: new ModelComparisonOptions(RunsPerModel: 5));

Console.WriteLine(results.ToMarkdown());
```

The quality, speed, cost and reliability scores rank the models against each other (best 100, worst 0), and cost is priced from a single model name. See [Model Comparison](https://agenteval.dev/model-comparison.html) for how the scores are computed.

## Quality Assurance

- The test suite runs in CI on .NET 8, 9 and 10; the [build status](https://github.com/AgentEvalHQ/AgentEval/actions/workflows/ci.yml) shows the latest result

## Installation

```bash
dotnet add package AgentEval --prerelease
```

**Single package, modular internals** — the `AgentEval` package embeds these assemblies; none of them is published as a separate package:
- `AgentEval.Abstractions` — Public contracts and interfaces
- `AgentEval.Core` — Metrics, assertions, comparison, tracing
- `AgentEval.DataLoaders` — Data loading and export (JSON, YAML, CSV, JSONL)
- `AgentEval.MAF` — Microsoft Agent Framework integration, including Gatekeeper
- `AgentEval.Memory` — Memory evaluation, benchmarks, LongMemEval, HTML reporting
- `AgentEval.RedTeam` and `AgentEval.RedTeam.Gatekeeper` — Security testing, and red-team evaluators as runtime gates
- `AgentEval.Evals.Agentic` and `AgentEval.Evals.Performance` — The agentic evaluator suite and the performance benchmarks
- `AgentEval.Compliance.Core`, `AgentEval.Compliance.Gdpr` and `AgentEval.Compliance.EuAiAct` — Compliance benchmarks
- `AgentEval.Rendering.Pdf` — PDF report rendering

The Copilot Studio add-on (`AgentEval.MAF.CopilotStudio`) is not part of this package and is not on NuGet; build it from the [repository](https://github.com/AgentEvalHQ/AgentEval) if you need it.

### Service Registration

```csharp
// Register all services at once (recommended):
services.AddAgentEvalAll();

// Or register selectively:
services.AddAgentEval();              // Core services only
services.AddAgentEvalDataLoaders();   // DataLoaders + Exporters
services.AddAgentEvalRedTeam();       // Red Team security testing
services.AddAgentEvalMemory();        // Memory evaluation (AgentEval.Memory.Extensions)
services.AddAgentEvalAgentic();       // Agentic evaluator options (AgentEval.Evals.Agentic)
```

## Documentation

- [Getting Started](https://agenteval.dev/getting-started.html)
- [Fluent Assertions](https://agenteval.dev/assertions.html)
- [Metrics Reference](https://agenteval.dev/metrics-reference.html)
- [Red Team Security](https://agenteval.dev/redteam.html)
- [Gatekeeper (Runtime Enforcement)](https://agenteval.dev/gatekeeper/introduction.html)
- [Trace Record & Replay](https://agenteval.dev/tracing.html)
- [Stochastic Evaluation](https://agenteval.dev/stochastic-evaluation.html)
- [Model Comparison](https://agenteval.dev/model-comparison.html)
- [Architecture](https://agenteval.dev/architecture.html)

## License

MIT License — See [LICENSE](https://github.com/AgentEvalHQ/AgentEval/blob/main/LICENSE) for details.

## Deterministic evals

A deterministic eval is one measurement of an agent run, computed in code — no model, no cost, same
answer every time. Register one with `AgentEvalBuilder.AddEval(eval, floor)`: the door takes the eval
**and** the chance floor it is judged against, because a score you cannot compare to luck is not a
measurement. See [Deterministic evals](https://agenteval.dev/deterministic-evals.html)
for the contract — what the eval sees, what `null` versus `[]` tool calls mean, and how to say "this
could not be measured" without saying "this scored zero".
