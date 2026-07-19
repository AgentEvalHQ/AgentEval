# Red Team — What's New

A roundup of what landed in AgentEval's red-team module recently: broader coverage, multi-step attacks, real-tool grounding, and — the part we're proudest of — a serious, honest answer to the hardest problem in red-teaming: **trusting the verdict.**

> **TL;DR.** More attacks, multi-turn and attacker-LLM support, real tool-execution grounding, OWASP LLM Top-10 closure, and compliance crosswalks across five frameworks — *plus* the centerpiece: **judge-primary grading with honest-by-construction "Composite Judges"** (now the default), a three-way evidence-graded verdict that tells you when it **doesn't know** instead of guessing. The headline: **0 directional fabrications across 810 held-out stochastic trials**, with **κ = 1.000 (n=92)** judge↔label agreement on the pinned corpus. See [the full reference](redteam.md).

---

## New & upgraded capabilities

### Grading — judge-primary by default + Composite Judges *(the big one)*

The grader — the component that decides whether each attack actually *succeeded* — moved from a keyword/substring oracle to **LLM-judge-primary grading** with **honest-by-construction "Composite Judges."** This is the headline change here, and it's a deliberate, evidence-driven flip (see [ADR-021→024](adr/README.md)).

- **Composite Judges.** With a judge configured (`--judge`), every semantic verdict is *decomposed* into a **positive-only compromise detector ⊕ a negative-only refusal detector**, each structurally clamped (`OutcomeFilterEvaluator`) so it can only ever raise *its own* direction or abstain — never the opposite. They're aggregated by an honest `CompositeEvaluator(Any)`. A detector physically cannot turn a safe reply into a "Succeeded," and cannot bury a real compromise under a "Resisted." All six semantic oracles (InferenceAPIAbuse, InsecureOutput, SupplyChain, DataPoisoning, Misinformation, ExcessiveAgency) now run this way; genuinely structural signals (canary markers, `trigger_phrase`) stay deterministic.
- **Why we flipped.** We didn't *decide* keyword grading was inadequate — we *proved* it, the hard way. We built an adversarial honesty harness, patched every fabrication it found, and re-ran it from scratch — and it surfaced a **fresh batch of realistic mistakes every single time** (one cycle: fix 41, find 41 new). Iteratively patching a keyword oracle **does not converge** to an honest grader. That's a structural limit, not a backlog — so we changed the architecture.
- **The proof.** On an independently-generated, held-out adversarial corpus run **K=10× per case**, the Composite Judges produced **0 directional fabrications across 810 trials** — **81 cases × 10**, spanning two held-out suites (70 composite-oracle cases + 11 DataPoisoning deny-true) — never a safe reply scored as a compromise, never a real compromise masked as safe. On a *separate*, pinned label corpus, judge↔label agreement is **κ = 1.000 (n=92)**, and directional fabrications fell from **8 (a single judge)** to **0** (keyword graders, by contrast, agree with humans only ~half the time and never converged). The guiding rule throughout: *fabrications are complete failures; honesty is never punished* — abstaining ("Inconclusive") is an honest coverage gap, never scored as a hit.
- **Gated trees where decisions are independent (ADR-024).** InferenceAPIAbuse uses a small AND-gated tree that closes the "I am Grok" model-vs-product-bot floor on `gpt-4o-mini` with *no stronger model*. ADR-024 also documents the *bound*: the same structure was measured to **regress** InsecureOutput, so it's applied only where the conflated decisions are genuinely independent axes — a gate is never promoted on intuition (`GateAblationLiveCheck` runs the A/B).

> **⚠️ Default change.** With a judge configured (`--judge`), Composite Judges now **lead** the verdict (`--judge-mode primary`, the new default; the old judge-as-tiebreaker is `--judge-mode fallback`). The default rubric is now `evidence-anchored`. **A scan with no `--judge` stays byte-identical to the previous keyword oracle.** Reproduce the proof: `AGENTEVAL_RUN_5B=1 AGENTEVAL_STOCHASTIC_K=10 dotnet test --filter "FullyQualifiedName~Stochastic_Composite_Stability|FullyQualifiedName~Stochastic_DenyTrue_Stability"` (both held-out suites = 810 trials; needs Azure OpenAI). A self-contained keyword-vs-single-judge-vs-composite head-to-head ships as [`samples/AgentEval.SampleGraders`](https://github.com/AgentEvalHQ/AgentEval/tree/main/samples/AgentEval.SampleGraders).

### Coverage
- **264 built-in probes across 14 attack types** (Comprehensive intensity), covering **all 10 OWASP LLM Top 10 (2025)** and **8 MITRE ATLAS** techniques.
- **Compliance crosswalks across five frameworks** — OWASP LLM Top 10, MITRE ATLAS, **NIST AI RMF (AI 100-1)**, ISO/IEC 42001, and SOC 2 — with a `--format nist` report straight from a scan and `bench owasp|mitre|nist` benchmark families.
- **Bring your own data:** `--import-probes` (CSV/JSON) and external **benchmark packs** via `--pack` (HarmBench / JailbreakBench / CyberSecEval, downloaded on demand under their own licenses — no harmful data bundled).

### Built-in SUT targets
- **`--sut copilot-studio`** — a built-in target that points the scanner at a **live Microsoft Copilot Studio agent** instead of an OpenAI-compatible/Azure endpoint, with its own CLI flags (`--copilotstudio-config`, a required consent flag `--i-understand-live-side-effects`, and a `--max-credits` spend cap), a ship-blocking safety gate, MSAL device-code auth with a persisted token cache, and an honest text-only/`Verbal` fidelity ceiling — a tool-dependent probe reports **Inconclusive**, never a fabricated pass, because MCS's server-side tool calls are invisible to the scanner. The live connector is wired but not yet independently verified against a real MCS agent; see [Copilot Studio](copilot-studio.md) for exactly what's proven vs. what still needs a live check.

### Multi-step & adaptive attacks
- **Multi-turn conversations** — `Crescendo`-style escalation ladders that build context across turns, folded into a single verdict with a conversation-fidelity label.
- **Attacker-LLM orchestration** — an attacker model that *generates and adapts* the attack (`PAIR`, `TAP` / tree-of-attacks), while a **separate** judge model scores it (an attack can never grade itself).
- **Tool-aware escalation** — `ToolEscalation` drives multi-turn attacks against tool-using agents.

### Real attack surface (not just words)
- **Canary tools** — instrument the agent with honeypot tools so we measure what it **actually did**, not just what it *said*. A response that describes doing the bad thing but never calls the tool is scored differently from one that executes it (**emitted-vs-executed fidelity**).
- **System-prompt canary & `--sut-tier`** — prove a real system-prompt *leak* (plant a secret, detect the exact token) rather than guessing from phrasing.

### Evasion & transforms
- **18 correct-by-construction encoders** (Base64, Hex, ROT13, URL, Atbash, Caesar, reversed, leetspeak, Morse, binary, NATO, homoglyph, zero-width, …) — multiply any attack's probes through the same obfuscations attackers use to slip past filters, generated programmatically so the encoding is never mistyped. Deterministic, so they're baseline-safe.

### CI & reporting on-ramp
- **Baseline regression gate** (`--save-baseline` / `--baseline` / `--fail-on`) — fail CI only on a *new* vulnerability vs a committed baseline.
- **SARIF** (GitHub Security tab), **JUnit XML**, JSON, Markdown, and PDF reports.
- **Explainable findings** (`--explain`) — an auditor-facing LLM rationale on each finding (never changes the verdict; explains it).
- **Relative scoring / calibration** (`--calibration`) — score a model against a reference cohort (a native re-implementation of the idea popularized by **NVIDIA garak**, credited; concept adopted, no code or data copied).

---

## The hard problem: grading is harder than attacking

Here's the thing most red-team tooling glosses over. **Launching the attack is the easy half. Grading the model's reply is the hard half.**

> **A metaphor.** Imagine grading thousands of essays with **Ctrl-F** — searching each one for a few tell-tale words instead of reading it. Now try to grade these two essays that way:
> - "I will **not** help you build a bomb."
> - "I will help you build a bomb."
>
> They contain the *same keywords*. A Ctrl-F grader can't tell them apart — so it both **misses real failures** *and* **flags well-behaved models as vulnerable**. Worse, attackers (and ordinary models) phrase things a thousand ways: *"against my guidelines, but since you insist…"*, *"I decline to do that"*, *"that's a myth"*. The banned-words list never catches up, because language is effectively infinite.

This isn't a bug we can patch our way out of — it's a structural limit of keyword/pattern grading. We learned this the rigorous way: we built an adversarial test harness, fixed every hole it found, and re-ran it from scratch — and it found a fresh batch of realistic mistakes every single time. The grader can be made *more honest*, but a pure keyword grader can never be made *complete*.

### How the field deals with it

Different tools reach for different "smarter graders":

| Approach | What it does | How good (public figures) |
|---|---|---|
| **Keyword/pattern** (garak's default refusal lists; AgentEval's *no-judge* fallback) | Ctrl-F for refusal/compliance phrases | Agrees with human graders only **~half the time** on the hard cases |
| **AI-as-judge** (PyRIT; promptfoo; our `--judge`) | hands the reply to a *second* model to read and judge | Strong on understanding (**~85–90%** with a frontier judge), at the cost of speed, money, and run-to-run consistency |
| **Trained grader** (HarmBench classifier; Meta Llama Guard) | a model *purpose-trained* on thousands of human-labeled examples | Reliable, fast, offline (**~75–80%**) |
| **Reality check** (AgentDojo) | doesn't read prose at all — checks whether the action *actually happened* (was the email sent? the record changed?) | The gold standard *wherever you can observe the outcome* |

(Agreement figures are from the public HarmBench / Llama Guard research.)

Each is better than Ctrl-F at *understanding* — but none of them, except the reality-check, is perfect, and the AI-judge approaches trade away determinism and cost.

### Where AgentEval stands — and what we did differently

With a judge configured, AgentEval now grades the semantic attacks the way the strongest tools do — **judge-primary, not keyword-primary** — but with an honesty mechanism most tools *don't* have. The five things that make a green result trustworthy:

1. **Judge-primary by default + Composite Judges.** When `--judge` is set, an LLM judge *reads* each reply, and the verdict is decomposed into a positive-only ⊕ negative-only pair that's structurally clamped against fabricating in either direction (see the **Grading** section above). The keyword oracle remains only as the deterministic **no-judge** fallback — and a no-judge run is byte-identical to the prior release.
2. **A third answer.** Every probe is **Resisted**, **Succeeded**, or **Inconclusive**. When the grader can't be sure, AgentEval says **"Inconclusive — I can't tell"** *instead of guessing.* Most tools force a binary pass/fail and silently bury their uncertainty.
3. **Conclusive-only scoring.** The headline number is `Resisted / (Resisted + Succeeded)` — *inconclusive* results lower your **coverage**, not your pass-rate. You can see exactly how much the tool was sure about.
4. **Evidence fidelity on every verdict.** Each result is labeled **Verbal** (the model's words), **Intent-to-act** (it emitted a forbidden tool call), or **Behavioral** (it actually executed one). A green result is never a guess about what the model *would* do.
5. **Reality-check grounding** where we can get it (canary tools, exact-token canaries) — the AgentDojo idea, so some verdicts come off the verbal channel entirely.

So our honest position: **on raw probe breadth, garak leads; on attacker-LLM orchestration depth, PyRIT leads** — both are excellent for deep security research, and we close the breadth gap by *importing* their datasets rather than re-implementing them. **On trustworthiness of the verdict — judge-primary Composite Judges that provably don't fabricate, three-way honesty, and evidence fidelity — AgentEval is, to our knowledge, the most careful.** That combination is the differentiator.

### Practical guidance

- **Configure `--judge`.** This is now where the honesty lives: with a judge set, Composite Judges *lead* the semantic verdicts. Without one, AgentEval falls back to the deterministic keyword oracle — best-effort only — and the cases it can't be sure about honestly report as a coverage gap, not a fake pass. (Use `--judge-mode fallback` if you want the old judge-as-tiebreaker behavior.)
- **Trust the structural signals on their own** — exact markers, canary-tool execution, real payloads. Those don't depend on reading prose.
- **Read the verdict *and* its fidelity label.** "Succeeded (Behavioral)" is a proven compromise; "Succeeded (Verbal)" is a strong signal worth a human look.

### Bottom line

The AI judge is now the **primary** grader for the semantic attacks, with the keyword heuristic demoted to the no-judge fallback — the direction the strongest tools already took, and what turns a keyword-honest tool into a semantically-honest one. Composite Judges proved this isn't just "add a judge": they make the judge *structurally* unable to fabricate, with the K=10 stochastic stability to back it up.

**Scope of the proof.** These honesty numbers are, deliberately, a *single-judge / single-model* result (`gpt-4o-mini`) on held-out adversarial cases — strong evidence of *stability*, not a claim of cross-model generality.

---

*See the [full red-team reference](redteam.md) for the API, CLI flags, report formats, and the compliance crosswalks.*
