# ADR-002: Result Directory Structure

**Status:** Proposed  
**Date:** 2026-01-07  
**Decision Makers:** AgentEval Contributors

---

## Context

### Current State

AgentEval currently exports results via `IResultExporter` implementations:

| Exporter | Output | Use Case |
|----------|--------|----------|
| `JsonExporter` | Single `.json` file | Programmatic access |
| `JUnitXmlExporter` | Single `.xml` file | CI/CD (Jenkins, Azure DevOps) |
| `TrxExporter` | Single `.trx` file | Visual Studio Test Explorer |
| `MarkdownExporter` | Single `.md` file | Human-readable reports |

**Current Usage:**
```csharp
var exporter = new JsonExporter();
await exporter.ExportAsync(report, stream);
// Produces: single JSON file with all results
```

### Problem

When users run multiple evaluations (A/B testing, regression testing, version comparison), they need to:

1. **Compare runs** — Side-by-side comparison of results
2. **Track history** — See how metrics change over time
3. **Reproduce runs** — Know what configuration produced results
4. **Aggregate** — Combine results from multiple files

**Current limitations:**

- Each run produces a single file — no standard location
- No run metadata (timestamp, config, parameters) stored with results
- No standard format for cross-run comparison tools
- Users must manually organize output files

### Competitor Approach

`ai-rag-chat-evaluator` uses a structured directory per run:

```
results/
└── experiment_2026-01-07/
    ├── eval_results.jsonl     # Per-question results (streaming-friendly)
    ├── summary.json           # Aggregate statistics
    ├── evaluate_parameters.json  # Run parameters
    └── config.json            # Original config (reproducibility)
```

This enables their `summary` and `diff` CLI commands for cross-run comparison.

---

## Decision

**Add a new `DirectoryExporter` that outputs structured result directories:**

```
results/
└── 2026-01-07_v1.2.3/
    ├── results.jsonl          # Per-test results (JSON Lines)
    ├── summary.json           # Aggregate statistics
    ├── run.json               # Run metadata and parameters
    └── config.json            # Original config (if provided)
```

### File Specifications

#### results.jsonl (Per-Test Results)

JSON Lines format — one JSON object per line, enabling streaming reads.

```jsonl
{"name":"Tool_ordering_test","passed":true,"score":100,"durationMs":1234,"metrics":{"llm_relevance":92}}
{"name":"Response_quality_test","passed":false,"score":65,"durationMs":987,"error":"Below threshold"}
```

**Why JSONL:**
- Streaming-friendly (read line by line, no full parse)
- Appendable (can add results during run)
- Standard format (used by OpenAI, HuggingFace, etc.)

#### summary.json (Aggregates)

```json
{
  "runId": "abc123",
  "timestamp": "2026-01-07T10:30:00Z",
  "stats": {
    "total": 50,
    "passed": 45,
    "failed": 5,
    "passRate": 0.90
  },
  "metrics": {
    "llm_relevance": { "mean": 85.2, "min": 62, "max": 100 },
    "llm_faithfulness": { "mean": 91.0, "min": 78, "max": 100 },
    "code_latency": { "mean": 1.2, "p50": 1.0, "p95": 2.5, "p99": 3.1 }
  }
}
```

#### run.json (Run Metadata)

```json
{
  "runId": "abc123",
  "name": "Baseline v1.2.3",
  "timestamp": "2026-01-07T10:30:00Z",
  "duration": "00:05:32",
  "agent": {
    "name": "CustomerSupportAgent",
    "model": "gpt-4o",
    "version": "1.2.3"
  },
  "environment": {
    "machine": "build-agent-01",
    "os": "Windows 11",
    "dotnetVersion": "10.0.0"
  },
  "parameters": {
    "temperature": 0.7,
    "maxTokens": 1000
  }
}
```

#### config.json (Original Config Copy)

If user provides a config file, copy it for reproducibility.

---

## Consequences

### Positive

- **Cross-Run Comparison** — Enables `agenteval summary` and `agenteval diff` commands
- **Reproducibility** — Run parameters stored with results
- **Streaming** — JSONL enables processing large result sets
- **History** — Directory-per-run enables time-series analysis
- **Standard Format** — JSONL widely used in ML/AI tooling

### Negative

- **More Files** — 4 files instead of 1 per run
- **Disk Space** — Slightly more storage (mitigated by JSONL efficiency)
- **Migration** — Users with existing JSON exports need to update scripts

### Neutral

- **Backward Compatible** — Existing exporters unchanged
- **Optional** — Users choose which exporter to use

---

## Alternatives Considered

### Alternative A: Keep Single File
**Rejected** — Doesn't enable comparison tooling.

### Alternative B: Extend JsonExporter with Metadata
```json
{
  "metadata": { ... },
  "results": [ ... ]
}
```
**Rejected** — Requires full file parse; not streaming-friendly.

### Alternative C: SQLite Database
**Rejected** — Adds dependency; less portable than JSON.

### Alternative D: Use Competitor's Exact Format
**Rejected** — Their format is Python-specific; ours should be .NET-idiomatic.

---

## Implementation

1. Create `DirectoryExporter : IResultExporter`
2. Add `--output-dir` option to CLI `eval` command
3. Implement `summary` and `diff` commands that read this format
4. Document format specification in docs

---

## References

- [ai-rag-chat-evaluator result structure](https://github.com/Azure-Samples/ai-rag-chat-evaluator)
- [JSON Lines specification](https://jsonlines.org/)
- [Competitor Analysis](../strategy/competitor-analysis.md)
