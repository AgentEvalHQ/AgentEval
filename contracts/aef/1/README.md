# AEF 1.0

**The AgentEval Evidence Format, version 1.0.** Status: **draft for 1.0**, unreleased; it may still change.

AEF lets someone other than the tool that made an evaluation claim check it: that a run's evidence is unchanged and
whose it is, that a release decision follows from that evidence, and that a runner kept to its plan. Start with the
[primer](primer.md) for a tour, then the specification.

## Specification (normative)

| | Document | Contents |
|---|---|---|
| 1 | [Introduction](spec/01-introduction.md) | Purpose, audience, scope and non-goals, conventions, terminology, conformance classes |
| 2 | [Encoding](spec/02-encoding.md) | JSON (I-JSON) and NDJSON, values and times, identifiers, portable patterns, limits, `ext` |
| 3 | [The run](spec/03-run.md) | The run folder, `run.json`, results, metrics, the summary, evidence, gates, the rules across files |
| 4 | [Integrity](spec/04-integrity.md) | Sealing, overlays and their effective view, redaction, signatures and trust policies, verification outcomes |
| 5 | [Checkpoints](spec/05-checkpoints.md) | Checkpoints, lane evaluation, the decision function and its exceptions, verifying a checkpoint |
| 6 | [Runners](spec/06-runners.md) | Run plans, runner manifests, matching, the event stream, plan conformance |
| 7 | [Versioning](spec/07-versioning.md) | Writers and readers, minor and major versions, unknown values, deprecation, AgentEval store v1 |
| 8 | [Security and privacy](spec/08-security.md) | Threat model, what each mechanism protects, secrets, privacy |
| 9 | [Conformance](spec/09-conformance.md) | Classes, the corpus, running it, claiming conformance |

The schemas (`schemas/writer/`, `schemas/reader/`) and the conformance corpus (`conformance/`) are part of the
specification.

## Also here (informative)

| | |
|---|---|
| [Primer](primer.md) | AEF in one walk-through: a run from start to verified checkpoint |
| [Field reference](reference/) | Every field of every schema, generated from the schemas |
| [Interoperability](interop/) | Mappings to OpenTelemetry GenAI evaluation events, Inspect, OpenAI Evals, EvalPort and ASSERT; the in-toto predicate types and media types |
| [Rationale and FAQ](rationale.md) | Why AEF is the way it is |
| [Changelog](CHANGELOG.md) | What changed, draft by draft |

## Layout

| Folder | Contents |
|---|---|
| `spec/` | The normative text |
| `schemas/writer/` | What a producer writes (strict) |
| `schemas/reader/` | What a reader accepts (tolerant; derived from the writer schemas) |
| `conformance/` | The corpus: `index.json` lists every vector with its kind, classes and rules |
| `../tools/` | Reference tools in Python, no dependencies: generators, the verifier, the conformance runner |
