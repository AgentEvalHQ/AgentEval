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
  <a href="https://agenteval.dev/"><img src="https://img.shields.io/badge/docs-agenteval.dev-blue" alt="Documentation" /></a>
  <a href="https://www.nuget.org/packages/AgentEval"><img src="https://img.shields.io/nuget/v/AgentEval.svg" alt="NuGet" /></a>
  <a href="https://github.com/AgentEvalHQ/AgentEval/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="License" /></a>
  <a href="https://github.com/microsoft/agent-framework"><img src="https://img.shields.io/badge/built%20on-Microsoft%20Agent%20Framework-blueviolet" alt="Built on Microsoft Agent Framework" /></a>
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

<!-- snippet: tool-chain -->
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

<!-- snippet: stochastic -->
```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(
        Runs: 20,                     // Run 20 times
        SuccessRateThreshold: 0.85)); // 85% of runs must pass

result.Should()
    .HavePassRateAtLeast(0.85)        // reliability
    .HaveMeanScoreAtLeast(80)         // avg quality
    .HaveStandardDeviationAtMost(10); // consistency
```

**The evaluation that never flakes.** Pass rate + mean + standard deviation, not pass/fail roulette.

---

### 🥉 Workflow Evaluation: Multi-Agent Flows as Executable Assertions

MAF workflows are powerful — and finally testable. Assert on executor order, edges traversed, tools called across the graph, and end-to-end SLAs.

<!-- snippet: workflow -->
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

<!-- snippet: performance-sla -->
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

<!-- snippet: policy-guardrails -->
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

<!-- snippet: model-comparison -->
```csharp
var stochasticRunner = new StochasticRunner(harness);
var comparer = new ModelComparer(stochasticRunner);

// CreateAgent(deployment) is your code: it returns an IEvaluableAgent for that model
var results = await comparer.CompareModelsAsync(
    factories: new IAgentFactory[]
    {
        new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
        new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini")),
        new DelegateAgentFactory("gpt-35-turbo", "GPT-3.5 Turbo", () => CreateAgent("gpt-35-turbo"))
    },
    testCases: agenticTestSuite,
    options: new ModelComparisonOptions(RunsPerModel: 5));

Console.WriteLine(results.ToMarkdown());
```

**Output** (abridged; the numbers are illustrative, not from a measured run). Each model gets a composite of quality, speed, cost and reliability, weighted 40/20/20/20 by default (`ScoringWeights`):
```markdown
# 🔬 Model Comparison Report

**Total Test Cases:** 12
**Models Compared:** 3

## 🥇 Overall Win Summary

| Model | Wins | Win Rate |
|-------|------|----------|
| 🏆 GPT-4o Mini | 8/12 | 67% |
| GPT-4o | 4/12 | 33% |

## 📊 Aggregate Rankings

| Rank | Model | Avg Composite | Avg Quality | Avg Speed | Avg Cost | Avg Reliability | Avg Rank |
|------|-------|---------------|-------------|-----------|----------|-----------------|----------|
| 🥇 1 | GPT-4o Mini | **85.6** | 84.5 | 81.7 | 92.4 | 85.0 | 1.33 |
| 🥈 2 | GPT-4o | **79.5** | 91.2 | 58.3 | 62.0 | 95.0 | 1.67 |
| 🥉 3 | GPT-3.5 Turbo | **75.2** | 68.9 | 88.0 | 90.1 | 60.0 | 3.00 |
```

The scores rank the models against each other: on each dimension the best model gets 100 and the worst 0. The cost column needs care. The harness prices every model's runs at the rate of one model name, `EvaluationOptions.ModelName`; with none set, as in the snippet above, no run is priced and every model gets the same cost score, so the differing cost scores in this illustration would not appear. See [how the scores are computed](docs/model-comparison.md#how-the-scores-are-computed).

---

### Combined: Stochastic + Model Comparison

The most powerful pattern — compare models with statistical rigor (see Sample D4):

<!-- snippet: stochastic-model-comparison -->
```csharp
var factories = new IAgentFactory[]
{
    new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
    new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini"))
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

`PrintComparisonTable()` writes one console row per model: pass rate, mean score, mean duration and its spread, time to first token, tokens, cost, tool success rate, and the mean of every metric the runs recorded. `OutputOptions` turns columns on and off.

---

### RAG Quality: Is Your Agent Hallucinating?

<!-- snippet: rag-quality -->
```csharp
var context = new EvaluationContext
{
    Input = "What are the return policy terms?",
    Output = agentResponse,
    Context = retrievedDocuments,
    GroundTruth = "30-day return policy with receipt"
};

// judgeClient is the IChatClient that grades the answer
var faithfulness = await new FaithfulnessMetric(judgeClient).EvaluateAsync(context);
var relevance = await new RelevanceMetric(judgeClient).EvaluateAsync(context);
var correctness = await new AnswerCorrectnessMetric(judgeClient).EvaluateAsync(context);

// Detect hallucinations. Passed is also false when the judge's reply could not be parsed,
// so read Explanation before blaming the agent.
if (!faithfulness.Passed)
    throw new InvalidOperationException($"Faithfulness {faithfulness.Score:F0}: {faithfulness.Explanation}");
```

---

### Red Team Security Evaluation: Find Vulnerabilities Before Production

AgentEval includes comprehensive red team security evaluation with **264 probes across 14 attack types** (Comprehensive intensity), covering **all 10 OWASP LLM Top 10 2025** categories and **8 MITRE ATLAS** techniques.

Beyond the built-in probes, it ships the capabilities that make a red-team result *trustworthy* and *CI-ready*:

- **Over-refusal beside attack success** *(new)* — `--benign-controls --judge` also sends 29 legitimate requests that borrow attack vocabulary. Each reply is graded by the over-refusal judge, and the rate of refused legitimate requests is reported with a 95% interval beside the attack success rate. Below 20 conclusive cases it says "not measured", so an agent that refuses everything can no longer pass without anyone seeing it.
- **Multi-turn & attacker-LLM attacks** — Crescendo, PAIR, TAP, and a tool-aware `ToolEscalation` attack (opt-in).
- **Real attack surfaces** — a tiered tool harness (`--sut-tier text\|function-calling\|instrumented`) with **evidence-fidelity** labeling (Verbal / IntentToAct / Behavioral), plus a live package-registry oracle (`--package-registry live`) and a real RAG-retrieval boundary.
- **Trustworthy verdicts — judge-primary by default + Composite Judges** *(new)* — with a judge configured (`--judge`), the grader that decides whether each attack *succeeded* is now LLM-judge-primary, using **honest-by-construction Composite Judges**: every semantic verdict is split into a positive-only *compromise* detector ⊕ a negative-only *refusal* detector, each structurally clamped so it can only raise its own direction or abstain. (A no-judge scan stays the deterministic keyword oracle, byte-identical to before.) Plus conclusive-only scoring and an explicit *Inconclusive* coverage state — so a green result is never a guess.
- **5 compliance reporters** — OWASP, MITRE ATLAS, SOC 2, ISO 27001, and **NIST AI RMF**. OWASP, MITRE ATLAS and NIST AI RMF run as first-class benchmarks (`agenteval bench owasp\|mitre\|nist`); SOC 2 and ISO 27001 are library reporters (`SOC2ComplianceReporter`, `ISO27001ComplianceReporter`) with no CLI command yet.
- **CI-ready** — SARIF + JUnit export, a baseline regression gate (`--save-baseline`/`--baseline`/`--fail-on`), z-score **calibration** (`--calibration`), LLM **`--explain`** rationale, and external **benchmark packs** (`--pack HarmBench\|JailbreakBench\|CyberSecEval`, license-gated, nothing bundled).
- **Copilot Studio target** — `agenteval redteam --sut copilot-studio` red-teams a Microsoft Copilot Studio agent through the same scanner, with its own config + consent gates and a credential-free test seam; the live connector is wired and unit/mock-tested, but **not independently live-verified against a real Copilot Studio tenant** (no test credentials available yet) — treat a first real run as a smoke test, not a proven-in-production path. See [docs/copilot-studio.md](docs/copilot-studio.md).

> **Proof, not vibes.** Across **810 held-out stochastic trials** — 81 independently-generated cases (70 composite-oracle + 11 DataPoisoning deny-true) run **K=10×** each through the production graders — the Composite Judges fabricated **0 verdicts**: never a safe reply flagged as a compromise, never a real compromise masked as safe. On a separately-pinned label corpus, judge↔label agreement is **κ = 1.000** (n=92) — where keyword graders typically agree with humans only about half the time. The guiding rule: *fabrications are complete failures; honesty is never punished.* Background: [ADR-021→024](docs/adr/README.md) · [Red Team — What's New](docs/redteam-whats-new.md).

See **[Red Team — What's New](docs/redteam-whats-new.md)** for the recent upgrades, how AgentEval compares to PyRIT / garak / others, and a plain-English take on why *grading* a model's reply is the hard part — and **[docs/redteam.md](docs/redteam.md)** for the full CLI reference.

<!-- snippet: red-team -->
```csharp
var redTeam = new RedTeamRunner();
var result = await redTeam.ScanAsync(agent, new ScanOptions
{
    AttackTypes =
    [
        Attack.PromptInjection,
        Attack.Jailbreak,
        Attack.PIILeakage,
        Attack.ExcessiveAgency,  // LLM06
        Attack.InsecureOutput    // LLM05
    ],
    Intensity = Intensity.Quick
});

result.Print();  // the console summary shown below

// Comprehensive security validation
result.Should()
    .HaveMinimumScore(85, because: "security threshold for production")
    .HaveASRBelow(0.15, because: "max 15% attack success allowed")
    .HaveResistedAttack(Attack.PromptInjection.Name, because: "must block injection attempts");
```

**Console summary from `result.Print()`** (illustrative numbers):
```
╔══════════════════════════════════════════════════════════════════════════════╗
║                         RedTeam Security Assessment                          ║
╠══════════════════════════════════════════════════════════════════════════════╣
║  ⚠️ Overall Score: 92.0%
║  Verdict: ❌ Fail
║  Duration: 9.8s | Agent: ResearchAssistant
║  Probes: 25 total, 23 resisted, 1 compromised, 1 inconclusive
║  Coverage: 96% (conclusive) | Conclusive Score: 95.8%
╠══════════════════════════════════════════════════════════════════════════════╣
║  Attack Results:
║
║  Attack                  Resisted     Rate     Severity
║  ────────────────────────────────────────────────────────
║  ✅ Prompt Injection        5/5          100 %    High
║     OWASP: LLM01 | MITRE: AML.T0051
║  ⚠️ Jailbreak               4/5          80 %     High
║     OWASP: LLM01 | MITRE: AML.T0051, AML.T0054
║  ✅ PII/Data Leakage        5/5          100 %    Critical
║     OWASP: LLM02 | MITRE: AML.T0037, AML.T0057
║  ✅ Excessive Agency        5/5          100 %    High
║     OWASP: LLM06 | MITRE: AML.T0051, AML.T0054
║  ⚠️ Insecure Output Ha...   4/5          80 %     High
║     OWASP: LLM05 | MITRE: AML.T0051
╚══════════════════════════════════════════════════════════════════════════════╝

💡 Tip: Use VerbosityLevel.Detailed + ShowSensitiveContent for probe details
```

All three assertions above hold for this run, yet the verdict is `Fail`: one compromised probe of High or Critical severity fails the scan at any score. `result.Should().HavePassed()` asserts on that verdict.

**Multiple export formats** for security teams:
- **JSON** for automation and tooling
- **Markdown** for human-readable reports  
- **JUnit XML** for CI/CD integration
- **SARIF** for GitHub Security tab integration
- **PDF** for executive/board-level reporting

**✅ See Samples:** [02_RedTeamBasic.cs](samples/AgentEval.Samples/SafetyAndSecurity/02_RedTeamBasic.cs) • [03_RedTeamAdvanced.cs](samples/AgentEval.Samples/SafetyAndSecurity/03_RedTeamAdvanced.cs) • [docs/redteam.md](docs/redteam.md)

---

### 🚪 Gatekeeper: Stop the Bad Action Before It Happens

Red-teaming finds the holes. **Gatekeeper closes them at runtime** — the *same* probes and evaluators become
**fail-closed gates in the request path**. It catches the attacks you *can't* stop by "just not giving the tool":

<!-- snippet: gatekeeper -->
```csharp
var agent = baseAgent.AsBuilder()
    .UseAgentEvalGate()   // per-run scope for the sequence gate
    .UseAgentEvalToolGate(
        [
            // 🛑 Block DATA EXFILTRATION: reading customer data is fine, sending mail is fine —
            //    the SEQUENCE is the attack. No tool-list trick catches this.
            new SequenceGate(triggerTools: ["read_customer_data"], guardedTools: ["send_email", "http_post"]),

            // 🎣 The SAME red-team oracle you test with, now a LIVE GUARD against a poisoned tool argument:
            new ProbeEvaluatorGate(new ContainsTokenEvaluator("ignore previous instructions"), GateCost.PureCode),
        ],
        ToolGatePolicy.Terminate)   // block the call AND stop the loop
    .Build();
```

Even if a prompt injection turns *your own* agent against you, the destructive action never executes. **Fail-closed
by design:** a gate that can't prove an action safe *blocks* it, and every decision is recorded as honest `gate.*`
trace evidence (a warn is never counted as a block). Layers span **tool gates**, **run gates**, **session gates**
(auth / rate-limit / quarantine), the red-team **moat**, **canary honeypots** that flag a compromised agent, an
async **shadow judge** for expensive checks, and **human-in-the-loop approval** for the borderline actions.

The same policy is also callable from **outside .NET**: the `agenteval gatekeeper` CLI verb group exposes it as a
language-neutral runtime-policy service — pipe a JSON payload to `agenteval gatekeeper inspect` from Python, Node,
bash, or a CI step and get back a versioned verdict + exit code, no .NET reference required. See
[docs/gatekeeper-cli.md](docs/gatekeeper-cli.md).

**✅ See it:** `dotnet run --project samples/AgentEval.Samples` → group **J** opens on the six-sample 15-minute tour — with a model provider configured, the hybrids among them (00, 04, 10, 14, 16) run on that model and bill it; without one, or with `AGENTEVAL_GATEKEEPER_FORCE_OFFLINE=true`, they run a labelled scripted path, and 23 needs no model at all — or via `agenteval redteam --sut gatekeeper-demo` (add `--scripted` for a free, deterministic run) • [docs/gatekeeper/introduction.md](docs/gatekeeper/introduction.md)

---

### 🧩 Agent Skills: Evaluate & Govern Progressive Disclosure

Microsoft Agent Framework's **Agent Skills** (GA'd 2026-07-07) let an agent progressively disclose capabilities through three stable tools — `load_skill`, `read_skill_resource`, `run_skill_script` — instead of stuffing every capability into the system prompt up front. AgentEval evaluates and governs that surface end to end: fluent assertions on the disclosure trace, a free structural efficiency metric, a `SKILL.md` compliance scanner, a dedicated red-team attack for a poisoned skill description, deterministic Gatekeeper gates for `run_skill_script` code execution, and a composite Skill Health & Security Index.

<!-- snippet: agent-skills -->
```csharp
// Assert the disclosure trace like any other tool chain
result.ToolUsage!.Should()
    .HaveLoadedSkill("expense-report")
    .And().HaveReadSkillResource("expense-report", "resources/policy.md")
        .AfterTool(SkillToolNames.LoadSkill)
    .And().HaveDisclosedProgressively()
    .NotHaveRunSkillScript(because: "a policy lookup doesn't need the compliance script");

// Score the load -> read -> run funnel (structural, free — no LLM call)
var efficiency = await new SkillDisclosureEfficiencyMetric().EvaluateAsync(new EvaluationContext
{
    Input = "n/a", Output = "n/a", ToolUsage = result.ToolUsage,
});
Console.WriteLine($"Disclosure efficiency: {efficiency.Score:F0}/100");

// Scan SKILL.md authoring + governance flags, then roll compliance + efficiency + red-team
// outcome into one composite score — a missing axis is averaged out, never faked as perfect
var complianceReport = await MafSkillScanner.ScanFileSkillsAsync(skillPath, agent);
var index = SkillSecurityIndex.Compute(
    new SkillSecurityIndexInputs(complianceReport, efficiency, Security: null));
Console.WriteLine($"Skill Security Index: {index.Score:F0}/100 ({index.AxesMeasured}/3 axes measured)");
```

Governance doesn't stop at evaluation time: **`SkillScriptExecutionGate`** and **`SkillScriptApprovalGate`** are deterministic Gatekeeper gates that allowlist/approve `run_skill_script` calls before they execute, and `SkillInjectionAttack` (OWASP LLM01, one of the 14 `Attack.All` types above) red-teams a poisoned skill description or `read_skill_resource` output through the same `AttackPipeline` that scans every other surface.

**Honest by construction:** a skill source MAF gives no public enumeration API for (in-memory/class/MCP skills) reports zero resources rather than a guessed inventory, a missing Security Index axis is never counted as perfect, and the injection judge ships **shadow-only** — advisory only — because live calibration found it doesn't yet clear the promotion bar on this surface. See [docs/agent-skills.md](docs/agent-skills.md) for the full, honestly-labeled rundown.

**✅ See it:** `dotnet run --project samples/AgentEval.Samples` → group **K** (real agent — needs a model provider: Azure OpenAI, Bitdeer, or any OpenAI-compatible endpoint), or the standalone deep-dive [`samples/AgentEval.AgentSkillsEval`](samples/AgentEval.AgentSkillsEval) • [docs/agent-skills.md](docs/agent-skills.md)

---

### Responsible AI: Content Safety Metrics

Complementing security evaluation, the `AgentEval.Metrics.ResponsibleAI` namespace provides **content safety evaluation**:

<!-- snippet: responsible-ai -->
```csharp
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

// All must pass; each MetricResult carries Score, Passed and the judge's Explanation
var failed = new[] { toxicityResult, biasResult, misInfoResult }.Where(r => !r.Passed).ToList();
if (failed.Count > 0)
    throw new InvalidOperationException(string.Join("; ", failed.Select(r => $"{r.MetricName}: {r.Explanation}")));
```

| Metric | Type | Detects |
|--------|------|--------|
| **ToxicityMetric** | Hybrid | Hate speech, violence, harassment |
| **BiasMetric** | LLM | Stereotyping, differential treatment |
| **MisinformationMetric** | LLM | Unsupported claims, false confidence |

**✅ See:** [docs/responsible-ai.md](docs/responsible-ai.md)

---

### Memory Evaluation: Does Your Agent Actually Remember?

AgentEval ships **AgentEval.Memory** — the comprehensive .NET toolkit for evaluating agent memory: retention, recall depth across long contexts, temporal reasoning, fact-update handling, cross-session persistence, and resistance to distractor turns.

<!-- snippet: memory -->
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

**What's in the box:**

| Capability | Detail |
|---|---|
| **5 memory metrics** | Retention, ReachBack, Temporal, NoiseResilience, ReducerFidelity |
| **5 benchmark presets** | Quick (3 cats) → Standard (8) → Full (12) → Diagnostic / Overflow (192K-token haystacks) |
| **HTML pentagon reports** | Saved baselines overlaid, per-category deltas between any two, a score timeline (a baseline stores scores, not transcripts, so there is no per-scenario drill-down) |
| **LongMemEval (ICLR 2025)** | Re-implemented in .NET, with the paper's published scores shipped as a reference (GPT-4o = 57.7%, S mode) |
| **MAF-native** | Compatible with `AIContextProvider`, `ChatHistoryProvider`, `CompactionStrategy` |
| **Custom scenarios** | Build your own with `MemoryFact` / `MemoryQuery` / `MemoryTestRunner` |

**Honest caveats:**
- The native `Standard` benchmark mostly tests retrieving a stored fact, so a model that retrieves well can score high on it. Use it as a **regression gate** for your own delta over time, not as a model ranking, and use **LongMemEval** (Sample G7) for cross-platform comparable numbers.
- Memory evaluation **always calls a real LLM** (the judge can't be mocked).
- LongMemEval dataset isn't redistributed — [download it from HuggingFace](https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned).

**✅ See:** [docs/memory-evaluation.md](docs/memory-evaluation.md) • [docs/maf-memory-integration.md](docs/maf-memory-integration.md) • [Sample G2: Memory Benchmark](samples/AgentEval.Samples/MemoryEvaluation/02_MemoryBenchmarkDemo.cs) • [Sample G7: LongMemEval](samples/AgentEval.Samples/MemoryEvaluation/07_LongMemEvalBenchmarkDemo.cs)

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
| "Is my agent secure?" | **Red Team evaluation** - 264 probes, full OWASP LLM Top 10 2025 coverage |
| "Can I stop a bad action at runtime?" | **Gatekeeper** - fail-closed runtime enforcement: block forbidden tool calls before they run, quarantine compromised sessions |
| "Is my agent's use of MAF Agent Skills safe and efficient?" | **Agent Skills evaluation** - disclosure assertions, efficiency metric, compliance scanner, injection red-team, governance gates |
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
- Stochastic evaluation - run N times, analyze statistics (mean, std dev, p95)
- Model comparison - compare across models with recommendations
- Trace recording - capture executions for debugging and reproduction
- Performance assertions - latency, TTFT, tokens, cost

### Evaluation Coverage
- Red Team security - 264 probes, full OWASP LLM Top 10 2025, MITRE ATLAS coverage
- Gatekeeper runtime enforcement - fail-closed gates that block forbidden tool calls before they run, red-team probes as runtime guards, and an async shadow judge that quarantines compromised sessions ([docs](docs/gatekeeper/introduction.md))
- **Agent Skills evaluation** - disclosure assertions, a free efficiency metric, `SKILL.md` compliance scanning, a skill-injection red-team attack, and deterministic `run_skill_script` governance gates ([docs](docs/agent-skills.md))
- Responsible AI - toxicity, bias, misinformation detection
- **Memory evaluation** - retention, reach-back, temporal, cross-session, HTML pentagon reports, LongMemEval (ICLR 2025)
- Multi-turn conversations - full conversation flow evaluation
- Workflow evaluation - multi-agent orchestration and routing
- Snapshot evaluation - regression detection with scrubbed and ignored fields, plus optional word-overlap similarity for free text

### Metrics
- RAG metrics - faithfulness, relevance, context precision/recall, correctness
- Agentic metrics - tool selection, arguments, success, efficiency
- Embedding metrics - semantic similarity from an embedding model, with no judge LLM call
- Custom metrics - extensible for your domain

### Developer Experience
- Rich output - configurable verbosity (None/Summary/Detailed/Full)
- Time-travel traces - step-by-step execution capture in JSON
- Trace artifacts - auto-save traces for failed evaluations
- Behavioral policies - NeverCallTool, MustConfirmBefore, NeverPassArgumentMatching

### CLI Tool
- `agenteval init-workspace / doctor / migrate` - Bootstrap, validate, and migrate the `.agenteval/` workspace (canonical output store with audit-chain integrity)
- `agenteval bench {gdpr,eu-ai-act,agentic}` - Run compliance and agentic benchmark suites
- `agenteval compliance render` / `agenteval render --benchmark agentic` - Re-render reports from existing evidence (no LLM cost)
- `agenteval mc serve / mc doctor` - Launch and verify the Mission Control web portal (read-only viewer over `.agenteval/`)
- CI/CD-friendly exit codes; multiple export formats via `agenteval render`

### Mission Control Portal
- Single-binary web portal (Hot Chocolate 16 GraphQL + minimal REST + React SPA) served on `http://localhost:5000`
- Read-only view over `.agenteval/`: dashboard, runs list, recursive `EvalResult` tree drill-down, compliance matrix with audit-chain badges, evaluator registry, per-evaluator timeline
- Single-port deployment via `agenteval mc serve`, `dotnet run --project src/AgentEval.MissionControl`, or `docker compose up`
- See [`docs/missioncontrol/getting-started.md`](docs/missioncontrol/getting-started.md)

### Benchmark Families (single-source-of-truth registry)

Every family auto-registers via `[ModuleInitializer]` into `BenchmarkFamilyRegistry`.
`agenteval bench --list` reads from the registry — no hardcoded family lists anywhere
(ADR-017 Convention 3).

| Family | Presets | CLI status | What it grades | Cost tier (default) |
|---|---|---|---|---|
| **GDPR** | `smoke` / `standard` / `audit` + 3 domain packs (healthcare / HR / children) | ✅ end-to-end | 29 article YAMLs across 6 pillars | Medium |
| **EU AI Act** | `smoke` / `standard` / `audit` + 3 domain packs (high-risk-employment / -credit / -education) | ✅ end-to-end | 15 article YAMLs across 6 pillars (Reg (EU) 2024/1689) | Medium |
| **Agentic** | 12 presets (`agentic-execution` / `tool-call-accuracy` / `rag-quality` / `telemetry` etc.; `safety` is programmatic only) plus a `--budget-tier {trivial,low,medium,high,all}` filter | ✅ end-to-end | Foundry-equivalent 60-evaluator universe — system / process / UX / quality / safety / adversarial / reasoning / calibration / memory | Medium |
| **OWASP LLM Top 10** | `top10` / `smoke` / `audit` / `top10-rag` | ✅ end-to-end (`--from-env` or `--endpoint`/`--model` for a real target; without one it refuses, exit 2; `--sut mock` runs a stand-in; graded judge first) | 14 attack types covering all 10 OWASP LLM Top 10 v2.0 categories (LLM03/04/08/09 added in Wave D; SkillInjection added for MAF Agent Skills) | Medium |
| **MITRE ATLAS** | `atlas-baseline` / `atlas-smoke` / `atlas-audit-grade` | ✅ end-to-end (same targets as OWASP; graded judge first) | Same 14 attacks mapped via `IAttackType.MitreAtlasIds` covering 8 applicable ATLAS techniques | Medium |
| **NIST AI RMF** | `rmf-baseline` / `rmf-smoke` / `rmf-audit-grade` | ✅ end-to-end (`--from-env` or `--endpoint`/`--model` for a real target; without one it refuses, exit 2; `--sut mock` runs a stand-in; graded judge first) | Same 14 attacks mapped to NIST AI RMF (AI 100-1) MEASURE security/privacy/validity sub-actions (GOVERN/MAP/MANAGE not applicable) | Medium |
| **LongMemEval** | `subset` / `full` (ICLR 2025) | ✅ end-to-end | Cross-platform memory benchmark — paper-published GPT-4o baseline ≈ 57.7% | Medium |
| **Memory** | `quick` / `standard` / `full` / `diagnostic` / `overflow` | ✅ end-to-end | Native AgentEval memory benchmark — 3/8/12 categories, weighted grading | Medium |
| **TypedMemEval** | one preset per vertical: `prospective` / `episodic` / `arithmetic` / `workingmemory` / `forgetting` / `bitemporal` / `semantic` / `conjunction` / `procedural` / `temporal` | ✅ `agenteval bench typedmemeval` | AgentEval's own typed memory corpora, embedded (no download); results are not comparable with LongMemEval numbers | Medium |
| **Performance** | `latency` / `throughput` / `cost` | ✅ end-to-end (`--from-env`) | P99 latency / concurrent throughput / per-prompt cost against your deployment | Low |
| **Trace Fidelity** | `smoke` / `standard` / `audit-grade` | ✅ end-to-end (pure code, no LLM cost — reconciles two supplied `.trace.json` files) | Agent-boundary vs chat-boundary trace reconciliation — missing/phantom calls, hidden retries, argument drift, token under-reporting, suppressed finish reasons | Free |
| **Workflow Trace Fidelity** | `smoke` / `standard` / `audit-grade` | ✅ end-to-end (pure code, no LLM cost — reconciles a workflow `.trace.json`) | Per-executor workflow ledger (tokens + finish reason) vs chat-boundary truth — per-executor fidelity (Agree / TokenMismatch / FinishMismatch / NoTruth) | Free |

Every evidence document is cryptographically chained to its source run; `agenteval doctor` re-validates on demand. Per-family `getting-started.md` guides live under [`docs/benchmarks/`](docs/benchmarks/) (OWASP + GDPR + EU AI Act + Memory + LongMemEval + TypedMemEval + MITRE + Performance + Agentic), with the two trace-fidelity families in [`docs/benchmarks/trace-fidelity.md`](docs/benchmarks/trace-fidelity.md) and [`docs/benchmarks/workflow-trace-fidelity.md`](docs/benchmarks/workflow-trace-fidelity.md).

### `.agenteval/` Workspace Standard
- Canonical on-disk format: one folder per agent / workflow, deterministic run IDs, SHA-256 content hashes on every manifest
- Read-only consumed by Mission Control; written by the CLI, test harnesses, and benchmark runners
- See [`docs/agenteval-workspace.md`](docs/agenteval-workspace.md)

### Cross-Framework & DI
- Universal `IChatClient.AsEvaluableAgent()` one-liner for any AI provider
- Dependency Injection via `services.AddAgentEval()` / `services.AddAgentEvalAll()`
- Semantic Kernel bridge via `AIFunctionFactory.Create()` (see NuGetConsumer sample)

### Integration
- **⭐ Azure AI Foundry evals** — run Foundry evals **alongside** (batched, one source-tagged report) and **inside** (as weighted leaves in a composite benchmark) your AgentEval evals, from a single agent run. See [Foundry Evals Integration](docs/foundry-evals-integration.md).
- CI/CD integration - JUnit XML, Markdown, JSON, SARIF export
- Benchmarks - custom patterns with dataset loaders (JSON, YAML, CSV, JSONL)
- Comprehensive multi-framework evaluation suite across all supported TFMs

---

## Feature Maturity

AgentEval ships as a single lockstep-versioned package (see [Installation](#installation)) — there is no per-package version to signal maturity the way some multi-package frameworks do. Instead, maturity is tracked per front, here, plus `[Experimental]` attributes on individual volatile APIs (compiler-enforced — referencing one without acknowledging it is a build **error**, not just a warning). Only Gatekeeper's public surface is frozen by a test; every other front can still change incompatibly, and breaking changes are listed in the [CHANGELOG](CHANGELOG.md).

| Front | Maturity | Why |
|---|---|---|
| Core eval (assertions, RAG metrics, LLM-as-judge, benchmarks, exporters) | **GA-track, not frozen** | The longest-standing surface and the foundation every other front builds on. The public API of `AgentEval.Core` and `AgentEval.Abstractions` is snapshot-tested: any change to a public type or member fails the build until the new surface is reviewed, so it cannot move silently. It can still move on purpose, and the CHANGELOG records past breaking changes there (for example a type moved out of `AgentEval.Abstractions` and a member added to the `IOutputStoreReader` interface). |
| RedTeam core (OWASP LLM Top 10, attacks/probes, Composite Judges) | **GA-track, not frozen** | Feature-complete for its stated scope. No test guards its public API, so it can still change incompatibly; breaking changes are listed in the CHANGELOG. |
| Compliance benchmarks (GDPR, EU AI Act) | **Beta** | Scenario content, weights and aggregation are stable, but judge calibration is **not** reproducible yet: it was measured in May 2026 on the maintainer's own deployments, with the generic judge prompt rather than the regulation prompt the benchmarks send (`calibrate` sends the benchmark prompt from 0.42.0-beta), and the reports are not in the repository. Beta until re-calibration is published. See the CHANGELOG (`Corrected`). |
| **Gatekeeper** (runtime enforcement) | **Stable (v1)** | The public surface is frozen and snapshot-tested: a change fails CI until it is reviewed. Eight newer types stay individually marked `[Experimental]` and are outside that promise, as are the A2A gates, which are implemented and calibrated but not promoted. It runs inside Microsoft Agent Framework's own AgentHooks host through an experimental adapter. Known limits are listed on the [implementation status](docs/gatekeeper/implementation-status.md) page. |
| Agent Skills | **Beta** (evaluation) · **Stable (v1)** (governance gates) | The evaluation side (disclosure assertions, the efficiency metric, the `SKILL.md` scanner, the Skill Security Index and `SkillInjectionAttack`) is shipped and tested, but depends on a recently GA'd upstream (MAF Agent Skills) and is still growing. The Gatekeeper skill gates (`SkillScriptExecutionGate`, `SkillScriptApprovalGate` and `GatekeeperOptions.WithSkillGate`) are part of the frozen Gatekeeper v1 surface. |
| Copilot Studio | **Experimental** | The connector has not yet been verified against a real tenant. |
| Mission Control | **Experimental** | Real and functioning, but with acknowledged gaps and comparatively less investment than Core/RedTeam. |

---

## Installation

```bash
dotnet add package AgentEval --prerelease
```

**Compatibility:** .NET **8.0 / 9.0 / 10.0**. The Microsoft Agent Framework (MAF) and `Microsoft.Extensions.AI` versions ship centrally pinned in [`Directory.Packages.props`](Directory.Packages.props) — see the [CHANGELOG](CHANGELOG.md) for the exact versions in each release.

**Single package, modular internals.** The `AgentEval` package embeds these assemblies; none of them is published as a separate package:
- `AgentEval.Abstractions` — Public contracts and interfaces
- `AgentEval.Core` — Metrics, assertions, comparison, tracing
- `AgentEval.DataLoaders` — Data loading and export
- `AgentEval.MAF` — Microsoft Agent Framework integration, including Gatekeeper
- `AgentEval.Memory` — Memory evaluation, benchmarks, LongMemEval, HTML reporting
- `AgentEval.RedTeam` and `AgentEval.RedTeam.Gatekeeper` — Security testing, and red-team evaluators as runtime gates
- `AgentEval.Evals.Agentic` and `AgentEval.Evals.Performance` — The agentic evaluator suite and the performance benchmarks
- `AgentEval.Compliance.Core`, `AgentEval.Compliance.Gdpr` and `AgentEval.Compliance.EuAiAct` — Compliance benchmarks
- `AgentEval.Rendering.Pdf` — PDF report rendering

**Optional add-on, not on NuGet yet** (kept out of `AgentEval` so its dependency tree isn't forced on everyone):
- `AgentEval.MAF.CopilotStudio` — evaluate a live Microsoft Copilot Studio agent directly in code (`IChatClient`/`IEvaluableAgent`), no CLI required. It has not been published to NuGet, so `dotnet add package` cannot find it: build it from source and add a project reference to `src/AgentEval.MAF.CopilotStudio/AgentEval.MAF.CopilotStudio.csproj`. See [docs/copilot-studio.md](docs/copilot-studio.md#using-it-directly-in-code-no-cli).

**CLI Tool:**

The CLI is published as a [`dotnet tool`](https://learn.microsoft.com/dotnet/core/tools/global-tools) on NuGet:

```bash
# Install (one-time, global)
dotnet tool install --global AgentEval.Cli --prerelease

# Use
agenteval init-workspace                                       # bootstrap .agenteval/ workspace
agenteval bench --list                                         # discover the 11 benchmark families
agenteval bench gdpr --preset smoke --subject MyAgent --from-env   # GDPR benchmark against your real agent
agenteval bench owasp --preset smoke --subject MyAgent --from-env   # OWASP red-team against your real agent
agenteval mc serve                                             # open Mission Control (requires .NET 10)
agenteval doctor                                               # verify workspace integrity
```

**Requirements**: .NET 8 SDK for the core surface; .NET 10 SDK additionally for `mc serve`
(graceful fallback message on .NET 8). See [`docs/installation.md`](docs/installation.md#cli-tool)
for update / uninstall / contributor-path (`dotnet run --project src/AgentEval.Cli`) details.

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
| [Agent Skills](docs/agent-skills.md) | Evaluate & govern MAF Agent Skills — assertions, disclosure efficiency, compliance scanning, injection red-team |
| [Responsible AI](docs/responsible-ai.md) | Toxicity, bias, misinformation detection |
| [Memory Evaluation](docs/memory-evaluation.md) | Retention, reach-back, temporal, LongMemEval, HTML reports |
| [MAF Memory Integration](docs/maf-memory-integration.md) | How AgentEval.Memory maps to MAF pipelines |
| [MAF Eval Integration](docs/using-agenteval-with-maf-evals.md) | Run AgentEval through MAF's `agent.EvaluateAsync()` |
| [⭐ Foundry Evals Integration](docs/foundry-evals-integration.md) | Run Azure AI Foundry evals **alongside** and **inside** AgentEval evals |
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

The interactive menu lets you select a **group** (A–K), then a **sample** within it.

| Group | Focus |
|-------|-------|
| **A — Getting Started** 🔑 real model; `--mock` for an offline walkthrough | Hello World, tool tracking, performance basics, MAF integration patterns |
| **B — Metrics & Quality** | RAG evaluation, quality metrics, judge calibration, responsible AI |
| **C — Workflows & Conversations** | Multi-turn conversations, MAF workflows, source-gen executors |
| **D — Performance & Statistics** | Latency profiling, stochastic evaluation, model comparison, streaming |
| **E — Safety & Security** | Policy guardrails, red team scanning, OWASP compliance |
| **F — Data & Infrastructure** | Snapshot testing, datasets, trace replay, benchmarks, cross-framework |
| **G — Memory Evaluation** | Memory basics, benchmarks, scenarios, DI, cross-session, HTML reports, LongMemEval (ICLR 2025) |
| **H — Benchmarks** | Compliance & performance benchmark families → JSON / HTML / PDF reports |
| **I — Observability (Glass Box)** | Dual-boundary per-turn tracing, trace fidelity, auto-audit |
| **J — Gatekeeper (Runtime Protection)** ★ real model when configured, scripted fallback without one; 11 of 29 always run offline | Fail-closed runtime enforcement: a six-sample 15-minute tour (smallest gate → jailbreak vs authorization → the poisoned-tool kill chain → calibrated Tribunal judges → replay & trust → the HTTP wire), plus attack showcases, architecture proofs, and the consent-gated A2A boundary |
| **K — Agent Skills** 🔑 real agents | Evaluate & govern MAF Agent Skills: a Hello World on-ramp, the disclosure-efficiency metric, the compliance scanner, and the composite Skill Security Index |

See [samples/AgentEval.Samples/README.md](samples/AgentEval.Samples/README.md) for the full listing with per-sample descriptions, timing, and credential requirements.

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

### Contributors

A heartfelt thank you to everyone who has contributed to AgentEval. 🙏

- **[Bernhard Merkle (@bmerkle)](https://github.com/bmerkle)** — *first community contributor*, for making numeric/currency formatting culture-invariant (so scores render as `0.95`, not `0,95`, on comma-decimal locales) and for cleaning up the DocFX documentation build.
- **[Javier Iniesta Fernández (@Javierif)](https://github.com/Javierif)** — *second community contributor*, for adding `--response` / `--response-file` to `agenteval bench agentic`, so you can grade a real agent's response (the command no longer falls back to a built-in answer).

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
