# ADR-035: AEF 1.0 is AgentEval's evidence format

- **Status:** **Accepted (2026-10-08).** AEF is the 1.0 release candidate now, polished as it is implemented, and
  final at the `aef-1.0` tag, released together with AgentEval 1.0.
- **Owner:** the AgentEval maintainers (lead editor of AEF: [@joslat](https://github.com/joslat)).
- **Specification:** [`contracts/aef/`](../../contracts/aef/README.md). Normative text in
  [`contracts/aef/1/spec/`](../../contracts/aef/1/spec/01-introduction.md); start with the
  [primer](../../contracts/aef/1/primer.md).

## 1. Context

AgentEval produces results in two shapes that only AgentEval reads: the store v1 workspace (`.agenteval/`, the
`EvalResult` trees of `eval-result.schema.json`) and the flat `EvaluationReport` that exporters receive (ADR-034).
Neither lets anyone outside AgentEval check a result without trusting the tool that produced it:

- nothing shows that a run's files were not changed after it closed;
- a release decision ("may version X ship?") cannot be recomputed from the evidence it rests on;
- a runner that ran evaluations for someone else cannot be checked against what it was asked to do;
- "not measured" and "failed" collapse as soon as a result leaves the result model (ADR-034).

Mission Control 2.0 needs exactly these guarantees to govern releases, and third parties (CI systems, auditors,
other evaluation tools) need them to rely on AgentEval's results. The AgentEval Evidence Format (AEF) was written for
this in `contracts/aef/` and taken through four rounds of independent review (critic means 5.5 → 7.2 → 7.8 → 8.4 by
round 3), each round's findings reworked into the text, the schemas and the conformance corpus.

## 2. Decision

1. **AEF 1.0 is AgentEval's evidence format.** A run AgentEval writes for anyone else to read is an AEF run: a folder
   of I-JSON files (`run.json`, `results.ndjson`, `metrics.json`, `summary.json`, evidence, blobs, gates), sealed by
   an in-toto statement over its bytes, optionally signed (DSSE, ECDSA P-256 or Ed25519), extended after closing only
   by sealed overlay batches. Release decisions are AEF checkpoints whose decision a pure function recomputes from
   the sealed runs. Runners are checked against their plans by the event stream and the runs they return.
2. **The specification is the source of truth, not AgentEval's code.** `contracts/aef/` holds the normative text,
   the writer and reader schemas, a conformance corpus with expected results written independently of any
   implementation, and Python reference tools written from the text alone. GOVERNANCE.md sets editors, the change
   process (every change with its vectors, a DCO sign-off, a CI job), versioning and the release criteria.
3. **AgentEval.Results is AgentEval's implementation, and the second implementation every conformance class needs.**
   It is written from the text, never from the reference tools, and passes the corpus through the command-line
   contract of spec 09 §9.3 (`tests/AgentEval.Results.Conformance`): today every class but Runner, as reader,
   verifier and writer. Whatever implementing finds is ruled in the specification, never resolved silently in code
   (`strategy/MissionControl/AEF-Q4-39-findings.md` records 100+ such rulings).
4. **Release.** AEF 1.0 ships with AgentEval 1.0. Until the `aef-1.0` tag it may change freely: nobody depends on it
   yet, and every finding of implementation or review is fixed in the text. At the tag it freezes: afterwards a 1.x
   minor only adds ([VER-5]), errata are fixed in the next minor, and anything else is AEF 2.0. The tag requires:
   - a critic mean above 9.5 on the standing 12-dimension rubric;
   - for every conformance class, a second implementation passing its vectors, or the class released *at risk*;
   - the patent commitment for implementers in force (the Community Specification License 1.0 is drafted and waits
     on counsel; until then Apache-2.0 alone applies).
5. **Relation to ADR-034.** AEF is the export path for the result model that ADR-034 found missing. The store v1
   exporter (`agenteval aef export`) writes existing runs as imported AEF runs ([RUN-15]); native writing from the
   evaluation pipelines follows (PLAN S1 #5). `IResultExporter` stays the test pipeline's flat-report extension point.
6. **No standards body yet.** Standards organisations take a format once several independent parties implement or
   adopt it. Until then AEF lives in this repository under its own governance, and the next steps are concrete and
   cheap: register its two in-toto predicate types with the in-toto attestation project; propose the evaluation-event
   mapping to the OpenTelemetry GenAI semantic-conventions group; offer the published mappings to ASSERT, Inspect and
   EvalPort. When a second organisation implements or adopts AEF, its editor seat opens and AEF moves to a neutral
   home (for example LF AI & Data or OpenSSF); version 1 keeps its identifiers either way.

## 3. Consequences

**Positive**

- A result can be checked without trusting AgentEval: seal, signature, cross-file rules, lane results and the
  decision are all recomputable by anyone with the Python reference or their own implementation.
- Mission Control 2.0 builds on a published contract instead of AgentEval internals.
- Two implementations that disagreed on crafted inputs found real ambiguities before any user depended on them; the
  corpus pins each one with a vector, except a symbolic link under `overlays/`, which no portable corpus can hold.
- Imports and exports (ASSERT, Inspect, OpenAI Evals, EvalPort, OpenTelemetry, in-toto) have documented mappings.

**Negative**

- A second result format beside store v1 until the pipelines write AEF natively; the store v1 exporter bridges it.
- The format is strict (I-JSON, byte-exact seals, limits, closed enums): a producer has more to get right, which is
  the point, but writers must use AgentEval.Results or follow the spec closely.
- Keeping two implementations and a large corpus in step costs every change a vector and a ruling; the CI job and
  `check_spec.py` enforce it.

## 4. Alternatives considered

- **Keep store v1 as the only format.** Rejected: it cannot be verified by anyone but AgentEval, and it cannot carry a
  release decision others can recompute.
- **Adopt an existing format as is.** OpenTelemetry evaluation events carry one score per event and no seal, typed
  absences or decisions; in-toto attestations seal and sign but describe no evaluation; EvalPort, Inspect logs and
  OpenAI Evals logs lack typed absences, sealed evidence or recomputable decisions. AEF reuses them where they fit
  (OTLP/JSON traces and logs, in-toto statements, DSSE, `gen_ai.*` names) and maps to each.
- **Submit AEF to a standards body now.** Rejected for now: one implementer and no adopters is not what a standards
  body can take; the community steps above come first.
- **Freeze AEF today.** Rejected: implementing it has kept finding places the text did not settle; freezing before
  AgentEval 1.0 would turn each into a breaking change.
