# 6. Runners

A **runner** executes an evaluation on someone's behalf: a local process, a CI job, a remote service. It is given a
**plan**, reports an **event stream**, and produces sealed runs that carry the plan's provenance ([RUN-12]). A
verifier can then check that the stream keeps to the plan.

## 6.1 Run plans

A run plan (schema `run-plan`) is what a runner is asked to evaluate:

- **[PLAN-1]** the exact subject version ([CKP-1]); its endpoint, which **MUST NOT** carry credentials (no
  `user:password@`); and for a container run the image as its OCI image manifest digest with the repository it is
  pulled from;
- the suites, each with an exact version and the lane it serves;
- **[PLAN-2]** the limits the runner **MUST** stop at: `maxUsd` always; `cases` and `timeout` (hours and minutes) when
  set;
- the content policy, the isolation (`process`, `container`, `remote-zone` with its `zone`), and the provider (`local`,
  `docker`, `k8s`, `ci:<name>`);
- the judges (model, provider, rubric digest), the baseline a comparison lane uses (a policy, or one sealed run by id
  and run hash) and the comparability axes, so a runner cannot pick them;
- the tags a runner must carry (`runnerSelector`);
- **[PLAN-3]** the credentials it needs, **as references only**: each has the environment variable it is given as
  (`name`), where its value lives (`scheme`: `env`, `keychain`, `vault`) and under what (`path`), and what it is for
  (`purpose`: `subject`, `judge`, `attacker`, `evaluator`, `other`). The runner resolves each where it runs and gives
  the value to the process the purpose names, as that environment variable; it **MUST NOT** write a value into any AEF
  file or event.
- **[PLAN-4]** A plan **MUST NOT** hold a secret value. The schema refuses what is visibly not a reference (a bare
  string, an endpoint with credentials), but it cannot see a secret written as a reference's `path` or put in `ext`:
  a producer **MUST NOT** write one there.
- **[PLAN-5]** A plan is identified by its `planId` and its bytes: `planDigest` is the SHA-256 of the plan file's exact
  bytes. A runner that accepts a plan records both (§6.4, [RUN-12]).

## 6.2 Runner capability manifests

- **[PLAN-6]** A runner capability manifest (schema `runner`) says what a runner is: its id and workload identity (and
  the id of the key it signs sealed evidence with, if any: a hint for the reader, never a trust decision, [SIG-4]), its
  kind (`local`, `remote`, `ci`, `pool`), OS, runtime, the providers it supports, its tags, GPU and network zone, and
  its version.

## 6.3 Matching

- **[PLAN-7]** A runner **can take** a plan when it carries every tag of the plan's `runnerSelector`, supports the
  plan's provider, and, for a `remote-zone` plan, has the plan's zone as its `networkZone`. A runner **MUST** refuse
  (`job.refused`) a plan it cannot take, or one with an isolation, a credential scheme or a purpose it does not
  know.
- `conformance/protocol/matching/` holds plan and runner pairs with the expected answer.

## 6.4 The event stream

A runner reports a job as NDJSON (§2.2), one event per line (schema `runner-event`, a union on `kind`): on its standard
output for a local runner, over its channel for a remote one.

| `kind` | Carries |
|---|---|
| `job.accepted` | the `planId`, the SHA-256 of the plan's bytes (`planDigest`), the `runnerId` |
| `job.refused` | the same, and the `reason`: the runner will not run this plan |
| `plan.estimated` | the cases and the cost range (`usdLow`, `usdHigh`) with the price table |
| `spend.updated` | `spentUsd`: the total so far |
| `case.completed` | the `caseId` and its `state` |
| `lane.completed` | the `lane` and its `status` |
| `evidence.produced` | a sealed run: its `runId` and `runHash` |
| `job.cancelled` | the `reason` |
| `job.failed` | the `reason`, the `limit` (`maxUsd`, `cases`, `timeout`) when the runner stopped at one, and the `runs` it had sealed |
| `job.sealed` | every run the job produced (`runs`) |

- **[STRM-1]** Every event has `seq`, `jobId`, `at` and **MAY** carry `ext`. `job.sealed`, `job.failed`,
  `job.cancelled` and `job.refused` are terminal; a later minor **MAY** add other kinds but never a terminal one, and a
  verifier skips the rules of a kind it does not know. A runner that stops at a limit ends with `job.failed` naming the
  limit and the runs it sealed before stopping.
- **[STRM-2]** A stream whose last line does not end in LF is still being written: it is not a finished stream
  ([ENC-7]).
- **[STRM-3]** A stream verifier checks a finished stream, given its plan and the digest of the plan file's bytes, line
  by line, and reports per event (`event:<n>`, the 1-based line), in event order and then by code:

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
- `conformance/protocol/` holds plans, runner manifests, matching pairs, and streams with the problems written by hand,
  including the cases a plausible wrong implementation gets wrong (a gap followed by more events, times a nanosecond
  apart, spend equal to the budget, a decrease followed by a rise, an unknown kind mid-stream).
- **Not in 1.0:** signed jobs a remote runner pulls from a queue (so it can check who sent a plan). A later minor adds
  them; until then a remote runner authenticates its channel by other means.
