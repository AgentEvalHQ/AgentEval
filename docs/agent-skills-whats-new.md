# Agent Skills — What's New

A roundup of what's landed for MAF Agent Skills evaluation and governance, in the order you'd actually adopt
it: assert first, then scan, then red-team and lock down execution, then enforce at construction time, then
roll it all into one score. See the [full reference](agent-skills.md) for the how-to; this page is the
capability history, so a new addition doesn't sit undocumented the way two of these briefly did (see the
last entry).

> **TL;DR.** Fluent assertions + a disclosure-efficiency metric, a static compliance scanner with a
> multi-repo baseline ledger, a skill-injection red-team attack (shadow-only — the judge hasn't cleared
> calibration on this surface yet), deterministic `run_skill_script` governance gates, **SkillGate**
> (construction-time drift enforcement — refuse to even build an agent if a skill was tampered with), a
> composite Skill Health & Security Index, and detection for skills MAF silently excludes from discovery.

---

## New & upgraded capabilities

### Assertions + the disclosure-efficiency metric
`AgentEval.Assertions.SkillUsageAssertions` — fluent checks on the real tool-call trace
(`HaveLoadedSkill`/`HaveReadSkillResource`/`HaveRunSkillScript`, disclosure-order validation via
`HaveDisclosedProgressively`). The companion `SkillDisclosureEfficiencyMetric` scores the observed
`load_skill → read_skill_resource → run_skill_script` funnel — order validity, redundant loads — without
ever fabricating a selection score when there's no ground truth to compare against.

### Compliance scanner + baseline ledger
`SkillComplianceValidator`/`MafSkillScanner` check a skill's `SKILL.md` authoring against MAF's GA rules plus
governance flags (untrusted resource source, script requiring review). Grew a repo-wide discovery mode
(`--repo`), a multi-repo workspace scan (`scan-workspace`), cross-location content-drift detection, and
trust-on-first-use reputation matching against a persisted baseline ledger.

### Skill-injection red-team attack + `run_skill_script` governance
`SkillInjectionAttack` (OWASP LLM01) red-teams a poisoned skill description and `read_skill_resource` output.
**Honestly shadow-only**: the reused `IndirectInjectionRubric` judge did not clear live calibration on this
surface (4 missed attacks on a 52-case gold set) — the pass/fail is keyed on real tool-call evidence, never
the uncalibrated judge's opinion. The *other* half of this surface — actually executing `run_skill_script`
code — is fully deterministic: `SkillScriptExecutionGate` (an allowlist at the function-invocation seam) and
`SkillScriptApprovalGate` (a human-in-the-loop trust gate) have no calibration debt at all.

### SkillGate — construction-time drift enforcement *(the newest addition)*
Everything above is audit-time — you run a scan when you choose to. **SkillGate Tier 1** moves the same drift
check to *enforcement* time: `UseGatekeeper(...).WithSkillGate(...)` refuses to let an agent construct at all
if a skill it will expose has drifted from its pinned baseline, or — under `SkillGateMode.Strict` — isn't
recognized at all. Two hash signals (structural fingerprint + full-content hash), fail-closed by default,
with a `Bootstrap` trust-on-first-sight mode and a CLI recovery path
(`agenteval skills baseline approve <skill-name>`). See [Explainability & Trust](gatekeeper/explainability-and-trust.md)
for the same "why enforcement, not just audit" pattern applied more broadly. Full detail:
[§3b of the reference](agent-skills.md#3b--skillgate-construction-time-drift-enforcement).

### Skill Health & Security Index + hash-pin drift
`SkillSecurityIndex` joins Compliance (the static scan), Efficiency (the disclosure metric), and Security
(red-team outcome + drift findings) into one composite 0-100 score — a missing axis is never faked as
perfect; the result reports exactly how many of the three axes actually contributed.
`SkillManifestPoisoningGate`'s hash-pin-and-diff catches a "rug pull" (a skill silently changing after it was
reviewed and trusted) — the same primitive SkillGate above builds on for enforcement.

### Detecting MAF's silent skill-discovery exclusions
MAF's own skill discovery silently **excludes** a folder entirely when its `SKILL.md` fails certain GA rules
— no error, no finding, the folder just vanishes as if it never existed, and the exclusion is *recursive*. The
scanner now runs an independent raw directory walk, reconciles it against what MAF actually returned, and
reports a `SkillExcludedFromDiscovery` finding for anything present-on-disk-but-silently-dropped — worded to
make clear the skill is non-functional as authored, not merely non-compliant.

---

## The doc-lag this page is meant to prevent

Two capabilities above — SkillGate and, in the neighboring Gatekeeper area, Explainability & Trust — shipped
as real, tested code with **zero** matching documentation, discovered only when someone went looking. That's
the actual failure mode a changelog entry buried in `CHANGELOG.md` doesn't catch: nobody reads the whole
changelog before writing docs for a *different* feature. This page exists so the next capability gets a line
here the same week it ships, not two sessions later.

---

*See the [full Agent Skills reference](agent-skills.md) for the API, CLI commands, and known limitations.*
