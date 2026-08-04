# Gate lifecycle and coordination

Gatekeeper is a set of controls at different execution boundaries, not one classifier. The controls become
effective when each gate is placed at the earliest boundary that can still prevent the effect it governs, and
when stateful gates share a run scope, evidence trail, identity, and containment policy.

Use this guide to decide where a gate belongs and how several gates cooperate. See the
[compact gate reference](gate-reference.md) for gate-family selection, the
[state ownership matrix](run-session-and-state.md#state-ownership-and-lifecycle-matrix) for exact lifetimes, and the
[sample index](sample-index.md) for executable coverage.

## Lifecycle at a glance

```text
agent construction
  ├─ validate configuration, policy floors, contracts, coverage inputs and skill baselines
  └─ build one coordinated middleware stack

run input
  ├─ establish AgentRunScope
  └─ run-pre gates inspect the request

model proposes a tool call
  ├─ containment override
  ├─ declarative tool contract
  ├─ remaining tool gates, in registration order
  └─ optional approval gate

tool executes only after every enforced pre-execution control allows it

tool returns
  └─ tool-result gates inspect/redact/block what enters model context

model returns
  ├─ run-post gates inspect the response
  └─ trace, telemetry and evidence sinks receive the decision record

after the hot path
  └─ an optional shadow judge can affect a later run, never retroactively protect this one
```

## Gate types and their authority

| Gate type | Boundary | Can prevent | Cannot prevent |
|---|---|---|---|
| Construction checks | Before `.Build()` or immediately after it | Unsafe composition, invalid contracts, uncalibrated promotion, known coverage gaps, skill drift | A runtime condition that was not visible during construction |
| Run-pre gate (`IChatGate`) | Request text before the protected model boundary | A rejected request from continuing through that boundary | Effects already performed by an outer orchestrator; authority checks on future tool arguments |
| Tool-call gate (`IToolGate`) | Each proposed local function call, before execution | The tool effect; it may also mutate arguments under an enforcing policy | Provider-hosted tools it cannot observe; redirects or DNS answers that do not exist yet |
| Tool approval (`IToolApprovalGate`) | Between proposal and execution | Execution until a human or policy approves it | Safety validation inside the tool; approval is not sanitization |
| Tool-result gate (`IToolResultGate`) | After execution, before the result returns to the model | Poisoned instructions, secrets, or oversized content entering model context | The tool effect that already occurred |
| HTTP egress handler | The tool's own `HttpClient`, at request/redirect/DNS time | Forbidden redirect hops, private-address resolution and DNS rebinding | Calls made by an unwrapped HTTP client |
| Run-post gate (`IChatGate`) | Final response | A leaking or unsafe response leaving the protected boundary | Tool effects that happened earlier in the run |
| Shadow judge | Asynchronous, after the hot path | Quarantine or another policy on a later run | The run it is judging |
| Containment gates | Before applicable runs and tool calls | Use of an already-contained session, MCP server, agent endpoint or other exact target | Discovery of compromise by itself; another gate or operator must create the containment record |
| Memory gates | Recall, write, promotion and lifecycle seams | Poisoned recall, untrusted writes, cross-tenant access, unsafe promotion and retention | Memory surfaces not routed through the protected provider/tool/MCP adapter |

The practical rule is simple: detection is not authorization. A jailbreak detector may reject obvious input, but
tool contracts, least privilege, taint controls and egress rules remain authoritative when detection misses a
paraphrase.

## What `UseGatekeeper` coordinates

`UseGatekeeper(enforcement, configure)` is the recommended composition point. It validates the synchronous
composition before mutating the builder, then wires:

1. run scope plus run-pre/run-post gates;
2. one tool middleware containing all tool-call and tool-result gates;
3. approval interop; and
4. the optional shadow-judge pump.

This order is the builder's composition order. The runtime tool path is still proposal checks, optional approval,
tool execution, and result checks. Result gates are intentionally distinct because their subject already exists:
they protect admission into model context, not execution of the tool.

Do not chain separate `UseAgentEvalToolGate(...)` calls. MAF middleware registrations wrap one another; a later,
outer registration can block without forwarding and starve the earlier gates. Put every tool gate in one list or
use `UseGatekeeper`.

## Coordination inside the tool boundary

Gatekeeper normalizes the configured tool gates before execution:

- active containment is an override, so a contained target cannot be re-enabled by a later allow verdict;
- declarative `ToolUsageContractGate` contracts are evaluated as the authoritative per-tool argument policy;
- remaining gates run deterministically in their resolved order;
- the first enforced block prevents tool execution and later gates do not get to reinterpret that call as safe;
- a mutation is applied only in an enforcing mode, then the resulting arguments are what downstream checks and
  the tool receive;
- every decision is written to the same operator evidence channels.

Order stateful gates for the data they require. For example, a sensitive-source read must be observed before a
`TaintTrackingGate` can reject its value at an email or HTTP sink. `SequenceGate` and
`ForbiddenIfPrecededByPredicate` use proposal history conservatively: seeing a proposal is not proof the tool
executed. When order inside one parallel model batch matters, add `SameBatchOrderingGate`; there is otherwise no
deterministic happens-before relationship between sibling calls in that batch.

## Shared state and evidence

Several gates coordinate through resources owned by the composed stack:

| Shared resource | Purpose | Typical consumers |
|---|---|---|
| `AgentRunScope` / run ledger | Isolates counters and proposal history per run | budgets, sequence, stateful contracts and block-storm controls |
| Per-run gate-owned state | Keeps mechanism-specific state bound to the current run | incremental taint and result-size anomaly baselines |
| `SessionIdentity` | Provides a stable logical session key across persisted reloads or workers | rate limits, identity drift and session-aware gates |
| `AgentTrace` | One Glass Box record of run, tool and result decisions | samples, review, incident reconstruction and replay |
| `IGateEvidenceSink` | Fans structured findings to another bounded operator sink | reference ledger and alerting |
| `GateTelemetry` | Aggregates gate effectiveness without replacing trace evidence | rollout tuning and false-positive review |
| `IContainmentStore` | Persists exact containment targets and evidence references | containment override and operator response |
| calibration report store | Proves a judge beat its deterministic baseline before inline promotion | `IRequiresCalibration` run gates |

For the authoritative call/batch/run/session/process/durable ownership, partition, concurrency, reset, restart,
fallback, and evidence contract, use the [state ownership matrix](run-session-and-state.md#state-ownership-and-lifecycle-matrix).

`EstablishRunScope` defaults to `true`. Gatekeeper refuses an enforcing composition that registers a
run-scope-dependent gate without establishing the scope. Configure `SessionIdentity` once on
`GatekeeperOptions`; it is injected into session-aware gates unless a gate already owns an explicit resolver.

## Enforcement modes

| Mode | Tool block | Run block | Use it for |
|---|---|---|---|
| `Observe` | Record only | Record only | Baselines and rollout tuning; requires trace or telemetry |
| `ReplaceResult` | Do not execute; return a bounded refusal and continue the loop | Redact/refuse while preserving the run contract | Recoverable denials where the agent may choose a safe alternative |
| `Terminate` | Do not execute and stop the function-calling loop | Throw a refusal exception | Canary trips, destructive actions and other hard-stop policies |

A gate can declare a `MinimumPolicy`. `UseGatekeeper` refuses construction rather than silently weakening a
canary or another control below its floor. Under `Observe`, mutations and result redactions are recorded as
not-applied; they do not alter runtime behavior.

## Construction, promotion and coverage gates

Some decisions belong before code is promoted, not on every request:

- calibrate LLM-backed judges against reviewed task-specific corpora and a deterministic baseline;
- call `ValidateInlineJudgesAsync` before trusting an `IRequiresCalibration` judge inline;
- make tool contracts strict, bounded and data-only; rebuild the agent to adopt a changed schema-v1 file;
- use `KnownTools` for an early coverage check, but treat it as a potentially stale list;
- after `.Build()`, run the agent overload of `GatekeeperCoverageAnalyzer.AnalyzeOrThrow` for the live tool set;
- fail promotion when a high-risk tool, dynamic tool surface, memory provider, or MCP boundary is outside the
  declared enforcement coverage.

Coverage is reachability, not correctness. A report showing 100% means every reported local tool is visible to a
registered gate; it does not prove the policy is semantically sufficient.

## Recommended defense stacks

### Poisoned retrieval or MCP tool

1. Treat tool/MCP output as untrusted and apply `ToolResultInjectionGate`, secret and size controls.
2. Withhold unsafe results from model context.
3. Record an exact evidence reference and contain the compromised MCP server when policy or an operator confirms
   the source is compromised.
4. Let containment override block retries.
5. Keep downstream contracts, taint and egress controls active in case an evasive payload reaches the model.

### Jailbreak and weird user request

1. Use a run-pre detector for clear attack markers.
2. Enforce per-tool contracts independently of the detector verdict.
3. Deny destructive tools unless explicitly required; constrain recipients, domains, identifiers and shell syntax.
4. Add budgets and block-storm escalation for repeated probing.
5. Preserve a benign control path to measure over-refusal.

### Agent Harness and dynamic capabilities

1. Discover the actual runtime-injected function names; do not assume documentation names.
2. Disable Harness capabilities the application does not need.
3. Gate the remaining Harness-owned functions by exact name and arguments.
4. Bound iterations and tool calls.
5. Re-run coverage after composition because a pre-build `KnownTools` list cannot prove dynamic coverage.

## Known boundaries

- A tool-result gate cannot undo a tool's side effect.
- An argument-domain gate cannot see redirect or DNS behavior; use the egress handler in the tool's client.
- Local function middleware cannot intercept a provider-hosted tool the provider executes remotely.
- `KnownTools` can drift from the built agent and cannot see every `AIContextProvider` contribution.
- Input text can be transformed by an outer agent or harness before it reaches an inner chat-client gate. Place
  the control at the boundary whose original input you need to govern and verify that placement with a fake
  provider call counter.
- Containment is an enforcement state, not a compromise classifier. Require evidence and an explicit policy or
  operator action before containing a shared server.
- HTTP Bulkhead isolation separates local pools and concurrency. It cannot partition a provider quota shared by
  the same credential; see [resource isolation operations](resource-isolation-and-containment.md).
- Fail-closed protects safety but can reduce availability. Bound timeouts, refusal content and evidence payloads,
  and test benign controls beside attacks.

## Executable examples

- [`14_GatekeeperPoisonedToolKillChain`](../../samples/AgentEval.Samples/Gatekeeper/14_GatekeeperPoisonedToolKillChain.cs)
  demonstrates poisoned-result admission, durable fake MCP containment, declarative contracts, taint, egress,
  forbidden deletion, worm blocking and block-storm escalation.
- [`15_GatekeeperHarnessOwnedToolMisuse`](../../samples/AgentEval.Samples/Gatekeeper/15_GatekeeperHarnessOwnedToolMisuse.cs)
  discovers a real Harness-owned function at runtime, blocks its misuse and retains a benign control.
- [`16_GatekeeperJailbreakAndToolAbuse`](../../samples/AgentEval.Samples/Gatekeeper/16_GatekeeperJailbreakAndToolAbuse.cs)
  contrasts an obvious pre-model block with paraphrases stopped by authoritative tool contracts.
- [`17_GatekeeperToolResultAdmission`](../../samples/AgentEval.Samples/Gatekeeper/17_GatekeeperToolResultAdmission.cs)
  composes secret masking and output-size truncation before tool results become model context.
- [`18_GatekeeperHostedToolCoverageBoundary`](../../samples/AgentEval.Samples/Gatekeeper/18_GatekeeperHostedToolCoverageBoundary.cs)
  demonstrates construction-time refusal and honest acknowledgment of provider-hosted execution.

All five are deterministic and offline. Their harmful operations are fake counters, and their pass/fail result is
derived from gate evidence and effect invariants rather than model compliance. The hosted-boundary sample invokes
neither the hosted tool nor a provider.
