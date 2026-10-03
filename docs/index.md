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
    .And()
    .HaveNoErrors();
```

**No more regex parsing logs. No more "did it call that function?"**

### 🥈 Stochastic Evaluation: Because LLMs Aren't Deterministic

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.85));

result.Should()
    .HavePassRateAtLeast(0.85)
    .HaveStandardDeviationAtMost(10);
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
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500),
        because: "streaming responsiveness matters")
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveEstimatedCostUnder(0.05m,
        because: "stay within budget");
```

**Know before production if your agent is too slow or too expensive.** A metric that was not captured cannot fail its check: time to first token is recorded only on streaming runs, and cost only when `EvaluationOptions.ModelName` names a model in the price table. Inside an `AgentEvalScope` such a check is recorded as inconclusive; outside one it is skipped.

### Compare Models, Get a Winner

```csharp
var stochasticRunner = new StochasticRunner(harness);
var comparer = new ModelComparer(stochasticRunner);

// CreateAgent(deployment) is your code: it returns an IEvaluableAgent for that model
var results = await comparer.CompareModelsAsync(
    factories: new IAgentFactory[]
    {
        new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
        new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini"))
    },
    testCases: testSuite,
    options: new ModelComparisonOptions(RunsPerModel: 5));

Console.WriteLine(results.ToMarkdown());
```

Every model runs each test case five times. The Markdown report counts each model's wins, averages its composite, quality, speed, cost and reliability scores, and ranks the models on every test case. The scores rank the models against each other (best 100, worst 0), so check the raw pass rates and latencies in each result's `ModelResults` too: [how the scores are computed](model-comparison.md#how-the-scores-are-computed).

### Record Once, Replay Forever (No API Costs)

```csharp
// RECORD once (live API call)
await using var recorder = new TraceRecordingAgent(realAgent, "booking");
await recorder.InvokeAsync("Book a flight to Paris");
await recorder.SaveAsync("booking.trace.json");

// REPLAY forever (no API call, instant, free)
var replayer = await TraceReplayingAgent.FromFileAsync("booking.trace.json");
var response = await replayer.InvokeAsync("Book a flight to Paris");  // Identical every time
```

**Save API costs. Run evaluations in CI. Get consistent results.**

---

## Red Team Security Evaluation

**Is your AI agent secure?** AgentEval's Red Team module evaluates against **264 attack probes** covering **all 10 OWASP LLM Top 10 vulnerabilities** with **MITRE ATLAS** technique mapping.

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

**Attack types included (14):** Prompt Injection, Jailbreaks, PII Leakage, System Prompt Extraction, Indirect Injection, Excessive Agency, Insecure Output Handling, Inference API Abuse, Encoding Evasion, Supply Chain, Data & Model Poisoning, Vector & Embedding (RAG), Misinformation, Skill-Description Injection — covering all 10 OWASP LLM Top 10 categories. Plus opt-in multi-turn attacks (Crescendo, PAIR, TAP, ToolEscalation).

```csharp
// Advanced: Full pipeline control
var result = await AttackPipeline
    .Create()
    .WithAttack(Attack.PromptInjection)
    .WithAttack(Attack.Jailbreak)
    .WithAttack(Attack.PIILeakage)
    .WithIntensity(Intensity.Comprehensive)
    .ScanAsync(agent);

// Export an executive PDF report (AgentEval.RedTeam.Reporting.Pdf); JSON, Markdown,
// JUnit and SARIF exporters live in AgentEval.RedTeam.Reporting
await new PdfReportGenerator().ExportToFileAsync(result, "security-report.pdf");
```

[Red Team Evaluation →](redteam.md)

---

## Memory Evaluation

**Does your agent actually remember?** AgentEval.Memory is the comprehensive .NET toolkit for evaluating retention, recall depth, temporal reasoning, fact updates, cross-session persistence, and resistance to noise.

```csharp
// One-line benchmark with grade
var runner = MemoryBenchmarkRunner.Create(chatClient);
var agent  = chatClient.AsEvaluableAgent(name: "MemoryAgent", includeHistory: true);

var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard);
Console.WriteLine($"Memory: {result.OverallScore:F1}% ({result.Grade})");

// Save a baseline. SaveAsync also places the interactive HTML pentagon report
// (report.html) in the same folder; it reads every baseline saved there.
var store = new JsonFileBaselineStore();
await store.SaveAsync(result.ToBaseline("GPT-4o", new AgentBenchmarkConfig { AgentName = "MemoryAgent" }));
Console.WriteLine($"Report folder: {store.GetReportDirectory("MemoryAgent")}");
```

**What ships:**
- **5 memory metrics** — retention, reach-back, temporal, noise resilience, reducer fidelity
- **5 benchmark presets** — Quick / Standard / Full / Diagnostic / Overflow (up to 192K tokens)
- **HTML pentagon reports** — saved baselines overlaid, per-category deltas between any two, a score timeline (a baseline stores scores, not transcripts, so there is no per-scenario drill-down)
- **LongMemEval (ICLR 2025)** — re-implemented in .NET, with the paper's published scores (GPT-4o: 57.7%, S mode) shipped as a reference
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
| "Is my agent secure?" | **Red team evaluation** - 264 OWASP LLM 2025 security probes (all 10 categories) |
| "Can I stop a bad action before it happens?" | **Gatekeeper** - fail-closed runtime gates block a bad tool call or a leaking response before it happens |
| "Is my agent's use of MAF Agent Skills safe and efficient?" | **Agent Skills evaluation** - disclosure assertions, efficiency metric, compliance scanner, injection red-team, governance gates |
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

-   **⛔ Gatekeeper**
    
    Runtime fail-closed enforcement — deterministic gates + calibrated judges block a bad tool call or a leaking response *before* it happens

-   **🔴 Red Team Security**
    
    264 probes, 14 attack types, full OWASP LLM Top 10 2025 coverage, MITRE ATLAS mapping

-   **🧩 Agent Skills**
    
    Evaluate & govern MAF Agent Skills — disclosure assertions, efficiency metric, `SKILL.md` compliance scanning, skill-injection red-team, `run_skill_script` governance gates, Skill Security Index

-   **🛡️ Responsible AI**
    
    Toxicity detection, bias measurement, misinformation risk

-   **🧠 Memory Evaluation**
    
    Retention, reach-back, temporal, cross-session, LongMemEval, HTML pentagon reports

-   **🖥️ CLI Tool**
    
    `agenteval init-workspace / doctor / bench / redteam / gatekeeper / mc serve` — workspace, benchmarks, red team scans, runtime gates, Mission Control

-   **🔌 Cross-Framework**
    
    Universal `IChatClient.AsEvaluableAgent()` one-liner + Semantic Kernel bridge

-   **🎯 Copilot Studio**

    Red-team or evaluate a live Microsoft Copilot Studio agent — via the CLI (`--sut copilot-studio`) or directly in code (`AgentEval.MAF.CopilotStudio`), no CLI required

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

## Mission Control + Compliance Benchmarks

Mission Control is the read-only web portal over your `.agenteval/` workspace —
GraphQL, REST, and SPA on one port. The benchmark families: the compliance +
agentic benchmarks (**Agentic** 60 evaluators / 12 presets, **EU AI Act** 6
pillars / 15 controls, **GDPR** 6 pillars / 29 articles + 3 domain packs), the
security families (**OWASP** LLM Top 10 v2.0, **MITRE ATLAS**, **NIST AI RMF**),
**Performance**, the memory families (**LongMemEval**, **TypedMemEval**,
**Memory**), and two pure-code trace reconciliation families (**Trace
Fidelity**, **Workflow Trace Fidelity**). All benchmarks produce audit-chained
evidence under `.agenteval/`. (For version-specific counts, run
`agenteval bench --list` against the installed tool.)

```bash
agenteval init-workspace             # one-time workspace bootstrap
agenteval bench agentic   --preset agentic-execution --subject MyAgent
agenteval bench eu-ai-act --preset standard          --subject MyAgent
agenteval bench gdpr      --preset standard          --subject MyAgent
agenteval bench owasp     --preset top10             --subject MyAgent
agenteval mc serve                         # browse runs + evidence at http://localhost:5000
```

[Mission Control →](missioncontrol/getting-started.md) ·
[Agentic →](benchmarks/agentic/getting-started.md) ·
[EU AI Act →](benchmarks/eu-ai-act/getting-started.md) ·
[GDPR →](benchmarks/gdpr/getting-started.md) ·
[OWASP →](benchmarks/owasp/getting-started.md) ·
[MITRE →](benchmarks/mitre/getting-started.md) ·
[Performance →](benchmarks/perf/getting-started.md) ·
[LongMemEval →](benchmarks/longmemeval/getting-started.md) ·
[TypedMemEval →](benchmarks/typedmemeval/getting-started.md) ·
[Memory →](benchmarks/memory/getting-started.md)

---

## Documentation

| Getting Started | Features | Advanced |
|-----------------|----------|----------|
| [Installation](installation.md) | [Assertions](assertions.md) | [stochastic evaluation](stochastic-evaluation.md) |
| [Quick Start](getting-started.md) | [Red Team Security](redteam.md) | [Model Comparison](model-comparison.md) |
|  | [Responsible AI](responsible-ai.md) | [Trace Record/Replay](tracing.md) |
| [Walkthrough](walkthrough.md) | [Memory Evaluation](memory-evaluation.md) | [Architecture](architecture.md) |
|  | [Metrics Reference](metrics-reference.md) | [MAF 1.10.0 Upgrade Notes](maf-1.10.0-upgrade-plan.md) |
|  | [Benchmarks](benchmarks.md) | [Composite Evaluations](composite-evals.md) |
|  | [Workflows](workflows.md) | [The .agenteval Workspace](agenteval-workspace.md) |
|  | [GDPR Benchmark](benchmarks/gdpr/getting-started.md) | [Mission Control](missioncontrol/getting-started.md) |
|  | [EU AI Act Benchmark](benchmarks/eu-ai-act/getting-started.md) | [Gatekeeper](gatekeeper/introduction.md) |
|  | [Agentic Benchmark](benchmarks/agentic/getting-started.md) | [Copilot Studio Target](copilot-studio.md) |
|  | [OWASP / MITRE Benchmarks](benchmarks/owasp/getting-started.md) |  |
|  | [Performance / Memory / LongMemEval](benchmarks/perf/getting-started.md) |  |
|  | [MAF Agent Skills Evaluation](agent-skills.md) |  |

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
