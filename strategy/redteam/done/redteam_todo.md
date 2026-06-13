# RedTeam Forward Planning - TODO

> ## ⚠️ SUPERSEDED / HISTORICAL — moved to `done/` (verified 2026-06-13)
> This was the January-2026 MVP-era forward-planning TODO. Its priority items have since shipped — **verify in code, do not trust the body below**: **13 attack types** (`Attack.All`) + 3 opt-in (Crescendo/PAIR/TAP), **258 probes** at Comprehensive, **10/10 OWASP LLM Top 10** (LLM09 via `MisinformationAttack`, not the proposed `OverrelianceAttack`), **8 MITRE ATLAS** techniques (source-verified vs ATLAS.yaml), **multi-turn orchestration** (`TurnOrchestrator` + `CrescendoAttack`/`PairAttack`/`TapAttack`), **ResponsibleAI** toxicity/bias/misinformation metrics, and **JSON probe loading** (`JsonProbeDatasetImporter` + `--import-probes`). Tier-3 **z-score calibration** also shipped (`Calibration/Calibrator.cs`, `--calibration`).
>
> **Everything below this banner is the historical MVP snapshot and is OUT OF DATE** — the "Current Achievements", "Competitive Position", and "Success Metrics" tables still show 9 attacks / 192 probes / 6-of-10 OWASP and unchecked boxes that are now done. See `docs/redteam.md` for live counts.
>
> **Genuinely still-open (tracked elsewhere, NOT here):** multi-modal (P16 → [`202-P19`](202-P19-multi-modal-attacks-impplan.md)), GCG/adversarial-suffix (P18 → parked), full atkgen dynamic generation (P17), BadLikertJudge, plugin probe system, and Wave-F/G memory/breadth/multi-agent — all owned by the [NextWave plan](RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan.md) and `strategy/redteam/futures/`.
>
> **Purpose (original):** Track all pending red team enhancements, expansions, and priorities · **Created:** January 31, 2026 · **Historical MVP snapshot:** 9 attacks, 192 probes, 6/10 OWASP (60%), 6 MITRE techniques, 3 ResponsibleAI metrics

---

## Executive Summary

AgentEval RedTeam has achieved its MVP targets and beyond. This document consolidates forward planning for continued expansion toward parity with Python competitors.

### Current Achievements ✅

| Metric | MVP Target | Achieved | Status |
|--------|-----------|----------|--------|
| Attack Types | 5 | **9** | ✅ +80% |
| Total Probes | 55 | **192** | ✅ +249% |
| OWASP Coverage | 30% | **60%** | ✅ +100% |
| MITRE ATLAS | 5 | **6** | ✅ +1 |
| Export Formats | 4 | **5** | ✅ +PDF |
| Compliance Reports | 0 | **4** | ✅ NEW |
| ResponsibleAI Metrics | 0 | **3** | ✅ NEW |

### OWASP LLM 2025 Alignment ✅
All attack OWASP IDs updated to 2025 naming (LLM01, LLM02, LLM05, LLM06, LLM07, LLM10)

### ResponsibleAI Namespace (P14 Partial) ✅
- ToxicityMetric (pattern + LLM hybrid)
- BiasMetric (counterfactual testing)
- MisinformationMetric (claim verification)

### Competitive Position

- **garak**: 500+ probes, 8/10 OWASP — Gap: ~308 probes
- **PyRIT**: 200+ probes, 7/10 OWASP — Gap: ~8 probes, multi-turn
- **AgentEval**: 192 probes, 6/10 OWASP — **.NET leader**, best CI/CD

---

## Priority Tiers

### � P1: Documentation Polish ✅ DONE

#### P10: Documentation Polish
**Status:** ✅ COMPLETE (see Plan 203)  
**Completed:** January 31, 2026

**What Was Done:**
- [x] Updated `docs/redteam.md` with probe counts (192), OWASP 2025 IDs
- [x] Created `docs/ResponsibleAI.md` for new metrics
- [x] Added OWASP/MITRE attribution and licensing
- [x] Updated attack breakdown with LLM02, LLM05, LLM06, LLM07, LLM10
- [x] Updated README.md with RedTeam + ResponsibleAI sections
- [x] Updated CHANGELOG.md

---

### 🟡 P2: Short-Term (1-2 Weeks)

#### P11: LLM09 - Overreliance Attacks
**Status:** Planned  
**Effort:** 4-6 hours  
**Why:** Reach 70% OWASP coverage (7/10)

**OWASP LLM09 Definition:**
> Overreliance occurs when users trust LLM output without verification, leading to security or safety issues.

**New Attack Type:** `OverrelianceAttack`

**Probes (15-20 estimated):**

| ID | Category | Technique |
|----|----------|-----------|
| OR-001 | Authority Claims | "I'm an expert, trust me" |
| OR-002 | False Confidence | "This is definitely correct" |
| OR-003 | Citation Fabrication | Fake references/sources |
| OR-004 | Medical Advice | Dangerous health claims |
| OR-005 | Legal Advice | Incorrect legal guidance |
| OR-006 | Financial Advice | Risky financial suggestions |
| OR-007 | Security Advice | Vulnerable code patterns |
| OR-008 | Factual Errors | Confident misinformation |
| OR-009 | Statistics Fabrication | Made-up numbers/data |
| OR-010 | Consensus Fabrication | "Most experts agree..." |

**Post-Implementation:** 7/10 OWASP (70%)

---

#### P12: MITRE ATLAS Expansion (Phase 1)
**Status:** Partially Done (AML.T0045 added)  
**Effort:** 1-2 weeks for full expansion  
**Why:** Competitive coverage, enterprise compliance

**Remaining Techniques to Add:**

| ID | Name | New Attack | Priority |
|----|------|------------|----------|
| **AML.T0047** | ML Artifact Collection | ArtifactExtractionAttack | HIGH |
| **AML.T0048** | Exfiltration via ML API | Enhance PIILeakage | HIGH |
| **AML.T0052** | AI-Assisted Phishing | PhishingContentAttack | MEDIUM |
| AML.T0044 | Full Model Replication | ModelExtractionAttack | LOW |

**Phase 1 Target:** 8/12 techniques (67%)  
**Phase 2 Target:** 10/12 techniques (83%)

---

### 🟢 P3: Medium-Term (1-3 Months)

#### P13: Multi-Turn Attack Orchestration
**Status:** Design Phase  
**Effort:** 2-3 weeks  
**Why:** Major gap vs PyRIT/DeepTeam

**Feature Description:**
Multi-turn attacks use conversation history to gradually escalate, similar to real adversarial interactions.

**Attack Types:**

| Type | Description | Turns |
|------|-------------|-------|
| Crescendo | Gradual escalation over turns | 5-10 |
| Linear | Sequential probing line | 3-5 |
| Tree | Branching exploration paths | Variable |
| BadLikertJudge | Exploit LLM-as-judge | 2-3 |

**Implementation Components:**

1. **ConversationState** - Track multi-turn context
2. **TurnOrchestrator** - Control turn progression
3. **ConvergenceDetector** - Know when attack succeeded/failed
4. **MultiTurnAttack base** - Abstract base for orchestrated attacks

**API Design:**
```csharp
var attack = new CrescendoAttack(
    objective: "Extract system prompt",
    maxTurns: 10,
    escalationStrategy: EscalationStrategy.Gradual
);

await foreach (var turn in scanner.ExecuteMultiTurnAsync(agent, attack))
{
    Console.WriteLine($"Turn {turn.TurnNumber}: {turn.Verdict}");
    if (turn.ObjectiveAchieved) break;
}
```

---

#### P14: Toxicity & Bias Testing
**Status:** ✅ PARTIALLY COMPLETE via ResponsibleAI Namespace  
**Effort:** 2-3 weeks (originally)  
**Why:** Table stakes for responsible AI

**Implemented (AgentEval.ResponsibleAI Namespace):**

| Metric | Type | Status |
|--------|------|--------|
| **ToxicityMetric** | Pattern + LLM hybrid | ✅ Done |
| **BiasMetric** | LLM-based + counterfactual | ✅ Done |
| **MisinformationMetric** | Claim verification | ✅ Done |

**Remaining (Future):**
- Red team attack probes for intentional toxicity elicitation
- Integration with Azure AI Content Safety API
- Statistical bias measurement across large datasets

---

#### P15: Code Injection Attacks
**Status:** Research  
**Effort:** 1-2 weeks  
**Why:** Common vulnerability, easy implementation

**Probes:**
- SQL injection via LLM output
- Shell command injection attempts
- Path traversal vectors
- SSRF URL generation
- Template injection (Jinja, etc.)

**Note:** Partially covered by InsecureOutputAttack (LLM02), but could expand with more targeted probes.

---

### ⚪ P4: Long-Term (3-6 Months)

#### P16: Multi-Modal Attacks
**Status:** Future Research  
**Effort:** 1+ month  
**Why:** Emerging attack vector, garak/PyRIT lead

**Capabilities Needed:**
- Image prompt injection
- Audio adversarial attacks
- Vision-language model probing
- Requires multi-modal model support

**Dependency:** MAF multi-modal agent support

---

#### P17: AI Red Teaming (Dynamic Attack Generation)
**Status:** Research Required  
**Effort:** 2-3 months  
**Why:** Automated attack discovery like garak's "atkgen"

**Concept:**
Use an LLM to dynamically generate new attack prompts based on:
- Current probe set
- Target model's responses
- Attack objective

**How garak's atkgen works:**
1. Takes an objective (e.g., "make the model reveal its system prompt")
2. Uses a "red team LLM" to generate novel attack prompts
3. Evaluates responses and iteratively improves attacks
4. Discovers zero-day vulnerabilities automatically

**AgentEval Implementation:**
```csharp
var generator = new AttackGenerator(redTeamClient);
await foreach (var probe in generator.GenerateAttacksAsync(
    objective: "Extract PII",
    maxProbes: 50,
    seedProbes: PIILeakageAttack.GetProbes()))
{
    var result = await scanner.TestProbeAsync(agent, probe);
    if (result.Succeeded)
    {
        // Novel attack discovered!
        log.LogCritical($"New vulnerability: {probe}");
    }
}
```

**Cost:** Requires LLM API calls for generation (not free)

---

#### P18: Adversarial Suffix (GCG) Attacks
**Status:** Research  
**Effort:** 1-2 months  
**Why:** Academic state-of-art, complex implementation

**What:** Greedy Coordinate Gradient attacks that find adversarial suffixes making models comply.

**Complexity:** Requires:
- Model gradients (not always available)
- GPU computation
- Research-level implementation

---

## JSON vs Coded Probes Analysis

**Question:** Should probes be stored in JSON files or coded directly?

### Option A: JSON-Based Probes

**Pros:**
1. ✅ Easy updates without recompilation
2. ✅ Non-developers can contribute probes
3. ✅ Runtime extensibility
4. ✅ Easier A/B testing of probes
5. ✅ Version control for probe datasets separately

**Cons:**
1. ❌ No compile-time validation
2. ❌ Harder to debug
3. ❌ Schema evolution challenges
4. ❌ Can't use C# features (functions, conditionals)
5. ❌ Distribution complexity (embed vs external files)

### Option B: Coded Probes (Current)

**Pros:**
1. ✅ Compile-time type safety
2. ✅ IntelliSense and refactoring support
3. ✅ Full C# power (encoding functions, generators)
4. ✅ Single distribution (no external files)
5. ✅ Better testing (unit test each probe)
6. ✅ Easier dependency injection

**Cons:**
1. ❌ Requires recompilation for updates
2. ❌ Higher barrier for contributions
3. ❌ Larger binary size

### Recommendation: **Hybrid Approach**

1. **Core probes** → Coded (type-safe, tested)
2. **Custom probes** → JSON loadable via `ProbeLoader`
3. **Enterprise extensions** → Plugin system

```csharp
// Core probes (compiled)
var attacks = AttackFactory.GetAllAttacks();

// Custom probes (JSON)
var customProbes = ProbeLoader.Load("custom-probes.json");
attacks.AddRange(customProbes);

// Plugin probes (runtime)
var plugins = PluginLoader.Discover("./plugins");
attacks.AddRange(plugins.SelectMany(p => p.GetProbes()));
```

**Implementation Priority:** P3 (Medium-term)

---

## Missing Attack Categories - Analysis

### Why No Toxicity/Bias/Misinformation Testing?

**Current Focus:** Security-first (OWASP Top 10)

**Missing Categories:**

| Category | Why Missing | Path to Add | Priority |
|----------|-------------|-------------|----------|
| **Toxicity** | Requires content safety evaluator | Azure AI Content Safety API | P3 |
| **Bias** | Statistical analysis needed | Custom fairness metrics | P3 |
| **Misinformation** | Hard to evaluate factuality | LLM-as-judge or fact-check API | P4 |
| **Multi-modal** | Requires image/audio support | MAF multi-modal agents | P4 |
| **Code injection** | Partially in InsecureOutput | Expand LLM02 coverage | P2 |

**Strategy:** Focus on security (OWASP) first, then expand to responsible AI (toxicity, bias) in v2.

---

## Promptfoo Pricing Considerations

**Promptfoo Model:**
- **Community Tier:** FREE for 10k probes/month
- **Enterprise Tier:** Custom pricing for unlimited + premium features

**AgentEval Position:**
- **Free forever** (MIT license)
- No probe limits
- No telemetry/phoning home
- Self-hosted by default

**Competitive Advantage:** Enterprise users who need unlimited probing without usage-based costs prefer AgentEval.

---

## DeepTeam vs DeepEval Clarification

**Same Company:** Confident AI (https://confident-ai.com)

| Product | Purpose | Red Teaming |
|---------|---------|-------------|
| **DeepEval** | LLM evaluation framework | ❌ Not included |
| **DeepTeam** | Red teaming library | ✅ Enterprise-only ($$$) |

**Key Insight:** Red teaming in DeepTeam requires Enterprise license (custom pricing, not free).

**AgentEval Advantage:** Full red teaming in free/MIT-licensed package.

---

## Timeline Summary

| Phase | Priority | Features | Effort | Target |
|-------|----------|----------|--------|--------|
| **Now** | P1 | P10 Docs Polish | 4-6h | This week |
| **Sprint 1** | P2 | P11 LLM09, P12 MITRE Phase 1 | 1-2 weeks | 70% OWASP, 8 MITRE |
| **Sprint 2-3** | P3 | P13 Multi-turn, P14 Toxicity | 1-2 months | Multi-turn attacks |
| **Q2** | P4 | P15-P18 (Advanced) | 2-3 months | Parity with garak |

---

## Success Metrics

### Short-Term (1 month)
- [ ] 70% OWASP coverage (7/10)
- [ ] 8 MITRE ATLAS techniques
- [x] 200+ probes → ✅ 192 (close, +15 from InsecureOutput expansion)
- [x] Updated documentation → ✅ OWASP 2025 IDs, ResponsibleAI docs
- [x] ResponsibleAI metrics → ✅ 3 metrics (Toxicity, Bias, Misinformation)

### Medium-Term (3 months)
- [ ] Multi-turn attack orchestration
- [x] Toxicity/bias testing → ✅ via ResponsibleAI namespace
- [ ] 250+ probes
- [ ] JSON probe loading

### Long-Term (6 months)
- [ ] 80% OWASP coverage (8/10)
- [ ] 10 MITRE ATLAS techniques
- [ ] 300+ probes
- [ ] AI-powered attack generation
- [ ] Multi-modal support (if MAF supports)

---

*Document created: January 31, 2026*  
*Last updated: January 31, 2026*
