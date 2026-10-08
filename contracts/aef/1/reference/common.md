# AEF 1.0: shared definitions

*Generated from [`schemas/writer/common.schema.json`](../schemas/writer/common.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

Definitions the other AEF 1.0 schemas reference. Not a document of its own.

## Definitions

### schemaVersion

MAJOR.MINOR. A writer emits the version it implements; a reader accepts any minor of a major it knows and refuses an unknown major.

Type: `"1.0"`

### id

An identifier: 1-128 characters, letters, digits and . _ : -

Type: string. Bounds: pattern `^[A-Za-z0-9._:-]{1,128}$`

### ref

A typed reference, kind:name (for example agent:support/support-triage, suite:support/triage-scenarios).

Type: string. Bounds: pattern `^[a-z][a-z0-9-]*:[!-~]{1,256}$`

### sha256Hex

A SHA-256 digest: 64 lower-case hex characters (the in-toto digest form).

Type: string. Bounds: ≤ 64 chars; pattern `^[0-9a-f]{64}$`

### sha256Uri

A SHA-256 digest with its algorithm: sha256:<64 lower-case hex>.

Type: string. Bounds: ≤ 71 chars; pattern `^sha256:[0-9a-f]{64}$`

### timestamp

An RFC 3339 date-time in UTC, ending in Z, with up to nine fraction digits. The pattern is the rule; format is an annotation.

Type: string. Bounds: pattern `^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,9})?Z$`

### resultId

r_ + the first 32 lower-case hex characters of SHA-256 over runId, caseId, path and trial joined by U+001F, where an absent trial is the empty string (RES-4).

Type: string. Bounds: ≤ 34 chars; pattern `^r_[0-9a-f]{32}$`

### evidenceId

The id of an evidence record, unique in its run: E- and then 1-64 letters, digits, '.', '_' or '-' (EVD-1).

Type: string. Bounds: pattern `^E-[A-Za-z0-9._-]{1,64}$`

### state

A node's state (RES-1): passed (the only pass), failed, warn (a soft failure), inconclusive (measured, undecided), scored (measured, no pass/fail rule applied), and the typed absences not_measured, not_applicable, skipped, error and pending. Closed for major 1 (VER-9).

Type: one of `"passed"`, `"failed"`, `"warn"`, `"inconclusive"`, `"scored"`, `"not_measured"`, `"not_applicable"`, `"skipped"`, `"error"`, `"pending"`

### trustedIdentity

An identity and the assurance its writer claims for it, as in an overlay event's by. A reader shows the assurance only as far as it verified it (OVL-3).

Type: object

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `identity` | string | yes | ≥ 1 chars; ≤ 256 chars | Who: a stable opaque identity is recommended (an OIDC subject, a SPIFFE id, a key id) rather than an e-mail address (spec 08, SEC-4). |
| `assurance` | one of `"self-attested"`, `"signed"`, `"authenticated"` | yes |  | What the writer claims: self-attested, signed (a batch signature verifies for this identity) or authenticated (a host authenticated it). A reader shows it only as far as it verified it (spec 04, OVL-3). |

### ext

Producer extensions. Readers ignore what they do not know; nothing in the contract depends on it.

Type: object

### text

A name with no control character (C0, DEL, C1): caseId and path, which a result id joins with U+001F.

Type: string. Bounds: pattern `^[^\u0000-\u001f\u007f-\u009f]+$`

### uri

A URI: a lower-case scheme, a colon, then printable ASCII without spaces; at most 2048 characters.

Type: string. Bounds: ≤ 2048 chars; pattern `^[a-z][a-z0-9+.-]*:[!-~]+$`

### exactVersion

An exact version: printable ASCII without spaces, at most 128 characters, compared byte for byte. 'latest' (any case) is never a version: it is resolved before anything runs.

Type: string. Bounds: pattern `^[!-~]{1,128}$`

### laneName

A lane: an id.

Type: [id](#id)

### duration

An ISO 8601 duration of days, hours and minutes (ENC-9): P, then optionally <n>D, then optionally T and <n>H, <n>M or both in that order; each n one to five digits; at least one part, and no T without one: P14D, PT36H, PT90M, P1DT12H30M.

Type: string. Bounds: pattern `^P(?:[0-9]{1,5}D(?:T(?:[0-9]{1,5}H(?:[0-9]{1,5}M)?|[0-9]{1,5}M))?|T(?:[0-9]{1,5}H(?:[0-9]{1,5}M)?|[0-9]{1,5}M))$`

### runRef

A sealed run a checkpoint lane used: its id, its run hash (frozen evidence), and where it came from.

Type: object

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `runId` | [id](#id) | yes |  | The run's runId, as its run.json gives it (CKP-2). |
| `runHash` | [sha256Hex](#sha256hex) | yes |  | The run's run hash, which freezes the exact sealed files the lane relied on (CKP-2). |
| `origin` | string | yes | pattern `^(launched\|adopted:[A-Za-z0-9._:-]{1,64})$` | launched: run for this checkpoint; adopted:<source>: an existing sealed run that matched the exact version and the comparability requirements. |

### relativePath

A path inside a run folder: segments of ASCII letters, digits, '.', '_' and '-' separated by single slashes; no leading slash; no segment that starts or ends with '.' (so no '.' or '..'); at most 255 bytes. Case clashes and Windows reserved names are a rule across files (spec 03, RUN-3).

Type: string. Bounds: ≤ 255 chars; pattern `^[A-Za-z0-9_-]([A-Za-z0-9._-]*[A-Za-z0-9_-])?(/[A-Za-z0-9_-]([A-Za-z0-9._-]*[A-Za-z0-9_-])?)*$`

### provider

Where a run executes: local, docker, k8s, or ci:<name> for a CI system. A runner refuses a plan whose provider it does not know or support (PLAN-7).

Type: one of `"local"`, `"docker"`, `"k8s"` or string

### integer

Written as plain digits; a reader takes an integral number such as 2.0 as 2.

Type: integer. Bounds: ≥ -9007199254740991; ≤ 9007199254740991

### severity

How bad a failure is, from none to critical (spec 03, RES-9). Where a rule needs the severity of a failure that has none, it is critical.

Type: one of `"none"`, `"low"`, `"medium"`, `"high"`, `"critical"`

### runPointer

One sealed run, exactly: its id and its run hash (spec 04, SEAL-4).

Type: object

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `runId` | [id](#id) | yes |  | The run's runId, as its run.json gives it. |
| `runHash` | [sha256Hex](#sha256hex) | yes |  | The run's run hash: the SHA-256 of its seal manifest, which identifies its exact content (SEAL-4). |

### axis

A comparability axis: a part of run.json two runs must agree on to be compared (spec 05, LANE-6).

Type: one of `"subject"`, `"suite"`, `"suite-content"`, `"judges"`, `"rubrics"`, `"target-mode"`, `"deployment"`, `"producer"`
