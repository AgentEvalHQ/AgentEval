# 9. Conformance

## 9.1 Classes

An implementation conforms to a class (§1.7) when it meets the requirements below and passes every corpus vector of
that class. Requirements not listed for a class still apply to it where it does what they describe.

| Class | Requirements | Vectors (§9.2 `kind`) |
|---|---|---|
| **Producer** | ENC-1–ENC-19, RUN-1–RUN-15, RES-1–RES-11, SUM-1–SUM-9, EVD-1–EVD-3, GATE-1–GATE-2, VER-1, VER-2, VER-6, VER-9 | `document` (writer side), `run` (valid runs), `result-id`, `paths`, and the write-side `summarize` and `produce` |
| **Sealer** | SEAL-1–SEAL-5, SIG-1–SIG-3; signs with at least one of [SIG-2]'s algorithms | `seal` (expected manifests), `signature` (signing side), and the write-side `seal-write`, and `sign` for the algorithms it claims |
| **Reader** | ENC-1–ENC-19, VER-3, VER-4, VER-8 (the reading rules of §7.3), VER-9 | `document` (reader side, including every encoding defect as a single document), `reader-only` |
| **Run verifier** | Reader, plus §3.9, SEAL-4, SEAL-6, SIG-4, SIG-5, SIG-7, and OVL-4, OVL-5, OVL-10 (to tell a withheld blob from a missing one) | `run`, `seal`, `encoding`, `paths`; at the *signed* level also `signature`; and the Reader's |
| **Overlay verifier** | OVL-1–OVL-11, SIG-4, SIG-5 (batch signatures and redaction authority, [OVL-3], [OVL-10]) | `chain`, `overlay-view`, `signature` |
| **Checkpoint verifier** | CKP-1–CKP-10, LANE-1–LANE-11, DEC-1–DEC-5, SIG-8, and Run verifier | `checkpoint`, `lane`, `decision`, `signature` ([CKP-9]), and the Run verifier's |
| **Decision engine** | DEC-1–DEC-5 | `decision` |
| **Runner** (*at risk* in 1.0, below) | PLAN-1–PLAN-10, STRM-1–STRM-2, RUN-12, and Producer and Sealer for the runs it produces | `plan`, `matching`, and the Producer's and the Sealer's |
| **Stream verifier** | STRM-1–STRM-4, and Run verifier ([STRM-4] verifies the runs a stream reports) | `stream`, `plan-conformance`, and the Run verifier's |

A class whose requirements include another class passes that class's vectors too; `index.json` lists each vector
under every class that must pass it, so a runner selects a class's vectors by its name alone.

**The Runner class is released *at risk* in AEF 1.0** (GOVERNANCE.md, release criteria): neither implementation that
passes the corpus is a runner, so its requirements have not been tested by a second implementation, and they may
change in 1.1 more than a minor version normally allows. The reference tools include a minimal runner
(`tools/aef_runner.py`, against a scripted target: it takes only plans that ask for `scripted`, and [STRM-3] and
[STRM-4] find nothing in its jobs), so the class has one implementation; it is still at risk until a second,
independent runner passes.

A Run verifier conforms at the **intact** level, or at the **signed** level when it also verifies signatures (§4.4).
`index.json` marks the vectors only the signed level must pass (`"level": "signed"`: the signature vectors, and
the runs and seals verified against a trust policy).

The **write-side** vectors (`summarize`, `produce`, `seal-write`, `sign`) test a Producer and a Sealer as writers: the
implementation computes or writes something, and the conformance runner judges it, partly through the reference
verifier (§9.3), so a correct validator over a broken writer does not pass. `produce` gives a Producer the facts of a
run as a producer has them when it writes one (the header, the metrics, each case's states and its own decisions)
and judges the run it writes: what [RES-4]–[RES-8] derive from those facts (result ids, parents, trial numbers,
rollups, aggregation counts) and the summary. A Producer's evidence, gate decisions and blobs are not yet written
under test: the run vectors check them only as a reader would (a gap 1.1 may close). A Runner has no write-side
vector of its own: what it writes is a job
over a live subject, which a corpus cannot hold. It is tested by `plan` and `matching`, and by the Producer and Sealer
vectors, which it passes for the runs it produces; the event stream it writes is checked against [STRM-3] and
[STRM-4] whenever someone verifies it (a Stream verifier).

A Sealer signs with at least one of [SIG-2]'s algorithms (a verifier supports both; a signer uses one), and passes
the `sign` vectors of those it claims: `index.json` gives each `sign` vector's `algorithm`, `ecdsa-p256` or
`ed25519`.

## 9.2 The corpus

`conformance/` is part of this specification. Every expected result in it is written down independently of the code
that checks it: by hand, or by a generator in `tools/` that implements only the rule it writes (a seal, a result id),
and is cross-checked by a second, independent implementation.

- **[CONF-1]** `conformance/index.json` lists every vector: its `id`, `kind`, the `classes` it tests, the `rules` it
  concerns (ids from this specification), the `path` of its files, and the SHA-256 of each file. The SHA-256 of
  `index.json`'s bytes identifies a corpus version.

| `kind` | Input | Expected (in `expected.json` beside the input, unless the vector file holds it) |
|---|---|---|
| `document` | one document and its schema name | `writer` and `reader`: `valid` or `invalid` |
| `reader-only` | a document a later minor could write | reader `valid`, and the reading of §7.3 (`reads`) |
| `encoding` | a file with an encoding defect | the run verifier's problems |
| `run` | a run folder | the run verifier's problems (empty for a valid run) and its outcome |
| `result-id` | `runId`, `caseId`, `path`, `trial` | the `resultId` |
| `seal` | a run folder | the manifest, and the problems of §4.1 |
| `chain` | a run folder with overlays | the problems of §4.2 |
| `overlay-view` | a run folder with overlays, and a time | the effective view (§4.3) |
| `signature` | an envelope, the signed file, a trust policy | per-signature results (§4.4) |
| `checkpoint` | a checkpoint manifest | schema validity and the problems of [CKP-7] |
| `lane` | a checkpoint manifest and its runs | each lane's recomputed result (§5.3) and the problems of [CKP-8] |
| `decision` | a decision input | the output, or `expectedError` |
| `plan` | a run plan or runner manifest | schema validity |
| `matching` | a plan and a runner manifest | whether the runner takes the plan ([PLAN-7]: it can take it and knows its values) |
| `stream` | an event stream, its plan and the plan's digest | the problems of [STRM-3] |
| `plan-conformance` | an event stream, its plan, the runs it produced, and optionally a trust policy | the problems of [STRM-4] |
| `fixture` | files several vectors use (test keys, a stream's plans) | nothing to run: the runner checks their digests |
| `paths` | a list of paths in a run folder | the problems of [RUN-3] |
| `summarize` | a run without its `summary.json`, and the entries to compute (lane, metric, path; optionally `aggregate`, `rule`, `verdict`) | the `summary.json` [SUM-2]–[SUM-9] give; or that it refuses the request |
| `produce` | a scenario: the facts of a closed run (§9.2.1) | the lines of its `results.ndjson` ([RES-4]–[RES-8]), as a set, and its `summary.json`; or that it refuses the scenario |
| `seal-write` | an unsealed run, `sealedBy` and `sealedAt` | the subjects and predicate of the `seal.json` it writes ([SEAL-1]–[SEAL-5]), and the outcome `intact`; or that it refuses the run (open, a `sealedAt` before it closed, or sealed on custody with a path or file that is not valid) |
| `sign` | a file, its payload type, a test private key of one algorithm | an envelope that verifies for that key's identity ([SIG-1]–[SIG-3]); for Ed25519 the signature itself |

- **[CONF-2] Comparing problems.** A list of problems is compared as an ordered list of `[path, code]` pairs (codes
  alone for [CKP-7]), in the order of §3.9; a vector passes only when the lists are equal. Times are compared as written, numbers as binary64.

### 9.2.1 Vector files

Each vector is a folder holding `expected.json` and its input; a run is always in a `run/` subfolder, so that
`expected.json` is never a file of the run. Every `expected.json` has `kind` and `rules` (the rule ids it concerns).

| `kind` | Folder | `expected.json` (beyond `kind` and `rules`) |
|---|---|---|
| `document` | `invalid/<name>/document.json` | `schema` (a schema name), `writer` and `reader` (`valid` or `invalid`), `why` |
| `reader-only` | `reader-only/<name>/document.json` | `schema`, `writer` (`invalid`), `reader` (`valid`), and `reads`: for each field path (`.name` for an object member, `[i]` for an array item; a member name holding `.`
or `[` is written `["name"]`, a JSON string), the known value a reader takes the unknown one as (§7.3) |
| `run` | `valid/<name>/run/`, `runs/<name>/run/` | `run` (`"run"`), optional inputs `policy` (a trust policy file beside `expected.json`) and `anchors` (a JSON list of trusted run hashes); `outcome` (`unsealed`, `intact` or `invalid`, §4.5), `problems`: the seal problems (§4.1) and the problems of §3.9 together, ordered; with `policy`, `signedBy` (identities, in policy order); with `anchors`, `anchored` (`true` or `false`) |
| `encoding` | `encoding/<name>/run/` | as `run` |
| `seal` | `seal-vectors/<name>/run/` | `run`, optionally `policy` (a trust policy, for redactions, [OVL-10]), `problems` (§4.1 only), and `manifest`: a file beside `expected.json` holding the expected manifest, when the run is sealable |
| `chain` | `chain-vectors/<name>/run/` | `run`, `problems` (§4.2 only) |
| `overlay-view` | `overlay-views/<name>/run/` | `run`, `at` (the time the view is computed at), `view` (below) |
| `checkpoint` | `checkpoints/<name>/document.json` | `schema`, `writer`, `reader`, `why`, and `problems` ([CKP-7] codes) when the reader accepts it |
| `lane` | `lane-vectors/<name>/checkpoint.json` and `runs/<n>/` | `checkpoint`, `runs` (the folder of runs, found by their `run.json`), `lanes`: per lane in manifest order, `lane` and `result` (§5.3; `null` for none), and `problems` ([CKP-8]); optionally `envelope` (the checkpoint's signature) and `anchors` (compared when stated) |
| `signature` | `signature-vectors/<name>/` (test keys in `signature-vectors/keys/`) | inputs: `envelope`, `file` (the signed file), `payloadType` (the type §4.4 gives that file), `policy` (a trust policy, below); expected: `envelopeResult` (`null`, `malformed` or `payload-mismatch`), `signatures` (per signature in envelope order: `keyid`, `result`, and `identity` when `verified`; empty when malformed), and `verifiesFor` (the identities the envelope verifies for, in policy order); or `policyRefused: true` when [SIG-3] refuses the trust policy (the operation exits 2) |
| `result-id` | `result-ids.json`: a list | each item: `runId`, `caseId`, `path`, `trial` (or `null`), `resultId` |
| `paths` | `paths.json`: a list | each item: `name`, `paths`, `problems` |
| `decision` | `decision-vectors/<name>.json`: one file per vector | `input` (a decision input, §5.4), `description`, `rules`, and either `expected` (the output: `outcome`, `lanes`, `reasons`) or `expectedError` (the function refuses the input; the value names why, such as `no-lanes`, for people: an implementation's message need not match); `schemaInvalid: true` when the input is also invalid against the decision schema, `readerOnly: true` when only the reader schema accepts it |
| `plan`, `matching`, `stream` | `protocol/` | as `tools/aef_stream.py` documents |
| `plan-conformance` | `protocol/plan-conformance/<name>/`: `events.ndjson`, `plan.json`, and the runs in `runs/` (found by their `run.json`) | `events`, `plan` and `runs` (those inputs, beside `expected.json`), optionally `policy` (a trust policy, for runs with authorized redactions), `problems`: the problems of [STRM-4] as `[path, code]` pairs, ordered, and `why` |
| `summarize` | `write-vectors/summarize/<name>/`: the run in `run/` (`run.json`, `results.ndjson`, `metrics.json`; no `summary.json`) and `request.json` | `run`, `request` (the request file: `{"lanes": [{"lane", "metrics": [{"metric", "path", "aggregate"?, "rule"?, "verdict"?, "value"?}]}]}`, the entries to compute in order; `verdict` is the producer's under its `rule`, and `value` its figure for an `aggregate` method AEF does not define, [SUM-8]), and either `summary` (the expected `summary.json`) or `refused: true` (an input error of §9.3); `why` |
| `produce` | `write-vectors/produce/<name>/scenario.json` | `scenario` (the scenario file, below), and either `results` (a file beside `expected.json` holding the expected lines of `results.ndjson`, in no particular order) and `summary` (the expected `summary.json`), or `refused: true` (a scenario that contradicts itself: an input error of §9.3); `why` |
| `seal-write` | `write-vectors/seal-write/<name>/run/` | `run`, `sealedBy`, `sealedAt`, and either `manifest` (a file beside `expected.json` holding the expected manifest) and `predicate` (the expected predicate), or `refused: true` (the run is open; `sealedAt` is before its `endedAt`; or, sealed by `ingest`, a path breaks [RUN-3] or a file is not valid against its reader schema: [SEAL-1]); `why` |
| `sign` | `write-vectors/sign/<name>/` (private test keys in `signature-vectors/keys/`) | `algorithm` (`ecdsa-p256` or `ed25519`: what a Sealer needs to run it, also in `index.json`), `file`, `payloadType`, `key` (an unencrypted PKCS#8 PEM private key, P-256 or Ed25519, as a path from the vector's folder), `policy` (a trust policy holding its public key), `keyid`, `identity`, `why`, and for Ed25519 `sig` (the expected signature, base64) |

**The effective view** of an `overlay-view` vector (§4.3) is an object with:
- `results`: one entry per result that a verified `override` or `adjudicate` targets, in `results.ndjson` order:
  `resultId`, `sealedState`, `effectiveState`, and `event` (the `eventId` that set it);
- `reviews`: one entry per target of a verified `approve` or `reject`, the run first and then results in
  `results.ndjson` order: `target` (`"run"` or the `resultId`), `status` (`approve` or `reject`), `event`;
- `waivers`: every verified `waive`, in file order: `target` (the event's target without `run` and `runHash`),
  `expires`, `active` (`at` ≤ the view time < `expires`), `event`;
- `withheld`: the blobs authorized `redact` events name (redacted, whether or not they are gone yet) ([OVL-10]; a vector that has some carries the `policy`
  that authorizes them), in file order;
- `unsealedEvents`: the number of lines after the last verified batch that a reader reads (an unfinished last line
  is none; in a file beyond its limits, no line after the verified batches is read, [OVL-5]);
- `assurance`: for each event of the verified batches that takes part in the view, in file order, `event` (its
  `eventId`) and `shown`: the assurance a reader shows for it ([OVL-3]): `signed` when the batch holding it carries a
  signature that verifies, under the trust policy, for the event's own `by.identity`, whatever the event claims, and
  otherwise `self-attested` (a reader of files never shows `authenticated`). A vector that states it is compared.

**A trust policy** is a JSON object `{"keys": [{"identity": …, "publicKey": <SPKI PEM>, "may": ["redact"]?}]}`, valid
against `schemas/writer/trust-policy.schema.json` ([SIG-4]). A key's id is computed from its public key ([SIG-3]),
never read from the policy. `seal`, `run` and `lane` vectors may carry a
`policy` file beside `expected.json`; `run` vectors with withheld blobs also expect `withheld` (a count).

**A scenario** (the input of `produce`) is a JSON object holding the facts a producer has when it writes a run, and
nothing it must derive:
- `run`: the `run.json` it writes, closed (`status` `completed` or `aborted`);
- `metrics`: the `metrics.json` it writes;
- `cases`: the result trees, one per case and root path, each a node (below) with its case's `caseId`;
- `summary`: the entries to compute, as a `summarize` request.

A node holds the facts of one line: `path`, `evaluator` and `state`, and optionally `scores`, `severity`, `reason` and
`lane`, all written as given; `component` (`weight`, `required`), on every child and only there; on every node with
children, `aggregation`: the producer's `strategy` and `rulePath`, and optionally `threshold`, `score` and `decisive`
(the paths of the children that decided it); and `children`, the nodes one level down (a child's `path` is its
parent's, then `/` and a name without `/`: a scenario's paths follow its trees, so a Producer can check them). A case run in trials also has `trials`:
the `aggregation` its rollups carry (and `k`, for `PassAtK`), and `trees`, one node per trial in trial order, each at
the case's `path`. The case's own node and its children are then its rollups, with a node at each path its trial
trees have and at no other.

The producer writes one line per node, with `schemaVersion` and its case's `caseId`, and derives the rest:
- `resultId` ([RES-4]), and `trial` on every line of a trial's tree (0 for the first);
- `parentResultId`: the id of its parent's line in the same tree (a case's own node and a trial's root have none);
- on a rollup, `trials`: `n`, `passed` and `agree` over the trial lines at its path ([RES-8]), and the case's
  `aggregation` (and `k`);
- on a node with children, `aggregation`: its facts, `total` (its children), `measured` (those in a measured state,
  [RES-1]), `unmeasured` (the others, counted by state) and `decisive` (the children's ids) ([RES-5], [RES-6]);
- `summary.json`, from those lines, as `summarize` computes it ([SUM-2]–[SUM-9]).

§3.4 orders no line of `results.ndjson`, so neither does a `produce` vector: its lines are compared as a set.

**Generated vectors.** A vector whose input would be too large for the corpus (a run of 100,000 files, a 40 MiB seal)
holds only `expected.json`, with `generate`: a list of steps the conformance runner applies, in order, to a copy of the
vector's folder before it runs the vector. Each step is an object with one member:

| Step | Does |
|---|---|
| `{"copy": [SOURCE, TARGET]}` | copies the corpus file or folder `SOURCE` (relative to `conformance/`) to `TARGET` |
| `{"remove": PATH}` | removes a file or a folder |
| `{"files": [FOLDER, N]}` | creates `N` empty files in `FOLDER`, named `0` to `N`−1 |
| `{"write": [PATH, PARTS]}`, `{"append": [PATH, PARTS]}` | writes or appends a file of `PARTS`: `[text, count]` pairs, each text in UTF-8 repeated `count` times |
| `{"link": [PATH, TARGET]}` | makes `PATH` a symbolic link to `TARGET` (both relative to the vector's folder; the link itself holds the relative path between them). A runner on a platform that cannot create one skips the vector and reports it as skipped |

Paths are relative to the vector's folder, `/`-separated, with no `..` segment, no drive and no leading `/`: a
runner refuses a recipe whose paths would leave the corpus or the vector. The implementation is given the generated folder as it
would be given a stored one: generating is the runner's job, never the implementation's. `limits/` holds these vectors,
each limit of [ENC-17] at its value or one beyond.

## 9.3 Running the corpus

- **[CONF-3]** A conformance runner reads `index.json`, selects the vectors of the classes it claims, performs for each
  the operation its `kind` names on the input, and compares the result with the expected one. It checks the SHA-256 of
  every file it reads against the index, so a modified corpus cannot pass.
- `tools/aef_conformance.py` is such a runner for the reference implementation (`tools/aef_verify.py`, and
  `tools/aef_produce.py` for the write operations). An
  implementation in another language is driven by it through this command-line contract: one invocation per
  operation, input paths as arguments, one JSON value (UTF-8, no BOM) on standard output, exit status 0 when the
  operation ran and 2 with a message on standard error for a usage or input error. Problems are `[path, code]` pairs
  in the order of §3.9, except a checkpoint's [CKP-7] codes, which are codes alone in code order. Times are RFC 3339
  UTC strings ([ENC-8]).

  | Vector kind | Operation | Output |
  |---|---|---|
  | `run`, `encoding` | `run DIR [--policy P] [--anchors A]` | `{"outcome", "problems"}`; `withheld` (a count) when not 0; `signedBy` (identities, in policy order) with `--policy`; `anchored` (true or false) with `--anchors`, a file holding a JSON list of run hashes |
  | `seal` | `seal DIR [--policy P]` | `{"manifest": text, "runHash": hex, "problems"}` |
  | `chain` | `chain DIR` | `{"problems"}` |
  | `overlay-view` | `view DIR --at T [--policy P]` (`--at` is always given) | the effective view of §9.2.1 |
  | `document`, `reader-only`, `plan` | `document SCHEMA FILE` | `{"writer": "valid"/"invalid", "reader": …, "reads": {field path: value as read}}` (every line of an NDJSON file; `reads` is `{}` unless the reader accepts the file and it holds one document or one line). `valid` means the file reads ([ENC-1]–[ENC-7], within the limits [ENC-17] sets for the file the schema names: 40 MiB for `seal`, 4 MiB per JSON document or NDJSON line otherwise) and every document is valid against that side's schema |
  | `checkpoint` | `checkpoint FILE` | `{"writer", "reader", "problems": [code, …] or null when the reader refuses it}` |
  | `lane` | `lanes CHECKPOINT --runs DIR [--at T] [--policy P] [--envelope E]` | `{"lanes": [{"lane", "result"}], "problems", "anchors"}`: `anchors` the run hashes the checkpoint anchors ([CKP-9], [SIG-8]: those of its lanes' runs and comparison baselines, in byte order) when it has no problem of [CKP-7] or [CKP-8] and `E` holds a signature verified for an identity of `P`; otherwise `[]` |
  | `signature` | `signature ENVELOPE FILE POLICY --payload-type T` (the type [SIG-1] gives the file) | `{"envelopeResult": null or code, "signatures": [{"keyid", "result", "identity"?}], "verifiesFor": [identity]}` |
  | `decision` | `decide FILE` | `{"output": decision}` or `{"error": message}` when the function refuses the input (exit 0) |
  | `matching` | `match PLAN RUNNER` | `{"matches": true or false}` |
  | `stream` | `stream EVENTS PLAN` | `{"problems": [[where, problem]]}` |
  | `plan-conformance` | `conform EVENTS PLAN RUNS [--policy P]` | `{"problems"}` at `run:<runId>` and `job` |
  | `paths` | `paths FILE` | `[{"name", "problems"}]` for the corpus file |
  | `result-id` | `result-id RUNID CASEID PATH [TRIAL]` | `{"resultId"}` |
  | `summarize` | `summarize DIR REQUEST` | the `summary.json` document of the run in `DIR` for the entries of `REQUEST`: `schemaVersion`, the run's `runId` ([SUM-2]), and per lane and entry, in request order, `metric`, `path`, `N`, `n`, `notMeasured`, `sum`, `sumSq`, `value`, `verdict` (`not_measured` when `n` is 0, [SUM-6]; otherwise the request's, or `scored` when it gives none), and `rule` and `aggregate` as requested. Input errors (exit 2): a metric `metrics.json` does not declare; a lane named twice, or one lane, metric and path twice ([SUM-9]); an `aggregate` method AEF does not define without a `value`; a `value` for an entry AEF computes (the mean or sum, or `median`, `min` or `max`), which would contradict it; results or metrics that do not read |
  | `produce` | `produce SCENARIO OUT` | writes the run the `SCENARIO` file describes (§9.2.1) in the folder `OUT`, which does not exist yet or is empty: `run.json` and `metrics.json` as given, `results.ndjson` and `summary.json`, and nothing else; `{"results": the number of lines}`. Input errors (exit 2), with nothing written: a scenario not of that shape; a run that is not closed; a `pending` node ([RES-3]); a child whose `path` is not its parent's and one more level; a `decisive` path that is no child's ([RES-6]); a node with children and no `aggregation`, or a child without `component` ([RES-5]); a trial tree rooted elsewhere than its case, or a path the case's tree and its trial trees do not both have ([RES-8]); two lines of one case at one path and trial (they would have one `resultId`, [RES-4]); and the input errors of `summarize` |
  | `seal-write` | `seal-write DIR --sealed-by B --sealed-at T` | writes `DIR/seal.json` ([SEAL-5]) and changes nothing else; `{"runHash": hex}`. Input errors (exit 2), with nothing written: an open run; a `T` before the run's `endedAt`; with `--sealed-by ingest`, a run with a path that breaks [RUN-3] or a file that is missing, does not read, or is not valid against its reader schema ([SEAL-1], [ENC-16]) |
  | `sign` | `sign FILE KEY --payload-type T` (`KEY`: an unencrypted PKCS#8 PEM private key, P-256 or Ed25519) | the DSSE envelope over `FILE`'s bytes ([SIG-1]): `payloadType`, `payload`, and one signature with the key's `keyid` ([SIG-3]). Input errors (exit 2): a key of an algorithm the implementation does not sign with, a key that is not an unencrypted PKCS#8 PEM, or an EC key on a curve other than P-256 |

  For `lanes`, `--at` defaults to the time of the call. Wherever an operation is given a plan (`match`, `stream`,
  `conform`) or a trust policy (`--policy`, [SIG-4]), one the reader refuses is an input error (exit 2), a plan whose
  `timeout` is not a duration ([ENC-9]) among them.

  The write operations are judged rather than compared byte for byte: JSON formatting is free, so two conforming
  writers may write different bytes for one `summary.json`, `results.ndjson` or `seal.json`, and an ECDSA signature
  need not be deterministic. The runner judges what was written with the reference verifier, whichever
  implementation is under test:
  - `summarize`: the output is valid against the writer `summary` schema; `runId`, the lanes and entries in request
    order, `N`, `n`, `notMeasured`, `verdict`, `rule` and `aggregate` are as expected; `sum`, `sumSq` and `value` match
    the expected values (the exact computation, rounded once) under §3.6's rule, within 1e-9 × max(1, |expected|);
    and the run, with the output added as its `summary.json`, verifies `unsealed` with no problems.
  - `produce`: the runner gives a fresh `OUT`. It then holds the four files and no other; `run.json` and
    `metrics.json` are the scenario's `run` and `metrics`; every line of `results.ndjson` is valid against the writer
    `result` schema, and the lines are the expected ones in any order: matched by `caseId`, `path` and `trial`,
    member by member, with no line missing, added or written twice. Everywhere, numbers compare under §3.6's rule and
    times as times ([ENC-8]), and a value and the absence that means the same compare equal: a null member and none
    ([ENC-2]), an `unmeasured` count of 0 and none ([RES-6]), an empty `decisive` and none, a `contentCapture` of `on`
    and none ([RUN-11]); `decisive` is compared as a set.
    `summary.json` is judged as `summarize`'s output is, and the run verifies `unsealed` with no problems.
  - `seal-write`: the runner seals a fresh copy of the run, never the corpus's. The `seal.json` written is valid
    against the writer `seal` schema; its subjects are the expected manifest's paths and digests, in its order; its
    predicate equals the expected one (`closedAt` and `sealedAt` compared as times, [ENC-8], at full precision); the
    printed `runHash` is the SHA-256 of the expected manifest; every other file of the copy is unchanged and none is
    added; and the copy verifies `intact` with no problems. `seal.json`'s bytes are not compared.
  - `sign`: the envelope has one signature, under the key's `keyid`, and its base64 is in the standard alphabet with
    padding ([SIG-1]); the `signature` operation, with the vector's trust policy, gives `envelopeResult` null, that
    signature `verified` for the vector's identity, and `verifiesFor` that identity. For Ed25519, whose signatures are
    deterministic ([RFC 8032]), `sig` must also be the expected one; an ECDSA signature is not compared.
  - A vector with `refused: true` passes when the operation is an input error (exit 2); for `seal-write`, the run's
    folder must also be unchanged, and for `produce`, `OUT` must hold no file.

## 9.4 Claiming conformance

- **[CONF-4]** A claim names the implementation and its version, the AEF version (`1.0`), the classes (and the Run
  verifier level; a Sealer claim names its signing algorithms), and the corpus version (the SHA-256 of `index.json`)
  it passed in full. A claim with exceptions is not a conformance claim.

> *Examples:* "aef-tools (the reference tools in `tools/`) conform to AEF 1.0 in every class, the Run verifier at the
> signed level, against corpus `sha256:…`." "agenteval-results 1.0.0 conforms to AEF 1.0 as Decision engine and
> Stream verifier, against corpus `sha256:…`." A claim names only the classes whose vectors it passed in full.

## 9.5 Changing the schemas

- **[CONF-5]** `tools/schema_diff.py` compares a candidate set of writer schemas with the published ones and fails on
  any change a minor version may not make ([VER-5]). A new minor version is published only when it passes.
