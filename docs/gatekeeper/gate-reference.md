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
> observe‑only. This means a `MinimumPolicy`-floored gate (e.g. `CanaryToolGate`) **cannot be composed under
> `UseGatekeeper(GatekeeperEnforcement.Observe, ...)`** either — `Observe` always resolves to `WarnOnly`, so the
> same floor check fires there too. That's deliberate, not a bug: running a honeypot under `WarnOnly` would
> silently defeat the trap, so it's excluded from Observe's "zero behavior change" guarantee by design — add it
> separately once you're ready to enforce at its `MinimumPolicy` or stronger.

## Tool RESULT gates (Phase 2, P0‑3)

Every gate above inspects the **proposed call** *before* it runs. Nothing inspected the tool's own **result**
before it flowed back into the model's context — a poisoned web page, file, or API response a tool fetches
can carry an injected instruction, an oversized payload, or a leaked credential that no argument‑side gate
ever sees, because the danger arrives in the *output*, not the *input*. `IToolResultGate` closes that gap: it
runs **after** every `IToolGate` has allowed the call and the tool has actually executed, and returns
**Allow / Block / Redact** over the already‑real result — it can decide what the model gets to see, never
whether the call itself was allowed (the tool already ran).

Register via `UseAgentEvalToolGate(..., resultGates: [...])` or (preferred) `GatekeeperOptions.ToolResultGates`
/ `AddResultGate(...)` through `UseGatekeeper`. A result‑gate‑only configuration (no `ToolGates` at all) is
valid — protecting results doesn't require also gating the proposed calls.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **ToolResultInjectionGate** | Blocks a result containing a configured injection marker (case‑insensitive substring, shares its default list with `TokenInjectionGate`). Not maskable — always **Block**, never **Redact** (the danger is the surrounding instruction text, not a substring you can blank out). | 🟡 **3** | The direct OWASP LLM01 indirect‑injection countermeasure at the one seam nothing else here watches — the tool's own output. Same honest limit as its chat‑side sibling: keyword matching is evadable/paraphrasable, so treat it as a cheap door‑check, not a robust filter — see [Extending](#extending-the-gatekeeper-llm-backed-detection) for a judge‑backed alternative at this seam too. |
| **ToolResultSizeGate** | Truncates an oversized result (default 8,000 chars) to a configurable limit; always **Redact** (a large legitimate result is still useful truncated, not a block‑worthy finding). | 🟠 **2** | If you control the tool, truncate inside its own body — simpler and stronger. Earns its keep for a tool you **don't** control (third‑party MCP tool, uninstrumented API) whose response size you can't bound at the source — → rises to ~🟢 **4** for exactly that case, plus it's the only backstop against a single runaway result silently consuming the context/cost budget for the rest of the conversation. |
| **ToolResultSizeAnomalyGate** | **Not the same gate as `ToolResultSizeGate` above.** A per‑tool, per‑session STATISTICAL outlier detector — flags a result more than Nx (default 5x) *that same tool's own* running average size this run, once enough prior calls (default 3) establish a baseline. v1: fixed multiplier, no real statistics library. Always **Redact**. | 🟡 **3** | Complements the fixed‑threshold gate rather than replacing it: a 50,000‑char result is unremarkable for a bulk‑file‑read tool but wildly anomalous for a tool that has returned ~200 chars all run — behavioral drift a global threshold can't see. Same honest ceiling as any threshold‑based heuristic: a slow, gradual size creep evades a fixed multiplier; v2 (rolling mean/stddev or median‑absolute‑deviation) is a documented follow‑on, not built here. |
| **ToolResultSecretGate** | Detects and masks common credential SHAPES (AWS/GitHub/Slack/Google/Stripe keys, PEM private‑key blocks, bearer tokens, JWTs) in a result; always **Redact** on a match — the rest of the result stays useful with just the credential blanked out. | 🟡 **3** | Same honest ceiling as `RegexPiiGate`: a real, useful deterministic baseline for "a fetched log/config/error dump happens to carry a live secret," but shape‑based regex has known blind spots (a secret in an unrecognized format, or deliberately obfuscated, slips through). Pairs with `DomainAllowListGate`/`TaintTrackingGate` for the *destination* half of the same exfiltration story. |

> **Policy reinterpretation for a post‑execution subject.** `ToolGatePolicy` is reused (not a second enum) but
> means something adjacent here: `WarnOnly` records the finding but still returns the **real** result (the tool
> already ran — there's nothing left to "warn instead of run"); `ReplaceResult`/`Terminate` swap in the same
> non‑revealing `{error, referenceId}` refusal shape call gates use. A `Redact` verdict is **always** applied
> regardless of policy — mirrors `ToolGateAction.Mutate`'s "always applied" precedent: a gate offering a safer
> version of real content isn't a decision an enforcement policy should gate. Recorded under the `gate.tool-result.*`
> trace stage (not `gate.tool.*`) so a result‑gate finding is distinguishable from a call‑gate finding while
> still counted by the same stage‑agnostic `GlassBoxEvidence.CountGateBlocks`.

## HTTP egress enforcement (Phase 2, #10) — `GatekeeperHttpMessageHandler`

`DomainAllowListGate` (below) candidly documents what it *cannot* see: it inspects the URL **string** inside a
tool call's arguments, never the actual outgoing network request. Two real attacks live exactly in that gap —
neither is theoretical, both are standard SSRF/exfiltration technique:

- **Redirect bypass.** An allow‑listed URL responds `302 Location: http://internal-service/` — the argument
  the model wrote was fine; where the request actually ends up isn't.
- **DNS‑rebinding.** An allow‑listed hostname's DNS answer resolves to a private/internal address (the
  cloud‑metadata endpoint `169.254.169.254` is the canonical target) — the hostname string was never wrong,
  the network topology behind it was.

`GatekeeperHttpMessageHandler` is a real `DelegatingHandler` that sits underneath whichever `HttpClient` a
**tool's own implementation** uses (not the MAF function‑invocation seam `IToolGate`/`IToolResultGate` plug
into — this is a different composition point, see below) and closes both gaps:

| Mechanism | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **`GatekeeperHttpMessageHandler`** | Validates the allow‑list (same exact‑or‑subdomain semantics as `DomainAllowListGate`, via the shared `HostAllowList` helper) AND resolves DNS, blocking if any answer is private/loopback/link‑local/reserved (`PrivateNetworkClassifier`) — before every hop, including every redirect. Redirects are followed manually (never delegated to the transport's own auto‑redirect), re‑validating the new target at each hop, bounded by `MaxRedirects`. Every block throws `HttpEgressBlockedException` — fail‑closed, the idiomatic signal for an `HttpMessageHandler`. | 🟢 **4** | A **genuinely unique capability** — no argument‑string scan can ever catch a redirect target or a DNS answer, because neither exists until the request actually goes out. Ceiling relative to a 5: it isn't automatically applied the way a `UseGatekeeper`‑registered gate is — a tool that builds its own `HttpClient` without `CreateHttpClient` below isn't protected. Composition is opt‑in per tool, not per agent. |

**Usage — wrap the tool's own `HttpClient`, don't register it with `UseGatekeeper`:**

```csharp
var httpClient = GatekeeperHttpMessageHandler.CreateHttpClient(["api.mycompany.com", "stripe.com"]);
// Build your AIFunction's tool implementation around THIS client, not a bare `new HttpClient()`.
var fetchTool = AIFunctionFactory.Create(async (string path) =>
{
    var response = await httpClient.GetAsync($"https://api.mycompany.com{path}");
    return await response.Content.ReadAsStringAsync();
}, "fetch_order");
```

`CreateHttpClient` builds a `SocketsHttpHandler` with `AllowAutoRedirect = false` for you (required — the
handler's own redirect re‑validation only ever runs if the transport itself never auto‑follows a 3xx first) and
wraps it in `GatekeeperHttpMessageHandler`. Pass a `GatekeeperHttpEgressOptions` to tune `MaxRedirects` (default
5), turn off the private‑network check (`BlockPrivateNetworks = false`, e.g. for a deliberately internal
allow‑listed service), or substitute `DnsResolver` (mainly for tests — the default wraps the real
`System.Net.Dns`).

**Composes naturally with `DomainAllowListGate` and the result gates above** — they're the same story at three
different points: `DomainAllowListGate` catches the URL in the *proposed call*, `GatekeeperHttpMessageHandler`
catches what actually happens on the wire, and `ToolResultSecretGate`/`ToolResultInjectionGate` catch what came
back. Registering only one is a real gap the other two would have caught.

### Budget & egress (off the `RunLedger`)

`RunLedger` is the per‑run **cross‑hop accumulator** (total tool calls, per‑tool counts, monetary sums) that the
**budget** gate rides — register `UseAgentEvalGate()` so each run gets its own ledger. The **flow‑control** gates
below (referential‑integrity, taint) are stateless: they recompute from the run history (`call.Messages`) per call,
so they need no ledger and can't leak state across runs.

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **RunBudgetGate** | Caps a run's budget off the `RunLedger`: total tool calls, per‑tool call count, or the running sum of a monetary argument. Blocks the call that would exceed it. | 🟢🟢 **5** | **Denial‑of‑wallet / runaway‑loop** defense with no tool‑body equivalent — cost accrues across the whole orchestration, so no single tool sees the total. Pure‑code, hot‑path safe; the check + record is one atomic ledger op (correct under concurrent invocation) and a **negative amount can't manufacture headroom** (clamped to 0). It caps tool‑call volume and monetary arguments; token / cost / wall‑clock budgets are out of scope here (they require model‑usage capture). |
| **MonetaryLimitGate** | Dedicated sibling of `RunBudgetGate`'s monetary cap: caps the running sum of one monetary argument (e.g. `"amount"`) across a run — its own `PolicyName`, its own isolated `RunLedger` dimension. Blocks the call that would exceed it. | 🟢 **4** | The economic sibling of `RunBudgetGate`, for when you want a *focused* gate over payment/refund/transfer tools without also wiring total/per‑tool caps. Same fail‑closed‑on‑unparseable / negative‑amount‑clamped discipline; the block reason names the argument and the *configured* cap only, never the attempted amount — evidence can't be used to infer transaction values. Writes an isolated ledger dimension so stacking it with `RunBudgetGate` (even over the same argument name) can't double‑count. |
| **PerToolCallBudgetGate** | Dedicated sibling of `RunBudgetGate`'s per‑tool cap: caps how many times specific tools may be called in one run (e.g. `["delete_account"] = 1`, `["send_email"] = 3`) — its own `PolicyName`, its own isolated `RunLedger` dimension. | 🟢 **4** | Blunts loops and spray attacks on high‑blast‑radius tools without wiring a full `RunBudgetGate`. A tool not named in the caps is unconditionally allowed by this gate — pair with a broader budget for full coverage. Isolated ledger storage means composing it with `RunBudgetGate` (even over the same tool name) can't cross‑contaminate either gate's count. |
| **DomainAllowListGate** | Allow‑list over the URLs in a tool call's arguments; a host not on the list (subdomains allowed) blocks the call. Catches `http(s)` / `ftp` / `ws` **and scheme‑relative** `//host`; fail‑closed on unserializable args / scan timeout. | 🟢🟢 **5** | **Exfiltration** is the payoff of most indirect injection, and an allow‑list is where the literature lands — sub‑millisecond, un‑paraphrasable, and it defends every networked tool from one policy. Resolves the userinfo trick (`https://good.com@evil.com`). **Limit:** it gates URLs it can extract — a *bare hostname* (no `//`) or a `data:` URI isn't detected (validate those in the tool / pair with an argument‑pattern gate), and open web‑browse surfaces degrade to advisory. |
| **ReferentialIntegrityGate** | A guarded (side-effecting) call may only reference ids the **user** provided or a **trusted** lookup surfaced this run; an id no legitimate source introduced blocks the call. | 🟢 **4** | Stops an injection **inventing** an id to act on (`refund #FAKE-9931`). The honest core is the trust model: model-generated content and *untrusted* (poisonable) tool results never confer legitimacy — else the attacker launders the id through the very document carrying the payload. A **tripwire** (heuristic id detection); run `WarnOnly` to measure false alarms first. |
| **TaintTrackingGate** | Coarse information-flow control: a value returned by a confidential **source** tool must not appear in an external **sink** tool's arguments — the tainted call is blocked (the reason never echoes the secret). | 🟡 **3** | Closes the exfil path directly (source → sink) without a keyword list. **Coarse — a tripwire, not a proof:** substring taint, so a transformed/re-encoded value slips past and an incidental shared token can false-alarm; tune `minTaintLength` and run `WarnOnly` first. |
| **SkillScriptExecutionGate** | MAF Agent Skills: deterministic hard gate on `run_skill_script`. Blocks a call whose script identifier is not on the allowlist — value‑based, key‑agnostic matching (fails closed on an unrecognized/missing script identifier, never on a guessed argument key). `MinimumPolicy` floors at `ReplaceResult` (can't be silently downgraded to `WarnOnly`). | 🟢 **4** | Code execution is the highest‑blast‑radius skill surface — this is the deterministic hard stop, no calibration debt. Must be paired with an auto‑approval posture (`AllToolsAutoApprovalRule` / `DisableRunSkillScriptApproval`) so `run_skill_script` reaches the FICC seam at all — otherwise MAF's own approval pause fires first and this gate never runs (see the Skills docs). |

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
> bundles this — `CalibrateAsync(model)` scores the `IndirectInjectionRubric` against the canonical both‑directions
> `CalibrationGoldSet()` and the `KeywordOracleGate` baseline; extend the gold set with your own data and re‑run on any
> model/prompt change.
>
> Its accuracy is **your** measurement on **your** data — this toolkit deliberately makes no blanket accuracy
> claim for the judge; the harness is how you find out honestly.

### Shipped Tribunal judges

Beyond the flagship `IndirectInjectionJudge` (above), three more single-axis judges ship the same bundle — rubric +
`CompositeJudgeGate<TRubric>` (optionally cached) + canonical both-directions gold set + keyword-oracle baseline —
over `AgentEval.Guardrails.Judges`. Each is placed **run-post** (scores the rendered output, via
`UseAgentEvalGate(post: […], policy: EvalGatePolicy.Redact)` once calibrated — `policy` has no implicit default;
it's required whenever any gate is registered) and each sits behind the **same calibration bar** as the flagship:
don't wire one inline until `CalibrateAsync` reports `IsInlineReady`.

| Judge | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **ExfiltrationIntentJudge** | Scores **exfiltration intent** in the output — reads context a keyword scan can't ("this customer's SSN, dropped here, then told to paste it into a paste site"). Run‑post; **blocks once calibrated**. Ships a 22‑attack / 24‑benign gold set (`ExfiltrationIntentRubric.CalibrationGoldSet()`) and a `DefaultExfilKeywords` keyword‑oracle baseline it must beat. | 🟢 **4** | Same ceiling as `CompositeJudgeGate<TRubric>` above — entirely contingent on calibration; an un‑calibrated instance is a fabrication risk, not a control. Pairs with the deterministic `DomainAllowListGate` (destination) and `TaintTrackingGate` (known‑secret provenance) for defense in depth — this judge covers the "is this data sensitive *in context*" half neither of those two can decide. |
| **SystemPromptExtractionJudge** | Scores **system‑prompt leakage / paraphrase** in the output. Run‑post; **blocks once calibrated**. Ships a 22‑leak / 24‑benign gold set (`SystemPromptExtractionRubric.CalibrationGoldSet()`) — including the hard‑negative of a *refusal* to reveal the prompt ("I can't reveal my system prompt, but I can explain what I'm able to help with") correctly labeled **benign**, not a leak. | 🟢 **4** | Same calibration‑contingent ceiling. Meant to be **hybridized with a deterministic canary token** planted in the system prompt: the canary catches an exact echo cheaply and deterministically; this judge is what catches the *paraphrased* leak the canary can't see. Ships a `DefaultLeakTells` keyword‑oracle baseline it must beat. |
| **OverRefusalJudge** | Flags a wrongful **refusal** in the output — the fleet's *utility valve* against a stack of fail‑closed judges/gates trending toward block‑everything. Ships a 22‑over‑refusal / 24‑non‑refusal gold set (`OverRefusalRubric.CalibrationGoldSet()`) and a `DefaultRefusalMarkers` keyword‑oracle baseline. | 🟡 **3** | **Advisory only — must be wired `WarnOnly`** (`UseAgentEvalGate(post: [judge], policy: WarnOnly)`), **never blocking**. A positive verdict is a *flag* (recorded as a `gate.run-post.*.judge:over-refusal` warning, feeding an offline false‑refusal metric or a retry path) — never an enforced block; hard‑blocking a refusal would punish honesty, the opposite of the point. Rank reflects the value of the *signal*, not a blocking capability there is deliberately none of; the same calibration bar applies before you trust the flag. |
| **IntentActionMismatchJudge** | Compares what the agent NARRATED it would do against what its tool call ACTUALLY does; vetoes on divergence. Ships a 26‑mismatch / 26‑consistent gold set (`IntentActionMismatchRubric.CalibrationGoldSet()`). **Live-calibrated 2026-07-16: 100% decisive accuracy, κ=1.000, `IsInlineReady=true`.** | 🟢 **4** | Turns the agent's own narration into an oracle — catches an injected instruction that hijacks the ACTION while narration keeps describing the original benign task. **Evadable if the agent stops narrating** (documented, honest limitation) — pair with deterministic argument-pattern gates for defense in depth when narration is unavailable. |
| **GoalHijackDriftJudge** | Detects the agent being steered off the user's ORIGINAL stated goal toward a different, injected objective — distinct from `IndirectInjectionJudge` (which asks "does this content instruct," not "has the agent's direction actually drifted"). Ships a 24‑hijack / 24‑on‑goal gold set. **Live-calibrated 2026-07-16: 100% decisive accuracy, κ=1.000, `IsInlineReady=true`.** | 🟢 **4** | Catches a successful hijack even turns downstream of the original injection, when the injected text itself is no longer in view. Run‑pre, comparing the session's original goal against the agent's current direction. |
| **UngroundedClaimJudge** | RAG faithfulness as a runtime gate: flags an answer CLAIM not supported by the RETRIEVED CONTEXT it was grounded in — the enforcement counterpart to the offline Faithfulness metric. Ships a 24‑ungrounded / 24‑grounded gold set. **Live-calibrated 2026-07-16: 100% decisive accuracy, κ=1.000, `IsInlineReady=true`.** | 🟢 **4** | Blocks a hallucinated RAG answer before it reaches the user instead of only scoring it after the fact offline. Hard-negatives include a hedged personal opinion (not a new fact claim), so it doesn't over-block reasonable inference. |
| **HallucinatedCitationJudge** | **Hybrid, not a plain rubric** — a bespoke `IChatGate` (not `CompositeJudgeGate<TRubric>`): a deterministic, zero-LLM-cost check ("does the cited source exist among what was retrieved?") composed with a judge support-check ("does that source's content actually support the claim?") only when the citation exists. Ships a 26‑hallucinated/unsupported / 26‑valid gold set covering BOTH failure modes. **Live-calibrated 2026-07-16: 100% decisive accuracy, κ=1.000, `IsInlineReady=true`.** | 🟢 **4** | Catches the nonexistent-citation case for free (no model call at all) and only spends a model call on the harder "does it actually support this" question. **Not registered in the CLI bridge's `judge:*` axis registry** (no `IJudgeRubric` to plug into the registry's parse-only-inspect path) — fully usable directly, just not through that specific CLI surface yet. |

### Overlooked seams (Section D)

Two gates guarding seams every OTHER gate above misses:

| Gate | What it does | Rank | Honest reasoning |
|---|---|:--:|---|
| **MemoryWritePoisoningGate** | Guards the memory/vector-store **WRITE** side — every injection judge above guards READS. Reuses `IndirectInjectionRubric` **verbatim** (same class, same prompt, same axis) at this new seam, per the design backlog's "reuse the pattern" guidance, rather than authoring a new rubric. | 🟢 **4** | Stored-memory poisoning has a time-delayed, cross-session, cross-user blast radius no read-side gate can see — the poison isn't dangerous until LATER retrieved and re-injected into a different context/user. Only relevant to agents with persistent memory. Reuse is a documented choice, not laziness: the semantic question is axis-invariant to where the text came from, and the caller should still run their own calibration on their actual memory-write traffic shape before an inline claim. |
| **McpToolDescriptionPoisoningGate** | Treats an MCP tool's DEFINITION (name/description/schema) as untrusted, model-visible attacker text. Deterministic hash-pin-and-diff (`AgentEval.Guardrails.ManifestFingerprint`/`ManifestDriftDetector` — the SAME generic primitive that backs Skills' `SkillManifestPoisoningGate`, applied to a different artifact type). Fires per-registration, not per-turn. | 🟢 **4** | Catches a rug-pull description swap cheaply and deterministically — no calibration debt. Schema comparison recursively canonicalizes JSON key order so a reformatted-but-unchanged schema never false-alarms. Deliberately takes no MCP client library dependency — `McpToolDefinition` is a caller-populated DTO. |

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
| **SkillScriptApprovalGate** | MAF Agent Skills: auto‑approves `load_skill`/`read_skill_resource` always; escalates `run_skill_script` to a human UNLESS its script is on a per‑script trust allowlist — finer‑grained than MAF's native `ReadOnlyToolsAutoApprovalRule` (tool‑granularity only). | 🟢 **4**† | The soft sibling of `SkillScriptExecutionGate` — same seam, human‑in‑the‑loop instead of a hard block. Composing BOTH is a design decision, not a mistake: pick Posture A (auto‑approve + deterministic gate) to demo the gate, or Posture B (per‑script trust + escalation) for human review — never conflate the two in one trace read. |

† The **rank is for the human‑in‑the‑loop capability**, which is genuinely valuable; the individual classifier
gates are supporting parts. Fail‑closed: at least one gate is required, and a call is auto‑approved only when
*every* gate affirms it routine — a throwing gate, or a call it can't affirm, escalates.

---

## Prompt-template drift — a construction-time-only guard, not a runtime gate

`PromptTemplateDriftGate` is the **third** application of the `ManifestFingerprint`/`ManifestDriftDetector`
primitive (after `SkillManifestPoisoningGate` for skill manifests and `McpToolDescriptionPoisoningGate` for MCP
tool schemas), applied to an agent's prompt template files — hash-pins a trusted template's content and diffs
it against the pin. Unlike every gate above, it is **not** an `IToolGate`/`IChatGate`/`IToolResultGate` at
all — a prompt template doesn't change mid-run, so a per-turn check would be pure waste. Instead, set
`GatekeeperOptions.PromptTemplates` (the current content, keyed by a caller-chosen identifier — typically just
the system-prompt file) and `GatekeeperOptions.PromptTemplateBaseline` (a prior, reviewed
`PromptTemplateDriftGate.CaptureBaseline(...)` snapshot); when both are set, `UseGatekeeper` checks drift
**eagerly at construction time** and throws `PromptTemplateDriftException` immediately if any pinned
template's content changed — fail-closed, the same pattern as `RefuseUnprotectedHighRiskTools`. Setting only
ONE of the two options throws `InvalidOperationException` at construction rather than silently no‑op‑ing —
same fail‑loud‑on‑half‑configuration discipline as `RefuseUnprotectedHighRiskTools` + a missing `KnownTools`.
A template present in only one of the two DICTIONARIES (added/removed, once both options ARE set) is not
treated as drift, only a changed fingerprint for a template present in both is — that's the actual tamper
signal this guard exists to catch.

## Calibration staleness & the Gatekeeper Fleet Health Index

Two more next-wave additions, both about the health of the judge fleet itself rather than any single gate
decision.

**Staleness.** `CalibrationReport` (the output of `GateCalibrationHarness.EvaluateAsync`) now carries
`CapturedAt` (a `DateTimeOffset`) and `IsStale(maxAge, clock?)` — a report older than the caller-chosen
threshold is flagged stale. This is **informational only**: staleness never affects `IsInlineReady` or
auto-demotes an already-promoted judge — it's a signal to re-calibrate, not itself a promotion-blocking
condition. A judge calibrated once and never re-checked looks identical to a freshly-proven one without this
field; `IsStale` is what lets a caller (or the Fleet Health Index below) actually notice.

**Persistence.** `CalibrationReport` was, until this addition, a purely in-memory return value — nothing
persisted it between runs. `ICalibrationReportStore` / `JsonFileCalibrationReportStore` is a small, deliberately
minimal persistence seam: **one report per axis** (the most recent calibration run), overwritten on each
`SaveAsync` — not an append-only historical ledger. (A full multi-snapshot history ledger is a separate, larger
shape — see the Agent Skills baseline ledger for that pattern, deliberately not duplicated here; the Fleet
Health Index below only ever needs "what does this axis look like right now," not a time series.)

**`GatekeeperFleetHealthIndex.Compute(reportsByAxis, staleAfter, clock?)`** joins every tracked judge axis's
latest report (typically sourced from `ICalibrationReportStore.LoadLatestAllAsync`) into one composite
fleet-health view — mirroring `SkillSecurityIndex`'s honesty discipline exactly: an axis with no report at all
is **never** fabricated into a passing score. Pass the full expected axis set (including axes that have never
been calibrated) so the report can actually surface the gap:

- `MeanDecisiveAccuracy` / `MeanKappa` — computed only over axes that actually have a report; `null` if zero
  axes are calibrated, never defaulted to a number.
- `TotalDangerousErrors` — summed across calibrated axes only, the number that matters most per this repo's
  grading motto.
- `NeverCalibratedAxes` — axes present in the input dictionary with a `null` report.
- `StaleAxes` — axes whose report is older than `staleAfter`, informational, does not affect the means.
- `Explanation` — a human-readable one-liner stating plainly which axes were/weren't measured.

This type is transport-agnostic (`AgentEval.Core`, no CLI or Mission Control dependency) — high value once an
ops-facing surface exists to put it on (Mission Control's compliance-matrix GraphQL layer is a real, live
precedent for exactly this kind of cross-cutting health view), but there's no CLI verb or Mission Control
wiring for it yet. Compute it directly against `ICalibrationReportStore.LoadLatestAllAsync()`'s result today.

## Composing gates safely — `UseGatekeeper`

Wiring more than one layer by hand (`.UseAgentEvalGate()` → `.UseAgentEvalToolGate(...)` →
`.UseAgentEvalToolApproval(...)` → `.UseAgentEvalShadowJudge(...)`) means getting the order right yourself, and
some misconfigurations run silently instead of failing loudly. `UseGatekeeper(enforcement, configure)` installs
all four in the correct order in one call, and refuses to construct — before any middleware is wired, never
partially — for the two ways manual composition can silently misbehave: a `GateRequirements.RunScope` gate
registered without a guaranteed run scope (falls back to shared, process-wide state instead of throwing), and a
`MinimumPolicy`-floored gate registered under an enforcement level too weak to meet it (see the callout above).
`GatekeeperEnforcement` (`Observe`/`ReplaceResult`/`Terminate`) is a required parameter — there is no default,
on purpose. Full walkthrough: [introduction — Wiring it together](introduction.md#wiring-it-together-usegatekeeper).

> **⚠ Never chain two separate `UseAgentEvalToolGate(...)` calls on one builder** — the later registration
> becomes the outermost middleware layer and can fully starve the earlier one's gates if it blocks without
> forwarding (empirically confirmed against the underlying MAF seam). Register every tool gate in one call, in
> one list — `UseGatekeeper` already does this for you.

## Coverage & telemetry

Two capabilities answer "is this actually working," not "did I call the API":

| Capability | What it does | Honest reasoning |
|---|---|---|
| **`GatekeeperCoverageAnalyzer`** | Classifies every tool exposed to the agent's model — `InterceptedLocalFunction` (a local `AIFunction`, the only execution model any tool gate can ever see) vs. `ProviderHostedOpaque` (executed by the model provider itself, structurally invisible to every mechanism in this namespace) — and a coarse `ToolRiskLevel` heuristic (`ToolRiskClassifier`, keyword-based, override via `AnalyzeOptions.IsHighRisk`). Reports `EnforcementCoveragePercent`; `AnalyzeOrThrow`/`GatekeeperOptions.RefuseUnprotectedHighRiskTools` refuse construction on an unprotected high-risk tool. | The "false assurance" fix: "Gatekeeper is installed" reads as "my tools are protected," which isn't automatically true. **Honest limits, not just a feature:** blind to a tool an `AIContextProvider` (Agent Skills, memory providers) contributes dynamically — only the static `ChatOptions.Tools` list is inspectable; blind to a tool's parameter schema (a generically-named dispatch tool whose `operation` parameter includes `"delete"` isn't flagged); and `AnalyzeOrThrow` itself throws `ToolInventoryUnavailableException` (not a silent pass) when the tool list can't be read at all — "I couldn't verify" fails the same direction as "I verified and it's bad." |
| **`GateTelemetry`** | A caller-owned counter/histogram (pass it to `UseAgentEvalToolGate`/`UseGatekeeper`): per-gate invocation count, allow/block/mutate counts, and latency. Records what the gate *found* (its verdict), not whether the enforcing `ToolGatePolicy` actually blocked the call — cross-reference the trace's `gate.tool.*` `action` (`"Block"` vs `"Warn"`) for the enforced/observed split. | Pairs with the coverage analyzer: coverage answers "is this tool structurally reachable by a gate," telemetry answers "when it ran, what did the gate actually do." No I/O, no new subsystem — a thin surface bolted onto the existing per-call gate loop. |

`Mutate` verdict evidence (a tool gate rewriting arguments before the call proceeds) is captured into the trace
per `TraceCaptureMode` — `None`/`SchemaOnly`/`Redacted`/`Hashed`/`Full`. **`Redacted` is the default** (argument
names visible, values replaced with a fixed marker) — a prior version always recorded arguments verbatim, which
could put a secret an argument carries into the trace. Pass `TraceCaptureMode.Full` explicitly only when you
know your arguments never carry secrets and want the exact before/after values for debugging.

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
