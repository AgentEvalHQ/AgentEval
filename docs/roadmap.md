# Roadmap

AgentEval is actively developed. This page outlines completed features and what's coming next.

## Current Status: v0.2.0-beta

AgentEval is in **beta** with all core features complete and ready for production use.

---

## What's Shipped

### Core Evaluation & Testing
- **evaluation harness** for AI agents (`MAFEvaluationHarness`, `IEvaluationHarness`)
- **Fluent assertions** for tool usage, performance, and responses
- **stochastic evaluation** - run N times, assert on pass rates
- **Model comparison** with statistical significance
- **Multi-turn conversation testing** (`ConversationRunner`)
- **Workflow testing** for multi-agent orchestration
- **Snapshot testing** for regression detection

### Metrics & Evaluation
- **RAG metrics:** Faithfulness, Relevance, Context Precision/Recall, Answer Correctness
- **Agentic metrics:** Tool Selection, Tool Arguments, Tool Success, Task Completion, Efficiency
- **Embedding-based similarity** metrics
- **Calibrated judges** with confidence intervals and voting strategies

### Performance & Observability
- **Streaming support** with real-time callbacks
- **Time to First Token (TTFT)** tracking
- **Per-tool timing** and execution waterfall
- **Token counting and cost estimation** (8+ models)
- **Performance benchmarks** (latency, throughput, cost)
- **Trace record/replay** - capture and reproduce executions deterministically

### Behavioral Policies
- `NeverCallTool()` - forbid specific tool calls
- `NeverPassArgumentMatching()` - forbid argument patterns (PII, etc.)
- `MustConfirmBefore()` - require confirmation before sensitive operations

### CI/CD Integration
- **CLI tool** (`agenteval eval`, `agenteval init`, `agenteval list`)
- **Result exporters:** JSON, JUnit XML, Markdown, TRX
- **Dataset loaders:** JSON, JSONL, CSV, YAML
- **Rich test output** with verbosity levels and trace artifacts

### Framework Support
- **Microsoft Agent Framework (MAF)** adapter
- **Generic `IChatClient`** adapter
- **Microsoft.Extensions.AI.Evaluation** integration

---

## What's Next

We're focused on making AgentEval the most comprehensive evaluation toolkit for AI agents in .NET. Upcoming areas include:

- **CLI enhancements** - summary views, diff comparisons, visualization
- **Additional framework adapters** - Semantic Kernel, LangChain.NET
- **Visual reporting** - HTML reports, interactive diagrams
- **Experiment management** - A/B testing, baseline comparison
- **Safety testing** - red-teaming, adversarial inputs

---

## Feature Requests

Have a feature request? [Open an issue](https://github.com/joslat/AgentEval/issues/new?template=feature_request.md) on GitHub!

We prioritize based on community needs and contributions.

---

## Version History

| Version | Date | Highlights |
|---------|------|------------|
| 0.2.0-beta | Jan 2026 | Evaluation toolkit, stochastic evaluation, model comparison, trace replay |
| 0.1.x-alpha | Jan 2026 | Initial alpha releases |

See [CHANGELOG.md](https://github.com/joslat/AgentEval/blob/main/CHANGELOG.md) for detailed release notes.
