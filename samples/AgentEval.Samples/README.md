# AgentEval Samples

> **📚 Comprehensive Learning Library - Get started with AgentEval in 5 minutes!**

This project contains **23 focused, educational samples** demonstrating all major AgentEval features. Each sample covers a distinct capability, ensuring features are exhaustively demonstrated.

## ⚡ Core Principle

**"Evaluation Always Real, Structure Optionally Mock"**

- **Evaluation** (LLM-as-judge scores, metrics) → Always real or gracefully skipped
- **Structure** (tool ordering, workflows, conversations) → Can be demonstrated with mock data

This means samples 1-4 work fully without credentials (mock mode), while samples 5-24 require Azure OpenAI for meaningful results.



## 🚀 Quick Start

```bash
# From the MAFPlayground folder:
cd AgentEval.Samples
dotnet run
```

You'll see an interactive menu to run each sample.

## 📚 Samples Overview

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 01 | **Hello World** | Basic test setup, TestCase, TestResult | No | 2 min |
| 02 | **Agent + One Tool** | Tool tracking, fluent assertions | No | 5 min |
| 03 | **Agent + Multi Tools** | **Tool ordering, timeline visualization** ⭐ | No | 7 min |
| 04 | **Performance Metrics** | **Latency, cost, TTFT, token tracking** ⭐ | No | 5 min |
| 05 | **Comprehensive RAG** | **Complete RAG: Build, Retrieve, Evaluate (8 metrics + IR)** ⭐⭐ | Yes + Embed | 15 min |
| 06 | **Benchmarks** | PerformanceBenchmark, AgenticBenchmark | Yes | 5 min |
| 07 | **Snapshot Testing** | Regression testing, JSON diff, scrubbing | Yes | 5 min |
| 08 | **Conversation Evaluation** | Multi-turn testing, ConversationRunner, fluent builder API | Yes | 5 min |
| 09 | **Workflow Evaluation** | **Real MAF workflow: WorkflowBuilder + InProcessExecution** ⭐ | Yes | 15 min |
| 10 | **Workflow + Tools** | **TripPlanner pipeline: 4 agents with tool tracking** ⭐ | Yes | 15 min |
| 11 | **Datasets & Export** | Batch evaluation, YAML datasets, JUnit/Markdown/JSON/TRX export | Yes | 7 min |
| 12 | **Policy & Safety** | **NeverCallTool, PII detection, MustConfirmBefore, guardrails** 🛡️ | Yes | 8 min |
| 13 | **Trace Record & Replay** | Deterministic evaluation, multi-turn & workflow traces, streaming replay | Yes | 10 min |
| 14 | **Stochastic Evaluation** | Multi-run reliability, statistical analysis | Yes | 5 min |
| 15 | **Model Comparison** | Compare & rank models on quality, speed, cost, reliability | Yes (×3) | 10 min |
| 16 | **Combined Test** | Stochastic + Model Comparison with statistical rigor | Yes (×2) | 10 min |
| 17 | **Quality & Safety Metrics** | Groundedness, Coherence, Fluency metrics | Yes | 5 min |
| 18 | **Judge Calibration** | Multi-model consensus voting (Median, Mean, Weighted) | Yes (×3) | 8 min |
| 19 | **Streaming vs Async** | Compare streaming and non-streaming performance, TTFT | Yes | 8 min |
| 20 | **Red Team Basic** | **Security scan, OWASP LLM probes, attack resistance** 🛡️ | Yes | 5 min |
| 21 | **Red Team Advanced** | **Custom pipeline, OWASP compliance, export, baseline comparison** 🛡️ | Yes | 10 min |
| 23 | **Responsible AI** | **Toxicity, bias, misinformation metrics with counterfactual testing** 🛡️ | Yes | 5 min |
| 24 | **Benchmark System** | **Performance, agentic, standard, cost benchmarks with comparison** | Yes | 5 min |

> **⭐ Samples 03 & 04** provide the foundational knowledge for tool chain and performance assertions.
> **🛡️ Samples 20 & 21** demonstrate AgentEval's red team security scanning capabilities.
> **Yes (×3)** means the sample uses 3 model deployments (`AZURE_OPENAI_DEPLOYMENT`, `_2`, `_3`).

## 🔧 Prerequisites

### With Azure OpenAI (Full Experience)

Set environment variables (**recommended for samples 5-24**):

```powershell
# PowerShell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY = "your-api-key"
$env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"  # Or your deployment name

# Optional: For embedding-based metrics (Sample 05)
$env:AZURE_OPENAI_EMBEDDING_DEPLOYMENT = "text-embedding-ada-002"

# Optional: For multi-model samples (Samples 15, 16, 18)
$env:AZURE_OPENAI_DEPLOYMENT_2 = "gpt-4o-mini"    # Secondary model
$env:AZURE_OPENAI_DEPLOYMENT_3 = "gpt-4.1"         # Tertiary model
```

```bash
# Bash/Linux/macOS
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-api-key"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"
export AZURE_OPENAI_DEPLOYMENT_2="gpt-4o-mini"     # Optional: multi-model samples
```

### Without Azure (Mock Mode - Samples 1-4)

Samples 1-4 work fully without credentials, demonstrating:
- Basic test setup and evaluation
- Tool tracking and fluent assertions
- Tool ordering and timeline visualization
- Performance metrics (simulated)

You'll see this banner:
```
╔══════════════════════════════════════════════════════════════╗
║  ⚠️  Azure OpenAI credentials not configured                  ║
║  All samples will run in MOCK MODE without real AI.          ║
╚══════════════════════════════════════════════════════════════╝
```

Samples 5-24 require credentials and will show:
```
┌─────────────────────────────────────────────────────────────────────────────┐
│  ⚠️  SKIPPING SAMPLE XX - Azure OpenAI Credentials Required                │
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

### Sample 09: Workflow Evaluation (Real MAF)
Build and evaluate a real MAF workflow with WorkflowBuilder + InProcessExecution.

```csharp
// Build a 4-agent pipeline: Planner → Researcher → Writer → Editor
var workflow = WorkflowBuilder.Create()
    .AddAgent(plannerAgent)
    .AddAgent(researcherAgent)
    .AddAgent(writerAgent)
    .AddAgent(editorAgent)
    .Build();

var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, startAgent);
var result = await harness.RunWorkflowTestAsync(adapter, testCase, options);
```

### Sample 10: Workflow With Tools
TripPlanner pipeline with 4 agents using function calling.

```csharp
// TripPlannerAgent → FlightReservationAgent → HotelReservationAgent → PresenterAgent
// Each agent has its own tools (GetInfoAbout, SearchFlights, BookFlight, BookHotel)
var result = await harness.RunWorkflowTestAsync(adapter, testCase, options);
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights")
    .And()
    .HaveCalledTool("BookHotel");
```

### Sample 11: Datasets & Export
Batch evaluation with YAML datasets and multi-format export.

```csharp
var dataset = await DatasetLoaderFactory.LoadAsync("tests.yaml");
var results = await harness.RunBatchAsync(adapter, dataset);
await new JUnitXmlExporter().ExportAsync(results, "results.xml");
await new MarkdownExporter().ExportAsync(results, "results.md");
```

### Sample 12: Policy & Safety Evaluation
Enterprise guardrails with NeverCallTool, PII detection, MustConfirmBefore.

```csharp
result.ToolUsage!.Should()
    .NeverCallTool("DeleteAccount")
    .And()
    .NeverPassArgumentMatching("ssn", @"\d{3}-\d{2}-\d{4}")
    .And()
    .MustConfirmBefore("TransferFunds");
```

### Sample 13: Trace Record & Replay
Record and replay agent executions for deterministic CI testing.

```csharp
// RECORD: Single agent, multi-turn chat, workflow, and streaming traces
var recorder = new TraceRecordingAgent(realAgent);
await recorder.ExecuteAsync("What tools do you have?");
TraceSerializer.Save(recorder.GetTrace(), "trace.json");

// REPLAY: Deterministic playback without LLM calls
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.ReplayNextAsync(); // Identical response
```

### Sample 14: Stochastic Evaluation
Run the same test multiple times to measure reliability and consistency.

```csharp
var runner = new StochasticRunner(harness, statisticsCalculator: null, options);
var result = await runner.RunStochasticTestAsync(agent, testCase,
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8));
result.PrintTable("Metrics"); // Min/max/mean statistics
```

### Sample 15: Model Comparison
Compare and rank multiple models on quality, speed, cost, and reliability.

```csharp
var factories = new IAgentFactory[] { gpt4oFactory, gpt4oMiniFactory, gpt41Factory };
foreach (var factory in factories)
{
    var agent = factory.CreateAgent();
    var result = await runner.RunStochasticTestAsync(agent, testCase, options);
    modelResults.Add((factory.ModelName, result));
}
modelResults.PrintComparisonTable();
```

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
```

### Sample 20: Red Team Basic
Quick security scan with assertions and detailed reporting.

```csharp
var result = await agent.RedTeamAsync(new ScanOptions { Intensity = Intensity.Quick });
result.Print(VerbosityLevel.Detailed);

result.Should()
    .HavePassed()
    .And().HaveMinimumScore(80)
    .And().HaveASRBelow(0.05);
```

### Sample 21: Red Team Advanced
Custom attack pipeline, OWASP compliance, export, and baseline comparison.

```csharp
var pipeline = AttackPipeline.Create()
    .WithAttack(Attack.PromptInjection)
    .WithAttack(Attack.Jailbreak)
    .WithIntensity(Intensity.Moderate);

var result = await pipeline.ScanAsync(agent);
await new JUnitReportExporter().ExportToFileAsync(result, "report.xml");
result.Should().HaveNoCompromisesFor("LLM01"); // OWASP check
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

1. Run all samples to understand the API: `dotnet run --project samples/AgentEval.Samples`
2. Copy patterns into your own test project
3. Check [docs/](../../docs/) for full API reference and guides
4. See [AgentEval.Tests](../../tests/AgentEval.Tests/) for more examples

## 💡 Tips

- Use `TrackTools = true` to capture tool calls
- Use `TrackPerformance = true` to capture metrics
- Use streaming (`RunTestStreamingAsync`) for TTFT measurement
- Use `FakeChatClient` for testing metrics without real LLM calls

---

**Happy Evaluating!** 🎉
