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
├─► Multi-Agent Workflow (orchestrated system)
│   │
│   ├─► Is the workflow structure correct?
│   │   └─► Use: code_workflow_structure_validity (FREE!)
│   │
│   ├─► Do agents execute in the right order?
│   │   └─► Use: code_workflow_execution_order (FREE!)
│   │
│   ├─► Are tools coordinated across agents?
│   │   └─► Use: code_workflow_tool_chain_validity (FREE!)
│   │
│   ├─► Do individual agents perform well?
│   │   └─► Use: Per-executor assertions + agent metrics
│   │
│   └─► Is the final workflow output high quality?
│       └─► Use: llm_workflow_output_quality
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

**Approach:** Run same evaluation multiple times, analyze statistics.

```csharp
var runner = new StochasticRunner(harness, statisticsCalculator: null, options);
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
var stochasticRunner = new StochasticRunner(harness);
var comparer = new ModelComparer(stochasticRunner);
var results = await comparer.CompareModelsAsync(
    factories: [gpt4Factory, gpt35Factory, claudeFactory],
    testCases: testSuite,
    metrics: [faithfulness, relevance]);

results.PrintComparisonTable();
// Shows: Model | Mean Score | Cost | Latency
```

---

### 6. Multi-Agent Workflow Evaluation

**Goal:** Evaluate complex multi-agent systems and orchestrated workflows.

**Key Challenges:**
- Multiple agents with different capabilities
- Sequential or parallel execution coordination
- Tool sharing and state management across agents
- End-to-end workflow performance
- Error propagation and recovery

**Recommended Approach:**

**Phase 1: Structural Validation (FREE)**
```csharp
// Fast workflow structure validation
var structureMetric = new WorkflowStructureValidityMetric();
var orderMetric = new WorkflowExecutionOrderMetric(
    expectedOrder: ["Planner", "Researcher", "Writer", "Editor"]
);

// Verify workflow topology and execution sequence
var result = await harness.RunWorkflowTestAsync(workflowAdapter, testCase);
result.ExecutionResult!.Should()
    .HaveStepCount(4, because: "content pipeline has 4 stages")
    .HaveExecutedInOrder("Planner", "Researcher", "Writer", "Editor")
    .HaveNoErrors();
```

**Phase 2: Per-Agent Performance**
```csharp
// Individual agent validation within workflow context
result.ExecutionResult!
    .ForExecutor("Researcher")
        .HaveCompletedWithin(TimeSpan.FromMinutes(3))
        .HaveCalledTool("ResearchTool")
        .HaveEstimatedCostUnder(0.20m)
        .And()
    .ForExecutor("Writer")
        .HaveOutputLongerThan(500, because: "content should be substantial")
        .HaveNonEmptyOutput()
        .And();
```

**Phase 3: Tool Chain Validation**
```csharp
// Multi-agent tool coordination
result.ExecutionResult!.Should()
    .HaveCalledTool("GetInfoAbout", because: "TripPlanner must research")
        .InExecutor("TripPlanner")
        .WithoutError()
        .And()
    .HaveCalledTool("SearchFlights")
        .BeforeTool("BookFlight", because: "must search before booking")
        .InExecutor("FlightReservation")
        .And()
    .HaveToolCallPattern("Search", "Book")  // Pattern across workflow
    .HaveNoToolErrors();
```

**Phase 4: End-to-End Quality (LLM-BASED)**
```csharp
// Overall workflow output assessment
var workflowQuality = new WorkflowOutputQualityMetric(chatClient, 
    criteria: "Evaluate if the multi-agent workflow produced coherent, complete output");

var qualityResult = await workflowQuality.EvaluateAsync(workflowContext);
qualityResult.Score.Should().BeGreaterThan(80);
```

**Cost-Optimized Workflow Strategy:**
1. **Structure Validation (FREE):** Always validate graph topology and execution order
2. **Performance Bounds (FREE):** Check timing, costs, basic success metrics
3. **Tool Coordination (FREE):** Validate multi-agent tool usage patterns
4. **Quality Sampling ($$):** Use LLM evaluation on subset of workflow outputs

**Example: Content Creation Pipeline**
```csharp
// Sample 09 
var testCase = new WorkflowTestCase
{
    Name = "Content Creation Pipeline",
    Input = "Create an article about sustainable technology",
    Agents = ["Planner", "Researcher", "Writer", "Editor"],
    WorkflowTimeout = TimeSpan.FromMinutes(10)
};

var result = await harness.RunWorkflowTestAsync(workflowAdapter, testCase);

// Comprehensive workflow validation
result.ExecutionResult!.Should()
    // Structure (FREE)
    .HaveStepCount(4)
    .HaveExecutedInOrder("Planner", "Researcher", "Writer", "Editor") 
    .HaveCompletedWithin(TimeSpan.FromMinutes(10))
    
    // Per-agent validation (FREE)  
    .ForExecutor("Writer")
        .HaveOutputLongerThan(200)
        .HaveEstimatedCostUnder(0.15m)
        .And()
        
    // Graph validation (FREE)
    .HaveGraphStructure()
        .HaveEntryPoint("Planner")
        .HaveExecutionPath("Planner", "Researcher", "Writer", "Editor")
        .And()
        
    // Final validation
    .HaveNoErrors();

// Optional: Deep quality assessment (LLM-based, for samples)
if (IsProductionSample())
{
    var qualityMetric = new WorkflowOutputQualityMetric(chatClient);
    var quality = await qualityMetric.EvaluateAsync(workflowContext);
    quality.Score.Should().BeGreaterThan(75);
}
```

**Workflow-Specific Metrics:**
- `code_workflow_structure_validity` - Graph topology validation (FREE)
- `code_workflow_execution_order` - Sequence verification (FREE)
- `code_workflow_executor_success` - Per-agent success rate (FREE)
- `code_workflow_tool_chain_validity` - Multi-agent tool patterns (FREE)
- `llm_workflow_output_quality` - End-to-end quality assessment (LLM)

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
| Mock evaluation LLM responses | Always use real LLM for evaluation metrics |

---

## Evaluation Always Real Principle

When building demos, samples, or tests, there's an important distinction between what should use real LLM calls versus what can be mocked.

### Core Principle

> **"Evaluation Always Real, Structure Optionally Mock"**

### What This Means

| Category | Mock OK? | Why |
|----------|----------|-----|
| **Agent responses** | ✅ Yes | Structure demos can show flows without real AI |
| **Tool call results** | ✅ Yes | Validates tool handling logic |
| **Conversation flows** | ✅ Yes | Tests multi-turn patterns |
| **Evaluation metrics** | ❌ No | Defeats the purpose of showing AI assessment |
| **LLM-as-a-Judge** | ❌ No | Hardcoded scores aren't real evaluation |
| **Consensus voting** | ❌ No | Multiple judges should have real variance |

### Acceptable vs Unacceptable Patterns

**❌ Silent Mocking (Bad)**
```csharp
// WRONG - User thinks they're seeing real evaluation
private IChatClient CreateEvaluatorClient()
{
    return new FakeChatClient("""{"score": 92, "explanation": "Mock"}""");
}
```

**✅ Explicit User Choice (Good)**
```csharp
// CORRECT - User explicitly chooses mock mode
Console.WriteLine("Select mode:");
Console.WriteLine("[1] MOCK MODE - Demo structure only");
Console.WriteLine("[2] REAL MODE - Full AI evaluation");

if (userChoice == "1")
    return CreateMockClient();  // User understands the trade-off
```

**✅ Graceful Skip (Good)**
```csharp
// CORRECT - Skip with explanation when not configured
if (!AIConfig.IsConfigured)
{
    Console.WriteLine("⚠️ LLM-as-a-Judge requires Azure OpenAI credentials.");
    Console.WriteLine("   Configure AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY");
    return null; // Caller handles gracefully
}
return CreateRealClient();
```

### When Testing Metrics Themselves

For **unit testing your metric implementations**, `FakeChatClient` is appropriate:

```csharp
// This is FINE - testing the metric code, not demonstrating evaluation
[Fact]
public async Task FaithfulnessMetric_ParsesLLMResponse_Correctly()
{
    var fakeClient = new FakeChatClient("""{"score": 85, "explanation": "Test"}""");
    var metric = new FaithfulnessMetric(fakeClient);
    
    var result = await metric.EvaluateAsync(context);
    
    Assert.Equal(85, result.Score);
}
```

The distinction: **Testing metric code** vs **Demonstrating evaluation capabilities**.

---

## See Also

- [RAG Metrics](rag-metrics.md) - Complete RAG evaluation guide
- [Metrics Reference](metrics-reference.md) - Complete metric catalog
- [stochastic evaluation](stochastic-evaluation.md) - Handle LLM variability
- [Model Comparison](model-comparison.md) - Compare models

---

*Last updated: January 2026*
