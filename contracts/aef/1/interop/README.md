# AEF interoperability

These pages map AEF 1.0 to six neighbouring formats. They are informative: nothing here changes a rule of the
[specification](../spec/01-introduction.md). Each page names the version of the target it was written against.

| Page | Target | Version read (2026-10-08) |
|---|---|---|
| [OpenTelemetry](opentelemetry.md) | GenAI semantic conventions: the `gen_ai.evaluation.result` event, traces, `gen_ai.usage.*` | `open-telemetry/semantic-conventions-genai` at `06ec68e7` (no release yet); OTLP 1.11.0 |
| [Inspect AI](inspect.md) | Inspect eval logs (`.eval`, `.json`) | `inspect_ai` 0.3.277 |
| [OpenAI Evals](openai-evals.md) | The open-source `evals` recording format, and the hosted Evals API | `openai/evals` at `8eac7a7d`; OpenAI OpenAPI spec 2.3.0 |
| [EvalPort](evalport.md) | EvalPort suites and result sets | SPEC 1.0.0-rc.5 at `d0e90c48` |
| [ASSERT](assert.md) | Microsoft ASSERT's suite and run artifacts, and its harm and over-refusal headline | `assert-ai` 0.3.0 (tag `v0.3.0`, `main` at `e03aa809`) |
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
files ([§3.9](../spec/03-run.md#39-rules-across-files)) like any other run. Every "→ AEF" conversion on these pages
seals its run with `sealedBy: ingest` ([SEAL-5](../spec/04-integrity.md#41-sealing-a-run)), as
[§7.5](../spec/07-versioning.md#75-agenteval-store-v1) seals a run migrated from AgentEval's older store: the converter
is the run's `producer` ([RUN-15](../spec/03-run.md#32-runjson)), but it takes custody of what another tool wrote. The
seal shows that the converted files did not change after the conversion. It says nothing about the original record.

Some facts AEF requires are missing from most sources: the subject, `execution.targetMode`, a suite version. A
converter has to supply them, and lists each one in `run.json`'s `imported.asserted`
([RUN-15](../spec/03-run.md#32-runjson)), so a reader shows them as the converter's claims. "Supplied rather than read"
covers a constant the converter writes, a default it falls back on, and its interpretation of a recorded value (a
target URL read as the subject).

**Worked examples.** Every target page converts lines of
[`conformance/valid/completed-eval/run/results.ndjson`](../conformance/valid/completed-eval/run/results.ndjson), or
converts a target record into AEF. All examples were checked:

- every JSON block parses;
- every AEF line is valid against the writer schemas;
- the EvalPort example is valid against EvalPort's `schema/resultset.json`;
- the run converted from ASSERT's sample passes the reference verifier (`tools/aef_verify.py run`);
- every result id was recomputed with RES-4 against the corpus.

**Checked examples.** For OpenTelemetry and Inspect, [`examples/`](examples/) holds checked examples (round trips
through OpenTelemetry and Inspect, imports from both), and the worked examples of [opentelemetry.md](opentelemetry.md) and
[inspect.md](inspect.md) are their data.
`tools/aef_interop.py` is a reference converter written from those two pages alone; `tools/check_interop.py` reruns it
on every example and fails on any difference. Each folder holds the input (a corpus run, copied, or a hand-written
OTLP/JSON file), the output and an `expected.json` naming the direction and the sections it exercises:

| Example | Shows |
|---|---|
| [`aef-otel-aef`](examples/aef-otel-aef/) | `completed-eval` (a composite, typed absences, scores, a reasoning blob, a trace link) to events and back. What the trip keeps and loses is checked field by field: it loses exactly the page's "What does not carry over" list |
| [`aef-otel-aef-redteam`](examples/aef-otel-aef-redteam/) | `redteam-campaign` (`contentCapture: off`, attacks, a `scored` line, no times) to events and back, checked the same way |
| [`otel-aef`](examples/otel-aef/) | a hand-written OTLP file, the registry's own example included, as an imported run; one refused input for each event the page does not place |
| [`aef-inspect`](examples/aef-inspect/) | `completed-eval` to an Inspect eval log and back, with the same result ids; what the trip keeps and loses is checked field by field against the page's list |
| [`aef-inspect-trials`](examples/aef-inspect-trials/) | `running-trials` to Inspect and back: trials as epochs and back, a rollup as a reduction and back, a running run as `started` and back, unsealed |
| [`inspect-aef`](examples/inspect-aef/) | a hand-written Inspect log (two epochs, letters, a map, NaN, a refusal, a sample that failed, case content) as an imported run, with content kept and not; one refused input for each case the page refuses |

The examples are informative, like these pages: they are not conformance vectors, and nothing in the corpus depends on
them. Every run they hold passes `tools/aef_verify.py run`. Writing the converter found what the two pages left
undecided (OT-1 to OT-6, IN-1 to IN-10); settled on 10-09, each is now a rule or a stated refusal under the page's table
for its direction.

## What survives a round trip

AEF → target → AEF, for one result line of the corpus run.

| Through | Survives | Lost | Same `resultId` |
|---|---|---|---|
| [OpenTelemetry event](opentelemetry.md) | metric name, score, state (as the label, [RUN-14](../spec/03-run.md#310-traces)), reason (or the reasoning's text), span ids, end time, case id (as `test.case.name`), service name; checked by [`examples/aef-otel-aef`](examples/aef-otel-aef/) | `runId`, `path`, a score's own `label`, the result tree, thresholds, uncertainty, severity, evaluator, annotator, usage, metric declarations, the run header | no: the event carries neither the run id nor the path |
| [Inspect sample score](inspect.md) | case, trial (as epoch − 1), path (as the score key), score value or label, explanation, usage per role with cache and reasoning tokens, case times, captured case content; checked by [`examples/aef-inspect`](examples/aef-inspect/) | the state (Inspect has no verdict, and one unscored value, NaN, for every typed absence), composite lineage, evaluator and metric names, which line of a case held a usage entry, trace links, gates, seal, overlays | yes |
| [EvalPort result](evalport.md) | case, trial (as attempt − 1), path (as `grader_id`), pass or fail, score in [0, 1], reason, duration, end time, captured output | typed absence kinds, `warn` against `failed`, `inconclusive`, `scored`, `component.required`, `rulePath`, trial rollups, the seal | yes, when the root's path is kept in metadata |
| [OpenAI evals log](openai-evals.md) | case, pass or fail, score, end time | typed absences, the result tree, judges, the seal | only with the path kept in the event's `data` |
| [Hosted OpenAI Evals](openai-evals.md) | nothing of AEF's grading: the hosted API grades runs itself and accepts no outside results | everything except the case content, which it can re-grade | no |
| [in-toto](in-toto.md) | the seal, exactly: `seal.json` is an in-toto Statement | per-case results in a `test-result` statement keep only passed, warned and failed case ids | n/a |
| [ASSERT judge-only run](assert.md) | the captured conversation, and the case id through AgentEval's case map; ASSERT's judge then grades it again | AEF's own verdicts, scores, tree and seal: ASSERT accepts no outside verdicts | yes, for the lines a converter writes from ASSERT's verdicts (`<type>:<test_case_id>` mapped back to the case id) |

Values kept in the target's free-form metadata are not counted as surviving in this table, except where a row says so.

## Gaps found by these mappings

The research behind these pages found eight things AEF lacked. Seven are now part of AEF 1.0; the pages say where a
mapping still loses something.

| Id | What was missing | In AEF 1.0 |
|---|---|---|
| I1 | A state for "measured, no pass/fail rule"; categorical results; aggregates other than the mean | The `scored` state ([RES-1](../spec/03-run.md#341-states)) and summary verdict ([SUM-6](../spec/03-run.md#36-summaryjson)), a score's `label`, and a summary entry's `aggregate`: median, min and max recomputed, pass@k or F1 shown as the producer's ([SUM-8](../spec/03-run.md#36-summaryjson)) |
| I2 | A typed place for case content | Evidence kinds `input`, `expected`, `output`, `transcript`, all content under [RUN-11](../spec/03-run.md#32-runjson) |
| I3 | OpenTelemetry alignment | An optional `logs.otlp.jsonl` for OpenTelemetry events, `otel.schemaUrls`, and the label vocabulary: an exported event's label is the result's state name ([RUN-14](../spec/03-run.md#310-traces)) |
| I4 | Usage detail | `usage` is one entry per role and model (agent, judge, attacker), with cache-read, cache-write and reasoning tokens in OpenTelemetry's names ([RES-10](../spec/03-run.md#345-facts-about-a-result)) |
| I5 | Trial aggregations beyond `MajorityVote` | `AllPass`, `AnyPass`, `Mean`, `Median`, `Max`, `PassAtK` (with `k`) |
| I6 | Times per result | `startedAt` and `endedAt` on a result line |
| I7 | Facts a converter supplied | `imported` in `run.json`: the original tool and the fields the converter asserted ([RUN-15](../spec/03-run.md#32-runjson)) |
| I8 | An attestation bound to the evaluated artifact; registered media types | **Open.** [in-toto.md](in-toto.md) gives the companion statement's shape and the proposed media type names; registering them is an external process. |

Found while mapping the current format: a summary entry with no rule is now `scored`
([SUM-6](../spec/03-run.md#36-summaryjson)), and `summary.json` has the run's usage per party and model
([SUM-7](../spec/03-run.md#36-summaryjson)). Still open:

- **EvalPort `isolation` and `group`.** AEF has no field for a run's trial isolation or for membership in a group of
  sibling runs.
