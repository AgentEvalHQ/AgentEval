# 5. Checkpoints

A **checkpoint** is a release decision over several kinds of evidence (a quality suite, a red-team campaign, a memory
benchmark, a compliance pack) for **one exact subject version**. It names the exact sealed runs it relied on, says how
each lane's result follows from them (§5.3), and how the outcome follows from the lanes (§5.4). Anyone holding the
manifest and the runs can recompute all of it (§5.5).

## 5.1 The manifest

A checkpoint manifest is a JSON document (schema `checkpoint`), conventionally `checkpoints/<checkpointId>.json`.

- **[CKP-1] One exact version.** `subject.version` is an exact version ([ENC-10]). "latest" is resolved before anything
  runs: the manifest records what was asked (`resolvedFrom`) and what it resolved to (`version`).
- **[CKP-2] Lanes.** Each lane names its `rule` (§5.3), the `requirements` it answers (external requirement ids:
  AEF does not define requirement catalogues), whether it is `blocking`, its `freshness`, and the exact sealed `runs`
  it used: each a `runId`, its `runHash` (so the evidence is frozen) and its `origin` (`launched` for this checkpoint,
  or `adopted:<source>` for an existing sealed run that matched the exact version and the comparability the lane
  requires). A lane with no runs yet is pending. Lane names are unique.
- **[CKP-3] Freshness** is the shortest freshness among the lane's requirements, resolved when the checkpoint is
  planned and recorded on the lane ([ENC-9]).
- **[CKP-4] States.** `state` moves `draft` → `planned` → `approved_to_spend` → `running` → `evidence_complete` →
  `decided`. A checkpoint is abandoned from any earlier state by moving to `decided` with the outcome `aborted`.
  `outcome` is `null` before `decided`; from `decided` on it is either the decision function's outcome (`approved`,
  `approved_with_exceptions`, `blocked`, `inconclusive` or `expired`: [DEC-3]), with its input (`decisionInput`, its
  exceptions included) recorded beside its output (`decision`), or `aborted` with an `abortReason` and no decision.
- **[CKP-5] A decided checkpoint never changes.** Its integrity is its signature: a DSSE envelope over the manifest's
  exact bytes, beside it (§4.4). An unsigned decided checkpoint is a claim anyone could have written.
- **[CKP-6]** `budget.approvedBy`, like an overlay's `by`, is a claim: a reader shows its assurance only as far as it
  verified it (§4.2).

## 5.2 What a lane's runs must be

- **[LANE-1]** A run a lane relies on is **eligible** when all of these hold:
  - it is intact (§4.5), closed `completed`, and `live` ([RUN-7]);
  - its `subject.ref` is the checkpoint's `subject.ref`, and its `subject.version` is present;
  - when the checkpoint names `subject.deployment`, its `deployment.ref` is that one;
  - when the lane's rule names a `suite`, its `suite` matches it: the same `ref`, and the same `version` and `digest`
    for those the rule gives.

  A lane result is computed from eligible runs only. A lane whose runs are not all eligible has the result
  `not_measured` (it fails closed), except a run that meets every condition above and is for another
  `subject.version`: the lane's result then carries that version, and the decision gives `wrong-version` (§5.3.5). Evidence about another subject, another deployment or another suite never counts for this one.

## 5.3 Lane evaluation

`LaneResult(rule, runs, baseline?) → result | null` turns a lane's sealed runs into the `result` the decision function
takes (§5.4). It is pure: it reads only the runs' sealed files. A lane with no runs, or none of whose runs is found,
has the result `null`. A rule kind this version does not know gives `not_measured` ([VER-3]).

### 5.3.1 `threshold`

`{kind: "threshold", lane, metric, path, op, value, suite?, minimumN?}`: a summary entry against a value.

- **[LANE-2]** For each run, take the `summary.json` entry with this `lane`, `metric` and `path`. The run's status is
  `not_measured` when there is no such entry, its `n` is 0, its `n` is below the rule's `minimumN` (counted in that
  run's entry), or it has an `aggregate` whose `method` [SUM-8] does not define (its value is the producer's,
  unchecked); otherwise `passed` when the entry's `value`, compared
  with the rule's `value` by the rule's `op` (`>=`, `>`, `<=` or `<`, as binary64), holds, else `failed`.
- The lane's status: `failed` if any run failed; otherwise `not_measured` if any run was not measured; otherwise
  `passed`.
- A family that publishes no pass threshold takes `comparison` or `evidence-present`, never `threshold`.

### 5.3.2 `severity`

`{kind: "severity", max, lane?, path?, suite?, minimumN?}`: the worst severity allowed among failures (`none`, `low`,
`medium`, `high`). With `lane`, only result lines of that summary lane count ([SUM-3]); with `path`, only lines at that
path or below it (`path` itself, or starting with `path/`).

- **[LANE-3]** Over every result line of every run, trial lines excluded:
  1. `failed` when a line in state `failed` or `warn` has a severity worse than `max` (order `none` < `low` <
     `medium` < `high` < `critical`; a missing severity counts as `critical`);
  2. otherwise `not_measured` when any line is `inconclusive`, `not_measured`, `skipped`, `error` or `pending`, or
     when fewer lines than `minimumN` (at least 1 when the rule gives none), counted over all the lane's runs, are
     `passed`, `failed` or `warn`: no
     evidence, or evidence that does not decide, is never a pass;
  3. otherwise `passed`.

  `not_applicable` and `scored` lines take no part.

### 5.3.3 `evidence-present`

`{kind: "evidence-present", runs, suite?}`: at least this many runs exist.

- **[LANE-4]** `passed` when every run of the lane is eligible ([LANE-1]) and there are at least `runs` of them;
  otherwise `not_measured`.

### 5.3.4 `comparison`

`{kind: "comparison", lane, metric, path, baseline, significance, minimumPairs, axes, suite?}`: no significant
regression of one run against a baseline run. The baseline is checked as the candidate is ([LANE-1]), except for its
version.

- **[LANE-5]** The lane has exactly one run, the candidate; `baseline` is a run reference (`runId`, `runHash`) to an
  intact, completed, live run. Otherwise the result is `not_measured`.
- **[LANE-6] Comparability.** For each axis named in `axes`, the candidate's and the baseline's `run.json` give the
  same value (an absent value equals only an absent value):

  | Axis | `run.json` field |
  |---|---|
  | `subject` | `subject.ref` |
  | `suite` | `suite.ref` and `suite.version` |
  | `suite-content` | `suite.digest` |
  | `judges` | the list of `judges[].model`, in order |
  | `rubrics` | the list of `judges[].rubricDigest`, in order |
  | `target-mode` | `execution.targetMode` |
  | `deployment` | `deployment.ref` |
  | `producer` | `producer.name` and `producer.version` |

  An axis this version does not name cannot be checked: it counts as differing. When any axis differs, the result is
  `incomparable` with the differing `axes` (in the order the rule lists them).
- **[LANE-7] Pairs.** A case is paired when both runs have a measured line for it at `path` in `lane` (as [SUM-3] and
  [SUM-4] define), with values `c` (candidate) and `b` (baseline). The metric's direction (from the candidate's
  `metrics.json`) orients the difference: for `higher_better` the pair regressed when `c < b` and improved when `c > b`;
  for `lower_better` the reverse; equal pairs are ties and are dropped. A metric of direction `none` cannot regress: the
  result is `not_measured`.
- **[LANE-8] The test** is an exact one-sided sign test. With `r` regressions and `i` improvements, `m = r + i`: when
  `m < minimumPairs` the result is `not_measured`; otherwise

  ```
  p = Σ_{k=r}^{m} C(m, k) / 2^m
  ```

  and the result is `failed` when `p ≤ significance`, else `passed`. `significance` is the binary64 value read from the
  manifest ([ENC-4]); the comparison is exact, between the rational `p` and that binary64 value's exact rational value
  (for example `Σ C(m,k) · 2^e ≤ M · 2^m` with integers, where `significance` = `M / 2^e`). An implementation **MUST
  NOT** compute `p` in floating point, approximate the binomial (a normal or z approximation), or use a two-sided test.
- **[LANE-11] Cost.** `m` is at most the number of result lines of a run, so at most 1,000,000 ([ENC-17]). The tail
  sum costs O(m) big-integer steps when each term follows from the previous one, C(m, k+1) = C(m, k)·(m − k)/(k + 1),
  summing whichever side of `r` is shorter (the other is 2^m minus it): the reference takes about 0.1 s at m = 40,000
  and about a minute at m = 1,000,000 in pure Python. An implementation **MAY** decide sooner when bounds it computes
  exactly already settle `p ≤ significance` either way; it **MUST NOT** give another result.

### 5.3.5 The lane's version and age

- **[LANE-9]** The lane's runs are found by `runId` and run hash ([CKP-8]); a lane none of whose runs is found has the
  result `null`, as a lane with no runs. Over the runs found (the baseline excluded): the result's `subjectVersion` is
  the checkpoint's version when every one with a `subject.version` has it, otherwise the `subject.version` of the first
  (in lane order) that does not; `oldestClosedAt` is the earliest `run.json` `endedAt` among them (for an intact run,
  its seal's `closedAt`): a re-run yesterday does not make 60-day-old evidence fresh. A run found without `endedAt`
  (still running) has no closing time and is not eligible; when no run found has one, `oldestClosedAt` is the
  checkpoint's `decisionInput.evaluatedAt` (or, for an undecided checkpoint, the evaluation time the verifier is
  given as an input).
- **[LANE-10]** `conformance/lane-vectors/` holds small sealed runs with lane rules and the result each gives.

## 5.4 The decision function

`Decide(input) → output` (schema `decision`: `$defs/input` and the document itself) is pure: no I/O and no clock; the
evaluation time is an input.

- **[DEC-1] Input:** the exact `subjectVersion`, `evaluatedAt`, an optional `supersededBy` (a newer version known at
  that time), per lane: `lane`, `blocking`, an optional `freshness`, `result` (§5.3: `status`, `subjectVersion`,
  `oldestClosedAt`, and `axes` when incomparable) or `null` when there is no evidence, and `evidence`, the `runHash` of
  each of the lane's `runs` (each once, in ascending order; required with a result); and optional `exceptions`. An
  **exception** accepts the failure of named, sealed evidence for a while: the `lane`, the `evidence` it accepts (run
  hashes, at least one, in the same form), optionally the `requirement` whose risk it accepts (for display: it takes
  no part in the decision), a `reason`, who granted it (`by`), when (`at`) and until when (`expires`). `by` is a claim,
  as in [CKP-6]. What makes an exception attributable is the checkpoint's signature ([CKP-5], [CKP-9]): the checkpoint
  records its exceptions in its `decisionInput`, so whoever signs it vouches for them, and a reader shows `by` with the
  assurance of that signature (the trusted identity it verified for, or none). At least one lane, no lane twice, and
  every exception for a lane of the input, with at least one run hash and an `expires` later than its `at`: the
  function refuses anything else rather than decide it.
- **[DEC-2] Each lane's status**, in this order:
  1. `result` is `null` → `missing` (reason `missing:<lane>`).
  2. The result is for another version → `missing` (reason `wrong-version:<lane>`).
  3. `oldestClosedAt` is later than `evaluatedAt` (the evidence did not exist then) → `missing`
     (`future-evidence:<lane>`).
  4. A `freshness` is set and `oldestClosedAt + freshness < evaluatedAt` → `stale` (`stale:<lane>`). Evidence exactly
     as old as the freshness is still fresh.
  5. Otherwise the result's status: `passed` (no reason), `failed` (`failed:<lane>`, or `advisory-failed:<lane>` for a
     lane that is not blocking), `not_measured` (`not-measured:<lane>`), `incomparable` (`incomparable:<lane>`, and the
     lane carries its `axes`). A status this version does not know reads as `not_measured`.
  6. A `failed` lane, blocking or not, becomes `waived` (reason `waived:<lane>`, in place of its failure's) when an
     exception for it **applies**: it is for this evidence (its `evidence` and the lane's are the same set of run
     hashes; a lane without `evidence` has none) and **in force** (`at` ≤ `evaluatedAt` < `expires`). When several
     apply, the one that waives the lane is the one that expires first (the first in input order among equals): a
     reader shows its `reason`, `by`, `requirement` and `expires` beside the lane. A `failed` lane that has
     exceptions, none of which applies, stays `failed`; its reason is followed by `exception-expired:<lane>` when it
     has an exception for this evidence that is not in force (expired, or granted after `evaluatedAt`), then by
     `exception-other-evidence:<lane>` when it has an exception for other evidence (in force or not).

  Only a `failed` status is ever waived, and only for the evidence the exception names: a re-run has new run hashes,
  so accepting one failure never accepts the next. An exception for a lane that is `passed`, `missing`, `stale`,
  `not_measured` or `incomparable` has no effect and adds no reason.
- **[DEC-3] The outcome**, first rule that holds:
  1. `supersededBy` is set and differs from `subjectVersion`, or any lane is `stale` → `expired`.
  2. Any blocking lane is `failed` → `blocked`. A `waived` lane is not `failed`.
  3. Any lane, blocking or not, is `missing`, `not_measured` or `incomparable` → `inconclusive`.
  4. Any lane, blocking or not, is `waived` → `approved_with_exceptions`.
  5. Otherwise → `approved`. A failed advisory lane does not block, and is reported.

  Missing evidence is never converted into a pass, and nothing is averaged. An exception cannot convert it either: it
  accepts named, sealed evidence, and it is not a policy. A `failed` lane is a measured risk: someone can read what
  failed in those runs, accept it by name, and set a date to look again. A `missing`, `stale`, `not_measured` or
  `incomparable` lane is an unknown risk: nothing current says what would be accepted, so a waiver there would approve
  what nobody measured. Its remedy is evidence, not a signature.
- **[DEC-4] Reasons** are codes, so every implementation produces the same list: the lane reasons in input order
  (each lane's own, then its `exception-expired:<lane>` and `exception-other-evidence:<lane>` when [DEC-2] adds them,
  in that order), then `superseded:<version>` if it applies, then `outcome:<outcome>`.
- **[DEC-5]** Versions, lane names and run hashes compare byte for byte; times (`evaluatedAt`, `oldestClosedAt`, an
  exception's `at` and `expires`) at full precision ([ENC-8]).
- `conformance/decision-vectors/` holds inputs with expected outputs written by hand from these rules.

## 5.5 Verifying a checkpoint

A checkpoint verifier is given the manifest, a way to find runs (a folder of runs, a store), and optionally a trust
policy. It reports problems as a path and a code, ordered by path and code.

- **[CKP-7] The manifest alone.** The manifest is one file, so these problems are codes alone, reported in code order.
  For a checkpoint decided by the decision function: `decision` (the decision is not what the function gives on the
  recorded input, its exceptions included, or the input cannot be decided), `evidence` (a lane has runs but no
  result in the input, or a result but no runs), `exception-evidence` (an exception's `evidence` names a run hash that
  is not one of its lane's `runs`), `lane-evidence` (a lane's `evidence` in the input is not the set of its `runs`' run
  hashes in the manifest; none is the empty set), `lanes` (the input does not decide exactly the manifest's lanes, with
  the same names, blocking and freshness, in order), `outcome` (the outcome is not the decision's), `version` (the input
  is for another version). Only the fields this version defines are compared. A manifest in a state, or with an outcome
  or a lane status, this version does not know, or one with a decided outcome but no decision, is reported only as
  `unverifiable`: a reader cannot recompute it, which is not the same as finding it wrong. A checkpoint not yet
  decided, or abandoned (`aborted`), has nothing to recompute and no problems.
- **[CKP-8] Against the runs.** A run is **found** when a run folder's `run.json` has the `runId` and the run's run
  hash ([SEAL-4]: its seal's, or for an unsealed run the recomputed one) is the one named. Whether its files still
  match is then the run verifier's question: a run changed since it was sealed is found, and not intact. When several
  folders hold that `runId` and run hash (a copy beside the original), the run is intact when any of them is, and the
  verifier uses an intact one; the order folders are listed in never matters. For each run
  a lane names, or a comparison's baseline (path `lanes/<lane>/runs/<runId>`): `run-missing` (not found),
  `run-unverified` (found, but not intact: unsealed, or with problems; a blob withheld by an authorized redaction is
  not a problem). The verifier takes the trust policy (for authorized redactions) and the evaluation time as inputs. Then, for a
  decided checkpoint, each lane's result recomputed with §5.3 is compared with the recorded input (path
  `lanes/<lane>`): `lane-result` (another `status` or other `axes`, or a result where `null` was recorded or the
  reverse), `lane-version` (another `subjectVersion`), `oldest-closed` (another `oldestClosedAt`).
- **[CKP-9] The signature**, when an envelope is present and a trust policy given: the per-signature results of §4.4.
  A checkpoint that verifies with no problems and a signature verified for a trusted identity **anchors** its runs
  (§4.5).
- `conformance/checkpoints/` holds manifests with the expected problems of the manifest rules;
  `conformance/lane-vectors/` holds checkpoints with their runs and the expected problems against the runs.

## 5.6 Expiry at read time

- **[CKP-10]** A decided checkpoint is never rewritten. A reader that shows it later evaluates the decision function
  again on its recorded input, with the time of reading as `evaluatedAt` (and any newer version it knows of as
  `supersededBy`), and shows the recorded outcome with the re-evaluated one beside it when they differ: `expired` once
  evidence has aged past its freshness. Exceptions lapse the same way: an exception whose `expires` has passed no
  longer waives its lane, so a checkpoint recorded as `approved_with_exceptions` reads as `blocked` once its waived
  blocking lanes have no exception left in force (or as `expired`, when its evidence has aged too). The reader adds
  nothing else to the input: an exception granted after the decision, or for a re-run's evidence, belongs to a new
  checkpoint ([CKP-5]). The re-evaluated outcome never replaces the recorded one, and never shows an approval the
  checkpoint did not record: a re-evaluated `approved` or `approved_with_exceptions` is shown only beside a recorded
  one of these.
