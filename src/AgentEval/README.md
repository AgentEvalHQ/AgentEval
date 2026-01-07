# AgentEval

> **The first .NET-native AI agent testing, evaluation, and benchmarking framework**

## Features

- 🎯 **Tool Tracking** - Monitor tool/function calls with timing and arguments
- 📊 **Performance Metrics** - TTFT, latency, tokens, cost estimation
- ✅ **Fluent Assertions** - Expressive test assertions with rich failure messages, "because" reasons, and assertion scopes
- 🔬 **RAG Metrics** - Faithfulness, relevance, context precision
- 📈 **Benchmarking** - Performance and agentic benchmarks
- 🔌 **Extensible** - Adapter pattern for multiple agent frameworks
- 📋 **Trace-First Failure Reporting** - Structured failure reports with tool timelines
- 🔧 **Testing Infrastructure** - FakeChatClient for mocking LLM responses

## Quick Start

```csharp
using AgentEval;
using AgentEval.MAF;
using AgentEval.Assertions;

// Create test harness
var harness = new MAFTestHarness(evaluatorClient);

// Run test with tool tracking
var result = await harness.RunTestAsync(agent, new TestCase
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

// Use assertion scopes to collect all failures
using (new AgentEvalScope())
{
    result.ToolUsage!.Should().HaveCalledTool("Tool1");
    result.ToolUsage!.Should().HaveCalledTool("Tool2");
    result.ActualOutput!.Should().Contain("success");
}
// Throws single exception with ALL failures listed
```

## Rich Failure Messages

When assertions fail, you get structured, actionable output:

```
Expected tool 'SearchTool' to be called, but it was not because user query requires search.

Expected: Tool 'SearchTool' called at least once
Actual:   Tools called: [CalculateTool, FormatTool]

Tools called:
  • CalculateTool
  • FormatTool

Suggestions:
  → Verify the agent has access to the expected tools
  → Check if the prompt clearly requests tool usage
```

## Test Coverage

- **2100+ unit tests** covering all core functionality
- Tests for all assertions, metrics, models, and adapters
- All tests passing ✅

## Installation

```bash
dotnet add package AgentEval
```

## Documentation

- [Getting Started Guide](../../docs/getting-started.md) - Quick introduction
- [Fluent Assertions Guide](../../docs/assertions.md) - Complete assertion reference  
- [Architecture Guide](../../docs/architecture.md) - Component overview
- [AgentEval Design Document](AgentEval-Design.md) - Full technical documentation

## License

MIT License - See LICENSE file for details.
