# AgentEval Documentation

Welcome to the **AgentEval** documentation. AgentEval is the first .NET-native AI agent testing, evaluation, and benchmarking framework.

<p align="center">
  <a href="https://www.nuget.org/packages/AgentEval">
    <img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet Version" />
  </a>
  <a href="https://github.com/joslat/AgentEval">
    <img src="https://img.shields.io/github/stars/joslat/AgentEval.svg" alt="GitHub Stars" />
  </a>
</p>

---

## Quick Install

```bash
dotnet add package AgentEval --prerelease
```

**NuGet:** https://www.nuget.org/packages/AgentEval

---

## Getting Started

| Guide | Description |
|-------|-------------|
| [Installation](installation.md) | Install AgentEval and verify setup |
| [Quick Start](getting-started.md) | Run your first agent test in 5 minutes |
| [Walkthrough](walkthrough.md) | Step-by-step tutorial with examples |

---

## Features

### Tool Usage Assertions
Assert on tool calls, order, arguments, results, errors, and duration with a fluent API.

```csharp
result.ToolUsage!
    .Should()
    .HaveCalledTool("SearchFlights")
        .BeforeTool("BookFlight")
        .WithArgument("destination", "Paris")
    .And()
    .HaveNoErrors();
```

### Performance Metrics
Track latency, TTFT (Time To First Token), tokens, estimated cost, and per-tool timing.

```csharp
result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(2))
    .HaveEstimatedCostUnder(0.10m);
```

### Multi-Turn Conversation Testing
Test complex multi-turn conversations with the `ConversationalTestCase` builder and `ConversationRunner`. See [Conversations](conversations.md).

### Workflow Testing
Test multi-agent orchestration with edge assertions, conditional routing, and Mermaid diagram export. See [Workflow Testing](workflows.md).

### Snapshot Testing
Compare agent responses against saved baselines with JSON diff, field ignoring, pattern scrubbing, and semantic similarity. See [Snapshots](snapshots.md).

### RAG Metrics
Evaluate faithfulness, relevance, context precision/recall, and answer correctness.

### Agentic Metrics
Measure tool selection accuracy, tool arguments, tool success, task completion, and efficiency.

### Benchmarks
Run latency, throughput, cost, and agentic benchmarks with percentile statistics (p50/p90/p95/p99). See [Benchmarks](benchmarks.md).

### CLI Tool
Full command-line interface for CI/CD integration with multiple output formats (JSON, JUnit XML, Markdown) and dataset loaders (JSON, JSONL, CSV, YAML). See [CLI Reference](cli.md).

---

## Guides

| Guide | Description |
|-------|-------------|
| [Architecture](architecture.md) | Component diagrams and metric hierarchy |
| [Benchmarks](benchmarks.md) | BFCL, GAIA, ToolBench guides |
| [CLI Reference](cli.md) | Command-line tool usage |
| [Conversations](conversations.md) | Multi-turn testing guide |
| [Embedding Metrics](embedding-metrics.md) | Semantic similarity metrics |
| [Extensibility](extensibility.md) | Custom metrics, plugins, adapters |
| [Snapshots](snapshots.md) | Snapshot testing guide |
| [Tracing & Record/Replay](tracing.md) | Deterministic testing with trace capture |
| [Workflow Testing](workflows.md) | Multi-agent orchestration testing |
| [Roadmap](roadmap.md) | Future development plans |

---

## API Reference

API documentation is auto-generated from XML comments. See the [API Reference](api/index.md) section.

---

## Test Coverage

AgentEval has **763 tests** (2,289 across 3 target frameworks) covering all major features.

---

## Community

- **GitHub:** https://github.com/joslat/AgentEval
- **NuGet:** https://www.nuget.org/packages/AgentEval
- **Issues:** https://github.com/joslat/AgentEval/issues
- **Discussions:** https://github.com/joslat/AgentEval/discussions

---

## Contributing

Contributions are welcome! Please read:
- [Contributing Guide](https://github.com/joslat/AgentEval/blob/main/CONTRIBUTING.md)
- [Code of Conduct](https://github.com/joslat/AgentEval/blob/main/CODE_OF_CONDUCT.md)
- [Security Policy](https://github.com/joslat/AgentEval/blob/main/SECURITY.md)
