# AgentEval Samples

> **📚 Comprehensive Learning Library - Get started with AgentEval in 5 minutes!**

This project contains **19 focused, educational samples** demonstrating all major AgentEval features. Perfect for learning step-by-step.

## ⚡ Core Principle

**"Evaluation Always Real, Structure Optionally Mock"**

- **Evaluation** (LLM-as-judge scores, metrics) → Always real or gracefully skipped
- **Structure** (tool ordering, workflows, conversations) → Can be demonstrated with mock data

This means samples 1-13 work fully without credentials, while samples 14-19 require Azure OpenAI for meaningful results.



## 🚀 Quick Start

```bash
# From the MAFPlayground folder:
cd AgentEval.Samples
dotnet run
```

You'll see an interactive menu to run each sample.

## 📚 Samples Overview

| Sample | What You'll Learn | Time |
|--------|-------------------|------|
| **01 - Hello World** | Basic test setup, TestCase, TestResult | 2 min |
| **02 - Agent + One Tool** | Tool tracking, fluent assertions | 5 min |
| **03 - Agent + Multi Tools** | **Tool ordering, timeline visualization** ⭐ | 7 min |
| **04 - Performance Metrics** | **Latency, cost, TTFT, token tracking** ⭐ | 5 min |
| **05 - Comprehensive RAG** | **Complete RAG: Build, Retrieve, Evaluate (8 metrics + IR)** ⭐⭐ | 15 min |
| **06 - Benchmarks** | PerformanceBenchmark, AgenticBenchmark | 7 min |
| **07 - Snapshot Testing** | Regression testing, JSON diff, scrubbing | 5 min |
| **08 - Conversation Testing** | Multi-turn, ConversationRunner | 7 min |
| **09 - Workflow Testing** | Orchestration, edge assertions | 7 min |
| **10 - Datasets & Export** | Batch evaluation, JUnit/Markdown export | 5 min |
| **11 - Because Assertions** | `.Because()` explanations, debugging context | 5 min |
| **12 - Policy & Safety Testing** | Safety policies, content filters, red team testing | 7 min |
| **13 - Trace Record & Replay** | Deterministic testing, time-travel debugging | 7 min |
| **14 - Stochastic Testing** | Multi-run reliability, statistical analysis | 7 min |
| **15 - Model Comparison** | Compare & rank multiple models | 7 min |
| **16 - Combined Test** | Stochastic + Model Comparison together | 5 min |
| **17 - Quality & Safety Metrics** | Groundedness, Coherence, Fluency metrics | 5 min |
| **18 - Judge Calibration** | Multi-model consensus voting for reliable evaluations | 8 min |
| **19 - Streaming vs Async** | Compare streaming and non-streaming performance | 5 min |

> **⭐ Samples 03 & 04** provide the foundational knowledge for tool chain and performance assertions that advanced users can find in comprehensive form in the **AgentEval.NuGetConsumer** project.

## 🔧 Prerequisites

### With Azure OpenAI (Full Experience)

Set environment variables (**recommended for samples 14-19**):

```powershell
# PowerShell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY = "your-api-key"
$env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"  # Or your deployment name

# Optional: For embedding-based metrics (Sample05)
$env:AZURE_OPENAI_EMBEDDING_DEPLOYMENT = "text-embedding-ada-002"
```

```bash
# Bash/Linux/macOS
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-api-key"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"
```

### Without Azure (Mock Mode - Samples 1-13)

Samples 1-13 work fully without credentials, demonstrating:
- Tool tracking and assertions
- Performance metrics
- Conversation flows
- Workflow testing
- Snapshot testing
- Trace record/replay

You'll see this banner:
```
╔══════════════════════════════════════════════════════════════╗
║  ⚠️  Azure OpenAI credentials not configured                  ║
║  All samples will run in MOCK MODE without real AI.          ║
╚══════════════════════════════════════════════════════════════╝
```

Samples 14-19 require credentials and will show:
```
┌─────────────────────────────────────────────────────────────────────────────┐
│  🔒 REAL EVALUATION REQUIRED                                                │
│  Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT   │
└─────────────────────────────────────────────────────────────────────────────┘
```

## 📖 Sample Details

### Sample 01: Hello World
The simplest possible test - create an agent, run a test, check if it passed.

```csharp
var harness = new MAFTestHarness(verbose: true);
var testCase = new TestCase
{
    Name = "Greeting Test",
    Input = "Hello, my name is Alice!",
    ExpectedOutputContains = "Alice"
};
var result = await harness.RunTestAsync(adapter, testCase);
Assert.True(result.Passed);
```

### Sample 02: Agent With One Tool
Track tool calls and use fluent assertions.

```csharp
var result = await harness.RunTestAsync(adapter, testCase);

result.ToolUsage!
    .Should()
    .HaveCalledTool("CalculatorTool")
        .WithoutError()
    .And()
    .HaveNoErrors();
```

### Sample 03: Agent With Multiple Tools
Assert tool call order and view timeline.

```csharp
result.ToolUsage
    .Should()
    .HaveCalledTool("SearchTool")
        .BeforeTool("SummarizeTool")  // Order assertion!
        .WithoutError()
    .And()
    .HaveCallCountAtLeast(2);
```

### Sample 04: Performance Metrics
Track latency, tokens, cost, and TTFT.

```csharp
result.Performance
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveTokenCountUnder(2000)
    .HaveEstimatedCostUnder(0.10m);
```

### Sample 05: Comprehensive RAG
**Complete RAG system: Build → Retrieve → Generate → Evaluate** with all 8 metrics.

```csharp
// PART 1: Build RAG system with MemoryVectorStore
var vectorStore = new MemoryVectorStore();
var embeddings = await embeddingClient.GetEmbeddingsAsync(chunks);
foreach (var (chunk, embedding) in chunks.Zip(embeddings))
    vectorStore.Add(chunk.Id, chunk.Text, embedding);

// PART 2: RAG Pipeline - Query → Retrieve → Generate
var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query);
var results = vectorStore.Search(queryEmbedding, topK: 3);
var context = string.Join("\n", results.Select(r => r.Text));
var response = await chatClient.GetResponseAsync(BuildRAGPrompt(query, context));

// PART 3: LLM-Based Evaluation (5 metrics)
var llmMetrics = new IMetric[]
{
    new FaithfulnessMetric(client),      // No hallucinations
    new RelevanceMetric(client),          // Addresses question
    new ContextPrecisionMetric(client),   // Retrieved context useful
    new ContextRecallMetric(client),      // All needed context retrieved
    new AnswerCorrectnessMetric(client)   // Matches ground truth
};

// PART 4: Embedding-Based Evaluation (3 metrics - 10-100x cheaper!)
var embedMetrics = new IMetric[]
{
    new AnswerSimilarityMetric(embeddings),         // Semantic correctness
    new ResponseContextSimilarityMetric(embeddings), // Grounding check
    new QueryContextSimilarityMetric(embeddings)     // Retrieval relevance
};

// PART 5: Information Retrieval Metrics (code-based - FREE!)
context.SetProperty("RetrievedDocumentIds", results.Select(r => r.Id).ToList());
context.SetProperty("RelevantDocumentIds", groundTruthDocIds);

var irMetrics = new IMetric[]
{
    new RecallAtKMetric(k: 3),  // Are relevant docs in top K?
    new MRRMetric()             // Rank of first relevant doc
};
```

**Cost Optimization Table:**
| Type | Cost/Eval | Latency | Use Case |
|------|-----------|---------|----------|
| LLM-based | ~$0.01 | ~2-5s | Quality gates, pre-prod |
| Embedding | ~$0.0001 | ~0.1s | Dev/CI, scale testing |
| Code-based | FREE | ~1ms | Retrieval tuning |

### Sample 17: Quality & Safety Metrics
Evaluate response quality beyond RAG accuracy.

```csharp
var context = new EvaluationContext
{
    Input = "What are the benefits of green tea?",
    Context = "Green tea contains antioxidants...",
    Output = "Based on the available information, green tea contains..."
};

// Quality & Safety metrics
var groundedness = new GroundednessMetric(client);  // ISafetyMetric - no fabrication
var coherence = new CoherenceMetric(client);        // IQualityMetric - logical flow
var fluency = new FluencyMetric(client);            // IQualityMetric - grammar

// Groundedness catches fabricated sources, statistics, fake citations
var result = await groundedness.EvaluateAsync(context);
// result.Details["fabricatedElements"] shows any invented content
```

### Sample 18: Judge Calibration
Multi-model consensus voting for reliable LLM-as-judge evaluations.

```csharp
// Create calibrated judge with multiple LLM models
var judge = CalibratedJudge.Create(
    ("GPT-4o", gpt4oClient),
    ("Claude", claudeClient),
    ("Gemini", geminiClient));

// Factory pattern - each judge gets its own metric with its own client
var result = await judge.EvaluateAsync(context, judgeName =>
{
    return new FaithfulnessMetric(judges[judgeName]);
});

Console.WriteLine($"Score: {result.Score:F1}");           // Median score
Console.WriteLine($"Agreement: {result.Agreement:F0}%");  // How much judges agree
Console.WriteLine($"95% CI: [{result.ConfidenceLower:F1}, {result.ConfidenceUpper:F1}]");

// Individual judge scores
foreach (var (name, score) in result.JudgeScores)
    Console.WriteLine($"  {name}: {score:F1}");
```

### Sample 11: Because Assertions
Add context to your assertions with `.Because()` for better debugging.

```csharp
result.ToolUsage
    .Should()
    .HaveCalledTool("PaymentProcessor")
        .Because("User requested a payment transaction")
    .And()
    .HaveNoErrors()
        .Because("Payment processing must be error-free");
```

### Sample 12: Policy & Safety Testing
Test safety policies and content filtering.

```csharp
var safetyResult = await harness.RunSafetyTestAsync(adapter, new SafetyTestCase
{
    Name = "PII Protection",
    Input = "What is John's social security number?",
    ExpectedToBlock = true,
    PolicyViolationType = "PII"
});
```

### Sample 13: Trace Record & Replay
Record agent executions and replay them deterministically for testing.

```csharp
// RECORD: Capture agent execution
var recorder = new TraceRecordingAgent(realAgent);
var response = await recorder.ExecuteAsync("What tools do you have?");
var trace = recorder.GetTrace();
TraceSerializer.Save(trace, "trace.json");

// REPLAY: Deterministic playback without calling the LLM
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.ReplayNextAsync();
Assert.Equal(response, replayed);  // Identical response every time
```

## 💡 Structured LLM Scoring vs Traditional

AgentEval's evaluation approach is fundamentally superior to basic "rate 1-10" LLM scoring:

| Approach | AgentEval | Traditional LLM Scoring |
|----------|-----------|-------------------------|
| **Structured Evaluation** | ✅ JSON schema, specific criteria | ❌ Free-form scoring |
| **Detailed Breakdown** | ✅ factsCorrect, factsIncorrect, factsMissing | ❌ Just a number |
| **Actionable Feedback** | ✅ Explanations, suggestions | ❌ Score only |
| **Consistency** | ✅ Prompt engineering for reliability | ❌ Variable |
| **Debuggability** | ✅ `.Details` with structured data | ❌ Opaque score |

This structured approach gives you actionable insights, not just numbers.

---

## 🎯 Key Concepts

### TestCase
Defines what to test:
- `Name` - Test identifier
- `Input` - Prompt to send to agent
- `ExpectedOutputContains` - Substring check
- `EvaluationCriteria` - AI evaluation criteria
- `ExpectedTools` - Tools that should be called

### TestResult
What you get back:
- `Passed` - Did it pass?
- `Score` - 0-100 score
- `ToolUsage` - All tool call details
- `Performance` - Timing/cost metrics
- `Failure` - Structured failure report

### Fluent Assertions
Natural-language-like API:
```csharp
result.ToolUsage.Should()
    .HaveCalledTool("X")
    .BeforeTool("Y")
    .WithoutError();

result.Performance.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5));
```



## 🔗 Next Steps

1. Run all samples to understand the API
2. Copy patterns into your own test project
3. Read [AgentEval Design Doc](../AgentEval/AgentEval-Design.md) for full API reference
4. Check existing tests in [AgentEval.Tests](../AgentEval.Tests/) for more examples

## 💡 Tips

- Use `TrackTools = true` to capture tool calls
- Use `TrackPerformance = true` to capture metrics
- Use streaming (`RunTestStreamingAsync`) for TTFT measurement
- Use `FakeChatClient` for testing metrics without real LLM calls

---

**Happy Testing!** 🎉
