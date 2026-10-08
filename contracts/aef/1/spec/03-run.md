# 3. The run

## 3.1 A run is one folder

- **[RUN-1]** A run is a self-contained folder. Its identity is in `run.json`, never in the path: a reader finds runs
  by looking for `run.json`. (A conventional location is `<root>/runs/<yyyy>/<mm>/<runId>/`; nothing depends on it.)
- **[RUN-2]** A run folder holds only these files:

  | File | Schema | Present |
  |---|---|---|
  | `run.json` | `run` | always |
  | `results.ndjson` | `result`, one line each | always (empty for a run aborted before any result) |
  | `metrics.json` | `metrics` | always |
  | `summary.json` | `summary` | in a closed run |
  | `evidence.ndjson` | `evidence`, one line each | when a result cites evidence |
  | `gates.ndjson` | `gate-decision`, one line each | when the producer took a gate decision |
  | `blobs/sha256/<ab>/<hex>` | none: raw bytes | when a line references a blob |
  | `traces.otlp.jsonl` | OTLP/JSON, one `TracesData` object per line | optional |
  | `logs.otlp.jsonl` | OTLP/JSON, one `LogsData` object per line | optional: OpenTelemetry events, such as `gen_ai.evaluation.result` |
  | `ext/…` | none: the producer's files | optional |
  | `seal.json` | `seal` | in a sealed run (§4.1) |
  | `attestation.dsse.json` | DSSE envelope (§4.4) | optional |
  | `overlays/events.ndjson` | `overlay-event`, one line each | after close, when something is decided or noted (§4.2) |
  | `overlays/seal-<nnnn>.json` | `overlay-seal` | one per batch of overlay events |
  | `overlays/seal-<nnnn>.dsse.json` | DSSE envelope | optional: a batch's signature |

  A file not in this list (a `notes.txt` someone added) is reported as `not-sealed` (§4.1) when the seal does not list
  it, as `unexpected-file` (§3.9) when it does (a producer seals only these files), and under `overlays/` as
  `unexpected-file` (§4.2); a hidden file a file manager added (`.DS_Store`) also breaks [RUN-3]. Tools that copy runs **MUST NOT** add files.
- **[RUN-3] Paths.** Every path in a run folder is made of segments of ASCII letters, digits, `.`, `_` and `-`,
  separated by `/`, at most 255 bytes in all (a longer path is a `path` problem, not a `limit`). No segment starts or
  ends with `.` (so `.`, `..` and hidden files such as `.DS_Store` are excluded), or is (ignoring case and any
  extension) a name Windows reserves (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`). No two paths, and no
  two of their folders, differ only in letter case (`ext/Data/x` and `ext/data/y` clash: a case-insensitive file
  system merges the folders). So no file system re-encodes, re-orders or merges them. A path that breaks this is
  reported as `path`; for a case clash, the later of the two paths in byte order.
- **[RUN-4] A closed run never changes.** While `status` is `running` the producer may rewrite its files. When it
  closes the run (`completed` or `aborted`) it writes their final form; from then on nothing edits, adds or removes a
  file outside `overlays/`. Everything added later is an overlay (§4.2).

## 3.2 `run.json`

The header of a run: who produced it, what was evaluated and how, with which cases and judges, when, and what content
was kept.

- **[RUN-5]** `status` is `running`, `completed` or `aborted`. There is no `sealed` status: being sealed is a fact
  about `seal.json`, established by verification (§4.5), never a field. A `completed` run has `endedAt`; an `aborted`
  run has `endedAt` and an `abortReason`. `endedAt` is not earlier than `startedAt`. A `running` run has no `endedAt`.
- **[RUN-6] What was evaluated.** `subject` names it (`ref`, `kind`, and its exact `version`, required when the run
  serves a checkpoint, §5.3); `deployment` names where it ran.
- **[RUN-7] How the target was driven.** `execution.targetMode` is **required**:

  | `targetMode` | The results describe |
  |---|---|
  | `live` | the real subject, answering at run time |
  | `replayed` | recorded answers of the real subject, played back |
  | `scripted` | a scripted stand-in that follows a fixed script (to show what checks do, not how a subject behaves) |
  | `mocked` | a stand-in that is not the subject |

  Only `live` evidence describes how the subject behaves now. A reader **MUST** show a run's target mode wherever it
  shows its results, and **MUST NOT** compare a `live` run with one of another mode as if they measured the same thing
  (§5.3.4). `execution.stimulus` says where the inputs came from: `suite` (a fixed suite), `generated` (generated at run
  time, for example by an attacker model), `external` (another tool's cases), or `other`. (A run converted from another
  tool's output is marked by `imported`, [RUN-15].)
- **[RUN-8] The suite.** `suite` names the cases that ran: `ref`, exact `version`, and, when the content is frozen,
  its `digest`. `executionPolicy` says how many trials each case had.
- **[RUN-9] Judges.** `judges` lists the models that graded results (`model`, `provider`, `mode`, `rubricDigest`).
  A judge **MAY** carry `calibration`: how far it agreed with labelled cases before this run (`labelSet`, `n`,
  `accuracy`, `kappa`, `dangerousErrors`, `measuredAt`). It is the producer's claim about the judge, sealed with the
  run; a reader shows it as such.
- **[RUN-10] No secrets.** `run.json` **MUST NOT** hold a credential: `deployment.endpoint` has no user information
  (no `user:password@`), no query string and no fragment (no `?api-key=…`; the schema refuses all three), and nothing in `config` or `ext` is a key, token or password. A run is
  sealed and kept: a secret written there cannot be taken back.
- **[RUN-11] Content capture.** `contentCapture` says what text the run keeps:
  - `on`: prompts, responses, tool arguments and judge reasoning **MAY** be kept, in blobs.
  - `off`: they are not kept, **and neither is any digest of them**: a SHA-256 of a short prompt is reversed by trying
    candidates. In a run with `off`, no result carries `reasoning` or an `annotator.promptHash`, and no evidence
    record of a content kind (`judge_reasoning`, `tool_call`, `document`, `input`, `expected`, `output`,
    `transcript`) is written. Reported as `content-capture` (§3.9).
  A producer **SHOULD** write `contentCapture`; a reader treats a run without it as `on` (content may be present).
- **[RUN-15] Imported runs.** A run converted from another tool's output carries `imported`: the tool (`from`) and
  every `run.json` field the converter supplied because the original did not record it (`asserted`, dotted paths). A
  reader **MUST** show those fields as the converter's claims, not the original producer's. The producer named in
  `producer` is the converter.
- **[RUN-12]** `provenance` is written by a runner into every run it produces: the plan (`planId`, and `planDigest`,
  the SHA-256 of the plan's bytes), the `jobId` and the `runnerId` (§6). Sealed with `run.json`, it ties the run to
  the job that made it.
- `config.thresholds` records, per metric or result path, the rule the producer's verdict used, for display.
  `costPolicy` records the spending limit and the price table the producer used to estimate cost.

## 3.3 Times and identity of a run

- **[RUN-13]** `runId` is unique for the producer: a producer **MUST NOT** reuse a `runId` for a different run. Two
  runs with the same `runId` and different run hashes are different runs; a host keeps both and reports the clash.

## 3.4 `results.ndjson`

One line per node of the run's result tree.

### 3.4.1 States

- **[RES-1]** `state` is one of:

  | State | Meaning | Counts as |
  |---|---|---|
  | `passed` | Measured, and the rule was met | the only pass |
  | `failed` | Measured, and the rule was not met | a failure |
  | `warn` | Measured; a soft failure | not a pass |
  | `inconclusive` | Measured, but the evidence does not decide (a split panel, an interval across the threshold) | not a pass |
  | `scored` | Measured, with no pass/fail rule applied: a score only (a latency, an imported score without a label) | not a pass, not a fail |
  | `not_measured` | An input the evaluator needs was not there | a typed absence |
  | `not_applicable` | The evaluator does not apply to this case | a typed absence |
  | `skipped` | Not run | a typed absence |
  | `error` | Did not complete | a typed absence |
  | `pending` | Not finished (only in an open run) | a typed absence |

- **[RES-2]** A typed absence is never a pass and never a fail with a score of 0. A line in a typed-absence state
  **MUST** have a `reason`, and carries no `scores`.
- **[RES-3]** A closed run has no `pending` line.

### 3.4.2 Result ids

- **[RES-4]** `resultId` is deterministic, so the same run read twice gives the same ids:

  ```
  resultId = "r_" + first 32 hex characters of SHA-256( UTF-8( runId ␟ caseId ␟ path ␟ trial ) )
  ```

  where ␟ is U+001F, and `trial` is the trial number in plain integer digits, or the empty string on a line without
  one. `caseId` and `path` contain no control character (C0, DEL or C1), so U+001F cannot appear in them.
  `conformance/result-ids.json` holds vectors, non-ASCII included.

### 3.4.3 Composites

- **[RES-5]** A composite node carries `aggregation`: the `strategy` the producer used, its `threshold` and `score`, how
  many children were `measured` of the `total`, why the others were not (`unmeasured`: counts per typed absence), the
  `rulePath` (which branch of the producer's verdict rules decided the state) and `decisive` (the children that decided
  it). Each child has `parentResultId` and `component` (`weight`, `required`).
- **[RES-6] Aggregation is descriptive.** It records how the producer reached a composite's state; it is not a
  formula AEF defines. A reader **MUST NOT** recompute a composite's state from its children or present a different
  one. `strategy` and `rulePath` are a vocabulary for display (see the table); a reader shows an unknown value as
  written. A verifier checks only what is structural: `measured` ≤ `total`, the `unmeasured` counts add up to
  `total` − `measured`, and every `decisive` id is a child of this node (§3.9).

  | `rulePath` | The state came from (informative) |
  |---|---|
  | `required-error` | a required child that errored |
  | `nothing-measured` | no child being measured |
  | `threshold` | the score against the threshold |
  | `severity` | the worst severity among the required children |
  | `under-covered` | too few children measured (fewer than `minimumMeasuredShare` of `total`) |

  | `strategy` | The producer combined the children by (informative) |
  |---|---|
  | `WeightedSum` | the weighted average of the measured children's scores |
  | `Min` | the lowest measured child score: any failing child fails the composite |
  | `WeightedMedian` | the weighted median of the children's scores (typically a panel of judges) |
  | `CapByWorst` | a weighted average, capped by the severity of the worst failing child |
  | `MajorityVote` | the verdict most children reached (a tie goes to the more severe); the score is their mean |

- **[RES-7]** `verdictRule.expr` is a human-readable description of the rule a leaf applied; a reader **MUST NOT**
  evaluate it.

### 3.4.4 Repeated trials

- **[RES-8]** When a case runs several times, each trial's lines carry `trial` (0-based), and one rollup line per case
  and path carries `trials`: `n`, `passed` (≤ `n`), the `aggregation` and `agree` (`false` when the trials disagreed:
  the case is flaky). The case's result at that path is the rollup line, never one trial. A line carries `trial` or
  `trials`, never both.

### 3.4.5 Facts about a result

- **[RES-9]** `severity` (`none`, `low`, `medium`, `high`, `critical`) is how bad a failure is. A `failed` or `warn`
  line **SHOULD** carry it; where a rule needs the severity of a failure that has none, it is taken as `critical`
  (§5.3.2).
- **[RES-10]** Other optional facts: `scores` (per metric, with the value and an optional normalised value),
  `uncertainty` (an interval), `annotator` (who graded: code, an LLM, a person; the prompt hash and rubric digest; a
  panel's agreement, `agree` ≤ `of`, checked as `annotator` in §3.9), `usage` (tokens in OpenTelemetry's
  `gen_ai.usage.*` names, cache and reasoning tokens included, and cost; one entry per party, no role twice),
  `startedAt` and `endedAt` (not before `startedAt`), `durationMs`, `turns` (conversation turns the case took), `attack` (for adversarial
  cases: the `technique`, its ids in public taxonomies, and whether it succeeded), `traceLink` (the span of the
  operation the result evaluates, such as the agent's invocation, or with `traceId` only its trace; OpenTelemetry
  parents an evaluation result to that span, and a judge's own call is cited as `evidence` instead), `evidence`
  (evidence ids) and `reasoning` (a blob).
- **[RES-11]** Every line names its `evaluator`: the `id` (and optionally the `version`) of the check, judge or
  composite that produced it. Two lines with the same evaluator id and version were produced by the same procedure.

## 3.5 `metrics.json`

- **[SUM-1]** `metrics.json` declares every metric a result or the summary names: its `id` (unique), `kind`
  (`score`, `rate`, `count`, `duration`, `cost`, `verdict`), `direction` (`higher_better`, `lower_better`, `none`) and
  `scale` (`min` ≤ `max`). A metric with direction `none` is shown, never coloured as better or worse.

## 3.6 `summary.json`

The summary is how a run's results roll up, per lane (a named group of cases, such as a suite) and metric. Lane
evaluation (§5.3) reads it, so it is defined exactly.

- **[SUM-2]** `summary.runId` is `run.json`'s `runId`.
- **[SUM-3]** Each entry names a `lane`, a `metric` and a `path`: it summarises the result lines of that lane at that
  path, one per case. A result line belongs to the lane its `lane` names; when the summary has a single lane, a line
  without `lane` belongs to it. Trial lines are not counted; their rollup line is.
- **[SUM-4]** For each such line: `not_applicable` lines are left out entirely; `not_measured`, `skipped`, `error` and
  `pending` lines are **not measured**. Every other line (`passed`, `failed`, `warn`, `inconclusive`, `scored`):
  - for a metric of kind `rate` or `verdict`, is measured, with the value 1 when its state is `passed` and 0
    otherwise (`warn` and `inconclusive` count as 0: not a pass), except a `scored` line, which has no verdict and is
    not measured;
  - for any other kind, is measured when it has a score for the metric, with that score's `value`, and is not
    measured when it has none.
- **[SUM-5]** Then: `N` is the number of lines not left out; `n` the measured ones; `notMeasured` = `N` − `n`; `sum`
  and `sumSq` are the sum and the sum of squares of the measured values; `value` is `sum` for a metric of kind `count`,
  and `sum` / `n` otherwise, or `null` when `n` is 0. `stderr` and `ci` are the producer's, over the same values.
- **[SUM-8] Aggregates.** An entry with `aggregate` carries a `value` computed by its `method` instead of the mean:
  - `median`, `min` and `max` are defined here, over the measured values of [SUM-4] (the median of an even count is
    the mean of the two middle values). A verifier recomputes their `value` like any other.
  - Any other `method` (pass@k, F1, a bootstrap figure) is the producer's, shown as written. A verifier recomputes the
    entry's `N`, `n`, `notMeasured` and `sum`, never its `value`, and **no lane reads it** ([LANE-2]): a number nobody
    can check never decides a release.
  In every case `value` is `null` when `n` is 0.
- **[SUM-9]** No two entries of a summary have the same `lane`, `metric` and `path`, so every rule that reads an entry
  reads one, and no two `usage` entries have the same `role` and `model` (an absent `model` is a value of its own).
  Reported as `summary-duplicate`.
- **[SUM-6]** `verdict` is the producer's verdict on the entry under its `rule`; when `n` is 0 it is `not_measured`;
  `scored` when the producer applied no rule to it (a measurement only).
- **[SUM-7]** `cost`, when present, is the run's total cost in US dollars and where the figure came from. `usage`, when
  present, is the run's total usage, one entry per party (`role`) and `model`.
- A verifier recomputes `N`, `n`, `notMeasured`, `sum` and `value` from `results.ndjson` (§3.9); `value` and `sum`
  match when they differ by at most 1e-9 × max(1, |recomputed|).

## 3.7 `evidence.ndjson` and blobs

- **[EVD-1]** An evidence record (`E-…`, unique in the run) is what a result cites: its `kind`, a `link` that is exactly
  one of a blob in the run, a span (`traceId` and `spanId`), or a URI outside the run, and a `description`.
- **[EVD-2]** For a blob link, `digest` is **REQUIRED** and is the blob's SHA-256 (`sha256:` and its file name). For a
  URI link, `digest` is **OPTIONAL**: the SHA-256 of the bytes the URI served when the record was written. A span link
  has no `digest`.
- **[EVD-3]** A blob is stored at `blobs/sha256/<first two hex characters>/<64 hex characters>`, and its file name is
  the SHA-256 of its bytes. A blob nothing references is allowed; it is sealed like every file.
- The three kinds of link are fixed: a new kind is a new major version (the link is not a union on a kind, so a reader
  could not take an unknown one as "other").

## 3.8 `gates.ndjson`

- **[GATE-1]** A gate decision is a decision the producer took when the run closed (a `--fail-on` gate, a baseline
  comparison): its `rule` (a strategy name, for display), its `inputs` (the results, the requirements, and a baseline as
  a run reference with its run hash), whether a comparison it needed was shown `comparable`, the `outcome` (`ship`,
  `no_ship`, `inconclusive`), the exit code and the `decisive` results.
- **[GATE-2]** An `incomparable` comparison never yields `ship`.

## 3.9 Rules across files

A schema checks one document. A **run verifier** also checks these rules, and reports each problem as a path and a
code, ordered by path and then by code. Paths are ordered by their UTF-8 bytes, except that `<file>:<line>` paths of one file are ordered by line number as a number (`results.ndjson:9` before `results.ndjson:10`). A run with any of these problems is invalid.

A verifier reads each JSON file whole and each NDJSON file line by line (§2.2). An NDJSON file whose framing breaks
[ENC-5] or [ENC-7] (a CR, a blank line, a missing final LF, a byte-order mark) is reported once, as `encoding` at the
file's path, and none of its lines is read. Otherwise a line that is not an I-JSON object is reported as `encoding`
at `<file>:<line>` (1-based). The rules in the table from `result-id` down are checked only when no file or line has
an `encoding`, `limit` or `schema` problem: they would otherwise be checked against data that was not read.

| Code | Path | The rule |
|---|---|---|
| `encoding` | the file, or `<file>:<line>` for one line | §2.1, §2.2 (I-JSON, UTF-8, NDJSON) |
| `limit` | the file | §2.6 |
| `schema` | the file (`results.ndjson:<line>` for a line) | the document or line is not valid against the reader schema, or holds a time that does not exist ([ENC-8]: a pattern cannot refuse `2026-02-31`); also a file [RUN-2] requires that is absent, at its path |
| `path` | the path | [RUN-3]; for two paths that differ only in case, the later one in byte order |
| `result-id` | `results.ndjson:<line>` | a `resultId` that is not the [RES-4] hash of the line, or one an earlier line already has |
| `parent` | `results.ndjson:<line>` | a `parentResultId` that is no line of the run |
| `aggregation` | `results.ndjson:<line>` | [RES-6]: counts that do not add up (an absent `unmeasured.pending` is 0), or a `decisive` id that is not a child |
| `annotator` | `results.ndjson:<line>` | a panel whose `agree` exceeds `of` ([RES-10]) |
| `trials` | `results.ndjson:<line>` | a rollup whose `passed` exceeds `n` |
| `pending` | `results.ndjson:<line>` | a `pending` line in a closed run ([RES-3]) |
| `evidence` | `results.ndjson:<line>` | an evidence id no record of `evidence.ndjson` has |
| `evidence-id` | `evidence.ndjson:<line>` | an `evidenceId` an earlier line already has |
| `evidence-digest` | `evidence.ndjson:<line>` | [EVD-2] |
| `blob` | the citing line | a blob a line references that is not in the run, unless a verified `redact` overlay withholds it ([OVL-10]) |
| `blob-digest` | the blob's path | a blob whose bytes do not hash to its name ([EVD-3]) |
| `reasoning-size` | `results.ndjson:<line>` | a `reasoning.bytes` that is not its blob's size |
| `metric` | the citing line or file | a metric no entry of `metrics.json` declares; a line that scores one metric twice (the summary then counts that line as not measured for it); in `metrics.json`, a metric declared twice or a `scale` whose `min` exceeds its `max` |
| `summary-run-id` | `summary.json` | [SUM-2] |
| `summary` | `summary.json` | an entry whose `N`, `n`, `notMeasured`, `sum` or `value` is not what [SUM-3]–[SUM-5] and [SUM-8] give |
| `summary-duplicate` | `summary.json` | two entries with the same `lane`, `metric` and `path`, or two `usage` entries with the same `role` and `model` ([SUM-9]) |
| `gate` | `gates.ndjson:<line>` | a result a decision names that is no line of the run, or `ship` on an incomparable comparison |
| `trace-link` | the citing line | a span link or `traceLink` that names no span of `traces.otlp.jsonl`, when that file is present (a `traceLink` without `spanId` names a trace: it resolves when any span has that `traceId`; ids compare without regard to letter case, and a writer writes lower case) |
| `content-capture` | the citing line, or `traces.otlp.jsonl:<line>` or `logs.otlp.jsonl:<line>` | [RUN-11], [SEC-6] |
| `run-times` | `run.json` | [RUN-5] |
| `unexpected-file` | the path | a file that [RUN-2] does not list (outside `ext/` and `overlays/`) and the seal lists |
| `attack` | `results.ndjson:<line>` | an `attack` with `success: true` on a line in state `passed`: an attack that succeeded is not a pass for the subject |
| `calibration` | `run.json` | a judge's calibration with `dangerousErrors` above `n`, or `measuredAt` after the run's `startedAt` ([RUN-9]: it was measured before the run) |
| `execution-policy` | `run.json` | `requirePasses` above `trialsPerCase` |
| `interval` | the line, or `summary.json` | an `uncertainty.ci` or a summary `ci` whose `low` exceeds its `high` |
| `result-times` | `results.ndjson:<line>` | a line whose `endedAt` is before its `startedAt`, or whose `usage` names one role twice |

When `traces.otlp.jsonl` is absent, a span link points to a trace store outside the run; a reader shows it as
external, and nothing verifies it.

## 3.10 Traces

- **[RUN-14]** `traces.otlp.jsonl` holds OpenTelemetry traces in the OTLP/JSON encoding, one `TracesData` object (the
  same JSON as an `ExportTraceServiceRequest`, as OpenTelemetry's file exporter writes it) per line (NDJSON, §2.2). `logs.otlp.jsonl`
  likewise holds OTLP/JSON `LogsData` objects, one per line: OpenTelemetry events such as `gen_ai.evaluation.result`.
  `run.json`'s `otel.schemaUrls` names the OpenTelemetry schema URLs both follow. When a producer writes a
  `gen_ai.evaluation.result` event for a result, its `gen_ai.evaluation.score.label` is the result's `state` name,
  so the ten states survive the trip (interop/opentelemetry.md). Spans use the OpenTelemetry GenAI semantic conventions where
  they apply; `run.json`'s `otel` names the convention version.
