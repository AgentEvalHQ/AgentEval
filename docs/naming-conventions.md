# Naming Conventions

This document defines naming conventions for AgentEval APIs, metrics, and code.

> **📋 Decision Records:** For detailed rationale behind these conventions, see the [ADR folder](adr/README.md).

---

## Metric Names

> **ADR:** [001-metric-naming-prefixes.md](adr/001-metric-naming-prefixes.md)

### Prefix Convention

Metrics use prefixes to indicate their computation method and cost:

| Prefix | Computation | Cost | Examples |
|--------|-------------|------|----------|
| `llm_` | LLM-evaluated via prompt | $$$ (API calls) | `llm_faithfulness`, `llm_relevance` |
| `code_` | Computed by code logic | Free | `code_tool_success`, `code_tool_efficiency` |
| `embed_` | Computed via embeddings | $ (embedding API) | `embed_answer_similarity` |

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

## Result File Structure (Proposed)

> **ADR:** [002-result-directory-structure.md](adr/002-result-directory-structure.md)  
> **Status:** Proposed — Not yet implemented

When the `DirectoryExporter` is implemented, it will produce:

| File | Purpose | Format |
|------|---------|--------|
| `results.jsonl` | Per-test results | JSON Lines |
| `summary.json` | Aggregate statistics | JSON |
| `run.json` | Run metadata | JSON |
| `config.json` | Original config copy | JSON |

**Current state:** AgentEval exports single files via `JsonExporter`, `JUnitXmlExporter`, etc.

---

## Test Data Files (Existing)

AgentEval's `DatasetLoader` supports these formats:

| Extension | Format | Loader Method |
|-----------|--------|---------------|
| `.jsonl` | JSON Lines | `LoadJsonlAsync()` |
| `.json` | JSON Array | `LoadJsonAsync()` |
| `.csv` | CSV | `LoadCsvAsync()` |
| `.yaml` | YAML | `LoadYamlAsync()` |

These are **existing conventions**, not new proposals.

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
- `JUnitExporter`
- `MarkdownExporter`
- `DirectoryExporter`

---

## CLI Commands

### Command Structure

```
agenteval <command> [subcommand] [options]
```

### Commands

| Command | Purpose |
|---------|---------|
| `eval` | Run evaluation |
| `init` | Initialize config |
| `list` | List available items |
| `summary` | View run summaries |
| `diff` | Compare runs |

### Options

- Use `--kebab-case` for multi-word options
- Use short forms for common options: `-o` (output), `-c` (config), `-v` (verbose)

---

*Last updated: January 2026*
