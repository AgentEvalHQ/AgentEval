# Red Team — What's New

A roundup of what landed in AgentEval's red-team module recently: broader coverage, multi-step attacks, real-tool grounding, and — the part we're proudest of — a serious, honest answer to the hardest problem in red-teaming: **trusting the verdict.**

> **TL;DR.** More attacks, multi-turn and attacker-LLM support, real tool-execution grounding, OWASP LLM Top-10 closure, and compliance crosswalks across five frameworks — *plus* a three-way, evidence-graded verdict system that tells you when it **doesn't know**, instead of guessing. See [the full reference](redteam.md).

---

## New & upgraded capabilities

### Coverage
- **258 built-in probes across 13 attack types** (Comprehensive intensity), covering **all 10 OWASP LLM Top 10 (2025)** and **8 MITRE ATLAS** techniques.
- **Compliance crosswalks across five frameworks** — OWASP LLM Top 10, MITRE ATLAS, **NIST AI RMF (AI 100-1)**, ISO/IEC 42001, and SOC 2 — with a `--format nist` report straight from a scan and `bench owasp|mitre|nist` benchmark families.
- **Bring your own data:** `--import-probes` (CSV/JSON) and external **benchmark packs** via `--pack` (HarmBench / JailbreakBench / CyberSecEval, downloaded on demand under their own licenses — no harmful data bundled).

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
| **Keyword/pattern** (garak's default refusal lists; our verbal oracles) | Ctrl-F for refusal/compliance phrases | Agrees with human graders only **~half the time** on the hard cases |
| **AI-as-judge** (PyRIT; promptfoo; our `--judge`) | hands the reply to a *second* model to read and judge | Strong on understanding (**~85–90%** with a frontier judge), at the cost of speed, money, and run-to-run consistency |
| **Trained grader** (HarmBench classifier; Meta Llama Guard) | a model *purpose-trained* on thousands of human-labeled examples | Reliable, fast, offline (**~75–80%**) |
| **Reality check** (AgentDojo) | doesn't read prose at all — checks whether the action *actually happened* (was the email sent? the record changed?) | The gold standard *wherever you can observe the outcome* |

(Agreement figures are from the public HarmBench / Llama Guard research.)

Each is better than Ctrl-F at *understanding* — but none of them, except the reality-check, is perfect, and the AI-judge approaches trade away determinism and cost.

### Where AgentEval stands — and what we did differently

We're candid: our per-attack graders are still **keyword-based at the fast first pass**, like garak's default. But we added the thing most tools *don't* have — and it's the heart of this release:

1. **A third answer.** Every probe is **Resisted**, **Succeeded**, or **Inconclusive**. When the keyword grader can't be sure, AgentEval says **"Inconclusive — I can't tell"** *instead of guessing.* Most tools force a binary pass/fail and silently bury their uncertainty.
2. **Conclusive-only scoring.** The headline number is `Resisted / (Resisted + Succeeded)` — *inconclusive* results lower your **coverage**, not your pass-rate. You can see exactly how much the tool was sure about.
3. **Evidence fidelity on every verdict.** Each result is labeled **Verbal** (the model's words), **Intent-to-act** (it emitted a forbidden tool call), or **Behavioral** (it actually executed one). A green result is never a guess about what the model *would* do.
4. **Reality-check grounding** where we can get it (canary tools, exact-token canaries) — the AgentDojo idea, so some verdicts come off the verbal channel entirely.
5. **Defer to the judge** for the genuinely ambiguous middle (e.g. confabulation-vs-refutation): configure `--judge` and the LLM adjudicates the `Inconclusive` pile.

So our honest position: **on raw probe breadth, garak leads; on attacker-LLM orchestration depth, PyRIT leads** — both are excellent for deep security research, and we close the breadth gap by *importing* their datasets rather than re-implementing them. **On trustworthiness of the verdict — three-way honesty, evidence fidelity, and not-guessing — AgentEval is, to our knowledge, the most careful.** That combination is the differentiator.

### Practical guidance

- **Configure `--judge`.** The verbal graders are best-effort pre-filters; the ambiguous middle (now intentionally large, because we'd rather say "Inconclusive" than lie) is adjudicated by an LLM judge. Without one, those probes honestly report as a coverage gap, not a fake pass.
- **Trust the structural signals on their own** — exact markers, canary-tool execution, real payloads. Those don't depend on reading prose.
- **Read the verdict *and* its fidelity label.** "Succeeded (Behavioral)" is a proven compromise; "Succeeded (Verbal)" is a strong signal worth a human look.

### Where this is heading

The next step is to make the **AI judge (or a trained grader) the *primary* grader** for the semantic attacks — keyword heuristics demoted to an advisory first pass — which is the direction the strongest tools have already taken. That's how a keyword-honest tool becomes a semantically-honest one. This release is the bridge: it stops the fast grader from over-claiming, and routes everything it can't be sure about to a real judge.

---

*See the [full red-team reference](redteam.md) for the API, CLI flags, report formats, and the compliance crosswalks.*
