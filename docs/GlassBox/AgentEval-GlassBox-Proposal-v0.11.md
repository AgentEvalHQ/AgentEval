# AgentEval — Glass Box

### Chat-Boundary Evidence, Trace Fidelity, and Runtime Policy Hooks

*From black-box agent evidence to glass-box wire-level truth. Proposal — v0.11 candidate, Fifth Revision.*

| | |
|---|---|
| **Status** | Draft / proposal. Fifth revision — records the decision to consolidate `AgentEval.Cli` into the `AgentEval` repo (deprecating the standalone repo) and publish it as a dotnet tool; scoped to the `AgentEval` repo only. Builds on the Rev 4 codebase grounding (two-layer type system, benchmark-family integration, corrected package model). |
| **Codename** | Glass Box — the throughline is moving AgentEval from recording what the agent *reported* to recording what *actually happened* at the model interface. |
| **Date** | 2026-05-30 |
| **Author** | Jose Luis Latorre Millas (proposal & direction); Claude Opus 4.7 (drafting, codebase grounding & analysis); Daniel (originating idea) |
| **Scope** | AgentEval v0.11.x → v0.13.x, phased delivery, plus AgentEval-repo CLI consolidation |
| **Verified against** | `AgentEvalHQ/AgentEval` — README, `docs/tracing.md`, `docs/assertions.md`, `docs/architecture.md` (incl. ADR-007/008/016/017). MAF 1.3.0, MEAI 10.5.0, .NET 8/9/10 |

---

## 1. Executive Summary

**AgentEval today records what the agent framework reports. Glass Box records what actually happened.** The existing `TraceRecordingAgent` captures one request/response per agent invocation — exactly what the agent boundary chooses to surface. Glass Box adds a second capture layer one level down, at the `Microsoft.Extensions.AI.IChatClient` boundary, where every LLM round-trip is visible verbatim before any agent layer summarises, retries, or filters it.

This revision keeps the architecture of the earlier proposals and corrects them against the real codebase. The headline capability is renamed to **Trace Fidelity** to avoid colliding with the existing RAG `FaithfulnessMetric`: by recording at two layers at once, AgentEval can compare what the framework *reported* against what the model *actually saw* and surface the discrepancies. The runtime **policy gate** is retained — pre-flight blocking is the only control that prevents a payment, deletion, or PII leak, because a post-hoc assertion or audit cannot un-send it.

**Net deliverable:** three MEAI middleware components, one additive bump to the existing `AgentTrace` schema (v1.0 → v1.1), one Mission Control GraphQL extension, one Trace Fidelity benchmark family, a multi-endpoint auto-audit sample, and consolidating + publishing the in-tree CLI as the `AgentEval.Cli` tool. No public-API breaking changes. Phased over v0.11 → v0.13.

> **Why "Glass Box"**
>
> A black box tells you its inputs and outputs. AgentEval is currently a black box over the agent: it reports the agent's own account of the run. Glass Box makes the model interface transparent — every system prompt, tool schema, finish reason, retry, and provider verdict becomes observable evidence. The name is the thesis.

---

## 2. Codebase Grounding

This revision was checked against the public AgentEval source so that every type name, namespace, and integration point in the implementation plan matches what exists today. The facts below are load-bearing for the rest of the document.

### 2.1 What already exists (and is reused, not rebuilt)

| Area | Verified facts |
|---|---|
| Tracing namespace | `AgentEval.Tracing`. Trace types live in `AgentEval.Core`. |
| Trace model | `AgentTrace { Version ("1.0"), TraceName, CapturedAt, AgentName, ModelId, Entries, Performance, Metadata }`. `TraceEntry { Type (TraceEntryType: Request\|Response), Index, Prompt, Text, DurationMs, TokenUsage, ToolCalls, Error, IsStreaming, StreamingChunks }`. |
| Existing recorders | `TraceRecordingAgent` (agent wrapper), `ChatTraceRecorder` (multi-turn conversation: `AddUserTurnAsync` / `GetResult` / `ToAgentTrace`), `WorkflowTraceRecorder`, `TraceReplayingAgent`, `WorkflowTraceReplayingAgent`, `TraceSerializer`, `WorkflowTraceSerializer`. |
| Two evaluation layers (important) | **Metric layer:** `IMetric.EvaluateAsync(EvaluationContext, CT) → MetricResult` (Score 0–100, Passed, Explanation, Details). **Eval/benchmark layer:** `IEval` / `CompositeEval` with `EvaluateAsync(EvalInput, CT) → EvalResult`. `IEvaluator` is the Microsoft.Extensions.AI.Evaluation judge interface. These are distinct — both `EvaluationContext` and `EvalInput` exist. |
| Test-run result type | `TestResult` exposes `ActualOutput`, `ToolUsage` (`ToolUsageReport`), `Performance` (`PerformanceMetrics`), `Score`, `Passed` — hence `result.ToolUsage!.Should()`, `result.Performance!.Should()`, `result.ActualOutput!.Should()`. |
| Metric hierarchy | `IMetric` → `IRAGMetric` (`FaithfulnessMetric`, `RelevanceMetric`, ContextPrecision/Recall, AnswerCorrectness), `IAgenticMetric` (ToolSelection/Arguments/Success/Efficiency, TaskCompletion), `IEmbeddingMetric`, `ISafetyMetric`. `MetricCategory` flags (ADR-007). Custom metrics documented in `docs/extensibility.md`. |
| Benchmark families + registry | `BenchmarkFamilyRegistry` (`AgentEval.Core.Benchmarks`) is the single source of truth; families auto-register via `[ModuleInitializer]`. Eight today: Agentic, GDPR, EU AI Act, OWASP, MITRE, LongMemEval, Memory, Performance. Factory namespace `AgentEval.Benchmarks`, class `{Family}Benchmark`. Shape A (`CompositeEval`-native) or Shape B (custom runner, e.g. LongMemEval/Memory). Adding a family needs **no** CLI or Mission Control changes (ADR-017). |
| Behavioral policies (today) | `NeverCallTool`, `NeverPassArgumentMatching`, `MustConfirmBefore` are **post-hoc** fluent assertions on `result.ToolUsage` — they run after execution, raising `BehavioralPolicyViolationException`. **Not** runtime gates today. |
| Runners & factories | `StochasticRunner`, `ModelComparer`, `WorkflowEvaluationHarness`, `WorkflowTestCase`, `RedTeamRunner`, `MemoryBenchmarkRunner`, `IAgentFactory`, `AzureModelFactory`, `CalibratedJudge` / `VotingStrategy`. `FakeChatClient` (`AgentEval.Core/Testing`) for deterministic tests. |
| Adapters / cross-framework | `ChatClientAgentAdapter : IEvaluableAgent` (what `AsEvaluableAgent()` builds), `MAFAgentAdapter` (AIAgent → IStreamableAgent), `MicrosoftEvaluatorAdapter : IMetric` (wraps MS `IEvaluator`). `services.AddAgentEval()` / `AddAgentEvalAll()`; SK bridge via `AIFunctionFactory.Create()`. |
| Workflow internals | `MAFWorkflowAdapter` (`.FromMAFWorkflow` / `.ExtractGraph` / `.TrackPerformance`), `MAFWorkflowEventBridge` (`.ProcessEventsAsync` / `.StreamEvents`), `WorkflowBuilder` (`.BindAsExecutor` / `.UseEventStreaming`), `WorkflowExecutionResult { ExecutorResults: IReadOnlyDictionary<string, ExecutorResult>, GraphDefinition, ... }`. |
| Mission Control | Hot Chocolate 16 GraphQL + minimal REST + React SPA on `:5000`. Recursive `EvalResult` tree drill-down, compliance matrix with audit-chain badges, evaluator registry. |
| CLI | `agenteval init / doctor / migrate / bench {gdpr,eu-ai-act,agentic} / compliance render / render / mc serve / mc doctor`. Lives in-tree at `src/AgentEval.Cli`; being consolidated into AgentEval and published as a dotnet tool (the standalone `AgentEval.Cli` repo is being deprecated). |
| `.agenteval/` store | One folder per agent/workflow, deterministic run IDs, SHA-256 content hashes on every manifest. Written by CLI/harnesses/benchmarks; read-only for Mission Control. |
| Packaging reality | Ships as **one** NuGet package (`AgentEval` umbrella) over internal projects: Abstractions, Core, DataLoaders, MAF, RedTeam (+ `IsPackable=false` eval/memory/compliance projects bundled via `PrivateAssets=all`). `RootNamespace=AgentEval` everywhere. Surfacing internal projects as separate packages is the maintainers' existing v1.1 roadmap item — not part of Glass Box. |

### 2.2 Corrections this revision makes to the earlier proposal

1. **FaithfulnessEval → Trace Fidelity.** The earlier name collided with the existing RAG `FaithfulnessMetric`. The framework-honesty capability is renamed throughout to `TraceFidelityEvaluator` ("does the reported trace match the observed trace").
2. **`TraceRecordingChatClient` is distinct from `ChatTraceRecorder`.** They are different layers (§4.4). The proposal makes the distinction explicit so the new type is not mistaken for a duplicate.
3. **Schema field is `Version`, not `SchemaVersion`.** The bump is `AgentTrace.Version` "1.0" → "1.1", with additive nullable fields on the real `TraceEntry`.
4. **Two layers, not one — and `EvalInput` does exist.** An earlier revision over-corrected by claiming the input type was always `EvaluationContext`. In fact metrics use `EvaluationContext → MetricResult`, while the eval/benchmark layer uses `EvalInput → EvalResult` via `CompositeEval`/`IEval`. Mission Control's tree node is `EvalResult` (not `ScenarioResult`).
5. **No `ITraceRecorder` interface exists.** Today's recorders are concrete and expose a `.Trace` property. Glass Box follows that pattern rather than inventing an interface (§4.3).
6. **Trace Fidelity is a benchmark family, not a loose "metric or evaluator."** The codebase has one blessed extension path — register with `BenchmarkFamilyRegistry` via `[ModuleInitializer]` (ADR-017). Trace Fidelity ships that way, so `bench trace-fidelity` and Mission Control surfacing come with no CLI changes (§5.3, Phase 3).
7. **The CLI lives in AgentEval.** §10 reflects the decision to consolidate `AgentEval.Cli` into the main repo (deprecating the standalone repo) and publish the in-tree CLI as a dotnet tool.

---

## 3. The Honesty Problem

### 3.1 What AgentEval sees today

`TraceRecordingAgent` wraps an agent and records one Request and one Response per invocation, plus the tool calls the agent surfaces. If a MAF agent runs an internal plan-act-observe loop that hits the LLM four times and the tool runtime three times, the recorder sees **one** top-level call and whatever subset of the loop the framework chose to expose in its final response.

**This is the contract, not a bug.** The agent-boundary recorder gets exactly what the agent boundary exposes. The problem is that the agent boundary *omits by design* — retried turns, intermediate tool failures the agent recovered from, system prompts the framework injected, and the model's per-turn finish reasons are all invisible.

### 3.2 The plain truth

> **We are presenting evidence we did not actually observe.**
>
> When a GDPR audit-grade run writes its evidence today, it says *"this is what happened"* but means *"this is what the agent framework told us happened, after the framework decided what to tell us."* For a black-box behavioural test that is fine. For an auditor who wants the model's reasoning, every tool it considered, every document it retrieved, and every content-filter trigger, it is not honest enough.

### 3.3 The evidence delta

Side-by-side for a realistic four-turn MAF interaction ("Plan a trip to Tokyo" → `SearchFlights` → `SearchHotels` → summarise → answer):

| Evidence | Agent boundary (today) | Chat boundary (Glass Box) |
|---|---|---|
| System prompt | Only if explicitly surfaced | Verbatim, every turn |
| Tool definitions sent to the model | Not captured | Name, description, JSON schema, every turn |
| Per-turn assistant text | Aggregated into the final response | Each of the N turns separately |
| Per-turn finish reason | Not captured | stop / tool_calls / length / content_filter |
| Tool-call request args (model→app) | Whatever the agent surfaces | Exactly as the model emitted |
| Per-turn token usage | Aggregated in `TracePerformance` | Prompt + completion per turn |
| Per-turn latency | Whole-invocation `DurationMs` | Wall-clock per round-trip |
| Provider metadata | Not captured | Azure content-filter verdicts, etc. |
| Retries / partial failures | Swallowed by the agent | Each attempt a separate entry |
| Raw request options | Not captured | Temperature, max tokens, response format |
| RAG documents (as tool messages) | Only if surfaced in tool result | Verbatim, in the conversation |

**Qualitative shift:** from one `TraceEntry` per agent invocation to one per LLM round-trip. For a 4-turn loop, AgentEval today writes ~1 evidence record; with Glass Box it writes ~4.

---

## 4. Chat-Boundary Capture

### 4.1 MEAI 10.5.0 already ships the base types

The earlier proposal assumed `DelegatingChatClient` and `ChatClientBuilder.Use(...)` were missing from MEAI 10.5.0 (AgentEval's pin) and proposed ~80 LOC of hand-rolled scaffolding. That assumption is incorrect:

- `FunctionInvokingChatClient` — already composed by AgentEval — is documented as `public class FunctionInvokingChatClient : Microsoft.Extensions.AI.DelegatingChatClient`, inheriting that base since MEAI 9.10.0.
- `Microsoft.Extensions.AI.OpenAI 10.5.2` examples use `new ChatClientBuilder(client).UseDistributedCache(...).UseFunctionInvocation().Build()`; the builder and its `Use(...)` overloads ship in 10.5.x (docs cite source from 10.4.1).

**Consequence:** the ~80 LOC of shim is never written. `TraceRecordingChatClient` inherits the real `DelegatingChatClient` and composes via the real `ChatClientBuilder`. Verify locally with `dotnet list package --include-transitive` before implementation, but this is settled.

### 4.2 The three middleware layers

| Layer | New type | Project / namespace | What it sees |
|---|---|---|---|
| Chat boundary | `TraceRecordingChatClient : DelegatingChatClient` | `AgentEval.Core` / `AgentEval.Tracing` | Every LLM round-trip verbatim |
| Tool boundary | `EvaluatingAIFunction : AIFunction` | `AgentEval.MAF` | Function execution start / duration / exception |
| Policy seam | `EvalGatingChatClient : DelegatingChatClient` | `AgentEval.Core` / `AgentEval.Guardrails` | Pre/post-flight verdicts on chat turns |

All three target `IChatClient`, so reach extends beyond MAF to Semantic Kernel, custom orchestration, and raw chat loops. Placing the recorder in `AgentEval.Core` (MEAI-only, no MAF dependency) is what makes that reach free.

### 4.3 Implementation sketch (aligned to the real trace model)

Following the existing recorder pattern, the client accumulates real `TraceEntry` instances carrying the new `Scope = TraceEntryScope.ChatTurn` and exposes a `.Trace` (an `AgentTrace`), exactly as `TraceRecordingAgent.Trace` does — no new interface required:

```csharp
namespace AgentEval.Tracing;

public sealed class TraceRecordingChatClient : DelegatingChatClient
{
    private readonly AgentTrace _trace;
    private int _index;

    public TraceRecordingChatClient(IChatClient inner, string agentName, AgentTrace trace)
        : base(inner)
    {
        _trace = trace;
        _trace.AgentName ??= agentName;
        _trace.Version = "1.1";              // additive bump
    }

    public AgentTrace Trace => _trace;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var i = Interlocked.Increment(ref _index) - 1;
        var sw = Stopwatch.StartNew();
        _trace.Entries.Add(ChatTurnEntry.ForRequest(i, messages, options));   // Scope = ChatTurn
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            _trace.Entries.Add(ChatTurnEntry.ForResponse(i, response, sw.ElapsedMilliseconds));
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _trace.Entries.Add(ChatTurnEntry.ForError(i, ex, sw.ElapsedMilliseconds));
            throw;
        }
    }

    // GetStreamingResponseAsync mirrors this, populating TraceEntry.StreamingChunks.
}

// Builder extension, idiomatic MEAI:
public static ChatClientBuilder UseTraceRecording(
    this ChatClientBuilder builder, string agentName, AgentTrace trace,
    SamplePreset preset = SamplePreset.Standard) =>
    builder.Use(inner => new TraceRecordingChatClient(inner, agentName, trace));
```

**Two overrides, ~40 LOC.** Usage: `client.AsBuilder().UseTraceRecording("planner", trace).Build()`. Serialize via the existing `TraceSerializer.SaveToFileAsync(trace, ...)`.

### 4.4 How this differs from the existing `ChatTraceRecorder`

AgentEval already has a `ChatTraceRecorder`. It is not the same thing, and Glass Box does not replace it:

| | `ChatTraceRecorder` (exists) | `TraceRecordingChatClient` (new) |
|---|---|---|
| Wraps | An agent / chat agent | An `IChatClient` |
| Records at | The application conversation level | The model round-trip level |
| Granularity | One entry per user turn you submit (`AddUserTurnAsync`) | One entry per LLM call, including the agent's internal turns the user never issued |
| Sees internal retries / hidden turns | No | Yes |
| Sees tool schemas sent to the model | No | Yes |
| Primary use | Replay a scripted conversation deterministically | Forensic / compliance evidence and Trace Fidelity |

They are complementary. `ChatTraceRecorder` answers "replay this conversation"; `TraceRecordingChatClient` answers "what did the model actually see on every call." The distinction is exactly why both exist.

---

## 5. Trace Fidelity — A New Capability

Once AgentEval records at **both** layers at once — the agent boundary (`TraceRecordingAgent`) and the chat boundary (`TraceRecordingChatClient`) — a capability emerges that neither layer provides alone, and that no competing framework offers.

> **The Trace Fidelity question**
>
> *Does what the agent framework reports it did match what the model actually saw?*

**Naming.** This is deliberately not called "faithfulness." `FaithfulnessMetric` already exists in AgentEval for RAG grounding (is the answer faithful to retrieved context). Trace Fidelity is a different axis entirely — the fidelity of the framework's self-report to the wire — and gets its own name to keep the vocabulary clean.

### 5.1 What the evaluator detects

The chat-boundary trace is ground truth at the model interface — every `FunctionCallContent`, every `ChatMessage` with `ChatRole.Tool`, every finish reason. The agent-boundary trace is the framework's account. `TraceFidelityEvaluator` reconciles the two and flags:

- **Missing tool calls.** Model emitted a tool call; the agent's account omitted it (usually a silent invocation failure).
- **Phantom tool calls.** Agent reports a call the model never requested (often a hardcoded retry path presented as a normal call).
- **Argument drift.** Agent reports args A; the model emitted args B (framework normalisation or post-processing the user did not opt into).
- **Hidden retries.** Chat boundary sees N round-trips; the agent account reports one. The framework retried silently after a tool exception.
- **Token under-reporting.** Sum of per-turn usage at the chat boundary disagrees with the aggregated `TracePerformance` at the agent boundary — material if billing relies on agent-layer numbers.
- **Suppressed finish reason.** Chat boundary sees content_filter; agent boundary reports stop. The framework swallowed a content-filter trigger.

### 5.2 Why it is genuinely new

AgentEval today evaluates *agents*. Trace Fidelity evaluates *agent frameworks* — the integrity of their reporting. RAGAS, DeepEval, PromptFoo, and the Foundry evaluators cannot do this, because none records at two layers simultaneously. The same pair of wrappers that improves compliance evidence doubles as a framework auditor.

### 5.3 Shape and integration — a benchmark family

`TraceFidelityEvaluator` consumes two traces (agent-boundary and chat-boundary) and produces a structured report. It plugs into AgentEval through the one blessed extension path the architecture defines (ADR-017): a **benchmark family** registered with `BenchmarkFamilyRegistry`.

- Factory `public static partial class TraceFidelityBenchmark` in the `AgentEval.Benchmarks` namespace, with presets (`Smoke` / `Standard` / `AuditGrade`).
- Registered via `[ModuleInitializer]` calling `BenchmarkFamilyRegistry.Register(new BenchmarkFamily(...))`, exactly as the eight existing families do. Because it consumes two captured traces rather than a single-shot `EvalInput`, it is a **Shape B** family (custom runner type, `evaluateAsync: null`), following the LongMemEval / Memory precedent.
- Its runner emits an `EvalResult` tree (one sub-result per discrepancy class, each scored 0–100 like `MetricResult`), so the existing audit-chain, `IRunOutputStore`, and Mission Control rendering pick it up with no bespoke wiring.

**CLI for free.** Per ADR-017, `bench --list` reads the registry, so `agenteval bench trace-fidelity --subject MyAgent --preset standard` works **with no changes to `src/AgentEval.Cli`**. The report lands under `.agenteval/`, ready to attach to an upstream issue.

### 5.4 The upstream loop with Microsoft Agent Framework

When the evaluator finds a mismatch, AgentEval holds two things no MAF maintainer currently has: a reproducible scenario, and two parallel evidence streams (reported vs. observed). That is the ideal shape of a bug report. A `trace-fidelity` report can be filed against `microsoft/agent-framework` directly, positioning AgentEval as a contributor to the .NET agent ecosystem rather than a passive consumer.

> **Strategic angle**
>
> AgentEval becomes the only .NET evaluation toolkit that can detect — and upstream-report — framework-level honesty violations. A credible "why AgentEval exists" story that does not depend on out-counting Python frameworks on metrics.

---

## 6. Runtime Policy Gate

AgentEval's behavioural policies today — `NeverCallTool`, `NeverPassArgumentMatching`, `MustConfirmBefore` — are **post-hoc fluent assertions** on `result.ToolUsage`. They run after execution and raise `BehavioralPolicyViolationException`. That is correct for tests, but a test that fails after the agent already called `DeleteAllCustomers` or already returned PII has not prevented anything.

**The gate lets the same policy concepts run inline, at runtime.** `EvalGatingChatClient` evaluates configured checks before the model call (refuse bad input) and after it (redact or block bad output). Three policies:

- `WarnOnly` (default) — record the verdict into the trace, let the call through. Graduates safely from CI to production observability.
- `ThrowOnFail` — throw before/after the call. Opt-in.
- `Redact` — mutate the messages (pre) or response (post) and proceed. Opt-in. Rejected at builder time for streaming responses — bytes already in flight cannot be redacted.

> **Runtime gate vs. the existing post-hoc policies**
>
> AgentEval's `NeverCallTool` / `MustConfirmBefore` / `NeverPassArgumentMatching` run *after* execution as assertions. `EvalGatingChatClient` applies the same intent *before/around* the model call, on live traffic. Same policy concepts, two operating modes; the gate is named for exactly what it does — gating chat — so it never reads as a generic "gatekeeper."

**Every gate decision is written into the `AgentTrace` as evidence** — it becomes part of the compliance record, not just a runtime side effect.

### 6.1 Recommended composition order

```csharp
var client = raw
    .AsBuilder()
    .UseEvalGate(pre: [injectionDetector])     // outermost: refuse bad input first
    .UseTraceRecording("agent", trace)          // record everything that survives
    .UseFunctionInvocation()                    // MEAI tool loop
    .UseEvalGate(post: [piiRedactor])           // innermost gate: redact model output
    .UseDistributedCache(cache)                 // innermost: cached responses still traced
    .Build();
```

---

## 7. The Tool-Execution Wrapper

`IChatClient` middleware sees tool-call *requests* (`FunctionCallContent`) and *results* (`FunctionResultContent` on the next turn). It does **not** see the function execute — that happens inside MAF's runtime or MEAI's `FunctionInvokingChatClient`. To capture execution timing, exceptions, and to block a call, a second wrapper is required, living in `AgentEval.MAF` because tool registration is MAF's surface:

```csharp
namespace AgentEval.MAF.Tracing;

public sealed class EvaluatingAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly AgentTrace _trace;
    private readonly EvalGatePolicy _policy;

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var i = _trace.Entries.Count;
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.InvokeCoreAsync(arguments, cancellationToken);
            _trace.Entries.Add(ToolExecutionEntry.For(i, _inner.Name, arguments, result, sw.ElapsedMilliseconds));
            return result;
        }
        catch (Exception ex)
        {
            _trace.Entries.Add(ToolExecutionEntry.ForError(i, _inner.Name, arguments, ex, sw.ElapsedMilliseconds));
            throw;
        }
    }
}

// Extension: aiFunction.WithEvaluation(trace, policy: EvalGatePolicy.WarnOnly)
```

This closes the one gap `IChatClient` middleware structurally cannot reach. Tool-execution entries correlate to their parent chat-turn entry via the new `CorrelationId` field (Phase 0).

---

## 8. Component Compatibility Matrix

**Guiding principle: Glass Box is additive.** Almost nothing in the existing surface changes. The chat-boundary recorder is a layer beneath the agent boundary, not a replacement. Existing assertions, metrics, runners, recorders, and Mission Control views inherit improved evidence without API changes.

| Component | Status after Glass Box |
|---|---|
| `TraceRecordingAgent` | Unchanged. Stays as the agent-boundary view and outer envelope. |
| `ChatTraceRecorder` | Unchanged. Conversation-level replay recorder; complementary to the new chat-client wrapper (§4.4). |
| `WorkflowTraceRecorder` / `WorkflowTraceReplayingAgent` | Unchanged. v0.11 captures chat-boundary detail per executor via user pre-wiring; v0.12 adds a transparent adapter hook (Phase 5b). |
| `TraceReplayingAgent` / `TraceSerializer` | Unchanged. v1.1 traces deserialize through the existing serializer (additive fields). |
| Fluent assertions (`result.ToolUsage` / `.Performance` / `.ActualOutput`) | Unchanged API. The data behind `ToolUsage` and `Performance` becomes more accurate when sourced from chat-boundary entries. |
| Behavioral policies (`NeverCallTool`, etc.) | Unchanged as post-hoc assertions. The same concepts additionally become available as runtime gates via the gate (§6) — new operating mode, same intent. |
| RAG metrics (`FaithfulnessMetric`, `RelevanceMetric`, `AnswerCorrectnessMetric`) | Unchanged. Inputs become easier to populate — RAG docs arriving as tool messages are captured automatically. |
| Red Team (`RedTeamRunner`, 192 probes) | Unchanged. Probes can additionally target the runtime gate itself — does it block what it claims? |
| Responsible AI (`ToxicityMetric`, `BiasMetric`, `MisinformationMetric`) | Unchanged. Optionally wirable as gate evaluators for pre-flight blocking. |
| Memory (`MemoryBenchmarkRunner`, LongMemEval) | Unchanged. Orthogonal to where chat capture happens. |
| `StochasticRunner` / `ModelComparer` | Unchanged. Per-turn capture adds a phase-level cost/latency breakdown — a new view, not a new API. |
| `WorkflowEvaluationHarness` | Unchanged structure. See Phase 5a/5b for chat-boundary capture inside workflows. |
| Cross-framework (`AsEvaluableAgent`, `AddAgentEval`, SK bridge) | Improved. SK and raw loops get the same forensic detail as MAF, because the recorder is MEAI-only in `AgentEval.Core`. |
| Compliance benchmarks (GDPR, EU AI Act, Agentic) | Structure unchanged; evidence becomes white-box. Phase 6 adds an optional evidence field to merge per-turn verdicts. |
| Exports (JUnit, Markdown, JSON, SARIF, PDF) | Schema-driven — automatically richer once per-turn data lands in v1.1. No exporter changes. |
| `.agenteval/` store | Additive sibling layout for per-turn entries. Existing run files and SHA-256 audit chain untouched. |
| Mission Control (`EvalResult` tree) | One additive GraphQL extension (Phase 7): a chat-turn type plus a `Turns` field on `EvalResult`. Existing queries unaffected. |
| CLI (`init`/`doctor`/`migrate`/`bench`/`render`/`mc`) | Existing commands unchanged. `doctor` gains a double-wrapping check; `migrate` handles v1.0→v1.1; new `bench trace-fidelity` and `bench autoaudit` subcommands. |

### 8.1 What Glass Box does not touch

It does not modify the result model (`ToolUsage` / `Performance` / `ActualOutput`), the fluent assertion DSL, any existing metric, the existing recorders, or the existing `.agenteval/` file layout. Four surfaces are touched additively: `AgentEval.Core` (chat client, gate, evaluator), `AgentEval.MAF` (tool wrapper), `AgentEval.Abstractions` (trace v1.1), and `AgentEval.MissionControl` (GraphQL).

---

## 9. OpenAI-Compatible Endpoint Support

Because every layer targets `IChatClient`, any OpenAI-compatible endpoint works today and continues to work with full chat-boundary recording, gating, and Trace Fidelity. That covers local stacks (Ollama, LM Studio, vLLM, LocalAI, llama.cpp server) and hosted alternatives (Groq, Together, OpenRouter, DeepSeek, Mistral, Fireworks).

### 9.1 Wiring

```csharp
var openaiClient = new OpenAI.Chat.ChatClient(
    model: "llama3.1",
    credential: new ApiKeyCredential("ollama"),
    options: new OpenAIClientOptions { Endpoint = new Uri("http://localhost:11434/v1") });

IChatClient chatClient = openaiClient.AsIChatClient();

var trace = new AgentTrace();
var traced = chatClient.AsBuilder()
    .UseEvalGate(pre: [injectionDetector])
    .UseTraceRecording("LocalLlama", trace)
    .Build();

var agent = traced.AsEvaluableAgent("LocalLlama", systemPrompt);
// All existing AgentEval evaluation now works against this agent.
```

### 9.2 CLI parity

The CLI inherits the same path. One confirmation before Phase 1: verify the CLI provider config supports a generic `openai-compatible` option with `endpoint` + `apiKey`. If today it only switches `azure | openai`, that is a ~10 LOC addition, naturally folded into Phase 10 where the CLI gets attention.

### 9.3 Honest caveats

Provider feature gaps, not AgentEval limitations:

- **Token counts vary.** Hosted endpoints return precise counts; some local backends omit or estimate them. Per-turn cost attribution is exact hosted, partial for some local setups.
- **Content-filter verdicts are Azure-only.** The provider-metadata channel surfacing them is specific to Azure OpenAI; local models will not produce that evidence class.
- **Tool-calling fidelity varies.** Recent local models handle tool calls well; some emit malformed function-call JSON. AgentEval records exactly what was emitted — which is the point.

> **Where Trace Fidelity earns its keep**
>
> Local models disagree with the framework's account in subtler ways than hosted ones — malformed calls silently retried, args normalised, counts reconstructed. **This is where `TraceFidelityEvaluator` finds the most interesting discrepancies,** and comparing fidelity reports across local vs. hosted endpoints is, by itself, a publishable result.

---

## 10. Consolidating the CLI into AgentEval

**Decided direction.** `AgentEval.Cli` is being consolidated into the `AgentEval` repository and the standalone `AgentEval.Cli` repo is being deprecated. This section records how to land that cleanly and finish the job the README already anticipates — publishing the in-tree CLI as a dotnet tool. Everything here concerns the `AgentEval` repo only.

Active CLI development already lives in-tree at `src/AgentEval.Cli` (the README documents running it via `dotnet run --project src/AgentEval.Cli`), so consolidation is mostly confirmation plus packaging — not a migration. The in-tree project becomes the single source of truth, it ships as the `AgentEval.Cli` dotnet tool, and the old standalone repo is retired.

### 10.1 What to do

1. **Make `src/AgentEval.Cli` authoritative.** It already inherits `Directory.Packages.props`, `Directory.Build.props`, `global.json`, and the single CI pipeline — the standard .NET monorepo pattern (dotnet/runtime, dotnet/aspnetcore, dotnet/extensions where MEAI itself lives, microsoft/semantic-kernel). Nothing to move; just declare it canonical.
2. **Publish it as a dotnet tool.** Add `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>agenteval</ToolCommandName>`, `<PackageId>AgentEval.Cli</PackageId>` to the project; add a pack/publish step to the release workflow. First release: `AgentEval.Cli 0.11.0-beta`, installable via `dotnet tool install -g AgentEval.Cli --prerelease`.
3. **Fold the OpenAI-compatible provider switch in here.** If the CLI provider config only switches `azure | openai`, add the generic `openai-compatible` option (`endpoint` + `apiKey`) — the ~10 LOC from §9.2 — so the published tool can target local and hosted endpoints out of the box.
4. **Standardise the docs.** Update README, `docs/cli.md`, and `docs/getting-started.md` to lead with `dotnet tool install -g AgentEval.Cli --prerelease` as the primary install path, with `dotnet run --project src/AgentEval.Cli` as the from-source fallback.

### 10.2 Resulting package surface (corrected to reality)

**AgentEval ships as one NuGet package today** — the `AgentEval` umbrella over internal projects (Abstractions, Core, DataLoaders, MAF, RedTeam, plus `IsPackable=false` eval/memory/compliance projects bundled via `PrivateAssets=all`). Glass Box does not change that. It adds exactly one new published artifact — the CLI tool — and lands its code inside the existing internal projects.

| Artifact | Change under Glass Box |
|---|---|
| `AgentEval` (umbrella NuGet package) | Unchanged as the single library package. New code lands in its internal projects: chat client / gate / Trace Fidelity in Core, tool wrapper in MAF, trace v1.1 in Abstractions. |
| `AgentEval.Cli` (new dotnet tool package) | The in-tree `src/AgentEval.Cli`, published as a tool (`PackAsTool`). This is the only new published package. |
| `AgentEval.MissionControl` | Remains a runnable app (`mc serve` / docker), not a library package. Gains the additive GraphQL ChatTurn type. |
| Internal eval/memory/compliance projects | Stay `IsPackable=false`. Surfacing them as standalone packages is the maintainers' separate v1.1 roadmap item — explicitly out of scope for Glass Box. |

**Land it as ADRs.** AgentEval records architecture decisions (ADR-007, -008, -016, -017). Two Glass Box decisions merit the same treatment: an ADR for chat-boundary tracing and the two-layer recording model, and an ADR for the `AgentTrace` v1.1 schema. Following the project's own process is the fastest route to maintainer sign-off.

---

## 11. Implementation Proposal — by Layer and Phase

Each phase is independently shippable. Phases 0–4 and 8–10 are v0.11. Phases 5b and 6 are v0.12. Phase 7 is v0.13. Effort assumes one engineer familiar with the codebase.

### Phase 0 — AgentTrace v1.1 (`AgentEval.Abstractions` / `AgentEval.Core`)

**Effort:** ~0.5 day.

Deliverables:

- Bump `AgentTrace.Version` "1.0" → "1.1".
- Add additive nullable fields to `TraceEntry`: `Scope` (`TraceEntryScope`: AgentInvocation | ChatTurn | ToolExecution; default AgentInvocation preserves v1.0 semantics), `CorrelationId`, `SystemPrompt`, `ToolDefinitions`, `FinishReason`, `RequestOptions`, `ProviderMetadata`.
- Extend `TraceSerializer` with a v1.0 back-compat read path; existing v1.0 files continue to load.
- Tool-definition de-dupe helper (hash first occurrence) — **disabled in AuditGrade preset** since auditors want verbatim payloads.

**Acceptance:** existing v1.0 trace fixtures deserialize unchanged; round-trip of a v1.1 trace is stable; Mission Control reads both versions.

### Phase 1 — TraceRecordingChatClient (`AgentEval.Core` / `AgentEval.Tracing`)

**Effort:** ~1.5 days.

Deliverables:

- `TraceRecordingChatClient : DelegatingChatClient` — two overrides, `.Trace` property, streaming via `StreamingChunks` (§4.3).
- Extension `ChatClientBuilder.UseTraceRecording(agentName, trace, preset)`.
- Docs subsection distinguishing it from `ChatTraceRecorder` (§4.4) to prevent confusion.

**Acceptance:** a 4-turn MAF sample produces a v1.1 trace with one ChatTurn entry per LLM round-trip; diff vs. the agent-boundary trace shows ≥7 of the 11 evidence classes in §3.3 appearing for the first time; <2 ms p99 overhead on a no-op client.

### Phase 2 — EvaluatingAIFunction (`AgentEval.MAF`)

**Effort:** ~1 day.

Deliverables:

- `EvaluatingAIFunction : AIFunction` (§7); extension `AIFunction.WithEvaluation(trace, policy)`; ToolExecution entries correlated to parent ChatTurn via `CorrelationId`.

**Acceptance:** a wrapped tool records start, duration, args, result, and exceptions; correlation to the parent turn verified.

### Phase 3 — TraceFidelityBenchmark (`AgentEval.Core` + benchmark registry)

**Effort:** ~1 day. New capability.

Deliverables:

- A `TraceFidelityRunner` reconciling agent-boundary and chat-boundary traces, emitting the six discrepancy classes (§5.1), each scored 0–100 with a severity.
- Factory `TraceFidelityBenchmark` (namespace `AgentEval.Benchmarks`) with Smoke/Standard/AuditGrade presets; Shape-B registration via `[ModuleInitializer]` → `BenchmarkFamilyRegistry.Register(...)`. Runner emits an `EvalResult` tree.
- CLI `bench trace-fidelity` appears automatically from the registry — **no** `src/AgentEval.Cli` changes (ADR-017).
- Contract-test inclusion: add to `BenchmarkNamespaceContractTests` and a registry integration test, per the ADR-017 walkthrough.

**Acceptance:** using `FakeChatClient` to script a silent-retry tool failure, the runner emits a HiddenRetry discrepancy at High severity; `bench --list` shows the family; the canonical suite covers one probe per discrepancy class.

### Phase 4 — EvalGatingChatClient (`AgentEval.Core` / `AgentEval.Guardrails`)

**Effort:** ~1.5 days.

Deliverables:

- `EvalGatingChatClient : DelegatingChatClient` with pre/post hooks; `EvalGatePolicy` (WarnOnly | ThrowOnFail | Redact, default WarnOnly); `EvalGateRefusalException`; streaming + Redact rejected at builder time; every decision written to the AgentTrace.

**Acceptance:** an injection pre-gate at ThrowOnFail blocks a known probe before the model call; a PII post-gate at Redact scrubs a known SSN pattern; WarnOnly never short-circuits.

### Phase 5 — Workflow integration (`AgentEval.MAF`)

**Effort:** ~1 day docs (5a, v0.11) + ~2 days adapter hook (5b, v0.12).

**5a — user pre-wiring (v0.11):** document the supported pattern — wrap each executor's `IChatClient` with `UseTraceRecording` before handing it to `WorkflowBuilder`. No AgentEval code change.

**5b — transparent hook (v0.12):** extend `MAFWorkflowAdapter` / `MAFWorkflowEventBridge` (which `WorkflowTraceRecorder` already builds on) to inject chat-boundary recording per executor automatically, populating each `ExecutorResult`. Trade-off: relies on adapter-level event fidelity; not byte-identical to direct chat-boundary capture.

### Phase 6 — Compliance evidence plumbing (Compliance suites)

**Effort:** ~1 day.

Deliverables:

- Surface per-turn gate/fidelity verdicts into GDPR (Art. 32) and EU AI Act (Art. 14) evidence packs, hash-anchored into the existing audit chain. Additive evidence field; backwards-compatible.

### Phase 7 — Mission Control GraphQL (`AgentEval.MissionControl`)

**Effort:** ~1 day.

Deliverables:

- New `ChatTurn` GraphQL type (role, content, tool definitions, usage, finish reason, latency, provider metadata); additive `Turns` field on `EvalResult`; recursive resolver; per-turn timeline drill-down in the React SPA.

### Phase 8 — CLI, doctor, H11 sample

**Effort:** ~1 day.

Deliverables:

- Extend `agenteval doctor` to warn on double-wrapping (`TraceRecordingAgent` + `UseTraceRecording` on the same agent) and `agenteval migrate` to handle v1.0 → v1.1.
- Sample `Observability/01_GlassBoxFullStack` — per-turn tracing + injection pre-gate + PII post-gate + a tool blocked by `EvaluatingAIFunction`, following the existing grouped-sample convention.

### Phase 9 — Auto-audit sample (multi-endpoint, full-stack)

**Effort:** ~1.5 days. The flagship demo.

**Scenario.** The existing TripPlanner → FlightReservation → HotelReservation → Presenter workflow, run against four endpoints in parallel — two local (Ollama-Llama3.1, LM Studio Qwen) and two hosted (Azure GPT-4o-mini, DeepSeek-V3). Each executor pre-wired with recording + gates; tools wrapped with `EvaluatingAIFunction`. Full stack applied per run:

- **`WorkflowEvaluationHarness`** (existing) — executor order, edges, tools across the graph.
- **GDPR / EU AI Act** (existing) — now with white-box per-turn evidence.
- **`RedTeamRunner`** (existing) — per executor.
- **`TraceFidelityEvaluator`** (Phase 3) — per executor; most revealing on local models.
- **`StochasticRunner` / `ModelComparer`** (existing) — N runs per endpoint for confidence.

**CLI:** `agenteval bench autoaudit --workflow <file> --endpoints <yml> --preset audit-grade --runs 5`, producing a per-endpoint and a cross-endpoint comparison report (Markdown + PDF + SARIF), all surfaced in Mission Control.

**Acceptance:** a single invocation produces a comparison report ranking the four endpoints on workflow correctness, compliance, fidelity, red-team resistance, and cost; under 15 min on Standard, under 90 min on AuditGrade ×5.

> **Why this matters strategically**
>
> **"Benchmark local vs. hosted multi-agent workflows for compliance, honesty, and cost — end-to-end, in .NET, in one command."** That is an AgentCon Zürich keynote, an MVP Summit demo, and a Foundry partner pitch. The pieces exist; the auto-audit sample wires them into one runnable artifact.

### Phase 10 — Consolidate + publish the CLI (AgentEval repo)

**Effort:** ~0.5 day. Plan in §10.

Deliverables:

- Declare `src/AgentEval.Cli` authoritative; configure it as a dotnet tool (`PackAsTool`, `ToolCommandName=agenteval`, `PackageId=AgentEval.Cli`); add publish to the release workflow; ship `AgentEval.Cli 0.11.0-beta`.
- Add the generic `openai-compatible` provider switch (§9.2) if absent; deprecate the standalone repo; standardise docs on `dotnet tool install -g AgentEval.Cli --prerelease`.

**Acceptance:** the tool installs and runs init/doctor/bench/mc as documented; all packages build and pack in one CI run.

### Phase summary

| Phase | Layer | Effort | Release |
|---|---|---|---|
| 0 — Trace v1.1 | Abstractions / Core | 0.5 d | v0.11 |
| 1 — Chat recorder | Core / Tracing | 1.5 d | v0.11 |
| 2 — Tool wrapper | MAF | 1 d | v0.11 |
| 3 — Trace Fidelity | Core | 1 d | v0.11 |
| 4 — Policy gate | Core / Guardrails | 1.5 d | v0.11 |
| 5a — Workflow pre-wiring docs | MAF (docs) | 1 d | v0.11 |
| 5b — Workflow adapter hook | MAF | 2 d | v0.12 |
| 6 — Compliance plumbing | Compliance suites | 1 d | v0.12 |
| 7 — Mission Control GraphQL | MissionControl | 1 d | v0.13 |
| 8 — CLI / doctor / sample | Cli + Samples | 1 d | v0.11 |
| 9 — Auto-audit sample | Cli + Samples | 1.5 d | v0.11 |
| 10 — Consolidate + publish CLI | Repo + Cli | 0.5 d | v0.11 |

**v0.11 total: ~8.5 days. v0.12: ~3 days. v0.13: ~1 day. Grand total ~12.5 days** — including Trace Fidelity, the gate, the flagship auto-audit sample, and consolidating + shipping the CLI tool. Realistic across a release cycle.

---

## 12. Value Improvement

### 12.1 Before / after, by capability

| Capability | Today | After Glass Box |
|---|---|---|
| Compliance evidence | Black-box: prompt + final answer; the auditor trusts AgentEval's summary. | White-box: every LLM round-trip, tool schema, finish reason, and Azure content-filter verdict, hash-anchored into the existing audit chain. |
| Cost attribution | Whole-agent totals in `TracePerformance`. | Per-turn: planning vs. execution cost, per-tool overhead, per-judge cost. "Planning costs 4x execution" becomes assertable. |
| Framework reach | MAF + `ChatClientAgentAdapter`-wrapped clients. | Anything on `IChatClient`: SK, custom orchestration, raw loops. Same metrics, no rewiring. |
| RAG document capture | Manual threading into `EvaluationContext`. | Automatic — docs arriving as tool messages are captured verbatim for `FaithfulnessMetric` and `RelevanceMetric`. |
| Runtime safety | Post-hoc assertions (`NeverCallTool`, etc.) fail after the action already happened. | Pre-flight block, post-flight redaction, tool-call refusal — the same policy intent, applied inline before damage. |
| Framework honesty (new) | No capability; AgentEval reports what the framework reports. | `TraceFidelityEvaluator` detects missing/phantom calls, hidden retries, argument drift, token under-reporting, suppressed finish reasons — and produces upstream-ready reports. |
| Reproducibility | Request options not captured. | Temperature, max tokens, response format captured per turn; fail an audit run when settings drift across checkpoints. |

### 12.2 Strategic positioning

1. **The only .NET toolkit that audits the framework itself.** Trace Fidelity is structurally novel; no two-layer recorder exists elsewhere, so no competitor can detect framework dishonesty. A defensible reason to exist that does not depend on metric-count parity.
2. **Same intent, grading and policing.** The behavioural-policy concepts you already assert in CI become runtime gates with no conceptual relearning — a clean path from test framework to safety layer.
3. **Audit-grade evidence that is actually audit-grade.** The compliance reporters keep their hash-chained packaging; the substance upgrades from the agent's summary to what the model saw.
4. **Framework-agnostic by construction.** Recorder in `AgentEval.Core` (MEAI-only) means SK and raw-loop users get the same surface. AgentEval stops being "the MAF toolkit" and becomes "the .NET toolkit that integrates especially well with MAF."
5. **Upstream contributor.** The Trace Fidelity → MAF-issue loop makes AgentEval a contributor to the .NET agent ecosystem — reputational value for an MVP-led project.

### 12.3 Positioning line

> *AgentEval no longer asks the agent framework what happened. Glass Box watches what actually happened — at the model interface, at the tool boundary, at every retry the framework swallowed. The same evaluators that grade your CI now police production traffic, and audit the agent framework itself.*

---

## 13. Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Trace volume multiplies (~4x for a 4-turn loop) | Tool-definition de-dupe in Smoke/Standard presets (off in AuditGrade); accumulate into the `AgentTrace` and serialize once; an opt-in per-turn detail flag for non-audit runs. |
| Double-wrapping (`TraceRecordingAgent` + `UseTraceRecording`) | `agenteval doctor` warns on detection; documentation makes the chat-client wrapper the source of truth and the agent wrapper the outer envelope; §4.4 distinguishes both recorders. |
| Confusion with `ChatTraceRecorder` | Explicit §4.4 contrast table; the new type is clearly the `IChatClient` round-trip recorder, not the conversation replay recorder. |
| Naming collision with `FaithfulnessMetric` | Resolved up front: the framework-honesty capability is Trace Fidelity / `TraceFidelityEvaluator`, never "faithfulness." |
| Policy gate misused as sole safety layer | WarnOnly default; ThrowOnFail/Redact opt-in; streaming + Redact rejected at build; docs: pair with provider moderation; every decision auditable in the trace. |
| Workflow opacity in v0.11 | v0.11 ships the pre-wiring pattern (works today); v0.12 adds the adapter hook; release notes flag this as the main fit-gap. |
| Trace Fidelity as Shape B vs Shape A | Shape B (custom runner) fits the two-trace input cleanly and matches LongMemEval; if a single-shot `EvalInput` envelope proves expressible, Shape A is a later simplification. Either way it is a registered benchmark family — the integration surface does not change. |
| MEAI base-type assumption | Verified against MEAI docs and the OpenAI connector; re-confirm with `dotnet list package --include-transitive` before Phase 1. |
| AIFunction override surface | `EvaluatingAIFunction` overrides `InvokeCoreAsync(AIFunctionArguments, CancellationToken)`; confirm the exact `AIFunction` member set on the pinned MAF/MEAI version before Phase 2 (it has shifted across MEAI previews). |

---

## 14. Open Questions

1. **Per-turn detail opt-in.** Should v1.1 always capture full per-turn detail, or gate it behind a flag for non-audit runs to control trace size? Recommendation: capture always for AuditGrade, flag-gated otherwise.
2. **Cross-layer correlation.** Propagate `CorrelationId` via AsyncLocal across the agent → chat → tool nesting? It flows correctly under normal MAF threading; confirm under parallel tool execution.
3. **Trace Fidelity scope for v0.11.** MAF-only canonical scenarios first, or include SK and raw loops? Recommendation: MAF first — the upstream-issue loop is the highest-value path.
4. **Trace Fidelity scoring rubric.** How to weight the six discrepancy classes into a single 0–100 family score — equal weight, or severity-weighted (a suppressed content-filter outranks a token-count delta)? Recommendation: severity-weighted, pinned by a divergence test like the OWASP preset tests.
5. **Preset propagation.** Explicit preset parameter per wrapper (recommended, debuggable) vs. an ambient AsyncLocal preset.

---

## 15. Recommendation

**Accept and sequence as Glass Box.**

1. **v0.11.0-beta —** Phases 0–4, 5a, 8, 9, 10. Ship the three middleware components, Trace Fidelity, the auto-audit flagship, and the consolidated + published CLI tool. ~8.5 days.
2. **v0.12.0 —** Phase 5b (workflow adapter hook) + Phase 6 (compliance plumbing). ~3 days.
3. **v0.13.0 —** Phase 7 (Mission Control per-turn drill-down). ~1 day.

The architecture from the earlier proposals holds: layer placement in `AgentEval.Core`, a two-layer recording model, `EvaluatingAIFunction` in `AgentEval.MAF`, and an additive schema bump. This revision makes it correct against the codebase: Trace Fidelity replaces the colliding "faithfulness" name, the real `AgentTrace`/`TraceEntry` model is used, `TraceRecordingChatClient` is distinguished from `ChatTraceRecorder`, the runtime gate is distinguished from the existing post-hoc policy assertions, and the CLI plan reflects the decision to consolidate `AgentEval.Cli` into the AgentEval repo and publish it as a tool. Trace Fidelity is placed where the architecture wants new evaluation capability — a benchmark family on `BenchmarkFamilyRegistry` — and the whole change is best landed as ADRs in the project's own decision log.

**The honest summary:** two new `DelegatingChatClient`s + one `AIFunction` wrapper + one trace-pair benchmark family + a flagship multi-endpoint sample + consolidating and publishing the in-tree CLI, all additive, no public-API breaks, schema bumped from 1.0 to 1.1. Glass Box turns AgentEval from "a toolkit that audits what agents report" into "a platform that records what actually happened, polices what is about to happen, audits the framework itself, and benchmarks any `IChatClient` — local or hosted — end-to-end in one command."

*End of proposal.*
