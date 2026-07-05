# Glass Box

**Frameworks narrate their own execution. Glass Box checks the story against what actually happened.**

A modern agent framework hands you a tidy account of a run — the tokens it used, the tools it called, the
finish reasons, the final answer. But that account is the *framework's self-report*. When the framework
silently retries a call, drops a tool result, under-reports tokens, or a provider safety filter fires, the
self-report can quietly diverge from what the model actually saw at the chat interface. Glass Box records
**both boundaries** and reconciles them, so you can trust the numbers you evaluate on.

## The two-layer model

Glass Box captures a run at two levels (ADR-019, ADR-020):

| Layer | What it sees | Analogy |
|---|---|---|
| **Agent boundary** | What the framework *reports* — the `AgentRunResponse`, its usage, its tool accounting | the framework's press release |
| **Chat / tool boundary** | What the *model and tools actually saw* — every `ChatMessage` request/response, every tool invocation, real token usage, real finish reasons | the raw footage |

Both are recorded into a single **`AgentTrace`** (the dual-boundary schema, ADR-020) whose entries are tagged with a
`TraceEntryScope` — `AgentInvocation`, `ChatTurn`, or `ToolExecution`. That one dual-boundary trace is the
substrate everything else reads.

## Trace Fidelity — reconciling the two accounts

**Trace Fidelity** answers: *does the framework's story match the footage?* It reconciles the agent-boundary
report against chat-boundary truth and flags the divergences that matter — missing/phantom tool calls, hidden
retries, argument drift, **token under-reporting**, and **suppressed finish reasons**.

- **Single agent** — the [`trace-fidelity`](benchmarks/trace-fidelity.md) benchmark reconciles one
  agent-boundary trace against one chat-boundary trace.
- **Workflows** — the [`workflow-trace-fidelity`](benchmarks/workflow-trace-fidelity.md) benchmark does it
  **per executor**: it sums each executor's framework-reported ledger and compares it to that executor's chat
  truth, reporting `Agree` / `TokenMismatch` / `FinishMismatch` / `NoTruth` for each. Assert it in tests with
  `result.Should().HaveTraceFidelity(chatTraces)`.

Under-reporting and suppressed finishes are treated as deceptions; over-reporting and sub-2% rounding noise are
not. Executors without chat truth are reported as `NoTruth` and **excluded** from the aggregate, so an untraced
majority can never mask a real divergence.

## What Glass Box makes evaluable — the diagnostics evaluators

Because the dual-boundary trace records what the model *actually* saw, a family of pure-code evaluators can
read signals that were previously invisible. Run them as the **`glass-box-diagnostics`** preset:

```
agenteval bench agentic --preset glass-box-diagnostics --subject my-agent --trace run.trace.json
```

| Evaluator | Reads | Flags |
|---|---|---|
| **Tool Reliability** | tool-execution successes | the least-reliable tool's success rate |
| **Tool Error Pattern** | tool-execution errors | a failure mode concentrated across calls |
| **Safety Intervention** | per-turn `content_filter` finishes | how often a provider guardrail fired |
| **Truncation Detection** | per-turn `length` finishes | responses cut off by the output-token cap |
| **System Prompt Drift** | per-turn system prompts | the system prompt silently changing mid-run |
| **System Prompt Injection** | system prompt vs. a trusted baseline (or an LLM judge) | injected/subverted instructions |
| **Argument Sanitization** | wire-level tool arguments | PII/secret content reaching the tool boundary |
| **Token Distribution** | per-turn completion tokens | one turn dominating the run (a formatting loop) |

Each evaluator **Skips** (and is excluded from the composite) when no trace is attached — so the preset is only
meaningful on a `--trace`-attached run. Attach a captured trace and all eight become live; **System Prompt
Injection** is the one judge-capable leaf — it uses an LLM judge when one is supplied and no trusted baseline is
present, and otherwise compares against the baseline deterministically (or skips).

## The runtime gate

Glass Box is not only post-hoc. The same chat-boundary interception powers a **runtime gate** — an
injection pre-gate and a PII post-gate can inspect the real request/response at the model interface and block a
turn before a bad action reaches a tool. See the `Glass Box Full Stack` sample (Observability group, item 1).

## The workflow live-truth caveat (Path-2)

Inside a live MAF `InProcessExecution` workflow, executor responses are **not** currently routed back through
the instrumented chat client, so a live run yields per-executor traces **without** chat-boundary `ChatTurn`
entries. Until an upstream per-executor forwarding hook lands (tracked with MAF; see #3075), live workflow runs
report every executor as `NoTruth`; real per-executor reconciliation works today for **direct-agent, pre-wired,
or replayed** traces (the offline `Real vs Framework: Workflow` sample, Observability item 4, shows the shape).

## Try it

- **Samples** — Observability group (menu `I`): *Glass Box Full Stack*, *Auto-Audit*, *Real vs Framework:
  Agent*, *Real vs Framework: Workflow*.
- **CLI** — `agenteval bench trace-fidelity`, `agenteval bench workflow-trace-fidelity`,
  `agenteval bench agentic --preset glass-box-diagnostics --trace <file>`.

## See also

- [Tracing](tracing.md) · [Trace Fidelity benchmark](benchmarks/trace-fidelity.md) ·
  [Workflow Trace Fidelity benchmark](benchmarks/workflow-trace-fidelity.md)
- ADR-019 (chat-boundary two-layer recording) · ADR-020 (AgentTrace schema)
