# 9. Conformance

## 9.1 Classes

An implementation conforms to a class (§1.7) when it meets the requirements below and passes every corpus vector of
that class. Requirements not listed for a class still apply to it where it does what they describe.

| Class | Requirements | Vectors (§9.2 `kind`) |
|---|---|---|
| **Producer** | ENC-1–ENC-19, RUN-1–RUN-15, RES-1–RES-11, SUM-1–SUM-9, EVD-1–EVD-3, GATE-1–GATE-2, VER-1, VER-2, VER-6, VER-9 | `document` (writer side), `run` (valid runs), `result-id`, `paths` |
| **Sealer** | SEAL-1–SEAL-5, SIG-1–SIG-3 | `seal` (expected manifests), `signature` (signing side) |
| **Reader** | ENC-1–ENC-19, VER-3, VER-4, VER-8 (the reading rules of §7.3), VER-9 | `document` (reader side), `encoding`, `reader-only` |
| **Run verifier** | Reader, plus §3.9, SEAL-4, SEAL-6, SIG-4, SIG-5, SIG-7, and OVL-4, OVL-5, OVL-10 (to tell a withheld blob from a missing one) | `run`, `seal`, `encoding`, `paths`; at the *signed* level also `signature` |
| **Overlay verifier** | OVL-1–OVL-11 | `chain`, `overlay-view` |
| **Checkpoint verifier** | CKP-1–CKP-10, LANE-1–LANE-11, DEC-1–DEC-5, SIG-8, and Run verifier | `checkpoint`, `lane`, `decision` |
| **Decision engine** | DEC-1–DEC-5 | `decision` |
| **Runner** | PLAN-1–PLAN-7, STRM-1–STRM-2, RUN-12, and Producer and Sealer for the runs it produces | `plan`, `matching` |
| **Stream verifier** | STRM-1–STRM-4 | `stream`, `plan-conformance` |

A Run verifier conforms at the **intact** level, or at the **signed** level when it also verifies signatures (§4.4).

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
| `matching` | a plan and a runner manifest | whether the runner can take the plan |
| `stream` | an event stream, its plan and the plan's digest | the problems of [STRM-3] |
| `plan-conformance` | an event stream, its plan, the runs it produced, and optionally a trust policy | the problems of [STRM-4] |
| `fixture` | files several vectors use (test keys, a stream's plans) | nothing to run: the runner checks their digests |
| `paths` | a list of paths in a run folder | the problems of [RUN-3] |

- **[CONF-2] Comparing problems.** A list of problems is compared as an ordered list of `[path, code]` pairs (codes
  alone for [CKP-7]), in the order of §3.9; a vector passes only when the lists are equal. Times are compared as written, numbers as binary64.

### 9.2.1 Vector files

Each vector is a folder holding `expected.json` and its input; a run is always in a `run/` subfolder, so that
`expected.json` is never a file of the run. Every `expected.json` has `kind` and `rules` (the rule ids it concerns).

| `kind` | Folder | `expected.json` (beyond `kind` and `rules`) |
|---|---|---|
| `document` | `invalid/<name>/document.json` | `schema` (a schema name), `writer` and `reader` (`valid` or `invalid`), `why` |
| `reader-only` | `reader-only/<name>/document.json` | `schema`, `writer` (`invalid`), `reader` (`valid`), and `reads`: for each field path (dots and `[i]`), the known value a reader takes the unknown one as (§7.3) |
| `run` | `valid/<name>/run/`, `runs/<name>/run/` | `run` (`"run"`), optional inputs `policy` (a trust policy file beside `expected.json`) and `anchors` (a JSON list of trusted run hashes); `outcome` (`unsealed`, `intact` or `invalid`, §4.5), `problems`: the seal problems (§4.1) and the problems of §3.9 together, ordered; with `policy`, `signedBy` (identities, in policy order); with `anchors`, `anchored` (`true` or `false`) |
| `encoding` | `encoding/<name>/run/` | as `run` |
| `seal` | `seal-vectors/<name>/run/` | `run`, optionally `policy` (a trust policy, for redactions, [OVL-10]), `problems` (§4.1 only), and `manifest`: a file beside `expected.json` holding the expected manifest, when the run is sealable |
| `chain` | `chain-vectors/<name>/run/` | `run`, `problems` (§4.2 only) |
| `overlay-view` | `overlay-views/<name>/run/` | `run`, `at` (the time the view is computed at), `view` (below) |
| `checkpoint` | `checkpoints/<name>/document.json` | `schema`, `writer`, `reader`, `why`, and `problems` ([CKP-7] codes) when the reader accepts it |
| `lane` | `lane-vectors/<name>/checkpoint.json` and `runs/<n>/` | `checkpoint`, `runs` (the folder of runs, found by their `run.json`), `lanes`: per lane in manifest order, `lane` and `result` (§5.3; `null` for none), and `problems` ([CKP-8]) |
| `signature` | `signature-vectors/<name>/` (test keys in `signature-vectors/keys/`) | inputs: `envelope`, `file` (the signed file), `payloadType` (the type §4.4 gives that file), `policy` (a trust policy, below); expected: `envelopeResult` (`null`, `malformed` or `payload-mismatch`), `signatures` (per signature in envelope order: `keyid`, `result`, and `identity` when `verified`; empty when malformed), and `verifiesFor` (the identities the envelope verifies for, in policy order) |
| `result-id` | `result-ids.json`: a list | each item: `runId`, `caseId`, `path`, `trial` (or `null`), `resultId` |
| `paths` | `paths.json`: a list | each item: `name`, `paths`, `problems` |
| `decision` | `decision-vectors/<name>.json`: one file per vector | `input` (a decision input, §5.4), `description`, `rules`, and either `expected` (the output: `outcome`, `lanes`, `reasons`) or `expectedError` (the function refuses the input; the value names why, such as `no-lanes`, for people: an implementation's message need not match); `schemaInvalid: true` when the input is also invalid against the decision schema, `readerOnly: true` when only the reader schema accepts it |
| `plan`, `matching`, `stream` | `protocol/` | as `tools/aef_stream.py` documents |
| `plan-conformance` | `protocol/plan-conformance/<name>/`: `events.ndjson`, `plan.json`, and the runs in `runs/` (found by their `run.json`) | `events`, `plan` and `runs` (those inputs, beside `expected.json`), optionally `policy` (a trust policy, for runs with authorized redactions), `problems`: the problems of [STRM-4] as `[path, code]` pairs, ordered, and `why` |

**The effective view** of an `overlay-view` vector (§4.3) is an object with:
- `results`: one entry per result that a verified `override` or `adjudicate` targets, in `results.ndjson` order:
  `resultId`, `sealedState`, `effectiveState`, and `event` (the `eventId` that set it);
- `reviews`: one entry per target of a verified `approve` or `reject`, the run first and then results in
  `results.ndjson` order: `target` (`"run"` or the `resultId`), `status` (`approve` or `reject`), `event`;
- `waivers`: every verified `waive`, in file order: `target` (the event's target without `run` and `runHash`),
  `expires`, `active` (`at` ≤ the view time < `expires`), `event`;
- `withheld`: the blobs withheld by authorized `redact` events ([OVL-10]; a vector that has some carries the `policy`
  that authorizes them), in file order;
- `unsealedEvents`: the number of events after the last verified batch.

**A trust policy** is a JSON object `{"keys": [{"identity": …, "publicKey": <SPKI PEM>, "may": ["redact"]?}]}`. A key's
id is computed from its public key ([SIG-3]), never read from the policy. `seal`, `run` and `lane` vectors may carry a
`policy` file beside `expected.json`; `run` vectors with withheld blobs also expect `withheld` (a count).

## 9.3 Running the corpus

- **[CONF-3]** A conformance runner reads `index.json`, selects the vectors of the classes it claims, performs for each
  the operation its `kind` names on the input, and compares the result with the expected one. It checks the SHA-256 of
  every file it reads against the index, so a modified corpus cannot pass.
- `tools/aef_conformance.py` is such a runner for the reference implementation (`tools/aef_verify.py`); `tools/`
  documents the command-line contract an implementation in another language follows to be driven by it: one command
  per operation, input paths as arguments, the result as JSON on standard output.

## 9.4 Claiming conformance

- **[CONF-4]** A claim names the implementation and its version, the AEF version (`1.0`), the classes (and the Run
  verifier level), and the corpus version (the SHA-256 of `index.json`) it passed in full. A claim with exceptions is
  not a conformance claim.

> *Examples:* "aef-tools (the reference tools in `tools/`) conform to AEF 1.0 in every class, the Run verifier at the
> signed level, against corpus `sha256:…`." "agenteval-results 1.0.0 conforms to AEF 1.0 as Decision engine and
> Stream verifier, against corpus `sha256:…`." A claim names only the classes whose vectors it passed in full.

## 9.5 Changing the schemas

- **[CONF-5]** `tools/schema_diff.py` compares a candidate set of writer schemas with the published ones and fails on
  any change a minor version may not make ([VER-5]). A new minor version is published only when it passes.
