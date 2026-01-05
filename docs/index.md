# AgentEval Documentation

Welcome to the **AgentEval** documentation. AgentEval is the first .NET-native AI agent testing, evaluation, and benchmarking framework.

## Getting Started

> 📚 See the [samples](https://github.com/joslat/AgentEval/tree/main/samples) for usage examples.

## Features

### Tool Usage Assertions
Assert on tool calls, order, arguments, results, errors, and duration with a fluent API.

### Multi-Turn Conversation Testing
Test complex multi-turn conversations with the `ConversationalTestCase` builder, `ConversationRunner`, and `ConversationCompletenessMetric`. See [Conversations](conversations.md).

### Snapshot Testing
Compare agent responses against saved baselines with JSON diff, field ignoring, pattern scrubbing, and semantic similarity. See [Snapshots](snapshots.md).

### Performance Metrics
Track latency, TTFT (Time To First Token), tokens, estimated cost, and per-tool timing.

### RAG Metrics
Evaluate faithfulness, relevance, context precision/recall, and answer correctness.

### Agentic Metrics
Measure tool selection accuracy, tool arguments, tool success, task completion, and efficiency.

### Benchmarks
Run latency, throughput, cost, and agentic benchmarks with percentile statistics (p50/p90/p95/p99). See [Benchmarks](benchmarks.md).

### CLI Tool
Full command-line interface for CI/CD integration with multiple output formats (JSON, JUnit XML, Markdown) and dataset loaders (JSON, JSONL, CSV, YAML). See [CLI Reference](cli.md).

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

## API Reference

API documentation is auto-generated from XML comments. See the [API Reference](api/index.md) section.

## Test Coverage

AgentEval has **554 tests** covering all major features.

## Contributing

Contributions are welcome! Please see the [GitHub repository](https://github.com/joslat/AgentEval) for guidelines.
