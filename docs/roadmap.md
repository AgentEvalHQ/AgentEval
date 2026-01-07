# Roadmap

AgentEval is actively developed. This page outlines completed features and planned enhancements.

## Current Status: v1.0.0-alpha

AgentEval is in **alpha** with all core features complete and ready for production use.

---

## ✅ Completed Features

### Core Testing (v1.0.0-alpha)
- [x] Test harness for AI agents (`MAFTestHarness`, `ITestHarness`)
- [x] Fluent assertions for tool usage, performance, and responses
- [x] Multi-turn conversation testing (`ConversationRunner`)
- [x] Snapshot testing for regression detection (`SnapshotComparer`)
- [x] Workflow testing for multi-agent orchestration

### Metrics & Evaluation
- [x] RAG metrics: Faithfulness, Relevance, Context Precision/Recall, Answer Correctness
- [x] Agentic metrics: Tool Selection, Tool Arguments, Tool Success, Task Completion, Efficiency
- [x] Embedding-based similarity metrics
- [x] AI-powered response evaluation

### Performance & Observability
- [x] Streaming support with real-time callbacks
- [x] Time to First Token (TTFT) tracking
- [x] Per-tool timing and execution waterfall
- [x] Token counting and cost estimation (8+ models)
- [x] Performance benchmarks (latency, throughput, cost)

### CI/CD Integration
- [x] CLI tool (`agenteval eval`, `agenteval init`, `agenteval list`)
- [x] Result exporters: JSON, JUnit XML, Markdown, TRX
- [x] Dataset loaders: JSON, JSONL, CSV, YAML

### Framework Support
- [x] Microsoft Agent Framework (MAF) adapter
- [x] Generic `IChatClient` adapter
- [x] Microsoft.Extensions.AI.Evaluation integration

---

## 🔄 In Progress

### Documentation & Community
- [x] Community files (CONTRIBUTING, CODE_OF_CONDUCT, SECURITY)
- [x] GitHub issue and PR templates
- [x] Installation and walkthrough documentation
- [ ] Complete API reference documentation (auto-generated from XML docs)
- [ ] Video tutorials and walkthroughs
- [ ] Community Discord server (deferred until 50+ active users)

---

## 📋 Planned Features

### Short-term (Q1 2026)
- [x] Workflow assertions P0 enhancements (`because`, structured exceptions)
- [ ] CLI `summary` command — tabular view of runs in directory
- [ ] CLI `diff` command — side-by-side answer comparison
- [ ] Standardized result directory structure (eval_results.jsonl, summary.json)
- [ ] Console visualization enhancements (Spectre.Console tables, progress)
- [ ] Visual assertion reports (ASCII diagrams)
- [ ] GitHub Actions workflow templates
- [ ] Visual Studio test integration
- [ ] Additional framework adapters (Semantic Kernel)

### Medium-term (Q2 2026)
- [ ] Code metrics (ResponseLength, HasCitation, CitationMatch)
- [ ] Refusal quality metric ("dontknowness" for unanswerable questions)
- [ ] Multi-agent orchestration contracts
- [ ] Assertion telemetry (local storage)
- [ ] Self-healing assertions (rule-based)
- [ ] Record/Replay for deterministic testing
- [ ] Experiment management and A/B testing
- [ ] Baseline comparison dashboard

### Long-term (Q3-Q4 2026)
- [ ] Visual assertion reports (HTML/interactive) — *Premium*
- [ ] Assertion telemetry (cloud dashboard) — *Premium*
- [ ] Self-healing assertions (LLM-powered) — *Premium*
- [ ] Assertion-driven prompt optimization — *Premium*
- [ ] Self-hosted dashboard & baseline comparison — *Enterprise Self-Host*
- [ ] AgentEval Studio (self-hosted option) — *Enterprise Self-Host*
- [ ] Red-teaming and safety testing
- [ ] Synthetic dataset generation
- [ ] AgentEval Studio (visual workflow editor)

> 📖 See [New Features Exploration](strategy/new-features-exploration.md) for detailed feature specifications and premium strategy.

---

## Feature Requests

Have a feature request? [Open an issue](https://github.com/joslat/AgentEval/issues/new?template=feature_request.md) on GitHub!

---

## Version History

| Version | Date | Highlights |
|---------|------|------------|
| 1.0.0-alpha | Jan 2026 | Initial public release with core features |

See [CHANGELOG.md](https://github.com/joslat/AgentEval/blob/main/CHANGELOG.md) for detailed release notes.
