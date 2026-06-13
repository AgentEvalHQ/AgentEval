# Implementation Plan: MITRE ATLAS Expansion

> **Plan ID:** 181-P12-MITRE-ATLAS-Expansion
> **Priority:** P4 (Medium)
> **Created:** January 30, 2026 · **Last audited:** 2026-06-13 (`feature/redteam-newwave-fixes`)
> **Status:** ⚠️ PARTIALLY OVERTAKEN — premise obsolete; Phase 1 shipped (redesigned); Phases 2–4 declined as NotApplicable
>
> ### Reality vs this plan (verified against code + git)
> - **Phase 0 (fix docs to "5 techniques") — OBSOLETE / DO NOT DO.** The MITRE ATLAS catalog was source-verified and rewritten vs ATLAS.yaml on 2026-06-13 (commit `fa26c46`, H13/H14). `docs/redteam.md` now honestly maps **8 techniques** (AML.T0010, T0020, **T0034**, T0037, T0051, T0054, T0056, T0057). The "code has only 5" discovery no longer holds. *(Note: a stale `AML.T0045` in the docs coverage line — the retired ID this plan's Phase 1 referenced — was corrected to `AML.T0034` during this audit.)*
> - **Phase 1 (Inference API) — ✅ SHIPPED, but redesigned.** `InferenceAPIAbuseAttack` (15 IAA probes) + `InferenceAbuseEvaluator` exist under `src/AgentEval.RedTeam/RedTeam/...` and are registered in `Attack.cs` (All roster + `ByOwasp("LLM10")`). They map to **AML.T0034 (Cost Harvesting) / OWASP LLM10 (Unbounded Consumption)** — NOT the planned T0045/LLM04 (T0045 retired from ATLAS; LLM04 removed in OWASP 2.0). Probes a chat agent cannot physically exercise are flagged Inconclusive, not scored.
> - **Phase 2 (T0048 exfiltration enhancement) — NOT DONE.** T0048 is intentionally a `NotApplicable` skipped leaf, reserved for a future judge-graded category.
> - **Phase 3 (`ArtifactExtractionAttack`, T0047) — NOT DONE / DECLINED.** No such file; T0047 is `NotApplicable` at the agent-API layer.
> - **Phase 4 (`AIAssistedPhishingAttack`, T0052) — NOT DONE / DECLINED.** No such file; T0052 is `NotApplicable`.
> - All `src/AgentEval/RedTeam/...` paths below are stale; real layout is `src/AgentEval.RedTeam/RedTeam/...`. Repo is now **258 probes / 13 attack types / 10/10 OWASP / 8 MITRE techniques**, so the 146-probe / 9-technique targets are superseded.
>
> **Decision still open:** whether T0047/T0048/T0052 should ever become live attacks, or remain documented `NotApplicable` leaves. Everything below is the original (Jan 30, 2026) proposal, retained for historical context only.

---

## Executive Summary

### Current State (Discovery)
**Documentation Discrepancy Found:** 
- `docs/redteam.md` claims "**8 technique IDs** mapped"
- Actual code shows only **5 unique MITRE ATLAS techniques**

### Code Verification

```bash
# Actual MITRE IDs in source code:
IndirectInjectionAttack.cs:    AML.T0051
JailbreakAttack.cs:            AML.T0051, AML.T0054  
PIILeakageAttack.cs:           AML.T0024, AML.T0037
PromptInjectionAttack.cs:      AML.T0051
SystemPromptExtractionAttack.cs: AML.T0043

# Unique techniques: 5 (not 8)
```

---

## Problem Statement

1. **Documentation is inaccurate** - Claims 8 techniques, have 5
2. **Coverage is limited** - PyRIT has ~10, garak has ~9 implicit
3. **Competitive gap** - We're behind on taxonomy mapping

---

## MITRE ATLAS Reference

### Full ATLAS Technique List (LLM-Relevant)

| ID | Name | Tactic | Current Status | Priority |
|----|------|--------|----------------|----------|
| **AML.T0051** | LLM Prompt Injection | Initial Access | ✅ Implemented (3 attacks) | - |
| **AML.T0054** | LLM Jailbreak | Defense Evasion | ✅ Implemented | - |
| **AML.T0043** | Craft Adversarial Data | ML Attack Staging | ✅ Implemented | - |
| **AML.T0024** | Develop Capabilities | Resource Development | ✅ Implemented | - |
| **AML.T0037** | Data from Information Repositories | Collection | ✅ Implemented | - |
| **AML.T0045** | Inference API | Initial Access | ❌ Not Implemented | **P1** |
| **AML.T0048** | Exfiltration via ML Inference API | Exfiltration | ❌ Not Implemented | **P2** |
| **AML.T0044** | Full ML Model Replication | Exfiltration | ❌ Not Implemented | P4 |
| **AML.T0046** | Publish Poisoned Dataset | Persistence | ❌ Cannot test (infra) | - |
| **AML.T0047** | ML Artifact Collection | Collection | ❌ Not Implemented | P3 |
| **AML.T0052** | Phishing: Spearphishing via AI | Initial Access | ❌ Not Implemented | P3 |
| **AML.T0053** | Adversarial SEO | Defense Evasion | ❌ Cannot test (web) | - |

---

## Implementation Plan

### Phase 0: Fix Documentation (Immediate)

**Task:** Update `docs/redteam.md` to accurately reflect 5 techniques

```diff
- **Coverage**: **8 technique IDs** mapped to attack implementations
+ **Coverage**: **5 technique IDs** mapped to attack implementations
```

**Time:** 15 minutes  
**Priority:** Critical (accuracy)

---

### Phase 1: Add AML.T0045 - Inference API Attacks

**MITRE Description:** Attackers may use inference APIs to interact with machine learning models for various purposes.

**New Attack Type:** `InferenceAPIAbuseAttack`

**Probes (15 estimated):**

| ID | Probe | Technique | Difficulty |
|----|-------|-----------|------------|
| IAA-001 | Excessive token generation | resource_exhaustion | Easy |
| IAA-002 | Rate limit bypass attempts | rate_limit_bypass | Moderate |
| IAA-003 | Hidden parameter extraction | parameter_probing | Moderate |
| IAA-004 | Model behavior fingerprinting | model_fingerprinting | Hard |
| IAA-005 | Temperature/top_p manipulation | hyperparameter_abuse | Moderate |
| IAA-006 | Batch inference abuse | batch_exploitation | Moderate |
| IAA-007 | Streaming endpoint abuse | stream_manipulation | Hard |
| IAA-008 | Context window overflow | context_overflow | Easy |
| IAA-009 | Stop sequence manipulation | stop_sequence_bypass | Moderate |
| IAA-010 | Function calling abuse | function_abuse | Moderate |
| IAA-011 | Max tokens manipulation | token_limit_abuse | Easy |
| IAA-012 | Presence/frequency penalty bypass | penalty_evasion | Hard |
| IAA-013 | Logprobs extraction | logprob_extraction | Hard |
| IAA-014 | Seed manipulation for determinism | seed_exploitation | Moderate |
| IAA-015 | Response format manipulation | format_injection | Moderate |

**Evaluator:** Custom `InferenceAbuseEvaluator` checking for:
- Resource exhaustion indicators
- Rate limit error patterns
- Unexpected model behavior

**OWASP Mapping:** LLM04 (Model Denial of Service)

**Time:** 4-6 hours

---

### Phase 2: Add AML.T0048 - Data Exfiltration via Inference

**MITRE Description:** Attackers may exfiltrate data by querying the model's inference API repeatedly.

**Enhancement to existing attacks:**

1. **Enhance PIILeakageAttack** with exfiltration focus
2. **Enhance SystemPromptExtractionAttack** with exfiltration patterns

**New Probes (10 estimated):**

| ID | Probe | Technique | Current Attack |
|----|-------|-----------|----------------|
| EXF-001 | Incremental data extraction | incremental_extraction | PIILeakage |
| EXF-002 | Cross-session data leakage | session_leakage | PIILeakage |
| EXF-003 | Fine-tuning data extraction | training_data_extraction | PIILeakage |
| EXF-004 | Membership inference | membership_inference | PIILeakage |
| EXF-005 | Embedding extraction attempts | embedding_extraction | SystemPromptExtraction |

**OWASP Mapping:** LLM06 (Sensitive Information Disclosure)

**Time:** 3-4 hours

---

### Phase 3: Add AML.T0047 - ML Artifact Collection

**MITRE Description:** Attackers may collect ML artifacts such as models, training data, or configuration.

**New Attack Type:** `ArtifactExtractionAttack`

**Probes (10 estimated):**

| ID | Probe | Technique |
|----|-------|-----------|
| ART-001 | Model architecture probing | architecture_extraction |
| ART-002 | Training dataset probing | dataset_probing |
| ART-003 | Hyperparameter extraction | config_extraction |
| ART-004 | Version information disclosure | version_extraction |
| ART-005 | Fine-tuning details extraction | finetuning_extraction |
| ART-006 | Prompt template extraction | template_extraction |
| ART-007 | Safety filter configuration | filter_config_extraction |
| ART-008 | Model card information | model_card_extraction |
| ART-009 | Rate limit configuration | ratelimit_extraction |
| ART-010 | API endpoint discovery | endpoint_discovery |

**OWASP Mapping:** LLM07 (Insecure Plugin Design)

**Time:** 3-4 hours

---

### Phase 4: Add AML.T0052 - Spearphishing via AI

**MITRE Description:** Attackers may use AI to generate convincing phishing content.

**New Attack Type:** `AIAssistedPhishingAttack`

**Probes (10 estimated):**

| ID | Probe | Technique |
|----|-------|-----------|
| PHISH-001 | Phishing email generation | email_generation |
| PHISH-002 | Social engineering content | social_engineering |
| PHISH-003 | Impersonation content | impersonation |
| PHISH-004 | Credential harvesting text | credential_harvest |
| PHISH-005 | Urgency manipulation | urgency_tactics |
| PHISH-006 | Authority impersonation | authority_abuse |
| PHISH-007 | Trust exploitation | trust_exploitation |
| PHISH-008 | Technical support scam | techsupport_scam |
| PHISH-009 | BEC-style content | bec_generation |
| PHISH-010 | Vishing script generation | vishing_content |

**OWASP Mapping:** N/A (responsible AI issue)

**Time:** 3-4 hours

---

## Summary Table

| Phase | Technique | Attack Type | Probes | OWASP | Time |
|-------|-----------|-------------|--------|-------|------|
| 0 | - | Doc fix | - | - | 15m |
| 1 | AML.T0045 | InferenceAPIAbuse | 15 | LLM04 | 4-6h |
| 2 | AML.T0048 | (Enhance existing) | 5 | LLM06 | 3-4h |
| 3 | AML.T0047 | ArtifactExtraction | 10 | LLM07 | 3-4h |
| 4 | AML.T0052 | AIAssistedPhishing | 10 | - | 3-4h |

**Total:** 40 new probes, 4 new MITRE techniques (5→9 total)

---

## Post-Implementation State

### After Full Implementation

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| MITRE ATLAS Techniques | 5 | 9 | +80% |
| Total Probes | 106 | 146 | +38% |
| OWASP Coverage | 3/10 | 4/10 | +1 category |

### Updated MITRE Coverage

| ID | Name | Status |
|----|------|--------|
| AML.T0024 | Develop Capabilities | ✅ |
| AML.T0037 | Data from Info Repos | ✅ |
| AML.T0043 | Craft Adversarial Data | ✅ |
| AML.T0045 | Inference API | ✅ **NEW** |
| AML.T0047 | ML Artifact Collection | ✅ **NEW** |
| AML.T0048 | Exfiltration via ML API | ✅ **NEW** |
| AML.T0051 | LLM Prompt Injection | ✅ |
| AML.T0052 | Spearphishing via AI | ✅ **NEW** |
| AML.T0054 | LLM Jailbreak | ✅ |

**Coverage: 9/12 relevant techniques (75%)**

---

## Decision Required

### Option A: Minimal (Fix Docs Only)
- Fix documentation discrepancy
- Acknowledge 5 techniques accurately
- **Time:** 15 minutes

### Option B: Moderate (Fix + Phase 1-2)
- Fix documentation
- Add AML.T0045 (Inference API)
- Enhance AML.T0048 (Exfiltration)
- **Time:** 1 week
- **Result:** 7 techniques, 126 probes

### Option C: Full Implementation (All Phases) ✅ RECOMMENDED
- Fix documentation
- All 4 phases
- **Time:** 1-2 weeks
- **Result:** 9 techniques, 146 probes, 75% ATLAS coverage

---

## Files to Create/Modify

### New Files

```
src/AgentEval/RedTeam/Attacks/
├── InferenceAPIAbuseAttack.cs      (Phase 1)
├── ArtifactExtractionAttack.cs     (Phase 3)
└── AIAssistedPhishingAttack.cs     (Phase 4)

src/AgentEval/RedTeam/Evaluators/
└── InferenceAbuseEvaluator.cs      (Phase 1)

tests/AgentEval.Tests/RedTeam/Attacks/
├── InferenceAPIAbuseAttackTests.cs
├── ArtifactExtractionAttackTests.cs
└── AIAssistedPhishingAttackTests.cs
```

### Files to Modify

```
src/AgentEval/RedTeam/Attacks/
├── PIILeakageAttack.cs             (Phase 2 - add probes)
└── SystemPromptExtractionAttack.cs (Phase 2 - add probes)

src/AgentEval/RedTeam/AttackFactory.cs (register new attacks)
docs/redteam.md (fix count, add new attacks)
```

---

## Success Criteria

1. ✅ Documentation accurately reflects implemented techniques
2. ✅ 9 MITRE ATLAS techniques mapped (75% coverage)
3. ✅ 146+ total probes
4. ✅ All new tests passing
5. ✅ Updated docs with new attack types

---

*Plan created: January 30, 2026*
