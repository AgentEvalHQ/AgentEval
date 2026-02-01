````instructions
---
applyTo: "**/*.md"
description: Comprehensive guidelines for AgentEval documentation - brand alignment, terminology, and structure
---

# AgentEval Documentation Guidelines

## 🎯 Core Principles

### 1. Evaluation First, Testing Second
- Lead with evaluation capabilities (quality metrics, LLM-as-judge)
- Follow with testing infrastructure (assertions, CI/CD)
- Frame testing as "automation of evaluation results"

### 2. Never Reveal Future Plans
Unless explicitly asked:
- Do NOT discuss upcoming features or planned changes
- Do NOT reference strategic direction or internal plans
- Do NOT mention /strategy folder content
- Focus on what IS available, not what WILL BE

### 3. No Redundancy - Point to Documents
- Never repeat detailed information already in another document
- Link to specific documents: "See [assertions.md](docs/assertions.md) for details"
- Only repeat key principles or critical safety information

### 4. No Specific Numbers or Versions
| ❌ Don't Use | ✅ Use Instead |
|-------------|---------------|
| "3,015+ tests" | "comprehensive test suite" |
| "21 samples" | "detailed examples" |
| "v0.2.0-beta" | "current version" |

---

## 🏷️ Brand Identity

**Primary Tagline:** "The .NET Evaluation Toolkit for AI Agents"

**AgentEval IS:**
- The .NET equivalent of RAGAS and DeepEval for Python
- Built first for Microsoft Agent Framework (MAF)
- An evaluation framework that also does testing

**AgentEval is NOT:**
- Just a testing framework
- A Python port or wrapper
- A simple assertion library

### Terminology Standards

| ✅ Preferred | ❌ Avoid | Context |
|-------------|---------|---------|
| evaluation | testing | Core purpose description |
| evaluate | test | When describing metrics |
| evaluation run | test run | Metric execution |
| evaluation result | test result | Metric outputs |

**Exception:** "Testing" is acceptable when discussing xUnit/NUnit/MSTest or CI/CD.

---

## 📚 Documentation Structure

### Folder Layout
- `docs/` - Main documentation folder
- `docs/adr/` - Architecture Decision Records
- `docs/api/` - Generated API reference
- `docs/showcase/` - Examples and case studies
- `README.md` - Project overview
- `CONTRIBUTING.md` - Contribution guidelines

### Document Section Order (Required)
1. **Purpose & Evaluation Context** - What quality aspects it addresses
2. **Quick Example** - Show immediate value
3. **Configuration & Setup** - Implementation details
4. **Testing Integration** - xUnit/NUnit usage
5. **CI/CD & Automation** - Export formats, scripts

### Section Headers (Use Evaluation-First Language)

| ✅ Use | ❌ Avoid |
|--------|---------|
| "Evaluation Quick Start" | "Testing Quick Start" |
| "Running Evaluations" | "Running Tests" |
| "Evaluation Results" | "Test Results" |

---

## 🏆 Feature Hierarchy

Always present features in this order:
1. **Tool Usage Evaluation** - Fluent assertions for tool chains
2. **Performance & Cost Metrics** - SLAs as code
3. **RAG Quality Metrics** - Complete pipeline coverage
4. **Red Team Security** - OWASP + MITRE coverage
5. **LLM-as-Judge** - Flexible evaluation criteria
6. **Model Comparison & Stochastic Evaluation**

**Never lead with:** Testing infrastructure, CI/CD integration, or Assertion APIs

---

## 📝 Code Examples

### Always Introduce with Evaluation Framing

✅ **Correct:**
```markdown
### Evaluate Tool Chain Behavior
Validate that your agent calls tools in the correct sequence:
```

❌ **Incorrect:**
```markdown
### Testing Tool Calls
Test that the agent called SearchFlights:
```

### Code Block Standards
- Use fenced code blocks with language hints
- Always use complete, runnable examples
- Include comments explaining the evaluation purpose

---

## 📋 ADR Format

Architecture Decision Records follow this structure:

```markdown
# ADR-XXX: Title

> **Status:** Proposed | Accepted | Deprecated | Superseded
> **Date:** YYYY-MM-DD
> **Decision Makers:** Team members involved

---

## Context
The situation that led to this decision.

## Decision
What we decided to do.

## Consequences
### Positive
### Negative
### Neutral

## Alternatives Considered
Other options evaluated.
```

---

## 🔗 Cross-References

Link to related docs instead of duplicating content:
```markdown
For the complete assertion API, see [Assertions Guide](docs/assertions.md).
```

---

## 🚫 Forbidden Actions

1. ❌ Reference specific version numbers publicly
2. ❌ Include /strategy/ folder paths
3. ❌ Use exact test counts or sample counts
4. ❌ Lead with testing features
5. ❌ Reveal roadmap or future plans
6. ❌ Duplicate content that exists elsewhere

### Content Guidelines

**❌ Never Write:**
- "AgentEval is a testing framework for AI agents"
- "Run tests to validate your agent"
- "3,015+ tests ensure quality"
- "See strategy/AgentEval-Strategy.md for details"

**✅ Always Write:**
- "AgentEval is the evaluation toolkit for AI agents"
- "Evaluate agent behavior with statistical confidence"
- "Comprehensive test suite ensures quality"
- "See docs/architecture.md for details"

---

## ✅ Pre-Publication Checklist

- [ ] Uses "evaluation" terminology appropriately
- [ ] Follows feature hierarchy in presentations
- [ ] No specific version numbers or test counts
- [ ] No references to /strategy folder
- [ ] No future plans or roadmap hints
- [ ] Points to docs instead of duplicating content
- [ ] Code examples compile and run
- [ ] All links work
- [ ] Follows required section order

````
