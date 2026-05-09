# AgentEval GDPR Benchmark

A behavioral GDPR compliance benchmark for AI agents. The sample demonstrates
how to compose `AtomicLlmEval` and `CompositeEval` into a 5-pillar, 21-article
evaluation suite, generate PDF and markdown reports, and integrate the results
with the canonical `IOutputStore` audit chain.

> **Disclaimer**: This benchmark evaluates an AI agent's dialog behavior against
> GDPR articles. It is a first-line screening tool, not a legal compliance
> attestation. A passing score does not mean the system is legally GDPR-compliant.
> Legal compliance depends on many factors outside the scope of any automated
> dialog benchmark. Consult a qualified Data Protection Officer and legal counsel
> before making compliance representations to regulators, customers, or partners.

## Quick Start

```
dotnet run --project src/AgentEval.Cli --framework net10.0 -- bench gdpr --preset smoke --subject TravelAgent
```

Without `AZURE_OPENAI_*` environment variables set, the CLI uses a stub judge
and prints a warning. Results from the stub are not meaningful for compliance
purposes.

## Project Layout

- `Articles/Yaml/` — 21 article scenario YAML files (one per GDPR article covered).
- `DomainPacks/` — three composable domain packs: `Healthcare/`, `HR/`, and `ChildrensService/`.

The calibration golden dataset (120 hand-labeled entries) lives in the test
project (`tests/AgentEval.Tests/GdprBenchmark/`), not in this sample.

## Further Reading

- [Full getting-started guide](../../docs/gdpr-benchmark/getting-started.md) —
  presets, output structure, result interpretation, custom scenarios, calibration,
  and audit chain.
- [Composite Evaluations](../../docs/composite-evals.md) — the underlying
  `CompositeEval` / `AtomicLlmEval` primitives that power this benchmark.
