# AEF v2: specification (draft)

**Status: draft, unreleased.** No producer writes v2 yet. "Must" below is a requirement on a conforming writer or
reader; the schemas in `schemas/` and the corpus in `conformance/` are part of this specification.

## 1. A run is one folder

A run is a self-contained folder. Its identity is in `run.json`, never in the path, so a reader finds runs by looking
for `run.json`. The conventional location is `<root>/.agenteval/runs/<yyyy>/<mm>/<runId>/`; nothing depends on it.

| File | Schema | Required |
|---|---|---|
| `run.json` | `run` | always |
| `results.ndjson` | `result`, one line each | always (empty for a run aborted before any result) |
| `metrics.json` | `metrics` | always |
| `summary.json` | `summary` | once the run is sealed |
| `evidence.ndjson` | `evidence`, one line each | when a result cites evidence |
| `gates.ndjson` | `gate-decision`, one line each | when the producer took a gate decision |
| `blobs/sha256/<ab>/<hex>` | none: raw bytes | when a line references a blob |
| `seal.json` | `seal` | once sealed |
| `attestation.dsse.json` | DSSE envelope | optional |
| `overlays/events.ndjson` | `overlay-event`, one line each | after close, when anything is decided or noted |
| `overlays/seal-<nnnn>.json` | `overlay-seal` | one per batch of overlay events |
| `overlays/seal-<nnnn>.dsse.json` | DSSE envelope | optional: a batch's signature (§7) |
| `traces.otlp.jsonl` | OTLP/JSON, one `ExportTraceServiceRequest` per line | optional |
| `ext/…` | none: producer files | optional; sealed like every other file |

A run folder holds only these files. A blob's file name is the SHA-256 of its bytes (64 lower-case hex), in a folder
named by its first two characters. Every path in a run folder is ASCII letters, digits, `.`, `_`, `-` and `/`, so no
operating system re-encodes or re-orders it. A file nobody sealed (a `.DS_Store` a file manager added) makes the run
fail verification: tools that copy runs must not add files.

**JSON files** are UTF-8 without a byte-order mark. **NDJSON files** are UTF-8 without a byte-order mark; lines are
separated by LF (0x0A) only and a writer writes no CR; every line, the last included, ends in LF; there are no blank
lines; an empty file is valid. A reader splits on LF alone: U+2028 and U+2029 inside a JSON string are text, not line
breaks (the corpus has one).

**A closed run never changes.** While `status` is `running` the producer may rewrite its files. When it closes the
run (`completed` or `aborted`) it writes their final form, and from then on nothing edits them. Everything added later
goes to `overlays/`.

## 2. Versions: writers are strict, readers are tolerant

Every JSON file and every NDJSON line carries `schemaVersion`, `MAJOR.MINOR` (this draft: `2.0`), except the in-toto
statements (`seal.json`, `overlays/seal-<nnnn>.json`), whose `_type` is fixed by in-toto: they carry it in
`predicate.schemaVersion`.

- A **writer** must produce documents valid against `schemas/writer/`: only known fields, only known enum values, its
  own version.
- A **reader** must accept documents valid against `schemas/reader/`: unknown fields are ignored, an unknown enum value
  is read as "other", and any minor of major 2 is accepted. A reader must refuse a major it does not know, with a
  message that says so. The reader schemas are the writer schemas with exactly those relaxations
  (`tools/derive_reader.py`), plus an unknown kind in a union discriminated by `kind`; every other rule (required fields,
  types, patterns, lengths, conditional rules, prohibitions) holds for both.
- **A minor version only adds** optional fields, enum values and union kinds. Changing a bound, a pattern or what is
  required is a new major version.
- **Patterns are the rule; `format` is an annotation.** Times, URIs and ids are checked by their patterns, whether or
  not a validator asserts `format`. A pattern's end is the end of the string: the schemas write `(?!\n)$` or a
  `maxLength`, because `$` alone also matches before a final newline in Python and .NET.

## 3. `run.json`

The header: who produced the run (`producer`), what was evaluated (`subject`, `deployment`), with which cases
(`suite`, with a content `digest` when frozen and an `executionPolicy` for repeated trials), which judges, the
configuration as applied (`config.thresholds` maps a metric or result path to the rule its verdict used), when, what
content was captured (`contentCapture`: `off`, only the size and SHA-256 of prompt and response text; `on`, the
content in `blobs/`) and the cost policy.

- `status` is `running`, `completed` or `aborted`. There is no `sealed` status: sealed is a fact about `seal.json`
  (present and verified), never a field.
- A `completed` run has an `endedAt` time. An `aborted` run has an `endedAt` time and an `abortReason`.
- Times are RFC 3339 in UTC, ending in `Z`, with up to nine fraction digits.
- `ext` is the extension point on every document: readers ignore what they do not know there, and nothing in the
  contract depends on it.

## 4. `results.ndjson`

One line per node of the run's result tree.

### 4.1 States

`state` is one of:

| State | Meaning |
|---|---|
| `passed` | The only pass |
| `failed` | Measured, and below the rule |
| `warn` | A soft fail: not a pass |
| `inconclusive` | Measured, but the evidence does not decide (a split panel, an interval across the threshold) |
| `not_measured` | An input the evaluator needs was not there |
| `not_applicable` | The evaluator does not apply to this case |
| `skipped` | Not run |
| `error` | Did not complete |
| `pending` | Not finished yet (an open run) |

`not_measured`, `not_applicable`, `skipped` and `error` are **typed absences**: never a pass, and never a fail with a
score of 0. A line in one of these states must have a `reason`.

### 4.2 Result ids

`resultId` is deterministic, so the same run read twice gives the same ids:

```
resultId = "r_" + first 32 hex characters of SHA-256( UTF-8( runId + U+001F + caseId + U+001F + path + U+001F + trial ) )
```

The hash is written in lower-case hex. `trial` is the trial number as plain integer digits (a writer writes `3`; a
reader that meets `3.0` uses `3`), or the empty string on a line without one. `caseId` and `path` contain no control
character (C0, DEL or C1), so the U+001F separator cannot appear in them. `conformance/result-ids.json` holds vectors, non-ASCII and a
trial written as `3.0` included.

### 4.3 Composites: how a verdict was reached

A composite node carries `aggregation`: the `strategy` (`WeightedSum`, `Min`, `WeightedMedian`, `CapByWorst`,
`MajorityVote`), its `threshold` and `score`, how many children were `measured` of the `total`, why the others were not
(`unmeasured`: `not_measured`, `not_applicable`, `skipped`, `errored`), and `rulePath`, the branch of the verdict rules that decided the state:

| `rulePath` | The state came from |
|---|---|
| `required-error` | a required child that errored |
| `nothing-measured` | no child being measured |
| `threshold` | the score against the threshold |
| `severity` | the worst severity among the required children |
| `under-covered` | too few children measured (`minimumMeasuredShare`) |

`decisive` lists the children that decided it, as the producer knew them; a reader never reverse-engineers this. Each
child has `parentResultId` and `component` (`weight`, `required`).

### 4.4 Repeated trials

When a case runs several times, each trial's lines carry `trial` (0-based), and one rollup line per case carries
`trials`: `n`, `passed`, the `aggregation` and `agree` (`false` when the trials disagreed: the case is flaky). The case's
result is the rollup line, never one trial. A line carries `trial` or `trials`, never both.

### 4.5 Rules across files

A schema checks one document; a reader also checks these, and treats a run that breaks one as invalid:

- every `resultId` in `results.ndjson` is unique;
- every `parentResultId`, every id in `aggregation.decisive` and every result a gate decision names is a line of
  `results.ndjson`;
- every evidence id a result cites is a line of `evidence.ndjson`;
- every blob a line references exists, and a `reasoning.bytes` equals its blob's size.

## 5. `summary.json` and `metrics.json`

`metrics.json` declares every metric: its `kind`, its `direction` (`higher_better`, `lower_better`, `none`) and its
`scale`. A metric with direction `none` is shown, never coloured as better or worse.

`summary.json` gives, per lane and metric, `n` (measured) beside `N` (asked for) and `notMeasured`, the `value` over the
`n` measured, its uncertainty (`stderr`, `ci`), the `verdict` and the `rule` it came from, and the sufficient
statistics (`sum`, `sumSq`) to pool runs. When `n` is 0, `value` is `null` and `verdict` is `not_measured`.

## 6. `evidence.ndjson` and `gates.ndjson`

An evidence record (`E-…`) is what a result cites: its `kind`, the `digest` of its content, and a `link` that is
exactly one of a blob in the run, a span (`traceId` and `spanId`), or a URI outside the run. These three are fixed:
a new kind of link is a new major version (the link is not a union on `kind`, so a reader cannot take an unknown one
as "other").

A gate decision is a decision the producer took when the run closed (a `--fail-on` gate, a baseline comparison): its
rule, its inputs, whether a comparison it needed was shown comparable, the `outcome` (`ship`, `no_ship`,
`inconclusive`), the exit code and the decisive results. An `incomparable` comparison never yields `ship`.

## 7. Overlays: what is added after close

`overlays/events.ndjson` is append-only. Each event has a `kind` (`approve`, `reject`, `override`, `adjudicate`,
`acknowledge`, `accept_baseline`, `waive`, `annotate`), a `target` (a run, result, requirement or checkpoint), who
(`by`) and when (`at`, and for a waiver `expires`: a time, not a date).

`by.assurance` is what the writer **claims**: `self-attested`, `signed` or `authenticated`. Anyone who can append to the
events file can write any of them, so a reader shows `signed` only when the batch holding the event has a DSSE envelope
(`overlays/seal-<nnnn>.dsse.json`, payload: the exact bytes of `seal-<nnnn>.json`, `payloadType`
`application/vnd.in-toto+json`) that verifies against a key it trusts for that identity, and `authenticated` only for
an event it received from a host it trusts. Otherwise it shows `self-attested`, and says the claim was not verified.

- A `waive` has a `reason` and an `expires`.
- An `override` or `adjudicate` names the `target.result`, the `state` it sets, and a `reason`.

Events are sealed in **batches**. Batch *n* is the bytes of the events appended since batch *n-1*: whole lines, at
`offset` with `length`. `overlays/seal-<nnnn>.json` (1-based, four digits) is an in-toto Statement v1 whose subject is
`overlays/events.ndjson` with the SHA-256 of the batch's bytes, and whose predicate names the previous batch's seal
file and the SHA-256 of that file's bytes (`null` for batch 1). The batches must cover the events file from its first
byte to its last without gaps.

A reader verifies the chain from `seal-0001.json` to the highest-numbered seal present and reports, per seal file: a
seal missing below the highest (`missing`); a predicate `batch` that is not the file's number (`batch-number`); another
`runId` (`run-id`); an `offset` that does not continue the previous batch (`offset`); a range that does not start and
end on a line boundary inside the file (`line-boundary`); bytes that no longer match the batch digest
(`batch-digest`); a `previous` that does not name the previous seal file and the SHA-256 of its bytes, including when
that file is missing (`previous`); and, for `overlays/events.ndjson`, bytes that no batch claims (`uncovered`: a claimed
range counts as covered even when its bytes changed, which `batch-digest` reports). Problems are reported ordered by
path (by its UTF-8 bytes) and then by name. Only files named `seal-` and four digits `.json` are batch seals. `conformance/chain-vectors/` holds a changed
batch, a missing seal and an unsealed tail with their expected problems. The run's own seal is unaffected by any of
them.

**What the chain cannot show:** removing the newest batches together with their seals leaves a shorter chain that
verifies. A reader that has seen a longer chain keeps its length; a signed newest batch, or a copy held elsewhere,
detects it.

## 8. Sealing

A run is sealed over its bytes. There is no canonical JSON: nothing is re-encoded, and the corpus includes non-ASCII
text and a CRLF inside a sealed blob to prove it.

1. **The sealed files** are every file in the run folder except `seal.json`, `attestation.dsse.json` and everything
   under `overlays/`. Only a closed run is sealed, and a host seals only a run whose files validate against the reader
   schemas.
2. **Each file's digest** is the SHA-256 of its exact bytes.
3. **The manifest** has one line per sealed file, ordered by the UTF-8 bytes of its path (`/` separators; `ext/Z` before
   `ext/a-b` before `ext/a.b` before `ext/a/b`), like `sha256sum` output with a size column (so `sha256sum -c` cannot
   read it):

   ```
   <sha256-hex>␠␠<size in bytes>␠␠<path>\n
   ```

4. **The run hash** is the SHA-256 of the manifest's UTF-8 bytes.
5. **`seal.json`** is an in-toto Statement v1: `_type` `https://in-toto.io/Statement/v1`; one `subject` per sealed file
   (`name` = its path, `digest.sha256` = its digest); `predicateType` `https://agenteval.dev/evidence/v2`; and a
   `predicate` with the `runId`, the `runHash`, the producer, subject, deployment, suite and judges from `run.json`,
   `closedAt`, and `sealedBy`: `producer` when the producer sealed the run, `ingest` when a host sealed it on taking
   custody of an unsealed run.
6. **Verification** reports every difference as a path and a problem, ordered by path (by its UTF-8 bytes) and then
   by name:
   - a `seal.json` that is not valid against the seal schema (`seal-invalid`, under `seal.json`): verification stops
     there;
   - a subject listed more than once (`duplicate-subject`, under the subject's name; its digest is not compared, since
     the listings may disagree);
   - a sealed file whose bytes changed (`digest`), a file present but not sealed (`not-sealed`), a sealed file that is
     gone (`missing`);
   - when every file matches its subject, a recomputed run hash that is not `predicate.runHash` (`run-hash`, under
     `seal.json`; when a file differs, the run hash necessarily differs too and adds nothing);
   - a `predicate.runId` that is not `run.json`'s (`run-id`, under `seal.json`);
   - a predicate that says something else than `run.json` about the producer (name, version), the subject (ref,
     version), the deployment (ref), the suite (ref, version, digest), the judges (model, rubric digest, in order) or,
     for a closed run, `closedAt` (which is `run.json`'s `endedAt`) (`predicate`, under `seal.json`);
   - a run whose `run.json` says `running` (`run-open`, under `run.json`).

   A run verifies only when there are none, and is shown verified only after a recomputation. A run with no
   `seal.json` is unsealed: neither verified nor failed.
7. **Signatures** are optional: `attestation.dsse.json` is a DSSE envelope whose payload is the exact bytes of
   `seal.json` (`payloadType` `application/vnd.in-toto+json`), with one or more signatures.

`conformance/seal-vectors/` holds sealed runs with their expected manifests (one whose paths only sort right by their
bytes, beside an `attestation.dsse.json` that is never sealed), and one vector for each kind of difference.

## 9. Checkpoints

A **checkpoint** is a release decision over several evidence lanes (a quality suite, a red-team campaign, a memory
benchmark, a compliance pack) for **one exact subject version** (schema `checkpoint`). A version is an exact string:
no whitespace, at most 128 characters, compared byte for byte, and never "latest" in any case: "latest" is resolved
before anything runs, and the manifest records what was asked (`resolvedFrom`) and what it resolved to (`version`).

- Each **lane** names its `rule`, the requirements it answers, whether it is `blocking`, its `freshness`, and the exact
  sealed `runs` it used: each run's `runId`, its `runHash` (so the evidence is frozen) and its `origin` (`launched` for
  this checkpoint, or `adopted:<source>` for an existing sealed run that matched the exact version and the
  comparability requirements). A lane with no runs yet is pending.
- A lane's **freshness** is the shortest freshness among its requirements, resolved when the checkpoint is planned and
  recorded on the lane (an ISO 8601 duration of days and hours, each at most five digits: `P14D`, `PT36H`, `P1DT12H`).
- A lane's rule is one of: `threshold` (a metric against a value), `severity` (the worst severity allowed), `comparison`
  (no significant regression against a baseline), `evidence-present` (a number of evidence groups exist and verify).
  A family that publishes no pass threshold takes `comparison` or `evidence-present`, never `threshold`. A reader that
  does not know a lane's rule kind cannot evaluate it: the lane's result is `not_measured`.
- `state` moves `draft` → `planned` → `approved_to_spend` → `running` → `evidence_complete` → `decided` → `sealed`.
  `outcome` is `null` before `decided`. From `decided` on it is set: either the decision function's outcome, with its
  input recorded (`decisionInput`) beside its output (`decision`) so anyone can recompute it, or `aborted` when the
  checkpoint was abandoned (spend not approved, runs failed), with an `abortReason` and no decision.
- `budget.approvedBy`, like an overlay's `by`, is a claim: a reader shows its assurance only as far as it verified it
  (§7).

**Rules across the manifest** that a schema cannot express, for a checkpoint decided by the decision function. A
verifier reports them in name order: `outcome` (the outcome is not the decision's), `decision` (the decision is not what
the decision function gives on the recorded input), `lanes` (the input does not decide exactly the manifest's lanes,
with the same names, blocking and freshness, in the same order), `version` (the input is for another version),
`evidence` (a lane has runs but no result in the input, or a result but no runs). `conformance/checkpoints/` holds
manifests that break each, with the expected problems.

**Expiry at read time.** A decided or sealed checkpoint is never rewritten. A reader that shows it later evaluates the
decision function again with the time of reading (and any newer version it knows of as `supersededBy`): when that gives
`expired`, it shows the checkpoint as expired beside the recorded outcome.

## 10. The decision function

`Decide(input) → output` (schema `decision`: `$defs/input` and the document itself) is pure: no I/O and no clock, the
evaluation time is an input. Anyone can recompute why a checkpoint was approved, blocked, inconclusive or expired.

**Input:** the checkpoint's exact `subjectVersion`, `evaluatedAt`, an optional `supersededBy` (a newer version known at
that time), and per lane: `lane`, `blocking`, an optional `freshness`, and `result`: what the lane's rule gave on its
evidence (`passed`, `failed`, `not_measured`, `incomparable` with the differing `axes`), the version it was produced
for and when the **oldest** run the result relied on closed (`oldestClosedAt`: a re-run yesterday does not make
60-day-old evidence fresh), or `null` when there is no evidence. Computing a lane's result from its runs is outside this
function. A checkpoint has at least one lane and no lane twice: the function refuses anything else rather than decide
it. Versions compare byte for byte. Times compare at the full precision written (up to nine fraction digits): an
implementation must not round them.

**Each lane's status**, in this order:

1. `result` is `null` → `missing` (reason `missing:<lane>`).
2. The result is for another version → `missing` (reason `wrong-version:<lane>`).
3. `oldestClosedAt` is later than `evaluatedAt`: the evidence did not exist then → `missing` (reason
   `future-evidence:<lane>`).
4. A `freshness` is set and `oldestClosedAt + freshness < evaluatedAt` → `stale` (reason `stale:<lane>`). Evidence
   exactly as old as the freshness is still fresh.
5. Otherwise the result's status (a status this version does not know reads as `not_measured`, failing closed): `passed` (no reason), `failed` (reason `failed:<lane>`, or `advisory-failed:<lane>`
   for a lane that is not blocking), `not_measured` (`not-measured:<lane>`), `incomparable` (`incomparable:<lane>`,
   and the lane carries its `axes`).

**The outcome**, first rule that holds:

1. `supersededBy` is set and differs from `subjectVersion`, or any lane is `stale` → `expired`.
2. Any blocking lane is `failed` → `blocked`.
3. Any lane, blocking or not, is `missing`, `not_measured` or `incomparable` → `inconclusive`.
4. Otherwise → `approved`. A failed advisory lane does not block, and is reported.

Missing evidence is never converted into a pass, and nothing is averaged.

**Reasons** are codes, so every implementation produces the same list: the lane reasons in input order, then
`superseded:<version>` if it applies, then `outcome:<outcome>`.

`conformance/decision-vectors/` holds inputs with expected outputs written by hand from these rules; the reference
implementation `tools/aef_decide.py` and the .NET one (`AgentEval.Results`) reproduce them.

## 11. Run plans and runners

A **run plan** (schema `run-plan`) is what a runner is asked to evaluate: the exact subject version (never "latest", in
any spelling; a container run also names the image by digest), the suites and the lanes they serve, the limits it must
stop at (`maxUsd` always; `cases` and `timeout` when set), the content policy, the isolation (`process`, `container`,
`remote-zone`), the provider (`local`, `docker`, `k8s`, `ci:<name>`), the tags a runner must carry, and the credentials
it needs **as references only** (`env:<NAME>`, `keychain:<entry>`, `vault:<path>`): the runner resolves them where it
runs, and a plan never holds a secret value.

A **runner capability manifest** (schema `runner`) says what a runner is: its id and workload identity (and the id of
the key it signs sealed evidence with, if any), its kind (`local`, `remote`, `ci`, `pool`), OS, runtime, the providers
it supports, its tags, GPU and network zone, and its version. A plan's `runnerSelector` matches when the runner carries
every tag in it.

## 12. The event stream

A runner reports a job as NDJSON (§1) on its standard output (a local runner) or over its channel (a remote one), one
event per line (schema `runner-event`, a union on `kind`):

| `kind` | Carries |
|---|---|
| `job.accepted` | the `planId` and the `runnerId` |
| `plan.estimated` | the cases and the cost range (`usdLow`, `usdHigh`) with the price table |
| `spend.updated` | `spentUsd`: the total so far |
| `case.completed` | the `caseId` and its `state` |
| `lane.completed` | the `lane` and its `status` |
| `evidence.produced` | a sealed run: its `runId` and `runHash` |
| `job.cancelled` | the `reason` |
| `job.failed` | the `reason`, and the `limit` (`maxUsd`, `cases`, `timeout`) when the runner stopped at one |
| `job.sealed` | every run the job produced (`runs`) |

Every event has `seq`, `jobId` and `at`. A verifier checks a finished stream line by line and reports, per event
(`event:<n>`, the 1-based line): the first event is not `job.accepted` (`first`); a `seq` that is not the previous plus
one (`seq`, starting at 1); another `jobId` than the first event's (`job-id`); an `at` earlier than the previous one
(`time`); an event after `job.sealed`, `job.failed` or `job.cancelled` (`after-terminal`); a `job.accepted` for another
plan than the one given (`plan-id`); a `spentUsd` below the previous one (`spend-decreased`) or above the plan's
`maxUsd` (`over-budget`); a `job.sealed` naming a run no `evidence.produced` announced (`unannounced-run`). Problems
come in event order and then by name; a stream with no terminal event ends with (`stream`, `no-terminal`).

`conformance/protocol/` holds valid and invalid plans and manifests, and streams with the problems written by hand;
`tools/aef_stream.py` and the .NET verifier (`AgentEval.Results`) reproduce them.

## 13. Not yet in this draft

These are specified in the design and will be added to v2 before it is released, each with its schema and vectors:

- the checkpoint seal (`checkpoint.seal.json`, an in-toto statement over the manifest and its runs' hashes) and its
  vectors;
- DSSE vectors (valid, wrong key, tampered payload) with test keys, for the run and for overlay batches;
- `tools/schema-diff`, which fails a schema change that removes, narrows or adds a required field without a new major;
- generated types for Python, TypeScript and Go, each with a conformance runner;
- v1-to-v2 migration vectors;
- `views.json` and the catalog manifest.

## 14. Conformance

A writer or reader in any language conforms when it passes `conformance/`:

- every file of every run in `valid/` validates against the writer and the reader schemas;
- every document in `invalid/` is refused by the writer schema, and accepted or refused by the reader schema as its
  `expected.json` says (with the rule it breaks);
- every vector in `result-ids.json` reproduces;
- every run in `valid/` keeps the rules across files (§4.5) and the NDJSON rules (§1);
- every vector in `seal-vectors/` verifies with exactly its expected differences (none for a match), with the manifest
  given where there is one;
- the overlay batches of every run in `valid/` form an unbroken chain, and every vector in `chain-vectors/` reports
  exactly its expected problems;
- every checkpoint in `checkpoints/` is accepted or refused by the writer and the reader schemas as its `expected.json`
  says, and a schema-valid one verifies with exactly its expected problems;
- every vector in `decision-vectors/` reproduces exactly;
- every plan and runner manifest in `protocol/` is accepted or refused as its `expected.json` says, every event of
  every stream in `protocol/streams/` is valid, and each stream's verification reports exactly its expected problems.
