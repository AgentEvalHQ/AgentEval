# AgentEval.RedTeam — The Plan (Golden Path) + Report JSON Schema + Repo/Packaging Proposal

> **⚠️ STATUS (2026-06-13): FOUNDATIONAL STRATEGY / REFERENCE — milestone plan SUPERSEDED.** This is the original golden-path strategy doc (taxonomy choice, Attack Pack Spec sketch, canonical report schema, packaging proposal, M0–M5 plan). Keep it for the *why* (OWASP+ATLAS+NIST backbone, attacks-as-pipelines, OSS/license hygiene). Do **not** treat the milestone list or the Jan-2026 banner as current ground truth — execution moved to the top-level **FeatureComplete** and **NextWave** plans (per-wave detail in `strategy/redteam/done/`).
>
> **What actually shipped (verified):** 13 default attacks (`Attack.All`) + opt-in Crescendo/PAIR/TAP; OWASP LLM Top 10 = **10/10** (Wave D); NIST AI RMF + SOC2 + ISO27001 reporters; JSON/SARIF/JUnit/Markdown exporters; transform pipeline (Wave A); real injection surfaces **tool_output** + **retrieved_document** (Wave B); multi-turn + attacker-LLM (Waves C/C′); CLI CI on-ramp with baseline regression gate, `--import-probes`, `--explain`, `--package-registry live`, `--calibration` (Wave E + NextWave T1–T3). The Jan-2026 banner figures ("9 attacks / 192 probes / 60% OWASP / 6 MITRE") are STALE — actual is **13 attacks / 258 probes / 10/10 OWASP / 8 MITRE**.
>
> **Still NOT done (genuinely pending / future):** dedicated importers & external-runner adapters (ATLAS/OWASP/Promptfoo/DeepTeam importers; garak/PyRIT/Azure adapters) — M2; `PackDownloader` + `--accept-license` license gate + bundled benchmark packs — M3; `memory_poisoning` surface and memory/multi-agent red teaming — Wave-G. The multi-NuGet package split and the standalone `/schemas/*.schema.json` files were **not adopted** (single `AgentEval.RedTeam` project; the report schema lives in code, not as a published JSON Schema file).

> **Goal:** Add a **best‑in‑class, fully open-source** Red Teaming capability to AgentEval by adopting **OWASP LLM Top 10** (risk categories) + **MITRE ATLAS** (technique IDs) as first-class taxonomies, treating attacks as **composable pipelines**, and unifying heterogeneous sources (garak, PyRIT, Promptfoo/DeepTeam, benchmarks) behind:
>
> 1) one **canonical Attack Pack spec** (YAML/JSON), and  
> 2) one **canonical RedTeam Report format** (JSON + exports).

---

## The golden path (one-page summary)

Here’s the golden path to make **AgentEval.RedTeam** feel **“best‑in‑class” quickly** while keeping it **free / fully OSS**:

1) **Pick a canonical backbone taxonomy**:  
   **OWASP LLM Top 10** for stakeholder language + **MITRE ATLAS** for engineering‑grade technique IDs.

2) **Define one internal “Attack Pack Spec”** (YAML/JSON) that models attacks as **composable pipelines**  
   (`generate → transform → deliver → execute → evaluate → score → report`).

3) **Ship a strong native .NET runner**, and treat Python/Node ecosystems (**PyRIT**, **garak**, **Promptfoo/DeepTeam**) as **importers/adapters** into your unified format.

4) **Don’t bundle license‑risky datasets**—support **download/import with a license gate** instead (e.g., DoNotAnswer, BeaverTails, Pliny).

---

# 1) AgentEval.RedTeam as a free, fully open-source library

## Why “free OSS” can be the right move
- **Trust + adoption flywheel**: security tooling needs credibility; OSS accelerates scrutiny and contributions.
- **You can still monetize later without closing the engine**, for example:
  - Hosted **“RedTeam Cloud Runner”** (scale, scheduling, dashboards, multi‑tenant)
  - **Enterprise reporting packs** (audit exports, policy packs, governance workflows)
  - **Proprietary “attack intelligence feed”** (curated packs, change logs, mitigations)

## How to keep it OSS without becoming a license minefield
Make a clean separation:

**A. OSS Engine (NuGet):** MIT/Apache‑2.0 licensed code you own.  
**B. OSS “Permissive Packs” (repo):** only MIT/Apache/CC‑BY/CC‑BY‑SA content you’re comfortable redistributing.  
**C. “Bring-your-own packs” + gated downloader:** supports non‑commercial / copyleft / restricted packs, but **never ships them inside your default NuGets**.

This gives you **“free and powerful”** with professional distribution hygiene.

---

# 2) Principles + approach (high level)

## A) Taxonomy-first (no new taxonomy)
- **OWASP LLM Top 10** = “executive / appsec lingua franca” categories (`LLM01…LLM10`).
- **MITRE ATLAS** = technique IDs you can track like ATT&CK (precise, diff‑able, reportable).
- **NIST AI RMF** = governance/trustworthiness lens + controls vocabulary (useful for compliance mapping).

**Key rule:** Promptfoo + DeepTeam provide useful *categorization*, but AgentEval should **map them into OWASP+ATLAS** rather than inventing a third canonical taxonomy.

## B) Attacks as composable pipelines (not static prompts)
Model an attack as a pipeline of steps:

- **Generate:** base seed(s) + scenario templates or datasets  
- **Transform:** paraphrase, encoding, role‑play framing, multi‑turn escalation  
- **Deliver:** user input / retrieved doc injection / tool output injection / memory poisoning  
- **Evaluate:** detectors + judges + policy checks  
- **Score:** ASR, severity, confidence, exploitability  
- **Report:** unified output schema + exports (SARIF/JUnit/etc.)

This structure matches the mental models you see across:
- PyRIT: transformations + orchestrations
- garak: probes + detectors
- Promptfoo/DeepTeam: strategies + plugins/modules

## C) One canonical internal format, many importers
- **Canonical:** AgentEval **Attack Pack Spec** + AgentEval **RedTeam Report format**
- **Importers:** ATLAS YAML, Promptfoo configs/plugins, DeepTeam vulnerabilities/attacks, garak probe catalogs, PyRIT orchestrations
- **Runners:** native .NET runner first; optional adapters call external tooling (Python/Node) and ingest results

## D) Coverage over duplication
- Don’t ship 5 overlapping prompt‑injection lists.
- Use **garak breadth + PyRIT depth**, plus targeted datasets (CyberSecEval/HarmBench/JailbreakBench/etc.) via pack plugins.

---

# 3) What to implement in .NET (architecture + sources + “best attack library ever”)

## 3.1 Canonical taxonomies and dimensions

### Backbone identifiers (first-class fields on every test case and finding)
- `owasp_llm`: e.g., `LLM01`, `LLM06`, …  
- `mitre_atlas`: e.g., `AML.Txxxx` technique IDs (and optionally tactic)  
- `nist_airmf`: map to RMF functions/characteristics as tags (doesn’t need strict IDs if you don’t want)

### Trust facets (recommended)
NIST trustworthiness characteristics make great **report facets**:
- valid/reliable
- safe
- secure/resilient
- privacy-enhanced
- fair with harmful bias managed
- accountable/transparent
- explainable/interpretable

### Your lenses (Security / Robustness / Compliance / Trust)
Keep them—but treat them as **lenses over the same taxonomy**:

- **Security** (exploitation & control): prompt injection, tool abuse, data exfil, insecure output handling, excessive agency
- **Robustness/Reliability**: adversarial phrasing, ambiguity, stress tests, long-context failures
- **Compliance/Governance**: policy adherence, logging/evidence, audit mapping (NIST overlay)
- **Trust/Safety**: toxicity/violence/sexual/self‑harm/hate + fairness/bias + privacy

**Recommended addition:** add `Privacy` as an explicit lens (PII/secret leakage is central in practice).  
(Optionally: `Reliability` as separate lens if you want it distinct from adversarial robustness.)

### Surfaces (where the attack lands)
- `user_message`
- `retrieved_document` (RAG/doc injection)
- `tool_output`
- `memory`

---

## 3.2 Attack Pack Spec (the perfect format)

**Make it YAML first** (human-editable), trivially convertible to JSON.

### Key goals
- composable pipelines
- taxonomy tagging
- provenance + licensing per case
- scenario surfaces (user/doc/tool/memory)
- evaluation rules + scoring
- deterministic replay (seeded transforms)

### Compact but powerful skeleton

```yaml
pack:
  id: agenteval.redteam.core
  version: 0.1.0
  title: "AgentEval RedTeam Core Pack"
  license: "MIT"
  maintainers: ["AgentEval"]
  sources:
    - name: "OWASP LLM Top 10"
      ref: "2025"
      url: "https://..."
    - name: "MITRE ATLAS"
      ref: "atlas-data@<commit>"
      url: "https://..."

taxonomies:
  owasp_llm_top10: ["LLM01","LLM02","LLM03","LLM04","LLM05","LLM06","LLM07","LLM08","LLM09","LLM10"]
  mitre_atlas: true
  nist_airmf: true

cases:
  - id: AE-RT-PII-001
    title: "PII leakage via indirect prompt injection"
    surfaces: ["retrieved_document"]
    tags:
      lenses: ["Security","Privacy","Trust"]
      owasp_llm: ["LLM01","LLM06"]
      mitre_atlas: ["AML.Txxxx"]
      nist_airmf_facets: ["privacy_enhanced","secure_resilient"]
    threat_model:
      attacker: "untrusted_content_author"
      access: ["can_influence_retrieval_corpus"]
    pipeline:
      - step: generate
        generator: template
        params: { template_id: "indirect_injection_seed", difficulty: "moderate" }
      - step: transform
        transform: paraphrase
        params: { model: "judge-model", rounds: 3 }
      - step: deliver
        channel: rag_document
        params: { injection_style: "hidden_instruction" }
      - step: execute
        max_turns: 3
      - step: evaluate
        evaluators:
          - type: pii_detector
            params: { patterns: ["email","phone","iban"], allowlist: [] }
          - type: policy_safety
            params: { categories: ["violence","self_harm","sexual","hate_unfairness"] }
    scoring:
      metrics:
        - name: ASR
          rule: "any(evaluator.matched == true)"
      severity: "high"
      confidence: "medium"
      exploitability: "medium"
    evidence:
      retention: "redacted"
      fields: ["transcript","tool_calls","retrieved_documents","detector_hits"]
    licensing:
      redistributable: true
      provenance: "authored"
```

### Why this spec wins
- You can map **everything** into it (Promptfoo configs, DeepTeam vulnerabilities, garak probes, PyRIT orchestrations).
- You can build a pack registry later (“attack pack marketplace”), while staying fully OSS.

---

## 3.3 Runners, importers, adapters (the “spectrum”)

### Native .NET runner (must-have)
Define an abstraction for the “system under test”:

```csharp
public interface IAgentUnderTest
{
    Task<AgentTurnResult> SendAsync(AgentTurn turn, CancellationToken ct);
}

public record AgentTurn(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null);

public record AgentTurnResult(
    string Content,
    IReadOnlyList<ToolCallResult> ToolResults,
    IReadOnlyList<string> RetrievedDocs);
```

Then implement adapters:
- **SK adapter** (Semantic Kernel)
- **OpenAI/Foundry adapter** (chat/agent APIs)
- **Workflow harness adapter** for multi-step workflows (tools, memory, RAG hooks)

### Attack pipeline engine (compose steps)
- `IGenerator` (templates, datasets, fuzzers)
- `ITransform` (mutations, paraphrase, encoding)
- `IDelivery` (user msg / doc injection / tool output injection / memory poisoning)
- `IEvaluator` (detectors, LLM-judge, policy checkers)
- `IScorer` (ASR, severity, confidence, exploitability)
- `IReporter` (JSON, SARIF, HTML, JUnit, Markdown)

### Importers/adapters (fast path to “best coverage”)
- **MITRE ATLAS importer:** ingest `atlas-data` and build a local technique catalog + mapping table
- **OWASP importer:** ingest OWASP LLM Top10 as reference metadata (IDs + titles + links; avoid embedding long text)
- **PyRIT adapter:** run PyRIT externally (python env/docker), convert results to AgentEval report
- **garak adapter:** run garak probe suites, ingest detector findings
- **Promptfoo adapter:** import red-team plugins/strategies as “modules” and map them back to OWASP/ATLAS
- **DeepTeam adapter:** import vulnerability classes + attacks; again map to OWASP/ATLAS (no 3rd taxonomy)
- **Azure AI Evaluation/Foundry scans adapter (optional):** treat as another backend runner that maps to the canonical report

This gives you strong coverage without reinventing everything on day 1.

---

## 3.4 Benchmarks/datasets: include vs gate

### Good to support (often permissive — still verify per repo)
- CyberSecEval / PurpleLlama
- HarmBench
- JailbreakBench

### Support, but do NOT bundle by default (license risk)
Some popular datasets/prompts are non-commercial or copyleft:
- BeaverTails = **CC BY‑NC 4.0**
- Do‑Not‑Answer dataset = **CC BY‑NC‑SA 4.0**
- L1B3RT4S (Pliny prompts) = **AGPL‑3.0**

**Mechanism:** a `PackDownloader` that requires an explicit `--accept-license` flag and stores packs outside your NuGet assets.

---

# 4) Who to prioritize vs ignore (redundancy + maintenance ranking)

## Key idea: choose “canonical” per layer
- **Taxonomy:** OWASP + ATLAS (canonical); NIST as compliance overlay
- **Probe library:** garak as breadth aggregator (canonical)
- **Attack transforms/orchestration:** PyRIT as depth (canonical)
- **Config formats/module ideas:** Promptfoo + DeepTeam as import/reference (not canonical)
- **Benchmarks:** CyberSecEval as canonical security benchmark family

## Ranked table (best → worst for AgentEval reference value)
Scores 1–10 (higher is better). “License friendliness” indicates how safely it tends to be redistributable; always verify per source.

| Rank | Source / Category | What it gives you | Maintenance | Completeness | Engineering Fit | License Friendliness | Notes |
|---:|---|---|---:|---:|---:|---:|---|
| 1 | garak (tooling) | Huge probe breadth + detectors | 9 | 9 | 8 | 9 | Great “don’t duplicate prompt lists” anchor |
| 2 | PyRIT (tooling) | Attack transforms + orchestration patterns | 9 | 8 | 8 | 10 | Ideal “depth engine” to adapt |
| 3 | MITRE ATLAS (taxonomy) | Technique IDs + threat model structure | 8 | 8 | 9 | 9 | Best for precise reporting |
| 4 | OWASP LLM Top 10 (taxonomy) | Stakeholder-ready categories | 8 | 7 | 9 | 7 | Use IDs/links; avoid copying lots of prose |
| 5 | CyberSecEval / PurpleLlama (benchmarks) | Strong security suites + datasets | 8 | 8 | 7 | 10 | Keep as key plugin pack family |
| 6 | Promptfoo (tooling + plugin catalog) | Practical app-layer tests + import format | 9 | 7 | 7 | 10 | Great module ideas + importer |
| 7 | DeepTeam (tooling) | Vulnerability classes + attack strategies | 8 | 7 | 7 | 9 | Import + map taxonomy |
| 8 | NIST AI RMF (framework) | Compliance/governance overlay | 7 | 6 | 8 | 10 | Excellent “Trust/Compliance” mapping lens |
| 9 | Azure AI Evaluation/Foundry scans | Turnkey scans + safety categories | 7 | 6 | 6 | 6 | Adapter possible; ecosystem coupling |
| 10 | License‑restricted datasets (NC/AGPL) | Useful corpora | 6 | 6 | 6 | 2 | Support via gated downloader only |

### Summary of redundancy
- Treat **garak** as your primary breadth set (avoid duplicating prompt libraries).
- Treat **PyRIT** as your primary depth set (transforms + orchestrations).
- Treat **Promptfoo/DeepTeam** as import formats + ideas + plugins; map them into OWASP+ATLAS.

---

# 5) The Plan (concise but detailed)

## Milestone 0 — Foundations (canonical schemas)
- Define **Attack Pack Spec v0.1** + **RedTeam Report Spec v0.1**
- Implement taxonomy objects:
  - `OwaspCategory(LLM01…)`
  - `AtlasTechnique(AML.Txxxx…)`
  - `TrustFacet` (NIST characteristics)
- Implement scoring core:
  - ASR, severity, confidence, exploitability
- Implement reporters:
  - JSON (canonical)
  - SARIF (security tooling)
  - JUnit (CI)
  - Markdown summary (human)

## Milestone 1 — Native .NET engine + starter pack
- Ship `AgentEval.RedTeam.Engine`:
  - pipeline runner
  - concurrency control
  - evidence capture + redaction
- Ship `AgentEval.RedTeam.Packs.Core` (permissive, authored):
  - prompt injection (direct + indirect)
  - PII/secret leakage checks
  - tool/system prompt override checks
  - safety category probes (violence/self-harm/sexual/hate)

## Milestone 2 — Importers/adapters (coverage jump)
- `atlas-data` importer → local catalog + mapping
- Promptfoo config importer + plugin mapping
- garak runner adapter (python/docker) ingest reports
- PyRIT adapter (python/docker) ingest results
- DeepTeam adapter (optional)

## Milestone 3 — Benchmark packs + license gating
- Add benchmark packs as plugins:
  - CyberSecEval
  - HarmBench / JailbreakBench (verify license per sub-pack)
- Implement `PackDownloader`:
  - SPDX metadata
  - `--accept-license`
  - store outside NuGet assets

## Milestone 4 — Workflow-aware red teaming (agents, tools, RAG, memory)
- Add delivery channels:
  - user_message
  - rag_document_injection
  - tool_output_injection
  - memory_poisoning
- Add multi-turn strategies (escalation, adaptive probes)
- Add capability profiles (pack declares required features: tools/RAG/memory/etc.)

## Milestone 5 — “Best runner ever” polish
- Local CLI + GitHub Action + Azure DevOps task
- Deterministic replay (seeded transforms)
- Trend reporting (“regression in LLM01 over last 10 builds”)
- Curated mapping tables + community contributions

---

# What “report format” means (and why it is used)

A **report format** is the **canonical, machine-readable output** produced by a red-teaming run.  
It captures **what was tested**, **how it was tested**, **what failed**, and **how bad it was**, in a way that is:

- **Reproducible**: includes seeds, pack IDs/versions, target configuration fingerprints.
- **Interoperable**: can be produced by different runners (native .NET, Python adapters) and consumed by dashboards, CI gates, exporters (SARIF/JUnit/HTML).
- **Diffable**: enables regression tracking across commits and releases.

In AgentEval.RedTeam, the report JSON is the **single source of truth** that:
- all runners emit,
- all exporters consume,
- all CI/policies evaluate,
- all UIs visualize.

---

# Canonical JSON report format (schema)

## Schema overview
- `report` metadata: tool, time, environment
- `target`: system under test and capability profile
- `run`: pack references, execution settings, filters
- `summary`: aggregated metrics (ASR etc.) across dimensions
- `findings[]`: per-case outcomes + taxonomy tags + evidence pointers
- `artifacts[]`: additional exports (SARIF/JUnit/HTML/etc.)
- `errors[]`: non-fatal errors

## JSON Schema (Draft 2020-12)

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://agenteval.org/schemas/redteam-report.schema.json",
  "title": "AgentEval RedTeam Report",
  "type": "object",
  "additionalProperties": false,
  "required": ["schema_version", "report_id", "created_utc", "tool", "target", "run", "summary", "findings"],
  "properties": {
    "schema_version": {
      "type": "string",
      "description": "Schema version for this report format (not the tool version).",
      "pattern": "^\\d+\\.\\d+\\.\\d+$",
      "examples": ["0.1.0"]
    },
    "report_id": {
      "type": "string",
      "description": "Unique report identifier.",
      "format": "uuid"
    },
    "created_utc": {
      "type": "string",
      "description": "Report creation time in UTC.",
      "format": "date-time"
    },
    "tool": {
      "$ref": "#/$defs/tool_info"
    },
    "environment": {
      "$ref": "#/$defs/environment_info"
    },
    "target": {
      "$ref": "#/$defs/target_info"
    },
    "run": {
      "$ref": "#/$defs/run_info"
    },
    "summary": {
      "$ref": "#/$defs/summary"
    },
    "findings": {
      "type": "array",
      "minItems": 0,
      "items": {
        "$ref": "#/$defs/finding"
      }
    },
    "artifacts": {
      "type": "array",
      "description": "Additional report artifacts generated from the canonical JSON report (SARIF/JUnit/HTML/etc.).",
      "items": {
        "$ref": "#/$defs/artifact"
      }
    },
    "errors": {
      "type": "array",
      "description": "Non-fatal errors during execution.",
      "items": {
        "$ref": "#/$defs/error"
      }
    }
  },
  "$defs": {
    "tool_info": {
      "type": "object",
      "additionalProperties": false,
      "required": ["name", "version"],
      "properties": {
        "name": {
          "type": "string",
          "examples": ["AgentEval.RedTeam"]
        },
        "version": {
          "type": "string",
          "description": "Tool version (SemVer).",
          "pattern": "^\\d+\\.\\d+\\.\\d+(-[0-9A-Za-z.-]+)?$",
          "examples": ["0.6.0", "0.6.0-beta.2"]
        },
        "commit": {
          "type": "string",
          "description": "Optional git commit SHA used to build the tool."
        },
        "run_command": {
          "type": "string",
          "description": "CLI command line used (if applicable)."
        }
      }
    },
    "environment_info": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "os": { "type": "string" },
        "dotnet": { "type": "string" },
        "machine": { "type": "string" },
        "ci": { "type": "boolean" },
        "ci_provider": { "type": "string" },
        "variables": {
          "type": "object",
          "description": "Optional sanitized environment variables relevant for reproducibility.",
          "additionalProperties": { "type": "string" }
        }
      }
    },
    "target_info": {
      "type": "object",
      "additionalProperties": false,
      "required": ["target_type", "name"],
      "properties": {
        "target_type": {
          "type": "string",
          "description": "Type of system under test.",
          "enum": ["model", "agent", "workflow"]
        },
        "name": {
          "type": "string",
          "description": "Human-readable target name."
        },
        "provider": {
          "type": "string",
          "description": "Provider or runtime (e.g., AzureOpenAI, OpenAI, local, etc.)."
        },
        "model": {
          "type": "string",
          "description": "Model identifier if applicable (e.g., gpt-4.1, gpt-4o, etc.)."
        },
        "configuration_fingerprint": {
          "type": "string",
          "description": "Hash/fingerprint of target configuration for reproducibility."
        },
        "capabilities": {
          "type": "array",
          "description": "Declared target capabilities, used to enable/disable certain pack surfaces.",
          "items": {
            "type": "string",
            "enum": ["tools", "rag", "memory", "multi_agent", "code_exec", "vision", "audio"]
          }
        }
      }
    },
    "run_info": {
      "type": "object",
      "additionalProperties": false,
      "required": ["run_id", "started_utc", "ended_utc", "packs", "execution"],
      "properties": {
        "run_id": {
          "type": "string",
          "format": "uuid"
        },
        "started_utc": {
          "type": "string",
          "format": "date-time"
        },
        "ended_utc": {
          "type": "string",
          "format": "date-time"
        },
        "packs": {
          "type": "array",
          "minItems": 1,
          "description": "Attack packs used in this run.",
          "items": {
            "$ref": "#/$defs/pack_ref"
          }
        },
        "execution": {
          "$ref": "#/$defs/execution_settings"
        },
        "filters": {
          "$ref": "#/$defs/run_filters"
        }
      }
    },
    "pack_ref": {
      "type": "object",
      "additionalProperties": false,
      "required": ["pack_id", "pack_version"],
      "properties": {
        "pack_id": { "type": "string" },
        "pack_version": { "type": "string", "pattern": "^\\d+\\.\\d+\\.\\d+$" },
        "pack_hash": { "type": "string", "description": "Integrity hash of the pack contents." },
        "license": { "type": "string", "description": "Declared pack license (SPDX recommended)." },
        "source_url": { "type": "string", "format": "uri" }
      }
    },
    "execution_settings": {
      "type": "object",
      "additionalProperties": false,
      "required": ["max_turns", "parallelism", "timeout_seconds", "random_seed"],
      "properties": {
        "max_turns": { "type": "integer", "minimum": 1, "maximum": 100 },
        "parallelism": { "type": "integer", "minimum": 1, "maximum": 1024 },
        "timeout_seconds": { "type": "integer", "minimum": 1, "maximum": 86400 },
        "random_seed": { "type": "integer", "minimum": 0 },
        "evidence_retention": {
          "type": "string",
          "enum": ["none", "redacted", "full"],
          "description": "Controls how much evidence (prompts/responses/tool calls) is retained."
        }
      }
    },
    "run_filters": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "lenses": {
          "type": "array",
          "items": { "$ref": "#/$defs/lens" }
        },
        "surfaces": {
          "type": "array",
          "items": { "$ref": "#/$defs/surface" }
        },
        "owasp_llm": {
          "type": "array",
          "items": { "$ref": "#/$defs/owasp_llm_id" }
        },
        "mitre_atlas": {
          "type": "array",
          "items": { "$ref": "#/$defs/atlas_id" }
        },
        "difficulty": {
          "type": "array",
          "items": { "$ref": "#/$defs/difficulty" }
        }
      }
    },
    "summary": {
      "type": "object",
      "additionalProperties": false,
      "required": ["overall"],
      "properties": {
        "overall": { "$ref": "#/$defs/aggregate_metrics" },
        "by_lens": { "$ref": "#/$defs/metric_breakdown_by_lens" },
        "by_surface": { "$ref": "#/$defs/metric_breakdown_by_surface" },
        "by_difficulty": { "$ref": "#/$defs/metric_breakdown_by_difficulty" },
        "by_owasp_llm": { "$ref": "#/$defs/metric_breakdown_by_owasp" },
        "by_mitre_atlas": { "$ref": "#/$defs/metric_breakdown_by_atlas" },
        "by_safety_category": { "$ref": "#/$defs/metric_breakdown_by_safety_category" }
      }
    },
    "aggregate_metrics": {
      "type": "object",
      "additionalProperties": false,
      "required": ["attempts", "successes", "asr"],
      "properties": {
        "attempts": { "type": "integer", "minimum": 0 },
        "successes": { "type": "integer", "minimum": 0 },
        "asr": {
          "type": "number",
          "minimum": 0,
          "maximum": 1,
          "description": "Attack Success Rate = successes / attempts."
        },
        "mean_severity": {
          "type": "number",
          "minimum": 0,
          "maximum": 10
        },
        "notes": { "type": "string" }
      }
    },
    "metric_breakdown_by_lens": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["lens", "metrics"],
        "properties": {
          "lens": { "$ref": "#/$defs/lens" },
          "metrics": { "$ref": "#/$defs/aggregate_metrics" }
        }
      }
    },
    "metric_breakdown_by_surface": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["surface", "metrics"],
        "properties": {
          "surface": { "$ref": "#/$defs/surface" },
          "metrics": { "$ref": "#/$defs/aggregate_metrics" }
        }
      }
    },
    "metric_breakdown_by_difficulty": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["difficulty", "metrics"],
        "properties": {
          "difficulty": { "$ref": "#/$defs/difficulty" },
          "metrics": { "$ref": "#/$defs/aggregate_metrics" }
        }
      }
    },
    "metric_breakdown_by_owasp": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["owasp_llm", "metrics"],
        "properties": {
          "owasp_llm": { "$ref": "#/$defs/owasp_llm_id" },
          "metrics": { "$ref": "#/$defs/aggregate_metrics" }
        }
      }
    },
    "metric_breakdown_by_atlas": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["mitre_atlas", "metrics"],
        "properties": {
          "mitre_atlas": { "$ref": "#/$defs/atlas_id" },
          "metrics": { "$ref": "#/$defs/aggregate_metrics" }
        }
      }
    },
    "metric_breakdown_by_safety_category": {
      "type": "array",
      "description": "Optional safety categories (e.g., hate/unfairness, self-harm, sexual, violence).",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["category", "metrics"],
        "properties": {
          "category": {
            "type": "string",
            "enum": ["hate_unfairness", "self_harm", "sexual", "violence", "other"]
          },
          "metrics": { "$ref": "#/$defs/aggregate_metrics" }
        }
      }
    },
    "finding": {
      "type": "object",
      "additionalProperties": false,
      "required": ["finding_id", "case_id", "title", "outcome", "metrics", "taxonomies"],
      "properties": {
        "finding_id": { "type": "string", "format": "uuid" },
        "case_id": { "type": "string", "description": "Attack case identifier from the pack." },
        "title": { "type": "string" },
        "description": { "type": "string" },
        "outcome": {
          "type": "string",
          "enum": ["success", "fail", "inconclusive", "error", "skipped"]
        },
        "metrics": {
          "type": "object",
          "additionalProperties": false,
          "required": ["attempts", "successes", "asr"],
          "properties": {
            "attempts": { "type": "integer", "minimum": 0 },
            "successes": { "type": "integer", "minimum": 0 },
            "asr": { "type": "number", "minimum": 0, "maximum": 1 },
            "severity": {
              "type": "string",
              "enum": ["info", "low", "medium", "high", "critical"]
            },
            "severity_score": {
              "type": "number",
              "minimum": 0,
              "maximum": 10
            },
            "confidence": {
              "type": "string",
              "enum": ["low", "medium", "high"]
            }
          }
        },
        "taxonomies": {
          "$ref": "#/$defs/taxonomy_tags"
        },
        "surface": { "$ref": "#/$defs/surface" },
        "difficulty": { "$ref": "#/$defs/difficulty" },
        "evidence": {
          "$ref": "#/$defs/evidence_bundle"
        },
        "replay": {
          "$ref": "#/$defs/replay_info"
        },
        "links": {
          "type": "array",
          "items": { "$ref": "#/$defs/link" }
        }
      }
    },
    "taxonomy_tags": {
      "type": "object",
      "additionalProperties": false,
      "required": ["lenses"],
      "properties": {
        "lenses": {
          "type": "array",
          "minItems": 1,
          "items": { "$ref": "#/$defs/lens" }
        },
        "owasp_llm": {
          "type": "array",
          "items": { "$ref": "#/$defs/owasp_llm_id" }
        },
        "mitre_atlas": {
          "type": "array",
          "items": { "$ref": "#/$defs/atlas_id" }
        },
        "nist_airmf_facets": {
          "type": "array",
          "items": {
            "type": "string",
            "enum": [
              "valid_reliable",
              "safe",
              "secure_resilient",
              "accountable_transparent",
              "explainable_interpretable",
              "privacy_enhanced",
              "fair_with_harmful_bias_managed"
            ]
          }
        }
      }
    },
    "evidence_bundle": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "retention": { "type": "string", "enum": ["none", "redacted", "full"] },
        "transcript": {
          "type": "array",
          "description": "Conversation transcript for this case. May be redacted based on retention.",
          "items": { "$ref": "#/$defs/transcript_item" }
        },
        "tool_calls": {
          "type": "array",
          "items": { "$ref": "#/$defs/tool_call" }
        },
        "retrieved_documents": {
          "type": "array",
          "items": { "$ref": "#/$defs/retrieved_document" }
        },
        "detector_hits": {
          "type": "array",
          "items": { "$ref": "#/$defs/detector_hit" }
        },
        "redactions": {
          "type": "array",
          "items": { "$ref": "#/$defs/redaction" }
        }
      }
    },
    "transcript_item": {
      "type": "object",
      "additionalProperties": false,
      "required": ["turn", "role", "content"],
      "properties": {
        "turn": { "type": "integer", "minimum": 0 },
        "role": { "type": "string", "enum": ["system", "developer", "user", "assistant", "tool"] },
        "content": { "type": "string" },
        "content_hash": { "type": "string", "description": "Optional content hash if retention is limited." }
      }
    },
    "tool_call": {
      "type": "object",
      "additionalProperties": false,
      "required": ["name", "arguments", "result"],
      "properties": {
        "name": { "type": "string" },
        "arguments": { "type": "object" },
        "result": {
          "type": "object",
          "additionalProperties": true
        },
        "duration_ms": { "type": "integer", "minimum": 0 }
      }
    },
    "retrieved_document": {
      "type": "object",
      "additionalProperties": false,
      "required": ["source", "content"],
      "properties": {
        "source": { "type": "string" },
        "uri": { "type": "string", "format": "uri" },
        "content": { "type": "string" },
        "content_hash": { "type": "string" }
      }
    },
    "detector_hit": {
      "type": "object",
      "additionalProperties": false,
      "required": ["detector", "matched"],
      "properties": {
        "detector": { "type": "string" },
        "matched": { "type": "boolean" },
        "details": { "type": "object", "additionalProperties": true }
      }
    },
    "redaction": {
      "type": "object",
      "additionalProperties": false,
      "required": ["type", "count"],
      "properties": {
        "type": {
          "type": "string",
          "enum": ["pii", "secret", "policy", "custom"]
        },
        "count": { "type": "integer", "minimum": 0 }
      }
    },
    "replay_info": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "random_seed": { "type": "integer", "minimum": 0 },
        "pack_id": { "type": "string" },
        "pack_version": { "type": "string" },
        "pipeline_id": { "type": "string", "description": "Optional pipeline identifier if the case was generated dynamically." }
      }
    },
    "artifact": {
      "type": "object",
      "additionalProperties": false,
      "required": ["type", "uri"],
      "properties": {
        "type": {
          "type": "string",
          "enum": ["sarif", "junit", "html", "markdown", "csv", "other"]
        },
        "uri": { "type": "string", "format": "uri" },
        "description": { "type": "string" }
      }
    },
    "error": {
      "type": "object",
      "additionalProperties": false,
      "required": ["message"],
      "properties": {
        "message": { "type": "string" },
        "code": { "type": "string" },
        "stack": { "type": "string" },
        "case_id": { "type": "string" }
      }
    },
    "link": {
      "type": "object",
      "additionalProperties": false,
      "required": ["title", "uri"],
      "properties": {
        "title": { "type": "string" },
        "uri": { "type": "string", "format": "uri" }
      }
    },
    "lens": {
      "type": "string",
      "enum": ["Security", "Robustness", "Compliance", "Trust", "Privacy", "Reliability"]
    },
    "surface": {
      "type": "string",
      "enum": ["user_message", "retrieved_document", "tool_output", "memory"]
    },
    "difficulty": {
      "type": "string",
      "enum": ["easy", "moderate", "difficult"]
    },
    "owasp_llm_id": {
      "type": "string",
      "pattern": "^LLM\\d{2}$",
      "examples": ["LLM01", "LLM06"]
    },
    "atlas_id": {
      "type": "string",
      "description": "MITRE ATLAS technique ID. Pattern kept flexible because ATLAS IDs may evolve.",
      "pattern": "^(AML\\.)?T\\d{4,6}$",
      "examples": ["AML.T1020", "T1020"]
    }
  }
}
```

---

# Repo structure proposal (inside AgentEval) + packaging discussion

## Why multiple packages (recommended)
Red teaming has optional external dependencies (Python/Node runtimes, datasets, adapters).  
If everything ships in one package, you risk:
- heavy dependencies,
- accidental license contamination,
- confusing install experience,
- forcing Python/Node on users who only want native checks.

**Therefore:** ship a small set of focused NuGets so users can install only what they need.

## Recommended NuGet packages
1. **AgentEval.RedTeam.Abstractions**  
   Interfaces + shared models (report model types, taxonomy types)

2. **AgentEval.RedTeam.Engine**  
   Pipeline runner, scoring, built-in evaluators, canonical report writer

3. **AgentEval.RedTeam.Packs.Core** (permissive, authored by you)  
   Minimal high-value pack covering: prompt injection, indirect injection, PII/secret leakage, tool misuse patterns, safety category probes.

4. **AgentEval.RedTeam.Importers.\***  
   Importers that convert external configs → internal Attack Pack Spec:
   - `Importers.Promptfoo`
   - `Importers.DeepTeam`
   - `Importers.AtlasData`
   - `Importers.Owasp`

5. **AgentEval.RedTeam.Adapters.\***  
   Optional runner adapters that execute external tools and normalize results:
   - `Adapters.Garak`
   - `Adapters.PyRIT`
   - (optional) `Adapters.AzureAIEval`

6. **AgentEval.RedTeam.Cli**  
   `agenteval redteam run ...` runner, pack management, exports.

### Alternative: single package (why I don’t recommend it)
A single “everything included” package is appealing for demos, but in practice it:
- forces non-.NET runtime dependencies,
- complicates enterprise adoption,
- increases accidental license exposure.

If you still want it, ship it as an **optional meta-package** (depends on other NuGets) rather than the only distribution.

---

## Suggested repo layout

```
/src
  /AgentEval.Core
  /AgentEval.RedTeam.Abstractions
  /AgentEval.RedTeam.Engine
  /AgentEval.RedTeam.Packs.Core
  /AgentEval.RedTeam.Importers.AtlasData
  /AgentEval.RedTeam.Importers.Owasp
  /AgentEval.RedTeam.Importers.Promptfoo
  /AgentEval.RedTeam.Importers.DeepTeam
  /AgentEval.RedTeam.Adapters.Garak          (optional)
  /AgentEval.RedTeam.Adapters.PyRIT          (optional)
  /AgentEval.RedTeam.Adapters.AzureAIEval    (optional)
  /AgentEval.RedTeam.Cli

/packs
  /core                                  (permissive packs, shipped in repo)
  /benchmarks                            (pack definitions, no restricted payloads)
  /restricted                            (empty by default; docs + downloader instructions)

/schemas
  redteam-report.schema.json
  attack-pack.schema.json                (next milestone)

/docs
  redteam/
    overview.md
    golden-path.md
    licensing.md
    mappings/
      owasp-llm-top10.json
      mitre-atlas.json
      nist-airmf-facets.json

/.github
  workflows/
    build.yml
    test.yml
    redteam-ci.yml
```

---

## Licensing (distribution policy)
- Default NuGets should contain only **permissive** code + permissive pack content.
- Non-commercial / copyleft packs must be:
  - excluded by default,
  - distributed via downloader/import,
  - gated by explicit `--accept-license`.

---

**End of document.**
