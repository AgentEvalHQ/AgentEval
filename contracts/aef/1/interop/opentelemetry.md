# OpenTelemetry

**Version read:** `open-telemetry/semantic-conventions-genai` at commit `06ec68e722c45a7218e23ea1bc1339fe4e21ecae`
(2026-10-07). That repository has no release yet. Its schema URL is
`https://opentelemetry.io/schemas/gen-ai-dev/1.42.0-dev`, and it builds on the core semantic conventions v1.44.0. The
GenAI conventions moved out of the core repository in core v1.42.0 (2026-06-12). The evaluation event first appeared
in core v1.38.0 (2025-10-29). OTLP specification 1.11.0. Everything on this page has Development stability and can
still change.

## The target

### The evaluation event

An evaluation result is an **event**: an OTLP log record with `eventName` set to `gen_ai.evaluation.result`. Its
requirement level is Recommended. The convention says: "This event SHOULD be parented to GenAI operation span being
evaluated when possible or set `gen_ai.response.id` when span id is not available." A parented event carries that
span's `traceId` and `spanId`. No body is defined.

| Attribute | Type | Requirement | Meaning |
|---|---|---|---|
| `gen_ai.evaluation.name` | string | Required | the name of the evaluation metric, e.g. `Relevance` |
| `gen_ai.evaluation.score.value` | double | Conditionally Required, "if applicable" | the score the evaluator returned |
| `gen_ai.evaluation.score.label` | string | Conditionally Required, "if applicable" | a human-readable, low-cardinality label, e.g. `pass`, `fail`, `relevant`. "Implementations SHOULD document the possible values." |
| `gen_ai.evaluation.explanation` | string | Recommended | the evaluator's free-form explanation |
| `gen_ai.response.id` | string | Recommended when available | the completion evaluated, when no span id is available |
| `error.type` | string | Conditionally Required, if the evaluation ended in an error | a low-cardinality error class; `_OTHER` as fallback |

The repository's own reference scenarios (`reference/scenarios/azure-ai-evaluation`, `deepeval`) emit the event inside
an evaluation span, so in practice the parent is often the evaluation's own span.

Open proposals that touch the mapping:

| Item | Proposes | State on 2026-10-08 |
|---|---|---|
| PR #359 | `gen_ai.evaluation.evaluator.id`, `.version`, `.type` (`llm_judge`, `deterministic`, `human`), `gen_ai.evaluation.reference_set.id` | open |
| #79, split into #422 | `test.suite.run.id`, `test.case.id` | open |
| #39 | `gen_ai.evaluation.score.range` | open |
| #43 | a confidence interval on the event | open |
| #33 | evaluation spans beside the events | open |
| #470 | an evidence reference (URI, hash, media type) on the event | closed as not planned |

The core registry already has `test.case.name`, `test.suite.name`, `test.case.result.status` (`pass`, `fail`) and
`test.suite.run.status` (core v1.44.0, Development).

### Traces and usage

GenAI spans carry token counts. On inference and agent spans `gen_ai.usage.input_tokens` and
`gen_ai.usage.output_tokens` are Recommended. Finer counts are subsets of those two:
`gen_ai.usage.cache_read.input_tokens`, `gen_ai.usage.cache_write.input_tokens`,
`gen_ai.usage.reasoning.output_tokens`, and per-modality counts. Prompt and completion content lives in
`gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`, `gen_ai.tool.call.arguments` and
`gen_ai.tool.call.result`.

In OTLP/JSON, trace and span ids are hex strings, 64-bit integers are decimal strings, and keys are lowerCamelCase.
OpenTelemetry's file exporter writes one JSON object per line and one signal per file: traces, metrics or logs.

## How AEF holds OpenTelemetry data

| AEF | Rule | What it holds |
|---|---|---|
| `traces.otlp.jsonl` | [RUN-14](../spec/03-run.md#310-traces) | OTLP/JSON traces, one `TracesData` object per line, sealed like every file ([SEAL-2](../spec/04-integrity.md#41-sealing-a-run)) |
| `logs.otlp.jsonl` | [RUN-2](../spec/03-run.md#31-a-run-is-one-folder), RUN-14 | OTLP/JSON `LogsData` objects, one per line: events such as `gen_ai.evaluation.result` |
| `otel.schemaUrls` in `run.json` | RUN-14 | the OpenTelemetry schema URLs the traces and logs follow, e.g. `https://opentelemetry.io/schemas/gen-ai-dev/1.42.0-dev` |
| `otel.semconvVersion`, `otel.dialects` | RUN-14 | the core convention version and the namespaces used |
| `traceLink` on a result | [RES-10](../spec/03-run.md#345-facts-about-a-result) | the span of the operation the result evaluates (or its trace, with `traceId` only): the span OpenTelemetry parents an evaluation event to; checked against `traces.otlp.jsonl` (`trace-link`, [§3.9](../spec/03-run.md#39-rules-across-files)) |
| evidence with a span link | [EVD-1, EVD-2](../spec/03-run.md#37-evidencendjson-and-blobs) | any other span, such as a judge's own call; a span link has no digest |
| `usage` on a result | RES-10 | one entry per role and model (`role`: `agent`, `judge`, `attacker`, `other`), in OpenTelemetry's names: `gen_ai.usage.input_tokens`, `output_tokens`, `cache_read.input_tokens`, `cache_write.input_tokens`, `reasoning.output_tokens`; and `costUsd`, `costSource` |
| `subject.telemetry` in `run.json` | [RUN-6](../spec/03-run.md#32-runjson) | `agentId` and `serviceName`, which join the subject to `gen_ai.agent.id` and `service.name` |
| `contentCapture: off` | [RUN-11](../spec/03-run.md#32-runjson), [§8.4](../spec/08-security.md#84-privacy) | traces carry none of the content attributes listed above (checked as `content-capture`) |

**Label vocabulary.** When an AEF producer writes a `gen_ai.evaluation.result` event for a result,
`gen_ai.evaluation.score.label` is the result's `state` name (RUN-14). The values are `passed`, `failed`, `warn`,
`inconclusive`, `scored`, `not_measured`, `not_applicable`, `skipped`, `error` and `pending`
([RES-1](../spec/03-run.md#341-states)).

In the corpus run, `traces.otlp.jsonl` holds two spans. `invoke_agent support-triage` is the agent's invocation that
`triage/helpfulness` evaluates, and the line's `traceLink` names it. `chat gpt-5.1` is the judge's call, cited as
evidence `E-1`.

## AEF → OpenTelemetry

An exporter writes one event per score of a result line, and one event without `gen_ai.evaluation.score.value` for a
line without scores. The events go to an OTLP logs endpoint, or into the run's own `logs.otlp.jsonl` when the producer
writes them before the run closes. The rules below the table cover what the table does not; the reference converter,
`tools/aef_interop.py to-otel`, follows both.

**Values, not bytes** (settled 2026-10-09). This page, in both directions, fixes values, not bytes: the order of
members and of attributes, and the spelling of numbers, are free ([ENC-2](../spec/02-encoding.md#21-json-documents),
[ENC-4](../spec/02-encoding.md#21-json-documents)). A checker compares JSON values, as `tools/check_interop.py` does.
The worked example writes the attributes in the table's order.

| AEF | OpenTelemetry | Fidelity |
|---|---|---|
| `traceLink.traceId`, `traceLink.spanId` | log record `traceId`, `spanId` | exact: both name the evaluated operation |
| `traceLink` with `traceId` only | log record `traceId`, no `spanId` | exact |
| `scores[].metric` | `gen_ai.evaluation.name` | exact |
| `scores[].value` | `gen_ai.evaluation.score.value` | exact |
| `state` | `gen_ai.evaluation.score.label` (RUN-14) | exact, for a reader that knows the vocabulary |
| `scores[].label` | none: the event has one label, and it holds the state | none |
| `scores[].normalized` | none | none |
| `reason` | `gen_ai.evaluation.explanation` | exact |
| `reasoning` (a blob; `contentCapture: on`) | `gen_ai.evaluation.explanation`, when the line has no `reason` | lossy: the text survives, the blob's digest does not |
| `state: error` | `error.type: _OTHER`, beside the label `error` | lossy: AEF records no error class |
| `endedAt` (or `startedAt`) | `timeUnixNano` | exact |
| `caseId` | `test.case.name` (core registry) | exact |
| `path` | none | none |
| `runId` | none (`test.suite.run.id` is proposed) | none |
| `trial`, `trials` | none | none |
| `evaluator.id`, `evaluator.version` | none (`gen_ai.evaluation.evaluator.id`, `.version` are proposed in PR #359) | none |
| `annotator.kind` | none (`gen_ai.evaluation.evaluator.type` is proposed; it has no value for `HYBRID` or `OTHER`) | none |
| `annotator.model`, `promptHash`, `rubricDigest`, `panel` | none | none |
| `verdictRule`, `run.json` `config.thresholds` | none | none |
| `uncertainty` | none | none |
| `parentResultId`, `component`, `aggregation` | none | none |
| `severity`, `durationMs`, `turns`, `attack`, `lane` | none | none |
| `evidence` | none | none |
| `usage` entries | the attributes of the same names on the span of the party that consumed them | exact; in the corpus the judge's entry matches its `chat gpt-5.1` span |
| `run.json` `subject.telemetry.serviceName` | resource attribute `service.name` | exact |
| `traces.otlp.jsonl`, `logs.otlp.jsonl` | OTLP traces and logs, sent as they are | exact |

**Beyond the table** (settled 2026-10-09; the ids are those of the former "Still open" items):

- **The event of a line without scores** (OT-1). `gen_ai.evaluation.name` is Required, and no field of a line without
  scores names a metric (a typed absence carries no scores, [RES-2](../spec/03-run.md#341-states)). The converter
  names the event after the metric of the summary entries at the line's path, in the lanes the line belongs to
  ([SUM-3](../spec/03-run.md#36-summaryjson)); a trial line belongs to its lane like any line. It refuses a
  line for which they name no metric or more than one (a run with no `summary.json`, such as a running one), and the
  refusal refuses the whole export: nothing is written, and no line is skipped.
- **The envelope** (OT-2). The converter writes one `LogsData` line per result line, holding that line's events, under
  the instrumentation scope `agenteval`, with `service.name` as the only resource attribute and no resource when
  `run.json` names no service. The event of a line with neither `endedAt` nor `startedAt` has no `timeUnixNano`, which
  OTLP reads as unknown.
- **A run with `contentCapture: off`** (OT-3). The converter writes no `gen_ai.evaluation.explanation` for such a run,
  wherever the events go: [SEC-6](../spec/08-security.md#84-privacy) forbids the attribute in that run's own
  `logs.otlp.jsonl`, and the `reason` row of the table yields to it.
- **The sealed results** (OT-7). The converter exports the lines as `results.ndjson` holds them, not the
  effective view ([§4.3](../spec/04-integrity.md#43-the-effective-view)): overlays are not applied, so an `override`
  does not change the exported state.
- **Only a run that verifies** (OT-8). The converter exports a run only when a Run verifier
  ([§4.5](../spec/04-integrity.md#45-verification-outcomes)) finds it `intact` or `unsealed`, with no problem but an
  authorized withhold; it refuses any other run (the reference converter uses `tools/aef_verify.py run`). A run it
  writes from events is verified the same way, and refused (nothing written) when it does not verify.
- **Refused exports** (OT-9). The converter refuses the whole export, and writes nothing, for a reasoning
  blob it would send as an explanation that is not UTF-8 or is over 4 MiB, and for a time `timeUnixNano` cannot hold:
  before 1970-01-01T00:00:00.000000001Z (0 means unknown) or after 2554-07-21T23:34:33.709551615Z (an unsigned 64-bit
  count of nanoseconds).

## OpenTelemetry → AEF

| OpenTelemetry | AEF | Fidelity |
|---|---|---|
| the log record itself | a line of `logs.otlp.jsonl` | exact |
| log record `traceId`, `spanId` | `traceLink` | exact |
| `gen_ai.evaluation.name` | `scores[].metric`, and `path` when nothing better is known | exact for the metric |
| (no metric declaration) | a `metrics.json` entry ([SUM-1](../spec/03-run.md#35-metricsjson)) with `kind: score`, `direction: none`, `scale: unbounded` | lossy: the event gives no kind, direction or range |
| `gen_ai.evaluation.score.value` | `scores[].value` | exact |
| `gen_ai.evaluation.score.label` that is an AEF state name | `state` | exact |
| any other label (`relevant`, `pass`) | `state: scored`, the label in `scores[].label` (at most 64 characters) | exact; an importer may also declare that `pass` means `passed` |
| a value and no label | `state: scored` | exact |
| `gen_ai.evaluation.explanation` | `reason` (at most 4096 characters) | exact up to that length |
| `error.type` | `state: error`, with the type in `reason` | exact |
| `timeUnixNano` | `endedAt` | exact |
| `test.case.name` | `caseId` | exact |
| `gen_ai.response.id` | `caseId`, when no case name is present | lossy |
| resource `service.name`; `gen_ai.agent.id` on the parent span | `subject.telemetry.serviceName`, `subject.telemetry.agentId` | exact |
| the parent span and its trace | lines of `traces.otlp.jsonl` | exact |
| the source's schema URL | `otel.schemaUrls` | exact |

A run built from events alone is an imported run: its `run.json` names the source in `imported` and lists what the
converter supplied, such as `subject.ref` and `execution.targetMode` ([RUN-15](../spec/03-run.md#32-runjson)). Like
every conversion, it is sealed with `sealedBy: ingest` ([README](README.md#what-maps-means)). The reference
converter, `tools/aef_interop.py from-otel`, follows the table and these rules (settled 2026-10-09):

- **The run header** (OT-4). The converter takes the run id, `imported.from`, the subject's `ref` and `kind` and the
  target mode from the person converting; writes `status: completed`; takes `startedAt` and `endedAt` from the
  earliest and latest event time (the conversion time when no event has one); and lists each of these in
  `imported.asserted`. It writes `contentCapture`, its own choice: `on` unless asked otherwise, listed in
  `imported.asserted` too ([RUN-11](../spec/03-run.md#32-runjson), [RUN-15](../spec/03-run.md#32-runjson); as the
  ASSERT importer does). It writes a `summary.json` without lanes (the events carry no summary), and a metric
  declaration only for a metric some line scores. It seals the run at the conversion time, which is not before the
  last event, and verifies it (OT-8). It reads no traces, so it imports neither `gen_ai.agent.id` nor the parent
  spans.
- **An event with both `error.type` and an explanation** (OT-5). Both rows write `reason`: the converter keeps the
  explanation, and the type is lost. An `error.type` without an explanation becomes the `reason`, with or without the
  label `error`: that event is `error`, not a refusal.
- **Each line** (OT-10). `evaluator.id` is the event's name. An explanation longer than 4096 characters is
  cut to its first 4096.

**Refused** (OT-4, OT-6, OT-8; settled 2026-10-09). The converter refuses these, naming the rule, and writes nothing:

- events whose resources name more than one `service.name`: a run has one subject (OT-4);
- logs whose records carry content ([SEC-6](../spec/08-security.md#84-privacy)) when asked for `contentCapture: off`:
  the run would not verify (OT-8);
- an event without `gen_ai.evaluation.name`, which the convention requires (OT-6);
- a label longer than 64 characters, the most `scores[].label` holds (OT-6);
- an event with neither `test.case.name` nor `gen_ai.response.id`: a result needs a `caseId` (OT-6);
- a second event of one case with the same name (a composite and its child scored on one metric, a case's trials, a
  line's two exports): the path comes from the name, so their lines would have one result id (OT-6);
- a label that is not a state name, without a score value: `scores[].value` is required (OT-6);
- a typed-absence label with a score value, or without an explanation (the label `error` beside an `error.type`
  excepted, OT-5): a typed absence has no scores and has a `reason` (OT-6);
- the label `pending`: a converted run is closed ([RES-3](../spec/03-run.md#341-states)) (OT-6);
- a label other than `error` beside `error.type` (OT-6);
- an event with no label, no value and no `error.type` (OT-6).

[`examples/otel-aef/refusals/`](examples/otel-aef/refusals/) holds an input for most of these, and
[`examples/aef-otel-aef/refusals/`](examples/aef-otel-aef/refusals/) the refused exports of OT-8 and OT-9.

## What does not carry over

[`examples/aef-otel-aef/`](examples/aef-otel-aef/) and
[`examples/aef-otel-aef-redteam/`](examples/aef-otel-aef-redteam/) take two corpus runs to events and back, and
`tools/check_interop.py` checks that what the trip loses is what these lists say, field by field.

**AEF → OpenTelemetry.**

- The run id and the result path.
- The result tree: `parentResultId`, `component` and `aggregation`.
- A score's own `label`, because the event's label holds the state; and its `normalized` value.
- Several scores of one line as one result: each is an event of its own, and comes back as a line of its own.
- Trials and their rollups.
- Thresholds, verdict rules and uncertainty.
- Evaluator and annotator identity, until PR #359 lands.
- `severity`, `durationMs`, `turns`, `attack` and `lane`.
- The reasoning blob: its text survives as the explanation, its digest does not.
- A line's start time, when it also has an end time: the event has one time.
- The traces and the `usage` on their spans, unless `traces.otlp.jsonl` is sent beside the events (the last rows of the
  table); even then, no row of "OpenTelemetry → AEF" puts `usage` back on a result line.
- Metric declarations, the summary and gate decisions.
- The run header, except the subject's `service.name`: the producer, the rest of the subject, the deployment, the suite
  and its digest, the judges and their calibration, `execution.targetMode`, the run's status and times, its
  configuration, `otel` and content capture.
- Evidence (its records, blobs and digests), the seal, overlays and signatures.

**OpenTelemetry → AEF.**

- The run and the result path: the event has no attribute for either, so an importer builds `runId` and `path`
  itself, and the result id differs from the original's.
- The metric's kind, direction and range.
- The evaluated operation's start time, when only the event's time is known.
- `error.type`, when the event also has an explanation (OT-5).

## Worked example

Every block below is data of a checked example: `tools/check_interop.py` fails when a block differs from it. The
corpus line for `triage/helpfulness` of `case-17` (run `01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10`). Its `resultId`
recomputes with [RES-4](../spec/03-run.md#342-result-ids):

```json
{"schemaVersion":"1.0","resultId":"r_1264eeb36620c9cbe97b71ffdbcfd331","parentResultId":"r_479d157f3423e95d566bcbfc0c6d2461","caseId":"case-17","path":"triage/helpfulness","evaluator":{"id":"llm:helpfulness","version":"3"},"state":"failed","severity":"medium","scores":[{"metric":"helpfulness","value":0.1,"normalized":0.1}],"verdictRule":{"expr":"helpfulness >= threshold","threshold":0.7,"source":"suite"},"annotator":{"kind":"LLM","model":"gpt-5.1","promptHash":"sha256:cf07194ee232eb531e15f690000d19846dea69cf05504782658afcfacb9228a2","rubricDigest":"sha256:29fd018a9848938bc2b0e33fffa32bde2827e81388d0e03195919be5835c3605"},"reasoning":{"blob":"sha256:635221c9c64f48e2843e4186b0a1b66f07a1492c14dcb866bb83dba6a5e5fef5","bytes":122},"usage":[{"role":"agent","gen_ai.usage.input_tokens":912,"gen_ai.usage.output_tokens":214,"gen_ai.usage.cache_read.input_tokens":640},{"role":"judge","gen_ai.usage.input_tokens":1747,"gen_ai.usage.output_tokens":488,"gen_ai.usage.reasoning.output_tokens":301,"costUsd":0.012,"costSource":"price-table:2026-09-30"}],"startedAt":"2026-10-02T14:02:11.120Z","endedAt":"2026-10-02T14:02:15Z","traceLink":{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7"},"component":{"weight":0.5,"required":false},"evidence":["E-2"]}
```

Exported as one OTLP/JSON logs line (the third line of
[`examples/aef-otel-aef/otel.jsonl`](examples/aef-otel-aef/otel.jsonl), which exports the whole run):

- the parent is the evaluated operation, `invoke_agent support-triage`, from `traceLink`;
- the time is the line's `endedAt`;
- the label is the state name;
- the explanation is the reasoning blob's text, because the run has `contentCapture: on` and the line has no `reason`.

```json
{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"support-api"}}]},"scopeLogs":[{"scope":{"name":"agenteval"},"logRecords":[{"timeUnixNano":"1790949735000000000","eventName":"gen_ai.evaluation.result","traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7","attributes":[{"key":"gen_ai.evaluation.name","value":{"stringValue":"helpfulness"}},{"key":"gen_ai.evaluation.score.value","value":{"doubleValue":0.1}},{"key":"gen_ai.evaluation.score.label","value":{"stringValue":"failed"}},{"key":"gen_ai.evaluation.explanation","value":{"stringValue":"The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n"}},{"key":"test.case.name","value":{"stringValue":"case-17"}}]}]}]}]}
```

Lost on the way: the run id, the path `triage/helpfulness`, the parent, the weight 0.5 and `required: false`, the
threshold 0.7, the severity, the evaluator id and version, the judge's model and digests, the line's start time, the
reasoning blob's digest and the evidence `E-2`. The `usage` entries are not on the event: of them, only the judge's
input and output tokens are also on a span, its `chat gpt-5.1` span in `traces.otlp.jsonl`.

Imported into a new run `otel-import-0001`, the event gives the line below, valid against the writer schema (the first
line of [`examples/otel-aef/run/results.ndjson`](examples/otel-aef/run/results.ndjson), which imports this event, the
registry's below, and five more from a hand-written file). The label `failed` is a state name, so the state comes back.
The run id is new and the path is the metric name, so the result id changes from `r_1264eeb36620c9cbe97b71ffdbcfd331`
to `r_66dbca71f5b161285e50f3acef16a88d`:

```json
{"schemaVersion":"1.0","resultId":"r_66dbca71f5b161285e50f3acef16a88d","caseId":"case-17","path":"helpfulness","evaluator":{"id":"helpfulness"},"state":"failed","reason":"The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n","scores":[{"metric":"helpfulness","value":0.1}],"endedAt":"2026-10-02T14:02:15Z","traceLink":{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7"}}
```

The event in the OpenTelemetry registry's own example, with `Relevance` 4.0 and the label `relevant` for the
completion `chatcmpl-123`, imports into the same run as a `scored` line with the label kept:

```json
{"schemaVersion":"1.0","resultId":"r_a9592acc5b239e713eb867a36a4d5734","caseId":"chatcmpl-123","path":"Relevance","evaluator":{"id":"Relevance"},"state":"scored","scores":[{"metric":"Relevance","value":4.0,"label":"relevant"}]}
```

## Still open

Nothing on this page waits on an AEF change. The losses above come from attributes OpenTelemetry has not defined yet:
a run id, a test case id, evaluator identity, a score range and an interval.

Settled 10-09: the cases writing the reference converter found undecided (OT-1 to OT-6), and those a second converter
(AgentEval.Results.Adapters, .NET) found (OT-7 to OT-10, and the items they refine), are now
rules of this page. OT-1 to OT-3 and OT-7 to OT-9 are under [AEF → OpenTelemetry](#aef--opentelemetry); OT-4, OT-5
and OT-10, and the refusals of OT-4, OT-6 and OT-8, are under [OpenTelemetry → AEF](#opentelemetry--aef).
