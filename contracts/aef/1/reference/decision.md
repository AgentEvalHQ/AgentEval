# AEF 1.0: the checkpoint decision function's input and output

*Generated from [`schemas/writer/decision.schema.json`](../schemas/writer/decision.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

Decide(input) -> output is pure: no I/O, no clock (the evaluation time is an input). Its rules are DEC-1 to DEC-5 and its vectors are in conformance/decision-vectors/. This schema is the output; $defs/input is the input.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `outcome` | one of `"approved"`, `"approved_with_exceptions"`, `"blocked"`, `"inconclusive"`, `"expired"` | yes |  | expired (superseded, or a lane is stale); blocked (a blocking lane failed and is not waived); inconclusive (a lane is missing, not_measured or incomparable); approved_with_exceptions (otherwise, when an exception waived a lane); or approved (DEC-3). |
| `lanes` | array of object | yes | ≤ 64 items | Each input lane's status, in the input's order (DEC-2). |
| `lanes[].lane` | [laneName](common.md#lanename) | yes |  | The lane's name (DEC-2). |
| `lanes[].status` | [laneStatus](#lanestatus) | yes |  | The lane's status under the decision rules (DEC-2). |
| `lanes[].blocking` | boolean | yes |  | Whether the lane blocks the release when it fails, as in the input (DEC-3). |
| `lanes[].axes` | array of string |  | ≤ 16 items | For incomparable: the comparability axes that differ. |
| `reasons` | array of string | yes | ≤ 256 items | Reason codes, so every implementation produces the same list: one per lane that did not pass, advisory-failed and waived included, in the input's lane order; a failed lane none of whose exceptions applies is followed by exception-expired:<lane> when one is for its evidence but not in force, then exception-other-evidence:<lane> when one is for other evidence; then superseded:<version> when it applies; then outcome:<outcome> (DEC-2, DEC-4). |

## Definitions

### laneStatus

passed / failed: the lane's rule held or did not on its evidence. missing: no evidence for this exact version at the evaluation time (none, another version's, or evidence that did not exist yet). not_measured: the rule could not decide on the evidence, for example nothing was measured or a run is not eligible. incomparable: a comparison's runs differ on a required axis. stale: older than the lane's freshness at the evaluation time. waived: failed, with an exception for exactly this evidence in force at the evaluation time; only a failure is ever waived (DEC-2, LANE-1).

Type: one of `"passed"`, `"failed"`, `"missing"`, `"not_measured"`, `"incomparable"`, `"stale"`, `"waived"`

### input

The decision function's input: the exact version, the evaluation time, an optional newer version, each lane's result and the run hashes it came from, and any exceptions. At least one lane, no lane twice, and every exception for a lane of the input, with at least one run hash, expiring after it is granted (DEC-1).

Type: object

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `subjectVersion` | [exactVersion](common.md#exactversion) | yes |  | The checkpoint's exact subject version. |
| `evaluatedAt` | [timestamp](common.md#timestamp) | yes |  | The time the decision is made for. Freshness, future evidence and whether an exception is in force are judged against it (DEC-1, DEC-2). |
| `supersededBy` | null or [exactVersion](common.md#exactversion) |  |  | A newer subject version known at the evaluation time, if any. When set and different from subjectVersion, the outcome is expired (DEC-3). |
| `lanes` | array of object | yes | ≥ 1 items; ≤ 64 items | The lanes, at least one and none twice; the reasons follow this order (DEC-1, DEC-4). |
| `lanes[].lane` | [laneName](common.md#lanename) | yes |  | The lane's name (DEC-1). |
| `lanes[].blocking` | boolean | yes |  | Whether a failure of this lane blocks the release (DEC-3). |
| `lanes[].freshness` | [duration](common.md#duration) |  |  | How old the lane's evidence may be. Without it, the lane never goes stale (DEC-2). |
| `lanes[].evidence` | array of [sha256Hex](common.md#sha256hex) |  | ≤ 1024 items; unique | The run hash of each of the lane's runs, each once, in ascending order: the exact sealed evidence of its result. Required with a result. An exception waives the lane only when its evidence is this same set; in a checkpoint it is the set of the lane's runs' run hashes (DEC-1, DEC-2, CKP-7). |
| `lanes[].result` | null or object | yes |  | What the lane's rule gave on its evidence (computed from the runs, outside this function), or null when there is no evidence. |
| `exceptions` | array of object |  | ≤ 256 items | Exceptions: each accepts the failure of named, sealed evidence until a time; it is not a policy. One for a failed lane's exact evidence and in force at evaluatedAt makes the lane waived; it never changes a missing, stale, not_measured or incomparable lane. A checkpoint records them in its decisionInput, so whoever signs the checkpoint vouches for them (DEC-1, DEC-2, CKP-5, CKP-9). |
| `exceptions[].lane` | [laneName](common.md#lanename) | yes |  | The lane it applies to: a lane of this input, or the function refuses the input (DEC-1). |
| `exceptions[].evidence` | array of [sha256Hex](common.md#sha256hex) | yes | ≥ 1 items; ≤ 1024 items; unique | The run hashes whose failure it accepts, each once, in ascending order; at least one, or the function refuses the input. It waives the lane only when this is the same set as the lane's evidence: a re-run has new run hashes, and is not accepted. In a checkpoint, every one is a run hash of the lane's runs (DEC-1, DEC-2, CKP-7). |
| `exceptions[].requirement` | [id](common.md#id) |  |  | The external requirement id whose risk it accepts, shown beside the waived lane; it takes no part in the decision (DEC-1, DEC-2). |
| `exceptions[].reason` | string | yes | ≥ 1 chars; ≤ 2048 chars | Why the risk is accepted, shown beside the waived lane (DEC-1, DEC-2). |
| `exceptions[].by` | [trustedIdentity](common.md#trustedidentity) | yes |  | Who granted it. A claim (CKP-6): the checkpoint's signature is what makes the exception attributable, so a reader shows by with the assurance of that signature (DEC-1, CKP-5, CKP-9). |
| `exceptions[].at` | [timestamp](common.md#timestamp) | yes |  | When it was granted: it is in force from this time, inclusive (DEC-2, DEC-5). |
| `exceptions[].expires` | [timestamp](common.md#timestamp) | yes |  | When it stops being in force: from this time on it waives nothing. Later than at, or the function refuses the input (DEC-1, DEC-2, CKP-10). |
