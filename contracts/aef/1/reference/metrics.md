# AEF 1.0: metrics.json

*Generated from [`schemas/writer/metrics.schema.json`](../schemas/writer/metrics.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

The metrics this run reports. A metric with direction none is shown, never coloured as better or worse.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this document follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `metrics` | array of object | yes | ≤ 4096 items | Every metric a result or the summary names, each declared once (SUM-1). |
| `metrics[].id` | string | yes | ≥ 1 chars; ≤ 256 chars | The metric's id, unique in this file; results and the summary name metrics by it (SUM-1). |
| `metrics[].kind` | one of `"score"`, `"rate"`, `"count"`, `"duration"`, `"cost"`, `"verdict"` | yes |  | score, rate, count, duration, cost or verdict. In the summary a rate or verdict line counts 1 when passed, else 0; a count is summed and every other kind averaged (SUM-1, SUM-4, SUM-5). |
| `metrics[].direction` | one of `"higher_better"`, `"lower_better"`, `"none"` | yes |  | Which way is better: higher_better, lower_better or none. A metric with none is never shown as better or worse, and cannot regress in a comparison (SUM-1, LANE-7). |
| `metrics[].scale` | `"unbounded"` or object | yes |  | The metric's range: unbounded, or min and max with min not above max (SUM-1). |
| `metrics[].unit` | string |  | ≤ 32 chars | The unit of the values, such as ms. |
| `metrics[].description` | string |  | ≤ 4096 chars | What the metric measures, for display. |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |
