# AgentEval

<p align="center">
  <img src="images/AgentEval_bounded.png" alt="AgentEval Logo" width="400" />
</p>

<p align="center">
  <strong>Your AI agent works great... until it doesn't.<br/>AgentEval catches the failures before your users do.</strong>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/AgentEval">
    <img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet Version" />
  </a>
  <img src="https://img.shields.io/badge/license-MIT-blue" alt="License" />
</p>

---

## The .NET Evaluation Toolkit for AI Agents

AgentEval is **the comprehensive .NET toolkit for AI agent evaluation**—tool usage validation, RAG quality metrics, stochastic evaluation, model comparison, and memory benchmarks—built for **Microsoft Agent Framework (MAF)** and **Microsoft.Extensions.AI**. What RAGAS and DeepEval do for Python, AgentEval does for .NET.

> **For years, agentic developers have imagined writing evaluations like this. Today, they can.**

---

## The Code You've Been Dreaming Of

### 🥇 Assert on Tool Chains Like Requirements

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("AuthenticateUser", because: "security first")
        .BeforeTool("FetchUserData")
        .WithArgument("method", "OAuth2")
    .And()
    .HaveCalledTool("SendNotification")
        .AtLeastTimes(1)
    .And()
    .HaveNoErrors();
```

**No more regex parsing logs. No more "did it call that function?"**

### 🥈 Stochastic Evaluation: Because LLMs Aren't Deterministic

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.85));

result.Statistics.SuccessRate.Should().BeGreaterThan(0.85);
result.Statistics.StandardDeviation.Should().BeLessThan(10);
```

**Run the same evaluation 10 times. Know your actual success rate, not your lucky-run rate.**

### 🥉 Workflow Evaluation: Multi-Agent Flows as Assertions

```csharp
var testCase = new WorkflowTestCase
{
    Name              = "TripPlanner — Tokyo & Beijing",
    Input             = "Plan a 7-day trip to Tokyo and Beijing — flights and hotels",
    ExpectedExecutors = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
    StrictExecutorOrder = true,
    ExpectedTools     = ["SearchFlights", "BookHotel"],
};

var result = await new WorkflowEvaluationHarness()
    .RunWorkflowTestAsync(workflowAdapter, testCase);

result.ExecutionResult!.Should()
    .HaveSucceeded()
    .HaveExecutedInOrder("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
    .HaveTraversedEdge("TripPlanner", "FlightReservation")
    .HaveAnyExecutorCalledTool("SearchFlights")
    .HaveCompletedWithin(TimeSpan.FromMinutes(2))
    .HaveNoToolErrors();
```

**4 agents, real MAF `WorkflowBuilder`, one test.** Executor order, edge traversal, and per-graph tool calls — all observable, all assertable.

### Performance SLAs as Executable Evaluations

```csharp
result.Performance!.Should()
    .HaveFirstTokenUnder(TimeSpan.FromMilliseconds(500),
        because: "streaming responsiveness matters")
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveEstimatedCostUnder(0.05m,
        because: "stay within budget");
```

**Know before production if your agent is too slow or too expensive.**

### Compare Models, Get a Winner

```csharp
var result = await comparer.CompareModelsAsync(
    factories: new[] { gpt4o, gpt4oMini, claude },
    testCases: testSuite,
    metrics: new[] { new ToolSuccessMetric(), new RelevanceMetric(eval) },
    options: new ComparisonOptions(RunsPerModel: 5));

Console.WriteLine(result.ToMarkdown());
```

**Output:**
```markdown
| Rank | Model         | Tool Accuracy | Relevance | Cost/1K Req |
|------|---------------|---------------|-----------|-------------|
| 🥇   | GPT-4o        | 94.2%         | 91.5      | $0.0150     |
| 🥈   | GPT-4o Mini   | 87.5%         | 84.2      | $0.0003     |

**Recommendation:** GPT-4o - Highest accuracy
**Best Value:** GPT-4o Mini - 87.5% accuracy at 50x lower cost
```

### Record Once, Replay Forever (No API Costs)

```csharp
// RECORD once (live API call)
var recorder = new TraceRecordingAgent(realAgent);
await recorder.ExecuteAsync("Book a flight to Paris");
TraceSerializer.Save(recorder.GetTrace(), "booking-trace.json");

// REPLAY forever (no API call, instant, free)
var replayer = new TraceReplayingAgent(trace);
var response = await replayer.ReplayNextAsync();  // Identical every time
```

**Save API costs. Run evaluations in CI. Get consistent results.**

---

## Red Team Security Evaluation

**Is your AI agent secure?** AgentEval's Red Team module evaluates against **192 attack probes** covering **6 OWASP LLM Top 10 vulnerabilities** (60% coverage) with **MITRE ATLAS** technique mapping.

```csharp
// One-line security scan
var result = await agent.QuickRedTeamScanAsync();

Console.WriteLine($"Security Score: {result.OverallScore}%");
Console.WriteLine($"Verdict: {result.Verdict}");

// Use with fluent assertions
result.Should()
    .HavePassed()
    .And()
    .HaveMinimumScore(80);
```

**Attack types included:** Prompt Injection, Jailbreaks, PII Leakage, System Prompt Extraction, Indirect Injection, Excessive Agency, Insecure Output Handling, API Abuse, Encoding Evasion.

```csharp
// Advanced: Full pipeline control
var result = await AttackPipeline
    .Create()
    .WithAttack(Attack.PromptInjection)
    .WithAttack(Attack.Jailbreak)
    .WithAttack(Attack.PIILeakage)
    .WithIntensity(Intensity.Comprehensive)
    .ScanAsync(agent);

// Export compliance reports
await result.ExportAsync("security-report.pdf", ExportFormat.Pdf);
```

[Red Team Evaluation →](redteam.md)

---

## Memory Evaluation

**Does your agent actually remember?** AgentEval.Memory is the comprehensive .NET toolkit for evaluating retention, recall depth, temporal reasoning, fact updates, cross-session persistence, and resistance to noise.

```csharp
// One-line benchmark with grade
var runner = MemoryBenchmarkRunner.Create(chatClient);
var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard);

Console.WriteLine($"Memory: {result.OverallScore:F1}% ({result.Grade})");

// Generate an interactive HTML pentagon report
await result.ExportHtmlReportAsync("memory-report.html");
```

**What ships:**
- **5 memory metrics** — retention, reach-back, temporal, noise resilience, reducer fidelity
- **5 benchmark presets** — Quick / Standard / Full / Diagnostic / Overflow (up to 192K tokens)
- **HTML pentagon reports** — multi-model overlay, baseline diffs, drill-down explanations
- **LongMemEval (ICLR 2025)** — fully re-implemented in .NET, paper-comparable scoring
- **MAF-native** — works with `AIContextProvider`, `ChatHistoryProvider`, `CompactionStrategy`

> **Honest note:** use the native `Standard` benchmark primarily as a regression gate for changes in your own agent, and use **LongMemEval** when you need broader cross-platform comparability.

[Memory Evaluation →](memory-evaluation.md)

---

## Why AgentEval?

| Challenge | How AgentEval Solves It |
|-----------|------------------------|
| "What tools did my agent call?" | **Full tool timeline** with arguments, results, timing |
| "Evaluations fail randomly!" | **stochastic evaluation** - assert on pass *rate*, not single run |
| "Which model should I use?" | **Model comparison** with cost/quality recommendations |
| "Is my agent compliant?" | **Behavioral policies** - guardrails as code |
| "Is my agent secure?" | **Red team evaluation** - 192 OWASP LLM 2025 security probes |
| "Is content safe/unbiased?" | **ResponsibleAI metrics** - toxicity, bias, misinformation |
| "Is my RAG hallucinating?" | **Faithfulness metrics** - grounding verification |
| "How do I debug CI failures?" | **Trace replay** - capture and reproduce executions |

---

## Feature Highlights

<div class="grid cards" markdown>

-   **🎯 Fluent Assertions**
    
    Tool chains, performance, responses - all with `Should()` syntax

-   **⚡ Performance Metrics**
    
    TTFT, latency, tokens, cost estimation with 8+ model pricing

-   **🔬 stochastic evaluation**
    
    Run N times, get statistics, assert on pass rates

-   **🤖 Model Comparison**
    
    Compare models side-by-side with recommendations

-   **🎬 Trace Record/Replay**
    
    Deterministic evaluations without API calls

-   **🛡️ Behavioral Policies**
    
    NeverCallTool, MustConfirmBefore, PII detection

-   **🔴 Red Team Security**
    
    192 probes, 9 attack types, 60% OWASP LLM 2025 coverage, MITRE ATLAS mapping

-   **🛡️ Responsible AI**
    
    Toxicity detection, bias measurement, misinformation risk

-   **🧠 Memory Evaluation**
    
    Retention, reach-back, temporal, cross-session, LongMemEval, HTML pentagon reports

-   **🖥️ CLI Tool**
    
    `agenteval eval` - evaluate any AI agent from the command line

-   **🔌 Cross-Framework**
    
    Universal `IChatClient.AsEvaluableAgent()` one-liner + Semantic Kernel bridge

-   **📦 Dependency Injection**
    
    `services.AddAgentEval()` - interface-first architecture

-   **📊 RAG Metrics**
    
    Faithfulness, Relevance, Context Precision/Recall

-   **🔄 Multi-Turn Evaluation**
    
    Full conversation flow evaluation

</div>

---

## Who Is AgentEval For?

### 🏢 .NET Teams Building AI Agents

If you're building production AI agents in .NET and need to verify tool usage, enforce SLAs, handle non-determinism, or compare models—AgentEval is for you.

### 🚀 Microsoft Agent Framework (MAF) Developers

Native integration with MAF concepts: `AIAgent`, `IChatClient`, automatic tool call tracking, and performance metrics with token usage and cost estimation.

### 📊 ML Engineers Evaluating LLM Quality

Rigorous evaluation capabilities: RAG metrics (Faithfulness, Relevance, Context Precision), embedding-based similarity, and calibrated judge patterns for consistent evaluation.

---

## Samples

**Detailed examples** included—from Hello World to advanced Multi-Agent Workflows, Red Team Security, Memory Evaluation, and Cross-Framework evaluation.

```bash
dotnet run --project samples/AgentEval.Samples
```

[View Examples →](https://github.com/AgentEvalHQ/AgentEval/tree/main/samples/AgentEval.Samples)

---

## Documentation

| Getting Started | Features | Advanced |
|-----------------|----------|----------|
| [Installation](installation.md) | [Assertions](assertions.md) | [stochastic evaluation](stochastic-evaluation.md) |
| [Quick Start](getting-started.md) | [Red Team Security](redteam.md) | [Model Comparison](model-comparison.md) |
|  | [Responsible AI](ResponsibleAI.md) | [Trace Record/Replay](tracing.md) |
| [Walkthrough](walkthrough.md) | [Memory Evaluation](memory-evaluation.md) | [Architecture](architecture.md) |
|  | [Metrics Reference](metrics-reference.md) | [Upgrading MAF](maf-1.3.0-migration-guide.md) |
|  | [Benchmarks](benchmarks.md) |  |
|  | [Workflows](workflows.md) |  |

---

## The .NET Advantage

| Feature | AgentEval | Python Alternatives |
|---------|-----------|---------------------|
| **Language** | Native C#/.NET | Python only |
| **Type Safety** | Compile-time errors | Runtime exceptions |
| **IDE Support** | Full IntelliSense | Variable |
| **MAF Integration** | First-class | None |
| **Fluent Assertions** | `Should().HaveCalledTool()` | N/A |
| **Trace Replay** | Built-in | Manual |

---

## Quality Assurance

AgentEval maintains a **comprehensive evaluation suite** running across **multiple target frameworks**, ensuring reliability.

[![codecov](https://codecov.io/gh/AgentEvalHQ/AgentEval/graph/badge.svg?token=Y28TAK3LNH)](https://codecov.io/gh/AgentEvalHQ/AgentEval)

---

## Community

- **GitHub:** [github.com/AgentEvalHQ/AgentEval](https://github.com/AgentEvalHQ/AgentEval)
- **NuGet:** [nuget.org/packages/AgentEval](https://www.nuget.org/packages/AgentEval)
- **Issues:** [Report bugs or request features](https://github.com/AgentEvalHQ/AgentEval/issues)
- **Discussions:** [Ask questions](https://github.com/AgentEvalHQ/AgentEval/discussions)
- **Commercial & Enterprise (planned):** [Learn more](commercial.md)

---

## Forever Open Source

AgentEval is **MIT licensed** and will remain open source forever.

- ✅ **No license changes** — MIT today, MIT forever
- ✅ **No bait-and-switch** — core stays MIT and fully usable
- ✅ **Community first** — built with the .NET AI community
- ℹ️ **Optional add-ons may exist separately** (if/when built)

---

<p align="center">
  <strong>Stop guessing if your AI agent works. Start proving it.</strong>
</p>

<p align="center">
  <a href="getting-started.md"><strong>Get Started →</strong></a>
</p>
