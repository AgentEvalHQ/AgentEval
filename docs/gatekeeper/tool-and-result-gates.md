# Tool-call, tool-result, and HTTP gates

This reference owns controls closest to a tool effect. For selection, start with the
[compact gate reference](gate-reference.md). For composition order, see
[Gate lifecycle and coordination](gate-lifecycle-and-coordination.md).

## Tool-call contract

An `IToolGate` sees one proposed local `AIFunction` call before execution and returns `Allow`, `Block`, or
`Mutate`. `ToolGatePolicy` determines how a finding is enforced:

| Policy | Block verdict | Mutate verdict |
|---|---|---|
| `WarnOnly` | records the finding and executes | records the proposed mutation without applying it |
| `ReplaceResult` | skips execution and returns a bounded refusal | applies the mutation, then continues |
| `Terminate` | skips execution and stops the function-calling loop | applies the mutation, then continues |

A gate may declare `MinimumPolicy`. Construction fails if the resolved policy would weaken it.

`WarnOnly` is the per-gate `ToolGatePolicy` value that the composition-level `GatekeeperEnforcement.Observe`
mode resolves to; the names differ because they are different enums at different layers. The normative mode
semantics live in [gate lifecycle and coordination](gate-lifecycle-and-coordination.md).

## Declarative tool contracts

`ToolUsageContractGate` is the preferred way to express per-tool argument policy. Build immutable contracts with
the fluent API or parse the packaged schema-v1 JSON. Parsing is strict, bounded, and atomic: duplicate or unknown
fields, unsupported input shapes, excessive depth/count/size, or an inconclusive projection fail construction or
fail closed at runtime.

| Predicate | Use |
|---|---|
| `piiScan` | Reject supported PII shapes in selected arguments |
| `deniedKeywords` | Reject bounded configured terms after the predicate's normalization |
| `recipientDomainAllowList` | Restrict parsed mail recipients to exact or subdomain matches |
| `shellMetacharDeny` | Reject configured PowerShell, POSIX shell, or cmd metacharacter dialects |
| `forbiddenIfPrecededBy` | Reject a guarded proposal after a configured trigger was proposed in the same run |
| `pathContainment` | Enforce lexical containment under reviewed absolute roots; not a filesystem/TOCTOU proof |
| distinct-value limit | Bound unique canonical values per run and contract dimension |

Contracts are authoritative authorization, not attack detectors. Keep them active after a jailbreak or injection
detector allows a paraphrase.

## Built-in tool-call gates

| Control | Purpose | State and key limit |
|---|---|---|
| `ForbiddenToolGate` | Deny exact tool names | Prefer removing the tool when you control the inventory |
| `ArgumentPatternGate` | Deny known-bad bounded argument patterns | Pattern matching is not semantic validation |
| `SequenceGate` | Block a guarded proposal after a trigger in a later model iteration | Proposal history is conservative; pair with same-batch protection |
| `SameBatchOrderingGate` | Block trigger + guarded siblings in one model turn | Stateless and conservative because concurrent siblings have no happens-before |
| `RunBudgetGate` | Cap total calls, per-tool calls, or monetary sum | Atomic per-run ledger |
| `MonetaryLimitGate` | Cap one monetary argument across a run | Isolated ledger dimension; negative values cannot create headroom |
| `PerToolCallBudgetGate` | Cap named tool counts across a run | Unnamed tools are outside this gate |
| `DomainAllowListGate` | Restrict URL arguments by host | Cannot see redirects or DNS; use the wire handler too |
| `ReferentialIntegrityGate` | Reject identifiers not introduced by the user or a trusted lookup | Heuristic recognizers; untrusted results never confer legitimacy |
| `TaintTrackingGate` | Stop source-result tokens reaching configured sinks | Incremental per-run state when scope exists; stateless history fallback otherwise |
| `BlockStormSentinelGate` | Detect repeated enforced denials in the root run tree | Coarse count, not argument-shape correlation |
| `ContainmentOverrideGate` | Enforce active/indeterminate exact or tenant-scope containment | Store failure and indeterminate state block |
| `ContainedIdentityGate` | Refuse a run/tool identity linked to contained targets | Requires authoritative resolvers |
| `SkillScriptExecutionGate` | Allow only reviewed Agent Skills scripts | Requires the chosen MAF approval posture to reach this seam |
| `ProbeEvaluatorGate` | Reuse a deterministic red-team evaluator inline | Only evaluators valid at this seam belong here |
| `CanaryToolGate` | Trip on a honeypot tool | Has an enforcement floor; never observation-only |
| `GateCostWatchdog` / cost checks | Refuse unbounded gate work on the hot path | LLM/network tool gates are rejected |

`HiddenInstructionPrefilterGate` protects result content, not tool arguments, and is listed below.

## Tool-result gates

An `IToolResultGate` runs after the tool has executed but before its result enters model context. It returns
`Allow`, `Block`, or `Redact`. It cannot undo the tool effect.

| Gate | Behavior | Scope and limit |
|---|---|---|
| `ToolResultInjectionGate` | Blocks configured injection markers | Cheap deterministic prefilter; paraphrases can evade |
| `HiddenInstructionPrefilterGate` | Blocks hidden/encoded instruction markers across bounded projections | Match or inconclusive blocks; independent of the contract engine |
| `ToolResultSecretGate` | Masks supported credential shapes | Shape-based, not a complete secret scanner |
| `ToolResultSizeGate` | Truncates above a fixed maximum | Best as a backstop for tools you do not control |
| `ToolResultSizeAnomalyGate` *(experimental)* | Redacts a per-tool outlier relative to prior results | Baseline is per run; flagged results do not poison the future baseline |

Under `WarnOnly`, a block finding records but returns the real result. Under enforcing policies it becomes a bounded
refusal. A `Redact` verdict supplies safer content and is applied by the result pipeline. Evidence uses the
`gate.tool-result.*` stage so it is distinguishable from a pre-execution tool block.

## Taint state: exact behavior

`TaintTrackingGate` owns a weak per-gate table keyed by the current `AgentRunScope`. It incrementally folds source
results into a bounded token set and keeps that state only for the run. It re-walks current history to remain safe
under sliding-window reducers, while tokenizing an identified source result at most once.

When no run scope exists, it does **not** use shared process state. It recomputes taint from the supplied history for
that call. This preserves isolation at higher cost and cannot remember a source result removed from history.

## HTTP wire boundary

`GatekeeperHttpMessageHandler` protects a different seam: the actual request made by the tool's own `HttpClient`.
It validates the host and DNS answers before each hop, disables transport auto-redirect, follows redirects itself,
and revalidates every target. It blocks private, loopback, link-local, or reserved resolution when configured.

`DomainAllowListGate` and the HTTP handler are complementary:

1. the tool gate validates the URL proposed in arguments;
2. the handler validates the request, redirect target, and DNS result;
3. result gates validate what comes back before model admission.

A tool that creates an unwrapped client is outside wire coverage. See
[Resource isolation and containment](resource-isolation-and-containment.md) for combining the handler with normal
and isolated client pools.

## Composition checklist

- Use one `UseGatekeeper` tool/result pipeline.
- Establish run scope for budgets, sequence, contracts with run history, and incremental taint.
- Pair `SequenceGate` with `SameBatchOrderingGate` when sibling calls may execute concurrently.
- Put fixed-size and secret controls before content is exposed downstream.
- Use attack and benign controls; a block-only fixture does not measure utility.
- Treat fake effects or real evidence as the pass oracle—not the model's refusal text.
