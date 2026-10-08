# AEF 1.0: checkpoint manifest

*Generated from [`schemas/writer/checkpoint.schema.json`](../schemas/writer/checkpoint.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

A release decision over several evidence lanes for one exact subject version: the lanes and their rules, the exact sealed runs each lane used (with their run hashes), the decision function's input and output once decided, and the state (CKP-1 to CKP-6).

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this document follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `checkpointId` | [id](common.md#id) | yes |  | The checkpoint's id. A manifest is conventionally stored as checkpoints/<checkpointId>.json. |
| `template` | object |  |  | The template the checkpoint was made from. |
| `template.ref` | [ref](common.md#ref) | yes |  | The template, as a typed reference. |
| `template.version` | [exactVersion](common.md#exactversion) | yes |  | The template's exact version. |
| `subject` | object | yes |  | The exact version decided on. 'latest' is resolved before anything runs, and recorded: resolvedFrom says what was asked, version what it resolved to. |
| `subject.ref` | [ref](common.md#ref) | yes |  | The subject decided on, as a typed reference (CKP-1). |
| `subject.version` | [exactVersion](common.md#exactversion) | yes |  | The exact version decided on; never latest (CKP-1). |
| `subject.image` | [sha256Uri](common.md#sha256uri) |  |  | The subject's OCI image manifest digest, when it is evaluated as an image. |
| `subject.deployment` | [ref](common.md#ref) |  |  | Where the subject is deployed, as a typed reference. |
| `subject.resolvedFrom` | string |  | ≤ 256 chars | What was asked for, such as latest, before it was resolved to version (CKP-1). |
| `subject.resolvedAt` | [timestamp](common.md#timestamp) |  |  | When the version was resolved. |
| `lanes` | array of object | yes | ≥ 1 items; ≤ 64 items | The evidence lanes, at least one, each named once (CKP-2). |
| `lanes[].lane` | [laneName](common.md#lanename) | yes |  | The lane's name, unique in the manifest (CKP-2). |
| `lanes[].rule` | [laneRule](#lanerule) | yes |  | How the lane's result follows from its runs (CKP-2). |
| `lanes[].requirements` | array of [id](common.md#id) |  | ≤ 256 items | The external requirement ids the lane answers (CKP-2). |
| `lanes[].runs` | array of [runRef](common.md#runref) | yes | ≤ 1024 items | The exact sealed runs this lane used; empty while pending. |
| `lanes[].blocking` | boolean | yes |  | A failed blocking lane blocks the release; a failed advisory lane is reported, never ignored. |
| `lanes[].freshness` | [duration](common.md#duration) |  |  | The shortest freshness among the lane's requirements, resolved when the checkpoint was planned. |
| `budget` | object |  |  | What the checkpoint may spend, what it spent, and who approved it (CKP-6). |
| `budget.approvedUsd` | number |  | ≥ 0 | The approved budget, in US dollars. |
| `budget.spentUsd` | number |  | ≥ 0 | What has been spent so far, in US dollars. |
| `budget.approvedBy` | [trustedIdentity](common.md#trustedidentity) |  |  | Who approved the budget. A claim: a reader shows its assurance only as far as it verified it (CKP-6). |
| `state` | one of `"draft"`, `"planned"`, `"approved_to_spend"`, `"running"`, `"evidence_complete"`, `"decided"` | yes |  | draft, planned, approved_to_spend, running, evidence_complete, then decided, in that order. An abandoned checkpoint moves to decided with the outcome aborted (CKP-4). |
| `outcome` | null or one of `"approved"`, `"approved_with_exceptions"`, `"blocked"`, `"inconclusive"`, `"expired"`, `"aborted"` |  |  | Set from state decided on; null before. approved, approved_with_exceptions, blocked, inconclusive or expired from the decision function (DEC-3); aborted when the checkpoint was abandoned (abortReason says why), never from the decision function (CKP-4). |
| `decision` | [decision](decision.md) |  |  | The decision function's output, recorded when the state became decided. |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |
| `abortReason` | string |  | ≥ 1 chars; ≤ 2048 chars | Why the checkpoint was abandoned. Required with the outcome aborted (CKP-4). |
| `decisionInput` | [input](decision.md#input) |  |  | The decision function's input, recorded with its output so anyone can recompute it. Each lane's evidence is the set of its runs' run hashes, and each exception names only run hashes of its lane's runs; its exceptions are recorded here, so whoever signs the checkpoint vouches for them (CKP-4, CKP-5, CKP-7, CKP-9, DEC-1). |

- **Rule:** Decided by the decision function: its input and output are recorded.
- **Rule:** Abandoned: it says why, and records no decision.
- **Rule:** From decided on, there is an outcome.
- **Rule:** Before the decision, there is none.

## Definitions

### laneRule

What kind of rule a lane applies. A family with no published pass threshold takes comparison or evidence-present, never threshold.

Type: object or object or object or object
