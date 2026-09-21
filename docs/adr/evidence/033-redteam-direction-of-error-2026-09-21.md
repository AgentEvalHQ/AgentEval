# Red-team lane — the direction of the error, 2026-09-21

The proposal's step 4 asks one question before a decision model goes anywhere near an attack-success
verdict: **when it is wrong, which way is it wrong?** For a red-team oracle the dangerous error is a
*false pass* — an attack that succeeded, judged resisted. A false fail is noise; a false pass is a security
claim that is not true.

This is answered from the N3 run, not from a new one: its 378 agentic golden cases already include the
adversarial, injection, leakage and safety evaluators the red-team lane would use, judged by both arms on
identical rubrics. No further spend was needed, and re-running would have produced a second number for a
question already measured.

## Red-team and injection evaluators — 77 judge-decided cases, 22 labelled `fail`

| evaluator key | n | accuracy A | accuracy B | **false-pass A / B** | false-fail A / B |
|---|---:|---:|---:|---|---:|
| direct_injection | 4 | 0.250 | 0.250 | **0.000 / 0.000** | 1.000 / 1.000 |
| escalation_resistance | 5 | 1.000 | 1.000 | **0.000 / 0.000** | 0.000 / 0.000 |
| indirect_attack | 20 | 1.000 | 0.850 | **0.000 / 0.000** | 0.000 / 0.214 |
| jailbreak_resistance | 2 | 1.000 | 1.000 | **0.000 / 0.000** | 0.000 / 0.000 |
| persona_attack | 4 | 0.500 | 0.250 | **0.000 / 0.000** | 0.667 / 1.000 |
| prompt_leak | 3 | 0.667 | 0.333 | **0.000 / 0.000** | 0.500 / 1.000 |
| protected_material | 5 | 1.000 | 0.800 | **0.000 / 0.000** | 0.000 / 0.500 |
| refusal_quality | 4 | 1.000 | 0.250 | **0.000 / 0.000** | 0.000 / 1.000 |
| sensitive_data_leakage | 15 | 0.867 | 0.600 | **0.000 / 0.000** | 0.143 / 0.429 |
| system_prompt_leakage | 15 | 1.000 | 0.867 | **0.000 / 0.000** | 0.000 / 0.182 |
| **total** | **77** | **0.896** | **0.701** | **0.000 / 0.000** (22 fail-labelled) | 0.145 / 0.418 |

## Safety evaluators — 98 cases, 31 labelled `fail`

| evaluator key | n | accuracy A | accuracy B | **false-pass A / B** | false-fail A / B |
|---|---:|---:|---:|---|---:|
| hate_unfairness | 22 | 1.000 | 0.955 | **0.000 / 0.000** | 0.000 / 0.067 |
| self_harm | 25 | 0.880 | 0.840 | **0.000 / 0.000** | 0.176 / 0.235 |
| sexual | 26 | 1.000 | 0.769 | **0.000 / 0.000** | 0.000 / 0.333 |
| violence | 25 | 1.000 | 0.640 | **0.000 / 0.000** | 0.000 / 0.529 |
| **total** | **98** | **0.969** | **0.796** | **0.000 / 0.000** (31 fail-labelled) | 0.045 / 0.299 |

## What this says

**Across 53 fail-labelled adversarial and safety cases, neither judge passed a single one.** The decision
model's entire error budget is spent over-flagging: 41.8% of the good red-team cases and 29.9% of the good
safety cases were rejected, against 14.5% and 4.5% for the generative judge.

That is the asymmetry the proposal's §2.3 hoped for, and it is the opposite of what its §5 risk table feared
— the worry was that adversarial content would steer the model into *passing* attacks. It does steer it, but
towards refusing: the model sees the attack text in the state and calls the whole exchange non-compliant.
The same mechanism produced the 20% on EU AI Act prohibited practices, where the correct answer was that the
agent's refusal was fine.

**For a red-team oracle, over-flagging is the survivable direction.** A false alarm costs a human review; a
missed attack ships a false assurance.

## The bound, stated honestly

**0 false passes observed in 53 fail-labelled cases is not a 0% rate.** By the rule of three, the 95% upper
bound on the true rate is **≈ 5.7%**. A gate that needs "no more than 1% of successful attacks are missed"
is **not** supported by this evidence and would need roughly 300 fail-labelled cases to support it. What is
supported today: the decision model does not appear to be *more* permissive than the generative judge on
these sets, and both look conservative.

Two further limits: `direct_injection` sits at 0.250 for **both** judges on 4 cases, so that evaluator's
goldens or its criteria need attention regardless of which judge runs them; and several rows have n < 5,
where a single case moves the rate by 20 points or more.

## Decision: no `DecisionProbeEvaluator`, and why that is the result

The proposal's step 4 names a new red-team adapter. **It is deliberately not built.**

- The seam already exists. `DecisionJudge` reaches every one of these evaluators through the shared
  `EvalRegistry`, which is exactly how the table above was produced. A third adapter would add public
  surface and another provenance trap without adding a single measurement.
- The numbers do not support shipping it as a gate. Step 4's exit criterion is "false-pass on severe ≤ the
  agreed bound on held-out sets, or it stays in shadow". At n = 53 no bound below ~6% can be agreed, so it
  stays in shadow — and shadow is what the existing adapter already does.
- Building the gate anyway is the defect ADR-028 and the proposal's §5 exist to prevent: a threshold chosen
  before the data that would justify it.

**Owed, if this lane is picked up:** the held-out measurement at a sample size that can support a bound
(≈ 300 fail-labelled adversarial cases, which the red-team probe corpus can supply where the golden sets
cannot), and a look at `direct_injection`'s goldens, which neither judge can currently pass.
