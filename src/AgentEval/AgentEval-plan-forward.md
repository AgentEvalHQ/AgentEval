# AgentEval - Forward Plan & Roadmap

> **Strategic roadmap to make AgentEval THE .NET AI Agent Evaluation Framework**

**Created:** January 2026  
**Version:** 2.0  
**Status:** Strategic Planning  
**Last Updated:** January 5, 2026

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [External Framework Analysis](#external-framework-analysis)
3. [Competitive Deep Dive](#competitive-deep-dive)
4. [Current State Analysis](#current-state-analysis)
5. [Code Quality Review](#code-quality-review)
6. [Architecture Overview](#architecture-overview)
7. [Strategic Vision](#strategic-vision)
8. [Phase 1: Polish & Publication](#phase-1-polish--publication)
9. [Phase 2: Enterprise Features](#phase-2-enterprise-features)
10. [Phase 3: Ecosystem & Community](#phase-3-ecosystem--community)
11. [Phase 4: Advanced Capabilities](#phase-4-advanced-capabilities)
12. [Phase 5: Production Infrastructure](#phase-5-production-infrastructure) ⭐ NEW
13. [Benchmark Compatibility](#benchmark-compatibility) ⭐ NEW
14. [Technical Debt & Optimizations](#technical-debt--optimizations)
15. [Competitive Positioning](#competitive-positioning)
16. [Success Metrics](#success-metrics)

---

## Executive Summary

AgentEval is positioned to become **THE** .NET AI agent evaluation framework. With core functionality complete (**570 tests passing** across 3 target frameworks), the focus shifts to:

1. **Polish & Publish** - Make it production-ready and ship to NuGet
2. **Production Infrastructure** - CLI tool, result exporters, snapshot testing ⭐ CRITICAL
3. **Enterprise** - Add features large organizations need
4. **Community** - Build ecosystem and developer experience
5. **Innovate** - Red-teaming, synthetic data, advanced safety

**Key Insight:** No other framework offers native .NET integration with this level of completeness. This is our moat.

**New Insight (Jan 2026):** After analyzing Microsoft.Extensions.AI.Evaluation and kbeaugrand/KernelMemory.Evaluation, we have a unique opportunity to be the bridge between Microsoft's official evaluators and the agentic/tool-tracking features they lack.

**Critical Gap Identified (Jan 5, 2026):** Competitive analysis reveals four critical missing features that block enterprise adoption:
1. **CLI Tool** - Required for CI/CD integration (Promptfoo's killer feature)
2. **Result Exporters** - JUnit XML, Markdown, JSON for pipeline integration
3. **Snapshot/Baseline Testing** - Regression detection without re-running agents
4. **Dataset Loaders** - Load BFCL, GAIA, ToolBench and custom test suites

**Recent Additions (Phase 5-7 Complete):**
- ✅ Trace-first failure reporting with FailureReport and ToolCallTimeline
- ✅ IAgentEvalLogger abstraction with ConsoleAgentEvalLogger, NullAgentEvalLogger, MicrosoftExtensionsLoggingAdapter
- ✅ FakeChatClient for zero-dependency testing of LLM metrics
- ✅ Metric tests for FaithfulnessMetric, ToolSelectionMetric, ToolSuccessMetric
- ✅ RAG metric tests (Relevance, ContextPrecision, ContextRecall, AnswerCorrectness)
- ✅ Agentic metric tests (ToolArguments, TaskCompletion, ToolEfficiency)
- ✅ Integration tests (MicrosoftEvaluatorAdapter, AgenticBenchmark, MAFTestHarness)
- ✅ **Production Infrastructure (Phase 5):**
  - ✅ CLI tool with real eval command (YAML/JSON/JSONL/CSV dataset support)
  - ✅ Result exporters (JSON, JUnit XML, Markdown, TRX)
  - ✅ Snapshot testing with SnapshotComparer and SnapshotStore
  - ✅ Dataset loaders with field aliasing support
  - ✅ Multi-turn conversation testing (ConversationRunner, ConversationalTestCase)
  - ✅ Cross-platform CLI with NO_COLOR support
- ✅ **570 total tests passing** (1710 across 3 target frameworks)

---

## External Framework Analysis

### 1. Microsoft.Extensions.AI.Evaluation (Official Microsoft Package)

**Location:** `dotnet/extensions` repository  
**Package:** `Microsoft.Extensions.AI.Evaluation` + `Microsoft.Extensions.AI.Evaluation.Quality`  
**Status:** Active development (marked `[Experimental("AIEVAL001")]`)

#### Key Design Patterns

```csharp
// Microsoft's IEvaluator interface
public interface IEvaluator
{
    IReadOnlyCollection<string> EvaluationMetricNames { get; }
    
    ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default);
}
```

#### Available Evaluators

| Evaluator | Type | Description |
|-----------|------|-------------|
| `FluencyEvaluator` | Quality | Linguistic correctness (1-5 score) |
| `CoherenceEvaluator` | Quality | Logical flow and consistency |
| `RelevanceEvaluator` | Quality | Answer relevance to question |
| `GroundednessEvaluator` | RAG | Answer grounded in context |
| `EquivalenceEvaluator` | RAG | Similarity to ground truth |
| `CompletenessEvaluator` | RAG | Completeness vs expected answer |
| `RetrievalEvaluator` | RAG | Context retrieval quality |
| `TaskAdherenceEvaluator` | Agentic | Task completion (1-5 score) |
| `IntentResolutionEvaluator` | Agentic | Intent detection/resolution |
| `ToolCallAccuracyEvaluator` | Agentic | Tool call correctness |
| `RelevanceTruthAndCompletenessEvaluator` | Combined | Multi-metric evaluator |
| `BLEUEvaluator` | NLP | BLEU score for translation quality |

#### Key Insights

1. **Uses 1-5 scoring** - We use 0-100; need score normalization utilities
2. **Requires ChatConfiguration** - All AI-based evaluators need IChatClient
3. **Context pattern** - Uses `EvaluationContext` classes per evaluator type
4. **Response parsing** - Uses `<S0>`, `<S1>`, `<S2>` tags for structured output
5. **Interpretation property** - `metric.Interpretation = metric.InterpretScore()`

#### What They DON'T Have (Our Opportunity)

| Feature | Microsoft.Extensions.AI.Evaluation | AgentEval |
|---------|-----------------------------------|-----------|
| Tool timing/duration tracking | ❌ | ✅ |
| Per-tool performance metrics | ❌ | ✅ |
| Tool call ordering assertions | ❌ | ✅ |
| Tool argument validation | ❌ | ✅ |
| Streaming TTFT metrics | ❌ | ✅ |
| Cost estimation | ❌ | ✅ |
| Fluent assertion API | ❌ | ✅ |
| Benchmark infrastructure | ❌ | ✅ |
| MAF integration | ❌ | ✅ |

### 2. kbeaugrand/KernelMemory.Evaluation (RAGAS for .NET)

**Location:** `github.com/kbeaugrand/KernelMemory.Evaluation`  
**Focus:** RAG evaluation with Kernel Memory

#### Architecture

```csharp
// Base class for all evaluators
public abstract class EvaluationEngine
{
    protected string GetSKPrompt(string pluginName, string functionName);
    protected async Task<T> Try<T>(int maxCount, Func<int, Task<T>> action);
}

// Example evaluator structure
public class FaithfulnessEvaluator : EvaluationEngine
{
    private readonly Kernel kernel;
    
    // Uses SK KernelFunction for prompt execution
    private KernelFunction ExtractStatements => kernel.CreateFunctionFromPrompt(...);
    private KernelFunction FaithfulnessEvaluation => kernel.CreateFunctionFromPrompt(...);
    
    public async Task<(float Score, IEnumerable<StatementEvaluation>? Evaluations)> EvaluateAsync(...)
}
```

#### Key Metrics Implemented

| Metric | Approach | Score Calculation |
|--------|----------|-------------------|
| **Faithfulness** | Extract statements → Verify each against context | Count(supported) / Count(total) |
| **Relevance** | Generate questions answer could address → Compare embeddings | Cosine similarity * committal |
| **Context Precision** | Check if context was useful for arriving at answer | Verdict (0/1) per context chunk |
| **Context Recall** | Classify ground truth statements as attributed or not | Count(attributed) / Count(total) |
| **Answer Correctness** | Extract statements → Classify as TP/FP/FN | TP / (TP + 0.5*(FP + FN)) |
| **Answer Similarity** | Generate embeddings → Cosine similarity | Direct cosine similarity |

#### Key Insights

1. **Prompt-based extraction** - Uses structured prompts in .txt files
2. **Statement-level evaluation** - Extracts individual claims for verification
3. **Retry logic** - `Try(3, async (remainingTry) => ...)` pattern
4. **Temperature near zero** - `Temperature = 1e-8f` for deterministic outputs
5. **JSON response format** - `ResponseFormat = "json_object"`
6. **Embedding integration** - Uses `ITextEmbeddingGenerationService`

#### What They DON'T Have (Our Opportunity)

| Feature | KernelMemory.Evaluation | AgentEval |
|---------|------------------------|-----------|
| Agentic tool metrics | ❌ | ✅ |
| Performance benchmarking | ❌ | ✅ |
| Streaming support | ❌ | ✅ |
| Cost tracking | ❌ | ✅ |
| Fluent assertions | ❌ | ✅ |
| MAF support | ❌ | ✅ |
| Non-SK framework support | ❌ | ✅ |

---

## Competitive Deep Dive

### Promptfoo Analysis

**Repository:** `github.com/promptfoo/promptfoo`  
**Language:** TypeScript/JavaScript  
**Focus:** LLM testing and red-teaming

#### Why Teams Choose Promptfoo

1. **CLI-First Design** - `promptfoo eval`, `promptfoo view`, `promptfoo share`
2. **YAML Configuration** - No code required for basic tests
3. **40+ Assertion Types** - `contains`, `is-json`, `similar`, `llm-rubric`, etc.
4. **Deterministic Testing** - Snapshot/baseline comparison without LLM judge

#### Promptfoo Assertion Types (Key for AgentEval to Match)

```yaml
tests:
  - vars:
      query: "What's the weather?"
    assert:
      # Exact matching
      - type: equals
        value: "sunny"
      
      # String matching
      - type: contains
        value: "weather"
      - type: icontains  # case-insensitive
        value: "WEATHER"
      
      # Pattern matching
      - type: regex
        value: "\\d+ degrees"
      
      # Structured output
      - type: is-json
      - type: contains-json
        value: '{"tool": "get_weather"}'
      
      # Semantic similarity (0-1 threshold)
      - type: similar
        value: "The weather is nice today"
        threshold: 0.8
      
      # LLM-as-judge
      - type: llm-rubric
        value: "Response accurately describes weather conditions"
      
      # Function calling validation
      - type: is-valid-openai-function-call
      - type: tool-call-f1
        value: 0.9
      
      # Custom JavaScript
      - type: javascript
        value: |
          return output.length < 500 && output.includes('sunny');
```

#### Snapshot Testing Pattern

Promptfoo doesn't do traditional snapshots, but uses **expected output comparison**:

```yaml
tests:
  - vars:
      query: "List all products"
    assert:
      - type: contains-json
        value: |
          {
            "products": [
              {"name": "Widget"},
              {"name": "Gadget"}
            ]
          }
```

**Key Learning:** Structured output validation is the .NET equivalent of snapshot testing.

### DeepEval Analysis

**Repository:** `github.com/confident-ai/deepeval`  
**Language:** Python  
**Focus:** LLM evaluation with enterprise features

#### DeepEval Multi-Turn Conversations

```python
from deepeval.test_case import ConversationalTestCase, Turn, ToolCall

# Define conversation as scripted turns
turns = [
    Turn(
        role="user",
        content="What's the weather in NYC?"
    ),
    Turn(
        role="assistant",
        content="I'll check the weather for you.",
        tools_called=[
            ToolCall(name="get_weather", arguments={"location": "NYC"})
        ],
        retrieval_context=["NYC weather data from API"]
    ),
    Turn(
        role="user",
        content="What about tomorrow?"
    ),
    Turn(
        role="assistant",
        content="Tomorrow will be sunny with highs of 75°F.",
        tools_called=[
            ToolCall(name="get_weather", arguments={"location": "NYC", "date": "tomorrow"})
        ]
    )
]

# Create test case
test_case = ConversationalTestCase(
    turns=turns,
    scenario="User asking about weather in NYC",
    expected_outcome="Agent provides accurate weather information",
    chatbot_role="A helpful weather assistant"
)
```

#### Conversation Completeness Metric

```python
from deepeval.metrics import ConversationCompletenessMetric

# Score = Satisfied User Intentions / Total User Intentions
metric = ConversationCompletenessMetric(
    threshold=0.5,
    model="gpt-4",
    include_reason=True
)

metric.measure(test_case)
print(f"Score: {metric.score}, Reason: {metric.reason}")
```

**Key Learning:** Multi-turn testing uses **scripted conversations** with tool calls tracked per turn.

### Result Exporters Deep Dive

#### Who Consumes What Format?

| Format | Consumers | Use Case |
|--------|-----------|----------|
| **JUnit XML** | Jenkins, Azure DevOps, GitHub Actions, GitLab CI | CI/CD test tab integration |
| **TAP** | Unix pipelines, older CI systems | Simple text streaming |
| **JSON** | Custom dashboards, LangSmith, Weights & Biases | Programmatic analysis |
| **Markdown** | GitHub PR comments, docs | Human-readable summaries |
| **HTML** | Standalone reports | Detailed interactive reports |

#### JUnit XML Structure

```xml
<?xml version="1.0" encoding="UTF-8"?>
<testsuites name="AgentEval Test Suite" tests="10" failures="2" time="45.23">
  <testsuite name="ToolUsageTests" tests="5" failures="1" time="12.5">
    <properties>
      <property name="model" value="gpt-4"/>
      <property name="temperature" value="0.7"/>
    </properties>
    
    <testcase name="test_tool_selection" classname="AgentEval.ToolTests" time="2.34">
      <!-- Passed test - no child elements needed -->
    </testcase>
    
    <testcase name="test_tool_arguments" classname="AgentEval.ToolTests" time="3.21">
      <failure message="Tool argument mismatch" type="AssertionError">
        Expected: {"location": "NYC"}
        Actual: {"city": "New York"}
      </failure>
    </testcase>
    
    <testcase name="test_skipped" classname="AgentEval.ToolTests">
      <skipped message="API rate limit exceeded"/>
    </testcase>
    
    <system-out>Model response: "I'll search for weather..."</system-out>
  </testsuite>
</testsuites>
```

**GitHub Actions Integration:**
```yaml
- name: Run AgentEval
  run: agenteval eval --config eval.yaml --output results.xml --format junit

- name: Publish Test Results
  uses: dorny/test-reporter@v1
  with:
    name: Agent Tests
    path: results.xml
    reporter: java-junit
```

### 3. Integration Strategy

#### Option A: Use Microsoft.Extensions.AI.Evaluation as Dependency ⭐ RECOMMENDED

```csharp
// Create adapter to use Microsoft evaluators
public class MicrosoftEvaluatorAdapter : IMetric
{
    private readonly IEvaluator _msEvaluator;
    private readonly IChatClient _chatClient;
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
    {
        var chatConfig = new ChatConfiguration(_chatClient);
        var messages = new[] { new ChatMessage(ChatRole.User, context.Input) };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, context.Output)]);
        
        var result = await _msEvaluator.EvaluateAsync(messages, response, chatConfig, ct: ct);
        
        // Normalize 1-5 to 0-100
        var msScore = result.Metrics.First().Value is NumericMetric nm ? nm.Value : 0;
        var normalizedScore = (msScore - 1) * 25; // 1->0, 5->100
        
        return new MetricResult { Score = normalizedScore, ... };
    }
}
```

**Benefits:**
- Leverage Microsoft's tested prompts
- Get updates automatically
- Reduce maintenance burden
- Enterprise credibility

**Risks:**
- Experimental API may change
- Extra dependency
- Less control over prompts

#### Option B: Port Best Patterns from Both

Take the best from each:
- **From Microsoft:** Tag-based response parsing, interpretation patterns
- **From kbeaugrand:** Statement extraction, retry logic, F1-score formulas

---

## Current State Analysis

### What's Complete ✅

| Category | Features | Quality |
|----------|----------|---------|
| **Core Interfaces** | IMetric, IRAGMetric, IAgenticMetric, ITestableAgent, IStreamableAgent, ITestHarness, IEvaluator | ⭐⭐⭐⭐⭐ |
| **Models** | ToolCallRecord, ToolUsageReport, PerformanceMetrics, TestCase, TestResult, TestSummary, TokenUsage, FailureReport, ToolCallTimeline | ⭐⭐⭐⭐⭐ |
| **Logging** | IAgentEvalLogger, ConsoleAgentEvalLogger, NullAgentEvalLogger, MicrosoftExtensionsLoggingAdapter | ⭐⭐⭐⭐⭐ |
| **Assertions** | Tool (12+ methods), Performance (10+ methods), Response (10+ methods) | ⭐⭐⭐⭐⭐ |
| **RAG Metrics** | Faithfulness, Relevance, ContextPrecision, ContextRecall, AnswerCorrectness | ⭐⭐⭐⭐ |
| **Agentic Metrics** | ToolSelection, ToolArguments, ToolSuccess, TaskCompletion, ToolEfficiency | ⭐⭐⭐⭐ |
| **Embedding Metrics** | AnswerSimilarity, ResponseContextSimilarity, QueryContextSimilarity | ⭐⭐⭐⭐ |
| **Benchmarks** | Latency, Throughput, Cost, ToolAccuracy, TaskCompletion, MultiStep | ⭐⭐⭐⭐ |
| **MAF Integration** | MAFAgentAdapter, MAFTestHarness with streaming, trace-first failure reporting | ⭐⭐⭐⭐⭐ |
| **Testing Infrastructure** | FakeChatClient for mocking IChatClient, no external dependencies | ⭐⭐⭐⭐⭐ |
| **Microsoft Adapter** | MicrosoftEvaluatorAdapter for wrapping official evaluators | ⭐⭐⭐⭐ |
| **CLI Tool** | AgentEval.Cli with eval, init, list commands | ⭐⭐⭐⭐⭐ |
| **Result Exporters** | JSON, JUnit XML, Markdown, TRX formats | ⭐⭐⭐⭐⭐ |
| **Dataset Loaders** | JSON, JSONL, CSV, YAML with field aliasing | ⭐⭐⭐⭐⭐ |
| **Snapshot Testing** | SnapshotComparer, SnapshotStore with JSON diff | ⭐⭐⭐⭐⭐ |
| **Multi-turn Testing** | ConversationRunner, ConversationalTestCase | ⭐⭐⭐⭐⭐ |
| **Tests** | **570 unit tests** (1710 across 3 frameworks), all passing | ⭐⭐⭐⭐⭐ |

### Architecture Strengths

1. **Interface Segregation** - Clean separation (IMetric → IRAGMetric → IAgenticMetric)
2. **Adapter Pattern** - MAFAgentAdapter enables easy framework support
3. **Fluent API** - Intuitive `Should()` assertions with chaining
4. **SOLID Principles** - Single responsibility, open for extension
5. **Multi-targeting** - net8.0, net9.0, net10.0 support
6. **Score Normalization** - ScoreNormalizer converts between 1-5 and 0-100 scales

### Lines of Code Summary

| File | Lines | Complexity |
|------|-------|------------|
| MAFTestHarness.cs | ~350 | High |
| AgenticBenchmark.cs | ~300 | High |
| PerformanceBenchmark.cs | ~250 | Medium |
| RAGMetrics.cs | ~350 | Medium |
| AgenticMetrics.cs | ~300 | Medium |
| EmbeddingMetrics.cs | ~200 | Medium |
| ToolUsageAssertions.cs | ~200 | Low |
| PerformanceAssertions.cs | ~100 | Low |
| **Total** | ~3,000+ | - |

---

## Architecture Overview

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                           AgentEval                                  │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                        Core Layer                             │   │
│  ├──────────────────────────────────────────────────────────────┤   │
│  │  IMetric ────────┬─────────────┬──────────────────────────   │   │
│  │       │          │             │                              │   │
│  │  IRAGMetric  IAgenticMetric  IEmbeddingMetric                │   │
│  │       │          │             │                              │   │
│  │  ┌────┴────┐ ┌───┴───┐   ┌────┴────┐                         │   │
│  │  │Faithfulness│Tool   │   │Answer   │                         │   │
│  │  │Relevance   │Selection│ │Similarity│                        │   │
│  │  │Context*    │Arguments│ │Context  │                         │   │
│  │  │Correctness │Success  │ │Similarity│                        │   │
│  │  └────────────┴────────┴──┴─────────┘                         │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                     Assertions Layer                          │   │
│  ├──────────────────────────────────────────────────────────────┤   │
│  │  ToolUsageAssertions    Should().HaveCalledTool("X")         │   │
│  │  PerformanceAssertions  Should().HaveTotalDurationUnder(5s)  │   │
│  │  ResponseAssertions     Should().Contain("expected")          │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    Benchmarks Layer                           │   │
│  ├──────────────────────────────────────────────────────────────┤   │
│  │  PerformanceBenchmark   Latency, Throughput, Cost            │   │
│  │  AgenticBenchmark       ToolAccuracy, TaskCompletion         │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    Integration Layer                          │   │
│  ├──────────────────────────────────────────────────────────────┤   │
│  │  MAFTestHarness         Microsoft Agent Framework             │   │
│  │  MicrosoftEvaluatorAdapter  MS.Extensions.AI.Evaluation      │   │
│  │  ChatClientAgentAdapter Generic IChatClient wrapper           │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                 Production Infrastructure 🆕                  │   │
│  ├──────────────────────────────────────────────────────────────┤   │
│  │  IResultExporter        JUnit, Markdown, JSON (PLANNED)      │   │
│  │  IDatasetLoader         JSONL, BFCL, HuggingFace (PLANNED)   │   │
│  │  SnapshotTestHarness    Baseline comparison (PLANNED)        │   │
│  │  AgentEval.CLI          dotnet tool (PLANNED)                │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### Metric Hierarchy

```
IMetric (base interface)
├── Name: string
├── Description: string
└── EvaluateAsync(context) -> MetricResult

IRAGMetric : IMetric
├── RequiresContext: bool
└── RequiresGroundTruth: bool
    ├── FaithfulnessMetric
    ├── RelevanceMetric
    ├── ContextPrecisionMetric
    ├── ContextRecallMetric
    └── AnswerCorrectnessMetric

IAgenticMetric : IMetric
├── RequiresToolUsage: bool
    ├── ToolSelectionMetric
    ├── ToolArgumentsMetric
    ├── ToolSuccessMetric
    ├── ToolEfficiencyMetric
    └── TaskCompletionMetric

IEmbeddingMetric : IMetric
├── RequiresEmbeddings: bool
    ├── AnswerSimilarityMetric
    ├── ResponseContextSimilarityMetric
    └── QueryContextSimilarityMetric
```

### Embedding-Based Metrics

AgentEval includes embedding-based metrics for semantic similarity evaluation:

```csharp
// Answer Similarity - Compare response to ground truth using embeddings
public class AnswerSimilarityMetric : IRAGMetric
{
    private readonly IAgentEvalEmbeddings _embeddings;
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
    {
        // Uses cosine similarity between response and ground truth embeddings
        var similarity = await EmbeddingSimilarity.CosineSimilarity(
            _embeddings, context.Output, context.GroundTruth!);
        
        return MetricResult.Pass(Name, similarity * 100, $"Similarity: {similarity:P1}");
    }
}

// EmbeddingSimilarity utilities
public static class EmbeddingSimilarity
{
    public static async Task<double> CosineSimilarity(IAgentEvalEmbeddings embeddings, string text1, string text2);
    public static async Task<double> EuclideanDistance(IAgentEvalEmbeddings embeddings, string text1, string text2);
    public static double DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b);
    public static IEnumerable<(int Index, double Score)> TopK(IEnumerable<double> scores, int k);
}
```

### Extensibility Patterns

#### Creating Custom Metrics

```csharp
// 1. Implement IMetric for basic metrics
public class CustomMetric : IMetric
{
    public string Name => "CustomMetric";
    public string Description => "My custom evaluation metric";
    
    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
    {
        // Your evaluation logic
        var score = CalculateScore(context.Output);
        return Task.FromResult(MetricResult.Pass(Name, score));
    }
}

// 2. Implement IRAGMetric for RAG-specific metrics
public class CustomRAGMetric : IRAGMetric
{
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => true;
    // ... implement EvaluateAsync
}

// 3. Implement IAgenticMetric for tool-tracking metrics
public class CustomAgenticMetric : IAgenticMetric
{
    public bool RequiresToolUsage => true;
    // ... implement EvaluateAsync
}
```

#### Using the Metric Registry

```csharp
var registry = new MetricRegistry();

// Register metrics
registry.Register(new FaithfulnessMetric(chatClient));
registry.Register(new ToolSelectionMetric(expectedTools));
registry.Register("custom", new CustomMetric());

// Get metrics
var faithfulness = registry.GetRequired("Faithfulness");
var allMetrics = registry.GetAll();

// Run all metrics
foreach (var metric in registry.GetAll())
{
    var result = await metric.EvaluateAsync(context);
    Console.WriteLine($"{result.MetricName}: {result.Score}");
}
```

#### Wrapping Microsoft Evaluators

```csharp
// Use official Microsoft evaluators through AgentEval's interface
var msEvaluator = new CoherenceEvaluator();
var adapter = new MicrosoftEvaluatorAdapter(msEvaluator, chatClient);

// Now use it like any AgentEval metric
var result = await adapter.EvaluateAsync(context);
// Score is automatically normalized from 1-5 to 0-100
```

---

## Code Quality Review

### Identified Issues & Fixes

#### 1. ✅ FIXED - Duplicate Code - ExtractToolUsage (Priority: Medium)

**Problem:** Same `ExtractToolUsage` method exists in both `MAFTestHarness.cs` and `AgenticBenchmark.cs`.

**Solution:** Created `Core/ToolUsageExtractor.cs` with shared implementation.
```csharp
// Implemented in Core/ToolUsageExtractor.cs
public static class ToolUsageExtractor
{
    public static ToolUsageReport Extract(IReadOnlyList<object>? rawMessages) { ... }
    public static ToolUsageReport Extract(AgentResponse response) { ... }
}
```

#### 2. ✅ FIXED - Thread Safety - ModelPricing (Priority: High)

**Problem:** `SetPricing` modifies a dictionary that could be read concurrently.

**Solution:** Changed to `ConcurrentDictionary` in `Models/PerformanceMetrics.cs`.
```csharp
public static class ModelPricing
{
    private static readonly ConcurrentDictionary<string, (decimal InputPer1K, decimal OutputPer1K)> Pricing = new();
    
    public static void SetPricing(string modelName, decimal inputPer1K, decimal outputPer1K)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        Pricing.AddOrUpdate(modelName.ToLowerInvariant(), (inputPer1K, outputPer1K), (_, _) => (inputPer1K, outputPer1K));
    }
}
```

#### 3. ✅ FIXED - Magic Numbers (Priority: Low)

**Problem:** Hardcoded values like `70` (passing score), `10` (penalty per extra tool).

**Solution:** Created `Core/EvaluationDefaults.cs` with all constants.
```csharp
// Implemented in Core/EvaluationDefaults.cs
public static class EvaluationDefaults
{
    public const int DefaultPassingScore = 70;
    public const double PassingScoreThreshold = 70.0;
    public const double ExtraToolPenaltyPercent = 10.0;
    public const double OrderPenaltyPercent = 20.0;
    public const int DefaultMaxExpectedToolCalls = 10;
    public static readonly TimeSpan DefaultMaxToolDuration = TimeSpan.FromSeconds(30);
    public const double DuplicateToolPenaltyPercent = 5.0;
}
```

#### 4. ✅ FIXED - JSON Parsing Foundation (Priority: Low)

**Problem:** Using `System.Text.Json.JsonDocument` in hot paths with repetitive extraction code.

**Solution:** Created `Core/LlmJsonParser.cs` helper utilities.
```csharp
// Implemented in Core/LlmJsonParser.cs
public static class LlmJsonParser
{
    public static string? ExtractJson(string text) { ... }
    public static T? ParseMetricResponse<T>(string response) { ... }
    public static List<string> ExtractStringArray(JsonElement element, string propertyName) { ... }
    public static bool GetBoolean(JsonElement element, string propertyName, bool defaultValue) { ... }
}
```
**Note:** Source generators for full optimization are a future enhancement.

#### 5. ✅ FIXED - Missing Null Checks (Priority: Medium)

**Problem:** Some methods don't validate required parameters.

**Solution:** Added `ArgumentNullException.ThrowIfNull()` to:
- `ToolSelectionMetric` constructor
- `ToolArgumentsMetric` constructor  
- `ModelPricing.SetPricing` method
- `ToolUsageExtractor.Extract` method
```csharp
public ToolSelectionMetric(IEnumerable<string> expectedTools, bool strictOrder = false)
{
    ArgumentNullException.ThrowIfNull(expectedTools);
    _expectedTools = expectedTools.ToList();
    _strictOrder = strictOrder;
}
```

---

## Strategic Vision

### What Makes "THE" Framework?

To become the definitive .NET AI agent evaluation framework, we need:

| Attribute | Current State | Target State |
|-----------|---------------|--------------|
| **Completeness** | Core complete | All features of DeepEval + RAGAS + Promptfoo |
| **Adoption** | Internal only | 10K+ monthly NuGet downloads |
| **Framework Support** | MAF complete | MAF + future frameworks via adapters |
| **Enterprise Ready** | Alpha | SOC2-compatible, enterprise features |
| **Community** | None | Active contributors, Discord, examples |
| **Documentation** | Basic | Comprehensive docs site |
| **Tooling** | CLI only | VS extension, GitHub Actions, dashboard |

### Competitive Advantages to Maintain

1. **First-mover in .NET** - No real competition exists
2. **Microsoft alignment** - Built on Microsoft.Extensions.AI
3. **Fluent API** - Best-in-class developer experience
4. **Performance focus** - Streaming, benchmarking, cost tracking

---

## Phase 1: Polish & Publication

**Timeline:** 2-3 weeks  
**Goal:** Ship v1.0.0-alpha to NuGet

### 1.1 Code Cleanup (Week 1)

- [ ] Extract duplicate `ExtractToolUsage` to shared utility
- [ ] Add `ConcurrentDictionary` for ModelPricing
- [ ] Extract magic numbers to constants class
- [ ] Add XML documentation to ALL public APIs
- [ ] Add nullable reference type annotations throughout
- [ ] Create `AgentEval.ruleset` for consistent code style

### 1.2 Testing Enhancement (Week 1)

- [ ] Add integration tests with mock LLM responses
- [ ] Add edge case tests (empty responses, network errors, timeouts)
- [ ] Add benchmark comparison tests (regression detection)
- [ ] Achieve 90%+ code coverage
- [ ] Add test categories: Unit, Integration, Performance

### 1.3 Package Preparation (Week 2)

- [ ] Create proper `README.md` for NuGet
- [ ] Add `CHANGELOG.md`
- [ ] Configure SourceLink for debugging
- [ ] Set up deterministic builds
- [ ] Add license header to all files
- [ ] Create logo/icon for NuGet
- [ ] Add package validation

```xml
<PropertyGroup>
  <EnablePackageValidation>true</EnablePackageValidation>
  <PackageValidationBaselineVersion>1.0.0</PackageValidationBaselineVersion>
</PropertyGroup>
```

### 1.4 Repository Setup (Week 2)

- [ ] Create standalone GitHub repository: `AgentEval/AgentEval`
- [ ] Set up GitHub Actions for CI/CD
- [ ] Configure Dependabot
- [ ] Add issue/PR templates
- [ ] Create CONTRIBUTING.md
- [ ] Set up discussions

### 1.5 Initial Publication (Week 3)

- [ ] Publish to NuGet as `1.0.0-alpha`
- [ ] Create GitHub release
- [ ] Write announcement blog post
- [ ] Share on .NET community channels

---

## Phase 2: Enterprise Features

**Timeline:** 6-8 weeks  
**Goal:** Make AgentEval enterprise-ready

### 2.1 Tracing & Observability

```csharp
public class TracingTestHarness : ITestHarness
{
    private readonly ActivitySource _activitySource = new("AgentEval");
    
    public async Task<TestResult> RunTestAsync(...)
    {
        using var activity = _activitySource.StartActivity("AgentEval.Test");
        activity?.SetTag("test.name", testCase.Name);
        activity?.SetTag("agent.name", agent.Name);
        
        try
        {
            var result = await _inner.RunTestAsync(...);
            activity?.SetTag("test.passed", result.Passed);
            activity?.SetTag("test.score", result.Score);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
```

**Integrations:**
- [ ] OpenTelemetry export
- [ ] Application Insights integration
- [ ] Jaeger/Zipkin support
- [ ] Custom span attributes for all metrics

### 3.2 Dataset Management

```csharp
public interface ITestDataset
{
    string Name { get; }
    IAsyncEnumerable<TestCase> GetTestCasesAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

public class JsonFileDataset : ITestDataset
{
    public static JsonFileDataset Load(string path) { ... }
}

public class CsvDataset : ITestDataset
{
    public static CsvDataset Load(string path, CsvDatasetOptions options) { ... }
}

public class HuggingFaceDataset : ITestDataset
{
    public HuggingFaceDataset(string datasetId, string split = "test") { ... }
}
```

### 3.3 Results Persistence

```csharp
public interface IResultStore
{
    Task SaveAsync(TestSummary summary, CancellationToken ct = default);
    Task<TestSummary?> LoadAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<TestSummary>> ListAsync(ResultFilter filter, CancellationToken ct = default);
}

public class SqlResultStore : IResultStore { ... }
public class CosmosResultStore : IResultStore { ... }
public class FileResultStore : IResultStore { ... }
```

### 3.4 Regression Detection

```csharp
public class RegressionDetector
{
    public RegressionReport Compare(TestSummary baseline, TestSummary current)
    {
        var regressions = new List<Regression>();
        
        foreach (var result in current.Results)
        {
            var baselineResult = baseline.Results.FirstOrDefault(r => r.TestName == result.TestName);
            if (baselineResult != null)
            {
                if (result.Score < baselineResult.Score - threshold)
                {
                    regressions.Add(new Regression
                    {
                        TestName = result.TestName,
                        BaselineScore = baselineResult.Score,
                        CurrentScore = result.Score,
                        Delta = result.Score - baselineResult.Score
                    });
                }
            }
        }
        
        return new RegressionReport { Regressions = regressions };
    }
}
```

### 3.5 CI/CD Integration

**GitHub Actions:**
```yaml
- name: Run Agent Evaluation
  uses: AgentEval/evaluate-action@v1
  with:
    test-project: ./tests/AgentTests.csproj
    baseline-results: ./baselines/main.json
    fail-on-regression: true
    regression-threshold: 5
    
- name: Upload Results
  uses: AgentEval/upload-results@v1
  with:
    results: ./test-results.json
    dashboard-url: ${{ secrets.AGENTEVAL_DASHBOARD }}
```

**Azure DevOps:**
```yaml
- task: AgentEval@1
  inputs:
    projectPath: '$(System.DefaultWorkingDirectory)/tests'
    publishResults: true
```

---

## Phase 3: Ecosystem & Community

**Timeline:** Ongoing  
**Goal:** Build thriving community

### 3.1 Documentation Site

- [ ] Create docs site with Docusaurus/VitePress
- [ ] Getting Started guide
- [ ] API Reference (auto-generated)
- [ ] Tutorials:
  - Testing MAF agents
  - RAG evaluation guide
  - Benchmarking guide
  - CI/CD integration
- [ ] Examples repository

### 3.2 Visual Studio Extension

```
AgentEval.VisualStudio/
├── TestExplorer/           # Custom test explorer for agent tests
├── ResultsViewer/          # Rich results visualization
├── Snippets/               # Code snippets for assertions
└── Analyzers/              # Roslyn analyzers for best practices
```

### 3.3 CLI Tool

```bash
# Install
dotnet tool install -g AgentEval.CLI

# Initialize project
agenteval init --framework maf

# Run tests
agenteval test --project ./tests

# Benchmark
agenteval benchmark --iterations 20 --output results.json

# Compare
agenteval compare baseline.json current.json --threshold 5

# Generate report
agenteval report results.json --format html --output report.html
```

### 3.4 Dashboard (Optional SaaS)

```
AgentEval.Dashboard/
├── Frontend/               # React dashboard
│   ├── TestResults/        # Results visualization
│   ├── Benchmarks/         # Performance charts
│   ├── Regressions/        # Regression tracking
│   └── Comparisons/        # A/B testing
└── Backend/                # ASP.NET Core API
    ├── Results/            # Store results
    ├── Webhooks/           # CI/CD integration
    └── Analytics/          # Usage analytics
```

### 3.5 Community Building

- [ ] Create Discord server
- [ ] Write blog posts:
  - "Introducing AgentEval"
  - "Testing AI Agents in .NET"
  - "RAG Evaluation Best Practices"
- [ ] Conference talks (NDC, .NET Conf)
- [ ] Twitter/X engagement
- [ ] Respond to issues within 24h

---

## Phase 4: Advanced Capabilities

**Timeline:** 3-6 months  
**Goal:** Feature parity with Python frameworks + innovation

### 4.1 Red-Teaming & Safety

```csharp
public interface IRedTeamingPlugin
{
    string Name { get; }
    IAsyncEnumerable<string> GenerateAttackPromptsAsync(RedTeamingContext context);
}

public class JailbreakPlugin : IRedTeamingPlugin { ... }
public class PromptInjectionPlugin : IRedTeamingPlugin { ... }
public class ContentFilterBypassPlugin : IRedTeamingPlugin { ... }
public class PIIExtractionPlugin : IRedTeamingPlugin { ... }

public class RedTeamingBenchmark
{
    public async Task<RedTeamingResult> RunAsync(
        ITestableAgent agent,
        IEnumerable<IRedTeamingPlugin> plugins)
    {
        // Run attacks, evaluate responses
    }
}
```

### 4.2 Synthetic Data Generation

```csharp
public class SyntheticDataGenerator
{
    private readonly IChatClient _generator;
    
    public async IAsyncEnumerable<TestCase> GenerateAsync(
        SyntheticDataSpec spec,
        int count,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Use LLM to generate test cases based on spec
        var prompt = BuildGenerationPrompt(spec);
        
        for (int i = 0; i < count; i++)
        {
            var response = await _generator.GetResponseAsync(prompt, cancellationToken: ct);
            yield return ParseTestCase(response.Text);
        }
    }
}

// Usage
var generator = new SyntheticDataGenerator(chatClient);
var testCases = await generator.GenerateAsync(new SyntheticDataSpec
{
    Domain = "Customer support",
    Difficulty = Difficulty.Hard,
    RequireToolUsage = true,
    ExpectedTools = new[] { "SearchKnowledgeBase", "CreateTicket" }
}, count: 100).ToListAsync();
```

### 4.3 Multi-Agent Evaluation

```csharp
public interface IMultiAgentScenario
{
    IReadOnlyList<ITestableAgent> Agents { get; }
    Task<MultiAgentResult> RunAsync(CancellationToken ct = default);
}

public class ConversationScenario : IMultiAgentScenario
{
    public async Task<MultiAgentResult> RunAsync(CancellationToken ct)
    {
        var conversation = new List<ConversationTurn>();
        var currentAgent = 0;
        
        while (!IsConversationComplete())
        {
            var agent = Agents[currentAgent];
            var response = await agent.InvokeAsync(GetNextPrompt(), ct);
            conversation.Add(new ConversationTurn(agent.Name, response));
            currentAgent = (currentAgent + 1) % Agents.Count;
        }
        
        return EvaluateConversation(conversation);
    }
}
```

### 4.4 Human-in-the-Loop Evaluation

```csharp
public interface IHumanEvaluator
{
    Task<HumanEvaluation> RequestEvaluationAsync(
        EvaluationContext context,
        IReadOnlyList<string> criteria,
        CancellationToken ct = default);
}

public class SlackHumanEvaluator : IHumanEvaluator { ... }
public class TeamsHumanEvaluator : IHumanEvaluator { ... }
public class WebUIHumanEvaluator : IHumanEvaluator { ... }
```

### 4.5 Model Comparison Framework

```csharp
public class ModelComparer
{
    public async Task<ComparisonResult> CompareAsync(
        IEnumerable<ITestableAgent> agents,
        IEnumerable<TestCase> testCases,
        ComparisonOptions options)
    {
        var results = new Dictionary<string, TestSummary>();
        
        foreach (var agent in agents)
        {
            var harness = new MAFTestHarness();
            var summary = await harness.RunTestSuiteAsync(
                agent.Name, agent, testCases, options.TestOptions);
            results[agent.Name] = summary;
        }
        
        return AnalyzeComparison(results);
    }
}
```

---

## Phase 5: Production Infrastructure ⭐ CRITICAL

**Timeline:** 2-4 weeks  
**Goal:** Enable CI/CD integration and enterprise adoption  
**Priority:** HIGHEST - Blocks enterprise adoption without these features

> **Key Insight (Jan 5, 2026):** Competitive analysis reveals that Promptfoo's success comes from its CLI + result exporters, not its evaluation metrics. These production infrastructure features are what enable teams to integrate AI testing into existing CI/CD workflows.

### 5.1 CLI Tool (`AgentEval.Cli`)

**Why Critical:** Without a CLI, teams must write custom harness code to integrate AgentEval into pipelines. Promptfoo's entire value proposition is `promptfoo eval` + YAML config.

#### Command Structure

```bash
# Install globally
dotnet tool install -g AgentEval.Cli

# Initialize configuration
agenteval init --framework maf --output agenteval.yaml

# Run evaluation
agenteval eval --config agenteval.yaml --output results.json

# Run with specific format
agenteval eval --config agenteval.yaml --format junit --output results.xml

# Update snapshots (when behavior intentionally changes)
agenteval snapshot update --config agenteval.yaml

# View results in browser
agenteval view --results results.json

# Compare baselines
agenteval compare --baseline baseline.json --current results.json --threshold 5

# Run specific benchmark
agenteval benchmark --suite bfcl-simple --output benchmark-results.json
```

#### Configuration File Format (agenteval.yaml)

```yaml
# agenteval.yaml
version: "1.0"

# Agent configuration
agent:
  type: maf  # or 'chat-client', 'custom'
  assembly: "./bin/Debug/net8.0/MyAgent.dll"
  class: "MyApp.WeatherAgent"

# Test cases
tests:
  - name: "Weather Query"
    input: "What's the weather in NYC?"
    expected_tools:
      - name: get_weather
        required_params: [location]
    assert:
      - type: contains
        value: "weather"
      - type: tool-called
        tool: get_weather
      - type: latency-under
        value: 5000ms

  - name: "Multi-step Planning"
    input: "Plan a trip to Paris"
    expected_tools:
      - name: search_flights
      - name: search_hotels
      - name: create_itinerary
    assert:
      - type: tools-in-order
      - type: task-complete
        threshold: 0.8

# Metrics to run
metrics:
  - faithfulness
  - relevance
  - tool-selection
  - tool-success

# Output configuration  
output:
  format: json  # json, junit, markdown, html
  path: ./results/

# Snapshot configuration
snapshots:
  enabled: true
  directory: ./.snapshots/
  ignore_fields:
    - "timestamp"
    - "request_id"
    - "token_count"
```

#### Implementation (csproj)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    
    <!-- Required for dotnet tool -->
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>agenteval</ToolCommandName>
    <PackageOutputPath>./nupkg</PackageOutputPath>
    
    <!-- Package metadata -->
    <PackageId>AgentEval.Cli</PackageId>
    <Version>1.0.0</Version>
    <Authors>AgentEval Contributors</Authors>
    <Description>CLI tool for AI agent evaluation and testing</Description>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
    <PackageReference Include="YamlDotNet" Version="15.1.0" />
    <ProjectReference Include="../AgentEval/AgentEval.csproj" />
  </ItemGroup>
</Project>
```

#### Exit Codes (CI/CD Integration)

| Code | Meaning |
|------|---------|
| 0 | All tests passed |
| 1 | One or more tests failed |
| 2 | Configuration error |
| 3 | Runtime error |
| 4 | Regression detected (when using --fail-on-regression) |

### 5.2 Result Exporters

**Why Critical:** CI/CD systems (GitHub Actions, Azure DevOps, Jenkins) consume JUnit XML to display test results in their UI. Without this, test failures are buried in console output.

#### Interface Design

```csharp
public interface IResultExporter
{
    string Format { get; }  // "junit", "markdown", "json", "html"
    Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default);
}

public interface IResultExporterFactory
{
    IResultExporter Create(string format);
}
```

#### JUnit XML Exporter

```csharp
public class JUnitXmlExporter : IResultExporter
{
    public string Format => "junit";
    
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct)
    {
        var settings = new XmlWriterSettings { Async = true, Indent = true };
        await using var writer = XmlWriter.Create(output, settings);
        
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "testsuites", null);
        await writer.WriteAttributeStringAsync(null, "name", null, report.Name);
        await writer.WriteAttributeStringAsync(null, "tests", null, report.TotalTests.ToString());
        await writer.WriteAttributeStringAsync(null, "failures", null, report.FailedTests.ToString());
        await writer.WriteAttributeStringAsync(null, "time", null, report.TotalDuration.TotalSeconds.ToString("F2"));
        
        foreach (var suite in report.Suites)
        {
            await WriteTestSuiteAsync(writer, suite);
        }
        
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
    }
}
```

#### Markdown Exporter (GitHub PR Comments)

```csharp
public class MarkdownExporter : IResultExporter
{
    public string Format => "markdown";
    
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct)
    {
        using var writer = new StreamWriter(output, leaveOpen: true);
        
        await writer.WriteLineAsync($"# AgentEval Results: {report.Name}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"**Status:** {(report.AllPassed ? "✅ PASSED" : "❌ FAILED")}");
        await writer.WriteLineAsync($"**Tests:** {report.PassedTests}/{report.TotalTests} passed");
        await writer.WriteLineAsync($"**Duration:** {report.TotalDuration.TotalSeconds:F2}s");
        await writer.WriteLineAsync();
        
        // Summary table
        await writer.WriteLineAsync("| Test | Score | Status | Duration |");
        await writer.WriteLineAsync("|------|-------|--------|----------|");
        
        foreach (var result in report.Results)
        {
            var status = result.Passed ? "✅" : "❌";
            await writer.WriteLineAsync($"| {result.Name} | {result.Score:F1} | {status} | {result.Duration.TotalMilliseconds:F0}ms |");
        }
        
        // Failures detail
        if (report.FailedTests > 0)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("## Failures");
            foreach (var failure in report.Results.Where(r => !r.Passed))
            {
                await writer.WriteLineAsync($"### ❌ {failure.Name}");
                await writer.WriteLineAsync($"- **Reason:** {failure.FailureReason}");
                await writer.WriteLineAsync($"- **Score:** {failure.Score:F1}/100");
            }
        }
    }
}
```

### 5.3 Snapshot/Baseline Testing

**Why Critical:** LLM outputs are non-deterministic. Snapshot testing allows comparing structured outputs while ignoring dynamic fields (timestamps, IDs, token counts).

**Storage Format:** JSON (human-readable, easy to diff in PRs)

#### Snapshot Test Harness

```csharp
public class SnapshotTestHarness
{
    private readonly string _snapshotDirectory;
    private readonly SnapshotOptions _options;
    
    public SnapshotTestHarness(string snapshotDirectory, SnapshotOptions? options = null)
    {
        _snapshotDirectory = snapshotDirectory;
        _options = options ?? new SnapshotOptions();
    }
    
    /// <summary>
    /// Compare actual result against stored snapshot.
    /// </summary>
    public async Task<SnapshotResult> AssertMatchesSnapshotAsync(
        string snapshotName,
        object actual,
        CancellationToken ct = default)
    {
        var snapshotPath = Path.Combine(_snapshotDirectory, $"{snapshotName}.snapshot.json");
        
        // Mask dynamic fields before comparison
        var maskedActual = MaskDynamicFields(actual, _options.IgnoreFields);
        var actualJson = JsonSerializer.Serialize(maskedActual, _jsonOptions);
        
        if (!File.Exists(snapshotPath))
        {
            if (_options.UpdateSnapshots)
            {
                await File.WriteAllTextAsync(snapshotPath, actualJson, ct);
                return SnapshotResult.Created(snapshotName);
            }
            return SnapshotResult.Missing(snapshotName);
        }
        
        var expectedJson = await File.ReadAllTextAsync(snapshotPath, ct);
        
        if (actualJson == expectedJson)
        {
            return SnapshotResult.Match(snapshotName);
        }
        
        if (_options.UpdateSnapshots)
        {
            await File.WriteAllTextAsync(snapshotPath, actualJson, ct);
            return SnapshotResult.Updated(snapshotName, expectedJson, actualJson);
        }
        
        return SnapshotResult.Mismatch(snapshotName, expectedJson, actualJson);
    }
    
    /// <summary>
    /// Mask fields that should be ignored during comparison.
    /// </summary>
    private object MaskDynamicFields(object obj, IEnumerable<string> fieldsToIgnore)
    {
        var json = JsonSerializer.SerializeToElement(obj);
        return MaskJsonElement(json, fieldsToIgnore.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}

public class SnapshotOptions
{
    /// <summary>Fields to ignore during comparison (e.g., "timestamp", "requestId").</summary>
    public IReadOnlyList<string> IgnoreFields { get; init; } = new[] { "timestamp", "requestId", "tokenCount" };
    
    /// <summary>Whether to update snapshots when they don't match.</summary>
    public bool UpdateSnapshots { get; init; } = false;
    
    /// <summary>Property matchers for partial matching.</summary>
    public IDictionary<string, IPropertyMatcher> PropertyMatchers { get; init; } = new Dictionary<string, IPropertyMatcher>();
}

public interface IPropertyMatcher
{
    bool Matches(JsonElement actual);
}

public class AnyStringMatcher : IPropertyMatcher
{
    public bool Matches(JsonElement actual) => actual.ValueKind == JsonValueKind.String;
}

public class AnyNumberMatcher : IPropertyMatcher
{
    public bool Matches(JsonElement actual) => actual.ValueKind == JsonValueKind.Number;
}

public class RegexMatcher : IPropertyMatcher
{
    private readonly Regex _pattern;
    public RegexMatcher(string pattern) => _pattern = new Regex(pattern);
    public bool Matches(JsonElement actual) => 
        actual.ValueKind == JsonValueKind.String && _pattern.IsMatch(actual.GetString()!);
}
```

#### Snapshot File Example

```json
// .snapshots/weather-query.snapshot.json
{
  "testName": "Weather Query",
  "response": {
    "text": "The weather in NYC is currently sunny with a temperature of 72°F.",
    "toolsCalled": [
      {
        "name": "get_weather",
        "arguments": {
          "location": "NYC"
        }
      }
    ]
  },
  "metrics": {
    "faithfulness": 95.0,
    "toolSelection": 100.0
  },
  "_masked": {
    "timestamp": "[MASKED]",
    "requestId": "[MASKED]",
    "tokenCount": "[MASKED]"
  }
}
```

### 5.4 Dataset Loaders

**Why Critical:** Enables loading external test datasets (BFCL, GAIA, ToolBench) and team-specific test suites from files.

#### Interface Design

```csharp
public interface IDatasetLoader
{
    Task<IReadOnlyList<TestCase>> LoadAsync(string source, CancellationToken ct = default);
    IAsyncEnumerable<TestCase> LoadStreamingAsync(string source, CancellationToken ct = default);
}

public interface IBenchmarkDatasetLoader : IDatasetLoader
{
    string BenchmarkName { get; }
    Task<IReadOnlyList<BenchmarkTestCase>> LoadBenchmarkAsync(string source, CancellationToken ct = default);
}
```

#### JSONL Loader (Industry Standard)

```csharp
public class JsonLinesLoader : IDatasetLoader
{
    public async Task<IReadOnlyList<TestCase>> LoadAsync(string filePath, CancellationToken ct)
    {
        var results = new List<TestCase>();
        await foreach (var testCase in LoadStreamingAsync(filePath, ct))
        {
            results.Add(testCase);
        }
        return results;
    }
    
    public async IAsyncEnumerable<TestCase> LoadStreamingAsync(
        string filePath, 
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(filePath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            var parsed = JsonSerializer.Deserialize<JsonLinesTestCase>(line);
            if (parsed != null)
            {
                yield return parsed.ToTestCase();
            }
        }
    }
}

// JSONL format
// {"input": "What's the weather?", "expected_output": "...", "expected_tools": ["get_weather"]}
internal record JsonLinesTestCase(
    string Input,
    string? ExpectedOutput,
    IReadOnlyList<string>? ExpectedTools,
    string? Context,
    string? GroundTruth
)
{
    public TestCase ToTestCase() => new TestCase
    {
        Name = Input[..Math.Min(50, Input.Length)] + "...",
        Input = Input,
        ExpectedTools = ExpectedTools,
        EvaluationCriteria = ExpectedOutput != null ? new[] { ExpectedOutput } : null
    };
}
```

#### BFCL Dataset Adapter

```csharp
public class BfclDatasetLoader : IBenchmarkDatasetLoader
{
    public string BenchmarkName => "BFCL";
    
    public async Task<IReadOnlyList<BenchmarkTestCase>> LoadBenchmarkAsync(string source, CancellationToken ct)
    {
        // Load BFCL JSON format
        var content = await File.ReadAllTextAsync(source, ct);
        var bfclCases = JsonSerializer.Deserialize<List<BfclTestCase>>(content);
        
        return bfclCases?.Select(bc => new BenchmarkTestCase
        {
            Id = bc.Id,
            Input = bc.Question,
            ExpectedTools = new[] { new ExpectedTool 
            { 
                Name = bc.GroundTruth.Name, 
                RequiredParameters = bc.GroundTruth.Arguments.Keys.ToList() 
            }},
            GroundTruth = bc.GroundTruth,
            Category = bc.Category
        }).ToList() ?? new List<BenchmarkTestCase>();
    }
}

// BFCL format
internal record BfclTestCase(
    string Id,
    string Question,
    BfclFunction Function,
    BfclGroundTruth GroundTruth,
    string Category
);
```

### 5.5 Multi-Turn Conversation Support

**Why Critical:** Real-world agents have conversations, not single queries. DeepEval's `ConversationalTestCase` is heavily used.

**Approach:** Scripted conversations first (simpler, deterministic). LLM-as-user for Phase 4.

#### Data Models

```csharp
/// <summary>
/// A single turn in a conversation.
/// </summary>
public record Turn
{
    public required string Role { get; init; }  // "user" or "assistant"
    public required string Content { get; init; }
    public IReadOnlyList<ToolCallRecord>? ToolsCalled { get; init; }
    public IReadOnlyList<string>? RetrievalContext { get; init; }
}

/// <summary>
/// A test case for multi-turn conversation testing.
/// </summary>
public record ConversationalTestCase
{
    public required string Name { get; init; }
    public required IReadOnlyList<Turn> Turns { get; init; }
    public string? Scenario { get; init; }
    public string? ExpectedOutcome { get; init; }
    public string? ChatbotRole { get; init; }
    public string? UserDescription { get; init; }
}

/// <summary>
/// Result of evaluating a conversation.
/// </summary>
public record ConversationResult
{
    public required string TestCaseName { get; init; }
    public bool Passed { get; init; }
    public double OverallScore { get; init; }
    public IReadOnlyList<TurnResult> TurnResults { get; init; } = [];
    public string? Summary { get; init; }
}

public record TurnResult
{
    public int TurnNumber { get; init; }
    public string Role { get; init; } = "";
    public double? Score { get; init; }
    public bool IntentionSatisfied { get; init; }
}
```

#### Conversation Runner

```csharp
public class ConversationRunner
{
    private readonly ITestableAgent _agent;
    private readonly IEvaluator? _evaluator;
    
    /// <summary>
    /// Run a scripted conversation test where user turns are predefined.
    /// </summary>
    public async Task<ConversationResult> RunScriptedAsync(
        ConversationalTestCase testCase,
        CancellationToken ct = default)
    {
        var actualTurns = new List<Turn>();
        var turnResults = new List<TurnResult>();
        var conversationHistory = new List<string>();
        
        foreach (var expectedTurn in testCase.Turns)
        {
            if (expectedTurn.Role == "user")
            {
                // User turn is scripted - just add to history
                conversationHistory.Add($"User: {expectedTurn.Content}");
                actualTurns.Add(expectedTurn);
            }
            else // assistant
            {
                // Get actual agent response
                var prompt = BuildConversationPrompt(conversationHistory, testCase.ChatbotRole);
                var response = await _agent.InvokeAsync(prompt, ct);
                var toolUsage = ToolUsageExtractor.Extract(response);
                
                var actualTurn = new Turn
                {
                    Role = "assistant",
                    Content = response.Text,
                    ToolsCalled = toolUsage.Calls
                };
                actualTurns.Add(actualTurn);
                conversationHistory.Add($"Assistant: {response.Text}");
                
                // Evaluate turn
                var turnScore = await EvaluateTurnAsync(expectedTurn, actualTurn, ct);
                turnResults.Add(new TurnResult
                {
                    TurnNumber = actualTurns.Count,
                    Role = "assistant",
                    Score = turnScore,
                    IntentionSatisfied = turnScore >= 70
                });
            }
        }
        
        // Calculate overall score
        var satisfiedIntentions = turnResults.Count(t => t.IntentionSatisfied);
        var totalIntentions = turnResults.Count;
        var completeness = totalIntentions > 0 ? (double)satisfiedIntentions / totalIntentions : 0;
        
        return new ConversationResult
        {
            TestCaseName = testCase.Name,
            Passed = completeness >= 0.7,
            OverallScore = completeness * 100,
            TurnResults = turnResults,
            Summary = $"Satisfied {satisfiedIntentions}/{totalIntentions} user intentions"
        };
    }
}
```

#### Conversation Completeness Metric

```csharp
/// <summary>
/// Measures what fraction of user intentions were satisfied in a conversation.
/// Score = Satisfied Intentions / Total Intentions
/// </summary>
public class ConversationCompletenessMetric : IMetric
{
    private readonly IChatClient _chatClient;
    
    public string Name => "ConversationCompleteness";
    public string Description => "Measures what fraction of user intentions were satisfied";
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
    {
        // Extract user intentions from context
        var conversation = context.GetProperty<ConversationalTestCase>("conversation");
        if (conversation == null)
        {
            return MetricResult.Fail(Name, "No conversation provided");
        }
        
        // Use LLM to evaluate intention satisfaction
        var userTurns = conversation.Turns.Where(t => t.Role == "user").ToList();
        var assistantTurns = conversation.Turns.Where(t => t.Role == "assistant").ToList();
        
        var satisfiedCount = 0;
        foreach (var (userTurn, assistantTurn) in userTurns.Zip(assistantTurns))
        {
            var isSatisfied = await EvaluateIntentionAsync(userTurn, assistantTurn, ct);
            if (isSatisfied) satisfiedCount++;
        }
        
        var score = userTurns.Count > 0 ? (double)satisfiedCount / userTurns.Count * 100 : 0;
        
        return new MetricResult
        {
            MetricName = Name,
            Score = score,
            Passed = score >= 50,
            Explanation = $"Satisfied {satisfiedCount}/{userTurns.Count} user intentions",
            Details = new Dictionary<string, object>
            {
                ["satisfiedIntentions"] = satisfiedCount,
                ["totalIntentions"] = userTurns.Count
            }
        };
    }
}
```

---

## Benchmark Compatibility ⭐ NEW

### Why Benchmark Support is a Game Changer

1. **Instant Credibility:** "Scores 85% on BFCL" vs "We tested it and it works"
2. **Comparability:** Compare your agent against 100+ published models on identical tasks
3. **Regression Detection:** Track performance changes across model versions
4. **Gap Analysis:** Identify specific capability gaps (parallel calls vs. sequential)

### Benchmark Compatibility Matrix

| Benchmark | Focus | AgentEval Support | What's Needed |
|-----------|-------|-------------------|---------------|
| **BFCL** | Function Calling | ✅ Ready | `BfclDatasetLoader` |
| **GAIA** | General AI Assistants | ✅ Ready | `GaiaDatasetLoader` |
| **ToolBench** | API Tool Use | ✅ Ready | Multi-step runner exists |
| **MINT** | Multi-turn Interaction | ⚠️ Partial | `ConversationRunner` (Phase 5) |
| **HumanEval** | Code Generation | ❌ Out of Scope | Requires code execution |
| **WebArena** | Web Browsing | ❌ Out of Scope | Requires browser simulation |
| **SWE-bench** | Software Engineering | ❌ Out of Scope | Requires codebase context |

### BFCL (Berkeley Function Calling Leaderboard)

**What It Tests:** Function/tool calling accuracy across categories

| Category | Description | AgentEval Metric |
|----------|-------------|------------------|
| `simple_python` | Single function calls | `ToolSelectionMetric` |
| `parallel` | Multiple independent calls | `ToolSelectionMetric` + order check |
| `multiple` | Sequential dependent calls | `AgenticBenchmark.MultiStep` |
| `multi_turn_base` | Multi-turn conversations | `ConversationRunner` |
| `live_*` | Real-world API scenarios | `AgenticBenchmark.ToolAccuracy` |

**Running BFCL with AgentEval:**

```csharp
// Load BFCL dataset
var loader = new BfclDatasetLoader();
var testCases = await loader.LoadBenchmarkAsync("bfcl/simple_python.json", ct);

// Convert to AgentEval test cases
var toolTestCases = testCases.Select(bc => new ToolAccuracyTestCase
{
    Name = bc.Id,
    Prompt = bc.Input,
    ExpectedTools = bc.ExpectedTools
}).ToList();

// Run benchmark
var benchmark = new AgenticBenchmark(agent);
var results = await benchmark.RunToolAccuracyBenchmarkAsync(toolTestCases, ct);

// Compare against leaderboard
Console.WriteLine($"BFCL Simple Python: {results.OverallAccuracy:P1}");
// Expected: GPT-4o scores ~90%, Claude-3 scores ~85%
```

### GAIA (General AI Assistants)

**What It Tests:** Multi-step reasoning, tool use, web browsing

```csharp
// Load GAIA dataset
var loader = new GaiaDatasetLoader();
var testCases = await loader.LoadAsync("gaia/validation.jsonl", ct);

// Run with task completion benchmark
var benchmark = new AgenticBenchmark(agent, evaluator);
var taskCases = testCases.Select(tc => new TaskCompletionTestCase
{
    Name = tc.Name,
    Prompt = tc.Input,
    CompletionCriteria = new[] { tc.ExpectedOutcome }
}).ToList();

var results = await benchmark.RunTaskCompletionBenchmarkAsync(taskCases, ct);
Console.WriteLine($"GAIA Validation: {results.AverageScore:F1}/100");
```

### Standard Benchmark Reporting

```csharp
public class BenchmarkReport
{
    public required string BenchmarkName { get; init; }
    public required string AgentName { get; init; }
    public required string ModelVersion { get; init; }
    public DateTimeOffset RunDate { get; init; } = DateTimeOffset.UtcNow;
    
    // Scores by category
    public IReadOnlyDictionary<string, double> CategoryScores { get; init; } = new Dictionary<string, double>();
    
    // Overall score
    public double OverallScore { get; init; }
    
    // Comparison to known baselines
    public IReadOnlyDictionary<string, double> ComparisonToBaselines { get; init; } = new Dictionary<string, double>();
}

// Generate leaderboard-style report
var report = new BenchmarkReport
{
    BenchmarkName = "BFCL v3",
    AgentName = "MyWeatherAgent",
    ModelVersion = "gpt-4o-2024-11-20",
    CategoryScores = new Dictionary<string, double>
    {
        ["simple_python"] = 92.5,
        ["parallel"] = 88.0,
        ["multiple"] = 85.5,
        ["multi_turn"] = 78.0
    },
    OverallScore = 86.0,
    ComparisonToBaselines = new Dictionary<string, double>
    {
        ["GPT-4o (OpenAI)"] = -4.0,  // 4% below
        ["Claude-3.5 (Anthropic)"] = +1.0  // 1% above
    }
};
```

---

## Technical Debt & Optimizations

### Performance Optimizations

| Optimization | Impact | Effort |
|--------------|--------|--------|
| JSON source generators | Medium | Low |
| ArrayPool for string building | Low | Low |
| Parallel metric evaluation | High | Medium |
| Connection pooling for benchmarks | Medium | Low |
| Caching for repeated evaluations | Medium | Medium |

### Code Health

| Task | Priority | Effort |
|------|----------|--------|
| Extract ExtractToolUsage | High | Low |
| ConcurrentDictionary for ModelPricing | High | Low |
| Constants for magic numbers | Medium | Low |
| Nullable annotations | Medium | Medium |
| XML docs on all public APIs | High | Medium |
| Roslyn analyzers for best practices | Low | High |

### Testing Improvements

| Task | Priority | Effort |
|------|----------|--------|
| Integration tests with mock LLM | High | Medium |
| Edge case coverage | Medium | Medium |
| Performance regression tests | Medium | Medium |
| Fuzz testing for parsing | Low | High |

---

## Competitive Positioning

### Feature Comparison After All Phases

| Feature | DeepEval | Promptfoo | RAGAS | AgentEval |
|---------|----------|-----------|-------|-----------|
| .NET Native | ❌ | ❌ | ❌ | ✅ |
| Python | ✅ | ❌ | ✅ | ⏳ Consider |
| Fluent Assertions | ❌ | ❌ | ❌ | ✅ |
| MAF Integration | ❌ | ❌ | ❌ | ✅ |
| Tool Tracking | ✅ | ❌ | ❌ | ✅ |
| Streaming Metrics | ❌ | ❌ | ❌ | ✅ |
| RAG Metrics | Partial | ❌ | ✅ | ✅ |
| Agentic Metrics | ✅ | ❌ | ❌ | ✅ |
| Red-Teaming | ✅ | ✅ | ❌ | ✅ Planned |
| Synthetic Data | ✅ | ❌ | ✅ | ✅ Planned |
| CI/CD Integration | ✅ | ✅ | ❌ | ✅ Planned |
| Dashboard | ✅ (SaaS) | ✅ | ❌ | ✅ Planned |
| Cost Tracking | Partial | ❌ | ❌ | ✅ |
| VS Integration | ❌ | ❌ | ❌ | ✅ Planned |

### Messaging

**Tagline:** "The first .NET-native AI agent testing framework"

**Key Messages:**
1. Built for .NET developers, by .NET developers
2. Native integration with xUnit/NUnit/MSTest
3. First-class support for Microsoft Agent Framework
4. Fluent API that feels like FluentAssertions
5. Enterprise-ready with full observability

---

## Success Metrics

### Year 1 Targets

| Metric | Q1 | Q2 | Q3 | Q4 |
|--------|----|----|----|----|
| NuGet Downloads | 1K | 5K | 15K | 30K |
| GitHub Stars | 100 | 300 | 700 | 1,500 |
| Contributors | 2 | 5 | 10 | 20 |
| Framework Adapters | 1 (MAF) | 3 | 4 | 5 |
| Enterprise Users | 0 | 2 | 5 | 10 |

### Quality Metrics

| Metric | Target |
|--------|--------|
| Test Coverage | 90%+ |
| Documentation Coverage | 100% public API |
| Issue Response Time | < 24 hours |
| Release Cadence | Monthly |
| Breaking Changes | 0 per minor version |

---

## Missing Features & Enhancement Proposals

Based on analysis of Microsoft.Extensions.AI.Evaluation and kbeaugrand/KernelMemory.Evaluation, here are features we should consider adding:

### High Priority - Unique Value

| Feature | Benefit | Effort | Priority |
|---------|---------|--------|----------|
| **Microsoft Evaluator Adapters** | Use official MS evaluators via our API | Medium | ⭐⭐⭐⭐⭐ |
| **Embedding-based Similarity** | More accurate relevance/equivalence scoring | Medium | ⭐⭐⭐⭐ |
| **Statement Extraction** | Better faithfulness via claim-level analysis | Medium | ⭐⭐⭐⭐ |
| **Retry Logic** | Resilience for LLM evaluation calls | Low | ⭐⭐⭐⭐ |
| **Score Normalization** | Convert between 1-5 and 0-100 scales | Low | ⭐⭐⭐⭐ |

### Medium Priority - Parity Features

| Feature | Source | Description |
|---------|--------|-------------|
| **Test Set Generation** | kbeaugrand | Synthetic question generation from documents |
| **Multi-context Questions** | kbeaugrand | Questions requiring multiple context chunks |
| **Reasoning Questions** | kbeaugrand | Questions requiring inference |
| **BLEU Score** | Microsoft | Standard NLP metric for translation quality |

### Lower Priority - Nice to Have

| Feature | Description |
|---------|-------------|
| **Prompt Templates as Resources** | Store prompts in .txt files like kbeaugrand |
| **Temperature=0 Enforcement** | Deterministic evaluation outputs |
| **Tag-based Response Parsing** | `<S0>`, `<S1>` parsing like Microsoft |
| **EvaluationDiagnostic** | Error/warning collection per metric |
| **Metric Interpretation** | Human-readable score interpretation |

### Proposed New Metrics

```csharp
// 1. Answer Semantic Similarity (embedding-based)
public class AnswerSimilarityMetric : IRAGMetric
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
    {
        var embeddings = await _embeddings.GenerateEmbeddingsAsync(
            [context.GroundTruth!, context.Output], ct);
        
        var similarity = TensorPrimitives.CosineSimilarity(
            embeddings[0].Vector.Span, 
            embeddings[1].Vector.Span);
        
        return MetricResult.Pass(Name, similarity * 100, $"Similarity: {similarity:P1}");
    }
}

// 2. Fluency Metric (linguistic correctness)
public class FluencyMetric : IMetric
{
    // Adapted from Microsoft.Extensions.AI.Evaluation.Quality.FluencyEvaluator
}

// 3. Coherence Metric (logical flow)
public class CoherenceMetric : IMetric
{
    // Adapted from Microsoft.Extensions.AI.Evaluation.Quality.CoherenceEvaluator
}
```

### Integration Decision Matrix

| Approach | Pros | Cons | Recommendation |
|----------|------|------|----------------|
| **Use MS.Extensions.AI.Evaluation directly** | Official, maintained, tested | Experimental API, less control | For quality metrics |
| **Port patterns only** | Full control, no dependency | More work, maintenance | For agentic-specific |
| **Dual support** | Best of both worlds | Complexity | ⭐ RECOMMENDED |

---

## Immediate Next Steps

### ✅ COMPLETED (Jan 2-5, 2026)

1. [x] **Fix critical issues:** ✅ COMPLETE
   - ✅ Extract duplicate ExtractToolUsage → `Core/ToolUsageExtractor.cs`
   - ✅ Add ConcurrentDictionary to ModelPricing
   - ✅ Extract magic numbers → `Core/EvaluationDefaults.cs`
   - ✅ Add null checks to constructors
   - ✅ Create JSON parsing helper → `Core/LlmJsonParser.cs`

2. [x] **Trace-First Failure Reporting:** ✅ COMPLETE
   - ✅ FailureReport model with structured failure information
   - ✅ ToolCallTimeline for detailed tool execution traces
   - ✅ IAgentEvalLogger abstraction (Console, Null, MicrosoftExtensions adapters)
   - ✅ MAFTestHarness integration - 4 call sites creating FailureReports
   - ✅ LogFailure method for trace-first output

3. [x] **Metric Tests (Phase 6-7):** ✅ COMPLETE
   - ✅ FakeChatClient for zero-dependency testing (no NSubstitute needed)
   - ✅ FaithfulnessMetricTests (7 tests)
   - ✅ ToolSelectionMetricTests (9 tests)
   - ✅ ToolSuccessMetricTests (8 tests)
   - ✅ RAG metric tests (Relevance, ContextPrecision, ContextRecall, AnswerCorrectness)
   - ✅ Agentic metric tests (ToolArguments, TaskCompletion, ToolEfficiency)
   - ✅ Integration tests (MicrosoftEvaluatorAdapter, AgenticBenchmark, MAFTestHarness)
   - ✅ **All 384 tests passing**

4. [x] **Documentation Cleanup:** ✅ COMPLETE
   - ✅ Deleted 11 redundant documentation files from root
   - ✅ Consolidated docs into `docs/building-docs.md`
   - ✅ Created `scripts/` folder for build scripts
   - ✅ Created `.github/workflows/docs.yml` for auto-publish

### 🚀 PRIORITY: Phase 5 Production Infrastructure ✅ COMPLETED

5. [x] **Result Exporters:** ✅ COMPLETE
   - ✅ Created `Exporters/` folder
   - ✅ Implemented `IResultExporter` interface
   - ✅ `JUnitXmlExporter` - CI/CD integration (GitHub Actions, Azure DevOps)
   - ✅ `MarkdownExporter` - GitHub PR comments
   - ✅ `JsonExporter` - Programmatic analysis
   - ✅ `TrxExporter` - Visual Studio test results format
   - ✅ `ResultExporterFactory` - Factory for creating exporters
   - ✅ Full test coverage for all exporters

6. [x] **CLI Tool:** ✅ COMPLETE
   - ✅ Created `AgentEval.Cli` project with `System.CommandLine`
   - ✅ Implemented `eval` command with real dataset validation
   - ✅ Dataset loading from YAML, JSON, JSONL, CSV files
   - ✅ Test case validation (completeness, ground truth, expected tools, context)
   - ✅ Multiple output formats (JSON, JUnit, Markdown, TRX)
   - ✅ Cross-platform color support with NO_COLOR environment variable respect
   - ✅ Implemented `init` command (create starter config)
   - ✅ Implemented `list` command (metrics, assertions, formats)
   - ✅ Configured as .NET global tool

7. [x] **Snapshot Testing:** ✅ COMPLETE
   - ✅ Created `Testing/SnapshotComparer.cs`
   - ✅ Created `Testing/SnapshotStore.cs`
   - ✅ JSON-based comparison with field ignoring
   - ✅ Regex-based content scrubbing
   - ✅ Full test coverage

8. [x] **Dataset Loaders:** ✅ COMPLETE
   - ✅ Created `DataLoaders/` folder
   - ✅ Implemented `IDatasetLoader` interface
   - ✅ `JsonDatasetLoader` - JSON format
   - ✅ `JsonLinesDatasetLoader` - JSONL format
   - ✅ `CsvDatasetLoader` - CSV format
   - ✅ `YamlDatasetLoader` - YAML format with field aliasing
   - ✅ `DatasetLoaderFactory` - Factory for creating loaders
   - ✅ Sample datasets: `travel-agent.yaml`, `rag-qa.yaml`
   - ✅ Comprehensive test coverage (750+ lines of tests)

### 📚 Documentation Updates ✅ IN PROGRESS

9. [ ] **Create docs/architecture.md:**
   - [ ] Component diagram (Mermaid)
   - [ ] Metric hierarchy visualization
   - [ ] Layer descriptions

10. [ ] **Create docs/embedding-metrics.md:**
    - [ ] Document AnswerSimilarityMetric
    - [ ] Document ResponseContextSimilarityMetric
    - [ ] Document QueryContextSimilarityMetric
    - [ ] Usage examples

11. [ ] **Create docs/extensibility.md:**
    - [ ] Custom metrics guide
    - [ ] Plugin system documentation
    - [ ] Microsoft adapter usage

12. [ ] **Create docs/benchmarks.md:**
    - [ ] BFCL integration guide
    - [ ] GAIA integration guide
    - [ ] ToolBench integration guide
    - [ ] How to compare against leaderboards

13. [ ] **Update README.md:**
    - [ ] Add "Supported Benchmarks" section
    - [ ] Add CI/CD Integration section
    - [ ] Add comparison table vs competitors

### Phase 6: Multi-Turn & Advanced (After Phase 5)

14. [ ] **Multi-Turn Conversations:**
    - [ ] Add Turn and ConversationalTestCase models
    - [ ] Implement ConversationRunner (scripted first)
    - [ ] Implement ConversationCompletenessMetric
    - [ ] Add tests

15. [ ] **Prepare for NuGet Publication:**
    - [ ] Complete XML documentation on all public APIs
    - [ ] Create NuGet README
    - [ ] Configure SourceLink
    - [ ] Set up deterministic builds
    - [ ] Create standalone GitHub repository

---

*This is a living document. Update as priorities evolve.*

**Last Updated:** January 6, 2026  
**Next Review:** Phase 6 planning  
**Owner:** AgentEval Team
