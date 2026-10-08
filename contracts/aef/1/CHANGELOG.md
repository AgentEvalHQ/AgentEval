# AEF 1.0 changelog

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
