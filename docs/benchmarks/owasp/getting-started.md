# OWASP LLM Top 10 Benchmark — Getting Started

> Status: beta. The OWASP LLM Top 10 benchmark ships as a red-team scanner with heuristic per-attack evaluators. Verdicts reflect dialog-observable behaviour only; they are not a substitute for code-level security review, threat modelling, or pen-testing.
>
> Coverage: all 10 OWASP LLM Top 10 v2.0 categories now have a dedicated attack at the agent-API layer (LLM01 Prompt Injection, LLM02 Sensitive Info Disclosure, LLM03 Supply Chain, LLM04 Data/Model Poisoning, LLM05 Improper Output Handling, LLM06 Excessive Agency, LLM07 System Prompt Leakage, LLM08 Vector/Embedding Weaknesses, LLM09 Misinformation, LLM10 Unbounded Consumption). LLM03/04/08/09 were added later than the other six. Verdicts remain dialog-observable only — a category whose evidence is weak (e.g. SupplyChain ships the in-context recommendation proxy, not the deeper `PackageHallucinationDetector`) reports `Inconclusive` rather than a fabricated pass.

## What this measures

The OWASP benchmark drives the agent under test with curated probes drawn from 14 attack types (PromptInjection, Jailbreak, IndirectInjection, EncodingEvasion, PIILeakage, InsecureOutput, ExcessiveAgency, SystemPromptExtraction, InferenceAPIAbuse, SupplyChain, DataPoisoning, VectorEmbedding, Misinformation, SkillInjection) and grades each response with the per-attack heuristic evaluator. The composite `EvalResult` aggregates the ten OWASP categories with a `MinAggregation` so any single category fail caps the verdict.

What IS tested: the agent's runtime dialog behaviour under adversarial prompts across all 10 categories — refusal quality, prompt-injection resistance, output-handling discipline, agency-bounding, system-prompt protection, unbounded-consumption guardrails, plus the later additions (supply-chain recommendation, data/model poisoning, vector/embedding retrieval boundary, and misinformation). What is NOT tested: the deeper, beyond-dialog assurance behind some categories — the model's training-data provenance, third-party plug-in supply-chain attestation, or vector-store internals. Where a category's evidence is only dialog-weak (e.g. SupplyChain's in-context recommendation proxy, or Misinformation's semantic confabulation), the leaf reports `Inconclusive` — never a fabricated pass.

## Coverage and evidence strength

All 10 OWASP LLM Top 10 v2.0 categories have a dedicated attack at the agent-API layer. They differ in how *structural* the evidence is — strong-signal dialog tests vs. categories where the deeper assurance lives upstream and the dialog probe is a proxy that defers to `Inconclusive` when it cannot be sure:

- Strong-signal dialog tests:
  - LLM01 Prompt Injection — direct + indirect + encoding-evasion probes; high-signal dialog test.
  - LLM02 Sensitive Information Disclosure — PII-leakage probes against system-prompt / context.
  - LLM05 Improper Output Handling — encoded-payload-in-output probes (the v2.0 rename of v1.0's LLM02 Insecure Output Handling).
  - LLM06 Excessive Agency — bounded-action probes (over-permissive tool use).
  - LLM07 System Prompt Leakage — extraction probes against system prompts and operator policy.
  - LLM10 Unbounded Consumption — inference-API-abuse probes (cost / token / loop runaway).
- Covered via a proxy or judge-deferred evidence (the later additions):
  - LLM03 Supply Chain — in-context dependency-recommendation probe; the deeper `PackageHallucinationDetector` with live registry lookups is opt-in via `--package-registry`. Reports `Inconclusive` rather than asserting the upstream attestation it cannot observe.
  - LLM04 Data and Model Poisoning — poisoned-context probes at the dialog boundary; training-corpus integrity itself remains an upstream-process obligation outside the agent-API layer.
  - LLM08 Vector and Embedding Weaknesses — exercises a real retrieval boundary via the `retrieve_context` canary tool, not just a verbal proxy; there are no deeper corpus-poisoning probe packs.
  - LLM09 Misinformation — adversarial false-premise probes; a confabulation the deterministic grader cannot decide is left to an LLM judge (`agenteval redteam --judge`); `bench owasp` has no judge option, so there it stays `Inconclusive`, since factual grading needs ground truth (see the `agentic` family's groundedness evaluators).

## Presets

Sourced verbatim from `BenchmarkFamilyRegistry` (see `src/AgentEval.RedTeam/RedTeam/Compliance/OwaspBenchmarkRegistration.cs:44-50`).

| Preset | Description (verbatim) | Cost tier | Typical scope | Approx. LLM cost |
|---|---|---|---|---|
| `top10` | All 14 built-in attacks at Quick intensity (default) | Medium | All 14 attacks, Quick intensity, 10-min timeout | no LLM (heuristic evaluators) |
| `smoke` | 3 MVP attacks (PromptInjection + Jailbreak + PIILeakage) — CI-friendly | Low | 3 attacks, Quick intensity, 10-min timeout | no LLM |
| `audit` | All 14 attacks at Comprehensive intensity — audit-grade evidence | High | All 14 attacks, Comprehensive intensity, 30-min timeout | no LLM |
| `top10-rag` | All 14 attacks at Comprehensive intensity, 20-min timeout — RAG-vector depth (LLM01 indirect-injection + LLM08 vector-embedding emphasis) | High | All 14 attacks, Comprehensive intensity, 20-min timeout, RAG-tuned probe selection | no LLM |

The current OWASP attack pipeline uses heuristic per-attack evaluators (see `src/AgentEval.RedTeam/RedTeam/Evaluators/`), not an LLM judge. Every run still resolves a judge, with or without `--azure-from-env`, for API symmetry with the other bench commands: the `AZURE_OPENAI_JUDGE_*` override if set, otherwise the provider `AI_INFERENCE_PROVIDER` selects; with no provider configured it needs `AGENTEVAL_ALLOW_STUB_JUDGE=1` or the command exits 3 (see [CLI Reference — Environment variables](../../cli.md#environment-variables)). The judge is not called during the scan and consumes no tokens. The dominant cost is the agent-under-test's per-probe inference calls — usually a few dozen calls for `smoke`, a few hundred for `top10`, and ~thousand+ for `audit`/`top10-rag`.

## CLI usage

```bash
# Real model from the configured inference provider
agenteval bench owasp --preset top10 --subject MyAgent --azure-from-env

# Smoke (CI-friendly)
agenteval bench owasp --preset smoke --subject MyAgent --azure-from-env

# Audit-grade
agenteval bench owasp --preset audit --subject MyAgent --azure-from-env

# RAG-focused
agenteval bench owasp --preset top10-rag --subject MyAgent --azure-from-env
```

The `--input` flag is accepted for provenance but the OWASP pipeline generates its own probes — `--input` is recorded in the run manifest, not consumed by the attacks.

`--azure-from-env` builds the agent from whichever provider `AI_INFERENCE_PROVIDER` selects — Azure OpenAI, Bitdeer, OpenAI, Azure AI Foundry or any OpenAI-compatible host; see the [provider table](../../cli.md#ai_inference_provider--which-provider-the-cli-talks-to) for the variables each one needs. Despite its name, the flag is not Azure-only: with the selector unset, the first fully configured provider in that table's order is used, so an environment with only the `AZURE_OPENAI_*` trio still gets Azure OpenAI. If no provider is configured, the command fails and names what is missing. `--endpoint <url> --model <name>` targets any OpenAI-compatible endpoint directly instead. Without a target the command refuses (exit 2). `--sut mock` runs a built-in stand-in instead: the run says MOCK, exits 11 whatever it scores, and nothing is written to `.agenteval/`, because it measures no agent.

## Attack → OWASP category mapping

The full mapping lives on `IAttackType.OwaspLlmId` per attack class; the table below summarises the current roster.

| Attack class | OWASP category | Default severity |
|---|---|---|
| `PromptInjectionAttack` | LLM01 Prompt Injection | High |
| `JailbreakAttack` | LLM01 Prompt Injection | High |
| `IndirectInjectionAttack` | LLM01 Prompt Injection (RAG-style) | High |
| `EncodingEvasionAttack` | LLM01 Prompt Injection (encoded payload) | Medium |
| `PIILeakageAttack` | LLM02 Sensitive Information Disclosure | High |
| `InsecureOutputAttack` | LLM05 Improper Output Handling | High |
| `ExcessiveAgencyAttack` | LLM06 Excessive Agency | High |
| `SystemPromptExtractionAttack` | LLM07 System Prompt Leakage | High |
| `InferenceAPIAbuseAttack` | LLM10 Unbounded Consumption | Medium |
| `SupplyChainAttack` | LLM03 Supply Chain | High |
| `DataPoisoningAttack` | LLM04 Data and Model Poisoning | High |
| `VectorEmbeddingAttack` | LLM08 Vector and Embedding Weaknesses | Medium |
| `MisinformationAttack` | LLM09 Misinformation | Medium |

Source of truth: `src/AgentEval.RedTeam/RedTeam/Attacks/*.cs` (each attack class declares its `OwaspLlmId`). All 10 categories now have at least one attack; a category whose probes yield only dialog-weak evidence reports `Inconclusive` (not a fabricated `pass`), and a category with no probes selected in a given preset still appears as an honest `skipped` leaf.

## Output

Each run writes to `.agenteval/compliance/OWASP-LLM-Top10/{subject}/{timestamp}/` and to the canonical run dir under `.agenteval/subjects/agents/{subject}/runs/{runId}/`:

- `report.json` — canonical eval-result shape (10-leaf composite, one leaf per OWASP category).
- `report.md` — human-readable markdown summary (PR-friendly).
- `report.html` — HTML report, rendered by `GenericReportRenderer`.
- `report.pdf` — PDF report, generated via `AgentEval.Rendering.Pdf` / QuestPDF.
- Plus the rich `OWASPComplianceReport` JSON written via `OWASPComplianceReporter.SaveReportAsync` in the canonical run dir for downstream evidence packs.

PDF and HTML emission is best-effort with warning-fallback — failures do not abort the run.

## Interpreting results

The composite `EvalResult` uses `MinAggregation` over the 10 OWASP-category leaves — any single category fail caps the overall verdict. Per-leaf score interpretation:

| Score band | Label | Severity | Meaning |
|---|---|---|---|
| `>= 0.8` | `pass` | none | All probes in the category were resisted |
| `>= 0.5` | `warn` | low / medium | At least one probe partially landed; review per-probe evidence |
| `< 0.5` | `fail` | high / critical | Probes landed reliably; treat as exploit-class regression |
| (no probes run) | `skipped` | none | Category appears in the tree for completeness; no signal |

The CLI exit code mirrors the composite verdict: `pass` → exit 0, `fail` → exit 9, `warn` → exit 10, `skipped` → exit 11 (see [CLI Reference — Exit codes](../../cli.md#exit-codes)).

## How to act on findings

- LLM01 Prompt Injection failures — review the agent's system-prompt scaffolding and any retrieval / tool-output sanitisation. The `IndirectInjection` failures specifically point at retrieved-content handling.
- LLM02 Sensitive Information Disclosure failures — audit the agent's context (what data is in its prompt, retrieved docs, tool outputs) and tighten redaction at the boundary.
- LLM05 Improper Output Handling failures — check downstream consumers of the agent's output for unsafe rendering (HTML / SQL / shell injection); the agent itself may need output-encoding policy.
- LLM06 Excessive Agency failures — narrow tool surface, add per-tool authorisation prompts, or require explicit confirmation before destructive actions.
- LLM07 System Prompt Leakage failures — the system prompt is leaking. Either accept that and design accordingly, or harden the refusal policy with extraction-specific probes.
- LLM10 Unbounded Consumption failures — add per-call token / cost caps and retry-loop bounding upstream of the agent invocation.

## When to use this benchmark

- You ship an LLM-powered agent and need a first-line red-team screening pass before exposing it to untrusted user input or untrusted retrieved content.
- You need CI-friendly fast feedback on prompt-injection / jailbreak / PII-leakage regressions (use `smoke`).
- You are preparing a security review for an audit-grade evidence pack (use `audit`).
- Your agent ingests retrieved-document context and you want depth on indirect-injection / RAG-vector probes (use `top10-rag`).
- You want comparable runs over time to track resistance regressions across deployments.

When NOT to use:
- For LLM03 (Supply Chain) or LLM04 (Data/Model Poisoning) attestation — those are upstream-process obligations, not dialog-testable.
- For LLM09 (Misinformation) factual-grounding grading — see the `agentic` family's groundedness evaluators instead.
- As a substitute for code-level security review, threat modelling, or pen-testing of the surrounding infrastructure (auth, sandboxing, network policy, etc.).

## Programmatic use

The CLI is the supported path for audit-grade evidence emission, but the underlying `OwaspBenchmark` factory + `OwaspBenchmarkRun` runner are public and usable from C# directly. Minimal example:

```csharp
using AgentEval.Benchmarks;
using AgentEval.Core;

// Build a preset (judge is currently advisory — heuristic evaluators do the grading).
var run = OwaspBenchmark.Top10(judge: null);

// Run against any IEvaluableAgent.
var redTeamResult = await run.ScanAsync(myAgent);

// Project into the unified EvalResult shape (10-leaf composite).
var compositeEval = run.BuildEvalResult(redTeamResult);

// Project into the rich OWASP compliance report for evidence packs.
var report = run.GenerateReport(redTeamResult);
Console.WriteLine(report.ToJson());
Console.WriteLine(report.ToMarkdown());
```

For Mission Control rendering or programmatic post-processing, prefer the `EvalResult` shape; for compliance evidence packs prefer the rich `OWASPComplianceReport`. Both derive from a single `ScanAsync` execution — there is no double-scan cost.

## Comparing across runs / baselines

The OWASP benchmark does not currently ship a CLI-level baseline-comparison command. Compare runs via:

- `agenteval doctor` — validates the audit chain on every `evidence.json` in the workspace; flags hash mismatches indicating evidence tampering or stale snapshots.
- Mission Control — renders runs from `.agenteval/subjects/agents/{subject}/runs/` with per-leaf detail; visual diff across runs by selecting two runs.
- Direct file diff — runs are stored canonically under `.agenteval/subjects/agents/{subject}/runs/{runId}/`; `git diff` on `report.json` between runs surfaces per-category score changes.

For per-attack baseline + regression detection, `AgentEval.RedTeam` ships `RedTeamBaseline` / `RedTeamBaselineComparer` — see `src/AgentEval.RedTeam/RedTeam/Baseline/` for the programmatic surface (not yet exposed via CLI for OWASP / MITRE specifically).

## Limitations

Known limitations:
- All 10 OWASP categories have a dedicated attack, but LLM03/04/08/09 lean on proxy or judge-deferred evidence, so they frequently report `Inconclusive` rather than a confident `pass` — read those leaves with their evidence caveat, not as silent passes.
- In `bench owasp`, per-attack heuristic (keyword/structural) evaluators do all of the grading; the judge the command resolves is not called. Judge-primary grading is available through `agenteval redteam --judge` — see [redteam-whats-new.md](../../redteam-whats-new.md).
- LLM08 (Vector / Embedding Weaknesses) exercises a real retrieval boundary via the `retrieve_context` canary; there is no deeper retrieval-corpus-poisoning probe pack.
- The built-in attack roster (14 attacks) is fixed; custom attack-type injection beyond the built-in roster plus `--import-probes` dataset packs is not yet exposed via CLI.
- The CLI can scan a plain chat model (`--azure-from-env` with any configured provider, or `--endpoint`/`--model` for an OpenAI-compatible endpoint) or the built-in `--sut` targets. There is no option that loads an agent from a manifest file. An agent with its own tools, memory or a non-chat interface is scanned from a small program that wraps it as an `IEvaluableAgent` — see `samples/AgentEval.Samples/Benchmarks/06_OwaspBenchmark.cs` and [Programmatic use](#programmatic-use).

See also:
- [GDPR getting-started](../gdpr/getting-started.md) — for dialog-based compliance benchmarking.
- [EU AI Act getting-started](../eu-ai-act/getting-started.md) — for AI-Act dialog screening.
- [MITRE ATLAS getting-started](../mitre/getting-started.md) — sister red-team family; same attack pipeline tagged against ATLAS techniques.
- `src/AgentEval.RedTeam/RedTeam/Compliance/OwaspBenchmark.cs` — preset factory source.
- `src/AgentEval.RedTeam/RedTeam/Reporting/Compliance/OWASPComplianceReporter.cs` — compliance-evidence reporter source.
