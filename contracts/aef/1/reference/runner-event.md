# AEF 1.0: one line of a runner event stream

*Generated from [`schemas/writer/runner-event.schema.json`](../schemas/writer/runner-event.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

A runner reports a job as NDJSON events, each with a sequence number. The stream's own rules (order, one terminal event, spend within the plan) are STRM-1 to STRM-3; STRM-4 checks the runs it names against the plan.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [integer](common.md#integer) | yes | ≥ 1 | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [id](common.md#id) | yes |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [timestamp](common.md#timestamp) | yes |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

## Kinds

### `job.accepted`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"job.accepted"` | yes |  | job.accepted: the runner accepted the plan. A stream starts with job.accepted or job.refused (STRM-3). |
| `planId` | [id](common.md#id) | yes |  | The id of the plan accepted; it must be the given plan's (STRM-3). |
| `runnerId` | [id](common.md#id) | yes |  | The runner that accepted the plan. |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |
| `planDigest` | [sha256Hex](common.md#sha256hex) | yes |  | SHA-256 of the exact bytes of the plan the runner accepted. |

### `plan.estimated`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"plan.estimated"` | yes |  | plan.estimated: the runner's estimate of cases and cost. |
| `cases` | integer | yes | ≥ 0; ≤ 9007199254740991 | How many cases the runner expects to run: the cases of the plan's suites, at most the plan's cases limit when it sets one. |
| `usdLow` | number | yes | ≥ 0 | The low end of the estimated cost, in US dollars; never above usdHigh (STRM-3). |
| `usdHigh` | number | yes | ≥ 0 | The high end of the estimated cost, in US dollars (STRM-3). |
| `priceTable` | string |  | ≤ 64 chars | The price table the estimate used, by name. |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

### `spend.updated`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"spend.updated"` | yes |  | spend.updated: the job's total spend so far (STRM-3). |
| `spentUsd` | number | yes | ≥ 0 | Total spent so far: never decreases. |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

### `case.completed`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"case.completed"` | yes |  | case.completed: a case finished; the plan's cases limit counts these (STRM-3). |
| `caseId` | any | yes | ≥ 1 chars; ≤ 256 chars | The case that finished. |
| `state` | [state](common.md#state) | yes |  | The case's state (RES-1). |
| `runId` | [id](common.md#id) |  |  | The run the case is a case of. A runner SHOULD name it: two suites of a job may share case ids (PLAN-8). |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

### `lane.completed`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"lane.completed"` | yes |  | lane.completed: a lane finished. Only from a runner given the checkpoint's rules (spec 05): a plan alone does not carry them. |
| `lane` | [laneName](common.md#lanename) | yes |  | The lane that finished. |
| `status` | one of `"passed"`, `"failed"`, `"not_measured"`, `"incomparable"` | yes |  | The lane's result: passed, failed, not_measured or incomparable. |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

### `evidence.produced`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"evidence.produced"` | yes |  | evidence.produced: the runner sealed a run (STRM-3). |
| `runId` | [id](common.md#id) | yes |  | The sealed run's runId. Announcing it again with another run hash is reported (STRM-3). |
| `runHash` | [sha256Hex](common.md#sha256hex) | yes |  | The sealed run's run hash (SEAL-4): the run the stream names is the one with this hash (STRM-4). |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

### `job.cancelled`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"job.cancelled"` | yes |  | job.cancelled: the job was cancelled. A terminal event (STRM-1). |
| `reason` | string | yes | ≥ 1 chars; ≤ 2048 chars | Why the job was cancelled. |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

### `job.failed`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"job.failed"` | yes |  | job.failed: the job failed or stopped at a plan limit. A terminal event (STRM-1). |
| `reason` | string | yes | ≥ 1 chars; ≤ 2048 chars | Why the job failed. |
| `limit` | one of `"maxUsd"`, `"cases"`, `"timeout"` |  |  | Set when the runner stopped at a plan limit. |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |
| `runs` | array of [id](common.md#id) |  | ≤ 1024 items | The runs it sealed before it stopped, a run a limit cut off (closed aborted) among them (PLAN-9). Each must keep to the plan (STRM-4). |

### `job.sealed`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `kind` | `"job.sealed"` | yes |  | job.sealed: the job finished and names every run it produced. A terminal event (STRM-1). |
| `runs` | array of [id](common.md#id) | yes | ≥ 1 items; ≤ 1024 items | Every run the job produced, sealed. Each must keep to the plan (STRM-4). |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |

### `job.refused`

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](#schemaversion) |  |  | The AEF version this event follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `seq` | [seq](#seq) |  |  | 1 for the first event, then each event one more than the previous one. |
| `jobId` | [jobId](#jobid) |  |  | The job the event reports on; every event of a stream has the first event's jobId (STRM-1, STRM-3). |
| `at` | [at](#at) |  |  | When the event happened; never earlier than the previous event's at (STRM-1, STRM-3). |
| `ext` | [ext](#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, STRM-1). |
| `kind` | `"job.refused"` | yes |  | job.refused: the runner will not run this plan. A terminal event (STRM-1, PLAN-7). |
| `planId` | [id](common.md#id) | yes |  | The id of the plan refused; it must be the given plan's (STRM-3). |
| `planDigest` | [sha256Hex](common.md#sha256hex) | yes |  | The SHA-256 of the exact bytes of the plan refused (STRM-3). |
| `runnerId` | [id](common.md#id) | yes |  | The runner that refused the plan. |
| `reason` | string | yes | ≥ 1 chars; ≤ 2048 chars | Why the runner refused the plan (PLAN-7). |
