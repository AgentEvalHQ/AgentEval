# ADR-009: Benchmark Strategy

## Status
**Superseded** (2026-05-17) — the v0.9.0-beta release removed the library-API `AgenticBenchmark` class described below (with `RunToolAccuracyBenchmarkAsync` / `RunTaskCompletionBenchmarkAsync` / `RunMultiStepReasoningBenchmarkAsync`) and replaced it with the preset-factory `AgentEval.Evals.Agentic.AgenticBenchmark` covering ~60 evaluators across 11 presets. The strategy described here was correct for v0.3-v0.8 betas; today the canonical agentic benchmark surface lives in `AgentEval.Evals.Agentic` and is driven via `agenteval bench agentic --preset X`. `PerformanceBenchmark` (the in-process latency/throughput/cost measurement) remains as documented.

Originally accepted: 2026-01-13.

## Date
2026-01-13

## Context

AgentEval aims to provide comprehensive evaluation capabilities for AI agents. A key question is: **How should AgentEval support industry-standard benchmarks like BFCL, GAIA, ToolBench, and HumanEval?**

The agentic AI ecosystem has several established benchmarks:

| Benchmark | Focus | Dataset Source | Complexity |
|-----------|-------|----------------|------------|
| **BFCL** (Berkeley Function Calling) | Tool/function calling accuracy | HuggingFace | Low |
| **GAIA** | General AI Assistant capabilities | HuggingFace | Medium |
| **ToolBench** | Complex API tool usage | GitHub | Medium |
| **HumanEval** | Code generation correctness | OpenAI | High (requires code execution) |
| **WebArena** | Web browsing tasks | Requires browser | Very High |

### Key Questions

1. Should AgentEval include dataset loaders for these benchmarks?
2. Should AgentEval auto-download from HuggingFace?
3. How do we handle benchmarks requiring code execution (HumanEval)?
4. What's the right abstraction for "running a benchmark"?

## Decision

### Tiered Benchmark Support

We will implement benchmark support in **three tiers**:

#### Tier 1: Core Infrastructure (In Scope - Phase 1)
- **Benchmark abstractions**: `IBenchmark`, `IBenchmarkDataset`, `IBenchmarkResult`
- **Manual test case patterns**: Users create BFCL/GAIA-style test cases manually
- **Result aggregation**: Accuracy, pass rate, latency statistics
- **Leaderboard comparison**: Compare your scores against published results

#### Tier 2: Dataset Loaders (In Scope - Phase 2)
- **BFCL Loader**: Download and parse BFCL dataset from HuggingFace
- **GAIA Loader**: Download and parse GAIA dataset
- **ToolBench Loader**: Download and parse ToolBench scenarios
- **Caching**: Local caching of downloaded datasets
- **Subset selection**: Run on subsets for quick validation

#### Tier 3: Execution Sandboxes (Out of Scope - Future Consideration)
- **HumanEval**: Requires secure code execution sandbox
- **WebArena**: Requires browser automation
- **SWE-bench**: Requires git/code editing capabilities

### Rationale

**Why Tier 1 First:**
- Provides immediate value with zero external dependencies
- Users can create benchmark-style tests without downloading datasets
- Works in air-gapped environments

**Why Tier 2 In Scope:**
- BFCL, GAIA, ToolBench are static datasets (no execution needed)
- High community value - "run BFCL with one command"
- Manageable implementation effort (weeks, not months)

**Why Tier 3 Out of Scope (for now):**
- **Security**: Executing untrusted LLM-generated code is dangerous
- **Complexity**: Sandboxing requires Docker, process isolation, resource limits
- **Maintenance**: These are moving targets with frequent updates
- **Focus**: AgentEval's core value is agent evaluation, not code execution

### HumanEval: If We Did It

If we decided to support HumanEval in the future, the approach would be:

1. **Docker-based sandbox**: Each code execution in isolated container
2. **Resource limits**: CPU, memory, time limits per execution
3. **Network isolation**: No network access from sandbox
4. **Pass@k scoring**: Run k samples, measure success rate
5. **Language support**: Python first, then expand

Estimated effort: 4-6 weeks for a secure implementation.

## Implementation

### Phase 1: Core Infrastructure

```csharp
// Core abstractions
public interface IBenchmark
{
    string Name { get; }
    string Version { get; }
    Task<BenchmarkResult> RunAsync(IEvaluableAgent agent, BenchmarkOptions options);
}

public interface IBenchmarkDataset
{
    string Name { get; }
    int Count { get; }
    IAsyncEnumerable<BenchmarkTestCase> GetTestCasesAsync();
}

public interface IBenchmarkResult
{
    string BenchmarkName { get; }
    double OverallScore { get; }
    Dictionary<string, double> CategoryScores { get; }
    BenchmarkStatistics Statistics { get; }
}
```

### Phase 2: Dataset Loaders

```csharp
// BFCL loader example
public class BfclDatasetLoader : IBenchmarkDatasetLoader
{
    private const string DatasetUrl = "https://huggingface.co/datasets/gorilla-llm/Berkeley-Function-Calling-Leaderboard";
    
    public async Task<IBenchmarkDataset> LoadAsync(BfclCategory category)
    {
        // Download from HuggingFace
        // Parse JSON structure
        // Return typed dataset
    }
}

// Usage
var loader = new BfclDatasetLoader();
var dataset = await loader.LoadAsync(BfclCategory.SimplePython);
var benchmark = new BfclBenchmark(dataset);
var result = await benchmark.RunAsync(agent, options);
```

### Directory Structure

```
src/AgentEval/
├── Benchmarks/
│   ├── Core/
│   │   ├── IBenchmark.cs
│   │   ├── IBenchmarkDataset.cs
│   │   ├── BenchmarkResult.cs
│   │   └── BenchmarkRunner.cs
│   ├── Datasets/
│   │   ├── BfclDatasetLoader.cs
│   │   ├── GaiaDatasetLoader.cs
│   │   └── ToolBenchDatasetLoader.cs
│   └── Implementations/
│       ├── BfclBenchmark.cs
│       ├── GaiaBenchmark.cs
│       └── ToolBenchBenchmark.cs
```

## Consequences

### Positive
- Clear scope boundaries prevent scope creep
- Phase 1 delivers immediate value
- Phase 2 enables "run BFCL with one line" experience
- Security-conscious approach to code execution

### Negative
- No HumanEval support initially
- Users wanting HumanEval must implement their own sandbox
- Some community pressure for "complete" benchmark support

### Neutral
- Dataset loaders add HuggingFace dependency (optional)
- Caching requires disk space management

## Alternatives Considered

### 1. Full Benchmark Support Including HumanEval
**Rejected**: Security risks outweigh benefits. Code execution requires significant investment in sandboxing.

### 2. No Benchmark Support (Just Patterns)
**Rejected**: Misses opportunity to provide significant value with dataset loaders.

### 3. External Benchmark Tool
**Rejected**: Fragmented experience. Users want one tool, not two.

## Implementation Progress (as of 2026-02-25)

The ADR's tiered plan remains the roadmap. Below is a snapshot of what has been built so far toward Phase 1, and what remains.

### Phase 1 — What Has Been Implemented

| Component | Current State |
|-----------|--------------|
| **PerformanceBenchmark** | Latency (single + multi-prompt), throughput (concurrent), cost estimation |
| **AgenticBenchmark** | Tool accuracy (BFCL-style), task completion (GAIA-style), multi-step reasoning |
| **Dataset loading** | `DatasetLoaderFactory` supports JSONL/JSON/YAML/CSV; bridge extensions convert to benchmark types |
| **DI-compatible extraction** | `AgenticBenchmark` accepts optional `IToolUsageExtractor` |
| **Configurable evaluation** | `AddDefaultCompletionCriteria` option for task completion |

### Phase 1 — What Is Still Pending

| ADR Proposal | Status | Notes |
|-------------|--------|-------|
| `IBenchmark` / `IBenchmarkDataset` / `IBenchmarkResult` interfaces | Not yet implemented | Current classes are concrete; interfaces can be extracted when Tier 2 dataset loaders arrive and a polymorphic contract becomes valuable |
| `BenchmarkRunner` orchestrator | Not yet implemented | Direct method calls (`benchmark.RunXxxAsync()`) are used today; a runner abstraction may be added for CLI integration |
| `WorkflowPerformanceBenchmark` | Planned | Documented as future feature in benchmarks.md |
| Regression detection infrastructure | Pattern documented | No baseline storage yet |

### Phase 2 — Not Yet Started

Auto-download dataset loaders for BFCL, GAIA, ToolBench (HuggingFace integration with caching) are planned but not yet started.

### Phase 3 (Out of Scope)

Execution sandboxes (HumanEval, WebArena, SWE-bench) remain deferred per the original decision.

### Note on DI Registration

Benchmarks are currently instantiated directly (not registered in DI), consistent with the AGENTS.md "test-time tools" policy (see ADR-006). This may be revisited when CLI or runner infrastructure arrives.

## References

- [BFCL Leaderboard](https://gorilla.cs.berkeley.edu/leaderboard.html)
- [GAIA Benchmark](https://huggingface.co/datasets/gaia-benchmark/GAIA)
- [ToolBench](https://github.com/OpenBMB/ToolBench)
- [HumanEval](https://github.com/openai/human-eval)
- ADR-005: Model Comparison & stochastic evaluation
- ADR-006: Service-Based Architecture & DI (for "test-time tools" policy)
