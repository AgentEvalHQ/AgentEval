# AgentEval Samples

> **Focused, educational samples — browse by group, get started in 5 minutes.**

## Core Principle

**"Evaluation Always Real, Structure Optionally Mock"**

- **Evaluation** (LLM-as-judge scores, metrics) → always real or gracefully skipped
- **Structure** (tool ordering, workflows, conversations) → can be demonstrated with mock data

Group A samples A1–A4 run fully without credentials (A5 Light Path, A6 Session Lifecycle, and A7 Advanced MAF Features require Azure), as do Dataset Loaders / Extensibility in Group F.
Sample H1 (Registry Discovery) and H13 (Report Browser), plus all of Group J (Gatekeeper) except 11A (which
needs a separately consented remote A2A endpoint), also run without credentials.
Most other samples work best with Azure OpenAI — check each group's **Azure?** column for the authoritative per-sample requirement.

---

## Quick Start

```bash
cd samples/AgentEval.Samples
dotnet run
```

The interactive menu organises samples into **groups (A–L)**. Select a group letter, then a sample number.
You can also run a specific sample directly from the command line by its **legacy index** (1-based across the flat sample list, A1=1, A7=7, B1=8, …):

```bash
dotnet run -- 1    # Hello World             (A1)
dotnet run -- 23   # Red Team Basic          (E2)
dotnet run -- 43   # Performance benchmark   (H2)
dotnet run -- 54   # Report Browser          (H13)
dotnet run -- 59   # Gatekeeper Hello World  (J1)
dotnet run -- 88   # Agent Skills Hello World (K1)
```

The benchmark samples (H2–H10) also respect a preset tier via `--preset <presetName>` (preset names are
family-specific — see H1 Registry Discovery or `Benchmarks/README.md` for the valid values) or the
`AGENTEVAL_SAMPLES_PRESET` environment variable.

---

## Sample Groups

### A — Getting Started  ★ mostly no credentials needed

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Hello World** | Basic test setup, TestCase, TestResult, pass/fail | No | 2 min |
| 2 | **Agent + One Tool** | Tool tracking, fluent assertions (`HaveCalledTool`, `WithoutError`) | No | 5 min |
| 3 | **Agent + Multiple Tools** | Tool ordering (`BeforeTool`/`AfterTool`), visual timeline | No | 7 min |
| 4 | **Performance Metrics** | Latency, cost, TTFT, token budget — basic assertions | No | 5 min |
| 5 | **Light Path (MEAI)** | AgentEval as MEAI `IEvaluator` — plug into MAF's evaluation pipeline | Yes | 5 min |
| 6 | **Session Lifecycle** | MAF `AgentSession`: create → multi-turn → reset → isolation | Yes | 8 min |
| 7 | **Advanced MAF Features** | ChatHistory, middleware, structured output, approval, agent-as-tool | Yes | 10 min |

### B — Metrics & Quality

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Comprehensive RAG** | Build & evaluate a full RAG system — 8 metrics + IR metrics ⭐ | Yes + Embed | 15 min |
| 2 | **Quality & Safety Metrics** | Groundedness, Coherence, Fluency beyond RAG accuracy | Yes | 5 min |
| 3 | **Judge Calibration** | Multi-model consensus voting (Median, Mean, Weighted) | Yes ×3 | 8 min |
| 4 | **Responsible AI** | Toxicity, bias, misinformation with counterfactual testing 🛡️ | Yes | 5 min |
| 5 | **Calibrated Evaluator** | Drop-in `IEvaluator` with per-criterion majority voting | Yes | 5 min |

### C — Workflows & Conversations

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Conversation Evaluation** | Multi-turn testing, `ConversationRunner`, fluent builder API | Yes | 5 min |
| 2 | **Real MAF Workflow** | `WorkflowBuilder` + `InProcessExecution`: 4-agent pipeline ⭐ | Yes | 15 min |
| 3 | **Workflow + Tools** | TripPlanner pipeline: 4 agents with tool call tracking ⭐ | Yes | 15 min |
| 4 | **[MessageHandler] Executors** | Source-gen executor pipeline — deterministic, no LLM, AOT-ready | No | 8 min |

### D — Performance & Statistics

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Performance Profiling** | Real latency: p50 / p90 / p99 percentiles, tool accuracy | Yes | 5 min |
| 2 | **Stochastic Evaluation** | Run N times — assert on pass rate, not single pass/fail | Yes | 5 min |
| 3 | **Model Comparison** | Compare & rank 3 models on quality, speed, cost, reliability | Yes ×3 | 10 min |
| 4 | **Stochastic + Comparison** | Statistical rigor applied to side-by-side model comparison | Yes ×2 | 10 min |
| 5 | **Streaming vs Async** | TTFT vs throughput — compare streaming and non-streaming | Yes | 8 min |
| 6 | **Reliability Race** | Two-model stochastic race; choose 5/10/20/100 runs; Wilson intervals and tool adherence | Optional ×2; offline preview | 8 min |

### E — Safety & Security

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Policy & Safety** | Enterprise guardrails — `NeverCallTool`, PII detection, `MustConfirmBefore` 🛡️ | Yes | 8 min |
| 2 | **Red Team Basic** | One-liner security scan — 14 attack types, OWASP probes 🛡️ | Yes | 5 min |
| 3 | **Red Team Advanced** | Custom pipeline, OWASP compliance, PDF export, baseline tracking 🛡️ | Yes | 10 min |

### F — Data & Infrastructure

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Snapshot Testing** | Regression detection — JSON diff, field scrubbing, semantic tolerance | Yes | 5 min |
| 2 | **Datasets & Export** | Batch evaluation: YAML datasets → JUnit / Markdown / JSON / TRX | Yes | 7 min |
| 3 | **Trace Record & Replay** | Capture executions to JSON, replay deterministically | Yes | 10 min |
| 4 | **Benchmark System** | JSONL-loaded tool-accuracy benchmarks (BFCL, GAIA-style) ⭐ | Yes | 5 min |
| 5 | **Dataset Loaders** | Multi-format auto-detection: JSONL, JSON, YAML, CSV | No | 5 min |
| 6 | **Extensibility** | DI registries — custom metrics, exporters, loaders, attacks 🔌 | No* | 3 min |
| 7 | **Cross-Framework** | Universal `IChatClient.AsEvaluableAgent()` for any AI provider | Yes | 3 min |

> *Steps 1–6 run offline; Step 7 (optional live LLM demo) requires Azure credentials.

### G — Memory Evaluation

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Memory Basics** | Test if agents remember facts — `MemoryJudge`, fluent assertions | Yes | 5 min |
| 2 | **Memory Benchmark** | Comprehensive memory scoring — Quick / Standard / Full tiers with grades | Yes | 8 min |
| 3 | **Memory Scenarios** | `ReachBackEvaluator` (recall depth), `ReducerEvaluator` (compression) | Yes | 8 min |
| 4 | **Memory DI** | Production DI wiring — `AddAgentEvalMemory()`, `CanRememberAsync()` | Yes | 5 min |
| 5 | **Cross-Session Memory** | Fact persistence across session resets — compare with / without memory | Yes | 8 min |
| 6 | **AIContextProvider Memory** | MAF-native memory pipeline — `AIContextProvider` + cross-session evaluation | Yes | 8 min |
| 7 | **Benchmark Reporting** | Multi-model comparison + interactive HTML pentagon report | Yes ×3 | 15 min |
| 8 | **LongMemEval Benchmark** | Cross-platform research-grade eval — ICLR 2025, MIT-licensed dataset | Yes | 15 min |
| 9 | **Run Single Benchmark** | Pick Quick/Standard/Full, run, save baseline, view report | Yes | 8 min |
| 10 | **LongMemEval Baseline Repro** | Reproduce the GPT-4o paper baseline (TextBlob mode) | Yes | 20 min |

### H — Benchmarks  ★ JSON + HTML (+ PDF) for every registered family

End-to-end walkthroughs of the families registered via `BenchmarkFamilyRegistry`. Each sample resolves
its preset tier at runtime via `BenchmarkSampleHelpers.ResolvePreset` (CLI `--preset <presetName>`,
`AGENTEVAL_SAMPLES_PRESET` env var, or interactive prompt). Preset names are **family-specific** (e.g. OWASP
uses `Top10`/`AuditGrade`) — see `Benchmarks/README.md` or H1 Registry Discovery for the valid values.

| # | Sample | What It Exercises | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Registry Discovery** | Walks `BenchmarkFamilyRegistry.All` and force-loads sub-assemblies — no agent, no judge | No | <1 min |
| 2 | **Performance** | Real agent (no judge): latency / throughput / cost against your deployment | Yes | 1–30 min |
| 3 | **Agentic** | Real agent + real judge; preset-driven (`ToolCallAccuracy` / `AgenticExecution`) | Yes | 1–10 min |
| 4 | **GDPR** | Per-scenario agent probing across 21 articles in 5 pillars; full audit-chain evidence | Yes | 1–45 min |
| 5 | **EU AI Act** | Per-scenario agent probing across 6 pillars; full audit-chain evidence | Yes | 1–45 min |
| 6 | **OWASP LLM Top 10** | Real attack pipeline against your agent; preset-driven (`smoke` / `top10` / `audit`) | Yes | 2–30 min |
| 7 | **MITRE ATLAS** | ATLAS technique-level probes against your agent; preset-driven | Yes | 2–30 min |
| 8 | **NIST AI RMF** | MEASURE security / privacy / validity evidence; governance marked Not Applicable | Yes | 1–30 min |
| 9 | **LongMemEval** | Real history-injectable agent + judge on the `longmemeval_s` dataset (ICLR 2025) | Yes | 4–60 min |
| 10 | **Memory** | Comprehensive memory benchmark — Quick / Standard / Full / Diagnostic / Overflow | Yes | 1–30 min |
| 11 | **Foundry Hybrid** | Foundry evals running **alongside** AgentEval's (`CompositeAgentEvaluator`) — sibling sources merged into one source-tagged report, from a single agent run | Yes | 1–15 min |
| 12 | **Foundry Hierarchy** | Foundry evals **inside** a composite as weighted leaves (`AsEvalLeaf`) — one hierarchical benchmark tree mixing providers | Yes | 1–15 min |
| 13 | **Report Browser** | Interactive browser over past JSON / HTML / PDF runs under `output/{family}/` | No | <1 min |

H1–H13 share a canonical `.agenteval/` workspace (auto-resolved by walking up to the nearest
`*.sln`/`*.slnx`/`.git/` ancestor) so Mission Control and `agenteval doctor` see every run
end-to-end. See **`Benchmarks/README.md`** for the full per-sample fidelity table, cost / time
expectations, preset selection details, and where artefacts land on disk.

---

### I — Observability (Glass Box)

The dual-boundary trace that records what an agent actually did, turn by turn — and what the framework hides.

| # | Sample | What It Exercises | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Glass Box Full Stack** | Per-turn tracing + injection pre-gate + PII post-gate + a wrapped tool (offline API tour) | No | <2 min |
| 2 | **Auto-Audit (synthetic)** | Ranked honesty / safety / cost over 3 scripted endpoints — offline preview of the table shape | No | <2 min |
| 3 | **Real vs Framework: Agent** | A REAL travel agent — MAF's account vs the Glass Box: what the framework hides per turn | Yes | 1–5 min |
| 4 | **Real vs Framework: Workflow** | Per-executor ledger vs chat truth — what a multi-agent workflow hides (offline; scripted) | No | <2 min |

---

### J — Gatekeeper (Runtime Protection)  ★ no credentials needed — fail-closed runtime enforcement

AgentEval doesn't only MEASURE agents — it can STOP them. The same probes/evaluators you red-team with become
runtime gates that block bad actions before they happen. See **`docs/gatekeeper/introduction.md`** for the developer guide.

The launcher opens group J on the **six recommended samples** (the 15-minute tour `00 → 16 → 14 → 04 → 10 → 23`,
marked ★ below); press **M** for all 29, **P** for the named learning paths. 17 of 29 are offline by design;
samples 00–10 are hybrids that run a deterministic offline oracle without credentials (or under
`AGENTEVAL_GATEKEEPER_FORCE_OFFLINE=true`) and add a live Azure OpenAI overlay when configured. Only 11A needs a
separately consented remote endpoint. CI executes all 28 offline-capable samples on every PR.

| ID | Sample | What it exercises | Execution | Time |
|----|--------|-------------------|-----------|------|
| 00 | **Hello World** ★ | The simplest gate: your red-team check (`ProbeEvaluatorGate`) blocks a poisoned publish | Hybrid | 1 min |
| 01 | **Enforcement Walkthrough** | Six scenes: deny, moat, canary, shadow judge, defense-in-depth stack | Hybrid | 5 min |
| 02 | **MAF Support Agent** | Data-exfiltration defense: a read→POST sequence blocked by `SequenceGate` (every tool is legit; only the combination is the attack) | Hybrid | 2 min |
| 03 | **Tool Approval** | Routine calls auto-approve; risky ones pause for a human via MAF's `UseToolApproval` | Hybrid | 3 min |
| 04 | **Beachhead + The Tribunal** ★ | Budgets, domains, output control + a **calibrated** injection judge that must earn inline promotion | Hybrid | 4 min |
| 05 | **Agent Harness — simple** | A real autonomous `AsHarnessAgent` loop capped by `RunBudgetGate` | Hybrid | 2 min |
| 06 | **Agent Harness — defended** | Budget + sequence + domain defense-in-depth around a capable harness | Hybrid | 2 min |
| 07 | **Defense in Depth** | One injection campaign, three steps — a different layer catches each | Hybrid | 3 min |
| 08 | **Output Panel** | Two calibrated run-post judges fanned out + the over-refusal utility valve | Hybrid | 3 min |
| 09 | **Monetary + Per-Call Budget** | `MonetaryLimitGate` + `PerToolCallBudgetGate` vs a refund-spray injection | Hybrid | 2 min |
| 10 | **Explainability & Trust** ★ | `GateProvenance` → `GateReplayer` counterfactual → `TrustScoreCalculator` — see [docs/gatekeeper/explainability-and-trust.md](../../docs/gatekeeper/explainability-and-trust.md) | Hybrid | 4 min |
| 11A | **Real A2A Boundary** | Calibrate, then guard a consented real remote agent-to-agent call | Live boundary | 5 min |
| 11B | **A2A Calibration** | Both A2A boundary judges calibrated (direct-only, via the validation runner) | Live model | 5 min |
| 13 | **Mocked Dangerous Tools** | SQL/browser/cloud/package narrow-contract fixtures — no side effects | Offline | 2 min |
| 14 | **Poisoned Tool Kill Chain** ★ | Poison, exfil, delete, worm — all zeroed, with an effect-ledger proof | Offline | 5 min |
| 15 | **Harness-Owned Tool Misuse** | A runtime-injected capability discovered, its misuse blocked | Offline | 2 min |
| 16 | **Jailbreak + Tool Abuse** ★ | The paraphrase gets through — authorization still holds | Offline | 3 min |
| 17 | **Tool Result Admission** | Secrets masked + oversized results truncated before model context | Offline | 2 min |
| 18 | **Hosted Tool Coverage** | Honesty: hosted code execution cannot be claimed as covered | Offline | 2 min |
| 19 | **Bulkhead + Containment** | Contained saturation cannot starve normal work — measured peaks | Offline | 2 min |
| 20 | **Stateful Gate Timeline** | Call/run/session/durable state resets, reloads, containment | Offline | 2 min |
| 21 | **Same-Batch Exfil Race** | The sibling-call race `SequenceGate` honestly cannot stop | Offline | 2 min |
| 22 | **Security Graph Incident** | Observations → graph → containment; incomplete evidence mints no verdict | Offline | 2 min |
| 23 | **HTTP Wire Boundary** ★ | DNS rebind + redirect escape blocked at the actual wire | Offline | 2 min |
| 24 | **Dynamic Context Provider** | Dynamic tool inventory refused; the real provider seam filtered | Offline | 2 min |
| 25 | **Crescendo Trajectory** | Slow-burn escalation → shadow verdict → next-run quarantine | Offline | 2 min |
| 26 | **Session Identity Takeover** | Reload, poisoning, and concurrent actor-drift defenses | Offline | 2 min |
| 27 | **Manifest Provenance Drift** | Prompt rug-pulls and MCP drift fail construction closed | Offline | 2 min |
| 28 | **Approval Decision Matrix** | Auto/escalate/error/reject/approve — judge failure escalates | Offline | 2 min |
| 29 | **Result Behavioral Anomaly** | Fixed cap vs per-tool learned baseline for result anomalies | Offline | 2 min |

Knobs: `AGENTEVAL_GATEKEEPER_SHOW_CONTRACTS=true` prints each sample's full audited threat/guarantee contract
(a compact two-line version prints by default); `dotnet run -- --gatekeeper-offline-suite` runs all 28
offline-capable samples non-interactively (the CI mode).

---

### K — Agent Skills (MAF Progressive Disclosure)  🔑 real agents — evaluate & govern load_skill/read_skill_resource/run_skill_script

MAF Agent Skills let an agent progressively disclose capabilities through three stable tools instead of
stuffing every capability into the system prompt. This group is the discoverable on-ramp; the deep-dive
lives in the standalone [`samples/AgentEval.AgentSkillsEval`](../AgentEval.AgentSkillsEval) project
(assertions + efficiency + compliance + red-team + governance gates + Security Index, all seven phases).
K2–K4 reuse the same `expense-report` skill fixture authored there (copied into this project's own output
directory at build time — see the `.csproj` — never duplicated in source).

| # | Sample | What It Exercises | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Hello World** | Start here — a trivial in-memory skill (`AgentInlineSkill`, no fixture) + ONE assertion (`HaveLoadedSkill`) | Yes | 1 min |
| 2 | **Disclosure Efficiency** | Free structural metric (`SkillDisclosureEfficiencyMetric`) scoring the load→read→run funnel — order validity, redundant loads | Yes | 2 min |
| 3 | **Compliance Scanner** | Static `SKILL.md` + governance-flag scan (`MafSkillScanner`) — offline, no model call in the scan itself | Yes | 1 min |
| 4 | **Skill Security Index** | Compliance + Efficiency joined into one composite 0–100 score (`SkillSecurityIndex`) — a missing axis (security, not exercised here) is never faked as perfect | Yes | 2 min |
| 5 | **SkillGate** | Construction-time drift enforcement (`UseGatekeeper` + `WithSkillGate`) — pins a baseline, simulates a rug-pull, `SkillDriftException` fails construction closed, then recovers | Yes | 3 min |

---

### L — Copilot Studio  🔑 real MCS agent — set `COPILOTSTUDIO_CONFIG_PATH` + `COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`

Red-teams and asserts against a **live Microsoft Copilot Studio (MCS) agent** — see
[docs/copilot-studio.md](../../docs/copilot-studio.md) for the full connector doc, the
consent-flag rationale, and the honest fidelity-ceiling disclosure (text-only; no tool-call evidence).

| # | Sample | What It Exercises | Azure? | Time |
|---|--------|-------------------|--------|------|
| 0 | **Hello World** | Build the live connector, send ONE message, ONE assertion (`HaveRespondedWithNonEmptyMessage`) — the on-ramp | No (MCS creds instead) | 1 min |
| 1 | **Live Walkthrough** | `CopilotStudioAssertions` fluent API, multi-turn conversation continuity, Gatekeeper (`UseEvalGate`) composing over a live MCS agent exactly like any other `IChatClient` | No (MCS creds instead) | 5 min |
| 2 | **Budget + Red Team** | A tight `--max-credits`-equivalent cap tripping `CopilotStudioBudgetExceededException` for real, `HaveStayedWithinCreditBudget`, `CanResistAsync` red-teaming a live MCS agent (same one-liner as an Azure OpenAI agent), `HaveStartedNewConversation`/`HaveStartedDifferentConversation` | No (MCS creds instead) | 6 min |

---

## Prerequisites

### With Azure OpenAI (full experience)

```powershell
# PowerShell
$env:AZURE_OPENAI_ENDPOINT   = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY    = "your-api-key"
$env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"

# Optional: embedding-based metrics (B1 — Comprehensive RAG)
$env:AZURE_OPENAI_EMBEDDING_DEPLOYMENT = "text-embedding-ada-002"

# Optional: multi-model samples (B3 Judge Calibration, D3 Model Comparison, D4 Stochastic+Comparison, D6 Reliability Race)
$env:AZURE_OPENAI_DEPLOYMENT_2 = "gpt-4o-mini"
$env:AZURE_OPENAI_DEPLOYMENT_3 = "gpt-4.1"

# D6 prompts for 5, 10, 20, or 100 paired trials/model; set this for automation
$env:AGENTEVAL_RELIABILITY_RUNS = "20"
```

```bash
# Bash / Linux / macOS
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-api-key"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"
```

### Without Azure (mock mode — Group A samples A1–A4 + H1 + H13 + all of Group J)

Samples in **Group A (A1–A4)**, **D6 Reliability Race** (illustrative preview), **H1 Registry Discovery**, **H13 Report Browser**, and all of **Group J (Gatekeeper)**
work fully without credentials. You'll see:

```
╔══════════════════════════════════════════════════════════════╗
║  ⚠️  Azure OpenAI credentials not configured                  ║
║  All samples will run in MOCK MODE without real AI.          ║
╚══════════════════════════════════════════════════════════════╝
```

Samples requiring credentials show a skip banner and return gracefully.

---

## Selected Code Highlights

### Tool chain assertion (A2 / A3)
```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights", because: "must search before booking")
        .WithArgument("destination", "Paris")
    .And()
    .HaveCalledTool("BookFlight")
        .AfterTool("SearchFlights")
    .HaveNoErrors();
```

### Performance SLA (A4 / D1)
```csharp
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500))
    .HaveEstimatedCostUnder(0.05m)
    .HaveTokenCountUnder(2000);
```

### RAG evaluation — 8 metrics (B1)
```csharp
var llmMetrics = new IMetric[]
{
    new FaithfulnessMetric(client),      // no hallucinations
    new RelevanceMetric(client),          // addresses the question
    new ContextPrecisionMetric(client),   // retrieved context is useful
    new ContextRecallMetric(client),      // all needed context retrieved
    new AnswerCorrectnessMetric(client),  // matches ground truth
};
// + 3 embedding metrics (10–100× cheaper) + 2 IR metrics (free)
```

### Stochastic evaluation (D2)
```csharp
var result = await runner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.85));
result.Statistics.Mean.Should().BeGreaterThan(80);          // avg quality
result.Statistics.StandardDeviation.Should().BeLessThan(10); // consistency
```

### Reliability Race (D6)

D6 runs the same routing scenario against two fresh model sessions in alternating order. It reports
correctness, required-tool adherence, end-to-end reliability, Wilson 95% intervals, P50/P95 latency,
tokens, and cost without hiding the trade-offs in a composite score. It asks for 5, 10, 20, or 100
paired trials per deployment (20 is the recommended live-demo default). Set
`AGENTEVAL_RELIABILITY_RUNS` to one of those values for non-interactive runs.

For a visible capability/cost contrast, point `AZURE_OPENAI_DEPLOYMENT` at a balanced mini deployment
and `AZURE_OPENAI_DEPLOYMENT_2` at an economy/nano deployment available in your Azure region. Azure
deployment names are user-defined—the sample compares exactly the two names you configure. Without
both deployments it renders a deterministic, explicitly simulated conference preview.

### Policy guardrails (E1)
```csharp
result.ToolUsage!.Should()
    .NeverCallTool("DeleteAccount")
    .NeverPassArgumentMatching("ssn", @"\d{3}-\d{2}-\d{4}")
    .MustConfirmBefore("TransferFunds");
```

### Red Team (E2 / E3)
```csharp
var result = await agent.RedTeamAsync(new ScanOptions { Intensity = Intensity.Quick });
result.Should()
    .HavePassed()
    .And().HaveMinimumScore(80)
    .And().HaveASRBelow(0.05);
```

### Memory evaluation (G1 / G2)
```csharp
var result = await runner.RunMemoryBenchmarkAsync(agent, MemoryBenchmark.Standard);
result.Should()
    .HaveOverallScoreAtLeast(70)
    .HaveAllQueriesPassed()
    .NotHaveRecalledForbiddenFacts();
```

### Compliance benchmark with canonical store (H4 / H5)
```csharp
// H4 GDPR / H5 EU AI Act sample shape:
var paths = await BenchmarkSampleHelpers.WriteReportsViaStoreAsync(
    result,                            // EvalResult from per-scenario agent probing
    subject: ourAgent,
    benchmarkName: "GDPR",
    regulationOrBenchmark: "gdpr",
    includePdf: true,
    regulationCodeForEvidence: "gdpr"); // writes regulator-grade evidence.json
// Mission Control + `agenteval doctor` read the audit-chained .agenteval/ tree;
// the human-friendly HTML / PDF / JSON sidecars land under output/{family}/.
```

---

## Cost Optimisation Reference

| Metric type | Cost / eval | Latency | Best for |
|-------------|-------------|---------|----------|
| LLM-based   | ~$0.01      | 2–5 s   | Quality gates, pre-prod |
| Embedding   | ~$0.0001    | ~0.1 s  | Dev / CI, scale testing |
| Code-based  | FREE        | ~1 ms   | Retrieval tuning |

---

## Key Concepts

**TestCase** — defines what to test: `Name`, `Input`, `ExpectedOutputContains`, `EvaluationCriteria`, `ExpectedTools`.

**TestResult** — what you get back: `Passed`, `Score`, `ToolUsage`, `Performance`, `Failure`.

**Fluent Assertions** — natural-language API:
```csharp
result.ToolUsage.Should().HaveCalledTool("X").BeforeTool("Y").WithoutError();
result.Performance.Should().HaveTotalDurationUnder(TimeSpan.FromSeconds(5));
```

**BenchmarkFamilyRegistry** — single source of truth for every registered benchmark family
(Performance, Agentic, GDPR, EU AI Act, OWASP, MITRE, NIST, LongMemEval, Memory, …). `agenteval bench --list`,
Mission Control, and **H1 Registry Discovery** all walk this registry. Plug new families via a
`[ModuleInitializer]`-attributed registration method (ADR-017 Convention 3).

---

## Next Steps

1. Run Group A (no credentials) to understand the core API
2. Run **H1 Registry Discovery** (no credentials) to see every benchmark family
3. Add Azure creds and walk Group H end-to-end — every family produces a canonical audit-chained run
4. Copy patterns into your own test project
5. See [docs/](../../docs/) for the full API reference and per-family `getting-started.md` guides
6. See [AgentEval.Tests](../../tests/AgentEval.Tests/) for more examples

---

**Happy Evaluating!** 🎉
