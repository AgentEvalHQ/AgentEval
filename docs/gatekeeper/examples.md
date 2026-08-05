# Gatekeeper recipes

This page is the shortest path from “I need runtime protection” to one correctly composed Gatekeeper stack.
Use the [introduction](introduction.md) for the model, the [gate reference](gate-reference.md) to select a
control family, and the [sample index](sample-index.md) for the complete executable catalog.

Gatekeeper intentionally keeps 30 sample contracts because they prove different boundaries. The interactive
launcher shows only six recommended samples at first; press **M** to reveal all 29 menu entries. The A2A calibration
fixture is direct-only, which is why the manifest contains one more entry than the menu.

## Pick a learning path

| Goal | Run these samples | What the path teaches |
|---|---|---|
| Fastest useful tour | **00 → 16 → 14** | One gate, layered jailbreak defense, then the poisoned-tool capstone |
| Tool and egress protection | **02 → 17 → 21 → 23** | Cross-call policy, result admission, same-batch ordering, and the HTTP wire |
| State and containment | **20 → 19 → 22 → 26** | Run/session/durable state, Bulkhead routing, graph response, and identity takeover |
| Construction and dynamic tools | **18 → 24 → 27** | Honest hosted coverage, dynamic providers, and prompt/MCP drift |
| Semantic judgment and approval | **03 → 28 → 25 → 04** | Human continuation, approval failure modes, Crescendo timing, and calibrated judges |
| Operations and assurance | **10 → 20 → 22** | Provenance/replay, lifecycle evidence, and read-only incident projection |

All paths except the live portion of sample 04 run without credentials. Sample 11A is intentionally absent because
it requires a separately authorized remote A2A endpoint.

## Canonical composition

Use `UseGatekeeper` when more than one layer participates. It validates configuration before mutating the builder
and installs run scope, run gates, one tool/result pipeline, approval, and optional shadow processing. The following
snippet is compiled in the test project and compared mechanically with this page.

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

- `Observe` records findings without applying blocks or mutations.
- `ReplaceResult` prevents an unsafe local call and lets the model choose a safer alternative.
- `Terminate` prevents the call and stops the function-calling loop.

Do not chain separate `UseAgentEvalToolGate(...)` registrations. MAF middleware wraps in registration order, so
an outer gate can stop forwarding and silently starve an inner list. Use a low-level builder only for one specialist
seam or a sample that explicitly teaches it.

## Recipe: authorize tools independently of jailbreak detection

A request detector and a tool contract answer different questions:

1. reject obvious attack text at run-pre;
2. authorize exact tool names and argument shapes;
3. limit behavior across proposals with budgets and sequence gates;
4. admit or redact tool results before model context; and
5. validate redirects and DNS at the actual HTTP seam.

Keep downstream authorization active when an upstream detector allows a paraphrase. Sample
[`16_GatekeeperJailbreakAndToolAbuse`](../../samples/AgentEval.Samples/Gatekeeper/16_GatekeeperJailbreakAndToolAbuse.cs)
demonstrates this contrast. Sample
[`14_GatekeeperPoisonedToolKillChain`](../../samples/AgentEval.Samples/Gatekeeper/14_GatekeeperPoisonedToolKillChain.cs)
extends it through poisoned results, containment, taint, exfiltration, deletion, and propagation attempts.

## Recipe: protect tool results

A result gate runs after the tool effect but before the result enters model context. It cannot undo execution.

<!-- compiled-snippet:result-admission -->
```csharp
private static AIAgent BuildResultProtectedAgent(AIAgent baseAgent, AgentTrace trace) =>
    baseAgent.AsBuilder()
        .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
        {
            options.AddResultGate(new ToolResultSecretGate());
            options.AddResultGate(new ToolResultSizeGate(maxLength: 4096));
            options.Trace = trace;
        })
        .Build();
```

Use `ToolResultInjectionGate` or `HiddenInstructionPrefilterGate` for hostile instructions. Use
`ToolResultSizeAnomalyGate` only as an experimental complement to a fixed exhaustion limit. Samples 17 and 29
show the fixed and adaptive cases.

## Recipe: protect the HTTP wire

`DomainAllowListGate` validates a URL argument; it cannot see future redirects or DNS answers. Put
`GatekeeperHttpMessageHandler` inside the tool’s `HttpClient`. A captured or unwrapped client remains outside
coverage. Combine it with `ContainmentHttpClientPool` only when degraded contained work is intentional. Samples 19
and 23 are the executable pair; see [resource isolation operations](resource-isolation-and-containment.md).

## Recipe: pause for approval

Approval is not sanitization. Contracts validate arguments; approval decides whether execution pauses.
`ArgumentPatternApprovalGate` covers risky arguments, `ToolNameApprovalGate` covers sensitive parameterless tools,
and `ToolArgumentGoalCoherenceApprovalGate` escalates mismatch, timeout, provider error, or parse failure. Sample 03
is the smallest continuation; sample 28 is the complete reject/approve matrix.

## Recipe: use semantic judges honestly

Every Tribunal judge owns one axis, rubric, parser, timeout, output cap, and calibration report. Promote the exact
configuration—not a model name—and retain deterministic authorization downstream. A shadow verdict affects only a
later run; sample 25 demonstrates that timing. Calibration-set accuracy is not held-out evidence. See
[judges, approval, and shadow](judges-approval-and-shadow.md).

## Recipe: verify state ownership

Test at least two calls, runs, sessions, reloads, and—where relevant—process restarts. Sample 20 follows:

```text
call/batch → run ledger resets → logical session survives reload
           → rate window expires → durable containment survives store reopen
```

Sample 21 handles sibling calls with no happens-before relation. Sample 26 proves why an object reference is not a
stable security identity and exercises atomic first-actor binding.

## Recommended versus complete

| Sample | Why it is recommended |
|---:|---|
| 00 | Smallest gate and evidence loop |
| 14 | Best end-to-end poisoned-tool capstone |
| 16 | Clearest detector-versus-authorization lesson |
| 20 | Best state ownership and restart timeline |
| 23 | The wire boundary argument-only examples miss |
| 27 | Construction-time prompt and MCP integrity |

The other samples are not obsolete. Each owns a unique threat, boundary, or operational proof in the
[manifest-backed catalog](sample-index.md). Hiding them behind the launcher’s **M** toggle reduces cognitive load
without losing coverage or changing legacy numeric execution order.

## From the CLI

```bash
agenteval redteam --sut gatekeeper-demo --intensity quick \
  --baseline gatekeeper-demo.baseline.json --fail-on regression
```

For language-neutral deterministic inspection, use `agenteval gatekeeper inspect`. See
[Attack the Gate](attack-the-gate.md) and the [Gatekeeper CLI](../gatekeeper-cli.md).
