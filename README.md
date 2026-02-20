# AgentEval

<p align="center">
  <img src="assets/AgentEval_bounded.png" alt="AgentEval Logo" width="450" />
</p>

<p align="center">
  <strong>The .NET Evaluation Toolkit for AI Agents</strong>
</p>

<p align="center">
  <a href="https://github.com/joslat/AgentEval/actions/workflows/ci.yml"><img src="https://github.com/joslat/AgentEval/actions/workflows/ci.yml/badge.svg" alt="Build" /></a>
  <a href="https://github.com/joslat/AgentEval/actions/workflows/security.yml"><img src="https://github.com/joslat/AgentEval/actions/workflows/security.yml/badge.svg" alt="Security" /></a>
  <a href="https://codecov.io/gh/joslat/AgentEval"><img src="https://codecov.io/gh/joslat/AgentEval/graph/badge.svg?token=Y28TAK3LNH" alt="Coverage" /></a>
  <a href="https://joslat.github.io/AgentEval/"><img src="https://img.shields.io/badge/docs-GitHub%20Pages-blue" alt="Documentation" /></a>
  <a href="https://www.nuget.org/packages/AgentEval"><img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet" /></a>
  <a href="https://github.com/joslat/AgentEval/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="License" /></a>
</p>

---

AgentEval is **the comprehensive .NET toolkit for AI agent evaluation**—tool usage validation, RAG quality metrics, stochastic evaluation, and model comparison—built first for **Microsoft Agent Framework (MAF) `1.0.0-rc1`** with **Microsoft.Extensions.AI `10.3.0`**. What RAGAS and DeepEval do for Python, AgentEval does for .NET, with the fluent assertion APIs .NET developers expect.

> **For years, agentic developers have imagined writing evaluations like this. Today, they can.**

---

## The Code You Have Been Dreaming Of

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

### Assert on Tool Chains Like You Have Always Imagined

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

---

### stochastic evaluation: Because LLMs Are Non-Deterministic

LLMs don't return the same output every time. Run evaluations multiple times and analyze statistics:

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions
    {
        Runs = 20,                    // Run 20 times
        SuccessRateThreshold = 0.85,  // 85% must pass
        ScoreThreshold = 75           // Min score to count as "pass"
    });

// Understanding the statistics:
// - Mean: Average score across all 20 runs (higher = better overall quality)
// - StandardDeviation: How much scores vary run-to-run (lower = more consistent)
// - SuccessRate: % of runs where score >= ScoreThreshold (75 in this case)

result.Statistics.Mean.Should().BeGreaterThan(80);            // Avg quality
result.Statistics.StandardDeviation.Should().BeLessThan(10);  // Consistency

// The evaluation that never flakes - pass/fail based on rate, not single run
Assert.True(result.PassedThreshold, 
    $"Success rate {result.SuccessRate:P0} below 85% threshold");
```

**Why this matters:** A single evaluation run might pass 70% of the time due to LLM randomness. stochastic evaluation tells you the *actual* reliability.

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

---

### Combined: Stochastic + Model Comparison

The most powerful pattern - compare models with statistical rigor (see Sample16):

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

### Integration
- CI/CD integration - JUnit XML, Markdown, JSON, SARIF export
- CLI tool - agenteval eval, agenteval init
- Benchmarks - custom patterns with dataset loaders (JSON, YAML, CSV, JSONL)
- 1,000+ tests (×3 TFMs) - production quality

---

## Installation

```bash
dotnet add package AgentEval --prerelease
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
| [Migration Guide](docs/comparison.md) | Coming from Python/Node.js frameworks |
| [Code Gallery](docs/showcase/code-gallery.md) | Stunning code examples |

---

## Samples

Run all 23 included samples:

```bash
dotnet run --project samples/AgentEval.Samples
```

| Sample | Description | Time |
|--------|-------------|------|
| **01: Hello World** | The simplest possible agent evaluation | 2 min |
| **02: Agent with One Tool** | Tool tracking and fluent assertions | 5 min |
| **03: Agent with Multiple Tools** | Tool ordering, timing, and timeline | 7 min |
| **04: Performance Metrics** | Latency, cost, TTFT, and token tracking | 5 min |
| **05: RAG Evaluation** | Faithfulness, relevance, precision, recall, correctness | 8 min |
| **06: Benchmarks** | Performance and agentic benchmark patterns | 5 min |
| **07: Snapshot Evaluation** | Regression detection with baselines | 5 min |
| **08: Conversation Evaluation** | Multi-turn agent interactions | 5 min |
| **09: Workflow Evaluation** | Multi-agent orchestration and routing | 10 min |
| **10: Workflow with Tools** | Workflow agents with tool integration | 8 min |
| **11: Datasets and Export** | Batch evaluation with JSON/YAML/CSV/JSONL | 5 min |
| **12: Policy & Safety Evaluation** | Enterprise guardrails (NeverCallTool, MustConfirmBefore) | 8 min |
| **13: Trace Record & Replay** | Capture executions for deterministic evaluation | 8 min |
| **14: stochastic evaluation** | Run evaluations N times for statistical confidence | 5 min |
| **15: Model Comparison** | Compare multiple models on the same task | 8 min |
| **16: Combined Stochastic + Comparison** | Stochastic evaluations across multiple models | 10 min |
| **17: Quality & Safety Metrics** | Groundedness, coherence, fluency evaluation | 5 min |
| **18: Judge Calibration** | Multi-model consensus for reliable LLM-as-judge | 8 min |
| **19: Streaming vs Async Performance** | Performance comparison of different execution modes | 5 min |
| **20: Red Team Basic** | Security evaluation with prompt injection and jailbreak | 8 min |
| **21: Red Team Advanced** | Comprehensive security testing across all attack types | 10 min |
| **22: Responsible AI** | Toxicity, bias, misinformation metrics with counterfactual testing | 8 min |
| **23: Benchmark System** | Performance, agentic, standard, and cost benchmarks | 10 min |

---

## CI Status

| Workflow | Status |
|----------|--------|
| Build & Test | [![Build](https://github.com/joslat/AgentEval/actions/workflows/ci.yml/badge.svg)](https://github.com/joslat/AgentEval/actions/workflows/ci.yml) |
| Security Scan | [![Security](https://github.com/joslat/AgentEval/actions/workflows/security.yml/badge.svg)](https://github.com/joslat/AgentEval/actions/workflows/security.yml) |
| Documentation | [![Docs](https://github.com/joslat/AgentEval/actions/workflows/docs.yml/badge.svg)](https://github.com/joslat/AgentEval/actions/workflows/docs.yml) |

---

## Contributing

We welcome contributions! Please see:
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- [SECURITY.md](SECURITY.md)

---

## Forever Open Source

AgentEval is **MIT licensed** and will remain open source forever. We believe in:
- ✅ **No license changes** - MIT today, MIT forever
- ✅ **No "open core"** - All features are open source, no proprietary tiers
- ✅ **Community first** - Built for and with the .NET AI community

---

## License

MIT License. See [LICENSE](LICENSE) for details.

---

<p align="center">
  <strong>Built with love for the .NET AI community</strong>
</p>

<p align="center">
  <a href="https://github.com/joslat/AgentEval">Star us on GitHub</a> |
  <a href="https://www.nuget.org/packages/AgentEval">NuGet</a> |
  <a href="https://github.com/joslat/AgentEval/issues">Issues</a>
</p>

---

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=joslat/AgentEval&type=Date)](https://star-history.com/#joslat/AgentEval&Date)
