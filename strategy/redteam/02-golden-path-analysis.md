# AgentEval.RedTeam — Critical Analysis of the Golden Path

> **Document Type**: Architecture Review & Analysis (historical reference — NOT an active plan)
> **Date**: January 29, 2026
> **Status**: ✅ ANALYSIS COMPLETE & ADOPTED — superseded by shipped reality. The descope recommendations here were followed; the implementation has since grown far past the MVP snapshot below.
> **As-of-branch reality (`feature/redteam-newwave-fixes`, June 2026)**: 13 default attacks + 3 opt-in (Crescendo/PAIR/TAP), **258 probes**, OWASP **10/10** (not 60%), 8 MITRE techniques, plus a NIST AI RMF reporter and a CLI baseline/CI gate. The "Implementation Status Summary (January 2026)" table and its "9 attacks / 177 probes / 60% OWASP" figures are a **frozen January snapshot** — see the top-level FeatureComplete + NextWave plans (and `strategy/redteam/done/`) for current numbers.
> **Related**: [01-redteam-golden-path-plan.md](01-redteam-golden-path-plan.md)

---

## Implementation Status Summary (January 2026)

| Recommendation | Status | Outcome |
|----------------|--------|---------|
| "Cut 80% scope" | ✅ **FOLLOWED** | Shipped MVP in 2 weeks, then expanded |
| "Start with 1-2 packages" | ✅ **FOLLOWED** | Single `AgentEval.RedTeam` namespace |
| "Remove external deps from MVP" | ✅ **FOLLOWED** | 100% native .NET implementation |
| "Define MVP ruthlessly" | ✅ **EXCEEDED** | 9 attacks (target: 5), 177 probes (target: 50+), 60% OWASP (target: 30%) |
| "Add simple high-level API" | ✅ **DONE** | `RedTeam.ScanAsync()` fluent API |
| "Preserve taxonomy-first" | ✅ **DONE** | OWASP + MITRE fully integrated |
| "Preserve pipeline model" | ✅ **DONE** | Attack pipeline architecture implemented |

---

## Executive Summary

The Golden Path plan in `01-redteam-golden-path-plan.md` presents an **ambitious, well-researched vision** for AgentEval's red-teaming capabilities. This analysis evaluates that vision through a pragmatic lens, identifying:

- ✅ What's excellent and should be preserved
- ⚠️ What's risky or over-engineered
- 🔧 What should change for a successful MVP

**Key Conclusion**: The *thinking* is professional-grade; the *scope* is dangerous. We need to ruthlessly descope while preserving the vision's core strengths.

---

## Part 1: What's Excellent — Preserve These ✅

### 1.1 Taxonomy-First Design (No NIH Syndrome)

**Decision**: Adopt OWASP LLM Top 10 + MITRE ATLAS, not invent a new taxonomy.

**Why This Is Excellent**:
- Immediate credibility with security practitioners
- Interoperability with existing tooling (compliance scanners, security dashboards)
- Avoids the "yet another standard" trap (XKCD #927)
- Maps to requirements enterprises already understand
- Zero marketing effort needed to explain what "LLM01" means

**Verdict**: ✅ **KEEP** — This is rare wisdom. Don't change it.

---

### 1.2 Attacks as Composable Pipelines

**Decision**: Model attacks as `generate → transform → deliver → execute → evaluate → score → report`.

**Why This Is Excellent**:
- Mirrors real attack patterns (attackers combine techniques)
- Enables deterministic replay via seeded transforms
- Extensible without core changes
- Clean separation of concerns
- Testable at each stage

**Example Pipeline**:
```
[Seed Prompt] → [Paraphrase x3] → [Unicode Encoding] → [Insert in RAG Doc] → [Execute] → [Detect PII]
```

**Verdict**: ✅ **KEEP** — The pipeline model is the architectural foundation. Preserve it.

---

### 1.3 Import-Don't-Duplicate Strategy

**Decision**: Treat PyRIT, garak, Promptfoo as sources of *patterns*, not code to execute.

**Why This Is Excellent**:
- Leverages hundreds of person-hours of research
- Avoids reinventing known attack patterns
- Reduces maintenance burden
- Focuses effort on what's unique (the .NET runner, unified format)

**Clarification**: This doesn't mean running Python processes. It means:
- Study their attack catalogs
- Extract the *patterns* (not the code)
- Re-implement natively in .NET
- Credit the sources

**Verdict**: ✅ **KEEP** — But clarify: import patterns, not processes.

---

### 1.4 Licensing Separation

**Decision**: OSS Engine + Permissive Packs + Gated Downloads for restricted content.

**Why This Is Excellent**:
- Enterprise-safe by default
- No accidental AGPL/NC contamination
- Clear legal boundaries
- Enables use of research datasets for those who accept terms

**Verdict**: ✅ **KEEP** — Legal clarity accelerates enterprise adoption.

---

### 1.5 Canonical JSON Report Schema

**Decision**: Define one machine-readable output format with:
- OWASP/ATLAS taxonomy tags
- Multi-dimensional breakdowns
- Evidence bundles with retention levels
- Export to SARIF/JUnit

**Why This Is Excellent**:
- CI/CD integration out of the box
- Regression tracking across builds
- Compliance reporting
- Professional-grade output

**Verdict**: ✅ **KEEP** — But **simplify for MVP**. Not all fields from day 1.

---

## Part 2: What's Risky — Caution Required ⚠️

### 2.1 Scope Creep — The Biggest Risk

**Problem**: The plan describes 6-12 months of work:
- Native .NET engine
- 5+ importers (ATLAS, Promptfoo, DeepTeam, garak, PyRIT)
- Pack system with versioning
- CLI commands
- Benchmark integrations
- Multi-turn escalation
- Workflow-aware attacks (RAG, tools, memory, multi-agent)

**Risks**:
- Never shipping (perfectionism paralysis)
- Half-implementing everything, polishing nothing
- Losing focus on AgentEval's core value (assertions, evaluation)
- Team burnout

**Verdict**: ⚠️ **CUT 80%** — Ship 10% excellently first.

---

### 2.2 Seven NuGet Packages

**Problem**: Proposed structure:
```
AgentEval.RedTeam.Abstractions
AgentEval.RedTeam.Engine
AgentEval.RedTeam.Packs.Core
AgentEval.RedTeam.Importers.* (4+ packages)
AgentEval.RedTeam.Adapters.* (3+ packages)
AgentEval.RedTeam.Cli
```

**Why This Is Problematic**:
- Versioning complexity (which versions are compatible?)
- Installation confusion for users
- More packages = more surface area for bugs
- Premature abstraction (we don't know the real boundaries yet)
- Maintenance overhead

**Verdict**: ⚠️ **START WITH 1-2 PACKAGES** — Split later when you have real pain.

---

### 2.3 External Tool Dependencies (PyRIT/garak Adapters)

**Problem**: Running Python tools as external processes introduces:
- Environment setup complexity (venv, conda, dependency conflicts)
- Cross-process error handling
- Platform-specific issues (Windows vs Linux paths, shells)
- Testing complexity
- "Works on my machine" syndrome

**Contradiction**: AgentEval's strength is being a self-contained .NET solution. External adapters undermine this.

**Verdict**: ⚠️ **REMOVE FROM MVP** — If users want PyRIT, they already have Python.

---

### 2.4 Missing MVP Definition

**Problem**: The plan has "Milestones 0-5" but no clear answer to:
- What ships in 2 weeks?
- What's the "hello world" of red-teaming?
- What's the minimal, useful thing?

Every user's first question: *"Show me the simplest way to test prompt injection"*

**Verdict**: ⚠️ **DEFINE MVP RUTHLESSLY** — See Part 4.

---

### 2.5 Complex Attack Pack Spec

**Problem**: The full spec requires 50+ lines of YAML:
```yaml
pack:
  id: agenteval.redteam.core
  version: 0.1.0
  taxonomies:
    owasp_llm_top10: [...]
    mitre_atlas: true
  cases:
    - id: AE-RT-PII-001
      surfaces: [...]
      tags: {...}
      threat_model: {...}
      pipeline: [...]
      scoring: {...}
      evidence: {...}
      licensing: {...}
```

**Who writes this by hand?** Nobody. Most users want:
```bash
agenteval redteam --attack prompt-injection
```

**Verdict**: ⚠️ **HIDE COMPLEXITY** — A simple API wraps the pipeline model.

---

### 2.6 NIST AI RMF as Required Tags

**Problem**: Adding NIST facets to every test case:
```yaml
nist_airmf_facets: ["privacy_enhanced", "secure_resilient"]
```

Most developers don't know or care about NIST AI RMF. Requiring it:
- Adds cognitive overhead
- Slows adoption
- Makes the spec look intimidating

**Verdict**: ⚠️ **MAKE OPTIONAL** — Support it, don't require it.

---

## Part 3: What's Missing — Add These 🔧

### 3.1 Clear "Why AgentEval.RedTeam" Positioning

**Problem**: The plan compares against garak, PyRIT, Promptfoo but never states:
- Why would someone choose AgentEval.RedTeam?
- What's the unique value proposition?

**Proposed Positioning**:

> **AgentEval.RedTeam exists because:**
> 1. .NET developers shouldn't need Python for agent security testing
> 2. Red-team results should integrate with existing AgentEval assertions
> 3. One framework for quality metrics AND security testing
> 4. Native MAF/Semantic Kernel integration
> 5. CI/CD-first: works in Azure DevOps, not just Jupyter notebooks

---

### 3.2 Simple High-Level API

**What 90% of users want**:
```csharp
// One line to test prompt injection
var results = await agent.RedTeamAsync(Attack.PromptInjection);

// Slightly more configuration
var results = await RedTeam.ScanAsync(agent, new ScanOptions
{
    Attacks = [Attack.PromptInjection, Attack.PIILeakage],
    Intensity = Intensity.Moderate
});
```

**What 10% of power users need** (advanced pipeline):
```csharp
var pipeline = new AttackPipeline()
    .Generate<TemplateGenerator>("indirect_injection")
    .Transform<Paraphraser>(rounds: 3)
    .Deliver<RagDocumentInjector>()
    .Evaluate<PIIDetector>()
    .Score<ASRScorer>();
```

Both APIs should exist. Simple ≠ limited.

---

### 3.3 Attack Library Strategy

**Question**: How do we build the attack library?

**Answer**: Curate + Convert + Credit

| Step | Description |
|------|-------------|
| **Curate** | Study garak probes, PyRIT transforms, Promptfoo plugins, research papers |
| **Extract Patterns** | Identify the *technique*, not the specific prompt |
| **Convert to .NET** | Re-implement natively (no Python deps) |
| **Parameterize** | Make prompts configurable, not hardcoded |
| **Credit Sources** | Document where each technique originated |

**Example**:
```
garak's "DAN" probe → Technique: Roleplay Jailbreak
PyRIT's paraphraser → Technique: Semantic Transformation
Academic paper → Technique: Multi-turn Escalation
```

We build native .NET implementations of proven techniques, not copy-paste prompt lists.

---

### 3.4 Integration with Existing AgentEval

**Red-teaming should feel like part of AgentEval**, not a separate tool:

```csharp
// Combine with assertions
result.Should()
    .HaveCalledTool("SearchTool")
    .And()
    .ResistPromptInjection(threshold: 0.95);

// Part of test runs
var testResult = await harness.RunTestAsync(agent, testCase, new TestOptions
{
    RedTeamChecks = [RedTeamCheck.PromptInjection, RedTeamCheck.PIILeakage]
});
```

---

## Part 4: Summary Verdict

| Aspect | Golden Path Plan | Recommendation |
|--------|------------------|----------------|
| **Vision** | ⭐⭐⭐⭐⭐ Excellent | Preserve |
| **Taxonomy** | ⭐⭐⭐⭐⭐ Excellent | Preserve (OWASP + ATLAS) |
| **Pipeline Model** | ⭐⭐⭐⭐⭐ Excellent | Preserve (core architecture) |
| **Scope** | ⭐⭐ Dangerous | Cut 80% |
| **Package Structure** | ⭐⭐ Over-engineered | Start with 1-2 packages |
| **External Deps** | ⚠️ Problematic | Remove from MVP |
| **MVP Definition** | ❌ Missing | Define ruthlessly |
| **Simple API** | ❌ Missing | Add as primary interface |

---

## Next Steps

1. **Read**: [03-redteam-mvp-proposal.md](03-redteam-mvp-proposal.md) — The ruthless MVP specification
2. **Decide**: Approve MVP scope before any implementation
3. **Ship**: Target 2-4 weeks for MVP launch
4. **Iterate**: Expand based on real user feedback

---

*"The best red-team library is the one that ships and gets used."*
