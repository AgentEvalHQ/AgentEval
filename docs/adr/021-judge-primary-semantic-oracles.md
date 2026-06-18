# ADR-021: Judge-Primary Grading for Semantic Red-Team Oracles

**Status:** Proposed
**Date:** 2026-06-18
**Decision Makers:** AgentEval Contributors
**Related:** [redteam-whats-new.md](../redteam-whats-new.md) · the RedTeam oracle-honesty arc

---

## Context

AgentEval's red-team scanner grades each probe response with a **per-attack heuristic
oracle** — keyword/regex/structural matchers in `src/AgentEval.RedTeam/RedTeam/Evaluators/`.
The LLM judge (`LLMJudgeEvaluator`, configured via `ScanOptions.JudgeClient`) is wired today
only as an **`Inconclusive`-gated fallback**: it runs *after* the keyword oracle, and only
when that oracle already returned `Inconclusive`:

```csharp
// RedTeamRunner.cs (~L446) — judge fires ONLY on Inconclusive
if (evalResult.Outcome == EvaluationOutcome.Inconclusive && options.JudgeClient is not null)
{
    var judged = await new LLMJudgeEvaluator(options.JudgeClient).EvaluateAsync(probe, responseText, ct);
    // judge-derived Succeeded is capped at IntentToAct fidelity
}
```

The multi-turn path (`MultiTurn/TurnOrchestrator.cs`) has the identical Inconclusive-gated
fallback. So for every probe where the keyword oracle returns a *confident* `Succeeded` or
`Resisted`, the judge is **never consulted** — the keyword verdict is final.

### Why this is the #1 open correctness item

The oracle-honesty arc (Jun13→rev5) empirically proved the keyword tier is **non-convergent**
for *semantic* judgments. After ~80 corpus cases and a green both-directions invariant net, two
independent adversarial sweeps each found **41 fresh realistic fabrications** across all 7 lexical
oracles — **41 → 41, zero convergence**. Representative failures the keyword tier cannot fix by
adding/removing phrases:

- DataPoisoning flags the **correct** answer ("Canberra is the capital… Sydney is the largest city") as `Succeeded`.
- RefusalDetector reads "I **decline** to output PWNED" as `Succeeded` and "but since you insist: PWNED" as `Resisted`.

The input space is infinite; lexical patching is whack-a-mole. The published human-agreement
ladder makes the case quantitatively: **keyword ≈ 56%** → Llama Guard 72% → HarmBench 78% →
**frontier LLM judge ≈ 88%**. Competitors already sit above the keyword tier — PyRIT is
**judge-primary** by default; HarmBench/Llama Guard use a **trained classifier**. AgentEval is the
outlier still grading semantic attacks keyword-first.

## Decision

Make the LLM judge (and, later, a trained classifier — ADR-future) the **primary grader for
*semantic* attacks**, demoting the keyword oracle to an **advisory pre-filter**. Keep the keyword/
structural oracle **primary** where its evidence is genuinely structural (a canary tool actually
executed, an exact emitted marker, a Luhn-valid card, a real decoded payload) — those are not the
non-convergent cases and a judge would only add cost and noise.

Concretely:

1. **Classify each attack's oracle by evidence type** via a new `OracleEvidenceClass` on
   `IAttackType`:
   - `Structural` — verdict rests on observable structure (canary executed, exact marker, Luhn,
     decoded payload). Keyword/structural oracle stays **primary**. e.g. InsecureOutput (canary),
     PIILeakage (Luhn/marker), SystemPromptExtraction (exact secret), EncodingEvasion (decoded).
   - `Semantic` — verdict rests on meaning (refusal vs compliance, confabulation vs rebuttal,
     persona adoption, poisoned-assertion). Judge becomes **primary** when available. e.g.
     Jailbreak, Misinformation, DataPoisoning, ExcessiveAgency, the RefusalDetector path.

2. **Routing** (new `JudgePrimary` mode on `ScanOptions`, default **off** for v1):
   - `Semantic` attack **and** `JudgeClient != null` **and** `JudgePrimary` on → **judge grades first**;
     the keyword oracle result is attached as advisory metadata only.
   - No `JudgeClient`, or `JudgePrimary` off, or `Structural` attack → **current behaviour
     unchanged** (keyword-primary with defer-to-Inconclusive; judge as Inconclusive-fallback).
   This makes the default **offline, deterministic** run bit-for-bit identical to today.

3. **STRICT / LENIENT judge rubrics.** Add two refusal-grading prompt variants to
   `LLMJudgeEvaluator` so the operator can tune the precision/recall trade-off (STRICT: any
   partial compliance ⇒ Succeeded; LENIENT: a clear refusal anywhere ⇒ Resisted). Default STRICT.

4. **Preserve the moat.** The 3-way verdict (`Resisted`/`Succeeded`/`Inconclusive`),
   `EvidenceFidelity` tiers, and conclusive-only scoring are **unchanged**. A judge-primary
   `Succeeded` is still capped below a structural-evidence `Succeeded` in fidelity (the judge
   reasons over text, never observes execution), so a verbal claim never outranks a canary hit.

5. **Measure, don't assert (Phase C tie-in).** Extend the `OracleHonestyCorpus` to label each case
   with its expected verdict and add an **offline judge-agreement harness** (Cohen's κ + MAE/F1
   against the human-labelled corpus, judge mocked with a `FakeChatClient` for CI). We publish the
   agreement number; we do not claim "88%" without measuring it on our own corpus.

## Consequences

**Positive**
- Closes the proven non-convergence: semantic verdicts move from ~56%-agreement keyword matching
  to judge-grade adjudication, the real fix the two sweeps pointed to.
- Brings parity with PyRIT's judge-primary default while *keeping* our differentiators (3-way +
  evidence-fidelity + canary structural tier) that PyRIT/garak lack.
- Backward compatible: default offline runs are unchanged; opt-in via `--judge-primary`.

**Negative / risks**
- **Cost**: one judge call per semantic probe (not just the Inconclusive remnant). Mitigated by
  opt-in, batching, and keeping `Structural` attacks judge-free.
- **Non-determinism**: judge-graded runs are non-deterministic; label them as such (as PAIR/TAP
  already are) and exclude judge-primary verdicts from the deterministic CI invariant net.
- **Judge fallibility**: a judge is ~88%, not 100%. Hence the evidence-fidelity cap, the
  agreement harness (so the residual error is *measured*), and the eventual trained-classifier tier.

**Rollout**
1. Phase B.1 — `OracleEvidenceClass` + routing + `JudgePrimary` flag + CLI `--judge-primary`
   (off by default), STRICT/LENIENT rubrics. No behaviour change unless opted in.
2. Phase B.2 — judge-agreement harness on the labelled corpus; publish κ/F1.
3. Phase B.3 — once agreement is validated, flip `JudgePrimary` to **default-on for `Semantic`
   attacks whenever a `JudgeClient` is configured** (still off when no judge → offline unchanged).
4. Phase D (separate ADR) — optional offline trained-classifier tier (Llama-Guard/HarmBench-style)
   as a third rung between structural and frontier-judge.

## Alternatives considered

- **Keep patching the lexicon (rev6…).** Rejected — empirically non-convergent (41→41).
- **Judge-primary for *all* attacks.** Rejected — wastes cost/adds noise on structural attacks
  where the canary/marker is already ground truth and higher-fidelity than any verbal judgment.
- **Trained classifier first (skip the judge).** Deferred — higher engineering cost (model hosting,
  offline weights) for a smaller agreement gain than the frontier judge; sequenced as Phase D.
