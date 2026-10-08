# AEF 1.0: summary.json

*Generated from [`schemas/writer/summary.schema.json`](../schemas/writer/summary.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

Aggregates per lane and metric, with what was measured beside what was asked for, and the sufficient statistics to pool runs.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this document follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `runId` | [id](common.md#id) | yes |  | The runId of the run.json beside it (SUM-2). |
| `lanes` | array of object | yes | ≤ 64 items | One entry per lane: a named group of cases, such as a suite (SUM-3). |
| `lanes[].lane` | [laneName](common.md#lanename) | yes |  | quality, security, memory, performance, compliance, or a producer's own. |
| `lanes[].metrics` | array of object | yes | ≤ 1024 items | The lane's entries, one per metric and result path (SUM-3). |
| `lanes[].metrics[].metric` | string | yes | ≥ 1 chars; ≤ 256 chars | The metric summarised, by its id in metrics.json (SUM-1, SUM-3). |
| `lanes[].metrics[].n` | integer | yes | ≥ 0; ≤ 9007199254740991 | Measured. |
| `lanes[].metrics[].N` | integer | yes | ≥ 0; ≤ 9007199254740991 | The lane's lines at this path, one per case, not counting not_applicable lines (SUM-4, SUM-5). |
| `lanes[].metrics[].notMeasured` | integer | yes | ≥ 0; ≤ 9007199254740991 | N minus n: the lines counted in N that were not measured (SUM-5). |
| `lanes[].metrics[].value` | number or null | yes |  | sum for a metric of kind count, otherwise sum / n (the rate or mean of the measured values); null when n is 0 (SUM-5). |
| `lanes[].metrics[].stderr` | number or null |  | ≥ 0 | The producer's standard error over the measured values, or null (SUM-5). |
| `lanes[].metrics[].ci` | null or object |  |  | The producer's interval over the measured values, or null (SUM-5). |
| `lanes[].metrics[].verdict` | one of `"passed"`, `"failed"`, `"warn"`, `"inconclusive"`, `"not_measured"` | yes |  | The producer's verdict on this entry under its rule: passed, failed, warn, inconclusive, or not_measured, which it always is when n is 0 (SUM-6). |
| `lanes[].metrics[].rule` | string |  | ≤ 1024 chars | The rule the verdict applied, as text for display (SUM-6). |
| `lanes[].metrics[].sum` | number |  |  | The sum of the measured values (SUM-5). |
| `lanes[].metrics[].sumSq` | number |  | ≥ 0 | The sum of the squares of the measured values (SUM-5). |
| `lanes[].metrics[].path` | [text](common.md#text) | yes |  | The result path this entry summarises, one line per case (spec 03, SUM-3). |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |
| `cost` | object |  |  | The run's total cost (spec 03, SUM-7). |
| `cost.totalUsd` | number | yes | ≥ 0 | The run's total cost, in US dollars (SUM-7). |
| `cost.source` | string |  | ≤ 256 chars | Where the figure came from (SUM-7). |
