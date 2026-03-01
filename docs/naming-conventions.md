# Naming Conventions

This document defines naming conventions for AgentEval APIs, metrics, and code.

> **📋 Decision Records:** For detailed rationale behind these conventions, see the [ADR folder](adr/README.md).

---

## Metric Names

> **ADR:** [001-metric-naming-prefixes.md](adr/001-metric-naming-prefixes.md)  
> **ADR:** [007-metrics-taxonomy.md](adr/007-metrics-taxonomy.md)

### Prefix Convention

Metrics use prefixes to indicate their computation method and cost:

| Prefix | Computation | Cost | MetricCategory Flag |
|--------|-------------|------|---------------------|
| `llm_` | LLM-evaluated via prompt | $$$ (API calls) | `MetricCategory.LLMBased` |
| `code_` | Computed by code logic | Free | `MetricCategory.CodeBased` |
| `embed_` | Computed via embeddings | $ (embedding API) | `MetricCategory.EmbeddingBased` |

### Domain Categories

Metrics are also categorized by evaluation domain:

| Domain | Interface | MetricCategory Flag | Examples |
|--------|-----------|---------------------|----------|
| RAG | `IRAGMetric` | `MetricCategory.RAG` | Faithfulness, Relevance |
| Agentic | `IAgenticMetric` | `MetricCategory.Agentic` | Tool Selection, Tool Success |
| Conversation | Special | `MetricCategory.Conversation` | ConversationCompleteness |
| Safety | `ISafetyMetric` | `MetricCategory.Safety` | Toxicity, Bias |

### Complete Metric Reference

| Metric Name | Type | Description |
|-------------|------|-------------|
| `llm_faithfulness` | LLM | Response grounded in context |
| `llm_relevance` | LLM | Response addresses the question |
| `llm_context_precision` | LLM | Retrieved context is relevant |
| `llm_context_recall` | LLM | Context contains needed info |
| `llm_answer_correctness` | LLM | Answer matches ground truth |
| `llm_tool_selection` | LLM | Correct tools were chosen |
| `llm_tool_arguments` | LLM | Tool arguments are correct |
| `llm_task_completion` | LLM | Task was completed successfully |
| `embed_answer_similarity` | Embedding | Answer similar to ground truth |
| `embed_response_context` | Embedding | Response relates to context |
| `embed_query_context` | Embedding | Query relates to context |
| `code_tool_success` | Code | Tools executed without errors |
| `code_tool_efficiency` | Code | Minimal tool calls used |

### Usage Examples

```csharp
// Filter by cost
var freeMetrics = metrics.Where(m => m.Name.StartsWith("code_"));
var llmMetrics = metrics.Where(m => m.Name.StartsWith("llm_"));
```

---

## Result File Structure

> **ADR:** [002-result-directory-structure.md](adr/002-result-directory-structure.md)  
> **Status:** Accepted — Implemented via `DirectoryExporter`

The `DirectoryExporter` produces:

| File | Purpose | Format |
|------|---------|--------|
| `results.jsonl` | Per-test results | JSON Lines |
| `summary.json` | Aggregate statistics | JSON |
| `run.json` | Run metadata | JSON |
| *(original filename)* | Original config copy | *(original)* |

**Usage:**

```csharp
var exporter = new DirectoryExporter();
await exporter.ExportToDirectoryAsync(report, "./results/baseline");

// Or via CLI:
// agenteval eval --azure --model gpt-4o --dataset tests.yaml --output-dir ./results
```

---

## Test Data Files (Existing)

AgentEval loads test datasets through the `IDatasetLoader` interface via `DatasetLoaderFactory`:

| Extension(s) | Format | Loader Class | Usage |
|---|---|---|---|
| `.jsonl`, `.ndjson` | JSON Lines | `JsonlDatasetLoader` | `LoadAsync(path)` / `LoadStreamingAsync(path)` |
| `.json` | JSON Array | `JsonDatasetLoader` | `LoadAsync(path)` / `LoadStreamingAsync(path)` |
| `.csv` | CSV | `CsvDatasetLoader` | `LoadAsync(path)` / `LoadStreamingAsync(path)` |
| `.tsv` | TSV | `CsvDatasetLoader('\t')` | `LoadAsync(path)` / `LoadStreamingAsync(path)` |
| `.yaml`, `.yml` | YAML | `YamlDatasetLoader` | `LoadAsync(path)` / `LoadStreamingAsync(path)` |

Entry point: `DatasetLoaderFactory.CreateFromExtension(".jsonl")` returns an `IDatasetLoader`.

---

## Class Naming

### Metrics

```
[Category][Name]Metric
```

Examples:
- `FaithfulnessMetric`
- `ToolSelectionMetric`
- `ResponseLengthMetric`

### Assertions

```
[Subject]Assertions
```

Examples:
- `ToolUsageAssertions`
- `ResponseAssertions`
- `PerformanceAssertions`
- `WorkflowAssertions`

### Exporters

```
[Format]Exporter
```

Examples:
- `JsonExporter`
- `JUnitXmlExporter`
- `MarkdownExporter`
- `DirectoryExporter`

---

## See Also

- [Metrics Reference](metrics-reference.md) - Complete metric catalog with usage guidance
- [Evaluation Guide](evaluation-guide.md) - How to choose the right metrics
- [Architecture](architecture.md) - System design and metric hierarchy

---

*Last updated: January 2026*
