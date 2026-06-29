# Using AgentEval with MAF's Evaluation Feature

> **Microsoft Agent Framework ships a built-in agent-evaluation feature**
> (`agent.EvaluateAsync(...)`). AgentEval plugs into it — score a MAF agent with AgentEval metrics, or
> a whole AgentEval benchmark composite, in one call, and render the result as a self-contained HTML
> report. The adapters that make this work ship in **`AgentEval.MAF`** (part of the `AgentEval` NuGet
> package), not in a sample.

**.NET only.** AgentEval ships as .NET NuGet packages; there is no Python binding. This guide targets
`Microsoft.Agents.AI` (.NET). MAF's Python evaluation story is out of scope.

A runnable end-to-end reference lives in
[`samples/AgentEval.MafEvalLightPath`](../samples/AgentEval.MafEvalLightPath).

---

## 1. `IEvaluator` vs `IAgentEvaluator` — in plain words

MAF's `agent.EvaluateAsync` accepts two kinds of evaluator. The difference matters, so here it is
simply:

- **`IEvaluator`** (`Microsoft.Extensions.AI.Evaluation`) is the **cross-vendor, one-item** contract.
  MAF hands it a finished `(messages, response)` and it returns scores. It's generic — usable by any
  MEAI tooling, not just MAF.
- **`IAgentEvaluator`** (`Microsoft.Agents.AI`) is **MAF's own, batch** contract. MAF hands it the raw
  `EvalItem` objects — each carrying the **full** conversation (`EvalItem.Conversation`), the tools
  that were available (`EvalItem.Tools`), expected outputs, expected tool calls, etc.

**Analogy:** grading a student with `IEvaluator` is like seeing only the exam *question* and their
*final answer*. Grading with `IAgentEvaluator` is like having the full worked solution in front of
you — every step (every tool call) is visible.

**Why this changes tool-metric results.** When you pass an `IEvaluator`, MAF wraps it in an internal
adapter that *splits* each conversation and forwards only the query half + the final response — the
middle, where the agent actually **called the tools**, is dropped. So a code-based metric that checks
"did it call `SearchFlights`?" sees nothing → **0%**. When you implement `IAgentEvaluator`, MAF gives
you the whole `EvalItem`; AgentEval's `AgentEvalAgentEvaluator` forwards `EvalItem.Conversation`
(tool-call turns included), so the code metric sees the real calls → **100%**. (LLM-judged metrics
grade the answer text and work either way.)

**Recommendation:** use the native path.

```csharp
using AgentEval.MAF.Evaluators;

var evaluator = AgentEvalEvaluators.Quality(judge).AsAgentEvaluator(chatConfig, name: "AgentEval");
AgentEvaluationResults results = await agent.EvaluateAsync(queries, evaluator);   // native overload
```

`AsAgentEvaluator` (in `AgentEval.MAF`) wraps any AgentEval MEAI `IEvaluator` as MAF's
`IAgentEvaluator`. MAF's own samples use this same `agent.EvaluateAsync(queries, evaluator)` shape with
`FoundryEvals` (cloud) or `LocalEvaluator` (boolean checks); AgentEval is the richer drop-in.

---

## 2. Prerequisites

```bash
dotnet add package AgentEval --prerelease          # brings AgentEval.MAF (+ Core/Abstractions)
# (the AgentEval package already depends on a Microsoft.Agents.AI version that ships this feature)
```

```bash
export AZURE_OPENAI_ENDPOINT=...    AZURE_OPENAI_API_KEY=...    AZURE_OPENAI_DEPLOYMENT=gpt-4o-mini
```

```csharp
var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
AIAgent agent = azure.GetChatClient(deployment).AsIChatClient().AsAIAgent(
    name: "TravelAgent", instructions: "...", tools: [ /* AIFunctionFactory.Create(...) */ ]);

IChatClient judge = azure.GetChatClient(deployment).AsIChatClient();
var chatConfig = new ChatConfiguration(judge);     // the judge AgentEval's LLM-as-judge metrics use
```

> The full **AgenticBenchmark composite** (§3c) uses the agentic suite (`AgentEval.Evals.Agentic`), which ships
> inside the `AgentEval` package, so this path works from the NuGet package too (in this repo it resolves via a
> project reference). The flat-metric path (§3a/b) needs only the core `AgentEval` types.

---

## 3. What you can run

Build any AgentEval evaluator, call `.AsAgentEvaluator(chatConfig)`, pass to `agent.EvaluateAsync`.

### 3a. A single metric / a preset bundle

```csharp
var metrics = AgentEvalEvaluators.Quality(judge);     // or Relevance(judge), RAG(judge), Safety(judge),
                                                      //    Agentic(["SearchFlights"]), Advanced(judge)
var results = await agent.EvaluateAsync([query], metrics.AsAgentEvaluator(chatConfig));
```

### 3b. A custom flat composite

```csharp
var metrics = AgentEvalEvaluators.Custom(
    new ToolSuccessMetric(), new ToolSelectionMetric(["SearchFlights", "SearchHotels"]),
    new TaskCompletionMetric(judge), new RelevanceMetric(judge), new CoherenceMetric(judge));
var results = await agent.EvaluateAsync([query], metrics.AsAgentEvaluator(chatConfig));
// → one scored leaf per metric: Tool Success · Tool Selection · Task Completion · Relevance · Coherence
```

### 3c. A FULL AgenticBenchmark composite (weighted tree + thresholds)

```csharp
using AgentEval.Benchmarks;   // AgenticBenchmark
using AgentEval.Core;         // ChatClientEvaluator

var composite      = AgenticBenchmark.ToolCallAccuracy(new ChatClientEvaluator(judge), judgeModel: deployment);
var compositeEval  = composite.AsMeaiEvaluator();                       // IEval -> MEAI IEvaluator
var results        = await agent.EvaluateAsync([query], compositeEval.AsAgentEvaluator(chatConfig));

EvalResult tree = compositeEval.CapturedResults[0];                     // full hierarchy, for HTML
// Tool Call Accuracy → Tool Selection / Input(Schema+Semantic) / Output / Success / Efficiency
```

---

## 4. Getting an HTML report out

| Source | Bridge (`AgentEval.MAF.Evaluators`) | Result |
|--------|--------------------------------------|--------|
| Flat metrics | `MeaiToEvalResultBridge.Build(name, queries, results, judgeModel)` | one node per query → one leaf per metric |
| Full composite | `AgentEvalCompositeEvaluator.CapturedResults` | the native composite tree, full hierarchy |

```csharp
byte[] html = await new HtmlEvalResultRenderer().RenderAsync(tree, new EvalResultRenderOptions(
    Subject: new SubjectIdentity(SubjectKind.Agent, agent.Name!, ModelId: deployment, Framework: "MAF"),
    Title: "MAF agent.EvaluateAsync() — AgentEval"));
await File.WriteAllBytesAsync("report.html", html);   // self-contained: inline CSS, no JS, no CDN
```

---

## 5. Fidelity table

When MAF runs the agent it builds an `EvalItem` whose `.Conversation` is the **full** transcript
(query + every response message, incl. assistant tool-call and tool-result turns) and whose `.Tools`
lists the tool definitions.

| Metric kind | Native `IAgentEvaluator` (`AsAgentEvaluator`) | MEAI `IEvaluator` overload |
|-------------|-----------------------------------------------|----------------------------|
| LLM-judged (Relevance, Coherence, TaskCompletion, AgenticBenchmark sub-evals) | ✅ | ✅ |
| Code-based tool metrics (`ToolSuccessMetric`, `ToolSelectionMetric`) | ✅ sees the real calls | ⚠️ `ToolSelection` → 0% |
| Timing / TTFT / cost | ⚠️ not in the conversation | ⚠️ |

**Proven in the sample:** the same agent answer scored `ToolSelection` **100%** via the native adapter
and **0%** via the MEAI-only path. For timing/cost fidelity use AgentEval's deep path
(`MAFEvaluationHarness`, which instruments the run directly).

---

## 6. The AgentEval.MAF surface

| Type (`AgentEval.MAF.Evaluators`) | Role |
|---|---|
| `AgentEvalEvaluators` | factory for AgentEval metrics as MEAI `IEvaluator` (`Quality/RAG/Safety/Agentic/Custom/…`) |
| `AgentEvalAgentEvaluator` | wraps an MEAI `IEvaluator` as MAF's native `IAgentEvaluator` (forwards full conversation) |
| `AgentEvalCompositeEvaluator` | wraps an AgentEval `IEval`/composite as an MEAI `IEvaluator`; captures the `EvalResult` tree |
| `MeaiToEvalResultBridge` | `AgentEvaluationResults` (MEAI) → AgentEval `EvalResult` tree, for rendering |
| `AgentEvaluatorExtensions` | `.AsAgentEvaluator(chatConfig)` / `.AsMeaiEvaluator()` fluent helpers |

Mapping to MAF's own samples (`dotnet/samples/.../Evaluation/`): `Evaluation_SimpleEval` →
`AgentEvalEvaluators.Quality(judge)`; `Evaluation_CustomEvals` → `AgentEvalEvaluators.Custom(...)`;
both via `agent.EvaluateAsync(queries, evaluator.AsAgentEvaluator(chatConfig))`.

---

## 7. Run the reference sample

```bash
dotnet run --project samples/AgentEval.MafEvalLightPath                 # flat + composite, opens both HTMLs
dotnet run --project samples/AgentEval.MafEvalLightPath -- --flat-only
dotnet run --project samples/AgentEval.MafEvalLightPath -- --composite-only
dotnet run --project samples/AgentEval.MafEvalLightPath -- --no-open    # CI/scripted
```

---

## 8. Interface & method analysis (MAF eval API vs AgentEval)

Source: the Microsoft Agent Framework packages + [MS Learn — agent-framework evaluation (C#)](https://learn.microsoft.com/en-us/agent-framework/agents/evaluation?pivots=programming-language-csharp).

### 8.1 The type map — and where AgentEval plugs in

| MAF / MEAI type | Namespace (package) | What it is | AgentEval counterpart |
|---|---|---|---|
| `IAgentEvaluator` | `Microsoft.Agents.AI` | **MAF's native** batch evaluator contract | **`AgentEvalAgentEvaluator`** implements it |
| `LocalEvaluator` + `EvalChecks` / `FunctionEvaluator` | `Microsoft.Agents.AI` | local boolean checks (`KeywordCheck`, `ToolCalledCheck`, `FunctionEvaluator.Create`) | scored metrics instead — `AgentEvalEvaluators.*` |
| `FoundryEvals` | `Microsoft.Agents.AI.Foundry` (cloud) | LLM-as-judge via the **Azure AI Foundry** service | **AgentEval is the local equivalent** — see 8.4 |
| `MeaiEvaluatorAdapter` *(internal)* | `Microsoft.Agents.AI` | adapts an MEAI `IEvaluator` → `IAgentEvaluator` | `AgentEvalAgentEvaluator` is our public, conversation-preserving version |
| `IEvaluator`, `CompositeEvaluator`, `RelevanceEvaluator`, … | `Microsoft.Extensions.AI.Evaluation(.Quality/.Safety)` | cross-vendor evaluators (see 8.3) | `AgentEvalEvaluator` / `AgentEvalCompositeEvaluator` implement `IEvaluator` |
| `LoopEvaluator` family (`AIJudgeLoopEvaluator`, `TodoCompletionLoopEvaluator`, …) | `Microsoft.Agents.AI` | **agent-loop continuation** evaluators (when to stop iterating) — a *different* feature, not output scoring | n/a |

**Do we return `AgentEvaluationResults`?** Yes. `AgentEvalAgentEvaluator.EvaluateAsync(...)` returns
`AgentEvaluationResults` (built via its public ctor), and `agent.EvaluateAsync(queries, evaluator)`
therefore returns `Task<AgentEvaluationResults>` — identical to `LocalEvaluator`/`FoundryEvals`.
`AgentEvaluationResults` exposes `Items` (one MEAI `EvaluationResult` per query), `Passed/Failed/Total/
AllPassed`, `RunId`, `ReportUrl`, `SubResults` (per-agent, for workflows), and `AssertAllPassed()`.

### 8.2 `IAgentEvaluator` — and its AgentEval analogue

```csharp
// MAF (Microsoft.Agents.AI)
public interface IAgentEvaluator {
    string Name { get; }
    Task<AgentEvaluationResults> EvaluateAsync(IReadOnlyList<EvalItem> items, string evalName = …, CancellationToken ct = default);
}
// EvalItem: Query, Response, Conversation (full), Tools, Context, ExpectedOutput, ExpectedToolCalls, RawResponse, Splitter
```

```csharp
// AgentEval (AgentEval.Evals) — the structurally-equivalent abstraction
public interface IEval {
    string Key { get; } string Name { get; } string Category { get; } string Version { get; }
    Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default);
}
// EvalInput: Query, Response, Context, GroundTruth, ToolCalls, ToolDefinitions, ExpectedActions, SystemMessage, Metadata
```

**Similarities (they rhyme):**
- A normalized input container: MAF `EvalItem` ≈ AgentEval `EvalInput` (both carry query/response/tools/expected-*).
- One interface for atomic **and** composite: MAF — `LocalEvaluator`/`FoundryEvals`/composite all implement `IAgentEvaluator`; AgentEval — `AtomicLlmEval`/`CompositeEval` both implement `IEval`.
- Batch orchestration via an extension on the agent (`agent.EvaluateAsync`).

**Differences:** MAF's `IAgentEvaluator` is **batch** (`IReadOnlyList<EvalItem>`) and aggregates into
pass/fail counts (`AgentEvaluationResults`). AgentEval's `IEval` is **single-input** and aggregates via
a pluggable `IAggregationStrategy` (e.g. `WeightedSum`) into a **weighted, thresholded `EvalResult`
tree** with severity. AgentEval models the *scoring math*; MAF models the *batch + provider plumbing*.

### 8.3 "CompositeEvaluator" — three different things (clearing up the name clash)

| Type | Where | Composition model |
|---|---|---|
| `CompositeEvaluator` | **MEAI** (`Microsoft.Extensions.AI.Evaluation`) — **not** MAF, **not** Foundry | **Flat merge**: runs N `IEvaluator`s, unions their metrics into one `EvaluationResult`. No weights, thresholds, or hierarchy. |
| `AgentEvalEvaluator` | AgentEval (`AgentEval.MAF.Evaluators`) | **Flat merge** of AgentEval metrics → one MEAI `EvaluationResult`. **This is our direct analogue of MEAI's `CompositeEvaluator`.** |
| **`AgentEvalCompositeEvaluator`** | AgentEval (`AgentEval.MAF.Evaluators`) | **Weighted hierarchical tree**: wraps an AgentEval `CompositeEval` (`AgenticBenchmark` preset) — weights + thresholds + nested sub-composites + a roll-up verdict, captured as a full `EvalResult` tree. |

So MS Learn's `new CompositeEvaluator(new RelevanceEvaluator(), new CoherenceEvaluator(), …)` is the
**MEAI flat merger** — the peer of `AgentEvalEvaluator`, *not* of `AgentEvalCompositeEvaluator`. Mine is
strictly richer: it preserves the weighted multi-level hierarchy (root → Tool Call Accuracy → Tool
Selection / Input(Schema+Semantic) / …) that a flat merger cannot represent. Class shapes:

```csharp
// MEAI flat (peer of AgentEvalEvaluator)
new CompositeEvaluator(params IEvaluator[] evaluators) : IEvaluator   // EvaluationMetricNames = union; one EvaluationResult

// AgentEval weighted tree
public sealed class AgentEvalCompositeEvaluator : IEvaluator {        // (Microsoft.Extensions.AI.Evaluation.IEvaluator)
    public AgentEvalCompositeEvaluator(IEval composite);             // wraps a CompositeEval / AgenticBenchmark preset
    public IReadOnlyList<EvalResult> CapturedResults { get; }        // the full weighted tree, per item
    public IReadOnlyCollection<string> EvaluationMetricNames { get; }
    public ValueTask<EvaluationResult> EvaluateAsync(IEnumerable<ChatMessage>, ChatResponse, ChatConfiguration?, …);
}
```

### 8.4 Evaluator coverage — AgentEval ≈ a local `FoundryEvals`

`FoundryEvals` exposes the Foundry evaluator universe (Agent behavior: `intent_resolution`,
`task_adherence`, `task_completion`, `task_navigation_efficiency`; Tool usage: `tool_call_accuracy`,
`tool_selection`, `tool_input_accuracy`, `tool_output_utilization`, `tool_call_success`; Quality:
`coherence`, `fluency`, `relevance`, `groundedness`, `response_completeness`, `similarity`; Safety).
**AgentEval re-implements the same universe locally** (`AgentEval.Evals.Agentic` + `AgentEval.Metrics.*`,
same names, same rubrics) as LLM-as-judge over any `IChatClient` — **no Azure AI Foundry project
required**. Net: pick `FoundryEvals` for the managed cloud service + portal; pick AgentEval for the same
metrics offline/self-hosted, with weighted composites, thresholds, and the HTML/PDF/audit-chain reports.

### 8.5 Workflow evaluation — complementary, not competing

```csharp
// MAF (Microsoft.Agents.AI.Workflows)
public static Task<AgentEvaluationResults> EvaluateAsync(this Run run, IAgentEvaluator evaluator,
    bool includeOverall = true, bool includePerAgent = true, string evalName = "Workflow Eval",
    IConversationSplitter? splitter = null, string? expectedOutput = null, CancellationToken ct = default);
```

MAF's `run.EvaluateAsync` walks the run's event stream (`ExecutorInvokedEvent` /
`AgentResponseEvent` / `ExecutorCompletedEvent`), extracts **each sub-agent's interaction**, scores it
plus the **overall** output with the *same* `IAgentEvaluator`, and returns `AgentEvaluationResults`
with a per-agent `SubResults` dictionary. It is **quality / LLM-judge scoring per agent**.

AgentEval's `WorkflowEvaluationHarness` + `WorkflowTestCase` answer a **different** question —
**structure & trajectory**: `ExpectedExecutors`, `StrictExecutorOrder`, `HaveTraversedEdge`,
`ExpectedTools`, `MaxDuration`, `HaveNoToolErrors` — deterministic graph assertions, no LLM.

**They compose:** use AgentEval's harness to assert the workflow *ran correctly*, and pass an
**AgentEval evaluator into MAF's `run.EvaluateAsync`** to score *how good each agent's output was* —
`await run.EvaluateAsync(AgentEvalEvaluators.Quality(judge).AsAgentEvaluator(chatConfig))` → per-agent
AgentEval quality scores in `SubResults`.
