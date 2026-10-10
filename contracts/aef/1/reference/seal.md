# AEF 1.0: seal.json

*Generated from [`schemas/writer/seal.schema.json`](../schemas/writer/seal.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

An in-toto Statement v1 over every file of the run except seal.json, attestation.dsse.json and overlays/: one subject per file, the exact bytes' SHA-256, and the run hash (SEAL-1, SEAL-5).

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `_type` | `"https://in-toto.io/Statement/v1"` | yes |  | The in-toto Statement type, v1 (SEAL-5). |
| `subject` | array of object | yes | ≥ 1 items; ≤ 100000 items | One subject per sealed file, each listed once (SEAL-5, SEAL-6). |
| `subject[].name` | [relativePath](common.md#relativepath) | yes |  | The file's path inside the run folder. |
| `subject[].digest` | object | yes |  | The file's digest (SEAL-5). |
| `subject[].digest.sha256` | [sha256Hex](common.md#sha256hex) | yes |  | The SHA-256 of the file's exact bytes (SEAL-2). |
| `predicateType` | `"https://agenteval.dev/aef/1/evidence"` | yes |  | Marks the statement as an AEF evidence seal (SEAL-5). |
| `predicate` | object | yes |  | The run's id and run hash, what run.json says about its producer, subject, deployment, suite and judges, and when it closed and was sealed (SEAL-5). |
| `predicate.schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version of this seal, MAJOR.MINOR: 1.0 for AEF 1.0. It sits in the predicate because in-toto fixes the statement's _type (VER-1). |
| `predicate.runId` | [id](common.md#id) | yes |  | run.json's runId (SEAL-5). |
| `predicate.runHash` | [sha256Hex](common.md#sha256hex) | yes |  | SHA-256 of the manifest: one line per sealed file, ordered by the UTF-8 bytes of its path, '<sha256-hex>  <size>  <path>\n'. |
| `predicate.producer` | object | yes |  | The producer, as run.json gives it (SEAL-5). |
| `predicate.producer.name` | string | yes | ≥ 1 chars; ≤ 128 chars | run.json's producer.name (SEAL-5). |
| `predicate.producer.version` | [exactVersion](common.md#exactversion) | yes |  | run.json's producer.version (SEAL-5). |
| `predicate.subject` | object | yes |  | The subject, as run.json gives it (SEAL-5). |
| `predicate.subject.ref` | [ref](common.md#ref) | yes |  | run.json's subject.ref (SEAL-5). |
| `predicate.subject.version` | [exactVersion](common.md#exactversion) |  |  | run.json's subject.version, when it has one (SEAL-5). |
| `predicate.deployment` | null or object | yes |  | The deployment, as run.json gives it, or null (SEAL-5). |
| `predicate.suite` | null or object | yes |  | The suite, as run.json gives it, or null (SEAL-5). |
| `predicate.judges` | array of object | yes | ≤ 64 items | The judges, as run.json gives them and in the same order (SEAL-5). |
| `predicate.judges[].model` | string | yes | ≥ 1 chars; ≤ 256 chars | The judge's model, as in run.json (SEAL-5). |
| `predicate.judges[].rubricDigest` | [sha256Uri](common.md#sha256uri) |  |  | The judge's rubric digest, as in run.json (SEAL-5). |
| `predicate.closedAt` | [timestamp](common.md#timestamp) | yes |  | When the run closed: run.json's endedAt (SEAL-5). |
| `predicate.sealedBy` | one of `"producer"`, `"ingest"` | yes |  | producer: the producer sealed the run; ingest: a host sealed it when it took custody, which it does only for a run valid against the reader schemas (SEAL-1, SEAL-5). |
| `predicate.sealedAt` | [timestamp](common.md#timestamp) | yes |  | When the seal was made. |
