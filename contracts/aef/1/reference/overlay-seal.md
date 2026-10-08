# AEF 1.0: overlays/seal-<n>.json

*Generated from [`schemas/writer/overlay-seal.schema.json`](../schemas/writer/overlay-seal.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

An in-toto Statement v1 over one batch of overlay events (a byte range of overlays/events.ndjson) that names the previous batch's seal: the batches form a chain.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `_type` | `"https://in-toto.io/Statement/v1"` | yes |  | The in-toto Statement type, v1 (OVL-4). |
| `subject` | array of object | yes | ≥ 1 items; ≤ 1 items | One subject: the events file, with the SHA-256 of this batch's bytes (OVL-4). |
| `subject[].name` | `"overlays/events.ndjson"` | yes |  | Always overlays/events.ndjson (OVL-4). |
| `subject[].digest` | object | yes |  | The batch's digest (OVL-4). |
| `subject[].digest.sha256` | [sha256Hex](common.md#sha256hex) | yes |  | SHA-256 of the batch's bytes. |
| `predicateType` | `"https://agenteval.dev/aef/1/overlay-batch"` | yes |  | Marks the statement as an AEF overlay batch seal (OVL-4). |
| `predicate` | object | yes |  | The batch: its number, the run and its run hash, the byte range, and the previous seal (OVL-4). |
| `predicate.schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version of this seal, MAJOR.MINOR: 1.0 for AEF 1.0. It sits in the predicate because in-toto fixes the statement's _type (VER-1). |
| `predicate.runId` | [id](common.md#id) | yes |  | The runId of the run the events belong to (OVL-4). |
| `predicate.batch` | integer | yes | ≥ 1; ≤ 9999 | 1-based; seal-0001.json is batch 1 (at most 9,999: four digits, OVL-4). |
| `predicate.offset` | integer | yes | ≥ 0; ≤ 9007199254740991 | Where the batch starts in overlays/events.ndjson, in bytes. |
| `predicate.length` | integer | yes | ≥ 1; ≤ 9007199254740991 | The batch's length in bytes: whole lines, each ending in \n. |
| `predicate.previous` | null or object | yes |  | The previous batch's seal file and the SHA-256 of its bytes; null for batch 1. |
| `predicate.runHash` | [sha256Hex](common.md#sha256hex) | yes |  | The run hash of the run the batch was appended to (spec 04, OVL-4). |
