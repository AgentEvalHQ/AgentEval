# Evaluation Guide

> **How to choose the right metrics for your AI evaluation needs**

---

## Quick Start Decision Tree

```
What are you evaluating?
│
├─► RAG System (retrieval + generation)
│   │
│   ├─► Is retrieval finding relevant documents?
│   │   └─► Use: code_recall_at_k, code_mrr (FREE!)
│   │
│   ├─► Is the response grounded in context?
│   │   └─► Use: llm_faithfulness, embed_response_context
│   │
│   ├─► Is the retrieved context good?
│   │   └─► Use: llm_context_precision, llm_context_recall
│   │
│   └─► Is the answer correct?
│       └─► Use: llm_answer_correctness, embed_answer_similarity
│
├─► AI Agent (tool-using)
│   │
│   ├─► Are the right tools being selected?
│   │   └─► Use: code_tool_selection
│   │
│   ├─► Are tools called correctly?
│   │   └─► Use: code_tool_arguments, code_tool_success
│   │
│   ├─► Is the agent efficient?
│   │   └─► Use: code_tool_efficiency
│   │
│   └─► Does it complete tasks?
│       └─► Use: llm_task_completion
│
└─► General LLM Quality
    │
    └─► Is the response relevant?
        └─► Use: llm_relevance
```

---

## Evaluation Strategies by Use Case

### 1. CI/CD Pipeline Testing

**Goal:** Fast, free tests that run on every commit.

**Recommended Metrics:**
- `code_tool_selection` - Verify correct tools
- `code_tool_arguments` - Validate parameters
- `code_tool_success` - Check execution success
- `code_tool_efficiency` - Monitor performance

**Why:** Code-based metrics are free, fast, and deterministic.

```csharp
[Fact]
public async Task TravelAgent_BookFlight_SelectsCorrectTools()
{
    var metric = new ToolSelectionMetric(["FlightSearchTool", "BookingTool"]);
    var result = await metric.EvaluateAsync(context);
    
    result.Score.Should().BeGreaterThan(80);
}
```

---

### 2. RAG Quality Assessment

**Goal:** Ensure retrieval and generation quality.

**Recommended Metrics:**

| Phase | Metric | Purpose | Cost |
|-------|--------|---------|------|
| Retrieval | `code_recall_at_k` | Are relevant docs found? | **Free** |
| Retrieval | `code_mrr` | Is relevant doc ranked first? | **Free** |
| Retrieval | `llm_context_precision` | Is retrieved content relevant? | LLM |
| Retrieval | `llm_context_recall` | Is all needed info retrieved? | LLM |
| Generation | `llm_faithfulness` | Is response grounded in context? | LLM |
| Generation | `llm_answer_correctness` | Is the answer factually correct? | LLM |

**Cost-Optimized Strategy:**
1. **CI/CD (Free):** Use `code_recall_at_k` and `code_mrr` for retrieval testing
2. **Volume Testing ($):** Use `embed_response_context` and `embed_answer_similarity`
3. **Production Sampling ($$):** Use `llm_faithfulness` and `llm_answer_correctness`

**Example: Retrieval Testing (FREE)**
```csharp
// Test retrieval quality without any API calls
var recallMetric = new RecallAtKMetric(k: 5);
var mrrMetric = new MRRMetric();

var context = new EvaluationContext
{
    RelevantDocumentIds = ["doc1", "doc2", "doc3"],
    RetrievedDocumentIds = ["doc1", "doc4", "doc2", "doc5", "doc6"]
};

var recall = await recallMetric.EvaluateAsync(context); // 67% (2/3 found)
var mrr = await mrrMetric.EvaluateAsync(context);       // 100% (first relevant at rank 1)
```

---

### 3. Agent Task Completion

**Goal:** Verify agents complete end-to-end tasks.

**Recommended Approach:**

```csharp
// 1. Fast tool validation (code-based)
var toolMetric = new ToolSelectionMetric(expectedTools);
var toolResult = await toolMetric.EvaluateAsync(context);

// 2. Deep task evaluation (LLM-based, sample)
if (IsProductionSample())
{
    var taskMetric = new TaskCompletionMetric(chatClient);
    var taskResult = await taskMetric.EvaluateAsync(context);
}
```

---

### 4. stochastic evaluation

**Goal:** Account for LLM non-determinism.

**Approach:** Run same test multiple times, analyze statistics.

```csharp
var runner = new StochasticRunner(harness, options);
var result = await runner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8));

// Analyze: min, max, mean, std dev
result.Statistics.Mean.Should().BeGreaterThan(75);
result.Statistics.StandardDeviation.Should().BeLessThan(15);
```

---

### 5. Model Comparison

**Goal:** Compare different models on same tasks.

**Approach:**
```csharp
var comparer = new ModelComparer(harness);
var results = await comparer.CompareModelsAsync(
    factories: [gpt4Factory, gpt35Factory, claudeFactory],
    testCases: testSuite,
    metrics: [faithfulness, relevance]);

results.PrintComparisonTable();
// Shows: Model | Mean Score | Cost | Latency
```

---

## Metric Selection by Data Availability

### What data do you have?

| I have... | Recommended Metrics |
|-----------|---------------------|
| Query + Response only | `llm_relevance` |
| Query + Response + Context | `llm_faithfulness`, `llm_context_precision`, `embed_response_context` |
| Query + Response + Ground Truth | `llm_answer_correctness`, `embed_answer_similarity` |
| Query + Response + Context + Ground Truth | All RAG metrics |
| Query + Response + Tool Calls | All agentic metrics |
| Retrieved + Relevant Document IDs | `code_recall_at_k`, `code_mrr` (**FREE!**) |

---

## Cost vs. Accuracy Trade-offs

```
Accuracy ▲
         │
    100% │    ★ llm_answer_correctness
         │    ★ llm_faithfulness
     90% │
         │    ● llm_context_precision
     80% │    ● llm_relevance
         │
     70% │        ▲ embed_answer_similarity
         │        ▲ embed_response_context
     60% │
         │            ■ code_recall_at_k  ■ code_mrr
     50% │            ■ code_tool_selection
         │            ■ code_tool_success
         │
         └───────────────────────────────────► Cost
              Free    $0.01   $0.05   $0.10
         
Legend: ★ LLM metrics  ▲ Embedding metrics  ■ Code metrics (FREE!)
```

**Guidance:**
- Use code metrics for CI/CD (free, fast, deterministic)
- Use IR metrics (`code_recall_at_k`, `code_mrr`) for retrieval testing (free!)
- Use embedding metrics for volume testing (cheap, good accuracy)
- Use LLM metrics for production sampling (expensive, highest accuracy)

---

## Recommended Evaluation Suites

### Minimal Suite (CI/CD)
```csharp
var metrics = new IMetric[]
{
    new ToolSelectionMetric(expectedTools),
    new ToolSuccessMetric()
};
```

### Standard Suite (Development)
```csharp
var metrics = new IMetric[]
{
    new FaithfulnessMetric(chatClient),
    new RelevanceMetric(chatClient),
    new ToolSelectionMetric(expectedTools),
    new ToolSuccessMetric()
};
```

### Comprehensive Suite (Release)
```csharp
var metrics = new IMetric[]
{
    // Information Retrieval (FREE)
    new RecallAtKMetric(k: 10),
    new MRRMetric(),
    
    // RAG Quality
    new FaithfulnessMetric(chatClient),
    new ContextPrecisionMetric(chatClient),
    new ContextRecallMetric(chatClient),
    new AnswerCorrectnessMetric(chatClient),
    
    // Agentic Quality
    new ToolSelectionMetric(expectedTools),
    new ToolArgumentsMetric(schema),
    new ToolSuccessMetric(),
    new ToolEfficiencyMetric(),
    new TaskCompletionMetric(chatClient)
};
```

---

## Common Patterns

### Pattern 1: Sampling Expensive Metrics

Run expensive LLM metrics on a sample:

```csharp
var sampleRate = 0.1; // 10% of traffic

if (Random.Shared.NextDouble() < sampleRate)
{
    await faithfulnessMetric.EvaluateAsync(context);
}
```

### Pattern 2: Tiered Evaluation

Start cheap, escalate if concerns:

```csharp
// Fast embedding check
var embedScore = await embedMetric.EvaluateAsync(context);

// Only call expensive LLM if embedding score is borderline
if (embedScore.Score < 80)
{
    var llmScore = await llmMetric.EvaluateAsync(context);
    return llmScore;
}

return embedScore;
```

### Pattern 3: Composite Scoring

Combine multiple metrics into one score:

```csharp
var scores = await Task.WhenAll(
    faithfulness.EvaluateAsync(context),
    relevance.EvaluateAsync(context),
    toolSuccess.EvaluateAsync(context));

var compositeScore = scores.Average(s => s.Score);
```

---

## Anti-Patterns to Avoid

| ❌ Don't | ✅ Do Instead |
|---------|---------------|
| Run LLM metrics on every request | Sample 1-10% for production |
| Use only code metrics for quality | Combine with LLM metrics for accuracy |
| Ignore stochasticity | Run multiple times, analyze statistics |
| Test with same data as training | Use held-out test sets |
| Skip ground truth when available | Use `llm_answer_correctness` |

---

## See Also

- [RAG Metrics](rag-metrics.md) - Complete RAG evaluation guide
- [Metrics Reference](metrics-reference.md) - Complete metric catalog
- [stochastic evaluation](stochastic-testing.md) - Handle LLM variability
- [Model Comparison](model-comparison.md) - Compare models

---

*Last updated: January 2026*
