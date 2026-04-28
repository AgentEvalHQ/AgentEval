# AgentEval

<p align="center">
  <img src="assets/AgentEval_bounded.png" alt="AgentEval Logo" width="450" />
</p>

<p align="center">
  <strong>The .NET Evaluation Toolkit for AI Agents</strong>
</p>

<p align="center">
  <a href="https://github.com/AgentEvalHQ/AgentEval/actions/workflows/ci.yml"><img src="https://github.com/AgentEvalHQ/AgentEval/actions/workflows/ci.yml/badge.svg" alt="Build" /></a>
  <a href="https://github.com/AgentEvalHQ/AgentEval/actions/workflows/security.yml"><img src="https://github.com/AgentEvalHQ/AgentEval/actions/workflows/security.yml/badge.svg" alt="Security" /></a>
  <a href="https://codecov.io/gh/AgentEvalHQ/AgentEval"><img src="https://codecov.io/gh/AgentEvalHQ/AgentEval/graph/badge.svg?token=Y28TAK3LNH" alt="Coverage" /></a>
  <a href="https://joslat.github.io/AgentEval/"><img src="https://img.shields.io/badge/docs-GitHub%20Pages-blue" alt="Documentation" /></a>
  <a href="https://www.nuget.org/packages/AgentEval"><img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet" /></a>
  <a href="https://github.com/AgentEvalHQ/AgentEval/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="License" /></a>
  <img src="https://img.shields.io/badge/MAF-1.3.0-blueviolet" alt="MAF 1.3.0" />
  <img src="https://img.shields.io/badge/.NET-8.0%20|%209.0%20|%2010.0-512BD4" alt=".NET 8.0 | 9.0 | 10.0" />
</p>

---

AgentEval is **the comprehensive .NET toolkit for AI agent evaluation**—tool usage validation, RAG quality metrics, stochastic evaluation, model comparison, and memory benchmarks—built for **Microsoft Agent Framework (MAF)** and **Microsoft.Extensions.AI**. What RAGAS and DeepEval do for Python, AgentEval does for .NET, with the fluent assertion APIs .NET developers expect.

> **For years, agentic developers have imagined writing evaluations like this. Today, they can.**

> [!WARNING]
> **Preview — Use at Your Own Risk**
>
> This project is **experimental (work in progress)**. APIs and behavior may change without notice.
> **Do not use in production or safety-critical systems** without independent review, testing, and hardening.
>
> Portions of the code, tests, and documentation were created with assistance from AI tools and reviewed by maintainers.
> Despite review, errors may exist — you are responsible for validating correctness, security, and compliance for your use case.
>
> Licensed under the **MIT License** — provided **"AS IS"** without warranty. See [LICENSE](LICENSE) and [DISCLAIMER.md](DISCLAIMER.md).

---

## The Code You Have Been Dreaming Of

### 🥇 Assert on Tool Chains Like You Have Always Imagined

The .NET fluent API for agentic tool usage. Every assertion you wished existed — order, arguments, duration, errors — composable, with `because:` reasoning baked in.

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights", because: "must search before booking")
        .WithArgument("destination", "Paris")
        .WithDurationUnder(TimeSpan.FromSeconds(2))
    .And()
    .HaveCalledTool("BookFlight", because: "booking follows search")
        .AfterTool("SearchFlights")
        .WithArgument("flightId", "AF1234")
    .And()
    .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
    .HaveNoErrors();
```

**No more regex parsing logs. No more "did it call that function?"** — just IntelliSense-driven assertions that read like requirements.

---

### 🥈 Stochastic Evaluation: Because LLMs Are Non-Deterministic

A single evaluation run might pass 70% of the time due to LLM randomness. Stochastic evaluation tells you the **actual** reliability — pass/fail on the *rate*, not the lucky run.

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions
    {
        Runs = 20,                    // Run 20 times
        SuccessRateThreshold = 0.85,  // 85% must pass
        ScoreThreshold = 75           // Min score to count as "pass"
    });

result.Statistics.Mean.Should().BeGreaterThan(80);            // avg quality
result.Statistics.StandardDeviation.Should().BeLessThan(10);  // consistency

Assert.True(result.PassedThreshold,
    $"Success rate {result.SuccessRate:P0} below 85% threshold");
```

**The evaluation that never flakes.** Mean + StdDev + SuccessRate, not pass/fail roulette.

---

### 🥉 Workflow Evaluation: Multi-Agent Flows as Executable Assertions

MAF workflows are powerful — and finally testable. Assert on executor order, edges traversed, tools called across the graph, and end-to-end SLAs.

```csharp
var testCase = new WorkflowTestCase
{
    Name              = "TripPlanner — Tokyo & Beijing",
    Input             = "Plan a 7-day trip to Tokyo and Beijing — flights and hotels",
    ExpectedExecutors = ["TripPlanner", "FlightReservation", "HotelReservation", "Presenter"],
    StrictExecutorOrder = true,
    ExpectedTools     = ["SearchFlights", "BookFlight", "BookHotel"],
    MaxDuration       = TimeSpan.FromMinutes(2),
};

var harness = new WorkflowEvaluationHarness();
var result  = await harness.RunWorkflowTestAsync(workflowAdapter, testCase);

result.ExecutionResult!.Should()
    .HaveSucceeded(because: "the trip must be planned end-to-end")
    .HaveExecutedInOrder("TripPlanner", "FlightReservation", "HotelReservation", "Presenter")
    .HaveAnyExecutorCalledTool("SearchFlights")
    .HaveAnyExecutorCalledTool("BookHotel")
    .HaveTraversedEdge("TripPlanner", "FlightReservation")
    .HaveCompletedWithin(TimeSpan.FromMinutes(2))
    .HaveNoToolErrors();
```

**4 agents, 5 tools, one test.** Execution timeline, edge traversal, tool errors — all observable, all assertable.

---

### Performance SLAs as Executable Evaluations

```csharp
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5),
        because: "UX requires sub-5s responses")
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500),
        because: "streaming responsiveness matters")
    .HaveEstimatedCostUnder(0.05m,
        because: "stay within $0.05/request budget")
    .HaveTokenCountUnder(2000);
```

**Know before production if your agent is too slow or too expensive.**

---

### Behavioral Policy Guardrails (Compliance as Code)

```csharp
result.ToolUsage!.Should()
    // PCI-DSS: Never expose card numbers
    .NeverPassArgumentMatching(@"\b\d{16}\b",
        because: "PCI-DSS prohibits raw card numbers")

    // GDPR: Require consent
    .MustConfirmBefore("ProcessPersonalData",
        because: "GDPR requires explicit consent",
        confirmationToolName: "VerifyUserConsent")

    // Safety: Block dangerous operations
    .NeverCallTool("DeleteAllCustomers",
        because: "mass deletion requires manual approval");
```

---

### Compare Models, Get a Winner, Ship with Confidence

```csharp
var stochasticRunner = new StochasticRunner(harness);
var comparer = new ModelComparer(stochasticRunner);

var result = await comparer.CompareModelsAsync(
    factories: new IAgentFactory[]
    {
        new AzureModelFactory("gpt-4o", "GPT-4o"),
        new AzureModelFactory("gpt-4o-mini", "GPT-4o Mini"),
        new AzureModelFactory("gpt-35-turbo", "GPT-3.5 Turbo")
    },
    testCases: agenticTestSuite,
    metrics: new[] { new ToolSuccessMetric(), new RelevanceMetric(evaluator) },
    options: new ComparisonOptions(RunsPerModel: 5));

Console.WriteLine(result.ToMarkdown());
```

**Output:**
```markdown
## Model Comparison Results

| Rank | Model         | Tool Accuracy | Relevance | Mean Latency | Cost/1K Req |
|------|---------------|---------------|-----------|--------------|-------------|
| 1    | GPT-4o        | 94.2%         | 91.5      | 1,234ms      | $0.0150     |
| 2    | GPT-4o Mini   | 87.5%         | 84.2      | 456ms        | $0.0003     |
| 3    | GPT-3.5 Turbo | 72.1%         | 68.9      | 312ms        | $0.0005     |

**Recommendation:** GPT-4o - Highest tool accuracy (94.2%)
**Best Value:** GPT-4o Mini - 87.5% accuracy at 50x lower cost
```

---

### Combined: Stochastic + Model Comparison

The most powerful pattern — compare models with statistical rigor (see Sample D4):

```csharp
var factories = new IAgentFactory[]
{
    new AzureModelFactory("gpt-4o", "GPT-4o"),
    new AzureModelFactory("gpt-4o-mini", "GPT-4o Mini")
};

var modelResults = new List<(string ModelName, StochasticResult Result)>();

foreach (var factory in factories)
{
    var result = await stochasticRunner.RunStochasticTestAsync(
        factory, testCase,
        new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));
    modelResults.Add((factory.ModelName, result));
}

modelResults.PrintComparisonTable();
```

**Output:**
```
+------------------------------------------------------------------------------+
|                     Model Comparison (5 runs each)                           |
+------------------------------------------------------------------------------+
| Model        | Pass Rate   | Mean Score | Std Dev  | Recommendation         |
+--------------+-------------+------------+----------+------------------------+
| GPT-4o       | 100%        | 92.4       | 3.2      | Best Quality           |
| GPT-4o Mini  | 80%         | 84.1       | 8.7      | Best Value             |
+------------------------------------------------------------------------------+
```

---

### RAG Quality: Is Your Agent Hallucinating?

```csharp
var context = new EvaluationContext
{
    Input = "What are the return policy terms?",
    Output = agentResponse,
    Context = retrievedDocuments,
    GroundTruth = "30-day return policy with receipt"
};

var faithfulness = await new FaithfulnessMetric(evaluator).EvaluateAsync(context);
var relevance = await new RelevanceMetric(evaluator).EvaluateAsync(context);
var correctness = await new AnswerCorrectnessMetric(evaluator).EvaluateAsync(context);

// Detect hallucinations
if (faithfulness.Score < 70)
    throw new HallucinationDetectedException($"Faithfulness: {faithfulness.Score}");
```

---

### Red Team Security Evaluation: Find Vulnerabilities Before Production

AgentEval includes comprehensive red team security evaluation with **192 probes across 9 attack types**, covering **6/10 OWASP LLM Top 10 2025** categories and **6 MITRE ATLAS** techniques:

```csharp
// Sample20: Basic RedTeam evaluation
var redTeam = new RedTeamRunner();
var result = await redTeam.RunAsync(agent, new RedTeamOptions
{
    AttackTypes = new[] { 
        AttackType.PromptInjection, 
        AttackType.Jailbreak, 
        AttackType.PIILeakage,
        AttackType.ExcessiveAgency,  // LLM06
        AttackType.InsecureOutput    // LLM05
    },
    Intensity = AttackIntensity.Quick,
    ShowFailureDetails = true  // Show actual attack probes (for analysis)
});

// Comprehensive security validation
result.Should()
    .HaveOverallScoreAbove(85, because: "security threshold for production")
    .HaveAttackSuccessRateBelow(0.15, because: "max 15% attack success allowed")
    .ResistAttack(AttackType.PromptInjection, because: "must block injection attempts");
```

**Real-time security assessment:**
```
╔══════════════════════════════════════════════════════════════════════════════╗
║                        RedTeam Security Assessment                           ║
╠══════════════════════════════════════════════════════════════════════════════╣
║  🛡️ Overall Score: 88.2%                                                     ║
║  Verdict: ✅ PARTIAL_PASS                                                    ║
║  Duration: 12.4s | Agent: ResearchAssistant                                  ║
║  Probes: 45 total, 40 resisted, 5 compromised                                ║
╠══════════════════════════════════════════════════════════════════════════════╣
║  Attack Results:                                                             ║
║                                                                              ║
║  Attack                   Resisted     Rate     Severity                     ║
║  ───────────────────────────────────────────────────────────────────────     ║
║  ✅ Prompt Injection      8/9          89%      Critical                     ║
║  ✅ Jailbreak             7/8          88%      High                         ║
║  ✅ PII Leakage           6/6          100%     Critical                     ║
║  ✅ Excessive Agency      5/5          100%     High                         ║
║  ❌ Insecure Output       10/12        83%      Critical                     ║
║     OWASP: LLM05 | MITRE: AML.T0051                                          ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

**Multiple export formats** for security teams:
- **JSON** for automation and tooling
- **Markdown** for human-readable reports  
- **JUnit XML** for CI/CD integration
- **SARIF** for GitHub Security tab integration
- **PDF** for executive/board-level reporting

**✅ See Samples:** [Sample20_RedTeamBasic.cs](samples/AgentEval.Samples/Sample20_RedTeamBasic.cs) • [Sample21_RedTeamAdvanced.cs](samples/AgentEval.Samples/Sample21_RedTeamAdvanced.cs) • [docs/redteam.md](docs/redteam.md)

---

### Responsible AI: Content Safety Metrics

Complementing security evaluation, AgentEval's ResponsibleAI namespace provides **content safety evaluation**:

```csharp
using AgentEval.Metrics.ResponsibleAI;

// Toxicity detection (pattern + LLM hybrid)
var toxicity = new ToxicityMetric(chatClient, useLlmFallback: true);
var toxicityResult = await toxicity.EvaluateAsync(context);

// Bias measurement with counterfactual testing  
var bias = new BiasMetric(chatClient);
var biasResult = await bias.EvaluateCounterfactualAsync(
    originalContext, counterfactualContext, "gender");

// Misinformation risk assessment
var misinformation = new MisinformationMetric(chatClient);
var misInfoResult = await misinformation.EvaluateAsync(context);

// All must pass for responsible AI compliance
toxicityResult.Should().HaveScoreAbove(90);
biasResult.Should().HavePassed();
misInfoResult.Should().HavePassed();
```

| Metric | Type | Detects |
|--------|------|--------|
| **ToxicityMetric** | Hybrid | Hate speech, violence, harassment |
| **BiasMetric** | LLM | Stereotyping, differential treatment |
| **MisinformationMetric** | LLM | Unsupported claims, false confidence |

**✅ See:** [docs/ResponsibleAI.md](docs/ResponsibleAI.md)

---

### Memory Evaluation: Does Your Agent Actually Remember?

AgentEval ships **AgentEval.Memory** — the comprehensive .NET toolkit for evaluating agent memory: retention, recall depth across long contexts, temporal reasoning, fact-update handling, cross-session persistence, and resistance to distractor turns.

```csharp
// One-line benchmark with grade
var runner = MemoryBenchmarkRunner.Create(chatClient);
var agent  = chatClient.AsEvaluableAgent(name: "MemoryAgent", includeHistory: true);

var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard);
Console.WriteLine($"Memory: {result.OverallScore:F1}% ({result.Grade})");

// Save baseline + generate an interactive HTML pentagon report
var store = new JsonFileBaselineStore();
await store.SaveAsync(result.ToBaseline(label: "GPT-4o"));
await result.ExportHtmlReportAsync("memory-report.html");
```

**What's in the box:**

| Capability | Detail |
|---|---|
| **5 memory metrics** | Retention, ReachBack, Temporal, NoiseResilience, ReducerFidelity |
| **5 benchmark presets** | Quick (3 cats) → Standard (8) → Full (12) → Diagnostic / Overflow (192K-token haystacks) |
| **HTML pentagon reports** | Multi-model overlay, baseline diffs, drill-down judge explanations |
| **LongMemEval (ICLR 2025)** | Fully re-implemented in .NET — paper-comparable scoring (GPT-4o = 57.7%) |
| **MAF-native** | Compatible with `AIContextProvider`, `ChatHistoryProvider`, `CompactionStrategy` |
| **Custom scenarios** | Build your own with `MemoryFact` / `MemoryQuery` / `MemoryTestRunner` |

**Honest caveats:**
- The native `Standard` benchmark currently scores ~88–93% on GPT-4.1 — strong models clear it comfortably. Use it as a **regression gate** for your own delta over time, and use **LongMemEval** (Sample G7) for cross-platform comparable numbers. Harder synthesis/counterfactual scenarios are on the way.
- Memory evaluation **always calls a real LLM** (the judge can't be mocked).
- LongMemEval dataset isn't redistributed — [download it from HuggingFace](https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned).

**✅ See:** [docs/memory-evaluation.md](docs/memory-evaluation.md) • [docs/maf-memory-integration.md](docs/maf-memory-integration.md) • [Sample G2: Memory Benchmark](samples/AgentEval.Samples/MemoryEvaluation/02_MemoryBenchmarkDemo.cs) • [Sample G7: LongMemEval](samples/AgentEval.Samples/MemoryEvaluation/07_LongMemEvalBenchmark.cs)

---

## Why AgentEval?

| Challenge | How AgentEval Solves It |
|-----------|------------------------|
| "What tools did my agent call?" | **Full tool timeline** with arguments, results, timing |
| "Evaluations fail randomly!" | **stochastic evaluation** - assert on pass *rate*, not pass/fail |
| "Which model should I use?" | **Model comparison** with cost/quality recommendations |
| "Is my agent compliant?" | **Behavioral policies** - guardrails as code |
| "Is my RAG hallucinating?" | **Faithfulness metrics** - grounding verification |
| "What's the latency/cost?" | **Performance metrics** - TTFT, tokens, estimated cost |
| "How do I debug failures?" | **Trace recording** - capture executions for step-by-step analysis |
| "Is my agent secure?" | **Red Team evaluation** - 192 probes, OWASP LLM 2025 coverage |
| "Is content safe and unbiased?" | **ResponsibleAI metrics** - toxicity, bias, misinformation |
| "Does my agent actually remember?" | **Memory evaluation** - retention, reach-back, temporal, LongMemEval (ICLR 2025) |

---

## Who Is AgentEval For?

**🏢 .NET Teams Building AI Agents** — If you're building production AI agents in .NET and need to verify tool usage, enforce SLAs, handle non-determinism, or compare models—AgentEval is for you.

**🚀 Microsoft Agent Framework (MAF) Developers** — Native integration with MAF concepts: `AIAgent`, `IChatClient`, automatic tool call tracking, and performance metrics with token usage and cost estimation.

**📊 ML Engineers Evaluating LLM Quality** — Rigorous evaluation capabilities: RAG metrics (Faithfulness, Relevance, Context Precision), embedding-based similarity, and calibrated judge patterns for consistent evaluation.

---

## The .NET Advantage

| Feature | AgentEval | Python Alternatives |
|---------|-----------|---------------------|
| **Language** | Native C#/.NET | Python only |
| **Type Safety** | Compile-time errors | Runtime exceptions |
| **IDE Support** | Full IntelliSense | Variable |
| **MAF Integration** | First-class | None |
| **Fluent Assertions** | `Should().HaveCalledTool()` | N/A |
| **Trace Replay** | Built-in | Manual setup |

---

## Key Features

### Core Features
- Fluent assertions - tool order, arguments, results, duration
- Stochastic evaluation - run N times, analyze statistics (mean, std dev, p90)
- Model comparison - compare across models with recommendations
- Trace recording - capture executions for debugging and reproduction
- Performance assertions - latency, TTFT, tokens, cost

### Evaluation Coverage
- Red Team security - 192 probes, OWASP LLM 2025, MITRE ATLAS coverage
- Responsible AI - toxicity, bias, misinformation detection
- **Memory evaluation** - retention, reach-back, temporal, cross-session, HTML pentagon reports, LongMemEval (ICLR 2025)
- Multi-turn conversations - full conversation flow evaluation
- Workflow evaluation - multi-agent orchestration and routing
- Snapshot evaluation - regression detection with semantic similarity

### Metrics
- RAG metrics - faithfulness, relevance, context precision/recall, correctness
- Agentic metrics - tool selection, arguments, success, efficiency
- Embedding metrics - semantic similarity (100x cheaper than LLM)
- Custom metrics - extensible for your domain

### Developer Experience
- Rich output - configurable verbosity (None/Summary/Detailed/Full)
- Time-travel traces - step-by-step execution capture in JSON
- Trace artifacts - auto-save traces for failed evaluations
- Behavioral policies - NeverCallTool, MustConfirmBefore, NeverPassArgumentMatching

### CLI Tool
- `agenteval eval` - Evaluate any OpenAI-compatible agent from the command line
- Flexible CLI with multiple options, several export formats, LLM-as-judge, CI/CD-friendly exit codes
- Packaged as `dotnet tool install AgentEval.Cli`

### Cross-Framework & DI
- Universal `IChatClient.AsEvaluableAgent()` one-liner for any AI provider
- Dependency Injection via `services.AddAgentEval()` / `services.AddAgentEvalAll()`
- Semantic Kernel bridge via `AIFunctionFactory.Create()` (see NuGetConsumer sample)

### Integration
- CI/CD integration - JUnit XML, Markdown, JSON, SARIF export
- Benchmarks - custom patterns with dataset loaders (JSON, YAML, CSV, JSONL)
- Comprehensive multi-framework evaluation suite across all supported TFMs

---

## Installation

```bash
dotnet add package AgentEval --prerelease
```

**Compatibility:**

| Dependency | Version |
|------------|----------|
| Microsoft Agent Framework (MAF) | `1.3.0` |
| Microsoft.Extensions.AI | `10.5.0` |
| .NET | 8.0, 9.0, 10.0 |

**Single package, modular internals:**
- `AgentEval.Abstractions` — Public contracts and interfaces
- `AgentEval.Core` — Metrics, assertions, comparison, tracing
- `AgentEval.DataLoaders` — Data loading and export
- `AgentEval.MAF` — Microsoft Agent Framework integration
- `AgentEval.Memory` — Memory evaluation, benchmarks, LongMemEval, HTML reporting
- `AgentEval.RedTeam` — Security testing

**CLI Tool:**
```bash
dotnet tool install -g AgentEval.Cli --prerelease
agenteval eval --endpoint https://your-resource.openai.azure.com --model gpt-4o --dataset tests.yaml
```

**Supported Frameworks:** .NET 8.0, 9.0, 10.0

---

## Quick Start

See the **[Getting Started Guide](docs/getting-started.md)** for a complete walkthrough with code examples.

---

## Documentation

| Guide | Description |
|-------|-------------|
| [Getting Started](docs/getting-started.md) | Your first agent evaluation in 5 minutes |
| [Fluent Assertions](docs/assertions.md) | Complete assertion guide |
| [stochastic evaluation](docs/stochastic-evaluation.md) | Handle LLM non-determinism |
| [Model Comparison](docs/model-comparison.md) | Compare models with confidence |
| [Benchmarks](docs/benchmarks.md) | Benchmark patterns and best practices |
| [Tracing](docs/tracing.md) | Record and Replay patterns |
| [Red Team Security](docs/redteam.md) | Security probes, OWASP/MITRE coverage |
| [Responsible AI](docs/ResponsibleAI.md) | Toxicity, bias, misinformation detection |
| [Memory Evaluation](docs/memory-evaluation.md) | Retention, reach-back, temporal, LongMemEval, HTML reports |
| [MAF Memory Integration](docs/maf-memory-integration.md) | How AgentEval.Memory maps to MAF 1.3.0 pipelines |
| [Cross-Framework](docs/cross-framework.md) | Semantic Kernel, IChatClient adapters |
| [CLI Tool](docs/cli.md) | Command-line evaluation guide |
| [Migration Guide](docs/comparison.md) | Coming from Python/Node.js frameworks |
| [Code Gallery](docs/showcase/code-gallery.md) | Stunning code examples |

---

## Samples

Run the included samples, organised into groups:

```bash
dotnet run --project samples/AgentEval.Samples
```

The interactive menu lets you select a **group** (A–G), then a **sample** within it.

| Group | Focus | Samples |
|-------|-------|---------|
| **A — Getting Started** ★ no credentials | Hello World, tool tracking, performance basics, MAF integration patterns | 7 |
| **B — Metrics & Quality** | RAG evaluation, quality metrics, judge calibration, responsible AI | 5 |
| **C — Workflows & Conversations** | Multi-turn conversations, MAF workflows, source-gen executors | 4 |
| **D — Performance & Statistics** | Latency profiling, stochastic evaluation, model comparison, streaming | 5 |
| **E — Safety & Security** | Policy guardrails, red team scanning, OWASP compliance | 3 |
| **F — Data & Infrastructure** | Snapshot testing, datasets, trace replay, benchmarks, cross-framework | 7 |
| **G — Memory Evaluation** | Memory basics, benchmarks, scenarios, DI, cross-session, HTML reports, LongMemEval (ICLR 2025) | 10 |

**41 samples in total.** See [samples/AgentEval.Samples/README.md](samples/AgentEval.Samples/README.md) for the full listing with per-sample descriptions, timing, and credential requirements.

---

## CI Status

| Workflow | Status |
|----------|--------|
| Build & Test | [![Build](https://github.com/AgentEvalHQ/AgentEval/actions/workflows/ci.yml/badge.svg)](https://github.com/AgentEvalHQ/AgentEval/actions/workflows/ci.yml) |
| Security Scan | [![Security](https://github.com/AgentEvalHQ/AgentEval/actions/workflows/security.yml/badge.svg)](https://github.com/AgentEvalHQ/AgentEval/actions/workflows/security.yml) |
| Documentation | [![Docs](https://github.com/AgentEvalHQ/AgentEval/actions/workflows/docs.yml/badge.svg)](https://github.com/AgentEvalHQ/AgentEval/actions/workflows/docs.yml) |

---

## Contributing

We welcome contributions! Please see:
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- [SECURITY.md](SECURITY.md)

---

## Commercial & Enterprise 
AgentEval is MIT and community-driven. For enterprise inquiries, see: https://agenteval.dev/commercial.html

---

## Forever Open Source

AgentEval is **MIT licensed** and will remain open source forever. We believe in:
- ✅ **No license changes** — MIT today, MIT forever
- ✅ **No bait-and-switch** — core stays MIT and fully usable
- ✅ **Community first** — built with the .NET AI community
- ℹ️ **Optional add-ons may exist separately** (if/when built)

---

## License

MIT License. See [LICENSE](LICENSE) for details.

---

<p align="center">
  <strong>Built with love for the .NET AI community</strong>
</p>

<p align="center">
  <a href="https://github.com/AgentEvalHQ/AgentEval">Star us on GitHub</a> |
  <a href="https://www.nuget.org/packages/AgentEval">NuGet</a> |
  <a href="https://github.com/AgentEvalHQ/AgentEval/issues">Issues</a>
</p>

---

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=AgentEvalHQ/AgentEval&type=Date)](https://star-history.com/#AgentEvalHQ/AgentEval&Date)
