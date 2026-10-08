# AEF 1.0: one line of results.ndjson

*Generated from [`schemas/writer/result.schema.json`](../schemas/writer/result.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

One node of a run's result tree. A composite node carries aggregation; its children carry component and parentResultId.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this line follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `resultId` | [resultId](common.md#resultid) | yes |  | The line's id, a hash of runId, caseId, path and trial, so the same run read twice gives the same ids. Unique in the run (RES-4). |
| `parentResultId` | [resultId](common.md#resultid) or null |  |  | The resultId of the composite node this line is a child of; absent or null on a root. It must be a line of the run (RES-5). |
| `caseId` | any | yes | ≥ 1 chars; ≤ 256 chars | The case this line belongs to: 1-256 characters, with no control character (RES-4). |
| `path` | any | yes | ≥ 1 chars; ≤ 1024 chars | The node's place in the tree, / between levels. |
| `trial` | integer |  | ≥ 0; ≤ 999 | 0-based trial, when the case ran several times. Written as plain integer digits (no fraction, no exponent). |
| `trials` | object |  |  | The rollup line of a case that ran several times. The case's result is this line, never one trial; agree false marks it flaky. |
| `trials.n` | integer | yes | ≥ 1; ≤ 1000 | How many trials the case had, from 1 to 1000 (RES-8). |
| `trials.passed` | integer | yes | ≥ 0; ≤ 1000 | How many of the n trials passed; never more than n (RES-8). |
| `trials.aggregation` | one of `"MajorityVote"`, `"AllPass"`, `"AnyPass"`, `"Mean"`, `"Median"`, `"Max"`, `"PassAtK"` | yes |  | How the trials were combined into the case's state: MajorityVote, AllPass, AnyPass, Mean, Median, Max or PassAtK (with k). Descriptive: a reader shows it, never recomputes it (RES-8). |
| `trials.agree` | boolean | yes |  | Whether the trials agreed. false marks the case as flaky (RES-8). |
| `trials.k` | integer |  | ≥ 1; ≤ 9007199254740991 | k, for PassAtK: the case passes when at least one of k trials passes. |
| `evaluator` | object | yes |  | The evaluator that produced this line. |
| `evaluator.id` | string | yes | ≥ 1 chars; ≤ 256 chars | The evaluator's id, as the producer names it. |
| `evaluator.version` | string |  | ≤ 64 chars | The evaluator's version, when known. |
| `state` | [state](common.md#state) | yes |  | The node's state. passed is the only pass; failed and warn (a soft failure) are measured failures; inconclusive is measured, undecided; not_measured, not_applicable, skipped, error and pending are typed absences (RES-1). |
| `reason` | string |  | ≥ 1 chars; ≤ 4096 chars | Why the line is in a typed absence: required for not_measured, not_applicable, skipped, error and pending (RES-2). Optional otherwise. |
| `scores` | array of object |  | ≤ 256 items | The node's scores, one per metric. A line in a typed absence carries none (RES-2, RES-10). |
| `scores[].metric` | string | yes | ≥ 1 chars; ≤ 256 chars | The metric, by its id in metrics.json (SUM-1). |
| `scores[].value` | number | yes |  | The score, on the metric's scale (RES-10). |
| `scores[].normalized` | number or null |  |  | The score normalised to between 0 and 1, or null when the producer gives none (RES-10). |
| `scores[].label` | string |  | ≥ 1 chars; ≤ 64 chars | A categorical value for the metric (an OpenTelemetry score label, Inspect's C/I/P/N, a grader's class), shown as written (RES-10). |
| `verdictRule` | object |  |  | The rule that turned the scores into the state, and where its threshold came from. |
| `verdictRule.expr` | string | yes | ≥ 1 chars; ≤ 1024 chars | A human-readable description of the rule; a reader never evaluates it (spec 03, RES-7). |
| `verdictRule.threshold` | number or null |  |  | The threshold the rule compared the score with, or null when it has none. |
| `verdictRule.source` | string |  | ≤ 512 chars | Where the threshold came from, as the producer names it. |
| `annotator` | object |  |  | Who or what decided this node. |
| `annotator.kind` | one of `"CODE"`, `"LLM"`, `"HUMAN"`, `"HYBRID"`, `"OTHER"` | yes |  | Who graded: CODE (code), LLM (a language model), HUMAN (a person) or HYBRID (a combination). A reader shows a kind it does not know as written (RES-10, VER-3). |
| `annotator.model` | string |  | ≤ 256 chars | The model that graded, when one did. |
| `annotator.promptHash` | [sha256Uri](common.md#sha256uri) |  |  | The SHA-256 of the prompt the grader was given (RES-10). |
| `annotator.rubricDigest` | [sha256Uri](common.md#sha256uri) |  |  | The SHA-256 of the rubric the grader used (RES-10). |
| `annotator.panel` | object |  |  | For a panel of graders: how many agreed, of how many (RES-10). |
| `annotator.panel.agree` | integer | yes | ≥ 0; ≤ 9007199254740991 | How many panel members agreed; never more than of (RES-10). |
| `annotator.panel.of` | integer | yes | ≥ 1; ≤ 9007199254740991 | How many members the panel had (RES-10). |
| `reasoning` | object |  |  | The judge's reasoning, stored as a content-addressed blob (blobs/sha256/ab/<hex>) when content capture allows. |
| `reasoning.blob` | [sha256Uri](common.md#sha256uri) | yes |  | The digest of the blob holding the reasoning; its hex is the blob's file name (EVD-3). |
| `reasoning.bytes` | integer | yes | ≥ 0; ≤ 9007199254740991 | The blob's size in bytes. A size that is not the blob's is reported as reasoning-size. |
| `uncertainty` | null or object |  |  | How uncertain the node's score is: a standard error, an interval, or both; null when not given (RES-10). |
| `usage` | array of object |  | ≥ 1 items; ≤ 8 items | What producing this node consumed, one entry per party (the agent, a judge, an attacker): no role and model twice (RES-10). |
| `usage[].role` | one of `"agent"`, `"judge"`, `"attacker"`, `"other"` | yes |  | Whose usage this is: agent (the subject), judge, attacker (an attacker model) or other. |
| `usage[].model` | string |  | ≥ 1 chars; ≤ 256 chars | The model this party used, when it used one: with role, it tells two judges of a panel apart (RES-10). |
| `usage[].gen_ai.usage.input_tokens` | integer |  | ≥ 0; ≤ 9007199254740991 | Input tokens, as OpenTelemetry's gen_ai.usage.input_tokens (RES-10). |
| `usage[].gen_ai.usage.output_tokens` | integer |  | ≥ 0; ≤ 9007199254740991 | Output tokens, as OpenTelemetry's gen_ai.usage.output_tokens (RES-10). |
| `usage[].costUsd` | number |  | ≥ 0 | The cost, in US dollars (RES-10). |
| `usage[].costSource` | string |  | ≤ 128 chars | Where the cost figure came from, such as a price table. |
| `usage[].gen_ai.usage.cache_read.input_tokens` | integer |  | ≥ 0; ≤ 9007199254740991 | Input tokens served from a cache, as OpenTelemetry's gen_ai.usage.cache_read.input_tokens; included in input_tokens (RES-10). |
| `usage[].gen_ai.usage.cache_write.input_tokens` | integer |  | ≥ 0; ≤ 9007199254740991 | Input tokens written to a cache, as OpenTelemetry's gen_ai.usage.cache_write.input_tokens; included in input_tokens (RES-10). |
| `usage[].gen_ai.usage.reasoning.output_tokens` | integer |  | ≥ 0; ≤ 9007199254740991 | Reasoning tokens, as OpenTelemetry's gen_ai.usage.reasoning.output_tokens; included in output_tokens (RES-10). |
| `traceLink` | object |  |  | The span this result was produced in, or with traceId only, the trace (RES-10). It resolves against traces.otlp.jsonl when that file is present (spec 03, §3.9 trace-link). |
| `traceLink.traceId` | string | yes | ≤ 32 chars; pattern `^[0-9a-f]{32}$` | The trace id: 32 lower-case hex characters. |
| `traceLink.spanId` | string |  | ≤ 16 chars; pattern `^[0-9a-f]{16}$` | The span id: 16 lower-case hex characters. |
| `aggregation` | object |  |  | On a composite node: how its children became its verdict, and which branch of the verdict rules decided it. |
| `aggregation.strategy` | one of `"WeightedSum"`, `"Min"`, `"WeightedMedian"`, `"CapByWorst"`, `"MajorityVote"` | yes |  | The strategy the producer used, for display: aggregation is descriptive (spec 03, RES-6). |
| `aggregation.threshold` | number or null |  |  | The threshold the composite score was compared with, or null (RES-5). |
| `aggregation.score` | number or null |  |  | The composite score the strategy gave, or null when it gave none (RES-5). |
| `aggregation.rulePath` | one of `"required-error"`, `"nothing-measured"`, `"threshold"`, `"severity"`, `"under-covered"` | yes |  | Which branch of the producer's verdict rules decided the state, for display (spec 03, RES-6). |
| `aggregation.measured` | integer | yes | ≥ 0; ≤ 9007199254740991 | How many children were measured; at most total (RES-6). |
| `aggregation.total` | integer | yes | ≥ 0; ≤ 9007199254740991 | How many children the node has (RES-5). |
| `aggregation.minimumMeasuredShare` | number |  | ≥ 0; ≤ 1 | The share of children, from 0 to 1, the producer's rules require to be measured. Fewer gives the rule path under-covered. |
| `aggregation.unmeasured` | object |  |  | The children not measured, by state. |
| `aggregation.unmeasured.not_measured` | integer |  | ≥ 0; ≤ 9007199254740991 | Children in state not_measured (RES-5). |
| `aggregation.unmeasured.not_applicable` | integer |  | ≥ 0; ≤ 9007199254740991 | Children in state not_applicable (RES-5). |
| `aggregation.unmeasured.skipped` | integer |  | ≥ 0; ≤ 9007199254740991 | Children in state skipped (RES-5). |
| `aggregation.unmeasured.error` | integer |  | ≥ 0; ≤ 9007199254740991 | Children in state error (RES-5). |
| `aggregation.unmeasured.pending` | integer |  | ≥ 0; ≤ 9007199254740991 | Children not finished (only in an open run). Absent means 0 (RES-6). |
| `aggregation.decisive` | array of [resultId](common.md#resultid) |  | ≤ 1024 items | The children that decided the verdict, as the producer knew them. A reader never reverse-engineers this. |
| `component` | object |  |  | On a child of a composite: its weight and whether it is required. |
| `component.weight` | number | yes | ≥ 0 | The child's weight in its parent's strategy, 0 or more (RES-5). |
| `component.required` | boolean | yes |  | Whether the parent's rules require this child (RES-5). |
| `evidence` | array of [evidenceId](common.md#evidenceid) |  | ≤ 256 items | Ids of records in evidence.ndjson this node cites. |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |
| `lane` | [laneName](common.md#lanename) |  |  | The summary lane this line belongs to; may be absent when the summary has one lane (spec 03, SUM-3). |
| `severity` | [severity](common.md#severity) |  |  | How bad a failure is. A failed or warn line should carry it; where a rule needs a missing one, it counts as critical (RES-9). |
| `durationMs` | number |  | ≥ 0 | Wall-clock time the result took, in milliseconds. |
| `turns` | integer |  | ≥ 1; ≤ 9007199254740991 | Conversation turns the case took. |
| `attack` | object |  |  | For an adversarial case: the technique, its ids in public taxonomies, and whether it succeeded (null when not decided). |
| `attack.technique` | string | yes | ≥ 1 chars; ≤ 256 chars | The attack technique, as the producer names it (RES-10). |
| `attack.taxonomy` | array of object |  | ≤ 32 items; unique | The technique's ids in public taxonomies, none listed twice (RES-10). |
| `attack.taxonomy[].scheme` | one of `"owasp-llm"`, `"owasp-agentic"`, `"mitre-atlas"`, `"nist-ai-rmf"`, `"other"` | yes |  | owasp-llm, owasp-agentic, mitre-atlas, nist-ai-rmf or other. |
| `attack.taxonomy[].id` | string | yes | ≥ 1 chars; ≤ 64 chars | The technique's id in that taxonomy, such as LLM01. |
| `attack.success` | boolean or null |  |  | Whether the attack succeeded; null when that was not decided (RES-10). |
| `startedAt` | [timestamp](common.md#timestamp) |  |  | When work on this node started (RES-10). |
| `endedAt` | [timestamp](common.md#timestamp) |  |  | When work on this node ended; not before startedAt (RES-10, result-times in §3.9). |

- **Rule:** A typed absence says why, and carries no scores (spec 03, RES-2).

## Definitions

### interval

An interval at a confidence level, and how it was computed.

Type: object

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `low` | number | yes |  | The interval's lower bound. |
| `high` | number | yes |  | The interval's upper bound. |
| `level` | number | yes | > 0; < 1 | The confidence level, strictly between 0 and 1: 0.95 for a 95% interval. |
| `method` | string |  | ≤ 64 chars | How the interval was computed, as the producer names it. |
