# Gatekeeper

**Glass Box tells you what your agent *did*. Gatekeeper stops it from doing the wrong thing — at runtime, fail‑closed.**

Evaluation and red‑teaming find problems *after the fact*. Gatekeeper is the other half: it puts the same
checks **in the request path** so a forbidden tool call, a poisoned argument, or a compromised conversation is
**blocked before it happens**. It plugs into the Microsoft Agent Framework agent pipeline — no rewrite — and
every gate is **fail‑closed**: if a gate cannot prove an action safe, it does not run it.

> **Three docs:** this page is the **introduction** (concepts + the layer categories). The
> [**Gate reference**](gatekeeper-gates.md) is the ranked catalogue — every gate, what it does, and *how much it
> actually earns its keep*. The [**Examples**](gatekeeper-examples.md) page is the runnable cookbook.

## The design principle: fail closed, and prove it

A security control that quietly does nothing is worse than none — it gives false assurance. Gatekeeper takes
two hard positions:

- **Cannot‑inspect ⇒ deny.** A gate that throws, times out, or can't read its context **blocks**. A gate is
  never allowed to fail *open*.
- **Honest evidence.** Every decision is recorded into the same [`AgentTrace`](glass-box.md) the evaluators
  read, under `gate.*` keys. A gate that only *warns* records `action="Warn"` — it is **never** counted as a
  block, so the evidence can never claim it stopped a call that actually ran.

## The layers

Gatekeeper wraps a MAF agent at the seams where an action can be stopped. Register only the gates you need; each
is independent and writes to one shared trace.

| Category | Seam | What it does |
|---|---|---|
| **Tool gates** | each tool call, pre‑execution | Block / mutate a *specific live tool call* (forbidden / poisoned / out‑of‑sequence) |
| **The moat** | each tool call | Your *red‑team oracles* + canaries run as runtime gates — your tests become defenses |
| **Run gates** | the run's input & output text | Reject an incoming attack (run‑pre) or a leaking response (run‑post) |
| **Session gates** | before a run | Enforce *who* may drive it (auth), *how often* (rate), and *quarantine* |
| **Shadow judge** | *after* the run, async | Run expensive LLM/network checks off the hot path → arm quarantine for a **later** run |
| **Tool approval** | agent boundary | Route a *borderline* call to a **human** (approve/reject) instead of hard‑blocking |

The **cost budget** is the load‑bearing constraint. Tool gates and the moat run on the hot path and **reject**
network / LLM work at construction (via `GateCost`) — an LLM judge on every tool call would stall the agent and
risk a fabricated verdict. The **shadow judge** is where that expensive judgment goes; run and session gates
reuse `IChatGate` (no cost member), so keeping those pure‑code is a convention.

## When is the Gatekeeper actually worth it?

Be honest with yourself: **a single, fully‑controlled agent barely needs most of this.** If you own the tool
list, *don't give the agent the dangerous tool*; if a tool has an unsafe mode, *validate inside its body*. Those
are stronger and simpler than any gate. The Gatekeeper earns its keep in four situations — and each gate's
[usefulness rank](gatekeeper-gates.md#how-to-read-the-rank) should be read through them:

1. **You don't control the tools** — MCP servers, plugins, runtime‑registered tools, or a multi‑agent handoff
   where a sub‑agent's tools leak upward. You can't "just not give the tool" a tool you never see coming.
2. **The model itself is the adversary** — a prompt injection turns *your own* agent against you, misusing a
   tool you legitimately granted. Not‑giving‑the‑tool is moot; *how* it is called is the attack.
3. **Audit & compliance** — the **evidence trail** ("we blocked X, here's the trace") has value even when the
   block is redundant. Not giving the tool leaves no such record.
4. **Genuinely unique capabilities** — dangerous *sequences*, *honeypots*, *human approval*, *expensive async*
   checks, and the closed‑loop *moat*. These have no simpler equivalent.

The [**Gate reference**](gatekeeper-gates.md) ranks every gate on exactly this basis.

## See also

- [**Gate reference**](gatekeeper-gates.md) — the ranked catalogue (what each gate does + how useful it is).
- [**Examples**](gatekeeper-examples.md) — runnable, credential‑free cookbook + the CLI on‑ramp.
- [Glass Box](glass-box.md) — the dual‑boundary trace Gatekeeper records its evidence into.
- [Guardrails](guardrails.md) — the chat‑gate primitives (`IChatGate`, `EvalGatePolicy`) the run gate reuses.
- [Red Team](redteam.md) — the probes and canaries the moat gates are built from.
