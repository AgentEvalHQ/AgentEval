# Gatekeeper

**Gatekeeper is AgentEval's fail-closed runtime protection layer for Microsoft Agent Framework agents.**

Evaluation and red teaming tell you what went wrong. Gatekeeper places reviewed controls at the execution seams
where unsafe work can still be stopped: before a run, before a local tool executes, before a tool result enters
model context, before a response leaves the boundary, and inside an explicitly wrapped HTTP client.

Gatekeeper is most useful when an agent legitimately needs powerful tools, when tools arrive dynamically, or when
you need evidence that a policy was evaluated. It does not replace least privilege or validation inside a tool.

## The model

| Protected seam | Typical decision | Examples |
|---|---|---|
| Construction | Refuse an unsafe or unverifiable configuration | coverage checks, prompt/MCP/skill drift, calibration readiness |
| Run input and output | Allow or refuse text crossing the agent boundary | injection, PII, rendered-output exfiltration, calibrated judges |
| Local tool call | Allow, block, or safely mutate before execution | contracts, budgets, sequences, taint, containment |
| Tool approval | Auto-approve or escalate before execution | sensitive names, risky arguments, semantic goal mismatch |
| Tool result | Allow, block, or redact before model admission | injected instructions, secrets, oversized or anomalous results |
| HTTP wire | Allow or block each request, redirect, and DNS result | host allow-lists, private-network and DNS-rebinding defense |
| Later run | Apply a bounded asynchronous finding | shadow judgment and quarantine |

One control rarely covers an entire attack. Put each gate at the earliest seam with enough information to make its
decision, and keep downstream authorization controls active even when an upstream detector allows the request.

## Start with one coordinated stack

Use `UseGatekeeper` when more than one Gatekeeper layer participates. It validates the composition and installs
run scope, run gates, one tool/result pipeline, approval integration, and optional shadow processing in the required
order.

The snippet below is compiled in the test project and compared mechanically with this page:

<!-- compiled-snippet:coordinated-stack -->
```csharp
private static AIAgent BuildProtectedAgent(AIAgent baseAgent, AgentTrace trace) =>
    baseAgent.AsBuilder()
        .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
        {
            options.Add(new ForbiddenToolGate("delete_all_customers"));
            options.Add(new RunBudgetGate(maxToolCalls: 20));
            options.AddPreGate(new TokenInjectionGate());
            options.Trace = trace;
        })
        .Build();
```

Choose the enforcement mode explicitly:

- `Observe` records findings without changing behavior. Use it to measure a policy before promotion.
- `ReplaceResult` prevents an unsafe local tool call and lets the agent choose a safe alternative.
- `Terminate` prevents the call and stops the function-calling loop.

There is deliberately no default. A security API named Gatekeeper must not look enforcing while silently running
in observation mode. For composition details and policy floors, see
[Gate lifecycle and coordination](gate-lifecycle-and-coordination.md).

## Three operating principles

1. **Fail closed when verification is required.** An unreadable contract, missing identity, inconclusive enforced
   check, or unavailable containment state does not become an allow decision.
2. **Report only what happened.** Evidence distinguishes a finding from the action applied. Observation never
   claims that a call was blocked, and incomplete graph or coverage data never becomes a healthy score.
3. **Promote with evidence.** Deterministic gates are replayed against attack and benign controls. LLM-backed
   judges remain shadow-only until the exact rubric, model, and options clear their calibration bar.

## Beachhead and Tribunal

The **Beachhead** is the deterministic starting set: bounded budgets, explicit destinations, and output controls.
It is cheap, offline-testable, and has no model-calibration dependency.

The **Tribunal** is the semantic layer: small single-purpose judges at run boundaries, approval, or the asynchronous
shadow lane. A judge earns inline authority only after task-specific calibration; a model name alone is not proof.

Start with deterministic authorization. Add semantic judgment only where deterministic policy cannot express the
decision.

## Honest limits

- A local tool gate cannot intercept a provider-hosted tool executed outside the MAF local-function seam.
- A static tool inventory cannot prove what every dynamic `AIContextProvider` may contribute later.
- A tool-result gate protects model context after execution; it cannot undo the tool's side effect.
- An argument URL check cannot see redirects or DNS answers. Wire-level protection requires the Gatekeeper HTTP
  handler inside the tool's own client.
- Local [Bulkhead](resource-isolation-and-containment.md) pools isolate local connection and concurrency pressure,
  not a downstream quota shared by the same provider credential.
- Containment enforces an existing decision; another trusted control or operator must create that decision.
- Fail-closed behavior protects safety but can reduce availability. Bound timeouts, queues, evidence, and refusals.

Use `GatekeeperCoverageAnalyzer` after composition and treat its report as reachability evidence—not proof that a
policy is semantically sufficient.

## Where to go next

**Start here → [First recipes](examples.md)** — compose one stack, then run the first sample:

```bash
dotnet run --project samples/AgentEval.Samples
# open group J — Gatekeeper (Runtime Protection)
```

### Reference

- [Gate reference](gate-reference.md) — choose a gate family and follow its focused reference.
- [Gate lifecycle and coordination](gate-lifecycle-and-coordination.md) — ordering, authority, and composition.
- [State ownership and lifecycle](run-session-and-state.md) — run, session, process, and durable state semantics.
- [Containment and resource isolation operations](resource-isolation-and-containment.md) — routing and Bulkhead use.
- [Sample index](sample-index.md) — execution modes, boundaries, and the manifest-backed sample catalog.
- [Implementation status](implementation-status.md) — shipped, externally gated, and demand-gated scope.
