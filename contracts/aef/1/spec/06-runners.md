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
  the value to the process the purpose names, as that environment variable; it **MUST NOT** write a value, or a
  reference's `path`, into any AEF file or event: a path names where a secret lives, and may hold one by mistake
  ([PLAN-4]). It resolves every one before `job.accepted`, whether or not its target needs it: a credential it cannot
  resolve is a refusal (`job.refused`), not a job that fails later. A credential of scheme `env` resolves when the
  variable its `path` names is set in the runner's environment and not empty.
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
  kind (`local`, `remote`, `ci`, `pool`), OS, runtime, the providers it supports, its tags, GPU and network zone, the
  target modes it can give (`targetModes`, [RUN-7]; a manifest without them gives `live` only), and its version.

## 6.3 Matching

- **[PLAN-7]** A runner **can take** a plan when it carries every tag of the plan's `runnerSelector`, supports the
  plan's provider, gives the plan's target mode (one of its `targetModes`; a plan without one asks for `live`, and a
  manifest without them gives `live` only), and, for a `remote-zone` plan, has the plan's zone as its `networkZone`.
  It **takes** the plan when it can take it, knows the plan's provider, isolation, content capture, target mode, and
  every credential's scheme and purpose, and can run the job as the plan asks: resolve its suites ([PLAN-8]) and
  credentials ([PLAN-3]), and keep to its limits ([PLAN-9]). A runner **MUST** refuse (`job.refused`) a plan it does
  not take. `matching` vectors ask whether a runner takes a plan as far as its manifest tells; `job` vectors (spec 09)
  ask the rest.
  - A plan the reader schema refuses is refused (`job.refused`) when its `planId` can be read: the plan is a JSON
    object, and its `planId` is valid against the schema's `id`. Otherwise the runner writes no stream and reports an
    input error.
- `conformance/protocol/matching/` holds plan and runner pairs with the expected answer, target modes among them
  (a plan that names none, and a manifest that names none).

## 6.4 The event stream

A runner reports a job as NDJSON (§2.2), one event per line (schema `runner-event`, a union on `kind`): a local runner
on its standard output or to a file the caller names, a remote one over its channel.

| `kind` | Carries |
|---|---|
| `job.accepted` | the `planId`, the SHA-256 of the plan's bytes (`planDigest`), the `runnerId` |
| `job.refused` | the same, and the `reason`: the runner will not run this plan |
| `plan.estimated` | the `cases` (those of the plan's suites, at most its `cases` limit) and the cost range (`usdLow`, `usdHigh`) with the price table |
| `spend.updated` | `spentUsd`: the total so far |
| `case.completed` | the `caseId` and its `state`, and **SHOULD** name the run it is a case of (`runId`): two suites of a job may share case ids ([PLAN-8]) |
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
  | `judges` | the run's `judges`, in order, are not the plan's with some left out (none, or all, among them). A run's judge is a plan's when it has the plan judge's `model`, and its `provider` and `rubricDigest` wherever the plan judge names them: a plan judge that leaves one out leaves it to the runner, and the run names the provider that served and the rubric that graded (only `model` is always compared). A run's judges are the models that graded it ([RUN-9]): a run that names none is within; a plan that names none allows none ([PLAN-1]); and a plan that names one judge twice lets a run name it twice, never more |
  | `no-cost` | at `run:<runId>`: a run found whose `summary.json` has no `cost.totalUsd`; the budget cannot be checked without it, so a runner cannot stay under it by leaving cost out |
  | `over-budget` | at `job`, once: the sum of `summary.json`'s `cost.totalUsd` over the runs found, computed exactly and rounded once ([SUM-5]), is above the plan's `maxUsd` (equal is within it), whatever order the runs are named in |
  | `over-cases` | at `job`, once: the plan sets `cases`, and the runs found have more: the distinct cases of their result lines without `parentResultId`, whatever their state, counted across the whole job, a case being its run's `suite` (`ref` and `version`) with its `caseId`: one case id in two suites counts twice, and in two runs of one suite once ([PLAN-8]) |
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
  exactly at the limit, runs each within the limits whose sum is not, two suites that share case ids, a case with a
  child line or repeated trials, runs that start or end exactly at the job's edges, runs a nanosecond outside them,
  judges (none on a plan that names some, some of the plan's, one on a plan that names none, one it does not name,
  the plan's model and rubric under another provider, one model and rubric under two providers, the plan's out of
  order, one judge more often than the plan names it), and target modes: scripted runs on a plan that asks for
  `scripted`, a live run on it, a scripted run on a plan that asks for `live`, and a mocked run on one that names no
  mode).
- **Not in 1.0:** signed jobs a remote runner pulls from a queue (so it can check who sent a plan). A later minor adds
  them; until then a remote runner authenticates its channel by other means.

## 6.5 Running a job

What a runner does with a plan it takes, from resolving its suites to sealing its runs, and what it writes where the
plan says nothing.

- **[PLAN-8] Suites and runs.** A runner resolves each suite of the plan by its `ref` and `version`; a suite it
  cannot resolve is a refusal (`job.refused`). When the plan gives the suite's `digest`, the runner **MUST** check that
  the content it resolved has that digest, and refuse the job when it does not: with `job.refused` when it finds the
  mismatch before `job.accepted`, with `job.failed` (no `limit`, naming the runs it sealed, [STRM-1]) when it finds it
  later. It runs each suite as one run, and a run names one suite ([RUN-8]), with the plan's `digest` when the plan
  gives one and no `digest` otherwise: a digest a runner computed itself could differ between runners, and
  `suite.digest` is a comparability axis (`suite-content`, [LANE-6]). A run of a suite the plan gives a `lane` serves
  that lane: each of its root lines (no `parentResultId`) names it (`lane`), and its `summary.json` has the lane
  ([SUM-3], [LANE-2]), so that a checkpoint's lane can read the run. A plan that names one suite twice (the same `ref`
  and `version`) is refused (`job.refused`): a job runs a suite's cases once. Case ids are the suite's: a runner
  **MUST NOT** rewrite them, so that a case has one id in every run of its suite, whatever plan it runs in ([LANE-7]
  pairs a comparison's runs by case; [STRM-4] counts a job's cases with their suite).
- **[PLAN-9] Limits.** Before each case a runner checks the plan's limits, in [PLAN-2]'s order, against what the
  case **could** take, and stops at the first one it could pass:
  - `maxUsd`, when the spend so far plus the case's **cost bound** is above it (equal is within it). The spend is
    computed as [STRM-3] and [STRM-4] will compute it, with the cost bound added to the case's own run, and the runner
    stops when either sum is above `maxUsd`:
    - the job's spend: the costs of the cases run and of the runs' own costs, added exactly and rounded once
      ([SUM-5]);
    - the sum of the runs' costs: each run's `cost.totalUsd` (its cases' costs and any cost of its own, added exactly
      and rounded once), added exactly and rounded once;
  - `cases`, when the plan's number of cases have started;
  - `timeout`, when the time since `job.accepted`, plus the case's **time bound**, plus the time the runner keeps for
    closing and sealing the run, is above it.

  Cases may run concurrently, and a run may cost more than its cases. So that a runner that keeps to its bounds never
  passes a limit:
  - the spend so far includes the cost bound of every case started and not yet complete;
  - the time check holds for every case in flight: each, with closing and sealing its run, ends within the timeout;
  - a run's own cost (its setup, a judge's own calls) is bounded and checked as a case's is, before it is incurred.

  A case's bounds are the runner's own: its cost bound is the most the case can cost under the runner's price table
  and the limits it enforces on the target (a maximum of tokens or of steps, for example), and its time bound the
  deadline the runner enforces on the case. A runner that cannot bound a case's cost takes no plan, since every plan
  sets `maxUsd`; one that cannot enforce a deadline on a case does not take a plan that sets `timeout` ([PLAN-7]).
  `plan.estimated`'s `usdHigh` is the sum of the cost bounds of the cases it counts and of the runs' own costs. A
  runner that keeps to its bounds never passes a limit; a case, or a run's own cost, that came to more than its bound,
  or a case that took longer, is reported as the limit it passed ([STRM-3], [STRM-4]). The timeout includes closing
  and sealing the runs: the job's terminal event is within it ([STRM-3] `over-time`). A run a limit cuts off is closed
  `aborted` ([RUN-5]), sealed, announced and named in `job.failed` with the limit ([STRM-1]); a limit that stops the
  job before a suite's first case opens no run for that suite. The cases not run get no result lines, not `skipped`
  ones: an absent case is not a typed absence of the run ([RES-1]).
- `plan.estimated`'s `cases` is the number of cases of the plan's suites, at most the plan's `cases` limit when it sets
  one.
- A runner emits `lane.completed` only when it was given the checkpoint's rules (spec 05). A plan alone does not carry
  them, so a runner given only a plan reports no lane.
- **[PLAN-10] What a plan does not say.** A runner writes into each run what the plan gives ([STRM-4]) and the job's
  `provenance` ([RUN-12]). Two fields `run.json` may need are not always in the plan; a runner **MUST** derive them
  as below, so that two runners given one plan write the same values (`deployment.ref` is a comparability axis,
  [LANE-6], and the `job` vectors check both):
  - `subject.kind` is the kind of `subject.ref` (the part before its colon) when that is one of `subject.kind`'s
    values, and `other` otherwise;
  - when the plan names an `endpoint` and no `deployment`, `deployment.ref` is `endpoint:` followed by the endpoint
    encoded as [ENC-13] encodes a name: `endpoint:http://localhost:5080/v1`.
- Job and run ids are the runner's choice: any ids valid against the schema, with each `runId` unique as [RUN-13]
  requires and one `jobId` for every event of a job ([STRM-3]).
