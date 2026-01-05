# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **CLI eval command** with real dataset validation
  - Loads datasets from YAML, JSON, JSONL, and CSV files
  - Validates test case completeness, ground truth, expected tools, and context
  - Outputs results in JSON, JUnit XML, Markdown, or TRX formats
  - Cross-platform color support with NO_COLOR environment variable respect
- **Sample datasets** for quick start
  - `samples/datasets/travel-agent.yaml` - agentic evaluation with tool usage
  - `samples/datasets/rag-qa.yaml` - RAG evaluation with context documents
  - `samples/datasets/README.md` - comprehensive dataset format documentation
- **YAML dataset loader** with flexible field aliasing
  - Supports both `expected_output` and `expectedOutput` naming conventions
  - Supports `ground_truth`, `expected_tools`, and `context` fields
  - Full YAML 1.2 compliance via YamlDotNet

### Changed
- **Test project reorganization** into logical folder structure:
  - `Core/` - AgentEvalBuilder, Logger, MetricRegistry, Retry, Normalizer, Concurrency tests
  - `Metrics/RAG/` - Faithfulness, Relevance, Context Precision/Recall, Answer Correctness
  - `Metrics/Agentic/` - Tool Selection, Arguments, Success, Efficiency, Task Completion
  - `DataLoaders/` - Dataset loader and serialization tests
  - `Exporters/` - Result exporter tests
  - `Testing/` - FakeChatClient, ConversationRunner, ConversationalTestCase tests
  - `Assertions/` - Tool usage and response assertion tests
  - `Models/` - Domain model tests
  - `Benchmarks/` - Performance and agentic benchmark tests
  - `MAF/` - Microsoft Agent Framework integration tests
- **CLI ConsoleHelper** for improved cross-platform terminal support
  - Detects NO_COLOR environment variable
  - Detects TERM=dumb terminals
  - Gracefully handles output redirection (piping to files)

### Fixed
- YAML loader tests now use correct 4-space indentation matching YAML standards

---

## [0.1.2-alpha] - 2026-01-04

### Added
- Additional test coverage for core components
- XML documentation generation enabled in project configuration
- DocFX build scripts (PowerShell and Batch) for automated API documentation generation
- Comprehensive documentation guides (GENERATE-DOCS.md, DOCUMENTATION-SUMMARY.md)

### Changed
- Project now generates XML documentation files for all target frameworks (net8.0, net9.0, net10.0)
- Suppressed CS1591 warnings for undocumented members

---

## [0.1.1-alpha] - 2026-01-03

### Added
- SourceLink support for debugging into source code
- Symbol packages (.snupkg) published to NuGet.org
- NuGet package icon (AgentEvalNugetLogoAE.png)
- Azure OpenAI environment variables in CI/CD workflows

### Changed
- Repository restructured to standard .NET layout (src/, samples/, tests/, docs/)
- Central package management with `Directory.Packages.props`
- Shared build configuration with `Directory.Build.props`
- GitHub Actions CI now tests on .NET 8, 9, and 10 across Ubuntu and Windows
- CI workflow optimized with NuGet caching and fail-fast disabled

### Infrastructure
- GitHub Actions CI workflow for automated build and test
- GitHub Actions release workflow for NuGet publishing
- DocFX documentation scaffolding
- EditorConfig for consistent code style

---

## [0.1.0-alpha] - 2026-01-02

### Added

#### Core Framework
- First .NET-native AI agent testing, evaluation, and benchmarking framework
- Full Microsoft Agent Framework (MAF) integration via `MAFAgentAdapter` and `MAFTestHarness`
- Extensible adapter pattern supporting `IChatClient` and other frameworks
- Plugin system with `IAgentEvalPlugin` interface

#### Tool Usage Tracking & Assertions
- `ToolCallRecord` for capturing tool invocations with timing, arguments, results, and errors
- `ToolCallTimeline` for visualizing parallel tool execution
- Fluent assertions: `HaveCalledTool()`, `BeforeTool()`, `WithArgument()`, `HaveNoErrors()`
- Tool usage reports with success/failure metrics

#### Performance Metrics
- Real-time performance tracking with TTFT (Time To First Token)
- Per-tool timing and execution waterfall data
- Token counting (prompt/completion/total)
- Cost estimation for 8+ models (GPT-4o, GPT-4o-mini, Claude 3.5, Claude 3 Opus, GPT-4 Turbo, GPT-3.5 Turbo, o1-preview, o1-mini)
- Performance assertions: `HaveTotalDurationUnder()`, `HaveTimeToFirstTokenUnder()`, `HaveEstimatedCostUnder()`

#### RAG Metrics
- Faithfulness metric (grounded in context)
- Relevance metric (response addresses query)
- Context Precision metric
- Context Recall metric
- Answer Correctness metric

#### Agentic Metrics
- Tool Selection metric (chose appropriate tools)
- Tool Arguments metric (correct arguments passed)
- Tool Success metric (tools executed successfully)
- Task Completion metric (agent completed the task)
- Efficiency metric (minimal steps, tokens, time)

#### Benchmarks
- `PerformanceBenchmark` for latency/throughput/cost analysis
- `AgenticBenchmark` for multi-step agentic task evaluation
- Percentile statistics (p50, p90, p95, p99)
- Summary statistics (mean, min, max, standard deviation)

#### Testing Infrastructure
- `FakeChatClient` for zero-dependency unit testing
- `TestCase` model with inputs, expected outputs, evaluation criteria
- `TestResult` with comprehensive run data
- Trace-first failure reporting with structured diagnostics

#### Observability
- `IAgentEvalLogger` abstraction with console and Microsoft.Extensions.Logging adapters
- Run artifacts for debugging and "time travel" inspection
- Designed for OpenTelemetry (OTel) integration

### Technical Details
- 210+ unit tests with comprehensive coverage
- Multi-target framework support: .NET 8.0, 9.0, 10.0
- Zero-dependency core (optional integrations for MAF, Azure OpenAI)

---

## Future Releases

### Planned Packages
- `AgentEval` (core) ✅ This release
- `AgentEval.Maf` (MAF integration) - planned
- `AgentEval.TestKit` (fixtures/builders/helpers) - planned
- `AgentEval.Tracing` (OTel + run artifacts) - planned
- `AgentEval.Studio` (workflow visualizer / time-travel UI) - future

[Unreleased]: https://github.com/joslat/AgentEval/compare/v0.1.2-alpha...HEAD
[0.1.2-alpha]: https://github.com/joslat/AgentEval/compare/v0.1.1-alpha...v0.1.2-alpha
[0.1.1-alpha]: https://github.com/joslat/AgentEval/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/joslat/AgentEval/releases/tag/v0.1.0-alpha
