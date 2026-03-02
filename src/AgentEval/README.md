# AgentEval

> **The .NET Evaluation Toolkit for AI Agents**

Built first for **Microsoft Agent Framework (MAF)** and **Microsoft.Extensions.AI**. What RAGAS and DeepEval do for Python, AgentEval does for .NET.

## Features

- 🎯 **Tool Tracking** — Monitor tool/function calls with timing, arguments, and ordering
- ✅ **Fluent Assertions** — Expressive assertions with rich failure messages, `because` reasons, and assertion scopes
- 📊 **Performance Metrics** — TTFT, latency, tokens, cost estimation for 8+ models
- 🔬 **RAG Metrics** — Faithfulness, relevance, context precision/recall, answer correctness
- 🛡️ **Red Team Security** — 9 attack types, 192 probes, OWASP LLM Top 10 coverage
- ⚖️ **Responsible AI** — Toxicity, bias, and misinformation detection metrics
- 📈 **Stochastic Evaluation** — Statistical model comparison with multi-run analysis
- 🔄 **Trace Record & Replay** — Deterministic CI testing without LLM calls
- 🎯 **Calibrated Evaluator** — Multi-model consensus-driven scoring
- 🔌 **Extensible** — Adapter pattern for any agent framework

## Quick Start

```csharp
using AgentEval;
using AgentEval.MAF;
using AgentEval.Assertions;

// Create evaluation harness
var harness = new MAFEvaluationHarness(evaluatorClient);

// Run evaluation with tool tracking
var result = await harness.RunEvaluationAsync(agent, new TestCase
{
    Name = "Feature Planning Test",
    Input = "Plan a user authentication feature",
    EvaluationCriteria = ["Should include security considerations"]
});

// Assert tool usage with "because" reasons
result.ToolUsage!
    .Should()
    .HaveCalledTool("SecurityTool", because: "auth features require security review")
        .BeforeTool("FeatureTool")
        .WithoutError()
    .And()
    .HaveNoErrors();

// Assert performance
result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveEstimatedCostUnder(0.10m);
```

## Red Team Security Scanning

```csharp
var result = await AttackPipeline.Create()
    .WithAllAttacks()
    .ScanAsync(agent);

result.Should().HaveOverallScoreAbove(85);
result.ExportAsync("security-report.sarif", ExportFormat.Sarif);
```

## Trace Record & Replay

Capture agent executions for deterministic replay — no LLM calls needed in CI:

```csharp
// Record
await using var recorder = new TraceRecordingAgent(realAgent, "weather_test");
var response = await recorder.InvokeAsync("What's the weather?");
await TraceSerializer.SaveToFileAsync(recorder.Trace, "trace.json");

// Replay (deterministic, free)
var trace = await TraceSerializer.LoadFromFileAsync("trace.json");
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.InvokeAsync("What's the weather?");
```

## Model Comparison

```csharp
var result = await comparer.CompareModelsAsync(
    factories: [gpt4oFactory, gpt4oMiniFactory],
    testCases: testSuite,
    options: new ComparisonOptions(RunsPerModel: 5));

Console.WriteLine(result.ToMarkdown());
```

## Quality Assurance

- **Comprehensive evaluation suite** targeting net8.0, net9.0, and net10.0
- All evaluations passing ✅

## Installation

```bash
dotnet add package AgentEval --prerelease
```

**Single package, modular internals** — AgentEval ships as one NuGet package containing 6 focused assemblies:
- `AgentEval.Abstractions` — Public contracts and interfaces
- `AgentEval.Core` — Metrics, assertions, comparison, tracing
- `AgentEval.DataLoaders` — Data loading and export (JSON, YAML, CSV, JSONL)
- `AgentEval.MAF` — Microsoft Agent Framework integration
- `AgentEval.RedTeam` — Security testing (multiple attack types and probes)

### Service Registration

```csharp
// Register all services at once (recommended):
services.AddAgentEvalAll();

// Or register selectively:
services.AddAgentEval();              // Core services only
services.AddAgentEvalDataLoaders();   // DataLoaders + Exporters
services.AddAgentEvalRedTeam();       // Red Team security testing
```

## Documentation

- [Getting Started](https://agenteval.dev/getting-started.html)
- [Fluent Assertions](https://agenteval.dev/assertions.html)
- [Metrics Reference](https://agenteval.dev/metrics-reference.html)
- [Red Team Security](https://agenteval.dev/redteam.html)
- [Trace Record & Replay](https://agenteval.dev/tracing.html)
- [Stochastic Evaluation](https://agenteval.dev/stochastic-evaluation.html)
- [Architecture](https://agenteval.dev/architecture.html)

## License

MIT License — See [LICENSE](https://github.com/AgentEvalHQ/AgentEval/blob/main/LICENSE) for details.
