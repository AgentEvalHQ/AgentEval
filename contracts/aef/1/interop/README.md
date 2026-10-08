# AEF interoperability

These pages map AEF 1.0 to five neighbouring formats. They are informative: nothing here changes a rule of the
[specification](../spec/01-introduction.md). Each page names the version of the target it was written against.

| Page | Target | Version read (2026-10-08) |
|---|---|---|
| [OpenTelemetry](opentelemetry.md) | GenAI semantic conventions: the `gen_ai.evaluation.result` event, traces, `gen_ai.usage.*` | `open-telemetry/semantic-conventions-genai` at `06ec68e7` (no release yet); OTLP 1.11.0 |
| [Inspect AI](inspect.md) | Inspect eval logs (`.eval`, `.json`) | `inspect_ai` 0.3.277 |
| [OpenAI Evals](openai-evals.md) | The open-source `evals` recording format, and the hosted Evals API | `openai/evals` at `8eac7a7d`; OpenAI OpenAPI spec 2.3.0 |
| [EvalPort](evalport.md) | EvalPort suites and result sets | SPEC 1.0.0-rc.5 at `d0e90c48` |
| [in-toto, DSSE, SLSA](in-toto.md) | in-toto Statements, DSSE envelopes, SLSA VSA, media types; the specifications of AEF's two predicate types | in-toto attestation spec v1.2; DSSE 1.0.2; SLSA 1.2 |

## What "maps" means

Each target page has two tables: **AEF → target** (what an exporter writes) and **target → AEF** (what an importer
writes). The last column of each row is one of:

| Word | Meaning |
|---|---|
| exact | The value and its meaning survive. Converting back gives the same AEF value. |
| lossy | Part of it survives. The row says what is lost. |
| none | The other side has no place for it. |

A value kept in AEF's `ext` ([ENC-19](../spec/02-encoding.md#27-the-extension-point)) or in a target's free-form
metadata survives as data, but no reader of that format interprets it. The tables count that as **none**, and say
where the value can be kept.

**Result ids.** AEF computes each `resultId` from `runId`, `caseId`, `path` and `trial`
([RES-4](../spec/03-run.md#342-result-ids)). A converter that keeps those four values gets the same ids back. Each page
says which target fields hold them.

**A converted run is a new run.** A run built from another format must meet the writer schemas and the rules across
files ([§3.9](../spec/03-run.md#39-rules-across-files)) like any other run. Its seal
([SEAL-5](../spec/04-integrity.md#41-sealing-a-run)) shows that the converted files did not change after the
conversion. It says nothing about the original record. [§7.5](../spec/07-versioning.md#75-agenteval-store-v1) treats a
run migrated from AgentEval's older store the same way, sealed with `sealedBy: ingest`. Some facts AEF requires are
missing from most sources: the subject, `execution.targetMode`, a suite version. A converter has to supply them, and
AEF has no marker yet for a value a converter supplied ([I7](#open-gaps)).

**Worked examples.** Every target page converts lines of
[`conformance/valid/completed-eval/run/results.ndjson`](../conformance/valid/completed-eval/run/results.ndjson), or
converts a target record into AEF. All examples were checked:

- every JSON block parses;
- every AEF line is valid against the writer schemas;
- the EvalPort example is valid against EvalPort's `schema/resultset.json`;
- every result id was recomputed with RES-4 against the corpus.

## What survives a round trip

AEF → target → AEF, for one result line of the corpus run.

| Through | Survives | Lost | Same `resultId` |
|---|---|---|---|
| [OpenTelemetry event](opentelemetry.md) | metric name, score, state (as the label), reason, span ids, case id (as `test.case.name`) | `runId`, `path`, the result tree, thresholds, uncertainty, severity, evaluator, annotator, metric declarations | no: the event carries neither the run id nor the path |
| [Inspect sample score](inspect.md) | case, trial (as epoch − 1), path (as the score key), score value, explanation, usage | the kind of typed absence (Inspect has one unscored value, NaN), composite lineage, gates, seal, overlays | yes |
| [EvalPort result](evalport.md) | case, trial (as attempt − 1), path (as `grader_id`), pass or fail, score in [0, 1], reason, duration | typed absence kinds, `warn` against `failed`, `inconclusive`, `component.required`, `rulePath`, trial rollups, the seal | yes, when the root's path is kept in metadata |
| [OpenAI evals log](openai-evals.md) | case, pass or fail, score | typed absences, the result tree, judges, the seal | only with the path kept in the event's `data` |
| [Hosted OpenAI Evals](openai-evals.md) | nothing of AEF's grading: the hosted API grades runs itself and accepts no outside results | everything except the case content, which it can re-grade | no |
| [in-toto](in-toto.md) | the seal, exactly: `seal.json` is an in-toto Statement | per-case results in a `test-result` statement keep only passed, warned and failed case ids | n/a |

Values kept in the target's free-form metadata are not counted as surviving in this table, except where a row says so.

## Open gaps

Where AEF lacks something a mapping needs, the pages link one of these findings. Each is tracked; none is decided.

| Id | What AEF lacks | Pages |
|---|---|---|
| I1 | A state for "measured, no pass/fail rule". Scores are numbers only, so categorical results have no field (OTel labels, Inspect `C`/`I`/`P`/`N`, OpenAI label graders). Summary values are recomputed as a mean or a sum ([SUM-5](../spec/03-run.md#36-summaryjson)), so other aggregates (F1, pass@k, median, bootstrap figures) have no field outside `ext`. | all |
| I2 | A typed place for case content: input, expected output, output, transcript. Evidence `kind` has no such values, and with `contentCapture: off` nothing is kept ([RUN-11](../spec/03-run.md#32-runjson)). | Inspect, OpenAI Evals, EvalPort |
| I3 | OpenTelemetry alignment: no run file for OTLP logs, where evaluation events live; `otel.semconvVersion` cannot name the GenAI registry, whose schema URL is now separate; no published vocabulary for `gen_ai.evaluation.score.label`. | OpenTelemetry |
| I4 | Usage detail: only input and output tokens, one `role` per line; no cache-read, cache-write or reasoning tokens; no run-level usage per model. | OpenTelemetry, Inspect, OpenAI Evals |
| I5 | Trial aggregation: `trials.aggregation` and `executionPolicy.aggregation` allow only `MajorityVote`. | Inspect |
| I6 | Times per result: a result line has `durationMs` but no start or end time. | OpenTelemetry, Inspect, OpenAI Evals, EvalPort |
| I7 | A marker for facts a converter supplied that the original producer did not record (subject, target mode, a suite version computed from content). | Inspect, OpenAI Evals, EvalPort |
| I8 | An attestation bound to the evaluated artifact. The seal's subjects are the run's files, so policy engines that look attestations up by the artifact's digest do not find AEF evidence. AEF's media types are unregistered. | in-toto |
