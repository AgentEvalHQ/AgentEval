# Gatekeeper — gate reference

Every built‑in gate, what it does, and **how much it actually earns its keep**. For the concepts and the layer
map, start with the [introduction](introduction.md); for runnable code, see the [examples](examples.md).

## How to read the rank

The **usefulness rank** answers one question honestly:

> *How much unique value does this gate add over the **simplest alternative** — not granting the tool at all, or
> validating inside the tool's own body?*

It is a **1–5** scale. It is about **marginal value**, not whether the gate works — every gate here works.

| Rank | | Meaning |
|:--:|---|---|
| **5** | 🟢🟢 **Essential** | A unique capability with no simpler equivalent — a *primary* reason to adopt Gatekeeper. |
| **4** | 🟢 **High** | A distinct benefit beyond the simpler alternative — a real capability, or the audit trail. |
| **3** | 🟡 **Situational** | Useful in the right context; overlaps with simpler approaches; often brittle. |
| **2** | 🟠 **Supplementary** | Mostly defense‑in‑depth over a *stronger, simpler* control; earns its keep for tools/agents you don't fully control, plus audit. |
| **1** | 🔴 **Marginal** | Rarely worth adopting on its own. |

> In practice the built‑in gates land in the **2–4** band. A **5** would be unique *and* robust; a **1** purely
> redundant. Nothing here is a silver bullet — the score tells you *where each gate pulls its weight*, so you can
> compose a few high‑value gates rather than switching on everything.

---

## Tool gates

Inspect one live tool call at the function‑invocation seam and return **Allow / Block / Mutate**. How a block is
*enforced* is a separate `ToolGatePolicy` (`WarnOnly` → record only; `ReplaceResult` → refusal as the tool
result; `Terminate` → stop the loop), so adding a gate never silently changes behavior.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **ForbiddenToolGate** | Given a tool the agent wants to call, matches the name against a deny‑list (case‑insensitive) and blocks on a match. | 🟠 **2** | If you control the tool list, *just don't give it the tool* — stronger and simpler. Real value is narrow: **tools you don't control** (MCP, plugins, runtime‑registered, multi‑agent handoff) **plus the audit record** of the block. Weak against renamed/aliased tools. → rises to ~🟢 **4** for dynamic/MCP tool sources. |
| **ArgumentPatternGate** | Blocks a call whose serialized *arguments* match a forbidden pattern (path traversal, secret shape, injected command). Bounded‑timeout regex, relaxed JSON encoding, fail‑closed on unserializable args. | 🟡 **3** | You *want* the tool (`read_file`) — you just don't want `/etc/shadow`. "Don't give the tool" can't solve this; the danger is in the argument. But pattern‑matching args is brittle (encoding/obfuscation evades) — good for *known‑bad deterministic* patterns, not a robust filter. |
| **SequenceGate** | Blocks a dangerous *ordered combination*: once a trigger tool runs, a guarded tool is blocked (e.g. `read_secrets` → `send_email` = exfiltration). Per‑run scoped. | 🟢 **4** | Genuinely hard to replace. Each tool alone is fine — you *want* both — but the **sequence** is the attack; no tool‑list trick or arg‑check catches it. Limits: per‑run scoped (a slow multi‑run attack evades) and deterministic (known sequences). |

> **Enforcement floor.** A gate whose purpose is to *stop* an action can declare a `MinimumPolicy`; a honeypot
> refuses to be registered `WarnOnly`, so `UseAgentEvalToolGate` throws rather than let it be downgraded to
> observe‑only.

### Budget & egress (off the `RunLedger`)

`RunLedger` is the per‑run **cross‑hop accumulator** (total tool calls, per‑tool counts, monetary sums) that the
**budget** gate rides — register `UseAgentEvalGate()` so each run gets its own ledger. The **flow‑control** gates
below (referential‑integrity, taint) are stateless: they recompute from the run history (`call.Messages`) per call,
so they need no ledger and can't leak state across runs.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **RunBudgetGate** | Caps a run's budget off the `RunLedger`: total tool calls, per‑tool call count, or the running sum of a monetary argument. Blocks the call that would exceed it. | 🟢🟢 **5** | **Denial‑of‑wallet / runaway‑loop** defense with no tool‑body equivalent — cost accrues across the whole orchestration, so no single tool sees the total. Pure‑code, hot‑path safe; the check + record is one atomic ledger op (correct under concurrent invocation) and a **negative amount can't manufacture headroom** (clamped to 0). It caps tool‑call volume and monetary arguments; token / cost / wall‑clock budgets are out of scope here (they require model‑usage capture). |
| **DomainAllowListGate** | Allow‑list over the URLs in a tool call's arguments; a host not on the list (subdomains allowed) blocks the call. Catches `http(s)` / `ftp` / `ws` **and scheme‑relative** `//host`; fail‑closed on unserializable args / scan timeout. | 🟢🟢 **5** | **Exfiltration** is the payoff of most indirect injection, and an allow‑list is where the literature lands — sub‑millisecond, un‑paraphrasable, and it defends every networked tool from one policy. Resolves the userinfo trick (`https://good.com@evil.com`). **Limit:** it gates URLs it can extract — a *bare hostname* (no `//`) or a `data:` URI isn't detected (validate those in the tool / pair with an argument‑pattern gate), and open web‑browse surfaces degrade to advisory. |
| **ReferentialIntegrityGate** | A guarded (side-effecting) call may only reference ids the **user** provided or a **trusted** lookup surfaced this run; an id no legitimate source introduced blocks the call. | 🟢 **4** | Stops an injection **inventing** an id to act on (`refund #FAKE-9931`). The honest core is the trust model: model-generated content and *untrusted* (poisonable) tool results never confer legitimacy — else the attacker launders the id through the very document carrying the payload. A **tripwire** (heuristic id detection); run `WarnOnly` to measure false alarms first. |
| **TaintTrackingGate** | Coarse information-flow control: a value returned by a confidential **source** tool must not appear in an external **sink** tool's arguments — the tainted call is blocked (the reason never echoes the secret). | 🟡 **3** | Closes the exfil path directly (source → sink) without a keyword list. **Coarse — a tripwire, not a proof:** substring taint, so a transformed/re-encoded value slips past and an incidental shared token can false-alarm; tune `minTaintLength` and run `WarnOnly` first. |

## The moat — your red‑team probes become gates

The most direct expression of the whole toolkit: the **same oracle that scores an attack offline now blocks it
at runtime**. Lives in `AgentEval.RedTeam.Gatekeeper`.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **ProbeEvaluatorGate** | Runs a *deterministic* red‑team `IProbeEvaluator` as a runtime gate. Fail‑closed on the enforcement path: only a clear *Resisted* verdict allows — *Succeeded* (attack) **and** *Inconclusive* (can't tell) both block. Rejects LLM‑backed evaluators at construction. | 🟢 **4** | The **closed loop** — the detector you red‑team with becomes a live guard. A distinctive concept with no simpler equivalent. Ceiling set by "deterministic oracles only inline" (LLM oracles must go to the shadow judge). |
| **CanaryToolGate** | Graduates a red‑team *canary* into a production *honeypot*: `CanaryLure.Tools(...)` advertises a lure tool, and the model *emitting* a call to it is the compromise signal — blocked before the body runs. | 🟢 **4** | A **tripwire, not a filter** — a legit agent never touches the honeypot, so a call is strong evidence the agent was manipulated (a prompt injection landed). Unique detection value. Limit: only catches an agent that takes the obvious bait. |

`ProbeEvaluatorGate` deliberately **inverts** the [grading](../llm-as-judge.md) convention (where abstention must
never be scored as failure) — because a runtime gate that cannot prove a call safe must not run it.

## Run gates

Inspect the run's **input** text (run‑pre — assess incoming attacks) and **output** text (run‑post — catch a
leak), reusing the shipped guardrail `IChatGate`s. Register outermost.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **TokenInjectionGate** | Run‑pre: blocks input containing any configured injection marker / phrase. | 🟠 **2** | A cheap door‑check for *known* phrases — but keyword matching is exactly what this project abandoned for red‑team grading (evadable, low ceiling). Fine as a fast pre‑filter, not a real defense. See [Extending](#extending-the-gatekeeper-llm-backed-detection) for a judge‑backed alternative. |
| **RegexPiiGate** | Run‑post: detects/redacts PII (email, phone, SSN, card, IP) in the response. | 🟡 **3** | Output monitoring/redaction is a real compliance need nothing else here covers. Regex PII is imperfect (misses formats, false positives) but a reasonable deterministic baseline. |
| **SafetyMetricGate** | Adapts any `ISafetyMetric` (e.g. `ToxicityMetric`) into an `IChatGate`, on input and/or output. | 🟡 **3** | Reuse your eval metrics as guards. But most safety metrics are LLM/network cost, so **inline they're rejected** and belong in the shadow judge or a fast‑model run‑pre gate. Inline value limited to cheap metrics. |
| **RenderedOutputExfilGate** | Run‑post: neutralizes exfil channels a client auto‑fetches/hides when it *renders* the answer — markdown image beacons `![](url)`, fetching HTML (`img`/`script`/…), `data:` URIs, zero‑width chars. Redacts under `Redact`. | 🟢 **4** | Closes a real, widely‑exploited channel the tool‑arg allow‑list can't see: a markdown image whose URL carries the secret is fetched *on render*, no tool call involved. Deterministic + fail‑closed on scan timeout. Pairs with `DomainAllowListGate` (args) to cover both egress paths. |

Under a blocking policy a run‑pre refusal is returned *without ever calling the model*. On a stream, a blocking
run‑post gate fails closed at stream start (it can't unsend bytes in flight); under `WarnOnly` a run‑post gate
accumulates the stream and records its evidence *after* — observe‑only.

## The Tribunal — LLM judge gates

When you need *judgment* — the clearest case is **indirect prompt injection** (retrieved content trying to
*instruct* the agent), which keyword gates can't catch *reliably* because the payload is natural language and
endlessly paraphrasable — a single‑axis LLM judge runs on the run‑pre/run‑post seam (which accepts model cost,
unlike the inline tool gate). These live in `AgentEval.Guardrails.Judges`.

**Recall is bounded by the prefilter.** A judge's model is only consulted when its rubric prefilter fires — a
retrieved snippet that trips no signal is allowed without a model call (the same blind spot a keyword list has). Keep
the prefilter conservative and grow your gold set with prefilter‑evading attacks.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **CompositeJudgeGate&lt;TRubric&gt;** | Turns a single‑axis `IJudgeRubric` (prefilter → one‑question prompt → parser) into an `IChatGate` backed by a fast model. Prefilter short‑circuit → model under a hard timeout → decisive verdict; **fail‑closed** on inconclusive (timeout / error / unparseable / non‑finite confidence). | 🟢 **4** | The only gate here that catches *paraphrased / novel* attacks — no deterministic equivalent. But its value is **entirely contingent on calibration**: an un‑calibrated inline judge is a fabrication risk, which the whole toolkit argues is worse than none. The Bar (below) is what earns the rank — without it, treat this as shadow‑only. |
| **ParallelJudgeFanOut** | Runs N judge gates over one turn concurrently (wall‑clock ≈ slowest), combined fail‑closed OR (any block blocks; a throwing judge is itself a block). | 🟡 **3** | Composition, not detection — makes a *multi‑axis* Tribunal viable on the hot path instead of serial K×latency. Compose single‑axis judges here rather than widening one rubric. Value scales with how many axes you run. |
| **JudgeVerdictCache** | Content‑hash cache over a judge gate; caches **only allow** verdicts (a transient fail‑closed block is never cached into a permanent one), bounded. | 🟠 **2** | A token/latency saver for recurring content (RAG scale); no detection value of its own. |

> **The Bar — `GateCalibrationHarness` (the moat, not a gate).** A judge must *earn* the right to block. Score it
> with `GateCalibrationHarness.EvaluateAsync(judge, goldSet)` against a **both‑directions** per‑axis
> `JudgeGoldSet` (attacks that must block AND benign that must be allowed). The report gives decisive accuracy, the
> **missed‑attack (dangerous‑error) count** — the number that matters — the false‑alarm rate, Cohen's κ, and
> (with a baseline) whether it beats a deterministic detector. `report.AssertInlineReady()` throws until it
> passes, so an un‑calibrated judge can't be promoted inline by an honest caller. The flagship `IndirectInjectionJudge`
> bundles this — `CalibrateAsync(model)` scores the `IndirectInjectionRubric` against a canonical 52‑case
> `CalibrationGoldSet()` and the `KeywordOracleGate` baseline; extend the gold set with your own data and re‑run on any
> model/prompt change.
>
> Its accuracy is **your** measurement on **your** data — this toolkit deliberately makes no blanket accuracy
> claim for the judge; the harness is how you find out honestly.

## Session gates

Run before a run and read the run's session — **fail‑closed when the session context is absent**.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **OperatorAuthGate** | Blocks the run unless the session carries an authorized operator identity (allow‑list). | 🟢 **4** | A real **access‑control** primitive — "who may drive this agent" — not solvable by tool‑list control. Only as good as whoever stamps the session identity, but that's true of all authz. |
| **RateLimitGate** | Blocks once more than `maxRuns` runs occur within a window, per session (race‑safe in‑process counter, injectable clock). | 🟢 **4** | Abuse / cost / DoS control — a real operational need, orthogonal to everything else. Standard but genuinely useful. |
| **QuarantineGate** | Blocks a run whose session was *armed for quarantine* by the shadow judge. | 🟡 **3** | The enforcement half of the shadow loop — only meaningful **paired with a shadow judge**. Part of a mechanism, not standalone. |

## Shadow judge

| Capability | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **Shadow judge** (`ShadowJudgePump` + `IShadowJudge`) | Runs the expensive LLM/network checks the inline gates reject, **after** the run returns, on a bounded background pump. An adverse verdict **arms quarantine** so a `QuarantineGate` refuses the *next* run. | 🟢 **4** | The only way to use **powerful** checks (an LLM judge) without stalling the agent — a distinctive pattern. Honest limit: it's **eventual** — it blocks a *future* run, not the one it observed. Detection + future‑prevention, not real‑time block. |

The pump is an **owned** object (`await using`) with a **bounded** queue: under load, items are dropped and
reported — the returning run is never slowed, and a hung network judge cannot hang disposal.

## Tool approval — human‑in‑the‑loop

Routes a *borderline* call to a **human** instead of hard‑blocking, over MAF's native `UseToolApproval`. Only
tools wrapped `.RequiresApproval()` enter the flow. *Experimental (`AEGK001`).*

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **ArgumentPatternApprovalGate** | Auto‑approves *only on positive evidence* of routine arguments (present + not matching the pattern); a parameterless or unserializable‑args call is escalated. | 🟢 **4**† | Fills the gap between "auto‑run" and "block" for actions too risky to auto‑run but too legitimate to forbid (refunds, deletes, transfers). The escalation gate shares the brittleness of its blocker sibling, but the human‑in‑the‑loop *pattern* is high value. |
| **ToolNameApprovalGate** | Escalates a call to a human whenever the tool is on an escalate list (case‑insensitive), regardless of arguments — the way to gate a sensitive *parameterless* tool. | 🟢 **4**† | Identity‑based escalation you can't express with argument patterns. Simple and robust. |

† The **rank is for the human‑in‑the‑loop capability**, which is genuinely valuable; the individual classifier
gates are supporting parts. Fail‑closed: at least one gate is required, and a call is auto‑approved only when
*every* gate affirms it routine — a throwing gate, or a call it can't affirm, escalates.

---

## Extending the Gatekeeper: LLM-backed detection

The inline gates are deterministic by design (they reject LLM/network cost so they never stall the hot path).
When you need *judgment* — the clearest example is **prompt‑injection (PI) detection**, which keyword gates
handle poorly — reach for one of two seams, both of which take a custom gate you write:

- **A fast run‑pre gate.** Wrap a [Composite Judge](../llm-as-judge.md) as an `IChatGate` and register it `pre:` so
  it scores the **incoming prompt** (not the model's response) *before the model is called* and blocks a
  detected injection at the door. Because it is on the hot path, **use a fast, small model** (a *mini* / *nano*
  tier) and a tight rubric so the added latency is a few hundred milliseconds, not seconds.
- **The shadow judge.** For heavier analysis that you don't want on the hot path at all, run the same judge in
  the shadow pump — it evaluates a snapshot off‑path and arms quarantine for the next run.

The same pattern applies to **`SafetyMetricGate`‑style checks on both input and output** (toxicity, jailbreak,
data‑exfiltration intent): a cheap deterministic version can run inline, while an LLM‑judge version belongs in a
fast run‑pre / run‑post gate or the shadow judge.

> **The primitive ships: `CompositeJudgeGate<TRubric>`.** Write a single‑axis `IJudgeRubric` — a cheap prefilter
> (so most turns skip the model entirely) + a one‑question prompt + a parser — and wrap it in a `CompositeJudgeGate`
> backed by a fast (*mini*/*nano*) model; it plugs into the run‑pre seam as an `IChatGate`. It runs the model under
> a hard timeout and **fails closed** on an inconclusive verdict (timeout / model error / unparseable reply),
> citing evidence spans in the gate verdict. **Calibrate before you trust it inline:** because a judge‑as‑a‑gate is
> itself a detection task, score the same rubric against a labelled corpus of injection vs. benign prompts to
> confirm it beats the deterministic baseline before it blocks live traffic (the calibration harness).

## `agenteval doctor` — a double‑gating check

`agenteval doctor` warns when the **same** policy recorded verdicts at both a chat seam (`pre`/`post`) and an
agent seam (`tool`/`run-*`) — a sign the same policy is gating twice. Register it once.
