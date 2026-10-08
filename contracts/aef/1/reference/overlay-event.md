# AEF 1.0: one line of overlays/events.ndjson

*Generated from [`schemas/writer/overlay-event.schema.json`](../schemas/writer/overlay-event.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

Everything after a run closed: a human decision, an adjudication, an annotation, a waiver. Appended, never edited; the run's own sealed files never change.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this line follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `eventId` | string | yes | pattern `^ov_[A-Za-z0-9]{1,64}$` | The event's id, unique in the events file: ov_ and then 1-64 letters or digits (OVL-1). |
| `kind` | one of `"approve"`, `"reject"`, `"override"`, `"adjudicate"`, `"acknowledge"`, `"accept_baseline"`, `"waive"`, `"annotate"`, `"redact"` | yes |  | What the event does: approve or reject sets a review status; override or adjudicate sets a result's effective state; waive waives until expires; redact withholds a blob; acknowledge, accept_baseline and annotate are recorded with no effect (OVL-1). |
| `target` | object | yes |  | What the event is about, always inside its own run (spec 04, OVL-2). |
| `target.run` | [id](common.md#id) | yes |  | The run's own runId. |
| `target.runHash` | [sha256Hex](common.md#sha256hex) |  |  | The run's run hash. |
| `target.result` | [resultId](common.md#resultid) |  |  | The result the event is about; it must be a result of the run (OVL-2). |
| `target.requirement` | [id](common.md#id) |  |  | The requirement the event is about, by its external id (OVL-1). |
| `target.blob` | [sha256Hex](common.md#sha256hex) |  |  | For redact: the SHA-256 of the blob withheld. |
| `state` | [state](common.md#state) |  |  | The state an override or adjudication sets on its result. |
| `reason` | string |  | ≥ 1 chars; ≤ 4096 chars | Why. Required for override, adjudicate, waive and redact (OVL-1). |
| `expires` | [timestamp](common.md#timestamp) |  |  | When a waiver ends. Required for waive (OVL-9). |
| `by` | [trustedIdentity](common.md#trustedidentity) | yes |  | Who wrote the event, and the assurance the writer claims (OVL-1, OVL-3). |
| `at` | [timestamp](common.md#timestamp) | yes |  | When the event was written. Shown, never used to reorder events (OVL-1, OVL-6). |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |

- **Rule:** A waiver always says why and until when.
- **Rule:** An override or adjudication names the result, the state it sets, and why.
- **Rule:** A redaction names the blob and says why.
