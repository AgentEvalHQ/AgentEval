# AEF 1.0: the checkpoint decision function's input and output

*Generated from [`schemas/writer/decision.schema.json`](../schemas/writer/decision.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

Decide(input) -> output is pure: no I/O, no clock (the evaluation time is an input). Its rules are DEC-1 to DEC-5 and its vectors are in conformance/decision-vectors/. This schema is the output; $defs/input is the input.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `outcome` | one of `"approved"`, `"blocked"`, `"inconclusive"`, `"expired"` | yes |  | approved; blocked (a blocking lane failed); inconclusive (a lane is missing, not_measured or incomparable); or expired (superseded, or a lane is stale) (DEC-3). |
| `lanes` | array of object | yes | ≤ 64 items | Each input lane's status, in the input's order (DEC-2). |
| `lanes[].lane` | [laneName](common.md#lanename) | yes |  | The lane's name (DEC-2). |
| `lanes[].status` | [laneStatus](#lanestatus) | yes |  | The lane's status under the decision rules (DEC-2). |
| `lanes[].blocking` | boolean | yes |  | Whether the lane blocks the release when it fails, as in the input (DEC-3). |
| `lanes[].axes` | array of string |  | ≤ 16 items | For incomparable: the comparability axes that differ. |
| `reasons` | array of string | yes | ≤ 256 items | Reason codes, so every implementation produces the same list: one per lane that did not pass, advisory-failed included, in the input's lane order; then superseded:<version> when it applies; then outcome:<outcome> (DEC-4). |

## Definitions

### laneStatus

passed / failed: the lane's rule held or did not on its evidence. missing: no evidence for this exact version at the evaluation time (none, another version's, or evidence that did not exist yet). not_measured: the rule could not decide on the evidence, for example nothing was measured or a run is not eligible. incomparable: a comparison's runs differ on a required axis. stale: older than the lane's freshness at the evaluation time (DEC-2, LANE-1).

Type: one of `"passed"`, `"failed"`, `"missing"`, `"not_measured"`, `"incomparable"`, `"stale"`

### input

The decision function's input: the exact version, the evaluation time, an optional newer version, and each lane's result. At least one lane and no lane twice (DEC-1).

Type: object

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `subjectVersion` | [exactVersion](common.md#exactversion) | yes |  | The checkpoint's exact subject version. |
| `evaluatedAt` | [timestamp](common.md#timestamp) | yes |  | The time the decision is made for. Freshness and future evidence are judged against it (DEC-1, DEC-2). |
| `supersededBy` | null or [exactVersion](common.md#exactversion) |  |  | A newer subject version known at the evaluation time, if any. When set and different from subjectVersion, the outcome is expired (DEC-3). |
| `lanes` | array of object | yes | ≥ 1 items; ≤ 64 items | The lanes, at least one and none twice; the reasons follow this order (DEC-1, DEC-4). |
| `lanes[].lane` | [laneName](common.md#lanename) | yes |  | The lane's name (DEC-1). |
| `lanes[].blocking` | boolean | yes |  | Whether a failure of this lane blocks the release (DEC-3). |
| `lanes[].freshness` | [duration](common.md#duration) |  |  | How old the lane's evidence may be. Without it, the lane never goes stale (DEC-2). |
| `lanes[].result` | null or object | yes |  | What the lane's rule gave on its evidence (computed from the runs, outside this function), or null when there is no evidence. |
