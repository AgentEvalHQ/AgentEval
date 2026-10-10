# AEF 1.0: one line of gates.ndjson

*Generated from [`schemas/writer/gate-decision.schema.json`](../schemas/writer/gate-decision.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

A gate decision the producer made when the run closed (a CLI --fail-on gate, a baseline comparison): its rule, inputs, outcome and exit code, sealed with the run.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this line follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `gateId` | [ref](common.md#ref) | yes |  | The gate that decided, as a typed reference. |
| `decisionId` | [id](common.md#id) | yes |  | This decision's id. |
| `rule` | object | yes |  | The rule the gate applied (GATE-1). |
| `rule.strategy` | string | yes | ≥ 1 chars; ≤ 128 chars | The gate's strategy, for display. |
| `rule.inputs` | array of string |  | ≤ 64 items | What the strategy read, by name, for display. |
| `inputs` | object | yes |  | What the decision was taken on: results, requirements and a baseline run (GATE-1). |
| `inputs.results` | array of [resultId](common.md#resultid) |  | ≤ 4096 items | The results the decision read; each is a line of the run (GATE-1). |
| `inputs.requirements` | array of [id](common.md#id) |  | ≤ 1024 items | The external requirement ids the decision answers (GATE-1). |
| `inputs.baselineRun` | [runPointer](common.md#runpointer) |  |  | The baseline run, exactly. |
| `comparability` | one of `"comparable"`, `"incomparable"`, `"not_applicable"` | yes |  | Whether a comparison the gate needed was shown comparable. incomparable never yields ship. |
| `outcome` | one of `"ship"`, `"no_ship"`, `"inconclusive"` | yes |  | ship, no_ship or inconclusive. Never ship when the comparison was incomparable (GATE-1, GATE-2). |
| `exitCode` | integer |  | ≥ 0; ≤ 255 | The exit code the gate ended with, from 0 to 255 (GATE-1). |
| `decisive` | array of [resultId](common.md#resultid) |  | ≤ 1024 items | The results that decided the outcome; each is a line of the run (GATE-1). |
| `decidedAt` | [timestamp](common.md#timestamp) | yes |  | When the decision was taken. |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |
