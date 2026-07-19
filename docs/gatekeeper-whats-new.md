# Gatekeeper — What's New

A roundup of what's landed for Gatekeeper's runtime enforcement layer recently — the fleet-wide correlation
signal, honest confidence propagation, construction-time drift enforcement, and the newest addition,
Explainability & Trust. See the [introduction](gatekeeper/introduction.md) and
[gate reference](gatekeeper/gate-reference.md) for the full guide; this page is the capability history.

> **TL;DR.** Judges now propagate a real confidence signal instead of discarding it, a Fleet Correlation
> Layer combines near-miss signals across a whole session, SkillGate extends Gatekeeper's enforcement model
> to Agent Skills, and — the newest addition — **Explainability & Trust**: reconstructable gate provenance,
> counterfactual gate-config replay, and a unified Trust Score.

---

## New & upgraded capabilities

### Explainability & Trust *(the newest addition)*
Three primitives that make a gate decision reconstructable instead of a bare Allow/Block. **Gate provenance
chains** (`GateProvenance`) attach a structured "why" — rule name, evidence, threshold vs. actual — to a
`GateVerdict`, wired into `CompositeJudgeGate<TRubric>`. **Counterfactual gate replay** (`GateReplayer`) runs
a baseline and a candidate `IToolGate` configuration against the same captured tool calls — the real gate
objects, not a simulation — and reports which calls would diverge. **Unified Trust Score**
(`TrustScoreCalculator`) combines gate verdicts and eval scores into one honest 0-100 composite, excluding
skipped/errored signals from the math entirely rather than scoring them 0. Full detail + a runnable sample:
[Explainability & Trust](gatekeeper/explainability-and-trust.md).

### Judge confidence propagation
`CompositeJudgeGate` used to discard a judge's confidence once a verdict was decided. It now carries the real
confidence through — including the near-miss case (a sub-threshold Block that gets downgraded to Allow) —
so a fleet-wide correlator can see a genuine uncertain signal instead of a laundered "clean allow." This is
the same signal Explainability & Trust's gate provenance now also reconstructs a full "why" around.

### Fleet Correlation Layer
Session-scoped correlation across multiple gates and turns — several individually-below-threshold near-miss
signals, combined, can indicate a real problem no single gate would have blocked on its own. Wired into
`EvalGatingChatClient` via an optional constructor parameter and a `UseEvalGate` builder extension.

### SkillGate — Gatekeeper's enforcement model applied to Agent Skills
`UseGatekeeper(...).WithSkillGate(...)` refuses to let an agent construct at all if a skill it will expose
has drifted from a pinned baseline. The third application of the same audit-time-vs-enforcement-time pattern
already used for skill manifests and prompt templates. Full detail (this one lives primarily in the
[Agent Skills reference](agent-skills.md#3b--skillgate-construction-time-drift-enforcement), since that's
where the governance question originates) —
[Agent Skills — What's New](agent-skills-whats-new.md) covers it from that side.

---

## The doc-lag this page is meant to prevent

SkillGate and, above, Explainability & Trust both shipped as real, tested code with **zero** matching
documentation at first — discovered only when someone went looking, not because anyone flagged the gap at
ship time. That's the actual failure mode: nothing surfaces "a capability shipped with no matching doc
change" automatically, and a changelog entry buried in `CHANGELOG.md` doesn't get read by whoever's about to
write docs for a *different* feature next session. This page exists so the next Gatekeeper capability gets a
line here the same week it ships.

---

*See the [Gatekeeper introduction](gatekeeper/introduction.md) for concepts and layers, the
[gate reference](gatekeeper/gate-reference.md) for every built-in gate ranked by honest usefulness, and
[examples](gatekeeper/examples.md) for runnable code.*
