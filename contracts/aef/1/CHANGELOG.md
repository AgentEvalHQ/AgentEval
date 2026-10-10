# AEF 1.0 changelog

## Unreleased (draft): rework after critic round 9

Critic round 9 scored 9.43 of 10 (from 9.39): "technically AEF 1.0 is ready"; no divergence between the two runners
or among the three plan-conformance verifiers. Changes since:

- **A plan that names no judges allows none** ([STRM-4], [PLAN-1]): the text, the primer and the rationale already
  said a runner cannot pick them; STRM-4 now agrees. A judge is compared by `model`, `provider` and `rubricDigest`, and
  a run's judges are the plan's with some left out, so a plan that names one judge twice (under two providers, say)
  lets a run name both. Five plan-conformance vectors.
- **PLAN-9 has one rule for every case**: `cases` counts cases started; the job's spend and each run's cost include a
  run's own costs; `usdHigh` sums every bound.
- **`job`'s input errors have vectors** (§9.2.1, §9.3): nine vectors expect exit 2 with nothing written, the clock's
  range among them, so a runner that crashes on bad input no longer passes the corpus.
- **Inspect logs are read as I-JSON** (inspect.md, IN-6): a member named twice, an unpaired surrogate, an infinity or a
  number beyond binary64, a NaN outside the places the page allows, and nesting deeper than [ENC-17]'s 64 are refused as
  the log is read; a numeric sample id or `task_version` must be a safe integer and is written as its digits; an
  `error` must be an object and a `limit` `{type, limit}`. Eleven refusals and three samples added to
  `inspect-aef-edges`; the reference converter no longer stops with a traceback on any of the critic's 51 probes.

## Unreleased (draft): rework after critic round 8

Critic round 8 scored 9.4 of 10 (from 9.3): "technically AEF is now at 1.0 quality: every class, the Runner
included, has two separately written implementations that pass the whole corpus"; what keeps it from publishing is
the patent commitment and a first green CI run. Changes since:

- **A run names the judges that graded it** ([RUN-9], [STRM-4]): STRM-4's `judges` accepts a sub-list of the plan's
  judges, in the plan's order, none twice, so a run no judge touched no longer claims one; a scripted target's runs
  name none (§9.2.1). Five plan-conformance vectors (none, some, one the plan does not name, out of order, twice).
- **Limits with cases in flight** ([PLAN-9]): cases may run concurrently; the spend so far includes the bound of every
  case started and not complete, `cases` counts cases started, the time check holds for every case in flight, and a
  run's cost beyond its cases is bounded and checked as a case's is, in both sums.
- **The `job` operation's edges** (§9.3): a start time from which the job's clock would leave [ENC-8]'s years is an
  input error, found before anything is written; the vectors' `keychain` and `vault` references name paths that
  cannot exist, so a runner that can read either refuses them too.
- **Inspect: a limit spelled from its value** (inspect.md, IN-8): `<limit>` is written as ECMAScript's
  Number::toString writes the binary64 value (`1000`, `10000000000000000`, `1e+21`), so any JSON parser gives the same
  text; a limit with an empty `type` or a value that is not finite is refused; an error without a message has a
  stated reason. Content that is not text becomes a blob serialized by JCS (RFC 8785), so two converters give it one
  name from the same values; such content holding NaN is refused. Five samples and three refusals added to
  `inspect-aef-edges`; the non-text blobs of `inspect-aef` are renamed to their JCS forms.
- **Documentation**: the primer explains bounds, case ids and the scripted target; the rationale gains "Limits checked
  against bounds, not estimates", "A case keeps its suite's id" and "A scripted target for runner conformance"; the
  §9.3 `sign` row is back inside its table, and check_spec now reports a table row indented unlike its table.

## Unreleased (draft): rework after critic round 7

Critic round 7 scored 9.3 of 10 (from 9.1): eleven dimensions at 9.3 to 9.6; what keeps 1.0 back is the patent
commitment and a first green CI run. Changes since:

- **Limits a live runner can follow** ([PLAN-9]): a runner checks each limit against what the next case *could*
  take: the spend plus the case's cost bound, the time plus its time bound and the time it keeps for closing and
  sealing. The bounds are the runner's own; a runner that cannot bound a case's cost takes no plan, one that cannot
  enforce a deadline takes none with a `timeout`; `plan.estimated`'s `usdHigh` is the sum of the bounds. Before, "the
  first limit the case would pass" could be read two ways for a case whose cost is known only afterwards.
- **Every credential resolved** ([PLAN-3]), whatever the target needs; a credential of scheme `env` resolves when its
  variable is set. The reference runner no longer exempts its scripted target.
- **A case keeps its suite's id** ([PLAN-8]): a runner never rewrites case ids, so a case has one id in every run of
  its suite, whatever plan it runs in, and comparison lanes pair it ([LANE-7]); [STRM-4] `over-cases` counts a case
  with its suite (`ref`, `version`, `caseId`); a plan that names one suite twice, or a suite the runner cannot
  resolve, is refused. `case.completed` **SHOULD** name its run (`runId`).
- **A runner manifest names its target modes** ([PLAN-6], [PLAN-7]): optional `targetModes` (absent: `live` only),
  part of "can take", so matching answers for a scripted plan and a scheduler can route one.
- **[PLAN-10] is a MUST**: two runners given one plan derive the same `subject.kind` and `deployment.ref`.
- **The Runner class has vectors a second runner can pass** (§9.1, §9.2.1, §9.3): `job` vectors
  (`conformance/jobs/`, 23) run a plan against a *scripted target*, a test fixture with fixed answers, costs, bounds
  and durations, on a fixed clock, through a new operation `job PLAN RUNNER TARGET OUT --at T`; the judge checks the
  stream and the runs with [STRM-3], [STRM-4] and the run verifier, the cases and states run, the spend, the
  estimate, the end time, and that no credential value or path is written. They include a case whose cost fits but
  whose bound does not, the same for time, spend exactly at the budget by bounds, credentials set, unset and of
  schemes the runner cannot resolve, a missing suite, two suites sharing case ids, and a manifest without target
  modes. `runner-examples/` became these vectors; `tools/check_runner.py` keeps the sweep of the corpus plans, the
  system clock and usage errors.
- **A second runner, and what it found** (R7R-1 to R7R-10). AgentEval.Results' scripted runner, written from the
  text alone, passes every `job` vector, so two implementations pass the Runner class's vectors and the class is no
  longer released *at risk* (§9.1 says what no vector reaches: live targets). Ruled from its findings:
  - the budget is checked as [STRM-3] and [STRM-4] will compute it: the job's spend and the sum of the runs' costs,
    each rounded once, with the case's bound added to its run; the two sums can differ in the last bit, so "a runner
    that keeps to its bounds never passes a limit" was false under either alone ([PLAN-9]);
  - a run of a suite that serves a lane names the lane on its lines and gives it in its summary, so a checkpoint lane
    can read it ([PLAN-8], [SUM-3], [LANE-2]); a run carries the plan's suite `digest` and no other ([PLAN-8]);
  - no run is opened for a suite a limit stops before its first case ([PLAN-9]); a credential's `path` is never
    written, and an empty `env` variable does not resolve ([PLAN-3]);
  - the scripted target fixes each case's `severity`, the run's end before closing, `process` isolation only, and
    its own shape (an unknown member is an input error) (§9.2.1).
- **CKP-7 says one thing** (§5.3): a list of four. Only a later minor's values are `unverifiable`; a 1.0 manifest
  checked as decided without a decision, or without its input, is a `decision` problem. The rulings R6N-1 and R6N-2
  are revised: their vectors now expect `decision`; added a 1.0 manifest with an unknown outcome and no decision, and
  its 1.1 twin.
- **Self-check**: runner mutations (cost bound, time bound, case-id prefixes, credentials, a suite twice), RUN-3's
  `overlays` boundary, and `over-cases` by id alone; each is caught.
- **Editorial**: OT-8 and IN-11 are defined by a Run verifier (§4.5), the reference tool named as one; §9.3 `produce`
  says what its judge accepts (`contentCapture: on` added when the scenario has none); §9.1's opening on runners;
  IMPLEMENTATIONS.md says CI's first run is pending.
- **Interop: both directions of both mappings have two converters.** AgentEval.Results.Adapters now converts AEF
  to and from Inspect eval logs as well as OpenTelemetry, written from the page alone, and reproduces every checked
  example. What it found (R7I-1 to R7I-17) is ruled into inspect.md (settled 10-10): values, not bytes; a run that
  keeps no content exports no explanation (IN-13), as OT-3; a blob that is not UTF-8 refuses the export, a withheld
  one is left out (IN-12), as OT-9; only the records a sample's lines cite are read or refused; a ref's name is
  decoded on export; equal root times count as one; a reducer with no AEF value is refused only with more than one
  epoch; `judges[]` holds judge roles only; and the header details (`config`, `epochs_reducer`, usage, durations,
  limits, cut messages). Two example folders pin them (`aef-inspect-edges`, `inspect-aef-edges`).
- **Media types** (in-toto.md, I8): a DSSE `payloadType` needs no registration, so SIG-1's checkpoint type stands as
  it is; as media types the `vnd.agenteval.aef.*` names are proposed, and their IANA registration is drafted.

## Unreleased (draft): rework after critic round 6

Critic round 6 scored 9.1 of 10 (from 8.8): what blocks 1.0 is the patent commitment and an editorial pass, not the
design; the two implementations agreed on every crafted input. Changes since:

- **The later-minor rule reaches the manifest** ([CKP-7], [CKP-8], §5.3): a manifest is `unverifiable` for an unknown
  state, outcome or lane status only when it declares a later minor; a 1.0 manifest is checked as usual, so a value
  nobody defined never hides a wrong decision. Only the candidate's metric direction counts ([LANE-7]). Vectors: the
  three version vectors now declare 1.1, each with a 1.0 twin; a 1.0 run with an unknown target mode; a baseline-only
  unknown direction.
- **A tree belongs to its case, and a trial is a whole tree** ([RES-5], [RES-8]): a child's `caseId` is its parent's
  (`parent` otherwise); a trial line's parent carries `trial` (`trials` otherwise). Vectors for each, and a
  `produce` vector for independent roots in trials.
- **Editorial**: VER-1 names the files that carry no `schemaVersion`; §7.5 binds migrators, not a conformance class;
  §9.2.1's paths stay inside, as the runner enforces; the in-toto page no longer offers Sigstore as a policy input;
  the rationale, the producer guide and §9.2.1's scenario text brought in line; a trust policy of another major is
  refused with a message that says so ([VER-4]).
- **Left for 1.1, said so**: `produce` writes no evidence, gates or blobs (§9.1); at-limit vectors for 1,000,000
  result lines and 19,999 overlay files would need a million distinct valid lines or a 19,994-problem expectation.
- **A minimal reference runner** (§9.1, spec 06): `tools/aef_runner.py` takes a plan only when [PLAN-7] says it does,
  runs its suites against a built-in scripted target, stops at `maxUsd`, `cases` or `timeout` with `job.failed`, and
  writes the event stream and one sealed run per suite with its `provenance` ([RUN-12]); `--at` fixes the clock.
  `tools/check_runner.py` runs it on `runner-examples/` (since round 7, the `job` vectors; several cases, a budget and a timeout that stop the job) and
  the protocol corpus's plans and matching pairs, and checks the output with the independent tools: [STRM-3] and
  [STRM-4] find nothing (its plans ask for `scripted`, below), every run is intact, and the same inputs give the same
  bytes. The Runner class now has one implementation; it stays *at risk* until a second, independent runner passes.
- **What building the runner found, ruled** (spec 06, §6.5). Before, [STRM-4] reported every run that was not `live`
  as `target-mode`, and a plan could not ask for anything else, so no scripted runner could conform. Now:
  - a plan may name its `targetMode` (the run's values; `live` when absent); [STRM-4] `target-mode` is a run whose
    `execution.targetMode` is not the plan's, compared as written; a runner that cannot drive the target as asked
    does not take the plan, and an unknown target mode is refused like any unknown plan value ([PLAN-7], [VER-8]);
  - a runner resolves a suite by `ref` and `version`, checks a `digest` the plan gives and refuses the job on a
    mismatch (`job.refused` before acceptance, `job.failed` after); one run per suite; case ids unique within the
    job, prefixed when suites share them ([PLAN-8]);
  - limits are checked before each case in [PLAN-2]'s order, the timeout includes closing and sealing, and a run a
    limit cuts off is closed `aborted`, sealed and named in `job.failed`, with no lines for the cases not run
    ([PLAN-9]);
  - `subject.kind` comes from the ref's kind (else `other`), and a plan that names only an endpoint gives the
    `deployment.ref` `endpoint:` plus the endpoint encoded as [ENC-13] says, a SHOULD ([PLAN-10]);
  - smaller: `plan.estimated`'s `cases` is the suites' cases capped by the `cases` limit; `lane.completed` only from
    a runner given the checkpoint's rules; a plan the reader refuses gets `job.refused` when its `planId` can be read,
    otherwise no stream and an input error; credentials are resolved before `job.accepted`, and one that cannot be is
    `job.refused` ([PLAN-3]); the stream goes to standard output or a file the caller names; job and run ids are the
    runner's choice within [RUN-13]; isolation is the runner's claim, which AEF records and cannot check.
  - The reference runner follows: its examples ask for `scripted`, it refuses a plan asking for another mode and a
    suite whose content does not have the plan's digest, and `check_runner.py` now expects no [STRM-4] problem. New
    examples: a live plan and a digest mismatch (both refused), a `cases` limit met as a suite ends, one suite named
    twice.
  - Vectors: plans asking for `scripted` and for an unknown mode (`reads` `refused`); matching with an unknown mode
    (refused) and with a known one no manifest can rule out (taken); plan conformance for scripted runs on a scripted
    plan (no problem), a live run on it, a scripted run on a plan asking for `live`, and a mocked run on a plan that
    names no mode (each `target-mode`); `--self-check` switches the old reading back on (`target-mode-live`) and the
    corpus notices.
  - `aef_verify.py stream` crashed on a plan whose `timeout` is not a duration (`plans/timeout-in-seconds`); a plan
    the reader refuses is now an input error (exit 2) for `match`, `stream` and `conform`, as §9.3 now says.
- **Interop** (informative): a second AEF ↔ OpenTelemetry converter (AgentEval.Results.Adapters, .NET, written from
  the page alone) reproduces the three OpenTelemetry examples.
  - What it found in `opentelemetry.md` is ruled into the page as rules, settled 10-09 (R7N-3 to R7N-12):
    - the page fixes values, not bytes, and `check_interop.py` now compares JSON outputs as values;
    - a refused line refuses the whole export;
    - the sealed lines are exported, overlays not applied (OT-7);
    - only a run that verifies is exported or written (OT-8, and IN-11 for Inspect);
    - a reasoning blob that is not UTF-8 or is over 4 MiB, and a time `timeUnixNano` cannot hold, refuse the export
      (OT-9);
    - a trial line belongs to its lane for OT-1;
    - an import writes `contentCapture` (`on` unless asked otherwise) and asserts it;
    - `error.type` beside the label `error` and no explanation is `error`, not a refusal;
    - `evaluator.id` is the event's name, an explanation is cut at 4096 characters, and a label over 64 characters or
      a nameless event is refused (OT-10).
  - The reference converter follows. Its imported runs now carry `contentCapture`; `otel-aef` gains a label-`error`
    event, and `aef-otel-aef` refused exports (a run that does not verify, a time before 1970, a reasoning blob that
    is not UTF-8).

## Unreleased (draft): rework after critic round 5

Critic round 5 scored 8.8 of 10 (from 8.6). Changes since:

- **Nothing under `overlays/` is a problem of the run** ([RUN-3], [OVL-5], §4.5): a badly named file there (a
  `.DS_Store`, a name with a space) is the chain's `unexpected-file`, never a `path` problem that invalidates the
  run; more than 19,999 files is `limit` once, and the chain is still checked from the files it names, so no number of
  junk files voids a signed redaction.
- **An oversized envelope is malformed, never a problem of the run** ([SIG-1], [ENC-18], §4.5): beyond 56 MiB an
  envelope verifies for no one; an attestation then signs for no one, a batch signature authorizes nothing, an
  orphan one changes nothing. The two implementations had split on it (W3-21).
- **Only a later minor can claim `unverifiable`** ([CKP-8], [VER-6]): a document that declares 1.0 is read as 1.0
  reads it, so a member nobody defined cannot turn a false recorded result from `lane-result` into `unverifiable`.
- **The rollup tree follows the trial trees** ([RES-8]): a rollup's parent is the rollup at its trial lines' parents'
  path; a path's spelling decides nothing (`q/x` may be a root of its own).
- **The trust policy has a version** ([SIG-4]): an optional `schemaVersion` `1.0`; a later one is refused. Keyless
  signing is left to a later minor.
- **Smaller**: a ref's name that is exactly `-` is `%2D`, and a ref's kind is at most 32 characters ([ENC-13]); the
  Runner class is marked *at risk* in §1.7 too; the Run verifier includes the Reader's vectors (§9.1); a generated
  vector's paths are relative and stay inside (§9.2.1); OVL-5 no longer says a crash line always reads as
  `event-invalid`; the producer guide writes the summary before closing the run ([RUN-4]); the `produce` judge takes
  an absent `contentCapture` as `on` ([RUN-11]).
- **Interop** (informative): the Inspect → AEF direction is now built and checked (R5-9).
  - `tools/aef_interop.py from-inspect` converts an Inspect eval log into an imported run, sealed as `ingest`, with
    what it supplies in `imported.asserted`. Writing it from `inspect.md` alone settled what the table left open, as
    rules and stated refusals of the page (IN-6 to IN-10, settled 10-09): the run header; values the table does not
    place (a map, NaN without a reason, a list); samples (epochs as trials, reductions as rollups, a sample that
    failed before it was scored, usage and case content); the summary, recomputed from the lines and compared with
    Inspect's; and overlays, which the converter does not write.
  - New checked examples: a hand-written Inspect log (`interop/examples/inspect-aef/`, with content kept and not, and
    nine refused inputs), and AEF → Inspect → AEF on `completed-eval` and `running-trials`. The first trip keeps the
    result ids and is checked field by field against the page's loss list, now bullets, which gained trace links;
    the worked example shows the way back.
- **Vectors**: at their values, a 40 MiB seal, a 4 MiB results line and a 56 MiB envelope; one byte beyond, an
  envelope, an attestation, a batch signature and an orphan one; junk files under `overlays/`, by name and by number;
  a batch beyond what a reader reads (R4N-2); 1,000,000 lines and an unfinished one (R4N-3); a 1.0 checkpoint with a
  member nobody defined; independent roots in trials; a versioned and a later-versioned trust policy; a `produce`
  run without `contentCapture`, and one with times to the nanosecond.

## Unreleased (draft): rework after critic round 4

Critic round 4 scored 8.6 of 10 (from 8.4), with no blocker: every round-3 finding but the patent commitment
addressed, and a second implementation passing every vector kind, writers included. Changes since:

- **Nothing appended changes a sealed run, and a crash never stops the chain** ([OVL-5], §8.1): the events file is
  judged line by line, inside the batches and after them: a blank line, a CR or a leading byte-order mark is
  `event-invalid` at its line and nothing more; a last line without LF is still being written; a writer appending
  after one ends it with an LF first, so the next batch claims it. The events file is read within [ENC-17]'s limits
  (an unfinished line is no line); a longer one is `limit` once, and its verified batches still stand. More than
  19,999 files under `overlays/` is `limit` at `overlays`. Before, one blank line appended by a crashed writer voided
  an authorized redaction, and a million appended lines split two verifiers; a first fix (the verified batches
  judged as a file) still let one crash line stop the chain for good, which implementing it found (R4N-9). A new
  adversary in §8.1: the appender.
- **1.0 verifiers never call a 1.1 lane wrong** ([CKP-8], [VER-5]): a lane is `unverifiable` when recomputing it
  reads anything this version does not know: a rule not valid against the writer schema (any new member, kind or
  value), or, in its runs, an unknown `execution.targetMode`, a severity lane's unknown `severity`, a comparison's
  unknown metric `direction`. A minor version changes how a lane computes only through its rule.
- **The trust policy has a schema** ([SIG-4], [VER-9]): `trust-policy.schema.json`, closed for readers too (a verifier
  cannot honour a restriction it does not know); `may` is matched exactly, and a value a verifier does not know
  grants nothing. Before, the reference granted redaction to `"may": "never-redact"`.
- **Every ruling born of a disagreement is pinned** (§9.2): `conformance/rulings/` holds a vector for each ruling
  made where the two implementations disagreed (W3-17 to W3-19, W4-1 to W4-3, W4-5, W4-6, W4-10, and LANE-3's
  trial lines); undoing any of them in the reference fails its vector, and `--self-check` now proves it with a
  mutation for each, and for each of this round's rules.
- **Limits tested at their values** (§9.2): generated vectors (`limits/`) that the conformance runner builds from a
  recipe: a run folder of exactly 100,000 files and one of 100,001, 1,000,001 lines, a 4 MiB line, a 40 MiB seal,
  20,000 files under `overlays/`. The corpus stays small.
- **Writers**: five more `summarize` refusals, one per input error §9.3 lists. A Producer is now tested as the writer
  of `results.ndjson` (R4-6): the write-side kind `produce` (§9.2.1, §9.3) gives it a scenario, the facts of a closed
  run (its `run.json` and `metrics.json`, each case's result tree and trial trees as facts, a summary request), and
  judges the run it writes: the lines as a set, matched by case, path and trial, with their result ids, parents,
  trial numbers, rollups (`n`, `passed`, `agree`) and aggregation counts, then the summary and the reference
  verifier: seven scenarios to write, nine to refuse (each contradicts itself). `agree` is now defined: `true`
  exactly when the trial lines at the path are all in one state ([RES-8]). The Runner class is released *at risk*: neither
  implementation is a runner (§9.1, GOVERNANCE.md).
- **Smaller rules**:
  - times are years 0001 to 9999 ([ENC-8]);
  - only `sum` must be exact; `sumSq` is binary64 within §3.6 ([SUM-5]);
  - a producer SHOULD write `sum`, and an overlay writer `target.runHash` ([SUM-5], [OVL-2]);
  - a writer SHOULD omit a null optional field, and writing `null` is valid ([ENC-2]);
  - LANE-3's steps 2 and 3 never read trial lines ([LANE-3]);
  - a composite case's trial rollups form its tree, checked as `trials` ([RES-8]);
  - a new aggregation strategy `Own`: a node's own verdict, its children recorded beside it ([RES-6]);
  - the `document` operation applies the limits of the file its schema names (§9.3);
  - trace and log lines are split under 4 MiB (§3.1).
- **Conversions** (§7.5, interop): what building the store v1 exporter and the ASSERT importer found (W5b-1 to
  W5b-18): fixed paths and evaluator ids so two migrators give the same result ids, how every unmapped field is
  carried, converted runs sealed as `ingest`, one encoding for a ref made from a name, and the ASSERT example's
  digests computed from the sample's LF bytes (now pinned by `.gitattributes`).
- **What a reader shows, and what a checkpoint anchors, are tested** ([OVL-3], [CKP-9], [SIG-8]): the effective view
  gives the assurance shown for each event (`signed` only for a signature verified for the event's own identity);
  the `lanes` operation takes the checkpoint's signature and gives the runs it anchors. A rollup's `agree` is checked
  against its trial lines, as `n` and `passed` are ([RES-8]). Stream vectors give problems as `[where, problem]`
  pairs, like every other kind.
- **A guide for producers** ([producers.md](producers.md)): the four files, saying what was not measured, the
  optional fields that make a run more checkable, privacy, the encoding traps implementations fell into, overlays,
  and testing a writer.
- **Conformance and documents**: §9.1 lists every kind each class's vectors hold, and a class that includes another
  passes its vectors (`check_spec.py` compares §9.1 with `index.json`); the README, rationale and interop pages
  brought in line; `aef_stream.py` sums a job's costs exactly, as [STRM-4] says.
  - **Checked interop examples** (interop, informative): `interop/examples/` holds round trips the critic asked for,
    AEF → OpenTelemetry events → AEF on two corpus runs, OTLP/JSON → AEF from a hand-written file (the registry's own
    example included), and AEF → Inspect on two corpus runs. `tools/aef_interop.py` is a reference converter written
    from `opentelemetry.md` and `inspect.md` alone, and `tools/check_interop.py` (in the AEF workflow) reruns it on
    every example, verifies every run, checks field by field that a round trip loses exactly the page's "What does not
    carry over" list, and fails when a page's worked example differs from an example's data. Writing it found what
    the two pages left undecided (OT-1 to OT-6, IN-1 to IN-5): the name of an event for a line without scores, the
    header of an imported run, the shape of Inspect's `results`, overlays, among others. Each was settled on 10-09
    and written into the page's mapping as a rule or a stated refusal; the converter follows them, and names the rule
    when it refuses. The OpenTelemetry page's loss list is now
    complete (severity, `durationMs`, `turns`, `attack`, `lane`, `normalized`, usage, start times were missing), and
    its worked example's claim that every `usage` entry sits on a span is corrected.

### Rulings from implementing the run, checkpoint and stream verifiers (W3-16 to W3-21, W4-1 to W4-14)

Written into the text in the round-3 rework, listed here for the record:

- **Runs** ([RES-6], §3.9, [OVL-2]): `total` counts distinct child ids; a second rollup for one case and path is
  `trials` at the later rollup only; a `results.ndjson` with only schema problems still gets the overlay `target`
  check; the `document` operation takes the limit of its schema's file; a file beyond a limit is `limit` whatever
  else is wrong with it; an envelope beyond 56 MiB is `limit` (changed in round 5: it is `malformed`, [SIG-1]).
- **Streams and plans** ([STRM-3], [STRM-4]): a stream line beyond the limits is `event-invalid`; a job's costs are
  summed exactly, rounded once, then compared with `maxUsd`.
- **Checkpoints and lanes** ([CKP-7], [CKP-8], [LANE-1]–[LANE-9], [LANE-6]): a lane on one side of the decision input
  only is `evidence`; only intact runs of the checkpoint's subject, deployment and suite give a lane its version and
  its age, so neither a copy's folder name nor a run of something else changes them; a run a lane names twice counts
  once; a run without `judges` equals one with `[]`; a baseline needs no `subject.version`; a folder holding run.json
  is one run, never searched for others; line paths order by number only for the run's own NDJSON files; a
  checkpoint anchors its comparison baselines too; a lane the decision input leaves out is not compared.

## Unreleased (draft): rework after critic round 3

Critic round 3 scored 8.4 of 10, with no blocker: every round-2 finding addressed, and AgentEval's own implementation
passing the whole corpus. Changes since:

- **Limits decide, not implementations** ([ENC-17], [ENC-18]): a reader **must** refuse anything beyond the limits, so
  two conforming verifiers never split on a run; `limit` has vectors at last (a run file, a results line, a seal, a
  batch seal, an events line, a document, each 65 deep). Seals may reach 40 MiB and envelopes 56 MiB, so a legal run
  of 100,000 files can be sealed and signed; seals and overlays do not count toward the file limit; at most 9,999
  overlay batches.
- **1.0 verifiers and 1.1 checkpoints** ([CKP-8], [VER-5], [SUM-8]): a lane whose rule holds a value this version does
  not know is `unverifiable`, never `lane-result`; the recomputed aggregates are fixed for major 1; a minor's new
  problem code applies only to what it adds.
- **No failure hides behind a rollup** ([RES-5], [RES-6], [RES-8], [LANE-3]): a rollup must match its trial lines, and a
  trial line needs its rollup; `total` is the number of children; a child has `component`, a parent `aggregation`; a
  trial's lines all carry its number; a failing trial counts for a severity lane by itself.
- **Writers tested as writers** (§9.1–§9.3): three write-side vector kinds, judged with the reference verifier rather
  than compared byte for byte. `summarize`: a Producer computes `summary.json` for a run and a list of entries (means,
  counts, rates, typed absences, scored and trial lines, median, min and max, lanes, an exact sum that cancels; a
  `value` given for what AEF computes refused). `seal-write`: a Sealer seals a copy of a run (subjects in byte order,
  the predicate as `run.json` gives it, times at full precision, the copy `intact`; refused: an open run, a seal dated
  before the run closed, and on custody (`ingest`) a run whose paths or files are not valid, [SEAL-1]). `sign`: a Sealer
  signs with a PKCS#8 test key, now in `signature-vectors/keys/` (the envelope must verify; an Ed25519 signature must
  be RFC 8032's). A Sealer signs with at least one of [SIG-2]'s algorithms and passes the `sign` vectors of those it
  claims (each names its `algorithm`; `aef_conformance.py --sign-algorithms`); its claim names them ([CONF-4]). A
  Runner is tested through its runs and its stream. Reference writer `tools/aef_produce.py`; generator
  `tools/write_vectors.py`.
- **Smaller rules**: a line's `usage` per role and model ([RES-10]); `oldestClosedAt` from `run.json` ([LANE-9]); PEM
  lines of 64 characters ([SIG-3]); a seal dated before its run closed is a `predicate` problem; only an aborted run
  has an `abortReason`; content in resource and scope attributes counts ([SEC-6]); a producer's aggregate has a value
  whenever `n` is not 0; what "valid against a schema" and "does not read" mean ([ENC-16], [OVL-2]); member order,
  whitespace, number forms and null-or-absent are free ([ENC-2], [ENC-4]).
- **Documents**: the interop pages, rationale, primer and READMEs brought up to the specification; new primer
  sections on exceptions and plan conformance; new rationale on exceptions, closed enums, redaction authority and
  recomputed aggregates.


## Unreleased (draft): what implementing it in .NET found

AgentEval's own implementation (AgentEval.Results, TODO Q4-39) is written from the text alone, as the second
implementation every class needs. Its design pass and its first two work packages found 39 places where the text was
ambiguous, contradictory or silent; running both implementations on crafted inputs found three real disagreements.
Each was ruled, written into the specification and pinned by a vector (488 vectors at the time):

- **Signatures** ([SIG-1]–[SIG-5]): Ed25519 is required of verifiers, as P-256 is, since a signer may use either.
  Pinned where libraries differ: Ed25519's k reduced mod L, the equation without the cofactor, small-order keys
  refused; every key strict DER in a strict RFC 7468 PEM; a policy listing a key twice, or holding a key that cannot
  be used, refused as a whole; the full list of malformed envelopes (a `null` keyid reads as absent, as DSSE says);
  either base64 alphabet, padded or not.
- **Limits** ([ENC-17], [ENC-18]): seals and envelopes may reach 32 MiB, so a run of 100,000 files can be sealed; depth
  counts the top-level value as 1; a line over a limit is reported at its line.
- **Reading** ([VER-8], §7.3): a value is known when the writer schema accepts it (so `ci:<name>` providers are
  known); rows for every open enum (a threshold's `op`, a plan's `contentCapture`, `job.failed`'s `limit`, a
  decision's outcome and statuses); every assurance reads as `self-attested` until a signature verifies it.
- **Precision**: string lengths count code points and numbers compare as binary64 ([ENC-4]); underflow reads as 0
  ([ENC-3]); patterns avoid constructs engines match differently ([ENC-14], enforced by `check_spec.py`); codes
  order by their bytes and only NDJSON lines by number (§3.9); reserved Windows names ignore everything from the
  first `.` ([RUN-3]); only regular files in a run folder.
- **Conformance** (§9): the command-line contract is one normative table; the Reader class has its own vectors
  (every encoding defect as a document); `index.json` marks what only a signed-level Run verifier must pass; a
  `matching` vector asks whether a runner *takes* the plan; the decision function refuses what the reader schema
  refuses; a migrated AgentEval store run is an imported run ([RUN-15], §7.5).
- **Tools**: the reference verifier reads its known values from the writer schemas, so they cannot drift (it had read
  a `scored` summary verdict as `inconclusive`); `check_spec.py` fails on a problem code no vector expects.
- **Runs, seals and overlays** (from the run verifier's implementation, six disagreements with the reference found
  on crafted runs): an absent `unmeasured` count is 0 ([RES-6]); a lane name twice is `summary-duplicate` and "a
  single lane" means a single lane name ([SUM-3], [SUM-9]); sums are exact, rounded once ([SUM-5]); a duplicated seal
  subject reports every code that applies ([SEAL-6]); only a sealed run's blob can be withheld (§3.9); RUN-3 is about
  the paths of files, empty folders are ignored, and a sealed path that is no longer a regular file is `missing`;
  OTLP/JSON 1.x names only ([RUN-14]); `limit` rows for seals, batch seals and events lines; an NDJSON file is at most
  1 GiB; "withheld" and "redacted" are separate terms (§4.5). The reference verifier no longer follows symbolic links.
- **Streams** ([STRM-3]): a line that does not read is `event-invalid` and takes no other part; a framing defect is
  one `encoding` problem at `stream`. Before, both made a verifier stop with an error.

## Unreleased (draft): rework after critic round 2

Critic round 2 scored the rework 7.8 of 10, with one blocker: a lane could pass on a summary number nobody checks.
Changes since:

- **No lane on an unchecked number** ([SUM-8], [LANE-2]): AEF defines the aggregates `median`, `min` and `max`, and a
  verifier recomputes their value; any other `aggregate` (pass@k, F1) is the producer's, shown as written, and a lane
  over it is `not_measured`. Two summary entries for one lane, metric and path, or two usage entries for one party and
  model, are `summary-duplicate` ([SUM-9]).
- **Exceptions bound to evidence** ([DEC-1], [DEC-2], [CKP-7]): each decided lane records the run hashes it was decided
  on, and each exception names the run hashes whose failure it accepts. A re-run has new run hashes, so accepting one
  failure never accepts the next (reason `exception-other-evidence`; codes `lane-evidence`, `exception-evidence`).
- **Runs are what the plan asked for, where and when** ([STRM-4]): codes `deployment` (the plan's deployment or
  endpoint) and `time` (a run that started before the job was accepted or ended after it ended). The plan endpoint
  takes `run.json`'s pattern, so a query string (`?api-key=`) or fragment is refused.
- **Content capture covers logs** ([SEC-6]): with `contentCapture: off`, no log record in `logs.otlp.jsonl` carries
  content or a body, and a judge's `gen_ai.evaluation.explanation` counts as content everywhere.
- **Copies of a run** ([CKP-8]): when several folders hold one run, it is intact when any copy is, whatever the order
  folders are listed in.
- **One duration grammar** ([ENC-9]): a lane's freshness and a plan's timeout are both ISO 8601 durations of days, hours
  and minutes (`P14D`, `PT90M`, `P1DT2H30M`), the common `duration`; a timeout may now be in days, a freshness in
  minutes.
- **Smaller rules**: `unexpected-file` for an extra file the seal lists; `attack` for a succeeded attack on a passed
  line; severity rules scoped by `lane` and `path` ([LANE-3]); `minimumN`'s scope stated per rule; which overlay problems
  end the verified prefix ([§4.3](spec/04-integrity.md)); where `ext` may appear ([ENC-19]).
- **Interop**: the OpenTelemetry, Inspect and OpenAI Evals pages rewritten against 1.0; a new ASSERT mapping; a summary
  entry with no rule is `scored` ([SUM-6]); `summary.json` carries the run's usage per party and model ([SUM-7]);
  `execution.stimulus` `imported` is now `external`, so it cannot be read as an imported run ([RUN-15]).
- **Gates**: `tools/schema_diff.py` refuses a new value in an enum closed by [VER-9]. A CI job runs every check on each
  change to `contracts/aef/`, and every AEF commit carries a DCO sign-off.
- **Corpus**: two lanes one binary64 step either side of the exact p of a 1,200-pair sign test, which only exact
  arithmetic decides correctly; vectors for the §7.3 readings that had none; a tampered copy beside a genuine run.
- **Drift fixed**: ten states, not nine; the primer's statements on found runs, unknown states and redaction; the
  classes of §9.1 list every rule.

## Unreleased (draft): rework after critic round 1

Critic round 1 scored the consolidation 7.2 of 10, with two blockers. Changes since:

- **Lanes bound to their subject** ([LANE-1]): a run counts for a lane only when it is of the checkpoint's subject and
  deployment, and of the rule's `suite` when the rule names one; threshold and severity rules take a `minimumN`.
- **No evidence is never a pass** ([LANE-3]): a severity lane over results that are all inconclusive, absent or empty is
  `not_measured`. LANE-4 requires every run eligible.
- **Checkpoint exceptions** ([DEC-1]–[DEC-4], [CKP-10]): a person can accept a `failed` lane's risk until a date, recorded
  in the signed decision input; the lane is `waived` and the outcome `approved_with_exceptions`. Missing, stale,
  unmeasured or incomparable evidence can never be waived; an expired exception lapses when the checkpoint is re-read.
- **Redaction needs authority** ([OVL-10], [SIG-4]): a blob is withheld only by a redaction in a batch signed by an
  identity the verifier's trust policy allows to redact (`"may": ["redact"]`); otherwise it is missing. A reader shows
  "intact, *n* withheld".
- **One run hash** ([SEAL-4]): the seal's `runHash` for a sealed run, the recomputed one otherwise; checkpoints and
  runner checks find runs by it, so a run changed after sealing is found and not intact.
- **Runner plan conformance** ([STRM-4]): each run a runner sealed is checked against its plan (provenance, subject,
  suites, judges, content capture, live target, job-wide case and cost limits). The plan's `contentPolicy` is now
  `contentCapture`.
- **Closed enums** ([VER-9]): result `state`, run `status` and metric `kind` are closed for major 1, so a later minor can
  never make a 1.0 verifier reject a legal run.
- **Expressiveness**: the `scored` state (measured, no rule); a score's `label`; summary entries with a producer
  `aggregate` (pass@k, F1, median); evidence kinds `input`, `expected`, `output`, `transcript`; result `startedAt` and
  `endedAt`; `usage` per party with cache and reasoning tokens; trial aggregations beyond `MajorityVote`; `imported`
  runs ([RUN-15]); an optional `logs.otlp.jsonl` and `otel.schemaUrls`.
- **Exact and bounded** ([LANE-8], [LANE-11]): the sign test's significance is binary64 compared exactly, computed in
  O(m) big-integer steps; numbers are binary64 everywhere and integer fields bounded to 2^53 − 1 ([ENC-3], [ENC-4]).
- **Ambiguities closed**: a metric scored twice, the verified overlay prefix and the events it ignores, case clashes in
  folders, long paths, OTLP id case, declarative statements are normative ([§1.5](spec/01-introduction.md)).
- **Invariants**: no query or fragment in an endpoint; codes `calibration`, `execution-policy`, `interval`,
  `result-times`, `annotator`; per-kind overlay targets.
- **Corpus**: about 390 vectors; `tools/check_spec.py` fails on any rule no vector names unless it is listed, with the
  reason, as untestable; a large comparison (1,200 pairs; round 2 found it did not yet defeat floating point); six
  effective-view vectors; reader-only vectors for most readings of §7.3; fixtures marked as such in `index.json`.
- **Governance**: lead editor, an open second seat, 90-day succession, DCO sign-off, release criteria (a second
  implementation per class, or marked at risk), profiles; the Community Specification License 1.0 planned for the
  specification text, pending counsel, with `SCOPE.md` limiting the patent commitment to what the specification
  requires.

## Unreleased (draft): consolidation for 1.0

The draft known as "v2" became AEF 1.0 and went through an independent review (critic round 0: mean 5.5 of 10, two
blockers). Changes since:

- **Identity.** Identifiers, paths and `schemaVersion` are 1.0 (`contracts/aef/1/`, `$id` and predicate types under
  `/aef/1/`). Governance and a NOTICE file added.
- **The specification** is nine numbered documents with BCP 14 keywords, an id on every rule, roles and conformance
  classes, a threat model (§8) and a conformance chapter (§9). The old one-page text is gone.
- **Verification outcomes**: unsealed, intact, signed, anchored, or invalid. An intact run is never called authentic.
- **Signatures**: DSSE v1; ECDSA P-256 required, Ed25519 recommended (required since the Q4-39 rulings); `keyid` is the SHA-256 of the SPKI DER; the trust
  policy is the verifier's input; an envelope needs at least one signature; no low-S rule.
- **Lanes bound to evidence**: a checkpoint lane's result is a function of its sealed runs (`threshold`, `severity`,
  `evidence-present`, `comparison` with an exact one-sided sign test). Only intact, completed, live runs are eligible.
  A checkpoint verifier finds runs by recomputed run hash and reports `run-missing`, `run-unverified`, `lane-result`,
  `lane-version`, `oldest-closed`. The checkpoint `sealed` state is gone: a decided checkpoint is signed.
- **Overlays bound to the run**: batch seals carry the run hash, events name their run, a `redact` event withholds a
  blob without breaking the seal, and the effective view is defined. New chain codes: `batch-number`, `run-id`,
  `run-hash`, `batch-invalid`, `event-id`, `target`, `unexpected-file`.
- **Encoding**: I-JSON (no duplicate members, no lone surrogates), NDJSON framing, limits, and patterns without
  lookaround where `$` is the end of the input.
- **Run fields**: `execution.targetMode` (live, replayed, scripted, mocked) is required; `contentCapture: off` keeps no
  content and no digests; no credentials in `run.json`; judge calibration; run cost.
- **Result fields**: `severity`, `durationMs`, `turns`, `attack` (technique, taxonomy ids, success), `lane`; `pending`
  is a typed absence and needs a reason; `unmeasured.errored` is now `unmeasured.error`.
- **Summary semantics** defined exactly (each entry names its `path`; `N`, `n`, `notMeasured`, `sum`, `value` are
  recomputable) and the cross-file problem codes of §3.9.
- **Fail-closed reading** of every unknown value (§7.3), a deprecation policy, and the store-v1 mapping (§7.5).
- **Comparability axes** are a fixed vocabulary.
- **Corpus**: rebuilt. Every vector is a folder with `expected.json` beside a `run/`; new families `runs/` (one per
  cross-file code), `encoding/`, `reader-only/`, `overlay-views/`, `lane-vectors/`, `signature-vectors/`, `paths.json`;
  `index.json` lists every vector with its classes, rules and file digests.
- **Tools**: `aef_crypto.py` (DSSE, ECDSA P-256, Ed25519), `aef_schema.py` (a JSON Schema validator with portable
  pattern semantics), `aef_verify.py` and `aef_conformance.py` (the reference verifier and runner), `schema_diff.py`,
  `gen_reference.py`, `lane_vectors.py`, `signature_vectors.py`, `build_index.py`. All standard-library Python.
- **Documentation**: a primer, a rationale and FAQ, interoperability mappings, and a field reference generated from
  the schemas.

## Earlier drafts

- The run folder and its files: `run.json`, `results.ndjson`, `metrics.json`, `summary.json`, `evidence.ndjson`,
  `gates.ndjson`, blobs, `seal.json`, overlay events and their chained batch seals.
- Writer and reader schemas (JSON Schema 2020-12); the reader schemas are derived from the writer schemas.
- Typed absences (`not_measured`, `not_applicable`, `skipped`, `error`) carry a reason; deterministic result ids;
  composite lineage (`aggregation.rulePath`, `decisive`); repeated trials with a rollup line.
- The seal over exact bytes (no canonical JSON): the manifest, the run hash, an in-toto Statement v1.
- The conformance corpus: valid runs, invalid documents with the rule each breaks, seal vectors, result-id vectors.
- The runtime-verdict profile (AEVP 0.1).
- Review of the draft: verification also checks the run hash, the run id, duplicate subjects and open runs; overlay
  assurance is a claim a reader verifies; patterns rule over `format` and guard the end of the string; run paths are
  ASCII and byte-ordered; NDJSON is LF-only; trials are integer digits; seal predicates carry `schemaVersion`;
  `contentCapture` is `off` or `on`; `unmeasured` uses the v2 state names; rules across files; every result state, a
  U+2028 inside a string, chain vectors and more seal vectors in the corpus.
- Review of the protocol: credentials are references with a name, scheme, path and purpose, delivered as environment
  variables, never in an endpoint; job.accepted binds the plan's bytes (planDigest) and runs carry provenance; a
  runner may refuse a plan (job.refused); more stream rules (plan digest, accepted twice, estimate, case and time
  limits, run hash changed, unsealed run) and vectors that catch plausible wrong implementations; plan-to-runner
  matching; plans carry judges, baseline, comparability, zone and the image's repository; providers and credential
  schemes extensible; the checkpoint verifier reports 'unverifiable' for what a later minor adds; impossible dates and
  integral numbers read the same in every implementation; [!-~] instead of \s; seal subjects cannot leave the folder.
- Review of checkpoints: the manifest records the decision's input beside its output and each run's hash and origin;
  rules across the manifest with a verifier; aborted modelled; freshness from the oldest run, resolved from the
  requirements; expiry at read time; future evidence is missing; unknown statuses fail closed; no lanes or a lane
  twice are refused; times compare at full precision; durations bounded; exact versions (no 'latest' in any case);
  lane names are ids; every pattern guards the end of the string; the predicate is checked against run.json.
- Run plans (credential references only), runner capability manifests and the runner event stream, with its
  verifier and hand-written stream vectors.
- Checkpoint manifests and the decision function (pure, with hand-written vectors); the reader derivation keeps
  `not` subtrees strict and accepts an unknown kind in a union discriminated by `kind`.
