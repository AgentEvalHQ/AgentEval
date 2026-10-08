# AEF 1.0: one line of evidence.ndjson

*Generated from [`schemas/writer/evidence.schema.json`](../schemas/writer/evidence.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

One piece of evidence a result cites: what it is, its digest, and where to find it.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this line follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `evidenceId` | [evidenceId](common.md#evidenceid) | yes |  | The record's id, unique in the run; results cite it (EVD-1). |
| `kind` | one of `"span"`, `"tool_call"`, `"judge_reasoning"`, `"compliance_artifact"`, `"document"`, `"other"` | yes |  | What the evidence is: span, tool_call, judge_reasoning, compliance_artifact, document or other. A run with contentCapture off has no tool_call, judge_reasoning or document record (EVD-1, RUN-11). |
| `digest` | [sha256Uri](common.md#sha256uri) |  |  | For a blob link, required: the blob's SHA-256. For a URI link, optional: the SHA-256 of what the URI served. A span link has none (spec 03, EVD-2). |
| `link` | any or any or any | yes | ≥ 1 members | Exactly one of: a blob in this run, a span (traceId and spanId), or a URI outside the run. The span is in traces.otlp.jsonl when the run has that file, else in a trace store outside the run (EVD-1). |
| `link.blob` | [sha256Uri](common.md#sha256uri) |  |  | A blob of this run, by its digest; the file is blobs/sha256/<first two hex>/<hex> (EVD-3). |
| `link.traceId` | string |  | ≤ 32 chars; pattern `^[0-9a-f]{32}$` | The span's trace id: 32 lower-case hex characters, given with spanId. |
| `link.spanId` | string |  | ≤ 16 chars; pattern `^[0-9a-f]{16}$` | The span's id: 16 lower-case hex characters, given with traceId. |
| `link.uri` | [uri](common.md#uri) |  |  | A URI outside the run (EVD-1). |
| `description` | string |  | ≤ 1024 chars | What the evidence is, in words, for a reader (EVD-1). |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |

- **Rule:** A blob link carries the blob's digest.
- **Rule:** A span link carries no digest.
