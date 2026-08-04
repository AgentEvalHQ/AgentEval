# Gatekeeper

**Glass Box tells you what your agent *did*. Gatekeeper stops it from doing the wrong thing — at runtime, fail‑closed.**

Evaluation and red‑teaming find problems *after the fact*. Gatekeeper is the other half: it puts the same
checks **in the request path** so a forbidden tool call, a poisoned argument, or a compromised conversation is
**blocked before it happens**. It plugs into the Microsoft Agent Framework agent pipeline — no rewrite — and
every gate is **fail‑closed**: if a gate cannot prove an action safe, it does not run it.

> **Where to go:** this page is the **introduction** (concepts, the layer categories, and how to wire them
> together with `UseGatekeeper`). The [**Gate lifecycle and coordination**](gate-lifecycle-and-coordination.md)
> guide explains execution order and cross-gate state. The [**Gate reference**](gate-reference.md) is the ranked
> catalogue, the [**Sample index**](sample-index.md) maps executable coverage, and [**Examples**](examples.md) is
> the recipe-oriented cookbook.

## The design principle: fail closed, and prove it

A security control that quietly does nothing is worse than none — it gives false assurance. Gatekeeper takes
two hard positions:

- **Cannot‑inspect ⇒ deny.** A gate that throws, times out, or can't read its context **blocks**. A gate is
  never allowed to fail *open*.
- **Honest evidence.** Every decision is recorded into the same [`AgentTrace`](../glass-box.md) the evaluators
  read, under `gate.*` keys. A gate that only *warns* records `action="Warn"` — it is **never** counted as a
  block, so the evidence can never claim it stopped a call that actually ran.

## The layers

Gatekeeper wraps a MAF agent at the seams where an action can be stopped. Register only the gates you need; each
is independent and writes to one shared trace.

| Category | Seam | What it does |
|---|---|---|
| **Tool gates** | each tool call, pre‑execution | Block / mutate a *specific live tool call* (forbidden / poisoned / out‑of‑sequence) |
| **Tool RESULT gates** | each tool call, post‑execution | Block / redact what the model gets to see of a result that already happened (injected instructions, secrets, oversized payloads a poisoned fetch/file/API response carries) |
| **HTTP egress enforcement** | the tool's own outbound HTTP request, on the wire | Catches what an argument‑string scan structurally can't: a redirect to a forbidden host, or a DNS answer resolving an allow‑listed hostname to a private/internal address (SSRF/DNS‑rebind). **Different composition point** — wraps the tool's own `HttpClient` (`GatekeeperHttpMessageHandler.CreateHttpClient`), not registered via `UseGatekeeper` |
| **The moat** | each tool call | Your *red‑team oracles* + canaries run as runtime gates — your tests become defenses |
| **Run gates** | the run's input & output text | Reject an incoming attack (run‑pre) or a leaking response (run‑post) |
| **Session gates** | before a run | Enforce *who* may drive it (auth), *how often* (rate), and *quarantine* |
| **Shadow judge** | *after* the run, async | Run expensive LLM/network checks off the hot path → arm quarantine for a **later** run |
| **Tool approval** | agent boundary | Route a *borderline* call to a **human** (approve/reject) instead of hard‑blocking |

The **cost budget** is the load‑bearing constraint. Tool gates and the moat run on the hot path and **reject**
network / LLM work at construction (via `GateCost`) — an LLM judge on every tool call would stall the agent and
risk a fabricated verdict. Expensive judgment goes to the **shadow judge**, or — for a *fast, calibrated* judge —
the run‑pre/run‑post seam (see The Tribunal, below); run and session gates reuse `IChatGate` (no cost member), so
keeping those pure‑code is a convention.

## Wiring it together: `UseGatekeeper`

Every example on the [examples page](examples.md) that wires more than one layer chains the low‑level builder
calls by hand: `.UseAgentEvalGate()` → `.UseAgentEvalToolGate(...)` → `.UseAgentEvalToolApproval(...)` →
`.UseAgentEvalShadowJudge(...)`. That works, but the order matters (run‑scope must be established before the
tool gate that depends on it) and it's easy to get wrong silently — a stateful gate that needs a run scope
still *runs* without one, it just falls back to shared, process‑wide state instead of throwing.

**`UseGatekeeper(enforcement, configure)`** is the recommended composite builder for anything beyond a single
gate: it installs run‑scope, tool gates, approval interop, and the shadow judge together, in the correct order,
in one call — and it **validates what manual composition can't catch**, at construction time:

```csharp
using AgentEval.MAF.Gatekeeper;

var agent = baseAgent.AsBuilder()
    .UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
    {
        g.Add(new SequenceGate(["read_customer_data"], ["send_email", "http_post"]));
        g.Add(new RunBudgetGate(maxToolCalls: 20));
        g.Trace = trace;               // shared Glass Box trace across every mechanism composed here
        g.Telemetry = telemetry;       // optional GateTelemetry sink — which gates fire, how often, how long
    })
    .Build();
```

`GatekeeperEnforcement` is a **required** parameter — `Observe` (record every finding, block nothing — the
recommended safe first rollout), `ReplaceResult` (block the call, keep the loop running), or `Terminate` (block
and stop the function‑calling loop). There is deliberately no default: the whole point of a name like
"Gatekeeper" is that developers assume it enforces, so a silent `WarnOnly` default would be the exact false
assurance this toolkit argues against elsewhere. Two named sugar methods make the choice visible in the call
site itself: `ObserveWithAgentEvalGates(configure)` and `EnforceAgentEvalGates(configure, level: ...)` (defaults
to `Terminate`, the strongest level — "enforce" unqualified should mean "actually protect me").

`UseGatekeeper` refuses to construct — throwing before any middleware is wired, never partially — when it can
prove the composition is unsafe:

- A gate that needs an established run scope (`GateRequirements.RunScope` — `RunBudgetGate`, `MonetaryLimitGate`,
  `PerToolCallBudgetGate`, `SequenceGate`) is registered without one (`EstablishRunScope = false` and no pre/post
  gate) under a non‑`Observe` enforcement level.
- A gate with an enforcement floor above the resolved policy — a canary/honeypot's `MinimumPolicy` — is
  registered under `Observe` (which always resolves to `WarnOnly`). Running a honeypot under `WarnOnly` would
  silently defeat the trap, so `UseGatekeeper` refuses rather than downgrade it; exclude such gates from an
  `Observe`‑mode rollout and add them separately once you're ready to enforce.

**Know what's actually protected — `GatekeeperCoverageAnalyzer`.** "I called `UseGatekeeper`" is not the same
question as "is every high‑risk tool actually reachable by a gate." The analyzer answers that honestly: it
classifies every tool exposed to the agent's model (a local `AIFunction` a tool gate can see vs. a
provider‑hosted tool no Gatekeeper mechanism ever will) and reports an `EnforcementCoveragePercent`.

```csharp
var options = new GatekeeperOptions();
var agent = baseAgent.AsBuilder()
    .UseGatekeeper(GatekeeperEnforcement.Terminate, g =>
    {
        g.Add(new ForbiddenToolGate("delete_account"));
        g.KnownTools = myTools;                    // the SAME list you set on ChatOptions.Tools
        g.RefuseUnprotectedHighRiskTools = true;    // eager, construction-time refusal
        options = g;
    })
    .Build();

Console.WriteLine(options.CoverageReport!.Render());   // the AUTHORITATIVE report — same gates that were actually wired
```

`RefuseUnprotectedHighRiskTools` throws `UnprotectedHighRiskToolException` right at registration if a tool a
coarse keyword heuristic (`ToolRiskClassifier` — override via `AnalyzeOptions.IsHighRisk` when it misclassifies
yours) flags as high‑risk has zero protecting gate — before the agent ever starts. It's a heuristic safety net,
not a proof: it's blind to a tool an `AIContextProvider` (e.g. Agent Skills) contributes dynamically, since only
`ChatOptions.Tools` is inspectable at this point — see the gate reference's [Coverage &
telemetry](gate-reference.md#coverage--telemetry) section for the full honest limits.

## The Beachhead and The Tribunal

Two named groupings across those layers capture the arc from "turn it on today" to "add judgment safely":

- **The Beachhead** — the *deterministic floor* you enable with **no LLM and no calibration**: `RunBudgetGate`
  (denial‑of‑wallet), `DomainAllowListGate` (exfil via tool‑argument URLs), and `RenderedOutputExfilGate` (exfil
  via a rendered‑answer image beacon), all off the per‑run `RunLedger` cross‑hop accumulator. Near‑zero false
  positives, hot‑path safe — it covers two of the highest‑severity agent threats *before any judge exists*.

- **The Tribunal** — fast, **single‑axis LLM judges** as runtime gates (`CompositeJudgeGate<TRubric>`), for the
  attacks a keyword list can't catch. The flagship `IndirectInjectionJudge` sits run‑pre, scoring the *incoming*
  prompt; three more — `ExfiltrationIntentJudge`, `SystemPromptExtractionJudge`, and `OverRefusalJudge` — sit
  run‑post, scoring the *rendered output* for leakage, exfiltration intent, and wrongful refusal (the last is
  advisory‑only, wired `WarnOnly`, never blocking). Its defining rule: a judge must **earn the right to block**.
  The `GateCalibrationHarness` ("the Bar") scores a judge against a both‑directions gold set and refuses to
  promote it inline until it beats the baseline — so an un‑calibrated judge stays in the shadow lane. Compose
  several axes with `ParallelJudgeFanOut`; cache repeats with `JudgeVerdictCache`. See the [gate
  reference](gate-reference.md#shipped-tribunal-judges) for the full roster.

Start with the Beachhead — it ships value immediately. Add a Tribunal judge only after you've **calibrated it on
your own data**; its accuracy is your measurement, not a number this toolkit claims for you.

## When is the Gatekeeper actually worth it?

Be honest with yourself: **a single, fully‑controlled agent barely needs most of this.** If you own the tool
list, *don't give the agent the dangerous tool*; if a tool has an unsafe mode, *validate inside its body*. Those
are stronger and simpler than any gate. The Gatekeeper earns its keep in four situations — and each gate's
[usefulness rank](gate-reference.md#how-to-read-the-rank) should be read through them:

1. **You don't control the tools** — MCP servers, plugins, runtime‑registered tools, or a multi‑agent handoff
   where a sub‑agent's tools leak upward. You can't "just not give the tool" a tool you never see coming.
2. **The model itself is the adversary** — a prompt injection turns *your own* agent against you, misusing a
   tool you legitimately granted. Not‑giving‑the‑tool is moot; *how* it is called is the attack.
3. **Audit & compliance** — the **evidence trail** ("we blocked X, here's the trace") has value even when the
   block is redundant. Not giving the tool leaves no such record.
4. **Genuinely unique capabilities** — dangerous *sequences*, *honeypots*, *human approval*, *expensive async*
   checks, and the closed‑loop *moat*. These have no simpler equivalent.

The [**Gate reference**](gate-reference.md) ranks every gate on exactly this basis.

## See also

- [**Gate lifecycle and coordination**](gate-lifecycle-and-coordination.md) — gate types, execution order, shared state, containment, and known boundaries.
- [**Sample index and coverage**](sample-index.md) — choose a runnable scenario by complexity, gate, feature, or protected boundary.
- [**Gate reference**](gate-reference.md) — the ranked catalogue (what each gate does + how useful it is).
- [**Examples**](examples.md) — offline and live runnable recipes plus a credential-free CLI on-ramp.
- [**Explainability & Trust**](explainability-and-trust.md) — reconstructable gate provenance, counterfactual gate-config replay, and a unified Trust Score.
- [Glass Box](../glass-box.md) — the dual‑boundary trace Gatekeeper records its evidence into.
- [Guardrails](../guardrails.md) — the chat‑gate primitives (`IChatGate`, `EvalGatePolicy`) the run gate reuses.
- [Red Team](../redteam.md) — the probes and canaries the moat gates are built from.
