# 6. Runners

A **runner** executes an evaluation on someone's behalf: a local process, a CI job, a remote service. It is given a
**plan**, reports an **event stream**, and produces sealed runs that carry the plan's provenance ([RUN-12]). A
verifier can then check that the stream, and the runs it names, keep to the plan ([STRM-3], [STRM-4]).

## 6.1 Run plans

A run plan (schema `run-plan`) is what a runner is asked to evaluate:

- **[PLAN-1]** the exact subject version ([CKP-1]); the deployment it answers in (`deployment`, a reference) and its
  `endpoint`, which **MUST NOT** carry credentials: scheme, host and path only, as `run.json`'s `deployment.endpoint`
  ([RUN-10]: no user information, no query string, no fragment); and for a container run the image as its OCI image
  manifest digest with the repository it is pulled from;
- the suites, each with an exact version, the `digest` of its content when it is frozen, and the lane it serves
  ([PLAN-8]);
- **[PLAN-2]** the limits the runner **MUST** stop at: `maxUsd` always; `cases` and `timeout` (a duration, [ENC-9]) when
  set;
- the content capture (`contentCapture`: `off` or `on`, what text the runs keep, as in `run.json`, [RUN-11]), the
  isolation (`process`, `container`, `remote-zone` with its `zone`), and the provider (`local`, `docker`, `k8s`,
  `ci:<name>`);
- the target mode (`targetMode`): how the runs drive the target, as `run.json`'s `execution.targetMode` ([RUN-7]):
  `live`, `replayed`, `scripted` or `mocked`. A plan without one asks for `live`. Every run the job produces has the
  plan's target mode ([STRM-4]);
- the judges (model, provider, rubric digest), the baseline a comparison lane uses (a policy, or one sealed run by id
  and run hash) and the comparability axes, so a runner cannot pick them;
- the tags a runner must carry (`runnerSelector`);
- **[PLAN-3]** the credentials it needs, **as references only**: each has the environment variable it is given as
  (`name`), where its value lives (`scheme`: `env`, `keychain`, `vault`) and under what (`path`), and what it is for
  (`purpose`: `subject`, `judge`, `attacker`, `evaluator`, `other`). The runner resolves each where it runs and gives
  the value to the process the purpose names, as that environment variable; it **MUST NOT** write a value into any AEF
  file or event. It resolves them before `job.accepted`: a credential it cannot resolve is a refusal (`job.refused`),
  not a job that fails later.
- **[PLAN-4]** A plan **MUST NOT** hold a secret value. The schema refuses what is visibly not a reference (a bare
  string, an endpoint with user information, a query string or a fragment), but it cannot see a secret written as a
  reference's `path` or put in `ext`: a producer **MUST NOT** write one there.
- **[PLAN-5]** A plan is identified by its `planId` and its bytes: `planDigest` is the SHA-256 of the plan file's exact
  bytes. A runner that accepts a plan records both (§6.4, [RUN-12]).
- Isolation is the runner's claim: AEF records the isolation the plan asked for, and that the runner accepted the
  plan (`job.accepted`, and each run's `provenance`), but cannot check that it was kept.

## 6.2 Runner capability manifests

- **[PLAN-6]** A runner capability manifest (schema `runner`) says what a runner is: its id and workload identity (and
  the id of the key it signs sealed evidence with, if any: a hint for the reader, never a trust decision, [SIG-4]), its
  kind (`local`, `remote`, `ci`, `pool`), OS, runtime, the providers it supports, its tags, GPU and network zone, and
  its version.

## 6.3 Matching

- **[PLAN-7]** A runner **can take** a plan when it carries every tag of the plan's `runnerSelector`, supports the
  plan's provider, and, for a `remote-zone` plan, has the plan's zone as its `networkZone`. It **takes** the plan when
  it can take it, knows the plan's provider, isolation, content capture, target mode, and every credential's scheme
  and purpose, and can drive the target as the plan asks (`targetMode`): a runner that cannot does not take the plan.
  A runner **MUST** refuse (`job.refused`) a plan it does not take. `matching` vectors ask whether a runner takes a
  plan as far as its manifest tells: a manifest does not say which target modes a runner can give.
  - A plan the reader schema refuses is refused (`job.refused`) when its `planId` can be read: the plan is a JSON
    object, and its `planId` is valid against the schema's `id`. Otherwise the runner writes no stream and reports an
    input error.
- `conformance/protocol/matching/` holds plan and runner pairs with the expected answer.

## 6.4 The event stream

A runner reports a job as NDJSON (§2.2), one event per line (schema `runner-event`, a union on `kind`): a local runner
on its standard output or to a file the caller names, a remote one over its channel.

| `kind` | Carries |
|---|---|
| `job.accepted` | the `planId`, the SHA-256 of the plan's bytes (`planDigest`), the `runnerId` |
| `job.refused` | the same, and the `reason`: the runner will not run this plan |
| `plan.estimated` | the `cases` (those of the plan's suites, at most its `cases` limit) and the cost range (`usdLow`, `usdHigh`) with the price table |
| `spend.updated` | `spentUsd`: the total so far |
| `case.completed` | the `caseId` and its `state` |
| `lane.completed` | the `lane` and its `status`; only from a runner given the checkpoint's rules (§6.5) |
| `evidence.produced` | a sealed run: its `runId` and `runHash` |
| `job.cancelled` | the `reason` |
| `job.failed` | the `reason`, the `limit` (`maxUsd`, `cases`, `timeout`) when the runner stopped at one, and the `runs` it had sealed |
| `job.sealed` | every run the job produced (`runs`) |

- **[STRM-1]** Every event has `seq`, `jobId`, `at` and **MAY** carry `ext`. `job.sealed`, `job.failed`,
  `job.cancelled` and `job.refused` are terminal; a later minor **MAY** add other kinds but never a terminal one, and a
  verifier skips the rules of a kind it does not know. A runner that stops at a limit ends with `job.failed` naming the
  limit and the runs it sealed before stopping ([PLAN-9]).
- **[STRM-2]** A stream whose last line does not end in LF is still being written: it is not a finished stream
  ([ENC-7]).
- **[STRM-3]** A stream verifier checks a finished stream, given its plan and the digest of the plan file's bytes, line
  by line, and reports per event (`event:<n>`, the 1-based line), in event order and then by code (its bytes):

  | Code | When |
  |---|---|
  | `first` | the first event is not `job.accepted` or `job.refused` |
  | `seq` | `seq` is not the previous event's plus one (the first is 1); a gap is reported once, and the count goes on from the value written |
  | `job-id` | the `jobId` is not the first event's |
  | `time` | `at` is earlier than the previous event's, compared at full precision |
  | `over-time` | the first event later than the first event's `at` plus the plan's `timeout` (reported once) |
  | `after-terminal` | an event after a terminal one |
  | `accepted-twice` | a second `job.accepted` |
  | `plan-id` | `job.accepted` or `job.refused` for another plan id than the plan given |
  | `plan-digest` | `job.accepted` or `job.refused` with another `planDigest` than the SHA-256 of the plan's bytes |
  | `estimate` | `usdLow` above `usdHigh` |
  | `spend-decreased` | `spentUsd` below the previous `spend.updated` (spend is cumulative: the previous value, not the highest) |
  | `over-budget` | `spentUsd` above the plan's `maxUsd` (equal is within it) |
  | `over-cases` | the first `case.completed` past the plan's `cases` |
  | `run-hash-changed` | `evidence.produced` for a run already announced, with another run hash |
  | `unannounced-run` | `job.sealed` or `job.failed` naming a run no `evidence.produced` announced |
  | `unsealed-run` | `job.sealed` or `job.failed` not naming a run that was announced |
  | `no-terminal` | at path `stream`, after every event's problems: a finished stream with no terminal event |
  | `event-invalid` | a line that is not an I-JSON object within the limits of [ENC-17] valid against the reader `runner-event` schema. It takes no part in the other checks: the first event is the first valid one, and the line after it is not checked for `seq` |

  A stream whose finished lines break [ENC-5] or [ENC-7] (a CR, a blank line, a byte-order mark) is reported once as
  `encoding` at `stream` and not checked further; a stream of more lines than [ENC-17] allows, as one `limit` at
  `stream`.
- **[STRM-4] Plan conformance.** Given also the runs the job produced (a folder of runs, found by their `run.json`,
  [RUN-1]) and, optionally, a trust policy, a stream verifier checks that the runs the `job.sealed` and `job.failed`
  events name are the runs the plan asked for: of what, where, how, within which limits, and made by this job, between
  its acceptance and its end. Each run is checked once, however often it is named. The run is the
  run folder whose `run.json` has its `runId`, whose run hash ([SEAL-4]: its seal's, or, without a seal, recomputed
  from its files) is the one the first `evidence.produced` for that `runId` announced, and that is intact (§4.5; a
  blob withheld by a redaction the trust policy authorizes, [OVL-10], leaves it intact). When no folder has the
  `runId`, the problem is `run-missing`; when folders have it but none is that run, or no `evidence.produced`
  announced the `runId`, it is `run-hash`. Either is the run's only problem: its other rules would be checked against
  files the runner did not announce. The other runs, the runs **found**, are each checked against the plan at
  `run:<runId>`, and together against the plan's limits at `job`: a runner cannot pass by splitting its work across
  runs. Problems are ordered as §3.9 orders them: by path (its UTF-8 bytes, so `job` first), then by code (its
  bytes):

  | Code | When |
  |---|---|
  | `content-capture` | `contentCapture`, read as [RUN-11] and [VER-8] read it (absent or unknown is `on`), is not the plan's |
  | `deployment` | the plan's `subject` names a `deployment` and the run's `deployment.ref` is not it, or names an `endpoint` and the run's `deployment.endpoint` is not it (an absent value is not it) |
  | `judges` | the plan's `judges` is not empty, and the run's is not the same list: each `model` and `rubricDigest`, in order (an absent value equals only an absent value) |
  | `no-cost` | at `run:<runId>`: a run found whose `summary.json` has no `cost.totalUsd`; the budget cannot be checked without it, so a runner cannot stay under it by leaving cost out |
  | `over-budget` | at `job`, once: the sum of `summary.json`'s `cost.totalUsd` over the runs found, computed exactly and rounded once ([SUM-5]), is above the plan's `maxUsd` (equal is within it), whatever order the runs are named in |
  | `over-cases` | at `job`, once: the plan sets `cases`, and the runs found have more: the distinct `caseId`s of their result lines without `parentResultId`, whatever their state, counted across the whole job (a `caseId` in two runs counts once, so a runner keeps case ids apart across suites, [PLAN-8]) |
  | `provenance` | `provenance` ([RUN-12]) is not the `planId`, `planDigest`, `jobId` and `runnerId` of the stream's first `job.accepted`, or the stream has none |
  | `run-hash` | folders have the `runId`, but none is intact with the run hash announced for it, or none was announced |
  | `run-missing` | no folder has the `runId` |
  | `subject` | `subject.ref` or `subject.version` is not the plan's |
  | `suite` | `suite` is none of the plan's suites: the same `ref` and `version`, and, when the plan's suite names a `digest`, that `digest` |
  | `target-mode` | `execution.targetMode` is not the plan's `targetMode` (`live` when the plan has none), compared as written ([RUN-7]) |
  | `time` | `startedAt` is before the `at` of the stream's first `job.accepted`, or `endedAt` is after the `at` of its first terminal event, compared at full precision ([ENC-8]). A run that started before the job was accepted is evidence the runner had before it was asked: adopted, not produced, and a plan does not authorize adopting runs. Each half is checked only when both its times exist: a stream with no `job.accepted` is a `provenance` problem, and one with no terminal event is still open |

  [STRM-3] ties the stream to the plan's bytes and checks the spend and cases the job reported; [STRM-4] ties each run
  the stream names to the stream, and checks the runs themselves against the plan. A check of a job runs both.
- `conformance/protocol/` holds plans, runner manifests, matching pairs, and streams with the problems written by hand,
  including the cases a plausible wrong implementation gets wrong (a gap followed by more events, times a nanosecond
  apart, spend equal to the budget, a decrease followed by a rise, an unknown kind mid-stream, a terminal event still
  being written). Its `plan-conformance/` holds streams with their plan and the runs they name, with the problems of
  [STRM-4] and the same kind of cases (a run named twice, two folders with one `runId`, a run edited and sealed again,
  a run without a seal, a redacted run with and without the policy that authorizes it, the job's cost and cases
  exactly at the limit, runs each within the limits whose sum is not, a case with a child line or repeated trials,
  runs that start or end exactly at the job's edges, runs a nanosecond outside them, and target modes: scripted runs
  on a plan that asks for `scripted`, a live run on it, a scripted run on a plan that asks for `live`, and a mocked run
  on one that names no mode).
- **Not in 1.0:** signed jobs a remote runner pulls from a queue (so it can check who sent a plan). A later minor adds
  them; until then a remote runner authenticates its channel by other means.

## 6.5 Running a job

What a runner does with a plan it takes, from resolving its suites to sealing its runs, and what it writes where the
plan says nothing.

- **[PLAN-8] Suites and runs.** A runner resolves each suite of the plan by its `ref` and `version`. When the plan
  gives the suite's `digest`, the runner **MUST** check that the content it resolved has that digest, and refuse the
  job when it does not: with `job.refused` when it finds the mismatch before `job.accepted`, with `job.failed` (no
  `limit`, naming the runs it sealed, [STRM-1]) when it finds it later. It runs each suite as one run, and a run names
  one suite ([RUN-8]), with the plan's `digest` when the plan gives one. Case ids are unique within the job, across
  its suites: when two suites share case ids, the runner prefixes them (with the suite's `ref` and `version`, for
  example), because [STRM-4] counts the distinct `caseId`s of the whole job.
- **[PLAN-9] Limits.** A runner checks the plan's limits before each case, in [PLAN-2]'s order (`maxUsd`, `cases`,
  `timeout`), and stops at the first one the case would pass. The timeout includes closing and sealing the runs: the
  job's terminal event is within it ([STRM-3] `over-time`). A run a limit cuts off is closed `aborted` ([RUN-5]),
  sealed, announced and named in `job.failed` with the limit ([STRM-1]). The cases not run get no result lines, not
  `skipped` ones: an absent case is not a typed absence of the run ([RES-1]).
- `plan.estimated`'s `cases` is the number of cases of the plan's suites, at most the plan's `cases` limit when it sets
  one.
- A runner emits `lane.completed` only when it was given the checkpoint's rules (spec 05). A plan alone does not carry
  them, so a runner given only a plan reports no lane.
- **[PLAN-10] What a plan does not say.** A runner writes into each run what the plan gives ([STRM-4]) and the job's
  `provenance` ([RUN-12]). Two fields `run.json` may need are not always in the plan; a runner **SHOULD** derive them
  as below, so that two runners given one plan write the same bytes (`deployment.ref` is a comparability axis,
  [LANE-6]):
  - `subject.kind` is the kind of `subject.ref` (the part before its colon) when that is one of `subject.kind`'s
    values, and `other` otherwise;
  - when the plan names an `endpoint` and no `deployment`, `deployment.ref` is `endpoint:` followed by the endpoint
    encoded as [ENC-13] encodes a name: `endpoint:http://localhost:5080/v1`.
- Job and run ids are the runner's choice: any ids valid against the schema, with each `runId` unique as [RUN-13]
  requires and one `jobId` for every event of a job ([STRM-3]).
